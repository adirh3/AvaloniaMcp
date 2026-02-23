using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class InspectionTools
{
    [McpServerTool(Name = "list_windows", ReadOnly = true, Destructive = false),
     Description("List all open windows in the running Avalonia application. Returns title, size, state, and visibility for each window. Use this as the entry point to understand what's currently visible.")]
    public static async Task<string> ListWindows(
        AvaloniaConnection connection,
        [Description("Process ID of the Avalonia app to connect to. Use discover_apps to find available PIDs. If omitted, auto-discovers (works when only one app is running).")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("list_windows", ct: ct);
    }

    [McpServerTool(Name = "get_visual_tree", ReadOnly = true, Destructive = false),
     Description("Get the visual tree of a window or control. Returns the complete hierarchy of visual elements with type, name, bounds, visibility, and content. This is the most important tool for understanding the UI structure.")]
    public static async Task<string> GetVisualTree(
        AvaloniaConnection connection,
        [Description("Index of the window (from list_windows). Default: 0 (main window).")] int windowIndex = 0,
        [Description("Control identifier: '#Name' to find by Name, or 'TypeName' / 'TypeName[index]' to find by type. If specified, shows the subtree rooted at this control.")] string? controlId = null,
        [Description("Maximum depth to traverse. Default: 10. Use lower values for large trees.")] int maxDepth = 10,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_visual_tree", new()
        {
            ["windowIndex"] = windowIndex,
            ["controlId"] = controlId,
            ["maxDepth"] = maxDepth,
        }, ct);
    }

    [McpServerTool(Name = "get_logical_tree", ReadOnly = true, Destructive = false),
     Description("Get the logical tree of a window or control. The logical tree represents the XAML structure (controls you declared), while the visual tree includes template-generated elements. Use this to understand the intended structure.")]
    public static async Task<string> GetLogicalTree(
        AvaloniaConnection connection,
        [Description("Index of the window (from list_windows). Default: 0.")] int windowIndex = 0,
        [Description("Control identifier to scope the tree. '#Name' or 'TypeName[index]'.")] string? controlId = null,
        [Description("Maximum depth. Default: 10.")] int maxDepth = 10,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_logical_tree", new()
        {
            ["windowIndex"] = windowIndex,
            ["controlId"] = controlId,
            ["maxDepth"] = maxDepth,
        }, ct);
    }

    [McpServerTool(Name = "find_control", ReadOnly = true, Destructive = false),
     Description("Search for controls by name, type, or text content across all windows. Traverses into control templates (finds PART_ elements). Returns matching controls with their type, bounds, and content.")]
    public static async Task<string> FindControl(
        AvaloniaConnection connection,
        [Description("Find controls whose Name contains this string (case-insensitive). '#' prefix is stripped automatically.")] string? name = null,
        [Description("Find controls whose type name contains this string (e.g. 'Button', 'TextBox').")] string? typeName = null,
        [Description("Find controls displaying this text content (searches TextBlock, ContentControl, AccessText, HeaderedContentControl, etc.).")] string? text = null,
        [Description("Maximum results to return. Default: 20.")] int maxResults = 20,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("find_control", new()
        {
            ["name"] = name,
            ["typeName"] = typeName,
            ["text"] = text,
            ["maxResults"] = maxResults,
        }, ct);
    }

    [McpServerTool(Name = "get_focused_element", ReadOnly = true, Destructive = false),
     Description("Get the currently focused element. Useful for debugging keyboard input and focus issues.")]
    public static async Task<string> GetFocusedElement(
        AvaloniaConnection connection,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_focused_element", ct: ct);
    }
}
