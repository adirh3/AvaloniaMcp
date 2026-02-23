using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class DiscoveryTools
{
    [McpServerTool(Name = "discover_apps", ReadOnly = true, Destructive = false),
     Description("Discover running Avalonia applications that have MCP diagnostics enabled. Returns process ID, pipe name, process name, and protocol version for each app. Use this when you don't know which app to connect to.")]
    public static Task<string> DiscoverApps(CancellationToken ct = default)
    {
        var apps = ConnectionPool.DiscoverApps();
        var results = new List<JsonObject>();
        foreach (var app in apps)
        {
            var obj = JsonSerializer.Deserialize<JsonObject>(app.RootElement.GetRawText())!;

            // Add version compatibility warning
            if (obj.TryGetPropertyValue("protocolVersion", out var versionNode))
            {
                var appVersion = versionNode?.GetValue<string>();
                if (appVersion != "0.3.0")
                    obj["versionWarning"] = $"App uses protocol v{appVersion}, CLI tool is v0.3.0. Some tools may not work. Update AvaloniaMcp.Diagnostics to match.";
            }
            else
            {
                obj["versionWarning"] = "App does not report protocol version. It may be using an older AvaloniaMcp.Diagnostics (<0.3.0). Update the package.";
            }

            results.Add(obj);
            app.Dispose();
        }

        return Task.FromResult(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
