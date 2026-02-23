using Avalonia;
using Avalonia.Logging;
using AvaloniaMcp.Diagnostics.Handlers;

namespace AvaloniaMcp.Diagnostics;

public static class DiagnosticExtensions
{
    private static DiagnosticServer? _server;

    /// <summary>
    /// Enables AvaloniaMcp diagnostics on the application builder.
    /// Call this in your Program.cs: BuildAvaloniaApp().UseMcpDiagnostics()
    /// </summary>
    public static AppBuilder UseMcpDiagnostics(this AppBuilder builder, string? pipeName = null)
    {
        _server = new DiagnosticServer(pipeName);
        _server.Start();

        // Hook into Avalonia's logging to capture binding errors
        Logger.Sink = new BindingErrorLogSink(Logger.Sink);

        // Write crash file on unhandled exceptions so the MCP server can report them
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashFile(args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashFile(args.Exception);
        };

        return builder;
    }

    private static void WriteCrashFile(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "avalonia-mcp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Environment.ProcessId}.crash.txt");
            File.WriteAllText(path, $"[{DateTime.UtcNow:O}] Unhandled exception:\n{ex}");
        }
        catch { /* best effort — process is dying */ }
    }

    public static DiagnosticServer? GetDiagnosticServer() => _server;

    /// <summary>
    /// Log sink that intercepts binding errors and records them.
    /// </summary>
    private sealed class BindingErrorLogSink : ILogSink
    {
        private readonly ILogSink? _inner;

        public BindingErrorLogSink(ILogSink? inner) => _inner = inner;

        public bool IsEnabled(LogEventLevel level, string area)
        {
            // Always capture binding errors, delegate rest to inner
            if (area == "Binding") return true;
            return _inner?.IsEnabled(level, area) ?? false;
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (area == "Binding" && level >= LogEventLevel.Warning)
            {
                BindingErrorTracker.RecordError(messageTemplate, level.ToString());
            }
            _inner?.Log(level, area, source, messageTemplate);
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (area == "Binding" && level >= LogEventLevel.Warning)
            {
                // Avalonia uses Serilog-style {Property} templates — interpolate them
                var msg = messageTemplate;
                if (propertyValues is { Length: > 0 })
                {
                    for (int i = 0; i < propertyValues.Length; i++)
                    {
                        // Replace first {Xxx} placeholder with the value
                        var idx = msg.IndexOf('{');
                        var end = idx >= 0 ? msg.IndexOf('}', idx) : -1;
                        if (idx >= 0 && end > idx)
                            msg = string.Concat(msg.AsSpan(0, idx), propertyValues[i]?.ToString(), msg.AsSpan(end + 1));
                    }
                }
                BindingErrorTracker.RecordError(msg, level.ToString());
            }
            _inner?.Log(level, area, source, messageTemplate, propertyValues);
        }
    }
}
