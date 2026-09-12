using Microsoft.Win32;

namespace Gci.App.Services;

/// <summary>Per-user "start with Windows" entry under HKCU\...\Run.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GCI";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
