using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal static class ScrollViewerHandler
{
    /// <summary>
    /// Unified scroll operation. Supports absolute (x/y), relative (deltaX/deltaY with unit),
    /// into-view (targetControlId or itemIndex on an ItemsControl), and edge jumps (top/bottom/left/right).
    /// Always returns current scroll state after the operation.
    /// </summary>
    public static async Task<DiagnosticResponse> Scroll(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");

            // --- BringIntoView mode ---
            var targetControlId = req.GetString("targetControlId");
            var itemIndex = req.GetInt("itemIndex", -1);

            if (!string.IsNullOrEmpty(targetControlId) || itemIndex >= 0)
            {
                var target = ControlResolver.Resolve(targetControlId ?? controlId);
                if (target is null)
                    return DiagnosticResponse.Fail($"Control '{targetControlId ?? controlId}' not found");

                if (itemIndex >= 0 && target is ItemsControl itemsControl)
                {
                    if (itemIndex >= itemsControl.ItemCount)
                        return DiagnosticResponse.Fail($"Item index {itemIndex} out of range (count: {itemsControl.ItemCount})");

                    itemsControl.ScrollIntoView(itemIndex);
                    var sv = FindScrollViewer(itemsControl);
                    var result = new JsonObject
                    {
                        ["method"] = J.Str("ScrollIntoView"),
                        ["itemIndex"] = J.Int(itemIndex),
                    };
                    if (sv is not null) result["scroll"] = SerializeScrollState(sv);
                    return DiagnosticResponse.Ok(result);
                }

                target.BringIntoView();
                var parentSv = FindScrollViewer(target);
                var bringResult = new JsonObject
                {
                    ["method"] = J.Str("BringIntoView"),
                    ["controlType"] = J.Str(target.GetType().Name),
                };
                if (parentSv is not null) bringResult["scroll"] = SerializeScrollState(parentSv);
                return DiagnosticResponse.Ok(bringResult);
            }

            // --- Positional scroll mode ---
            var scrollViewer = ResolveScrollViewer(controlId);
            if (scrollViewer is null)
                return DiagnosticResponse.Fail(
                    string.IsNullOrEmpty(controlId)
                        ? "No ScrollViewer found"
                        : $"No ScrollViewer found for '{controlId}'");

            var maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

            // Edge jump
            var edge = req.GetString("edge");
            if (!string.IsNullOrEmpty(edge))
            {
                var (ex, ey) = edge.ToLowerInvariant() switch
                {
                    "top" => (scrollViewer.Offset.X, 0.0),
                    "bottom" => (scrollViewer.Offset.X, maxY),
                    "left" => (0.0, scrollViewer.Offset.Y),
                    "right" => (maxX, scrollViewer.Offset.Y),
                    "home" => (0.0, 0.0),
                    "end" => (maxX, maxY),
                    _ => (scrollViewer.Offset.X, scrollViewer.Offset.Y),
                };
                scrollViewer.Offset = new Vector(ex, ey);
                return DiagnosticResponse.Ok(SerializeScrollState(scrollViewer));
            }

            // Relative scroll (deltaX/deltaY)
            var deltaX = req.GetDouble("deltaX", double.NaN);
            var deltaY = req.GetDouble("deltaY", double.NaN);
            if (!double.IsNaN(deltaX) || !double.IsNaN(deltaY))
            {
                var dx = double.IsNaN(deltaX) ? 0 : deltaX;
                var dy = double.IsNaN(deltaY) ? 0 : deltaY;
                var unit = req.GetString("unit") ?? "pixels";

                if (unit.Equals("pages", StringComparison.OrdinalIgnoreCase))
                {
                    dx *= scrollViewer.Viewport.Width;
                    dy *= scrollViewer.Viewport.Height;
                }
                else if (unit.Equals("lines", StringComparison.OrdinalIgnoreCase))
                {
                    const double lineHeight = 20.0;
                    dx *= lineHeight;
                    dy *= lineHeight;
                }

                var newX = Math.Clamp(scrollViewer.Offset.X + dx, 0, maxX);
                var newY = Math.Clamp(scrollViewer.Offset.Y + dy, 0, maxY);
                scrollViewer.Offset = new Vector(newX, newY);
                return DiagnosticResponse.Ok(SerializeScrollState(scrollViewer));
            }

            // Absolute scroll (x/y)
            var absX = req.GetDouble("x", double.NaN);
            var absY = req.GetDouble("y", double.NaN);
            if (!double.IsNaN(absX) || !double.IsNaN(absY))
            {
                var nx = double.IsNaN(absX) ? scrollViewer.Offset.X : Math.Clamp(absX, 0, maxX);
                var ny = double.IsNaN(absY) ? scrollViewer.Offset.Y : Math.Clamp(absY, 0, maxY);
                scrollViewer.Offset = new Vector(nx, ny);
                return DiagnosticResponse.Ok(SerializeScrollState(scrollViewer));
            }

            // No scroll params: just return current state
            return DiagnosticResponse.Ok(SerializeScrollState(scrollViewer));
        });
    }

    /// <summary>
    /// Returns virtualization info for an ItemsControl: total items, realized containers
    /// (indices + bounds), panel type, and parent scroll state.
    /// </summary>
    public static async Task<DiagnosticResponse> GetScrollableItems(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            var itemsControl = control as ItemsControl
                ?? control.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();

            if (itemsControl is null)
                return DiagnosticResponse.Fail($"No ItemsControl found at or within '{controlId}'");

            var totalItems = itemsControl.ItemCount;
            var vsp = itemsControl.GetVisualDescendants()
                .OfType<VirtualizingStackPanel>().FirstOrDefault();
            var isVirtualized = vsp is not null;

            var result = new JsonObject
            {
                ["controlType"] = J.Str(itemsControl.GetType().Name),
                ["totalItems"] = J.Int(totalItems),
                ["isVirtualized"] = J.Bool(isVirtualized),
            };

            if (!string.IsNullOrEmpty(itemsControl.Name))
                result["controlName"] = J.Str(itemsControl.Name);

            if (vsp is not null)
            {
                var firstRealized = vsp.FirstRealizedIndex;
                var lastRealized = vsp.LastRealizedIndex;
                result["firstRealizedIndex"] = J.Int(firstRealized);
                result["lastRealizedIndex"] = J.Int(lastRealized);
                result["realizedCount"] = J.Int(firstRealized >= 0 ? lastRealized - firstRealized + 1 : 0);

                var realizedContainers = new JsonArray();
                for (int i = firstRealized; i >= 0 && i <= lastRealized; i++)
                {
                    var container = itemsControl.ContainerFromIndex(i);
                    var entry = new JsonObject { ["index"] = J.Int(i) };
                    if (container is Control c)
                    {
                        entry["bounds"] = new JsonObject
                        {
                            ["y"] = J.Dbl(c.Bounds.Y),
                            ["height"] = J.Dbl(c.Bounds.Height),
                        };
                    }
                    realizedContainers.Add(entry);
                }
                result["realizedContainers"] = realizedContainers;
            }
            else
            {
                var realizedCount = 0;
                for (int i = 0; i < totalItems; i++)
                {
                    if (itemsControl.ContainerFromIndex(i) is Control)
                        realizedCount++;
                }
                result["realizedCount"] = J.Int(realizedCount);
            }

            var scrollViewer = FindScrollViewer(itemsControl);
            if (scrollViewer is not null)
                result["scroll"] = SerializeScrollState(scrollViewer);

            return DiagnosticResponse.Ok(result);
        });
    }

    private static ScrollViewer? ResolveScrollViewer(string? controlId)
    {
        if (string.IsNullOrEmpty(controlId))
        {
            var window = ControlResolver.GetMainWindow();
            return window?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        }

        var control = ControlResolver.Resolve(controlId);
        if (control is null) return null;
        if (control is ScrollViewer sv) return sv;

        return control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
    }

    private static ScrollViewer? FindScrollViewer(Control control) =>
        control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault()
        ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private static JsonObject SerializeScrollState(ScrollViewer sv)
    {
        var maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        return new JsonObject
        {
            ["offset"] = new JsonObject { ["x"] = J.Dbl(sv.Offset.X), ["y"] = J.Dbl(sv.Offset.Y) },
            ["viewport"] = new JsonObject { ["width"] = J.Dbl(sv.Viewport.Width), ["height"] = J.Dbl(sv.Viewport.Height) },
            ["extent"] = new JsonObject { ["width"] = J.Dbl(sv.Extent.Width), ["height"] = J.Dbl(sv.Extent.Height) },
            ["scrollMax"] = new JsonObject { ["x"] = J.Dbl(maxX), ["y"] = J.Dbl(maxY) },
            ["isAtTop"] = J.Bool(sv.Offset.Y <= 0),
            ["isAtBottom"] = J.Bool(sv.Offset.Y >= maxY - 0.5),
        };
    }
}
