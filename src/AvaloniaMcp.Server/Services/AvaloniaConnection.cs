using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaloniaMcp.Server.Services;

/// <summary>
/// Connects to a running Avalonia app's diagnostic server via named pipe.
/// </summary>
public sealed class AvaloniaConnection : IDisposable
{
    private string? _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<AvaloniaConnection> _logger;
    private bool _validated;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string? PipeName => _pipeName;
    private readonly string? _fixedPipeName;

    public AvaloniaConnection(ConnectionOptions options, ILogger<AvaloniaConnection>? logger = null)
    {
        _pipeName = options.PipeName;
        _fixedPipeName = options.PipeName;
        _logger = logger ?? NullLogger<AvaloniaConnection>.Instance;
    }

    /// <summary>
    /// Switch to a specific Avalonia app by PID. Disconnects from the current app if different.
    /// </summary>
    public void SwitchTo(int pid)
    {
        var newPipe = $"avalonia-mcp-{pid}";
        if (_pipeName == newPipe && _pipe is { IsConnected: true })
            return;

        _logger.LogDebug("Switching to PID {Pid} (pipe: {PipeName})", pid, newPipe);
        Disconnect();
        _pipeName = newPipe;
        _validated = false;
    }

    public async Task<JsonDocument> SendAsync(string method, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);

            var request = new
            {
                method,
                @params = parameters ?? new Dictionary<string, object?>(),
            };

            var json = JsonSerializer.Serialize(request, JsonOptions);
            _logger.LogDebug("Sending: {Json}", json);
            await _writer!.WriteLineAsync(json);
            await _writer.FlushAsync(ct);
            await _pipe!.FlushAsync(ct);

            var response = await _reader!.ReadLineAsync(ct);
            if (response is null)
            {
                _logger.LogWarning("Null response from pipe, attempting reconnect");
                // Connection was lost — try to reconnect once
                await ReconnectAsync(ct);
                await _writer!.WriteLineAsync(json);
                await _writer.FlushAsync(ct);
                await _pipe!.FlushAsync(ct);
                response = await _reader!.ReadLineAsync(ct);
            }

            if (response is null)
                throw new InvalidOperationException("No response from Avalonia app (pipe returned null after reconnect)");

            _logger.LogDebug("Received: {Response}", response.Length > 500 ? response[..500] + "..." : response);
            return JsonDocument.Parse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendAsync failed for method '{Method}'", method);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Send a request and return the full JSON response as a formatted string.
    /// Returns error text on failure instead of throwing, so the LLM sees the actual error.
    /// </summary>
    public async Task<string> RequestAsync(string method, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await SendAsync(method, parameters, ct);
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timeout connecting to Avalonia app for '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return $"Error: Timeout connecting to Avalonia app on pipe '{_pipeName}'. Is the app running with .UseMcpDiagnostics()? Details: {ex.Message}";
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Pipe I/O error for '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return $"Error: Pipe communication failed for '{method}' on pipe '{_pipeName}'. The app may have closed. Details: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke '{Method}' on pipe '{PipeName}'", method, _pipeName);
            return $"Error invoking '{method}': [{ex.GetType().Name}] {ex.Message}";
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
                return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            }

            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
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

    /// <summary>
    /// Send a ping to verify the connection works and check protocol version compatibility.
    /// </summary>
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
                _logger.LogWarning("Ping returned null — pipe may be broken");
                _validated = true; // avoid retry loop, let the actual call surface the error
                return;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) && success.GetBoolean()
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("protocolVersion", out var versionEl))
            {
                var appVersion = versionEl.GetString();
                _logger.LogInformation("Connected to Avalonia app on pipe '{PipeName}' (protocol version: {Version})", _pipeName, appVersion);

                if (appVersion != "0.2.0")
                {
                    _logger.LogWarning(
                        "Protocol version mismatch: CLI tool is 0.2.0, app reports '{AppVersion}'. " +
                        "Some tools may not work correctly. Update AvaloniaMcp.Diagnostics NuGet package to match.",
                        appVersion);
                }
            }
            else
            {
                // Older diagnostics library without protocol version — warn but continue
                _logger.LogWarning(
                    "Connected to Avalonia app on pipe '{PipeName}' but protocol version could not be determined. " +
                    "The app may be using an older AvaloniaMcp.Diagnostics version. Consider updating to 0.2.0+.",
                    _pipeName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protocol validation ping failed — connection may be unstable");
        }

        _validated = true;
    }

    private string ResolvePipeName()
    {
        // If a pipe was explicitly set (via --pipe, --pid, or SwitchTo), use it
        if (_pipeName is not null)
        {
            _logger.LogDebug("Using pipe name '{PipeName}'", _pipeName);
            return _pipeName;
        }

        // Auto-discover
        var apps = DiscoverApps();
        if (apps.Count == 1)
        {
            _pipeName = apps[0].RootElement.GetProperty("pipeName").GetString()!;
            _logger.LogInformation("Auto-discovered single Avalonia app on pipe '{PipeName}'", _pipeName);
            foreach (var app in apps) app.Dispose();
            return _pipeName;
        }

        foreach (var app in apps) app.Dispose();

        if (apps.Count > 1)
        {
            _logger.LogWarning("Multiple Avalonia apps found ({Count}). Caller must specify pid.", apps.Count);
            throw new InvalidOperationException(
                $"Multiple Avalonia apps found ({apps.Count}). Call discover_apps first, then pass the pid parameter to target a specific app.");
        }

        _logger.LogWarning("No Avalonia apps with MCP diagnostics found");
        throw new InvalidOperationException(
            "No Avalonia apps with MCP diagnostics found. Start your Avalonia app with .UseMcpDiagnostics() first.");
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        Disconnect();

        var pipeName = ResolvePipeName();
        _logger.LogDebug("Connecting to pipe '{PipeName}'...", pipeName);
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await _pipe.ConnectAsync(5000, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogError("Timed out connecting to pipe '{PipeName}' after 5s. Is the Avalonia app still running?", pipeName);
            throw new TimeoutException(
                $"Timed out connecting to pipe '{pipeName}' after 5 seconds. " +
                $"Verify the Avalonia app is still running and has .UseMcpDiagnostics() enabled.");
        }

        var encoding = new System.Text.UTF8Encoding(false);
        _reader = new StreamReader(_pipe, encoding, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, encoding, 1024, leaveOpen: true) { AutoFlush = true };
        _validated = false;
        _logger.LogInformation("Connected to pipe '{PipeName}'", pipeName);
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
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
    /// Discover available Avalonia apps by checking temp discovery files.
    /// </summary>
    public static List<JsonDocument> DiscoverApps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avalonia-mcp");
        if (!Directory.Exists(dir))
            return [];

        var apps = new List<JsonDocument>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonDocument.Parse(json);

                // Verify the process is still running
                if (doc.RootElement.TryGetProperty("pid", out var pidEl))
                {
                    var pid = pidEl.GetInt32();
                    try
                    {
                        System.Diagnostics.Process.GetProcessById(pid);
                        apps.Add(doc);
                    }
                    catch
                    {
                        // Process no longer running — clean up
                        File.Delete(file);
                    }
                }
            }
            catch { /* skip corrupt files */ }
        }

        return apps;
    }
}

public sealed class ConnectionOptions
{
    public string? PipeName { get; set; }
}
