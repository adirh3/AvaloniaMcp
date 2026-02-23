using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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

            var props = new JsonArray();

            var registeredProps = AvaloniaPropertyRegistry.Instance.GetRegistered(control)
                .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(control.GetType()));

            foreach (var prop in registeredProps)
            {
                try
                {
                    var value = control.GetValue(prop);
                    props.Add(new JsonObject
                    {
                        ["name"] = prop.Name,
                        ["propertyType"] = prop.PropertyType.Name,
                        ["ownerType"] = prop.OwnerType.Name,
                        ["value"] = SafeSerialize(value),
                        ["isSet"] = control.IsSet(prop),
                    });
                }
                catch
                {
                    // Skip unreadable properties
                }
            }

            var result = new JsonObject
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
                return DiagnosticResponse.Ok(new JsonObject { ["message"] = "DataContext is null", ["controlType"] = control.GetType().Name });

            var result = new JsonObject
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

            var classesArr = new JsonArray();
            foreach (var cls in control.Classes)
                classesArr.Add(cls);

            var pseudoArr = new JsonArray();
            foreach (var cls in control.Classes.Where(c => c.StartsWith(':')))
                pseudoArr.Add(cls);

            var result = new JsonObject
            {
                ["type"] = control.GetType().Name,
                ["name"] = control.Name,
                ["classes"] = classesArr,
                ["pseudoClasses"] = pseudoArr,
            };

            // Get applied style setters
            var appliedStyles = new JsonArray();
            if (control is StyledElement styled)
            {
                foreach (var style in styled.Styles)
                {
                    if (style is Style s)
                    {
                        var setters = new JsonArray();
                        foreach (var setter in s.Setters)
                        {
                            if (setter is Setter avSetter)
                            {
                                setters.Add((JsonNode)$"{avSetter.Property?.Name} = {SafeSerialize(avSetter.Value)}");
                            }
                        }

                        appliedStyles.Add(new JsonObject
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

            var resources = new JsonArray();

            IResourceDictionary? resDict = null;
            if (control is not null)
                resDict = control.Resources;
            else if (Application.Current is not null)
                resDict = Application.Current.Resources;

            if (resDict is not null)
            {
                foreach (var kvp in resDict)
                {
                    resources.Add(new JsonObject
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
                            resources.Add(new JsonObject
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

            return DiagnosticResponse.Ok(new JsonObject
            {
                ["controlType"] = control?.GetType().Name ?? "Application",
                ["resourceCount"] = resources.Count,
                ["resources"] = resources,
            });
        });
    }

    public static async Task<DiagnosticResponse> GetBindingErrors()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var errors = BindingErrorTracker.GetErrors();
            var errorsArr = new JsonArray();
            foreach (var err in errors.TakeLast(100))
                errorsArr.Add(JsonSerializer.SerializeToNode(err, BindingErrorJsonContext.Default.BindingErrorEntry)!);

            return DiagnosticResponse.Ok(new JsonObject
            {
                ["errorCount"] = errors.Count,
                ["errors"] = errorsArr,
            });
        });
    }

    private static string? SafeSerialize(object? value)
    {
        if (value is null) return null;
        try
        {
            var type = value.GetType();
            if (value is double d && (double.IsInfinity(d) || double.IsNaN(d))) return d.ToString();
            if (value is float f && (float.IsInfinity(f) || float.IsNaN(f))) return f.ToString();
            if (type.IsPrimitive || value is string || value is decimal || value is DateTime) return value.ToString();
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

    private static JsonObject SerializeObject(object obj)
    {
        var dict = new JsonObject();
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
