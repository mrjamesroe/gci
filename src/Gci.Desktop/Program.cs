using Avalonia;

namespace Gci.Desktop;

internal static class Program
{
    // Avalonia needs an STA thread and must be initialised before any control is created.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()   // also selects the OS default UI font: Segoe UI on Windows, San Francisco on macOS
            .LogToTrace();
    // Note: no .WithInterFont() — we honor each platform's system UI font instead of bundling Inter.
}
