using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class DiscoveryTools
{
    [McpServerTool(Name = "discover_apps", ReadOnly = true, Destructive = false),
     Description("Discover running Avalonia applications that have MCP diagnostics enabled. Returns process ID, pipe name, and process name for each app. Use this when you don't know which app to connect to.")]
    public static Task<string> DiscoverApps(CancellationToken ct = default)
    {
        var apps = AvaloniaConnection.DiscoverApps();
        var results = new List<object>();
        foreach (var app in apps)
        {
            results.Add(JsonSerializer.Deserialize<object>(app.RootElement.GetRawText())!);
            app.Dispose();
        }

        return Task.FromResult(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
