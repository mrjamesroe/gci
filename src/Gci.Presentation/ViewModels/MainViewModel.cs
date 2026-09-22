using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const int InventoryTab = 0, WatchesTab = 1, ChangesTab = 2, NewsTab = 3, StoresTab = 4, PatientTab = 5, SettingsTab = 6;
    private const string SettingsFile = "settings.json";
    private const string WatchesFile = "watches.json";
    private const int MaxChangeRows = 1500;
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromHours(24);

    private readonly DataStore _data;
    private readonly InventoryService _inventory;
    private readonly INotifier _notifier;
    private readonly IThumbnailCache _thumbnails;
    private readonly FeedService _feeds;
    private readonly UpdateChecker _updates;
    private readonly SponsorService _sponsors;
    private readonly IEmbeddedBrowser _browser;
    private readonly IUpdateInstaller _updater;
    private readonly IStartupRegistration _startup;
    private readonly ISystemSnapshot _systemSnapshot;
    private readonly IClipboard _clipboard;
    private readonly IReadOnlyList<string> _launchArgs;
    private readonly Version _currentVersion;
    private bool _checkedUpdatesThisSession;
    private bool _checkingUpdates;
    private readonly ITicker _ticker;
    private readonly List<WatchRule> _watches;
    private List<ItemRow> _allRows = new();
    private DateTimeOffset _nextRefresh = DateTimeOffset.Now.AddSeconds(2);
    private CancellationTokenSource? _refreshCts;
    private bool _bulkStoreEdit;

    public MainViewModel(DataStore data, InventoryService inventory, IProfileStore profiles, INotifier notifier,
        IThumbnailCache thumbnails, FeedService feeds, UpdateChecker updates, SponsorService sponsors, TelemetryClient telemetry,
        IEmbeddedBrowser browser, IUpdateInstaller updater, IStartupRegistration startup, ISystemSnapshot systemSnapshot,
        IClipboard clipboard, ITicker ticker, IReadOnlyList<string> launchArgs)
    {
        _data = data;
        _inventory = inventory;
        _notifier = notifier;
        _thumbnails = thumbnails;
        _feeds = feeds;
        _updates = updates;
        _sponsors = sponsors;
        _browser = browser;
        _updater = updater;
        _startup = startup;
        _systemSnapshot = systemSnapshot;
        _clipboard = clipboard;
        _ticker = ticker;
        _launchArgs = launchArgs;
        _currentVersion = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);
        CurrentVersion = _currentVersion.ToString(3);
        Settings = data.Load(SettingsFile, () => new AppSettings());
        _watches = data.Load(WatchesFile, () => new List<WatchRule>());
        InitTelemetry(telemetry);
        Profile = new ProfileViewModel(profiles, telemetry);

        // Assign backing fields directly so loading doesn't re-save settings through the change hooks.
#pragma warning disable MVVMTK0034
        _refreshMinutes = Settings.EffectiveRefreshMinutes;
        _autoRefresh = Settings.AutoRefresh;
        _toastNotifications = Settings.ToastNotifications;
        _ntfyTopicUrl = Settings.NtfyTopicUrl ?? "";
        _minimizeToTray = Settings.MinimizeToTray;
        _startWithWindows = Settings.StartWithWindows;
        _startMinimized = Settings.StartMinimized;
        _showImages = Settings.ShowImages;
        _checkForUpdates = Settings.CheckForUpdates;
        _showSponsor = Settings.ShowSponsor;
        _theme = Settings.Theme;
        _showWelcome = !Settings.FirstRunComplete;

        CategoryOptions = new[] { new Option<ProductCategory?>(null, "All categories") }
            .Concat(Enum.GetValues<ProductCategory>().Select(c => new Option<ProductCategory?>(c, c.ToString()))).ToList();
        _selectedCategory = CategoryOptions[0];
        _selectedOperator = new Option<string?>(null, "All operators");
        _selectedStore = new Option<string?>(null, "All monitored stores");
