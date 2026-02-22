using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
                    return DiagnosticResponse.Ok(new { clicked = true, method = "command", controlType = control.GetType().Name });
                }
            }

            // For non-button controls or buttons without command, try invoking click via automation
            if (control is Avalonia.Controls.Button btn2)
            {
                // Programmatically invoke click via reflection on the OnClick method
                var onClickMethod = typeof(Avalonia.Controls.Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                onClickMethod?.Invoke(btn2, null);
                return DiagnosticResponse.Ok(new { clicked = true, method = "onClick", controlType = control.GetType().Name });
            }

            return DiagnosticResponse.Ok(new { clicked = false, message = "Control is not a Button and pointer simulation is not supported. Use set_property or input_text instead.", controlType = control.GetType().Name });
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
                return DiagnosticResponse.Ok(new { set = true, property = propertyName, newValue = value });
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

            var control = ControlResolver.Resolve(controlId);
            if (control is null)
                return DiagnosticResponse.Fail($"Control '{controlId}' not found");

            if (control is TextBox textBox)
            {
                textBox.Text = text;
                return DiagnosticResponse.Ok(new { typed = true, controlType = "TextBox", text });
            }

            if (control is AutoCompleteBox acb)
            {
                acb.Text = text;
                return DiagnosticResponse.Ok(new { typed = true, controlType = "AutoCompleteBox", text });
            }

            return DiagnosticResponse.Fail($"Control type {control.GetType().Name} does not support text input. Use a TextBox or similar.");
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

            return DiagnosticResponse.Ok(new
            {
                format = "png",
                width = pixelSize.Width,
                height = pixelSize.Height,
                base64Data = base64,
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
