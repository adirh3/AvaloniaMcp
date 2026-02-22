# AvaloniaMcp

[MCP](https://modelcontextprotocol.io/) server for debugging [Avalonia UI](https://avaloniaui.net/) applications. Connects to a running Avalonia app and provides 15 tools for live inspection, debugging, and interaction — designed for use with AI coding agents like GitHub Copilot.

## What It Does

Add one line to your Avalonia app and get a full diagnostic server that AI agents (or humans via CLI) can use to:

- **Inspect** the visual/logical tree, control properties, styles, and resources
- **Debug** data bindings, ViewModels, and binding errors
- **Interact** — click buttons, type text, change properties at runtime
- **Screenshot** — capture windows or individual controls as PNG

## Architecture

```
┌─────────────────────────┐          Named Pipe          ┌──────────────────────┐
│   Your Avalonia App     │  ◄──── (avalonia-mcp-{pid}) ──── │   MCP Server         │
│                         │                               │                      │
│  ┌───────────────────┐  │     JSON request/response     │  ┌────────────────┐  │
│  │ .UseMcpDiagnostics()│ │  ◄─────────────────────────  │  │ 15 MCP Tools   │  │
│  │ DiagnosticServer  │  │          (stdio)              │  │ CLI mode       │  │
│  └───────────────────┘  │                               │  └────────────────┘  │
└─────────────────────────┘                               └──────────────────────┘
                                                                    ▲
                                                                    │ MCP (stdio)
                                                               ┌────┴────┐
                                                               │ VS Code │
                                                               │ Copilot │
                                                               └─────────┘
```

| Project | Description |
|---|---|
| **AvaloniaMcp.Diagnostics** | Library added to your Avalonia app. Starts a named-pipe diagnostic server. |
| **AvaloniaMcp.Server** | MCP server (stdio) + CLI. Connects to the app and exposes all tools. |
| **AvaloniaMcp.Demo** | Sample Avalonia app with data binding, lists, styles — for testing. |

## Quick Start

### 1. Add diagnostics to your Avalonia app

Add a reference to `AvaloniaMcp.Diagnostics` and one line to your `Program.cs`:

```csharp
using AvaloniaMcp.Diagnostics;

public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .UseMcpDiagnostics()   // ← add this line
        .LogToTrace();
```

This starts a named-pipe server inside your app at startup. It has zero UI impact — all introspection happens on-demand via the pipe.

### 2. Install the MCP server as a dotnet tool

```bash
# From the repo root:
dotnet pack src/AvaloniaMcp.Server -o nupkg
dotnet new tool-manifest   # if you don't already have one
dotnet tool install AvaloniaMcp --add-source ./nupkg
```

Verify:
```bash
dotnet avalonia-mcp cli --help
```

### 3. Configure in VS Code / GitHub Copilot

Add to your **global** MCP settings (`Settings → MCP` or `~/.vscode/mcp.json`):

```json
{
  "servers": {
    "avalonia-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["avalonia-mcp"]
    }
  }
}
```

Or per-workspace in `.vscode/mcp.json`:

```json
{
  "servers": {
    "avalonia-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["avalonia-mcp"]
    }
  }
}
```

The server auto-discovers running Avalonia apps. If multiple apps are running, pass a PID:

```json
"args": ["avalonia-mcp", "--pid", "12345"]
```

## Using the CLI

The tool also works as a standalone CLI for quick debugging:

```bash
# Start your Avalonia app first, then:
dotnet avalonia-mcp cli list_windows
dotnet avalonia-mcp cli get_visual_tree --maxDepth 5
dotnet avalonia-mcp cli find_control --typeName Button
dotnet avalonia-mcp cli get_control_properties --controlId "#MyButton"
dotnet avalonia-mcp cli get_data_context
dotnet avalonia-mcp cli get_binding_errors
dotnet avalonia-mcp cli click_control --controlId "#MyButton"
dotnet avalonia-mcp cli input_text --controlId "#MyTextBox" --text "Hello"
dotnet avalonia-mcp cli set_property --controlId "#MyButton" --propertyName IsVisible --value false
dotnet avalonia-mcp cli take_screenshot
```

Auto-discovery is built in — if only one app is running, no `--pipe` or `--pid` needed.

## Tools Reference

### Inspection
| Tool | Parameters | Description |
|---|---|---|
| `list_windows` | — | List all open windows with title, size, position, state |
| `get_visual_tree` | `maxDepth?` | Full visual element hierarchy with type, name, bounds, visibility |
| `get_logical_tree` | `maxDepth?` | XAML-intent logical tree (controls you declared, not template internals) |
| `find_control` | `name?`, `typeName?`, `text?` | Search by Name (`#MyBtn`), type (`Button`), or text content |
| `get_control_properties` | `controlId` | All Avalonia properties: values, types, whether explicitly set |

### Data & Binding
| Tool | Parameters | Description |
|---|---|---|
| `get_data_context` | `controlId?` | ViewModel type and all public properties, serialized as JSON |
| `get_binding_errors` | — | All broken bindings with error messages and timestamps |

### Visual & Style
| Tool | Parameters | Description |
|---|---|---|
| `take_screenshot` | `controlId?` | Capture window or specific control as base64 PNG |
| `get_applied_styles` | `controlId` | CSS classes, pseudo-classes (`:pointerover`, `:pressed`), style setters |
| `get_resources` | `controlId?` | Resource dictionary entries (brushes, templates, converters) |
| `get_focused_element` | — | Currently focused element info |

### Interaction
| Tool | Parameters | Description |
|---|---|---|
| `click_control` | `controlId` | Simulate click — executes bound Command for buttons |
| `set_property` | `controlId`, `propertyName`, `value` | Set any Avalonia property at runtime |
| `input_text` | `controlId`, `text` | Type text into TextBox or input controls |

### Discovery
| Tool | Parameters | Description |
|---|---|---|
| `discover_apps` | — | Find running Avalonia apps with MCP diagnostics enabled |

## Control Identifiers

Many tools accept a `controlId` parameter. Three formats are supported:

| Format | Example | What it matches |
|---|---|---|
| `#Name` | `#MyButton` | Control with `Name="MyButton"` |
| `TypeName` | `Button` | First control of that type in the visual tree |
| `TypeName[n]` | `Button[2]` | Nth control of that type (0-indexed) |

## How It Works

1. **Startup** — `UseMcpDiagnostics()` starts a named-pipe server (`avalonia-mcp-{pid}`) and writes a discovery file to `%TEMP%/avalonia-mcp/{pid}.json`
2. **Connection** — The MCP server (or CLI) reads the discovery file to find the pipe name, then connects
3. **Request/Response** — Newline-delimited JSON over the named pipe: `{"method":"list_windows","params":{}}`
4. **UI Thread** — All visual tree operations are marshaled to the Avalonia dispatcher thread for safety
5. **MCP Protocol** — The server wraps pipe responses into MCP `tools/call` results over stdio

## Requirements

- .NET 10 SDK (Preview)
- Avalonia 11.2+

## Building

```bash
dotnet build
```

## Running the Demo

```bash
# Terminal 1: Start the demo app
dotnet run --project src/AvaloniaMcp.Demo

# Terminal 2: Interact via CLI
dotnet avalonia-mcp cli list_windows
dotnet avalonia-mcp cli find_control --typeName Button
dotnet avalonia-mcp cli click_control --controlId "#IncrementBtn"
dotnet avalonia-mcp cli get_data_context
dotnet avalonia-mcp cli take_screenshot
```

## License

MIT
