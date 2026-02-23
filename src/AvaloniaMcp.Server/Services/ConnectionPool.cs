using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AvaloniaMcp.Server.Services;

/// <summary>
/// Manages a pool of <see cref="AvaloniaConnection"/> instances, one per PID.
/// Connections are created lazily and evicted when their target process exits.
/// Thread-safe: concurrent tool calls targeting different PIDs use independent pipes.
/// </summary>
public sealed class ConnectionPool : IDisposable
{
    private readonly ConcurrentDictionary<int, AvaloniaConnection> _connections = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConnectionPool> _logger;

    public ConnectionPool(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConnectionPool>();
    }

    /// <summary>
    /// Get a connection for the given PID. If pid is null, auto-discovers.
    /// The returned connection is a long-lived pooled instance — do NOT dispose it.
    /// </summary>
    public AvaloniaConnection GetConnection(int? pid)
    {
        var resolvedPid = pid ?? AutoDiscoverSinglePid();

        return _connections.GetOrAdd(resolvedPid, static (p, state) =>
        {
            var pipeName = $"avalonia-mcp-{p}";
            state.logger.LogInformation("Creating pooled connection for PID {Pid} (pipe: {PipeName})", p, pipeName);
            return new AvaloniaConnection(pipeName, state.loggerFactory.CreateLogger<AvaloniaConnection>());
        }, (logger: _logger, loggerFactory: _loggerFactory));
    }

    /// <summary>
    /// Send a request to the app identified by pid (or auto-discovered).
    /// Returns formatted JSON string or error text.
    /// </summary>
    public async Task<string> RequestAsync(string method, Dictionary<string, object?>? parameters = null, int? pid = null, CancellationToken ct = default)
    {
        var connection = GetConnection(pid);
        var result = await connection.RequestAsync(method, parameters, ct);

        // If the request failed with a pipe error, the process may have exited.
        // Evict the dead connection so the next call creates a fresh one.
        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            var resolvedPid = pid ?? TryAutoDiscoverSinglePid();
            if (resolvedPid.HasValue && !IsProcessRunning(resolvedPid.Value))
            {
                _logger.LogInformation("Evicting dead connection for PID {Pid}", resolvedPid.Value);
                EvictConnection(resolvedPid.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Remove and dispose a connection for a specific PID.
    /// </summary>
    public void EvictConnection(int pid)
    {
        if (_connections.TryRemove(pid, out var connection))
        {
            _logger.LogDebug("Evicted connection for PID {Pid}", pid);
            connection.Dispose();
        }
    }

    /// <summary>
    /// Remove all connections whose target process is no longer running.
    /// </summary>
    public void EvictDead()
    {
        foreach (var (pid, _) in _connections)
        {
            if (!IsProcessRunning(pid))
                EvictConnection(pid);
        }
    }

    private int AutoDiscoverSinglePid()
    {
        var apps = DiscoverApps();
        try
        {
            if (apps.Count == 1)
            {
                var pid = apps[0].RootElement.GetProperty("pid").GetInt32();
                _logger.LogDebug("Auto-discovered single Avalonia app: PID {Pid}", pid);
                return pid;
            }

            if (apps.Count > 1)
                throw new InvalidOperationException(
                    $"Multiple Avalonia apps found ({apps.Count}). Call discover_apps first, then pass the pid parameter to target a specific app.");

            throw new InvalidOperationException(
                "No Avalonia apps with MCP diagnostics found. Start your Avalonia app with .UseMcpDiagnostics() first.");
        }
        finally
        {
            foreach (var app in apps) app.Dispose();
        }
    }

    private int? TryAutoDiscoverSinglePid()
    {
        try { return AutoDiscoverSinglePid(); }
        catch { return null; }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            System.Diagnostics.Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
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

                if (doc.RootElement.TryGetProperty("pid", out var pidEl))
                {
                    var pid = pidEl.GetInt32();
                    if (IsProcessRunning(pid))
                    {
                        apps.Add(doc);
                    }
                    else
                    {
                        doc.Dispose();
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { /* skip corrupt files */ }
        }

        return apps;
    }

    public void Dispose()
    {
        foreach (var (_, connection) in _connections)
            connection.Dispose();
        _connections.Clear();
    }
}
