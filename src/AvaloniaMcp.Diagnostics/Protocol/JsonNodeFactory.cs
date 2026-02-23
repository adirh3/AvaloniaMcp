using System.Text.Json.Nodes;

namespace AvaloniaMcp.Diagnostics.Protocol;

/// <summary>
/// Creates JsonNode values using the source-generated type info from DiagnosticJsonContext.
/// This is required because implicit conversion from primitives to JsonNode stores
/// JsonSerializerOptions.Default internally. In apps with PublishAot=true, that default
/// uses EmptyJsonTypeInfoResolver which can't serialize basic types.
/// </summary>
internal static class J
{
    public static JsonNode? Str(string? s) =>
        s is null ? null : JsonValue.Create(s, DiagnosticJsonContext.Default.String);

    public static JsonNode Bool(bool b) =>
        JsonValue.Create(b, DiagnosticJsonContext.Default.Boolean)!;

    public static JsonNode Int(int i) =>
        JsonValue.Create(i, DiagnosticJsonContext.Default.Int32)!;

    public static JsonNode Dbl(double d) =>
        JsonValue.Create(d, DiagnosticJsonContext.Default.Double)!;
}
