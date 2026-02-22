using System.Text.Json.Serialization;

namespace AvaloniaMcp.Diagnostics.Protocol;

public sealed class DiagnosticRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public Dictionary<string, object?> Params { get; set; } = new();

    public string? GetString(string key)
    {
        if (Params.TryGetValue(key, out var val) && val is System.Text.Json.JsonElement el)
        {
            return el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : el.GetRawText();
        }
        return val?.ToString();
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (Params.TryGetValue(key, out var val) && val is System.Text.Json.JsonElement el && el.TryGetInt32(out var i))
            return i;
        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (Params.TryGetValue(key, out var val) && val is System.Text.Json.JsonElement el)
            return el.GetBoolean();
        return defaultValue;
    }
}
