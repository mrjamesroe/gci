using Avalonia;

namespace Gci.Desktop;

internal static class Program
{
    // Avalonia needs an STA thread and must be initialised before any control is created.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()   // WebView2 host on Windows, Cocoa on macOS, X11 on Linux
            .WithInterFont()
            .LogToTrace();
}
