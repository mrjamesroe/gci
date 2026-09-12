using System.Windows;
using Gci.App.Services;
using Gci.App.ViewModels;
using Gci.App.Views;
using Gci.Core.Providers;
using Gci.Core.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Gci.App;

public partial class App : Application
{
    private const string InstanceName = "GCI.GeorgiaCannabisInventory";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private InventoryService? _inventory;
    private Notifier? _notifier;
    private ThumbnailService? _thumbnails;
    private FeedService? _feeds;
    private UpdateChecker? _updates;
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch just brings the running copy forward. Right after an update the previous version may
        // still be shutting down, so the new one waits for it instead of deferring to it.
        var justUpdated = e.Args.Contains(UpdateInstaller.UpdatedFlag);
        _singleInstance = new Mutex(false, $"{InstanceName}.Mutex");
        bool isFirst;
        try
        {
            isFirst = _singleInstance.WaitOne(justUpdated ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            isFirst = true; // previous copy exited without releasing; we own it now
        }
        if (!isFirst)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            try { EventWaitHandle.OpenExisting($"{InstanceName}.Show").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"{InstanceName}.Show");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "GCI", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // --data <folder> keeps a separate profile (handy for testing); default is %LOCALAPPDATA%\GCI.
        var dataIndex = Array.IndexOf(e.Args, "--data");
        var data = new DataStore(dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : null);
        _inventory = new InventoryService(data);
        _notifier = new Notifier();
        var userAgent = new ProviderConfig().UserAgent;
        _thumbnails = new ThumbnailService(data, userAgent, Dispatcher);
        Thumb.Service = _thumbnails;
        _feeds = new FeedService(data, userAgent: userAgent);
        _updates = new UpdateChecker();
        _vm = new MainViewModel(data, _inventory, new ProfileStore(data), _notifier, _thumbnails, _feeds, _updates, e.Args);
        _vm.ExitRequested += ExitApp;
        _window = new MainWindow(_vm);
        _window.Closed += (_, _) => ExitApp();

        if (justUpdated)
        {
            _ = UpdateInstaller.CleanupAsync();
            _notifier.ShowToast($"GCI updated to {_vm.CurrentVersion}", "Your watches, settings and history carried over.",
                "GCI", _vm.ReleasesPage, null, "What's new");
        }

        _tray = new TrayIcon(
            show: ShowWindow,
            refresh: () => _ = _vm.RefreshAsync(),
            togglePause: () => { _vm.TogglePauseCommand.Execute(null); _tray!.SetPaused(_vm.IsPaused); },
            exit: ExitApp);
        _vm.TrayStatusChanged += status => _tray.SetStatus(status);
        _window.HiddenToTray += (_, _) =>
            _tray.ShowBalloon("GCI is still running", "It keeps watching for changes. Right-click the leaf icon to exit.");

        ToastNotificationManagerCompat.OnActivated += args =>
        {
            var toastArgs = ToastArguments.Parse(args.Argument);
            Dispatcher.BeginInvoke(() =>
            {
                var action = toastArgs.TryGetValue("action", out var a) ? a : "open";
                if (action == "open" && toastArgs.TryGetValue("url", out var url)) _vm.OpenUrlCommand.Execute(url);
                else ShowWindow();
            });
        };

        var startHidden = e.Args.Contains("--minimized") || _vm.Settings.StartMinimized;
        if (!startHidden || ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
            _window.Show();
        Dispatcher.BeginInvoke(() => _ = _vm.RefreshAsync(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ShowWindow() => _window?.BringToFront();

    private bool _exiting;

    private void ExitApp()
    {
        // Closing the window raises Closed, which calls back in here.
        if (_exiting) return;
        _exiting = true;
        if (_window is { AllowClose: false } w)
        {
            w.AllowClose = true;
            w.Close();
        }
        _tray?.Dispose();
        _tray = null;
        _vm?.Dispose();
        _inventory?.Dispose();
        _notifier?.Dispose();
        _thumbnails?.Dispose();
        _thumbnails = null;
        _feeds?.Dispose();
        _feeds = null;
        _updates?.Dispose();
        _updates = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
