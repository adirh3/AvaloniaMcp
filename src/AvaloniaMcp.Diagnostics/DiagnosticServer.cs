using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvaloniaMcp.Diagnostics.Handlers;
using System.Text.Json.Serialization;
using AvaloniaMcp.Diagnostics.Protocol;


/// <summary>
/// Named-pipe server that runs inside the Avalonia app and handles diagnostic requests.
/// </summary>
public sealed class DiagnosticServer : IDisposable
{
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string PipeName => _pipeName;

    public DiagnosticServer(string? pipeName = null)
    {
        _pipeName = pipeName ?? $"avalonia-mcp-{Environment.ProcessId}";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunServerAsync(_cts.Token));

        // Write discovery file
        WriteDiscoveryFile();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        RemoveDiscoveryFile();
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);
                // Handle client on a background task; pipe ownership transferred
                _ = HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AvaloniaMcp] Server error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using var _ = pipe; // dispose when handler completes
            var encoding = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { AutoFlush = true };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                var request = JsonSerializer.Deserialize<DiagnosticRequest>(line, JsonOptions);
                if (request is null)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        DiagnosticResponse.Fail("Invalid request"), JsonOptions));
                    continue;
                }

                DiagnosticResponse response;
                try
                {
                    response = await DispatchAsync(request);
                }
                catch (Exception ex)
                {
                    response = DiagnosticResponse.Fail($"Handler error: {ex.Message}");
                }

                string responseJson;
                try
                {
                    responseJson = JsonSerializer.Serialize(response, JsonOptions);
                }
                catch (Exception serEx)
                {
                    responseJson = JsonSerializer.Serialize(
                        DiagnosticResponse.Fail($"Serialization error: {serEx.Message}"), JsonOptions);
                }

                await writer.WriteLineAsync(responseJson);
                await writer.FlushAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AvaloniaMcp] Client handler error: {ex.Message}");
        }
    }

    private static async Task<DiagnosticResponse> DispatchAsync(DiagnosticRequest request)
    {
        return request.Method switch
        {
            "list_windows" => await InspectionHandler.ListWindows(),
            "get_visual_tree" => await InspectionHandler.GetVisualTree(request),
            "get_logical_tree" => await InspectionHandler.GetLogicalTree(request),
            "find_control" => await InspectionHandler.FindControl(request),
            "get_focused_element" => await InspectionHandler.GetFocusedElement(),
            "get_control_properties" => await PropertyHandler.GetControlProperties(request),
            "get_data_context" => await PropertyHandler.GetDataContext(request),
            "get_applied_styles" => await PropertyHandler.GetAppliedStyles(request),
            "get_resources" => await PropertyHandler.GetResources(request),
            "get_binding_errors" => await PropertyHandler.GetBindingErrors(),
            "click_control" => await InteractionHandler.ClickControl(request),
            "set_property" => await InteractionHandler.SetProperty(request),
            "input_text" => await InteractionHandler.InputText(request),
            "take_screenshot" => await InteractionHandler.TakeScreenshot(request),
            "ping" => Task.FromResult(DiagnosticResponse.Ok(new { status = "ok", pid = Environment.ProcessId })).Result,
            _ => DiagnosticResponse.Fail($"Unknown method: {request.Method}"),
        };
    }

    private void WriteDiscoveryFile()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "avalonia-mcp");
            Directory.CreateDirectory(dir);
            var info = new
            {
                pid = Environment.ProcessId,
                pipeName = _pipeName,
                processName = Process.GetCurrentProcess().ProcessName,
                startTime = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(
                Path.Combine(dir, $"{Environment.ProcessId}.json"),
                JsonSerializer.Serialize(info, JsonOptions));
        }
        catch { /* best effort */ }
    }

    private void RemoveDiscoveryFile()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "avalonia-mcp", $"{Environment.ProcessId}.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }
}
