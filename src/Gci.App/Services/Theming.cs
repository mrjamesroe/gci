using System.Windows;
using System.Windows.Media;
using Gci.Core.Models;
using Microsoft.Win32;

namespace Gci.App.Services;

/// <summary>
/// Applies the light or dark palette by swapping the values of the themeable brush resources (which the XAML
/// references via DynamicResource). Light mode uses the same colors the app has always shipped, so nothing about the
/// light appearance changes; dark mode overrides them. "System" follows the Windows apps light/dark setting.
/// </summary>
public static class Theming
{
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Ground"] = "#F5F6F3", ["Card"] = "#FFFFFF", ["Line"] = "#E1E4DE", ["Ink"] = "#1D2420",
        ["MutedBrush"] = "#66706A", ["AccentSoft"] = "#E8F3E9",
        ["WarnBannerBg"] = "#FFF6DD", ["InfoBannerBg"] = "#EEF3FB", ["DangerBannerBg"] = "#FDECEA",
        ["TilePlaceholder"] = "#F0F2EE", ["HeaderBg"] = "#F0F2EE", ["AltRow"] = "#FAFBF9", ["GridLine"] = "#EEF0EC",
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Ground"] = "#161A18", ["Card"] = "#212623", ["Line"] = "#333B36", ["Ink"] = "#E6EAE7",
        ["MutedBrush"] = "#9AA39D", ["AccentSoft"] = "#21372B",
        ["WarnBannerBg"] = "#37331F", ["InfoBannerBg"] = "#232B34", ["DangerBannerBg"] = "#3A2420",
        ["TilePlaceholder"] = "#2A302C", ["HeaderBg"] = "#2A302C", ["AltRow"] = "#1E231F", ["GridLine"] = "#333B36",
    };

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        var palette = ResolveDark(theme) ? Dark : Light;
        foreach (var (key, hex) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static bool ResolveDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => IsWindowsDark(),
    };

    /// <summary>True when Windows is set to a dark app theme.</summary>
    private static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false; // default to light if the setting can't be read
        }
    }
}
