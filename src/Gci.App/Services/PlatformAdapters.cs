using System.Windows.Threading;

namespace Gci.App.Services;

/// <summary>Windows implementations of the shared platform-service interfaces, wrapping the app's static helpers.</summary>
public sealed class StartupRegistrationService : IStartupRegistration
{
    public void Apply(bool enabled) => StartupRegistration.Apply(enabled);
}

public sealed class UpdateInstallerService : IUpdateInstaller
{
    public bool CanSelfUpdate(out string reason) => UpdateInstaller.CanSelfUpdate(out reason);
    public void InstallAndRestart(string downloadedExe, IEnumerable<string> args) => UpdateInstaller.InstallAndRestart(downloadedExe, args);
}

public sealed class SystemSnapshotService : ISystemSnapshot
{
    public Dictionary<string, object?> Snapshot() => TelemetryEnvironment.Snapshot();
}

public sealed class WpfClipboard : IClipboard
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}

/// <summary>An <see cref="ITicker"/> backed by a WPF <see cref="DispatcherTimer"/> that ticks on the UI thread.</summary>
public sealed class DispatcherTicker : ITicker
{
    private readonly DispatcherTimer _timer;

    public DispatcherTicker(TimeSpan interval, Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public event Action? Tick;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