#pragma warning restore MVVMTK0034

        RebuildStores();
        RebuildAllRows();
        RebuildWatches();
        RebuildChanges();
        RebuildNews();
        RebuildFeedRows();
        if (EnabledKeys().Select(k => _inventory.GetStatus(k)?.LastSuccess).Max() is { } cached)
            LastRefreshText = $"Updated {cached.LocalDateTime:MMM d, h:mm tt}";
        UpdatePhoneNudge();

        // Show the cached sponsor instantly, then refresh the feed in the background (both no-ops when sponsors are off).
        if (ShowSponsor) Sponsor = _sponsors.Current();
        _ = RefreshSponsorAsync();

        _ticker.Tick += OnTick;
        _ticker.Start();
    }

    public AppSettings Settings { get; }
    public ProfileViewModel Profile { get; }

    /// <summary>Set by the view: shows the watch editor modally, returns true when saved. Async so it works on both
    /// WPF (a synchronous ShowDialog wrapped in a completed task) and Avalonia (a genuinely async ShowDialog).</summary>
    public Func<WatchEditorViewModel, Task<bool>>? ShowWatchEditor { get; set; }
    /// <summary>Set by the view: shows the phone-alerts setup dialog modally.</summary>
    public Func<PhoneSetupViewModel, Task>? ShowPhoneSetup { get; set; }
    /// <summary>Set by the view: a yes/no confirmation. Returns true to proceed.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }
    /// <summary>Raised with a short status line for the tray tooltip.</summary>
    public event Action<string>? TrayStatusChanged;

    // ---- Inventory ----------------------------------------------------------------------------

    public IReadOnlyList<Option<ProductCategory?>> CategoryOptions { get; }
    public ObservableCollection<Option<string?>> OperatorOptions { get; } = new();
    public ObservableCollection<Option<string?>> StoreOptions { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private Option<ProductCategory?> _selectedCategory;
    [ObservableProperty] private Option<string?> _selectedOperator;
    [ObservableProperty] private Option<string?> _selectedStore;
    [ObservableProperty] private bool _inStockOnly = true;
    [ObservableProperty] private bool _watchedOnly;
    [ObservableProperty] private IReadOnlyList<ItemRow> _rows = [];
    [ObservableProperty] private ItemRow? _selectedRow;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _showWelcome;

    partial void OnSearchTextChanged(string value) { ApplyFilters(); TrackSearch(value); }
    partial void OnSelectedCategoryChanged(Option<ProductCategory?> value) { ApplyFilters(); TrackFilter("category", value?.Value?.ToString()); }
    partial void OnSelectedOperatorChanged(Option<string?> value) { ApplyFilters(); TrackFilter("operator", value?.Value); }
    partial void OnSelectedStoreChanged(Option<string?> value)
    {
        ApplyFilters();
        TrackFilter("store", value?.Value is { } key ? _inventory.GetStore(key)?.DisplayName : null);
    }
    partial void OnInStockOnlyChanged(bool value) { ApplyFilters(); TrackFilter("in_stock_only", value.ToString()); }
    partial void OnWatchedOnlyChanged(bool value) { ApplyFilters(); TrackFilter("watched_only", value.ToString()); }

    // ---- Status -------------------------------------------------------------------------------

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private string _lastRefreshText = "Not refreshed yet";
    [ObservableProperty] private string _nextRefreshText = "";
    [ObservableProperty] private bool _isPaused;

    // ---- Stores / watches / changes -----------------------------------------------------------

    public ObservableCollection<StoreRow> StoreRows { get; } = new();
    [ObservableProperty] private StoreRow? _selectedStoreRow;
    [ObservableProperty] private string _storesSummary = "";

    public ObservableCollection<WatchRow> WatchRows { get; } = new();
    [ObservableProperty] private WatchRow? _selectedWatch;

    [ObservableProperty] private IReadOnlyList<ChangeRow> _changeRows = [];
    [ObservableProperty] private ChangeRow? _selectedChange;
    [ObservableProperty] private bool _changesWatchedOnly;
    [ObservableProperty] private string _changesSummary = "";

    partial void OnChangesWatchedOnlyChanged(bool value) => RebuildChanges();

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == SettingsTab) UpdateImagesStatus();
        TrackTab(value);
    }

    // ---- Settings -----------------------------------------------------------------------------

    [ObservableProperty] private int _refreshMinutes;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private bool _toastNotifications;
    [ObservableProperty] private string _ntfyTopicUrl;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _showImages;
    [ObservableProperty] private string _imagesStatusText = "";
    [ObservableProperty] private string _settingsMessage = "";

    // ---- Appearance ---------------------------------------------------------------------------

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();
    [ObservableProperty] private AppTheme _theme;

    /// <summary>Set by the view to apply the chosen theme to its platform (WPF resource swap / Avalonia variant).</summary>
    public Action<AppTheme>? ApplyTheme { get; set; }

    partial void OnThemeChanged(AppTheme value)
    {
        Settings.Theme = value;
        SaveSettings();
        ApplyTheme?.Invoke(value);
        TrackSetting("theme", value.ToString());
    }

    partial void OnShowImagesChanged(bool value) { Settings.ShowImages = value; SaveSettings(); TrackSetting("show_images", value); }

    // ---- Sponsor (header awareness slot) ------------------------------------------------------

    /// <summary>The current sponsor message, or null when there's nothing to show.</summary>
    [ObservableProperty] private Sponsor? _sponsor;
    [ObservableProperty] private bool _showSponsor;

    /// <summary>True only when sponsors are enabled and one is available, so the header chip binds to a single flag.</summary>
    public bool HasSponsor => ShowSponsor && Sponsor is not null;

    partial void OnSponsorChanged(Sponsor? value)
    {
        OnPropertyChanged(nameof(HasSponsor));
        if (value is not null && ShowSponsor)
            _telemetry.TrackOnce($"sponsor_shown:{value.Id}", TimeSpan.FromDays(1), "sponsor_shown",
                new Dictionary<string, object?> { ["sponsor"] = value.Brand, ["id"] = value.Id });
    }

    partial void OnShowSponsorChanged(bool value)
    {
        Settings.ShowSponsor = value;
        SaveSettings();
        OnPropertyChanged(nameof(HasSponsor));
        TrackSetting("show_sponsor", value);
        if (value) _ = RefreshSponsorAsync();
        else Sponsor = null;
    }

    private async Task RefreshSponsorAsync()
    {
        if (!ShowSponsor) return;
        var sponsor = await _sponsors.RefreshAsync();
        if (ShowSponsor) Sponsor = sponsor; // the user may have turned it off while the fetch was in flight
    }

    [RelayCommand]
    private void OpenSponsor()
    {
        if (Sponsor is not { } s) return;
        _telemetry.Track("sponsor_clicked", new Dictionary<string, object?> { ["sponsor"] = s.Brand, ["id"] = s.Id });
        OpenUrl(s.Url);
    }

    public string DataFolder => _data.Root;

    public IReadOnlyList<Option<int>> RefreshOptions { get; } = new[]
    {
        new Option<int>(5, "5 minutes"), new Option<int>(10, "10 minutes"), new Option<int>(15, "15 minutes"),
        new Option<int>(30, "30 minutes"), new Option<int>(60, "1 hour"),
    };

    partial void OnRefreshMinutesChanged(int value)
    {
        Settings.RefreshMinutes = Math.Max(AppSettings.MinimumRefreshMinutes, value);
        SaveSettings();
        ScheduleNext();
        TrackSetting("refresh_minutes", Settings.RefreshMinutes);
    }

    partial void OnAutoRefreshChanged(bool value) { Settings.AutoRefresh = value; SaveSettings(); ScheduleNext(); TrackSetting("auto_refresh", value); }
    partial void OnToastNotificationsChanged(bool value) { Settings.ToastNotifications = value; SaveSettings(); TrackSetting("toasts", value); }
    partial void OnMinimizeToTrayChanged(bool value) { Settings.MinimizeToTray = value; SaveSettings(); TrackSetting("minimize_to_tray", value); }
    partial void OnStartMinimizedChanged(bool value) { Settings.StartMinimized = value; SaveSettings(); TrackSetting("start_minimized", value); }

    partial void OnNtfyTopicUrlChanged(string value)
    {
        var wasSet = Settings.NtfyTopicUrl is not null;
        Settings.NtfyTopicUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        SaveSettings();
        // Only whether phone push is configured, never the topic URL.
        if (wasSet != (Settings.NtfyTopicUrl is not null)) TrackSetting("ntfy", Settings.NtfyTopicUrl is not null);
        UpdatePhoneNudge();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        Settings.StartWithWindows = value;
        SaveSettings();
        TrackSetting("start_with_windows", value);
        try
        {
            _startup.Apply(value);
        }
        catch (Exception ex)
        {
            SettingsMessage = $"Couldn't update the startup entry: {ex.Message}";
        }
    }

    // ---- Refresh ------------------------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Progress = 0;
        _refreshCts = new CancellationTokenSource();
        TrackActivePing(); // keeps a long-running install counted as active day to day
        try
        {
            await EnsureStoresAsync(force: false);
            var keys = EnabledKeys();
            if (keys.Count == 0)
            {
                StatusText = "No stores selected. Pick some on the Stores tab.";
                return;
            }

            StatusText = $"Refreshing {keys.Count} stores…";
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                Progress = 100.0 * p.Done / Math.Max(1, p.Total);
                StatusText = $"Refreshing… {p.Done}/{p.Total} stores";
            });
            var token = _refreshCts.Token;
            var stopwatch = Stopwatch.StartNew();
            var result = await Task.Run(() => _inventory.RefreshAsync(keys, token, progress), token);
            stopwatch.Stop();

            RefreshStoreStatuses();
            RebuildAllRows();
            RebuildChanges();
            _ = SweepImagesAsync(force: false);

            var logged = result.Events.Where(e => e.Kind != ChangeKind.QuantityChanged).ToList();
            var matches = result.Events
                .SelectMany(e => _watches.Where(w => WatchMatcher.Matches(w, e)).Select(w => (e, w)))
                .ToList();
            await _notifier.NotifyAsync(matches, Settings);
            TrackRefresh(result, matches, stopwatch.Elapsed);
            await RefreshFeedsAsync();
            MaybeSendDailySummary();

            LastRefreshText = $"Updated {result.FinishedAt.LocalDateTime:t}";
            StatusText = result.StoresFailed == 0
                ? $"Updated {result.StoresSucceeded} stores · {logged.Count} changes · {matches.Count} watched"
                : $"Updated {result.StoresSucceeded} stores, {result.StoresFailed} failed (see Stores tab) · {logged.Count} changes";
            CheckCardExpiry();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            Progress = 0;
            ScheduleNext();
            // Stores enabled mid-refresh get picked up shortly instead of waiting a full interval.
            if (EnabledKeys().Any(k => _inventory.GetStatus(k) is null))
                _nextRefresh = DateTimeOffset.Now.AddSeconds(5);
            TrayStatusChanged?.Invoke($"GCI · {LastRefreshText}");
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        StatusText = IsPaused ? "Auto-refresh paused." : "Auto-refresh resumed.";
        ScheduleNext();
    }

    private void OnTick()
    {
        UpdateNextRefreshText();
        if (SelectedTabIndex == SettingsTab) UpdateImagesStatus();
        if (CheckForUpdates && !_checkingUpdates &&
            (!_checkedUpdatesThisSession || DateTimeOffset.Now - (Settings.LastUpdateCheck ?? DateTimeOffset.MinValue) > UpdateCheckInterval))
            _ = CheckForUpdatesAsync(manual: false);
        if (!AutoRefresh || IsPaused || IsRefreshing) return;
        if (DateTimeOffset.Now >= _nextRefresh) _ = RefreshAsync();
    }

    private void ScheduleNext()
    {
        _nextRefresh = DateTimeOffset.Now.AddMinutes(Settings.EffectiveRefreshMinutes);
        UpdateNextRefreshText();
    }

    private void UpdateNextRefreshText()
    {
        if (!AutoRefresh) { NextRefreshText = "Auto-refresh off"; return; }
        if (IsPaused) { NextRefreshText = "Paused"; return; }
        if (IsRefreshing) { NextRefreshText = ""; return; }
        var mins = (int)Math.Ceiling((_nextRefresh - DateTimeOffset.Now).TotalMinutes);
        NextRefreshText = mins <= 1 ? "Next refresh in under a minute" : $"Next refresh in {mins} min";
    }

    private async Task EnsureStoresAsync(bool force)
    {
        var fresh = Settings.LastDiscovery is { } last && DateTimeOffset.Now - last < DiscoveryInterval;
        if (!force && fresh && _inventory.Stores.Count > 0) return;

        StatusText = "Finding Georgia stores…";
        var warnings = await Task.Run(() => _inventory.DiscoverStoresAsync(CancellationToken.None));
        Settings.LastDiscovery = DateTimeOffset.Now;
        var stores = _inventory.Stores;

        if (Settings.EnabledStoreKeys is null)
        {
            Settings.EnabledStoreKeys = stores.Where(IsDefaultEnabled).Select(s => s.Key).ToList();
        }
        else if (Settings.KnownStoreKeys is { } known)
        {
            // A dispensary that opened since the last check gets monitored and announced.
            foreach (var s in stores.Where(s => !known.Contains(s.Key)))
            {
                if (!s.IsPharmacyPartner) Settings.EnabledStoreKeys.Add(s.Key);
                _notifier.ShowToast($"New store found: {s.DisplayName}",
                    s.IsPharmacyPartner ? "Enable it on the Stores tab to monitor it." : "Now monitoring it.",
                    s.City, s.MenuUrl, null);
                TrackStoreDiscovered(s);
            }
        }
        Settings.KnownStoreKeys = stores.Select(s => s.Key).ToList();
        SaveSettings();
        RebuildStores();
        if (warnings.Count > 0)
            StatusText = "Some store lists couldn't be read: " + string.Join("; ", warnings);
    }

    private static bool IsDefaultEnabled(StoreInfo s) =>
        !s.IsPharmacyPartner || s.Name.StartsWith("Lotus Farmacy", StringComparison.OrdinalIgnoreCase);

    private List<string> EnabledKeys()
    {
        var existing = _inventory.Stores.Select(s => s.Key).ToHashSet();
        return (Settings.EnabledStoreKeys ?? new()).Where(existing.Contains).Distinct().ToList();
    }

    private void CheckCardExpiry()
    {
        if (Profile.Current.DaysUntilExpiry(DateTime.Today) is not { } days || days > 30) return;
        if (Settings.LastExpiryReminder?.Date == DateTime.Today) return;
        Settings.LastExpiryReminder = DateTime.Today;
        SaveSettings();
        _notifier.ShowToast(days < 0 ? "Your registry card has expired" : $"Your registry card expires in {days} days",
            "Dispensaries won't sell without a valid Low THC Oil Registry card.", "GCI", null, null);
    }

    // ---- Inventory rows -----------------------------------------------------------------------

    private void RebuildAllRows()
    {
        var stores = _inventory.Stores.ToDictionary(s => s.Key);
        var enabled = EnabledKeys();
        var watches = _watches.Where(w => w.Enabled).ToList();
        _allRows = _inventory.GetItems(enabled)
            .Where(i => stores.ContainsKey(i.StoreKey))
            .Select(i =>
            {
                var store = stores[i.StoreKey];
                return new ItemRow(i, store, watches.Any(w => WatchMatcher.Matches(w, i, store)));
            })
            .ToList();

        var operators = enabled.Select(k => stores[k].Operator).Distinct().Order().ToList();
        SyncOptions(OperatorOptions, new Option<string?>(null, "All operators"), operators.Select(o => new Option<string?>(o, o)));
        SyncOptions(StoreOptions, new Option<string?>(null, "All monitored stores"),
            enabled.Select(k => stores[k]).OrderBy(s => s.DisplayName).Select(s => new Option<string?>(s.Key, s.DisplayName)));
        SelectedOperator = OperatorOptions.FirstOrDefault(o => o.Value == SelectedOperator?.Value) ?? OperatorOptions[0];
        SelectedStore = StoreOptions.FirstOrDefault(o => o.Value == SelectedStore?.Value) ?? StoreOptions[0];
        ApplyFilters();
        UpdateWatchCounts();
    }

    private static void SyncOptions(ObservableCollection<Option<string?>> target, Option<string?> all, IEnumerable<Option<string?>> options)
    {
        var desired = new[] { all }.Concat(options).ToList();
        if (target.SequenceEqual(desired)) return;
        target.Clear();
        foreach (var o in desired) target.Add(o);
    }

    private void ApplyFilters()
    {
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<ItemRow> rows = _allRows;
        if (InStockOnly) rows = rows.Where(r => r.InStock);
        if (WatchedOnly) rows = rows.Where(r => r.Watched);
        if (SelectedCategory?.Value is { } c) rows = rows.Where(r => r.Item.Category == c);
        if (SelectedOperator?.Value is { } op) rows = rows.Where(r => r.Operator == op);
        if (SelectedStore?.Value is { } store) rows = rows.Where(r => r.Store.Key == store);
        if (words.Length > 0)
            rows = rows.Where(r =>
            {
                var hay = $"{r.Name} {r.Brand} {r.StoreName} {r.City} {r.Category} {r.Strain} {r.Potency} {r.Item.RawCategory}";
                return words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
            });

        Rows = rows.OrderBy(r => r.Item.Category).ThenBy(r => r.Name).ThenBy(r => r.StoreName).ToList();
        var storeCount = Rows.Select(r => r.Store.Key).Distinct().Count();
        ResultSummary = $"{Rows.Count:N0} items at {storeCount} store{(storeCount == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedCategory = CategoryOptions[0];
        SelectedOperator = OperatorOptions.FirstOrDefault() ?? SelectedOperator;
        SelectedStore = StoreOptions.FirstOrDefault() ?? SelectedStore;
        WatchedOnly = false;
        InStockOnly = true;
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the link: {ex.Message}";
        }
    }

    // ---- Stores -------------------------------------------------------------------------------

    private void RebuildStores()
    {
        var enabled = (Settings.EnabledStoreKeys ?? new()).ToHashSet();
        StoreRows.Clear();
        foreach (var s in _inventory.Stores
                     .OrderBy(s => s.Operator).ThenBy(s => s.IsPharmacyPartner).ThenBy(s => s.Name))
        {
            var row = new StoreRow(s, enabled.Contains(s.Key), OnStoreToggled);
            row.Update(_inventory.GetStatus(s.Key));
            StoreRows.Add(row);
        }
        UpdateStoresSummary();
    }

    private void RefreshStoreStatuses()
    {
        foreach (var row in StoreRows) row.Update(_inventory.GetStatus(row.Store.Key));
        UpdateStoresSummary();
    }

    private void UpdateStoresSummary()
    {
        var on = StoreRows.Count(r => r.IsEnabled);
        var failing = StoreRows.Count(r => r.IsEnabled && r.Error is not null);
        StoresSummary = $"Monitoring {on} of {StoreRows.Count} stores" + (failing > 0 ? $" · {failing} with errors" : "");
        UpdateWebView2Banner();
    }

    // ---- WebView2 -----------------------------------------------------------------------------

    /// <summary>Shown when a monitored store can only be read through Edge WebView2 and this PC doesn't have it.</summary>
    [ObservableProperty] private bool _showWebView2Banner;
    [ObservableProperty] private string _webView2BannerText = "";

    private void UpdateWebView2Banner()
    {
        var operators = StoreRows
            .Where(r => r.IsEnabled && r.Error?.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase) == true)
            .Select(r => r.Store.Operator).Distinct().ToList();
        ShowWebView2Banner = operators.Count > 0 && !_browser.IsAvailable;
        if (ShowWebView2Banner)
            WebView2BannerText = $"{string.Join(" and ", operators)} {(operators.Count == 1 ? "needs" : "need")} Microsoft Edge WebView2, " +
                                 "which isn't installed on this PC.";
    }

    [RelayCommand]
    private void DownloadWebView2()
    {
        OpenUrl(Gci.Core.Providers.BrowserUnavailableException.DownloadUrl);
        StatusText = "Run the downloaded MicrosoftEdgeWebview2Setup.exe, then click \"I've installed it\".";
        _telemetry.Track("webview2_download_clicked");
    }

    [RelayCommand]
    private void RecheckWebView2()
    {
        var installed = _browser.IsAvailable;
        _telemetry.Track("webview2_rechecked", new Dictionary<string, object?> { ["installed"] = installed });
        if (!installed)
        {
            StatusText = "WebView2 isn't installed yet. Run the downloaded MicrosoftEdgeWebview2Setup.exe, then try again.";
            return;
        }
        ShowWebView2Banner = false;
        StatusText = "WebView2 found. Refreshing menus…";
        _ = RefreshAsync();
    }

    private void OnStoreToggled(StoreRow row)
    {
        var keys = Settings.EnabledStoreKeys ??= new();
        keys.Remove(row.Store.Key);
        if (row.IsEnabled) keys.Add(row.Store.Key);
        if (_bulkStoreEdit) return;
        AfterStoreSelectionChanged();
        _telemetry.Track("store_toggled", new Dictionary<string, object?>
        {
            ["store"] = row.Store.DisplayName, ["operator"] = row.Store.Operator, ["platform"] = row.Store.Provider.ToString(),
            ["pharmacy"] = row.Store.IsPharmacyPartner, ["monitored"] = row.IsEnabled, ["stores_monitored"] = EnabledKeys().Count,
        });
    }

    private void AfterStoreSelectionChanged()
    {
        SaveSettings();
        RebuildAllRows();
        UpdateStoresSummary();
        if (!IsRefreshing && EnabledKeys().Any(k => _inventory.GetStatus(k) is null))
            _nextRefresh = DateTimeOffset.Now.AddSeconds(3);
    }

    private void SetStores(string action, Func<StoreRow, bool> enable)
    {
        _bulkStoreEdit = true;
        foreach (var row in StoreRows) row.IsEnabled = enable(row);
        _bulkStoreEdit = false;
        AfterStoreSelectionChanged();
        _telemetry.Track("stores_bulk_changed", new Dictionary<string, object?> { ["action"] = action, ["stores_monitored"] = EnabledKeys().Count });
    }

    [RelayCommand] private void EnableDispensaries() => SetStores("all_dispensaries", r => r.IsEnabled || !r.Store.IsPharmacyPartner);
    [RelayCommand] private void EnableAllStores() => SetStores("everything", _ => true);
    [RelayCommand] private void DisableAllStores() => SetStores("none", _ => false);

    [RelayCommand]
    private async Task RediscoverStoresAsync()
    {
        if (IsRefreshing) return;
        await EnsureStoresAsync(force: true);
        StatusText = $"Found {_inventory.Stores.Count} stores.";
        RebuildAllRows();
        _telemetry.Track("stores_rediscovered", new Dictionary<string, object?> { ["stores_found"] = _inventory.Stores.Count });
    }

    // ---- Watches ------------------------------------------------------------------------------

    private void RebuildWatches()
    {
        WatchRows.Clear();
        foreach (var w in _watches)
            WatchRows.Add(new WatchRow(w, key => _inventory.GetStore(key)?.DisplayName, row =>
            {
                SaveWatches();
                RebuildAllRows();
                RebuildChanges();
                RebuildNews();
                _telemetry.Track("watch_toggled", new Dictionary<string, object?> { ["enabled"] = row.Enabled, ["watches_total"] = _watches.Count });
            }));
        UpdateWatchCounts();
    }

    private void UpdateWatchCounts()
    {
        foreach (var row in WatchRows)
            row.MatchingNow = _allRows.Count(r => r.InStock && WatchMatcher.Matches(row.Rule, r.Item, r.Store));
    }

    [RelayCommand]
    private Task AddWatch() => EditAndSave(new WatchRule { Name = "New watch" }, isNew: true, "new");

    [RelayCommand]
    private Task EditWatch(WatchRow? row)
    {
        row ??= SelectedWatch;
        return row is not null ? EditAndSave(row.Rule, isNew: false, "edit") : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteWatch(WatchRow? row)
    {
        row ??= SelectedWatch;
        if (row is null) return;
        if (Confirm is { } confirm && !await confirm($"Delete the watch \"{row.Name}\"?")) return;
        _watches.RemoveAll(w => w.Id == row.Rule.Id);
        SaveWatches();
        RebuildWatches();
        RebuildAllRows();
        RebuildChanges();
        RebuildNews();
        _telemetry.Track("watch_deleted", new Dictionary<string, object?>
        {
            ["category"] = row.Rule.Category?.ToString() ?? "any", ["watches_total"] = _watches.Count,
        });
    }

    // ---- Preorder ---------------------------------------------------------------------------

    /// <summary>Opens (or reuses) the Preorder window; set by the main window.</summary>
    public Action<PreorderViewModel>? ShowPreorder { get; set; }

    /// <summary>The Preorder window's own Edge profile, so store sign-ins are remembered.</summary>
    public string PreorderBrowserFolder => _data.PathFor("webview-shop");

    [RelayCommand]
    private void Preorder(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row?.Url is null) return;
        OpenPreorder(new PreorderRequest
        {
            StoreKey = row.Store.Key, StoreName = row.StoreName, Operator = row.Operator, Provider = row.Store.Provider,
            ProductName = row.Name, ProductUrl = row.Url, Source = "inventory",
        });
    }

    [RelayCommand]
    private void PreorderChange(ChangeRow? row)
    {
        row ??= SelectedChange;
        if (row?.Url is null || _inventory.GetStore(row.Event.StoreKey) is not { } store) return;
        OpenPreorder(new PreorderRequest
        {
            StoreKey = store.Key, StoreName = store.DisplayName, Operator = store.Operator, Provider = store.Provider,
            ProductName = row.Name, ProductUrl = row.Url, Source = "changes",
        });
    }

    /// <summary>From a watch alert's Preorder button.</summary>
    public void OpenPreorderFromToast(string storeKey, string url, string name)
    {
        if (_inventory.GetStore(storeKey) is not { } store)
        {
            OpenUrl(url);
            return;
        }
        OpenPreorder(new PreorderRequest
        {
            StoreKey = store.Key, StoreName = store.DisplayName, Operator = store.Operator, Provider = store.Provider,
            ProductName = string.IsNullOrWhiteSpace(name) ? "Product" : name, ProductUrl = url, Source = "toast",
        });
    }

    private void OpenPreorder(PreorderRequest request)
    {
        if (ShowPreorder is null || !_browser.IsAvailable)
        {
            _telemetry.Track("preorder_browser_fallback", new Dictionary<string, object?>
            {
                ["platform"] = request.Provider.ToString(), ["reason"] = ShowPreorder is null ? "no_window" : "no_webview2",
            });
            OpenUrl(request.ProductUrl);
            if (ShowPreorder is not null)
                StatusText = "Preorder needs Microsoft Edge WebView2, which isn't installed, so the product opened in your browser instead.";
            return;
        }
        ShowPreorder(new PreorderViewModel(request, () => PrefillData.From(Profile.Current), _telemetry));
    }

    [RelayCommand]
    private Task WatchProductHere(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return Task.CompletedTask;
        return EditAndSave(new WatchRule
        {
            Name = $"{row.Name} @ {row.Store.Name}",
            Keywords = { row.Name },
            StoreKeys = { row.Store.Key },
            LowStockThreshold = row.Quantity is not null ? 3 : null,
        }, isNew: true, "product_here");
    }

    [RelayCommand]
    private Task WatchProductAnywhere(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return Task.CompletedTask;
        return EditAndSave(new WatchRule { Name = row.Name, Keywords = { row.Name } }, isNew: true, "product_anywhere");
    }

    [RelayCommand]
    private Task WatchCategoryAtStore(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return Task.CompletedTask;
        return EditAndSave(new WatchRule
        {
            Name = $"{row.Item.Category} @ {row.Store.Name}",
            Category = row.Item.Category,
            StoreKeys = { row.Store.Key },
        }, isNew: true, "category_store");
    }

    [RelayCommand]
    private Task WatchCurrentFilters()
    {
        var rule = new WatchRule
        {
            Category = SelectedCategory?.Value,
            Operator = SelectedOperator?.Value,
            Keywords = string.IsNullOrWhiteSpace(SearchText) ? new() : new() { SearchText.Trim() },
        };
        if (SelectedStore?.Value is { } store) rule.StoreKeys.Add(store);
        rule.Name = rule.Summary == "Everything" ? "Everything" : rule.Summary;
        return EditAndSave(rule, isNew: true, "filters");
    }

    private async Task EditAndSave(WatchRule rule, bool isNew, string source)
    {
        if (ShowWatchEditor is null) return;
        var current = _allRows.Select(r => (r.Item, r.Store)).ToList();
        var editor = new WatchEditorViewModel(rule, _inventory.Stores, current);
        if (!await ShowWatchEditor(editor)) return;

        var updated = editor.ToRule();
        var index = _watches.FindIndex(w => w.Id == updated.Id);
        if (index >= 0) _watches[index] = updated;
        else _watches.Add(updated);
        SaveWatches();
        RebuildWatches();
        RebuildAllRows();
        RebuildChanges();
        RebuildNews();
        StatusText = isNew ? $"Watching \"{updated.Name}\"." : $"Updated \"{updated.Name}\".";
        TrackWatchSaved(updated, source, isNew);
        UpdatePhoneNudge();
    }

    // ---- Changes ------------------------------------------------------------------------------

    private void RebuildChanges()
    {
        var watches = _watches.Where(w => w.Enabled).ToList();
        // Fall back to the current inventory image for change rows recorded before events carried their own image.
        var currentImages = new Dictionary<string, ImageRef>();
        foreach (var row in _allRows)
            if (row.Image is { } img) currentImages[row.Item.Key] = img;
        var rows = _inventory.Changes
            .Select(e => new ChangeRow(e, watches.Any(w => WatchMatcher.Matches(w, e)), currentImages.GetValueOrDefault(e.ItemKey)))
            .Where(r => !ChangesWatchedOnly || r.Watched)
            .Take(MaxChangeRows)
            .ToList();
        ChangeRows = rows;
        var today = rows.Count(r => r.At.LocalDateTime.Date == DateTime.Today);
        ChangesSummary = $"{rows.Count:N0} changes shown · {today} today";
    }

    [RelayCommand]
    private async Task ClearChanges()
    {
        if (Confirm is { } confirm && !await confirm("Clear the change history?")) return;
        _inventory.ClearChanges();
        RebuildChanges();
    }

    // ---- Settings / misc ----------------------------------------------------------------------

    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        _notifier.ShowToast("GCI test notification", "Watched items will show up like this.", "GCI", "https://www.trulieve.com/dispensaries/georgia", null);
        if (!string.IsNullOrWhiteSpace(Settings.NtfyTopicUrl))
        {
            var error = await _notifier.SendNtfyAsync(Settings.NtfyTopicUrl!, "GCI test", "Phone notifications are working.", null);
            SettingsMessage = error is null ? "Sent a test toast and a phone push." : $"Toast sent; phone push failed: {error}";
            _telemetry.Track("test_notification", new Dictionary<string, object?> { ["ntfy"] = true, ["ntfy_ok"] = error is null });
        }
        else
        {
            SettingsMessage = "Sent a test toast.";
            _telemetry.Track("test_notification", new Dictionary<string, object?> { ["ntfy"] = false });
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenUrl(_data.Root);

    // ---- Phone alerts -------------------------------------------------------------------------

    /// <summary>Shown once someone has a watch but no phone alerts, since desktop-only alerts miss overnight drops.</summary>
    [ObservableProperty] private bool _showPhoneNudge;

    private void UpdatePhoneNudge() =>
        ShowPhoneNudge = _watches.Count > 0 && string.IsNullOrWhiteSpace(Settings.NtfyTopicUrl) && !Settings.PhoneNudgeDismissed;

    [RelayCommand]
    private async Task OpenPhoneSetup()
    {
        if (ShowPhoneSetup is null) return;
        var setup = new PhoneSetupViewModel(Settings.NtfyTopicUrl, _notifier, _clipboard, url => NtfyTopicUrl = url ?? "", _telemetry);
        await ShowPhoneSetup(setup);
        UpdatePhoneNudge();
        SettingsMessage = string.IsNullOrWhiteSpace(Settings.NtfyTopicUrl) ? "Phone alerts are off." : "Phone alerts are on.";
    }

    [RelayCommand]
    private void DismissPhoneNudge()
    {
        Settings.PhoneNudgeDismissed = true;
        SaveSettings();
        UpdatePhoneNudge();
        _telemetry.Track("phone_nudge_dismissed");
    }

    // ---- Updates ------------------------------------------------------------------------------

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    public string CurrentVersion { get; }
    public string ReleasesPage => _updates.ReleasesPage;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private ReleaseInfo? _availableUpdate;
    [ObservableProperty] private bool _showUpdateBanner;
    [ObservableProperty] private string _updateBannerText = "";
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateProgress;

    /// <summary>Raised after a new version has been put in place and started; the app should exit.</summary>
    public event Action? ExitRequested;

    partial void OnCheckForUpdatesChanged(bool value) { Settings.CheckForUpdates = value; SaveSettings(); TrackSetting("check_updates", value); }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        _checkedUpdatesThisSession = true;
        if (manual) UpdateStatusText = "Checking GitHub for a newer version…";
        try
        {
            var latest = await _updates.GetLatestAsync();
            Settings.LastUpdateCheck = DateTimeOffset.Now;
            SaveSettings();

            if (latest is null || !UpdateChecker.IsNewer(latest.Version, _currentVersion))
            {
                AvailableUpdate = null;
                ShowUpdateBanner = false;
                UpdateStatusText = $"You're on the latest version. Checked {DateTime.Now:MMM d, h:mm tt}.";
                return;
            }

            var version = latest.Version.ToString(3);
            AvailableUpdate = latest;
            UpdateBannerText = $"GCI {version} is available. You have {CurrentVersion}.";
            ShowUpdateBanner = true;
            UpdateStatusText = $"Version {version} is available ({latest.PublishedAt?.LocalDateTime:MMM d}).";
            _telemetry.TrackOnce($"update_available:{latest.Tag}", TimeSpan.FromDays(30), "update_available",
                new Dictionary<string, object?> { ["available"] = version, ["current"] = CurrentVersion, ["manual_check"] = manual });
            if (Settings.LastAnnouncedVersion != latest.Tag)
            {
                Settings.LastAnnouncedVersion = latest.Tag;
                SaveSettings();
                _notifier.ShowToast($"GCI {version} is available", "Open GCI and choose Install update. Your data and settings carry over.",
                    "GCI", latest.PageUrl, null, "What's new");
            }
        }
        catch (Exception ex)
        {
            if (manual) UpdateStatusText = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => CheckForUpdatesAsync(manual: true);

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release || IsInstallingUpdate) return;
        if (!_updater.CanSelfUpdate(out var reason))
        {
            UpdateStatusText = reason;
            UpdateBannerText = reason;
            _telemetry.Track("update_blocked", new Dictionary<string, object?>
            {
                ["reason"] = reason.Contains("development") ? "dev_build"
                    : reason.Contains("framework") ? "multi_file_build"
                    : reason.Contains("write") ? "folder_not_writable" : "other",
            });
            OpenUrl(release.PageUrl);
            return;
        }

        IsInstallingUpdate = true;
        var version = release.Version.ToString(3);
        var stage = "download";
        try
        {
            _telemetry.Track("update_install_started", new Dictionary<string, object?> { ["from"] = CurrentVersion, ["to"] = version });
            await _telemetry.FlushAsync();
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateBannerText = $"Downloading GCI {version}… {p:P0}";
            });
            var downloaded = await _updates.DownloadAsync(release, _data.PathFor("updates"), progress);
            stage = "install";
            UpdateBannerText = $"Installing GCI {version} and restarting…";
            _updater.InstallAndRestart(downloaded, _launchArgs);
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateBannerText = $"Update failed: {ex.Message}";
            UpdateStatusText = UpdateBannerText;
            _telemetry.Track("update_failed", new Dictionary<string, object?>
            {
                ["from"] = CurrentVersion, ["to"] = version, ["stage"] = stage, ["error"] = ex.GetType().Name,
            });
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private void ViewReleaseNotes() => OpenUrl(AvailableUpdate?.PageUrl ?? _updates.ReleasesPage);

    [RelayCommand]
    private void DismissUpdate() => ShowUpdateBanner = false;

    // ---- News feeds ---------------------------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<NewsRow> _newsRows = [];
    [ObservableProperty] private NewsRow? _selectedNews;
    [ObservableProperty] private string _newsHeader = "News";
    [ObservableProperty] private string _newsSummary = "";
    [ObservableProperty] private string _newFeedUrl = "";
    [ObservableProperty] private string _feedMessage = "";
    [ObservableProperty] private bool _isRefreshingNews;
    public ObservableCollection<FeedRow> FeedRows { get; } = new();

    /// <summary>Reads followed feeds and notifies about new posts (every post, or only watch-keyword matches, per feed).</summary>
    private async Task RefreshFeedsAsync()
    {
        if (IsRefreshingNews) return;
        IsRefreshingNews = true;
        try
        {
            var fresh = await Task.Run(() => _feeds.RefreshAsync());
            RebuildNews();
            RebuildFeedRows();

            var sources = _feeds.Sources.ToDictionary(s => s.Id);
            var watches = _watches.Where(w => w.Enabled).ToList();
            var alerts = fresh
                .Select(p => (Post: p, Source: sources.GetValueOrDefault(p.FeedId),
                              Watch: watches.FirstOrDefault(w => WatchMatcher.Matches(w, p))?.Name))
                .Where(x => x.Source is not null && (x.Watch is not null || x.Source.Notify))
                .Select(x => (x.Post, x.Source!.Name, x.Watch))
                .ToList();
            await _notifier.NotifyPostsAsync(alerts, Settings);
            _telemetry.Increment("news_new_posts", fresh.Count);
            foreach (var (post, source, watch) in alerts)
                _telemetry.TrackLimited("news_alert_sent", 10, new Dictionary<string, object?>
                {
                    ["feed"] = source, ["title"] = post.Title, ["matched_watch"] = watch is not null,
                    ["tags"] = string.Join(",", post.Categories.Take(3)),
                });
        }
        catch (Exception ex)
        {
            FeedMessage = $"Couldn't read feeds: {ex.Message}";
        }
        finally
        {
            IsRefreshingNews = false;
        }
    }

    private void RebuildNews()
    {
        var sources = _feeds.Sources.ToDictionary(s => s.Id, s => s.Name);
        var watches = _watches.Where(w => w.Enabled).ToList();
        NewsRows = _feeds.Posts
            .Select(p => new NewsRow(p, sources.GetValueOrDefault(p.FeedId) ?? "", !_feeds.IsRead(p),
                watches.FirstOrDefault(w => WatchMatcher.Matches(w, p))?.Name))
            .ToList();
        UpdateNewsCounts();
    }

    private void UpdateNewsCounts()
    {
        var unread = NewsRows.Count(r => r.IsUnread);
        NewsHeader = unread > 0 ? $"News ({unread})" : "News";
        var lastChecked = _feeds.Sources.Select(s => _feeds.GetStatus(s.Id)?.LastChecked).Max();
        NewsSummary = $"{NewsRows.Count} posts · {unread} unread" +
                      (lastChecked is { } t ? $" · checked {t.LocalDateTime:h:mm tt}" : " · not checked yet");
    }

    private void RebuildFeedRows()
    {
        FeedRows.Clear();
        foreach (var s in _feeds.Sources)
            FeedRows.Add(new FeedRow(s, _feeds.GetStatus(s.Id), row =>
            {
                _feeds.UpdateSource(row.Source.Id, row.Enabled, row.Notify);
                RebuildNews();
                _telemetry.Track("feed_changed", new Dictionary<string, object?>
                {
                    ["feed_host"] = new Uri(row.Url).Host, ["followed"] = row.Enabled, ["notify_every_post"] = row.Notify,
                });
            }));
    }

    [RelayCommand]
    private async Task RefreshNewsAsync()
    {
        FeedMessage = "";
        await RefreshFeedsAsync();
    }

    [RelayCommand]
    private void OpenPost(NewsRow? row)
    {
        row ??= SelectedNews;
        if (row is null) return;
        _telemetry.TrackLimited("news_opened", 30, new Dictionary<string, object?>
        {
            ["feed"] = _feeds.Sources.FirstOrDefault(s => s.Id == row.Post.FeedId)?.Name,
            ["title"] = row.Title,
            ["was_unread"] = row.IsUnread,
            ["matched_watch"] = row.IsMatch,
            ["age_days"] = row.Post.PublishedAt is { } p ? Math.Round((DateTimeOffset.Now - p).TotalDays) : null,
        });
        _feeds.MarkRead(row.Post);
        row.IsUnread = false;
        UpdateNewsCounts();
        OpenUrl(row.Link);
    }

    [RelayCommand]
    private void MarkAllNewsRead()
    {
        _telemetry.Increment("news_mark_all_read");
        _feeds.MarkAllRead();
        RebuildNews();
    }

    [RelayCommand]
    private async Task AddFeedAsync()
    {
        try
        {
            var source = _feeds.AddSource(NewFeedUrl);
            NewFeedUrl = "";
            FeedMessage = "";
            RebuildFeedRows();
            await RefreshFeedsAsync();
            if (_feeds.GetStatus(source.Id)?.Error is { } err) FeedMessage = $"Added, but it couldn't be read: {err}";
            // The feed's host only; a full feed URL can carry personal tokens.
            _telemetry.Track("feed_added", new Dictionary<string, object?>
            {
                ["feed_host"] = new Uri(source.Url).Host, ["readable"] = _feeds.GetStatus(source.Id)?.Error is null,
                ["feeds_total"] = _feeds.Sources.Count,
            });
        }
        catch (ArgumentException ex)
        {
            FeedMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveFeed(FeedRow? row)
    {
        if (row is null) return;
        if (Confirm is { } confirm && !await confirm($"Stop following \"{row.Name}\"?")) return;
        _feeds.RemoveSource(row.Source.Id);
        RebuildFeedRows();
        RebuildNews();
        _telemetry.Track("feed_removed", new Dictionary<string, object?>
        {
            ["feed_host"] = new Uri(row.Url).Host, ["default_feed"] = row.Url == FeedService.PeachScoutUrl,
        });
    }

    // ---- Product images -----------------------------------------------------------------------

    /// <summary>
    /// Tells the image cache which pictures the current menus use (so rotated links are noticed and unused ones
    /// expire) and re-checks due ones against their originals. <paramref name="force"/> re-checks every cached image.
    /// </summary>
    private async Task SweepImagesAsync(bool force)
    {
        if (!ShowImages && !force) return;
        var images = _allRows.Select(r => r.Image)
            .Concat(NewsRows.Select(n => n.Image))
            .Concat(ChangeRows.Select(c => c.Image))
            .OfType<ImageRef>().ToList();
        try
        {
            await _thumbnails.SweepAsync(images, force);
        }
        catch (Exception ex)
        {
            ImagesStatusText = $"Image check failed: {ex.Message}";
            return;
        }
        UpdateImagesStatus();
    }

    [RelayCommand]
    private async Task CheckImagesAsync()
    {
        ImagesStatusText = "Checking every cached image against its original…";
        await SweepImagesAsync(force: true);
        var s = _thumbnails.GetStats();
        _telemetry.Track("images_checked", new Dictionary<string, object?>
        {
            ["cached"] = s.Images, ["changed"] = s.Changed, ["removed"] = s.Removed, ["links_changed"] = s.LinksChanged,
        });
    }

    [RelayCommand]
    private async Task ClearImageCache()
    {
        if (Confirm is { } confirm && !await confirm("Delete all cached product images? They'll download again as you browse.")) return;
        _telemetry.Track("image_cache_cleared", new Dictionary<string, object?> { ["cached"] = _thumbnails.GetStats().Images });
        _thumbnails.Clear();
        RebuildAllRows();
        UpdateImagesStatus();
    }

    private void UpdateImagesStatus()
    {
        var s = _thumbnails.GetStats();
        ImagesStatusText =
            $"{s.Images:N0} thumbnails cached ({s.Bytes / 1024.0 / 1024.0:0.0} MB). " +
            $"Since launch: {s.Downloaded} downloaded, {s.Revalidated} confirmed unchanged, {s.Changed} updated, " +
            $"{s.Removed} removed by the store, {s.LinksChanged} image links changed.";
    }

    [RelayCommand]
    private void DismissWelcome()
    {
        ShowWelcome = false;
        Settings.FirstRunComplete = true;
        SaveSettings();
        _telemetry.Track("welcome_action", new Dictionary<string, object?> { ["action"] = "dismissed" });
    }

    [RelayCommand]
    private void GoToTab(string? index)
    {
        if (!int.TryParse(index, out var i)) return;
        SelectedTabIndex = i;
        _telemetry.Track("welcome_action", new Dictionary<string, object?> { ["action"] = $"go_to_tab_{i}" });
    }

    private void SaveSettings() => _data.Save(SettingsFile, Settings);
    private void SaveWatches() => _data.Save(WatchesFile, _watches);

    public void Dispose()
    {
        _ticker.Stop();
        _refreshCts?.Cancel();
    }
}
