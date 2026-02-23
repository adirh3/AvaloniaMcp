using System.Text.Json.Serialization;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal sealed class BindingErrorEntry
{
    public string Timestamp { get; set; } = "";
    public string Level { get; set; } = "Error";
    public string Message { get; set; } = "";
}

[JsonSerializable(typeof(BindingErrorEntry))]
internal partial class BindingErrorJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Thread-safe tracker for binding errors captured from Avalonia's logging system.
/// </summary>
internal static class BindingErrorTracker
{
    private static readonly List<BindingErrorEntry> _errors = new();
    private static readonly object _lock = new();
    private const int MaxErrors = 500;

    public static void RecordError(string message, string? level = null)
    {
        lock (_lock)
        {
            if (_errors.Count >= MaxErrors)
                _errors.RemoveAt(0);

            _errors.Add(new BindingErrorEntry
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                Level = level ?? "Error",
                Message = message,
            });
        }
    }

    public static List<BindingErrorEntry> GetErrors()
    {
        lock (_lock)
        {
            return new List<BindingErrorEntry>(_errors);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _errors.Clear();
        }
    }
}
