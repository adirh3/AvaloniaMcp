namespace AvaloniaMcp.Diagnostics.Handlers;

/// <summary>
/// Thread-safe tracker for binding errors captured from Avalonia's logging system.
/// </summary>
internal static class BindingErrorTracker
{
    private static readonly List<Dictionary<string, object?>> _errors = new();
    private static readonly object _lock = new();
    private const int MaxErrors = 500;

    public static void RecordError(string message, string? level = null)
    {
        lock (_lock)
        {
            if (_errors.Count >= MaxErrors)
                _errors.RemoveAt(0);

            _errors.Add(new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["level"] = level ?? "Error",
                ["message"] = message,
            });
        }
    }

    public static List<Dictionary<string, object?>> GetErrors()
    {
        lock (_lock)
        {
            return new List<Dictionary<string, object?>>(_errors);
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
