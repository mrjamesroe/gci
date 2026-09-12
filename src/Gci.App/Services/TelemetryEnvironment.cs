using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Gci.Core.Services;
using Microsoft.Win32;

namespace Gci.App.Services;

/// <summary>Non-identifying facts about the PC and build, for telemetry.</summary>
public static class TelemetryEnvironment
{
    /// <summary>
    /// Development builds (Debug, or running from a bin folder) report as debug so Aptabase keeps them out of
    /// production numbers. GCI_TELEMETRY_DEBUG=1 forces the same for testing a published copy.
    /// </summary>
    public static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return Environment.GetEnvironmentVariable("GCI_TELEMETRY_DEBUG") == "1"
                   || Environment.ProcessPath?.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase) == true;
#endif
        }
    }

    public static TelemetrySystemInfo SystemInfo()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(TelemetryEnvironment).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var commit = informational?.Split('+') is [_, var hash, ..] ? hash[..Math.Min(7, hash.Length)] : null;
        var os = Environment.OSVersion.Version;
        return new TelemetrySystemInfo($"{os.Major}.{os.Minor}.{os.Build}", CultureInfo.CurrentCulture.Name, version, commit,
            RuntimeInformation.OSArchitecture.ToString(), IsDebugBuild);
    }

    public static Dictionary<string, object?> Snapshot()
    {
        var build = Environment.OSVersion.Version.Build;
        var props = new Dictionary<string, object?>
        {
            ["windows"] = build >= 22000 ? "11" : "10",
            ["os_build"] = build,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["dotnet"] = Environment.Version.ToString(3),
            ["cpu_count"] = Environment.ProcessorCount,
            ["ram_gb"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024),
            ["culture"] = CultureInfo.CurrentCulture.Name,
            ["ui_culture"] = CultureInfo.CurrentUICulture.Name,
            ["timezone"] = TimeZoneInfo.Local.Id,
            ["high_contrast"] = SystemParameters.HighContrast,
            ["install_location"] = InstallLocation(),
        };
        try
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            props["screen"] = primary is null ? null : $"{primary.Bounds.Width}x{primary.Bounds.Height}";
            props["monitors"] = System.Windows.Forms.Screen.AllScreens.Length;
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            props["dpi_scale"] = Math.Round(g.DpiX / 96.0 * 100);
        }
        catch (Exception)
        {
            // Headless or restricted session.
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) props["dark_mode"] = light == 0;
        }
        catch (Exception)
        {
            // Not available on every edition.
        }
        return props;
    }

    /// <summary>Where the exe lives, as a category rather than a path.</summary>
    private static string InstallLocation()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        bool Under(Environment.SpecialFolder f) =>
            Environment.GetFolderPath(f) is { Length: > 0 } root && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        if (dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return "dev_build";
        if (dir.Contains("Downloads", StringComparison.OrdinalIgnoreCase)) return "downloads";
        if (Under(Environment.SpecialFolder.Desktop)) return "desktop";
        if (Under(Environment.SpecialFolder.MyDocuments)) return "documents";
        if (Under(Environment.SpecialFolder.ProgramFiles) || Under(Environment.SpecialFolder.ProgramFilesX86)) return "program_files";
        if (Under(Environment.SpecialFolder.LocalApplicationData) || Under(Environment.SpecialFolder.ApplicationData)) return "appdata";
        if (Under(Environment.SpecialFolder.UserProfile)) return "user_folder";
        return "other";
    }
}
