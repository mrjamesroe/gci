using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Gci.App.ViewModels;
using Gci.Core.Models;
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
    private SponsorService? _sponsors;
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
            // macOS: a menu-bar tray plus a lingering Dock icon reads as a "ghost", and closing a window is expected
            // to leave nothing behind — so there, closing quits (default OnLastWindowClose) and there's no tray. On
            // Windows we keep the tray and drive shutdown ourselves so it can go on monitoring after the window closes.
            var useTray = !OperatingSystem.IsMacOS();
            if (useTray) desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
            _sponsors = new SponsorService(data, userAgent: userAgent);
            _telemetry = new TelemetryClient(data, DesktopSystemSnapshot.SystemInfo());
            _thumbnails = new DesktopThumbnailService(data, userAgent);
            Views.Thumb.Service = _thumbnails;
            _vm = new MainViewModel(data, _inventory, new KeychainProfileStore(data), new DesktopNotifier(), _thumbnails,
                _feeds, _updates, _sponsors, _telemetry, new MacEmbeddedBrowser(), new MacUpdateInstaller(), new NoopStartupRegistration(),
                new DesktopSystemSnapshot(), new DesktopClipboard(), new DesktopTicker(TimeSpan.FromSeconds(15)), args);

            _vm.ApplyTheme = ApplyThemeVariant;
            ApplyThemeVariant(_vm.Settings.Theme);

            // Brings up the real window, tray and first refresh. `show` is true only when the lifetime has already
            // shown its initial window (the deferred, post-disclaimer path) and won't auto-show this one.
            void StartApp(bool show)
            {
                _window = new MainWindow(_vm!);
                desktop.MainWindow = _window;
                desktop.ShutdownRequested += (_, _) => Teardown();
                _vm!.ShowPreorder = OpenPreorder; // macOS: open the store checkout in an embedded WKWebView window

                var startHidden = false;
                if (useTray)
                {
                    SetupTray(_vm!);
                    startHidden = args.Contains("--minimized") || _vm!.Settings.StartMinimized;
                    if (startHidden)
                        _window.Opened += HideOnFirstOpen; // shown once, then tucked into the tray
                    _vm!.TrayStatusChanged += status => { if (_tray is not null) _tray.ToolTipText = status; };
                }
                if (show) _window.Show();

                _vm!.RecordLaunch(startHidden ? "minimized" : "normal");
                // Let the window paint before the first (network) refresh.
                Dispatcher.UIThread.Post(() => _ = _vm.RefreshAsync(), DispatcherPriority.Background);
            }

            // First-run consent: nothing starts until the disclaimer is accepted. Declining quits.
            if (_vm.Settings.DisclaimerAcceptedVersion != Legal.Version)
            {
                var priorMode = desktop.ShutdownMode;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the disclaimer must not quit the app
                var disclaimer = new DisclaimerWindow();
                disclaimer.Closed += (_, _) =>
                {
                    if (!disclaimer.Accepted) { desktop.Shutdown(); return; }
                    _vm!.AcceptDisclaimer();
                    StartApp(show: true);              // open the real window first,
                    desktop.ShutdownMode = priorMode;  // then restore the normal quit-on-last-close behavior
                };
                desktop.MainWindow = disclaimer;
                disclaimer.Show();
            }
            else
            {
                StartApp(show: false);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyThemeVariant(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default, // follow the OS
    };

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

    private PreorderWindow? _preorder;

    /// <summary>Opens (or reuses) the Preorder window over the main window.</summary>
    private void OpenPreorder(PreorderViewModel vm)
    {
        if (_preorder is null)
        {
            _preorder = new PreorderWindow(vm);
            _preorder.Closed += (_, _) => _preorder = null;
            if (_window is not null) _preorder.Show(_window); else _preorder.Show();
        }
        else
        {
            _preorder.Open(vm);
        }
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
        _sponsors?.Dispose();
        _telemetry?.Dispose(); // last: flushes pending events
    }
}
