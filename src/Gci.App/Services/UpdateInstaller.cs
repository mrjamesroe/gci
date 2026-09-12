using System.Diagnostics;
using System.IO;

namespace Gci.App.Services;

/// <summary>
/// Swaps a verified new gci.exe into place and restarts. Windows won't let a running exe be overwritten but does let
/// it be renamed, so the current file becomes gci.exe.old, the new one takes its name (and its "Start with Windows"
/// entry keeps working), and the old file is deleted by the next launch.
/// </summary>
public static class UpdateInstaller
{
    public const string UpdatedFlag = "--updated";

    private static string CurrentExe => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    /// <summary>Whether this copy can replace itself; otherwise the release page is opened instead.</summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "GCI isn't running from its exe.";
            return false;
        }
        var dir = Path.GetDirectoryName(exe)!;
        if (dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            reason = "This is a development build; updates install over published copies only.";
            return false;
        }
        // Releases are single-file; a loose gci.dll means a multi-file build that swapping the exe alone would break.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "gci.dll")))
        {
            reason = "This is a framework-dependent build; download the new version from the release page.";
            return false;
        }
        try
        {
            var probe = Path.Combine(dir, $".gci-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception)
        {
            reason = $"GCI can't write to {dir}; move gci.exe to a folder you own (e.g. Documents) to enable updates.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Puts <paramref name="downloadedExe"/> in place of the running exe and starts it with the same arguments.</summary>
    public static void InstallAndRestart(string downloadedExe, IEnumerable<string> args)
    {
        var current = CurrentExe;
        var old = current + ".old";
        TryDelete(old);
        File.Move(current, old);
        try
        {
            File.Move(downloadedExe, current);
        }
        catch
        {
            File.Move(old, current); // put the working copy back
            throw;
        }

        var start = new ProcessStartInfo(current) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current)! };
        foreach (var a in args.Where(a => a != UpdatedFlag)) start.ArgumentList.Add(a);
        start.ArgumentList.Add(UpdatedFlag);
        Process.Start(start);
    }

    /// <summary>Removes the previous version left behind by an update (it may take a moment to exit).</summary>
    public static async Task CleanupAsync()
    {
        var old = Environment.ProcessPath + ".old";
        for (var attempt = 0; attempt < 20 && File.Exists(old); attempt++)
        {
            if (TryDelete(old)) return;
            await Task.Delay(500);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
