using System.Text.Json.Serialization;

namespace AvaloniaMcp.Diagnostics.Protocol;

public sealed class DiagnosticResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static DiagnosticResponse Ok(object? data) => new() { Success = true, Data = data };
    public static DiagnosticResponse Fail(string error) => new() { Success = false, Error = error };
}
