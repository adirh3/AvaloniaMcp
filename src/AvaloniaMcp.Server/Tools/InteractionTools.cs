using System.ComponentModel;
using ModelContextProtocol.Server;
using AvaloniaMcp.Server.Services;

namespace AvaloniaMcp.Server.Tools;

[McpServerToolType]
public sealed class InteractionTools
{
    [McpServerTool(Name = "click_control", ReadOnly = false, Destructive = false),
     Description("Simulate a click on a control. For Buttons, executes the bound Command if available; otherwise simulates pointer press/release events.")]
    public static async Task<string> ClickControl(
        AvaloniaConnection connection,
        [Description("Control identifier: '#Name' or 'TypeName[index]'.")] string controlId,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("click_control", new()
        {
            ["controlId"] = controlId,
        }, ct);
    }

    [McpServerTool(Name = "set_property", ReadOnly = false, Destructive = false),
     Description("Set an Avalonia property on a control at runtime. Useful for live debugging — change visibility, width, margin, content, etc. without restarting the app.")]
    public static async Task<string> SetProperty(
        AvaloniaConnection connection,
        [Description("Control identifier.")] string controlId,
        [Description("Property name (e.g. 'IsVisible', 'Width', 'Margin', 'Content').")] string propertyName,
        [Description("Value as string. Will be converted to the property's type. For Thickness: '10' or '10,5' or '10,5,10,5'. For bool: 'true'/'false'. For enum: the enum name.")] string value,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("set_property", new()
        {
            ["controlId"] = controlId,
            ["propertyName"] = propertyName,
            ["value"] = value,
        }, ct);
    }

    [McpServerTool(Name = "input_text", ReadOnly = false, Destructive = false),
     Description("Type text into a TextBox or similar input control. If the target control is not a TextBox (e.g. a composite UserControl), automatically finds the first TextBox child inside it. Use pressEnter to simulate pressing Enter after typing (e.g. to submit a chat message).")]
    public static async Task<string> InputText(
        AvaloniaConnection connection,
        [Description("Control identifier for the TextBox or a parent control containing a TextBox.")] string controlId,
        [Description("The text to enter.")] string text,
        [Description("If true, simulates pressing Enter after typing. Useful for submitting forms or sending messages.")] bool pressEnter = false,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("input_text", new()
        {
            ["controlId"] = controlId,
            ["text"] = text,
            ["pressEnter"] = pressEnter,
        }, ct);
    }

    [McpServerTool(Name = "take_screenshot", ReadOnly = true, Destructive = false),
     Description("Capture a screenshot of a window or specific control as a base64-encoded PNG. Returns image dimensions and data. Use this for visual verification of UI state.")]
    public static async Task<string> TakeScreenshot(
        AvaloniaConnection connection,
        [Description("Index of the window to capture. Default: 0.")] int windowIndex = 0,
        [Description("Control identifier to capture a specific control instead of the whole window.")] string? controlId = null,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("take_screenshot", new()
        {
            ["windowIndex"] = windowIndex,
            ["controlId"] = controlId,
        }, ct);
    }

    [McpServerTool(Name = "invoke_command", ReadOnly = false, Destructive = false),
     Description("Execute an ICommand on a control's DataContext (ViewModel). This is the most direct way to trigger ViewModel actions like SendMessage, Save, Delete, etc. without needing to find and click the bound button.")]
    public static async Task<string> InvokeCommand(
        AvaloniaConnection connection,
        [Description("Control identifier whose DataContext contains the command.")] string controlId,
        [Description("Name of the ICommand property on the DataContext (e.g. 'SendMessageCommand', 'SaveCommand').")]
        string commandName,
        [Description("Optional parameter to pass to the command's Execute method.")] string? parameter = null,
        [Description("Process ID of the Avalonia app to connect to. If omitted, auto-discovers.")] int? pid = null,
        CancellationToken ct = default)
    {
        if (pid.HasValue) connection.SwitchTo(pid.Value);
        return await connection.RequestAsync("invoke_command", new()
        {
            ["controlId"] = controlId,
            ["commandName"] = commandName,
            ["parameter"] = parameter,
        }, ct);
    }
}
