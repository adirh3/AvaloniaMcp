using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal static class PropertyHandler
{
    public static async Task<DiagnosticResponse> GetControlProperties(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            var props = new List<Dictionary<string, object?>>();

            var registeredProps = AvaloniaPropertyRegistry.Instance.GetRegistered(control)
                .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(control.GetType()));

            foreach (var prop in registeredProps)
            {
                try
                {
                    var value = control.GetValue(prop);
                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = prop.Name,
                        ["propertyType"] = prop.PropertyType.Name,
                        ["ownerType"] = prop.OwnerType.Name,
                        ["value"] = SafeSerialize(value),
                        ["isSet"] = control.IsSet(prop),
                    };

                    props.Add(entry);
                }
                catch
                {
                    // Skip unreadable properties
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["type"] = control.GetType().FullName,
                ["name"] = control.Name,
                ["propertyCount"] = props.Count,
                ["properties"] = props,
            };

            return DiagnosticResponse.Ok(result);
        });
    }

    public static async Task<DiagnosticResponse> GetDataContext(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var control = ControlResolver.Resolve(controlId)
                ?? ControlResolver.GetMainWindow();
            if (control is null)
                return DiagnosticResponse.Fail("Control not found");

            var dc = control.DataContext;
            if (dc is null)
                return DiagnosticResponse.Ok(new { message = "DataContext is null", controlType = control.GetType().Name });

            var result = new Dictionary<string, object?>
            {
                ["controlType"] = control.GetType().Name,
                ["controlName"] = control.Name,
                ["dataContextType"] = dc.GetType().FullName,
                ["properties"] = SerializeObject(dc),
            };

            return DiagnosticResponse.Ok(result);
        });
    }

    public static async Task<DiagnosticResponse> GetAppliedStyles(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            var result = new Dictionary<string, object?>
            {
                ["type"] = control.GetType().Name,
                ["name"] = control.Name,
                ["classes"] = control.Classes.ToList(),
                ["pseudoClasses"] = control.Classes.Where(c => c.StartsWith(':')).ToList(),
            };

            // Get applied style setters
            var appliedStyles = new List<Dictionary<string, object?>>();
            if (control is StyledElement styled)
            {
                foreach (var style in styled.Styles)
                {
                    if (style is Style s)
                    {
                        var setters = new List<string>();
                        foreach (var setter in s.Setters)
                        {
                            if (setter is Setter avSetter)
                            {
                                setters.Add($"{avSetter.Property?.Name} = {SafeSerialize(avSetter.Value)}");
                            }
                        }

                        appliedStyles.Add(new Dictionary<string, object?>
                        {
                            ["selector"] = s.Selector?.ToString(),
                            ["setterCount"] = s.Setters.Count,
                            ["setters"] = setters,
                        });
                    }
                }
            }

            result["styles"] = appliedStyles;
            return DiagnosticResponse.Ok(result);
        });
    }

    public static async Task<DiagnosticResponse> GetResources(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            StyledElement? control = ControlResolver.Resolve(controlId)
                ?? (StyledElement?)ControlResolver.GetMainWindow();
            if (control is null && Application.Current is not null)
                control = null; // will use Application.Current below

            var resources = new List<Dictionary<string, object?>>();

            IResourceDictionary? resDict = null;
            if (control is not null)
                resDict = control.Resources;
            else if (Application.Current is not null)
                resDict = Application.Current.Resources;

            if (resDict is not null)
            {
                foreach (var kvp in resDict)
                {
                    resources.Add(new Dictionary<string, object?>
                    {
                        ["key"] = kvp.Key?.ToString(),
                        ["valueType"] = kvp.Value?.GetType().Name,
                        ["value"] = SafeSerialize(kvp.Value),
                    });
                }

                foreach (var merged in resDict.MergedDictionaries)
                {
                    if (merged is ResourceDictionary rd)
                    {
                        foreach (var kvp in rd)
                        {
                            resources.Add(new Dictionary<string, object?>
                            {
                                ["key"] = kvp.Key?.ToString(),
                                ["valueType"] = kvp.Value?.GetType().Name,
                                ["value"] = SafeSerialize(kvp.Value),
                                ["source"] = "merged",
                            });
                        }
                    }
                }
            }

            return DiagnosticResponse.Ok(new
            {
                controlType = control?.GetType().Name ?? "Application",
                resourceCount = resources.Count,
                resources,
            });
        });
    }

    public static async Task<DiagnosticResponse> GetBindingErrors()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var errors = BindingErrorTracker.GetErrors();
            return DiagnosticResponse.Ok(new
            {
                errorCount = errors.Count,
                errors = errors.TakeLast(100).ToList(),
            });
        });
    }

    private static object? SafeSerialize(object? value)
    {
        if (value is null) return null;
        try
        {
            var type = value.GetType();
            if (value is double d && (double.IsInfinity(d) || double.IsNaN(d))) return d.ToString();
            if (value is float f && (float.IsInfinity(f) || float.IsNaN(f))) return f.ToString();
            if (type.IsPrimitive || value is string || value is decimal || value is DateTime) return value;
            if (type.IsEnum) return value.ToString();
            if (value is Avalonia.Media.IBrush brush) return brush.ToString();
            if (value is Avalonia.Media.Color color) return color.ToString();
            if (value is Thickness thickness) return $"{thickness.Left},{thickness.Top},{thickness.Right},{thickness.Bottom}";
            if (value is CornerRadius cr) return $"{cr.TopLeft},{cr.TopRight},{cr.BottomRight},{cr.BottomLeft}";
            return value.ToString();
        }
        catch
        {
            return $"<{value.GetType().Name}>";
        }
    }

    private static Dictionary<string, object?> SerializeObject(object obj)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                var value = prop.GetValue(obj);
                dict[prop.Name] = SafeSerialize(value);
            }
            catch
            {
                dict[prop.Name] = "<error reading>";
            }
        }
        return dict;
    }
}
