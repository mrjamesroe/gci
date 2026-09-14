using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Gci.App.ViewModels;
using Gci.Core.Providers;
using Gci.Core.Services;
using Gci.Desktop.Services;
using Gci.Desktop.Views;

namespace Gci.Desktop;

public partial class App : Application
{
    private InventoryService? _inventory;
    private FeedService? _feeds;
    private UpdateChecker? _updates;
    private TelemetryClient? _telemetry;
    private DesktopThumbnailService? _thumbnails;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // A tray icon keeps GCI monitoring after the window is closed, so we drive shutdown ourselves.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var args = desktop.Args ?? [];

            // --data <folder> keeps a separate profile (handy for testing); default is the per-user app-data folder.
            var dataIndex = Array.IndexOf(args, "--data");
            var customData = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : null;

            var data = new DataStore(customData);
            var userAgent = new ProviderConfig().UserAgent;
            // No embedded browser on macOS yet (Phase 4: WKWebView), so Cloudflare-only menus fall back to curl.
            _inventory = new InventoryService(data);
            _feeds = new FeedService(data, userAgent: userAgent);
            _updates = new UpdateChecker();
            _telemetry = new TelemetryClient(data, DesktopSystemSnapshot.SystemInfo());
            _thumbnails = new DesktopThumbnailService(data, userAgent);
            Views.Thumb.Service = _thumbnails;
            _vm = new MainViewModel(data, _inventory, new KeychainProfileStore(data), new DesktopNotifier(), _thumbnails,
                _feeds, _updates, _telemetry, new NoopEmbeddedBrowser(), new NoopUpdateInstaller(), new NoopStartupRegistration(),
                new DesktopSystemSnapshot(), new DesktopClipboard(), new DesktopTicker(TimeSpan.FromSeconds(15)), args);

            _window = new MainWindow(_vm);
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (_, _) => Teardown();
            SetupTray(_vm);

            var startHidden = args.Contains("--minimized") || _vm.Settings.StartMinimized;
            if (startHidden)
                _window.Opened += HideOnFirstOpen; // shown once by the lifetime, then tucked into the tray
            _vm.TrayStatusChanged += status => { if (_tray is not null) _tray.ToolTipText = status; };

            _vm.RecordLaunch(startHidden ? "minimized" : "normal");
            // Let the window paint before the first (network) refresh.
            Dispatcher.UIThread.Post(() => _ = _vm.RefreshAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HideOnFirstOpen(object? sender, EventArgs e)
    {
        _window!.Opened -= HideOnFirstOpen;
        _window.Hide();
    }

    private void SetupTray(MainViewModel vm)
    {
        var show = new NativeMenuItem("Show GCI");
        show.Click += (_, _) => ShowWindow();
        var refresh = new NativeMenuItem("Refresh now");
        refresh.Click += (_, _) => { if (vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null); };
        var pause = new NativeMenuItem(vm.IsPaused ? "Resume" : "Pause");
        pause.Click += (_, _) => vm.TogglePauseCommand.Execute(null);
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsPaused)) pause.Header = vm.IsPaused ? "Resume" : "Pause"; };

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://gci/Assets/gci.ico"))),
            ToolTipText = "GCI — Georgia Cannabis Inventory",
            Menu = new NativeMenu { Items = { show, refresh, pause, new NativeMenuItemSeparator(), exit } },
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_window is not null) _window.AllowClose = true;
        _desktop?.Shutdown();
    }

    private bool _tornDown;

    private void Teardown()
    {
        if (_tornDown) return;
        _tornDown = true;
        if (_tray is not null) { _tray.IsVisible = false; _tray.Dispose(); _tray = null; }
        _vm?.TrackSessionEnded();
        _vm?.Dispose();
        _thumbnails?.Dispose();
        _inventory?.Dispose();
        _feeds?.Dispose();
        _updates?.Dispose();
        _telemetry?.Dispose(); // last: flushes pending events
    }
}
