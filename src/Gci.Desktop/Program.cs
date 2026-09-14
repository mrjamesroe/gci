using System.Security.Cryptography;
using System.Text;
using Avalonia;

namespace Gci.Desktop;

internal static class Program
{
    // Avalonia needs an STA thread and must be initialised before any control is created.
    [STAThread]
    public static void Main(string[] args)
    {
        // One copy per data folder: a second launch (including a second downloaded copy of the app) exits at once
        // instead of leaving another window/Dock icon behind. Scoped to the data folder so a --data test profile can
        // run alongside the normal one. The named mutex is cross-process on Windows and macOS.
        using var single = new Mutex(false, SingleInstanceName(args));
        var owned = false;
        try
        {
            try { owned = single.WaitOne(0, false); }
            catch (AbandonedMutexException) { owned = true; } // previous copy exited without releasing; we own it now
            if (!owned) return;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (owned) single.ReleaseMutex();
        }
    }

    private static string SingleInstanceName(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        var data = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : "default";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..12];
        return $"GCI.GeorgiaCannabisInventory.{hash}";
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()   // also selects the OS default UI font: Segoe UI on Windows, San Francisco on macOS
            .LogToTrace();
    // Note: no .WithInterFont() — we honor each platform's system UI font instead of bundling Inter.
}
