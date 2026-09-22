using System.Diagnostics;
using Gci.App.Services;

namespace Gci.Desktop.Services;

/// <summary>
/// Self-update for the macOS app bundle. GCI runs from GCI.app/Contents/MacOS/gci; a running bundle can't be swapped
/// in-process, so a tiny detached shell script waits for this process to quit, replaces the .app, and relaunches it.
///
/// Because the build isn't notarized yet, a freshly downloaded bundle carries com.apple.quarantine and Gatekeeper
/// would block the relaunch — so the installer strips that attribute (xattr) before opening the new app. Once the
/// release is signed + notarized this is harmless and no longer required.
///
/// Written on Windows; verify on a real Mac.
/// </summary>
public sealed class MacUpdateInstaller : IUpdateInstaller
{
    private const string AppMarker = ".app/Contents/MacOS/";

    /// <summary>Whether this copy can replace itself; otherwise the release page is opened instead.</summary>
    public bool CanSelfUpdate(out string reason)
    {
        var exe = Environment.ProcessPath;
        if (!OperatingSystem.IsMacOS() || exe is null || !exe.Contains(AppMarker, StringComparison.Ordinal))
        {
            reason = "In-app updates need GCI to run from its app bundle; download the latest from the releases page.";
            return false;
        }
        if (exe.Contains("/bin/Debug/", StringComparison.Ordinal) || exe.Contains("/bin/Release/", StringComparison.Ordinal))
        {
            reason = "This is a development build; updates install over the released app only.";
            return false;
        }
        var parent = Directory.GetParent(AppPath(exe))?.FullName;
        if (parent is null || !IsWritable(parent))
        {
            reason = "GCI can't write next to its app bundle; move GCI.app to a folder you own (e.g. Applications or Downloads) to enable updates.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Replaces the running .app with the one inside <paramref name="downloadedZip"/> and relaunches it.</summary>
    public void InstallAndRestart(string downloadedZip, IEnumerable<string> args)
    {
        var app = AppPath(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."));
        var extractDir = Path.Combine(Path.GetTempPath(), $"gci-update-{Environment.ProcessId}");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        // Expand the bundle zip (ditto preserves the .app structure, symlinks and exec bits).
        Run("/usr/bin/ditto", "-x", "-k", downloadedZip, extractDir);
        var newApp = Directory.EnumerateDirectories(extractDir, "*.app").FirstOrDefault()
                     ?? throw new InvalidOperationException("The downloaded update didn't contain an app bundle.");

        // Strip quarantine on the new bundle so Gatekeeper doesn't block the relaunch (unsigned build).
        Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", newApp);

        var script = Path.Combine(extractDir, "swap.sh");
        File.WriteAllText(script, $"""
            #!/bin/sh
            while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.3; done
            rm -rf "{app}"
            mv "{newApp}" "{app}"
            xattr -dr com.apple.quarantine "{app}" 2>/dev/null
            open "{app}"
            rm -rf "{extractDir}"

            """);

        // Detached: keeps running after this process exits, then does the swap + relaunch.
        Process.Start(new ProcessStartInfo("/bin/sh", $"\"{script}\"") { UseShellExecute = false });
    }

    /// <summary>The GCI.app path from an executable path inside it.</summary>
    private static string AppPath(string exe)
    {
        var i = exe.IndexOf(AppMarker, StringComparison.Ordinal);
        return i < 0 ? exe : exe[..(i + 4)]; // up to and including ".app"
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".gci-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't run {file}.");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {p.StandardError.ReadToEnd().Trim()}");
    }
}
