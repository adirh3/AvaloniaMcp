using System.IO.Pipes;
using System.Text;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string? PipeName => _pipeName;
    private readonly string? _fixedPipeName;

    public AvaloniaConnection(ConnectionOptions options)
    {
        _pipeName = options.PipeName;
        _fixedPipeName = options.PipeName;
    }

    /// <summary>
    /// Switch to a specific Avalonia app by PID. Disconnects from the current app if different.
    /// </summary>
    public void SwitchTo(int pid)
    {
        var newPipe = $"avalonia-mcp-{pid}";
        if (_pipeName == newPipe && _pipe is { IsConnected: true })
            return;

        Disconnect();
        _pipeName = newPipe;
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
            await _writer!.WriteLineAsync(json);
            await _writer.FlushAsync(ct);
            await _pipe!.FlushAsync(ct);

            var response = await _reader!.ReadLineAsync(ct);
            if (response is null)
            {
                // Connection was lost — try to reconnect once
                await ReconnectAsync(ct);
                await _writer!.WriteLineAsync(json);
                await _writer.FlushAsync(ct);
                await _pipe!.FlushAsync(ct);
                response = await _reader!.ReadLineAsync(ct);
            }

            if (response is null)
                throw new InvalidOperationException("No response from Avalonia app");

            return JsonDocument.Parse(response);
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
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var success) && !success.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
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
            return;

        await ReconnectAsync(ct);
    }

    private string ResolvePipeName()
    {
        // If a pipe was explicitly set (via --pipe, --pid, or SwitchTo), use it
        if (_pipeName is not null)
            return _pipeName;

        // Auto-discover
        var apps = DiscoverApps();
        if (apps.Count == 1)
        {
            _pipeName = apps[0].RootElement.GetProperty("pipeName").GetString()!;
            foreach (var app in apps) app.Dispose();
            return _pipeName;
        }

        foreach (var app in apps) app.Dispose();

        if (apps.Count > 1)
            throw new InvalidOperationException(
                $"Multiple Avalonia apps found ({apps.Count}). Call discover_apps first, then pass the pid parameter to target a specific app.");

        throw new InvalidOperationException(
            "No Avalonia apps with MCP diagnostics found. Start your Avalonia app with .UseMcpDiagnostics() first.");
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        Disconnect();

        var pipeName = ResolvePipeName();
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(5000, ct);
        var encoding = new System.Text.UTF8Encoding(false);
        _reader = new StreamReader(_pipe, encoding, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, encoding, 1024, leaveOpen: true) { AutoFlush = true };
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
