using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal static class InspectionHandler
{
    public static async Task<DiagnosticResponse> ListWindows()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = ControlResolver.GetWindows();
            var result = windows.Select((w, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["title"] = w.Title,
                ["type"] = w.GetType().Name,
                ["width"] = w.Width,
                ["height"] = w.Height,
                ["clientWidth"] = w.ClientSize.Width,
                ["clientHeight"] = w.ClientSize.Height,
                ["isActive"] = w.IsActive,
                ["isVisible"] = w.IsVisible,
                ["windowState"] = w.WindowState.ToString(),
                ["position"] = w.Position.ToString(),
            }).ToList();

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

            var results = new List<Dictionary<string, object?>>();

            foreach (var window in ControlResolver.GetWindows())
            {
                foreach (var desc in window.GetVisualDescendants())
                {
                    if (results.Count >= maxResults) break;

                    var matches = true;
                    if (!string.IsNullOrEmpty(name))
                        matches &= desc is Control c && c.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;
                    if (!string.IsNullOrEmpty(typeName))
                        matches &= desc.GetType().Name.Contains(typeName, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(text))
                    {
                        var controlText = desc switch
                        {
                            TextBlock tb => tb.Text,
                            ContentControl cc when cc.Content is string s => s,
                            _ => null
                        };
                        matches &= controlText?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
                    }

                    if (matches && (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(text)))
                    {
                        results.Add(ControlResolver.SerializeNode(desc, maxDepth: 0));
                    }
                }
            }

            return DiagnosticResponse.Ok(results);
        });
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
                    node["windowTitle"] = w.Title;
                    return DiagnosticResponse.Ok(node);
                }
            }
            return DiagnosticResponse.Ok(new { message = "No element is focused" });
        });
    }
}
