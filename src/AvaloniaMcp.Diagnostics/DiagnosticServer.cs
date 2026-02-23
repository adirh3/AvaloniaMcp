using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        TypeInfoResolver = DiagnosticJsonContext.Default,
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

        // Write discovery file so the MCP tool can find us
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
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);
                // Handle client on a background task; pipe ownership transferred
                _ = HandleClientAsync(pipe, ct);
                pipe = null; // ownership transferred
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Server error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var encoding = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, encoding, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, encoding, 1024, leaveOpen: true);
            writer.AutoFlush = true;

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (IOException)
                {
                    break; // client disconnected
                }
                
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                DiagnosticRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<DiagnosticRequest>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    Log($"Failed to parse request: {ex.Message}");
                    await WriteResponse(writer, pipe, DiagnosticResponse.Fail($"Invalid JSON: {ex.Message}"));
                    continue;
                }

                if (request is null)
                {
                    await WriteResponse(writer, pipe, DiagnosticResponse.Fail("Invalid request"));
                    continue;
                }

                DiagnosticResponse response;
                try
                {
                    response = await DispatchAsync(request);
                }
                catch (Exception ex)
                {
                    Log($"Handler error for '{request.Method}': {ex.Message}");
                    response = DiagnosticResponse.Fail($"Handler error: {ex.Message}");
                }

                string responseJson;
                try
                {
                    responseJson = JsonSerializer.Serialize(response, JsonOptions);
                }
                catch (Exception serEx)
                {
                    Log($"Serialization error for '{request.Method}': {serEx.Message}");
                    responseJson = $"{{\"success\":false,\"data\":null,\"error\":\"Serialization error: {serEx.Message.Replace("\"", "'")}\"}}";
                }

                try
                {
                    await writer.WriteLineAsync(responseJson);
                    await writer.FlushAsync(ct);
                    await pipe.FlushAsync(ct);
                }
                catch (IOException)
                {
                    break; // client disconnected
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Client handler error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    private static async Task WriteResponse(StreamWriter writer, NamedPipeServerStream pipe, DiagnosticResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(json);
        await writer.FlushAsync();
        await pipe.FlushAsync();
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
            "ping" => DiagnosticResponse.Ok(new JsonObject { ["status"] = "ok", ["pid"] = Environment.ProcessId }),
            _ => DiagnosticResponse.Fail($"Unknown method: {request.Method}"),
        };
    }

    private void WriteDiscoveryFile()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "avalonia-mcp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Environment.ProcessId}.json");
            var info = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["pipeName"] = _pipeName,
                ["processName"] = Process.GetCurrentProcess().ProcessName,
                ["startTime"] = DateTime.UtcNow.ToString("O"),
            };
            var json = info.ToJsonString();
            File.WriteAllText(path, json);
            Log($"Discovery file written: {path}");
        }
        catch (Exception ex)
        {
            Log($"Failed to write discovery file: {ex.Message}");
        }
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

    private static void Log(string message)
    {
        var msg = $"[AvaloniaMcp] {message}";
        Debug.WriteLine(msg);
        Trace.WriteLine(msg);
        Console.Error.WriteLine(msg);
    }
}
