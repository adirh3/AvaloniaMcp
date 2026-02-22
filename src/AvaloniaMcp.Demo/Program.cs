using Avalonia;
using AvaloniaMcp.Diagnostics;

namespace AvaloniaMcp.Demo;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseMcpDiagnostics()   // ← enables AvaloniaMcp diagnostic server
            .LogToTrace();
}
