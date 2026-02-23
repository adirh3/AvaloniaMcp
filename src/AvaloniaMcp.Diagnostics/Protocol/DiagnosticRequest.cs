using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AvaloniaMcp.Diagnostics.Protocol;

public sealed class DiagnosticRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonObject Params { get; set; } = new();

    public string? GetString(string key)
    {
        if (Params.TryGetPropertyValue(key, out var node) && node is not null)
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToJsonString();
        }
        return null;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (Params.TryGetPropertyValue(key, out var node) && node is not null)
        {
            try { return node.GetValue<int>(); }
            catch { return defaultValue; }
        }
        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (Params.TryGetPropertyValue(key, out var node) && node is not null)
        {
            try { return node.GetValue<bool>(); }
            catch { return defaultValue; }
        }
        return defaultValue;
    }

    public List<string>? GetStringArray(string key)
    {
        if (Params.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
        {
            var list = new List<string>();
            foreach (var item in arr)
            {
                if (item is not null)
                    list.Add(item.GetValue<string>());
            }
            return list.Count > 0 ? list : null;
        }
        return null;
    }
}

[JsonSerializable(typeof(DiagnosticRequest))]
[JsonSerializable(typeof(DiagnosticResponse))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonValue))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(long))]
internal partial class DiagnosticJsonContext : JsonSerializerContext
{
}
