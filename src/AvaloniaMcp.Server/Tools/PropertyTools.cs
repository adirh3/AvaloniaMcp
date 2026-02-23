using System.ComponentModel;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class PropertyTools
{
    [McpServerTool(Name = "get_control_properties", ReadOnly = true, Destructive = false),
     Description("Get all Avalonia properties of a control, including current values, property types, whether they are set explicitly, and binding information. Essential for debugging why a control looks or behaves unexpectedly.")]
    public static async Task<string> GetControlProperties(
        AvaloniaConnection connection,
        [Description("Control identifier: '#Name' to find by Name, or 'TypeName[index]' to find by type.")] string controlId,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_control_properties", new()
        {
            ["controlId"] = controlId,
        }, ct);
    }

    [McpServerTool(Name = "get_data_context", ReadOnly = true, Destructive = false),
     Description("Get the DataContext (ViewModel) of a control. Returns the type and all public properties of the bound ViewModel, serialized as JSON. Use expandProperty to read items from collection properties like ObservableCollection<T>.")]
    public static async Task<string> GetDataContext(
        AvaloniaConnection connection,
        [Description("Control identifier. If omitted, returns the main window's DataContext.")] string? controlId = null,
        [Description("Name of a collection property to expand (e.g. 'Messages', 'Items'). Returns the actual items with their properties.")] string? expandProperty = null,
        [Description("Maximum number of items to return when expanding a collection. Default: 50.")] int maxItems = 50,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_data_context", new()
        {
            ["controlId"] = controlId,
            ["expandProperty"] = expandProperty,
            ["maxItems"] = maxItems,
        }, ct);
    }

    [McpServerTool(Name = "get_applied_styles", ReadOnly = true, Destructive = false),
     Description("Get styles applied to a control, including CSS-like classes, pseudo-classes (e.g. :pointerover, :pressed), and style setters. Use this to debug visual appearance issues.")]
    public static async Task<string> GetAppliedStyles(
        AvaloniaConnection connection,
        [Description("Control identifier.")] string controlId,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_applied_styles", new()
        {
            ["controlId"] = controlId,
        }, ct);
    }

    [McpServerTool(Name = "get_resources", ReadOnly = true, Destructive = false),
     Description("Get resources defined in a control's resource dictionary (brushes, styles, templates, etc.). If no control is specified, returns application-level resources.")]
    public static async Task<string> GetResources(
        AvaloniaConnection connection,
        [Description("Control identifier. If omitted, returns application resources.")] string? controlId = null,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_resources", new()
        {
            ["controlId"] = controlId,
        }, ct);
    }

    [McpServerTool(Name = "get_binding_errors", ReadOnly = true, Destructive = false),
     Description("Get all binding errors captured since the application started. Binding errors are the #1 source of bugs in XAML apps — missing properties, wrong paths, null DataContexts. Returns error messages with timestamps.")]
    public static async Task<string> GetBindingErrors(
        AvaloniaConnection connection,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("get_binding_errors", ct: ct);
    }
}
