using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
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
    private readonly Notifier _notifier;
    private readonly ThumbnailService _thumbnails;
    private readonly FeedService _feeds;
    private readonly UpdateChecker _updates;
    private readonly IReadOnlyList<string> _launchArgs;
    private readonly Version _currentVersion;
    private bool _checkedUpdatesThisSession;
    private bool _checkingUpdates;
    private readonly DispatcherTimer _timer;
    private readonly List<WatchRule> _watches;
    private List<ItemRow> _allRows = new();
    private DateTimeOffset _nextRefresh = DateTimeOffset.Now.AddSeconds(2);
    private CancellationTokenSource? _refreshCts;
    private bool _bulkStoreEdit;

    public MainViewModel(DataStore data, InventoryService inventory, ProfileStore profiles, Notifier notifier,
        ThumbnailService thumbnails, FeedService feeds, UpdateChecker updates, IReadOnlyList<string> launchArgs)
    {
        _data = data;
        _inventory = inventory;
        _notifier = notifier;
        _thumbnails = thumbnails;
        _feeds = feeds;
        _updates = updates;
        _launchArgs = launchArgs;
        _currentVersion = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);
        CurrentVersion = _currentVersion.ToString(3);
        Settings = data.Load(SettingsFile, () => new AppSettings());
        _watches = data.Load(WatchesFile, () => new List<WatchRule>());
        Profile = new ProfileViewModel(profiles);

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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public AppSettings Settings { get; }
    public ProfileViewModel Profile { get; }

    /// <summary>Set by the view: shows the watch editor, returns true when saved.</summary>
    public Func<WatchEditorViewModel, bool>? ShowWatchEditor { get; set; }
    public Func<string, bool>? Confirm { get; set; }
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

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedCategoryChanged(Option<ProductCategory?> value) => ApplyFilters();
    partial void OnSelectedOperatorChanged(Option<string?> value) => ApplyFilters();
    partial void OnSelectedStoreChanged(Option<string?> value) => ApplyFilters();
    partial void OnInStockOnlyChanged(bool value) => ApplyFilters();
    partial void OnWatchedOnlyChanged(bool value) => ApplyFilters();

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

    partial void OnShowImagesChanged(bool value) { Settings.ShowImages = value; SaveSettings(); }

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
    }

    partial void OnAutoRefreshChanged(bool value) { Settings.AutoRefresh = value; SaveSettings(); ScheduleNext(); }
    partial void OnToastNotificationsChanged(bool value) { Settings.ToastNotifications = value; SaveSettings(); }
    partial void OnNtfyTopicUrlChanged(string value) { Settings.NtfyTopicUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); SaveSettings(); }
    partial void OnMinimizeToTrayChanged(bool value) { Settings.MinimizeToTray = value; SaveSettings(); }
    partial void OnStartMinimizedChanged(bool value) { Settings.StartMinimized = value; SaveSettings(); }

    partial void OnStartWithWindowsChanged(bool value)
    {
        Settings.StartWithWindows = value;
        SaveSettings();
        try
        {
            StartupRegistration.Apply(value);
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
            var result = await Task.Run(() => _inventory.RefreshAsync(keys, token, progress), token);

            RefreshStoreStatuses();
            RebuildAllRows();
            RebuildChanges();
            _ = SweepImagesAsync(force: false);

            var logged = result.Events.Where(e => e.Kind != ChangeKind.QuantityChanged).ToList();
            var matches = result.Events
                .SelectMany(e => _watches.Where(w => WatchMatcher.Matches(w, e)).Select(w => (e, w)))
                .ToList();
            await _notifier.NotifyAsync(matches, Settings);
            await RefreshFeedsAsync();

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
    }

    private void OnStoreToggled(StoreRow row)
    {
        var keys = Settings.EnabledStoreKeys ??= new();
        keys.Remove(row.Store.Key);
        if (row.IsEnabled) keys.Add(row.Store.Key);
        if (_bulkStoreEdit) return;
        AfterStoreSelectionChanged();
    }

    private void AfterStoreSelectionChanged()
    {
        SaveSettings();
        RebuildAllRows();
        UpdateStoresSummary();
        if (!IsRefreshing && EnabledKeys().Any(k => _inventory.GetStatus(k) is null))
            _nextRefresh = DateTimeOffset.Now.AddSeconds(3);
    }

    private void SetStores(Func<StoreRow, bool> enable)
    {
        _bulkStoreEdit = true;
        foreach (var row in StoreRows) row.IsEnabled = enable(row);
        _bulkStoreEdit = false;
        AfterStoreSelectionChanged();
    }

    [RelayCommand] private void EnableDispensaries() => SetStores(r => r.IsEnabled || !r.Store.IsPharmacyPartner);
    [RelayCommand] private void EnableAllStores() => SetStores(_ => true);
    [RelayCommand] private void DisableAllStores() => SetStores(_ => false);

    [RelayCommand]
    private async Task RediscoverStoresAsync()
    {
        if (IsRefreshing) return;
        await EnsureStoresAsync(force: true);
        StatusText = $"Found {_inventory.Stores.Count} stores.";
        RebuildAllRows();
    }

    // ---- Watches ------------------------------------------------------------------------------

    private void RebuildWatches()
    {
        WatchRows.Clear();
        foreach (var w in _watches)
            WatchRows.Add(new WatchRow(w, key => _inventory.GetStore(key)?.DisplayName,
                _ => { SaveWatches(); RebuildAllRows(); RebuildChanges(); RebuildNews(); }));
        UpdateWatchCounts();
    }

    private void UpdateWatchCounts()
    {
        foreach (var row in WatchRows)
            row.MatchingNow = _allRows.Count(r => r.InStock && WatchMatcher.Matches(row.Rule, r.Item, r.Store));
    }

    [RelayCommand]
    private void AddWatch() => EditAndSave(new WatchRule { Name = "New watch" }, isNew: true);

    [RelayCommand]
    private void EditWatch(WatchRow? row)
    {
        row ??= SelectedWatch;
        if (row is not null) EditAndSave(row.Rule, isNew: false);
    }

    [RelayCommand]
    private void DeleteWatch(WatchRow? row)
    {
        row ??= SelectedWatch;
        if (row is null) return;
        if (Confirm?.Invoke($"Delete the watch \"{row.Name}\"?") == false) return;
        _watches.RemoveAll(w => w.Id == row.Rule.Id);
        SaveWatches();
        RebuildWatches();
        RebuildAllRows();
        RebuildChanges();
        RebuildNews();
    }

    [RelayCommand]
    private void WatchProductHere(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        EditAndSave(new WatchRule
        {
            Name = $"{row.Name} @ {row.Store.Name}",
            Keywords = { row.Name },
            StoreKeys = { row.Store.Key },
            LowStockThreshold = row.Quantity is not null ? 3 : null,
        }, isNew: true);
    }

    [RelayCommand]
    private void WatchProductAnywhere(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        EditAndSave(new WatchRule { Name = row.Name, Keywords = { row.Name } }, isNew: true);
    }

    [RelayCommand]
    private void WatchCategoryAtStore(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        EditAndSave(new WatchRule
        {
            Name = $"{row.Item.Category} @ {row.Store.Name}",
            Category = row.Item.Category,
            StoreKeys = { row.Store.Key },
        }, isNew: true);
    }

    [RelayCommand]
    private void WatchCurrentFilters()
    {
        var rule = new WatchRule
        {
            Category = SelectedCategory?.Value,
            Operator = SelectedOperator?.Value,
            Keywords = string.IsNullOrWhiteSpace(SearchText) ? new() : new() { SearchText.Trim() },
        };
        if (SelectedStore?.Value is { } store) rule.StoreKeys.Add(store);
        rule.Name = rule.Summary == "Everything" ? "Everything" : rule.Summary;
        EditAndSave(rule, isNew: true);
    }

    private void EditAndSave(WatchRule rule, bool isNew)
    {
        if (ShowWatchEditor is null) return;
        var current = _allRows.Select(r => (r.Item, r.Store)).ToList();
        var editor = new WatchEditorViewModel(rule, _inventory.Stores, current);
        if (!ShowWatchEditor(editor)) return;

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
    }

    // ---- Changes ------------------------------------------------------------------------------

    private void RebuildChanges()
    {
        var watches = _watches.Where(w => w.Enabled).ToList();
        var rows = _inventory.Changes
            .Select(e => new ChangeRow(e, watches.Any(w => WatchMatcher.Matches(w, e))))
            .Where(r => !ChangesWatchedOnly || r.Watched)
            .Take(MaxChangeRows)
            .ToList();
        ChangeRows = rows;
        var today = rows.Count(r => r.At.LocalDateTime.Date == DateTime.Today);
        ChangesSummary = $"{rows.Count:N0} changes shown · {today} today";
    }

    [RelayCommand]
    private void ClearChanges()
    {
        if (Confirm?.Invoke("Clear the change history?") == false) return;
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
        }
        else
        {
            SettingsMessage = "Sent a test toast.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenUrl(_data.Root);

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

    partial void OnCheckForUpdatesChanged(bool value) { Settings.CheckForUpdates = value; SaveSettings(); }

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
        if (!UpdateInstaller.CanSelfUpdate(out var reason))
        {
            UpdateStatusText = reason;
            UpdateBannerText = reason;
            OpenUrl(release.PageUrl);
            return;
        }

        IsInstallingUpdate = true;
        var version = release.Version.ToString(3);
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateBannerText = $"Downloading GCI {version}… {p:P0}";
            });
            var downloaded = await _updates.DownloadAsync(release, _data.PathFor("updates"), progress);
            UpdateBannerText = $"Installing GCI {version} and restarting…";
            UpdateInstaller.InstallAndRestart(downloaded, _launchArgs);
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateBannerText = $"Update failed: {ex.Message}";
            UpdateStatusText = UpdateBannerText;
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
        _feeds.MarkRead(row.Post);
        row.IsUnread = false;
        UpdateNewsCounts();
        OpenUrl(row.Link);
    }

    [RelayCommand]
    private void MarkAllNewsRead()
    {
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
        }
        catch (ArgumentException ex)
        {
            FeedMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveFeed(FeedRow? row)
    {
        if (row is null) return;
        if (Confirm?.Invoke($"Stop following \"{row.Name}\"?") == false) return;
        _feeds.RemoveSource(row.Source.Id);
        RebuildFeedRows();
        RebuildNews();
    }

    // ---- Product images -----------------------------------------------------------------------

    /// <summary>
    /// Tells the image cache which pictures the current menus use (so rotated links are noticed and unused ones
    /// expire) and re-checks due ones against their originals. <paramref name="force"/> re-checks every cached image.
    /// </summary>
    private async Task SweepImagesAsync(bool force)
    {
        if (!ShowImages && !force) return;
        var images = _allRows.Select(r => r.Image).Concat(NewsRows.Select(n => n.Image)).OfType<ImageRef>().ToList();
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
    }

    [RelayCommand]
    private void ClearImageCache()
    {
        if (Confirm?.Invoke("Delete all cached product images? They'll download again as you browse.") == false) return;
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
    }

    [RelayCommand]
    private void GoToTab(string? index)
    {
        if (int.TryParse(index, out var i)) SelectedTabIndex = i;
    }

    private void SaveSettings() => _data.Save(SettingsFile, Settings);
    private void SaveWatches() => _data.Save(WatchesFile, _watches);

    public void Dispose()
    {
        _timer.Stop();
        _refreshCts?.Cancel();
    }
}
