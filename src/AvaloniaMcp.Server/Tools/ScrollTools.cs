using System.ComponentModel;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class ScrollTools
{
    [McpServerTool(Name = "scroll", ReadOnly = false, Destructive = false),
     Description("Scroll a ScrollViewer. Supports multiple modes:\n" +
                 "- **Read state**: Call with just controlId (or omit for first ScrollViewer) to get current scroll position, viewport, extent, and edge flags.\n" +
                 "- **Absolute**: Set x/y to scroll to a specific offset.\n" +
                 "- **Relative**: Set deltaX/deltaY to scroll by an amount (in pixels, lines, or pages via 'unit').\n" +
                 "- **Edge jump**: Set edge to 'top', 'bottom', 'left', 'right', 'home', or 'end'.\n" +
                 "- **Into view**: Set targetControlId to BringIntoView a specific control. For ItemsControls, pass itemIndex to scroll a specific item into view (works with virtualization).\n" +
                 "The controlId auto-resolves: if the target is not a ScrollViewer, searches descendants then ancestors.")]
    public static async Task<string> Scroll(
        ConnectionPool pool,
        [Description("Control identifier for the ScrollViewer, or a control near/inside one. '#Name' or 'TypeName[index]'. If omitted, uses the first ScrollViewer in the main window.")] string? controlId = null,
        [Description("Absolute horizontal scroll offset (pixels).")] double? x = null,
        [Description("Absolute vertical scroll offset (pixels).")] double? y = null,
        [Description("Relative horizontal scroll delta.")] double? deltaX = null,
        [Description("Relative vertical scroll delta.")] double? deltaY = null,
        [Description("Unit for delta values: 'pixels' (default), 'lines' (~20px each), or 'pages' (one viewport).")] string? unit = null,
        [Description("Jump to an edge: 'top', 'bottom', 'left', 'right', 'home' (top-left), 'end' (bottom-right).")] string? edge = null,
        [Description("Control identifier to scroll into view (calls BringIntoView). For ItemsControls with itemIndex, this should be the ItemsControl itself.")] string? targetControlId = null,
        [Description("Item index to scroll into view within a virtualized ItemsControl. Uses BringIndexIntoView for VirtualizingStackPanel.")] int? itemIndex = null,
        [Description("Process ID of the Avalonia app. If omitted, auto-discovers.")] int? pid = null,
        [Description("Timeout in milliseconds. Default: 30000.")] int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?> { ["controlId"] = controlId };
        if (x.HasValue) p["x"] = x.Value;
        if (y.HasValue) p["y"] = y.Value;
        if (deltaX.HasValue) p["deltaX"] = deltaX.Value;
        if (deltaY.HasValue) p["deltaY"] = deltaY.Value;
        if (unit is not null) p["unit"] = unit;
        if (edge is not null) p["edge"] = edge;
        if (targetControlId is not null) p["targetControlId"] = targetControlId;
        if (itemIndex.HasValue) p["itemIndex"] = itemIndex.Value;

        return await pool.RequestAsync("scroll", p, pid: pid, timeoutMs: timeoutMs, ct: ct);
    }

    [McpServerTool(Name = "get_scrollable_items", ReadOnly = true, Destructive = false),
     Description("Get virtualization diagnostics for an ItemsControl (ListBox, DataGrid, TreeView, etc.). " +
                 "Returns total item count, whether the panel virtualizes, realized container indices with bounds, and parent scroll state. " +
                 "Use this to understand which items are actually materialized in the visual tree vs. virtual.")]
    public static async Task<string> GetScrollableItems(
        ConnectionPool pool,
        [Description("Control identifier for the ItemsControl (e.g. '#MyListBox', 'ListBox'). If the control is not an ItemsControl, searches descendants.")] string controlId,
        [Description("Process ID of the Avalonia app. If omitted, auto-discovers.")] int? pid = null,
        [Description("Timeout in milliseconds. Default: 30000.")] int timeoutMs = 30000,
        CancellationToken ct = default)
    {
        return await pool.RequestAsync("get_scrollable_items", new()
        {
            ["controlId"] = controlId,
        }, pid: pid, timeoutMs: timeoutMs, ct: ct);
    }
}
