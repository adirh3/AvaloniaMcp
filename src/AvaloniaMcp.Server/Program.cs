using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AvaloniaMcp.Server.Services;

// Check for CLI mode: avalonia-mcp cli <method> [--param value ...]
if (args.Length > 0 && args[0] == "cli")
{
    try
    {
        await RunCliAsync(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal: {ex}");
        Environment.ExitCode = 1;
    }
    return;
}

// Standard MCP server mode (stdio transport)
// The server starts immediately — connection to an Avalonia app is lazy (on first tool call).
var pipeName = GetArg(args, "--pipe") ?? GetArg(args, "-p");
var pid = GetArg(args, "--pid");

// If PID is specified, derive pipe name
if (pipeName is null && pid is not null)
    pipeName = $"avalonia-mcp-{pid}";

var builder = Host.CreateApplicationBuilder(args);

// MCP servers use stdio — redirect all logging to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new ConnectionOptions { PipeName = pipeName });
builder.Services.AddSingleton<AvaloniaConnection>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "AvaloniaMcp",
            Version = "0.2.0"
        };
        options.ServerInstructions = """
            Avalonia UI debugging server. Connects to a running Avalonia application and provides tools to inspect, debug, and interact with the UI.

            RECOMMENDED WORKFLOW:
            1. discover_apps — find running Avalonia apps (returns PIDs)
            2. list_windows — see all open windows (pass pid if multiple apps)
            3. get_visual_tree — inspect the control hierarchy of a window
            4. find_control — search for specific controls by name, type, or text
            5. get_control_properties — inspect all properties of a control
            6. get_binding_errors — check for broken data bindings
            7. get_data_context — inspect the ViewModel data
            8. take_screenshot — capture a visual snapshot

            MULTIPLE APPS: If multiple Avalonia apps are running, call discover_apps first,
            then pass the pid parameter to all subsequent tool calls to target the right app.
            If only one app is running, pid is optional (auto-discovered).

            CONTROL IDENTIFIERS: Tools accept a controlId parameter:
            - '#MyButton' — find by Name property
            - 'Button' — find first Button in the tree
            - 'Button[2]' — find third Button (0-indexed)
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();


// ── CLI mode ──────────────────────────────────────────────────

static async Task RunCliAsync(string[] args)
{
    var method = args.Length > 1 ? args[1] : null;
    if (method is null or "--help" or "-h")
    {
        PrintCliHelp();
        return;
    }

    var pipeName = GetArg(args, "--pipe") ?? GetArg(args, "-p");
    var pid = GetArg(args, "--pid");

    if (pipeName is null && pid is not null)
        pipeName = $"avalonia-mcp-{pid}";

    if (pipeName is null)
    {
        var apps = AvaloniaConnection.DiscoverApps();
        if (apps.Count == 1)
        {
            pipeName = apps[0].RootElement.GetProperty("pipeName").GetString();
            foreach (var app in apps) app.Dispose();
        }
        else
        {
            Console.Error.WriteLine("Specify --pipe or --pid to connect to an Avalonia app.");
            return;
        }
    }

    using var connection = new AvaloniaConnection(new ConnectionOptions { PipeName = pipeName });

    // Parse remaining args as parameters
    var parameters = new Dictionary<string, object?>();
    for (int i = 2; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--") && args[i] != "--pipe" && args[i] != "--pid")
        {
            var key = args[i][2..];
            var value = args[i + 1];
            // Try to parse as int or bool
            if (int.TryParse(value, out var intVal))
                parameters[key] = intVal;
            else if (bool.TryParse(value, out var boolVal))
                parameters[key] = boolVal;
            else
                parameters[key] = value;
            i++; // skip value
        }
    }

    try
    {
        var result = await connection.RequestAsync(method, parameters);
        Console.WriteLine(result);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
}

static void PrintCliHelp()
{
    Console.WriteLine("""
        AvaloniaMcp CLI — Debug running Avalonia applications

        Usage: avalonia-mcp cli <method> [--param value ...] [--pipe name | --pid processId]

        Methods:
          list_windows                           List all open windows
          get_visual_tree    [--maxDepth 10]      Get visual tree of a window
          get_logical_tree   [--maxDepth 10]      Get logical tree of a window
          find_control       [--name X] [--typeName Y] [--text Z]
          get_control_properties --controlId X    Get all properties of a control
          get_data_context   [--controlId X]      Get ViewModel/DataContext
          get_binding_errors                      Get all binding errors
          get_applied_styles --controlId X        Get styles on a control
          get_resources      [--controlId X]      Get resource dictionary
          get_focused_element                     Get focused element
          take_screenshot    [--controlId X]      Capture screenshot (base64 PNG)
          click_control      --controlId X        Simulate click
          set_property       --controlId X --propertyName Y --value Z
          input_text         --controlId X --text Y

        Connection:
          --pipe <name>      Named pipe to connect to (e.g. 'avalonia-mcp-12345')
          --pid <processId>  Process ID (derives pipe name automatically)
          (If omitted, auto-discovers a single running app)

        Examples:
          avalonia-mcp cli list_windows
          avalonia-mcp cli get_visual_tree --maxDepth 5
          avalonia-mcp cli find_control --typeName Button
          avalonia-mcp cli get_control_properties --controlId "#MyButton"
          avalonia-mcp cli take_screenshot --controlId "#MainGrid"
        """);
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}
