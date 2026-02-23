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

    public string PipeName => _pipeName;

    /// <summary>Last time this connection was used for a request.</summary>
    public DateTime LastUsed => _lastUsed;

    public AvaloniaConnection(string pipeName, ILogger? logger = null)
    {
        _pipeName = pipeName;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<JsonDocument> SendAsync(string method, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _lastUsed = DateTime.UtcNow;
            try
            {
                return await SendCoreAsync(method, parameters, ct);
            }
            catch (Exception ex) when (IsPipeError(ex))
            {
                _logger.LogWarning(ex, "Pipe error for '{Method}' on '{PipeName}', reconnecting and retrying once...", method, _pipeName);
                Disconnect();
                _validated = false;
                return await SendCoreAsync(method, parameters, ct);
            }
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

    private async Task<JsonDocument> SendCoreAsync(string method, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var request = new
        {
            method,
            @params = parameters ?? new Dictionary<string, object?>(),
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        _logger.LogDebug("[{PipeName}] Sending: {Json}", _pipeName, json);
        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync(ct);
        await _pipe!.FlushAsync(ct);

        var response = await _reader!.ReadLineAsync(ct);
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

                if (appVersion != "0.2.0")
                {
                    _logger.LogWarning(
                        "[{PipeName}] Protocol version mismatch: CLI tool is 0.2.0, app reports '{AppVersion}'. " +
                        "Some tools may not work correctly. Update AvaloniaMcp.Diagnostics NuGet package to match.",
                        _pipeName, appVersion);
                }
            }
            else
            {
                _logger.LogWarning(
                    "[{PipeName}] Protocol version could not be determined. " +
                    "The app may be using an older AvaloniaMcp.Diagnostics version. Consider updating to 0.2.0+.",
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
}
