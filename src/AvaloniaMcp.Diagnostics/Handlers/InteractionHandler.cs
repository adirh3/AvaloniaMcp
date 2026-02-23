using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using AvaloniaMcp.Diagnostics.Protocol;

namespace AvaloniaMcp.Diagnostics.Handlers;

internal static class InteractionHandler
{
    public static async Task<DiagnosticResponse> ClickControl(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            if (control is Avalonia.Controls.Button button)
            {
                // Use the Command if available
                if (button.Command?.CanExecute(button.CommandParameter) == true)
                {
                    button.Command.Execute(button.CommandParameter);
                    return DiagnosticResponse.Ok(new JsonObject { ["clicked"] = J.Bool(true), ["method"] = J.Str("command"), ["controlType"] = J.Str(control.GetType().Name) });
                }
            }

            // For non-button controls or buttons without command, try invoking click via automation
            if (control is Avalonia.Controls.Button btn2)
            {
                // Programmatically invoke click via reflection on the OnClick method
                var onClickMethod = typeof(Avalonia.Controls.Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                onClickMethod?.Invoke(btn2, null);
                return DiagnosticResponse.Ok(new JsonObject { ["clicked"] = J.Bool(true), ["method"] = J.Str("onClick"), ["controlType"] = J.Str(control.GetType().Name) });
            }

            return DiagnosticResponse.Ok(new JsonObject { ["clicked"] = J.Bool(false), ["message"] = J.Str("Control is not a Button and pointer simulation is not supported. Use set_property or input_text instead."), ["controlType"] = J.Str(control.GetType().Name) });
        });
    }

    public static async Task<DiagnosticResponse> SetProperty(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var propertyName = req.GetString("propertyName");
            var value = req.GetString("value");

            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            if (string.IsNullOrEmpty(propertyName))
                return DiagnosticResponse.Fail("propertyName is required");

            // Find the property
            var props = AvaloniaPropertyRegistry.Instance.GetRegistered(control);
            var prop = props.FirstOrDefault(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
                return DiagnosticResponse.Fail($"Property '{propertyName}' not found on {control.GetType().Name}");

            try
            {
                var converted = ConvertValue(value, prop.PropertyType);
                control.SetValue(prop, converted);
                return DiagnosticResponse.Ok(new JsonObject { ["set"] = J.Bool(true), ["property"] = J.Str(propertyName), ["newValue"] = J.Str(value) });
            }
            catch (Exception ex)
            {
                return DiagnosticResponse.Fail($"Failed to set property: {ex.Message}");
            }
        });
    }

    public static async Task<DiagnosticResponse> InputText(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var text = req.GetString("text") ?? "";
            var pressEnter = req.GetBool("pressEnter");

            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            // Find the target TextBox (directly or as a child)
            TextBox? targetTextBox = control as TextBox;
            string? resolvedFrom = null;

            if (targetTextBox is null && control is AutoCompleteBox acb)
            {
                acb.Text = text;
                if (pressEnter)
                    RaiseEnterKey(acb);
                return DiagnosticResponse.Ok(new JsonObject { ["typed"] = J.Bool(true), ["controlType"] = J.Str("AutoCompleteBox"), ["text"] = J.Str(text), ["pressedEnter"] = J.Bool(pressEnter) });
            }

            if (targetTextBox is null)
            {
                // Auto-find the first TextBox child inside composite controls
                targetTextBox = control.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                if (targetTextBox is not null)
                    resolvedFrom = control.GetType().Name;
            }

            if (targetTextBox is null)
                return DiagnosticResponse.Fail($"Control type {control.GetType().Name} does not contain a TextBox.");

            targetTextBox.Text = text;

            if (pressEnter)
                RaiseEnterKey(targetTextBox);

            var result = new JsonObject
            {
                ["typed"] = J.Bool(true),
                ["controlType"] = J.Str(targetTextBox.GetType().Name),
                ["text"] = J.Str(text),
                ["pressedEnter"] = J.Bool(pressEnter),
            };
            if (resolvedFrom is not null)
                result["resolvedFrom"] = J.Str(resolvedFrom);
            if (!string.IsNullOrEmpty(targetTextBox.Name))
                result["resolvedTo"] = J.Str(targetTextBox.Name);
            return DiagnosticResponse.Ok(result);
        });
    }

    private static void RaiseEnterKey(Control control)
    {
        var keyDown = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        };
        control.RaiseEvent(keyDown);
    }

    public static async Task<DiagnosticResponse> InvokeCommand(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var controlId = req.GetString("controlId");
            var commandName = req.GetString("commandName");
            var parameterValue = req.GetString("parameter");

            if (string.IsNullOrEmpty(commandName))
                return DiagnosticResponse.Fail("commandName is required");

            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            var dc = control.DataContext;
            if (dc is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' has no DataContext");

            // Find the ICommand property on the DataContext
            var prop = dc.GetType().GetProperty(commandName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop is null)
                return DiagnosticResponse.Fail($"Property '{commandName}' not found on {dc.GetType().Name}");

            var command = prop.GetValue(dc) as System.Windows.Input.ICommand;
            if (command is null)
                return DiagnosticResponse.Fail($"Property '{commandName}' on {dc.GetType().Name} is not an ICommand (got {prop.PropertyType.Name})");

            if (!command.CanExecute(parameterValue))
                return DiagnosticResponse.Ok(new JsonObject
                {
                    ["executed"] = J.Bool(false),
                    ["reason"] = J.Str("CanExecute returned false"),
                    ["commandName"] = J.Str(commandName),
                });

            command.Execute(parameterValue);
            return DiagnosticResponse.Ok(new JsonObject
            {
                ["executed"] = J.Bool(true),
                ["commandName"] = J.Str(commandName),
                ["dataContextType"] = J.Str(dc.GetType().Name),
            });
        });
    }

    public static async Task<DiagnosticResponse> TakeScreenshot(DiagnosticRequest req)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var controlId = req.GetString("controlId");
            var windowIndex = req.GetInt("windowIndex");

            Control? target;
            if (!string.IsNullOrEmpty(controlId))
            {
                target = ControlResolver.Resolve(controlId);
            }
            else
            {
                var windows = ControlResolver.GetWindows();
                target = windowIndex < windows.Count ? windows[windowIndex] : null;
            }

            if (target is null)
                return DiagnosticResponse.Fail("Target control or window not found");

            var pixelSize = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
                return DiagnosticResponse.Fail("Control has zero size, cannot capture screenshot");

            var rtb = new RenderTargetBitmap(pixelSize);
            rtb.Render(target);

            using var ms = new MemoryStream();
            rtb.Save(ms);
            var base64 = Convert.ToBase64String(ms.ToArray());

            return DiagnosticResponse.Ok(new JsonObject
            {
                ["format"] = J.Str("png"),
                ["width"] = J.Int(pixelSize.Width),
                ["height"] = J.Int(pixelSize.Height),
                ["base64Data"] = J.Str(base64),
            });
        });
    }

    private static object? ConvertValue(string? value, Type targetType)
    {
        if (value is null) return null;
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.Parse(value);
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(double)) return double.Parse(value);
        if (targetType == typeof(float)) return float.Parse(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, ignoreCase: true);
        if (targetType == typeof(Thickness))
        {
            var parts = value.Split(',').Select(double.Parse).ToArray();
            return parts.Length switch
            {
                1 => new Thickness(parts[0]),
                2 => new Thickness(parts[0], parts[1]),
                4 => new Thickness(parts[0], parts[1], parts[2], parts[3]),
                _ => throw new ArgumentException("Invalid Thickness format"),
            };
        }
        return Convert.ChangeType(value, targetType);
    }
}
