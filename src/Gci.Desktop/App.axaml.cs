using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    private MainViewModel? _vm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
            _vm = new MainViewModel(data, _inventory, new NoopProfileStore(), new DesktopNotifier(), new NoopThumbnailCache(),
                _feeds, _updates, _telemetry, new NoopEmbeddedBrowser(), new NoopUpdateInstaller(), new NoopStartupRegistration(),
                new DesktopSystemSnapshot(), new DesktopClipboard(), new DesktopTicker(TimeSpan.FromSeconds(15)), args);

            desktop.MainWindow = new MainWindow(_vm);
            desktop.ShutdownRequested += (_, _) => Teardown();

            _vm.RecordLaunch("normal");
            // Let the window paint before the first (network) refresh.
            Dispatcher.UIThread.Post(() => _ = _vm.RefreshAsync(), DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Teardown()
    {
        _vm?.TrackSessionEnded();
        _vm?.Dispose();
        _inventory?.Dispose();
        _feeds?.Dispose();
        _updates?.Dispose();
        _telemetry?.Dispose(); // last: flushes pending events
    }
}
