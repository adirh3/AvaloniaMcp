using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaloniaMcp.Server.Services;

/// <summary>
/// A single named-pipe connection to one Avalonia app.
/// Each instance is bound to one pipe name (one PID) for its lifetime.
/// Use <see cref="ConnectionPool"/> to manage connections to multiple apps.
/// </summary>
public sealed class AvaloniaConnection : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger _logger;
    private bool _validated;
    private DateTime _lastUsed = DateTime.UtcNow;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// Default timeout (ms) for a single request-response round trip.
    /// 0 means no timeout (wait indefinitely).
    /// </summary>
    public const int DefaultTimeoutMs = 30_000;

    public string PipeName => _pipeName;

    /// <summary>Last time this connection was used for a request.</summary>
    public DateTime LastUsed => _lastUsed;

    public AvaloniaConnection(string pipeName, ILogger? logger = null)
    {
        _pipeName = pipeName;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<JsonDocument> SendAsync(string method, Dictionary<string, object?>? parameters = null, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            _lastUsed = DateTime.UtcNow;
            try
            {
                return await SendCoreAsync(method, parameters, timeoutMs, ct);
            }
            catch (Exception ex) when (IsPipeError(ex))
            {
                _logger.LogWarning(ex, "Pipe error for '{Method}' on '{PipeName}', reconnecting and retrying once...", method, _pipeName);
                Disconnect();
                _validated = false;
                return await SendCoreAsync(method, parameters, timeoutMs, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            // Timeout fired (not external cancellation). Reset connection to avoid protocol desync.
            _logger.LogError("Request '{Method}' on pipe '{PipeName}' timed out after {ElapsedMs}ms (limit: {TimeoutMs}ms)", method, _pipeName, sw.ElapsedMilliseconds, timeoutMs);
            Disconnect();
            _validated = false;
            throw new TimeoutException(BuildTimeoutDiagnostics(method, parameters, timeoutMs, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendAsync failed for method '{Method}' on pipe '{PipeName}'", method, _pipeName);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<JsonDocument> SendCoreAsync(string method, Dictionary<string, object?>? parameters, int timeoutMs, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        // Link the caller's token with a timeout so ReadLineAsync doesn't block forever
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            timeoutCts.CancelAfter(timeoutMs);
        var linked = timeoutCts.Token;

        var request = new
        {
            method,
            @params = parameters ?? new Dictionary<string, object?>(),
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        _logger.LogDebug("[{PipeName}] Sending: {Json}", _pipeName, json);
        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync(linked);
        await _pipe!.FlushAsync(linked);

        var response = await _reader!.ReadLineAsync(linked);
        if (response is null)
            throw new IOException($"Pipe '{_pipeName}' returned null response — connection lost");

        _logger.LogDebug("[{PipeName}] Received: {Response}", _pipeName, response.Length > 500 ? response[..500] + "..." : response);
        return JsonDocument.Parse(response);
    }

    private static bool IsPipeError(Exception ex)
        => ex is IOException or ObjectDisposedException;

    /// <summary>
    /// Send a request and return the full JSON response as a formatted string.
    /// Returns error text on failure instead of throwing, so the LLM sees the actual error.
    /// </summary>
    public async Task<string> RequestAsync(string method, Dictionary<string, object?>? parameters = null, int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await SendAsync(method, parameters, timeoutMs, ct);
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timeout for '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return AppendCrashFileIfDead($"Error (timeout): {ex.Message}");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Pipe I/O error for '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return AppendCrashFileIfDead($"Error: Pipe communication failed for '{method}' on pipe '{_pipeName}'. The app may have closed. Details: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return AppendCrashFileIfDead($"Error invoking '{method}': [{ex.GetType().Name}] {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) && !success.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                _logger.LogWarning("Avalonia app returned error for '{Method}': {Error}", method, error);
                return $"Error from Avalonia app: {error}";
            }

            if (root.TryGetProperty("data", out var data))
            {
                return JsonSerializer.Serialize(data, IndentedOptions);
            }

            return JsonSerializer.Serialize(root, IndentedOptions);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true })
        {
            if (!_validated)
                await ValidateProtocolAsync(ct);
            return;
        }

        await ReconnectAsync(ct);
        await ValidateProtocolAsync(ct);
    }

    private async Task ValidateProtocolAsync(CancellationToken ct)
    {
        try
        {
            var request = JsonSerializer.Serialize(new { method = "ping", @params = new Dictionary<string, object?>() }, JsonOptions);
            await _writer!.WriteLineAsync(request);
            await _writer.FlushAsync(ct);
            await _pipe!.FlushAsync(ct);
            var response = await _reader!.ReadLineAsync(ct);

            if (response is null)
            {
                _logger.LogWarning("[{PipeName}] Ping returned null — pipe may be broken", _pipeName);
                _validated = true;
                return;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) && success.GetBoolean()
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("protocolVersion", out var versionEl))
            {
                var appVersion = versionEl.GetString();
                _logger.LogInformation("[{PipeName}] Connected (protocol version: {Version})", _pipeName, appVersion);

                if (appVersion != "0.4.0")
                {
                    _logger.LogWarning(
                        "[{PipeName}] Protocol version mismatch: CLI tool is 0.4.0, app reports '{AppVersion}'. " +
                        "Some tools may not work correctly. Update AvaloniaMcp.Diagnostics NuGet package to match.",
                        _pipeName, appVersion);
                }
            }
            else
            {
                _logger.LogWarning(
                    "[{PipeName}] Protocol version could not be determined. " +
                    "The app may be using an older AvaloniaMcp.Diagnostics version. Consider updating to 0.4.0+.",
                    _pipeName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{PipeName}] Protocol validation ping failed — connection may be unstable", _pipeName);
        }

        _validated = true;
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        Disconnect();

        _logger.LogDebug("[{PipeName}] Connecting...", _pipeName);
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await _pipe.ConnectAsync(5000, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogError("[{PipeName}] Timed out connecting after 5s. Is the Avalonia app still running?", _pipeName);
            throw new TimeoutException(
                $"Timed out connecting to pipe '{_pipeName}' after 5 seconds. " +
                $"Verify the Avalonia app is still running and has .UseMcpDiagnostics() enabled.");
        }

        var encoding = new UTF8Encoding(false);
        _reader = new StreamReader(_pipe, encoding, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, encoding, 1024, leaveOpen: true) { AutoFlush = true };
        _validated = false;
        _logger.LogInformation("[{PipeName}] Connected", _pipeName);
    }

    private void Disconnect()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        _semaphore.Dispose();
    }

    /// <summary>
    /// Extract the PID from the pipe name (e.g. "avalonia-mcp-12345" → 12345).
    /// </summary>
    private int? ExtractPid()
    {
        var idx = _pipeName.LastIndexOf('-');
        if (idx >= 0 && int.TryParse(_pipeName.AsSpan(idx + 1), out var pid))
            return pid;
        return null;
    }

    /// <summary>
    /// If the target process is dead and a crash file exists, append the crash
    /// details to the error message so the caller gets the actual exception.
    /// </summary>
    private string AppendCrashFileIfDead(string errorMessage)
    {
        var pid = ExtractPid();
        if (!pid.HasValue)
            return errorMessage;

        // Only read crash file if the process is actually dead
        try
        {
            Process.GetProcessById(pid.Value);
            return errorMessage; // still alive, no crash file to read
        }
        catch
        {
            // Process is dead — check for crash file
        }

        try
        {
            var crashPath = Path.Combine(Path.GetTempPath(), "avalonia-mcp", $"{pid.Value}.crash.txt");
            if (File.Exists(crashPath))
            {
                var crashInfo = File.ReadAllText(crashPath);
                File.Delete(crashPath);
                _logger.LogWarning("Crash file found for PID {Pid}", pid.Value);
                return $"{errorMessage}\n\n--- App crash details (PID {pid.Value}) ---\n{crashInfo}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read crash file for PID {Pid}", pid.Value);
        }

        return errorMessage;
    }

    /// <summary>
    /// Build a concise, actionable diagnostic message when a request times out.
    /// Classifies the hang type (busy loop vs sleep/deadlock) using CPU heuristics.
    /// </summary>
    private string BuildTimeoutDiagnostics(string method, Dictionary<string, object?>? parameters, int timeoutMs, long elapsedMs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Request '{method}' timed out after {elapsedMs}ms (limit: {timeoutMs}ms) on pipe '{_pipeName}'.");

        // Check if target process is still alive and classify the hang
        var pid = ExtractPid();
        if (pid.HasValue)
        {
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                var cpuSeconds = proc.UserProcessorTime.TotalSeconds;
                var wallSeconds = elapsedMs / 1000.0;
                var cpuRatio = wallSeconds > 0 ? cpuSeconds / wallSeconds : 0; // rough; includes pre-timeout CPU

                sb.AppendLine();
                if (!proc.Responding)
                {
                    if (cpuRatio > 0.5)
                        sb.AppendLine($"Diagnosis: UI thread is in a BUSY LOOP (CPU: {cpuSeconds:F1}s user time, process not responding). A tight loop or heavy computation is pegging the CPU.");
                    else
                        sb.AppendLine($"Diagnosis: UI thread is BLOCKED/DEADLOCKED (CPU: {cpuSeconds:F1}s user time, process not responding). The thread is sleeping, waiting on a lock, or blocked on synchronous I/O.");
                }
                else
                {
                    sb.AppendLine($"Diagnosis: Process is responding but the operation took too long. The handler may be doing expensive work (large tree traversal, heavy serialization).");
                }
            }
            catch
            {
                sb.AppendLine();
                sb.AppendLine($"Diagnosis: Process (PID {pid.Value}) is DEAD — the app crashed or was closed.");

                // Try to read crash file for details
                try
                {
                    var crashPath = Path.Combine(Path.GetTempPath(), "avalonia-mcp", $"{pid.Value}.crash.txt");
                    if (File.Exists(crashPath))
                    {
                        var crashInfo = File.ReadAllText(crashPath);
                        File.Delete(crashPath);
                        sb.AppendLine();
                        sb.AppendLine($"--- Crash details ---");
                        sb.AppendLine(crashInfo);
                    }
                    else
                    {
                        sb.AppendLine("No crash file found — the app may have been killed externally or crashed without writing diagnostics.");
                    }
                }
                catch { /* best-effort */ }

                sb.AppendLine();
                sb.AppendLine("Action: Restart the Avalonia app and retry.");
                return sb.ToString();
            }
        }

        // Method-specific advice (most actionable suggestion first)
        sb.AppendLine();
        sb.Append("Action: ");
        sb.AppendLine(method switch
        {
            "click_control" or "invoke_command" =>
                "The command you triggered may have started a long-running operation on the UI thread. " +
                "Call 'list_windows' to check if the UI is responsive again, then retry.",
            "get_visual_tree" or "get_logical_tree" =>
                "Try reducing maxDepth (e.g. maxDepth=3). If that also times out, the UI thread is frozen by app code.",
            "get_control_properties" =>
                "Use the propertyNames filter to request only specific properties. If that also times out, the UI thread is frozen.",
            "get_data_context" =>
                "Use expandProperty on a specific collection instead of serializing the entire ViewModel.",
            "take_screenshot" =>
                "Try capturing a specific controlId instead of the full window.",
            "input_text" or "set_property" =>
                "The property change or input may have triggered a blocking operation. Call 'list_windows' to probe UI responsiveness.",
            _ =>
                "Call 'list_windows' to check if the UI thread is responsive. If it also times out, all MCP calls will fail until the app's UI thread unblocks.",
        });

        // Only suggest increasing timeout when the process is still responding
        if (pid.HasValue)
        {
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                if (proc.Responding)
                    sb.AppendLine($"You can also increase timeoutMs (currently {timeoutMs}ms) if the operation needs more time.");
            }
            catch { /* process died between checks */ }
        }

        return sb.ToString();
    }
}
