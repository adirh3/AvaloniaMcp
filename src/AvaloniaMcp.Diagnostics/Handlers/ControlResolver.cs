using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

/// <summary>
/// Resolves controls by name or type path and provides tree serialization.
/// </summary>
internal static class ControlResolver
{
    /// <summary>
    /// Resolve a control by identifier. Supports:
    /// - "#Name" to find by Name property
    /// - "TypeName" or "TypeName[index]" to find by type
    /// </summary>
    public static Control? Resolve(string? controlId, Window? window = null)
    {
        if (string.IsNullOrWhiteSpace(controlId))
            return window;

        var windows = GetWindows();
        if (window is not null)
            windows = [window];

        // By name: #MyButton
        if (controlId.StartsWith('#'))
        {
            var name = controlId[1..];
            foreach (var w in windows)
            {
                var found = w.FindControl<Control>(name)
                    ?? FindByName(w, name);
                if (found is not null)
                    return found;
            }
            return null;
        }

        // By type: Button or Button[2]
        var typeName = controlId;
        var index = 0;
        var bracketPos = controlId.IndexOf('[');
        if (bracketPos >= 0)
        {
            typeName = controlId[..bracketPos];
            var indexStr = controlId[(bracketPos + 1)..].TrimEnd(']');
            int.TryParse(indexStr, out index);
        }

        var matchCount = 0;
        foreach (var w in windows)
        {
            foreach (var desc in w.GetVisualDescendants())
            {
                if (desc.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    if (matchCount == index)
                        return desc as Control;
                    matchCount++;
                }
            }
        }

        return null;
    }

    public static List<Window> GetWindows()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.Windows.ToList();
        return [];
    }

    public static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    private static Control? FindByName(Visual root, string name)
    {
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is Control c && c.Name == name)
                return c;
        }
        return null;
    }

    /// <summary>
    /// Serialize a visual tree node to a JsonObject for AOT-safe JSON output.
    /// </summary>
    public static JsonObject SerializeNode(Visual visual, int maxDepth, int currentDepth = 0)
    {
        var node = new JsonObject
        {
            ["type"] = J.Str(visual.GetType().Name),
        };

        try
        {
            if (visual is Control c)
            {
                if (!string.IsNullOrEmpty(c.Name))
                    node["name"] = J.Str(c.Name);
                node["isVisible"] = J.Bool(c.IsVisible);
                node["isEnabled"] = J.Bool(c.IsEnabled);
            }

            node["bounds"] = new JsonObject
            {
                ["x"] = J.Dbl(visual.Bounds.X),
                ["y"] = J.Dbl(visual.Bounds.Y),
                ["width"] = J.Dbl(visual.Bounds.Width),
                ["height"] = J.Dbl(visual.Bounds.Height),
            };

            if (visual is ContentControl cc && cc.Content is string text)
                node["content"] = J.Str(text);
            else if (visual is TextBlock tb)
                node["text"] = J.Str(tb.Text);

            if (visual.Classes.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var cls in visual.Classes)
                    arr.Add(J.Str(cls));
                node["classes"] = arr;
            }

            if (visual.GetValue(Visual.OpacityProperty) is double op && Math.Abs(op - 1.0) > 0.01)
                node["opacity"] = J.Dbl(op);
        }
        catch (Exception ex)
        {
            node["_error"] = J.Str($"Failed to read properties: {ex.GetType().Name}: {ex.Message}");
        }

        if (currentDepth < maxDepth)
        {
            try
            {
                var children = visual.GetVisualChildren().ToList();
                if (children.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var child in children)
                    {
                        try
                        {
                            arr.Add(SerializeNode(child, maxDepth, currentDepth + 1));
                        }
                        catch (Exception ex)
                        {
                            arr.Add(new JsonObject
                            {
                                ["type"] = J.Str(child.GetType().Name),
                                ["_error"] = J.Str($"{ex.GetType().Name}: {ex.Message}"),
                            });
                        }
                    }
                    node["children"] = arr;
                }
            }
            catch (Exception ex)
            {
                node["_childrenError"] = J.Str($"{ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            var childCount = visual.GetVisualChildren().Count();
            if (childCount > 0)
                node["childCount"] = J.Int(childCount);
        }

        return node;
    }

    /// <summary>
    /// Serialize a logical tree node.
    /// </summary>
    public static JsonObject SerializeLogicalNode(ILogical logical, int maxDepth, int currentDepth = 0)
    {
        var node = new JsonObject
        {
            ["type"] = J.Str(logical.GetType().Name),
        };

        try
        {
            if (logical is Control c)
            {
                if (!string.IsNullOrEmpty(c.Name))
                    node["name"] = J.Str(c.Name);
                node["isVisible"] = J.Bool(c.IsVisible);
            }

            if (logical is ContentControl cc && cc.Content is string text)
                node["content"] = J.Str(text);
            else if (logical is TextBlock tb)
                node["text"] = J.Str(tb.Text);
        }
        catch (Exception ex)
        {
            node["_error"] = J.Str($"Failed to read properties: {ex.GetType().Name}: {ex.Message}");
        }

        if (currentDepth < maxDepth)
        {
            try
            {
                var children = logical.GetLogicalChildren().ToList();
                if (children.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var child in children)
                    {
                        try
                        {
                            arr.Add(SerializeLogicalNode(child, maxDepth, currentDepth + 1));
                        }
                        catch (Exception ex)
                        {
                            arr.Add(new JsonObject
                            {
                                ["type"] = J.Str(child.GetType().Name),
                                ["_error"] = J.Str($"{ex.GetType().Name}: {ex.Message}"),
                            });
                        }
                    }
                    node["children"] = arr;
                }
            }
            catch (Exception ex)
            {
                node["_childrenError"] = J.Str($"{ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            var childCount = logical.GetLogicalChildren().Count();
            if (childCount > 0)
                node["childCount"] = J.Int(childCount);
        }

        return node;
    }
}
