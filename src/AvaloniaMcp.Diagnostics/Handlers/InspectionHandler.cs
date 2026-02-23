using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal static class InspectionHandler
{
    public static async Task<DiagnosticResponse> ListWindows()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = ControlResolver.GetWindows();
            var result = new JsonArray();
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                result.Add(new JsonObject
                {
                    ["index"] = J.Int(i),
                    ["title"] = J.Str(w.Title),
                    ["type"] = J.Str(w.GetType().Name),
                    ["width"] = J.Dbl(w.Width),
                    ["height"] = J.Dbl(w.Height),
                    ["clientWidth"] = J.Dbl(w.ClientSize.Width),
                    ["clientHeight"] = J.Dbl(w.ClientSize.Height),
                    ["isActive"] = J.Bool(w.IsActive),
                    ["isVisible"] = J.Bool(w.IsVisible),
                    ["windowState"] = J.Str(w.WindowState.ToString()),
                    ["position"] = J.Str(w.Position.ToString()),
                });
            }

            return DiagnosticResponse.Ok(result);
        });
    }

    public static async Task<DiagnosticResponse> GetVisualTree(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windowIndex = req.GetInt("windowIndex");
            var controlId = req.GetString("controlId");
            var maxDepth = req.GetInt("maxDepth", 10);

            Visual? root;
            if (!string.IsNullOrEmpty(controlId))
            {
                root = ControlResolver.Resolve(controlId);
            }
            else
            {
                var windows = ControlResolver.GetWindows();
                root = windowIndex < windows.Count ? windows[windowIndex] : null;
            }

            if (root is null)
                return DiagnosticResponse.Fail("Window or control not found");

            var tree = ControlResolver.SerializeNode(root, maxDepth);
            return DiagnosticResponse.Ok(tree);
        });
    }

    public static async Task<DiagnosticResponse> GetLogicalTree(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windowIndex = req.GetInt("windowIndex");
            var controlId = req.GetString("controlId");
            var maxDepth = req.GetInt("maxDepth", 10);

            Control? root;
            if (!string.IsNullOrEmpty(controlId))
            {
                root = ControlResolver.Resolve(controlId);
            }
            else
            {
                var windows = ControlResolver.GetWindows();
                root = windowIndex < windows.Count ? windows[windowIndex] : null;
            }

            if (root is null)
                return DiagnosticResponse.Fail("Window or control not found");

            var tree = ControlResolver.SerializeLogicalNode(root, maxDepth);
            return DiagnosticResponse.Ok(tree);
        });
    }

    public static async Task<DiagnosticResponse> FindControl(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var name = req.GetString("name");
            var typeName = req.GetString("typeName");
            var text = req.GetString("text");
            var maxResults = req.GetInt("maxResults", 20);

            // Strip leading '#' from name if present (users may pass '#MyButton' from controlId syntax)
            if (name is not null && name.StartsWith('#'))
                name = name[1..];

            var hasFilter = !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(text);
            if (!hasFilter)
                return DiagnosticResponse.Fail("At least one filter (name, typeName, or text) is required.");

            var results = new JsonArray();

            foreach (var window in ControlResolver.GetWindows())
            {
                // GetVisualDescendants traverses into control templates (PART_ elements etc.)
                foreach (var desc in window.GetVisualDescendants())
                {
                    if (results.Count >= maxResults) break;

                    var matches = true;
                    if (!string.IsNullOrEmpty(name))
                        matches &= desc is Control c && c.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;
                    if (!string.IsNullOrEmpty(typeName))
                        matches &= desc.GetType().Name.Contains(typeName, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(text))
                        matches &= GetDisplayText(desc)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

                    if (matches)
                    {
                        results.Add(ControlResolver.SerializeNode(desc, maxDepth: 0));
                    }
                }
            }

            return DiagnosticResponse.Ok(results);
        });
    }

    /// <summary>
    /// Extract display text from a visual element, checking multiple common control types.
    /// </summary>
    private static string? GetDisplayText(Visual visual)
    {
        return visual switch
        {
            TextBlock tb => tb.Text,
            TextBox txb => txb.Text,
            ContentControl cc when cc.Content is string s => s,
            ContentControl cc when cc.Content is TextBlock ctb => ctb.Text,
            _ => null,
        };
    }

    public static async Task<DiagnosticResponse> GetFocusedElement()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = ControlResolver.GetWindows();
            foreach (var w in windows)
            {
                var focused = w.FocusManager?.GetFocusedElement();
                if (focused is Visual v)
                {
                    var node = ControlResolver.SerializeNode(v, maxDepth: 0);
                    node["windowTitle"] = J.Str(w.Title);
                    return DiagnosticResponse.Ok(node);
                }
            }
            return DiagnosticResponse.Ok(new JsonObject { ["message"] = J.Str("No element is focused") });
        });
    }
}
