using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

/// <summary>
/// Usage telemetry (see README "Privacy"): an anonymous install ping on by default, and opt-in detailed usage.
/// Everything here is anonymous — store and product names come from public menus, keywords are reduced to a fixed
/// vocabulary, and patient details, typed text and URLs are never sent.
/// </summary>
public sealed partial class MainViewModel
{
    public const string PrivacyUrl = "https://github.com/mrjamesroe/gci#privacy";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private TelemetryClient _telemetry = null!;
    private readonly HashSet<string> _sessionOnce = new();
    private readonly DateTimeOffset _sessionStart = DateTimeOffset.Now;
    private int _refreshesThisSession;

    [ObservableProperty] private bool _shareUsageStats;
    [ObservableProperty] private bool _basicTelemetry;
    [ObservableProperty] private bool _showTelemetryBanner;

    public bool TelemetryAvailable => _telemetry.IsConfigured;

    private void InitTelemetry(TelemetryClient telemetry)
    {
        _telemetry = telemetry;
        _telemetry.BasicEnabled = Settings.BasicTelemetry;
        _telemetry.DetailedEnabled = Settings.ShareUsageStats == true;
#pragma warning disable MVVMTK0034 // loading, not a user change
        _shareUsageStats = Settings.ShareUsageStats == true;
        _basicTelemetry = Settings.BasicTelemetry;
#pragma warning restore MVVMTK0034
        // The banner only asks about the deeper opt-in tier; the basic install ping is on by default.
        ShowTelemetryBanner = telemetry.IsConfigured && Settings.ShareUsageStats is null;
    }

    partial void OnShareUsageStatsChanged(bool value) => SetConsent(value, "settings");

    partial void OnBasicTelemetryChanged(bool value)
    {
        if (Settings.BasicTelemetry == value) return;
        Settings.BasicTelemetry = value;
        SaveSettings();
        // Record the choice before switching the ping off, so an opt-out is itself counted once.
        if (!value) _telemetry.TrackBasic("basic_telemetry_off", new Dictionary<string, object?> { ["install_id"] = Settings.InstallId });
        _telemetry.BasicEnabled = value;
    }

    [RelayCommand] private void AcceptTelemetry() => SetConsent(true, "banner");
    [RelayCommand] private void DeclineTelemetry() => SetConsent(false, "banner");
    [RelayCommand] private void OpenPrivacy() => OpenUrl(PrivacyUrl);

    [RelayCommand]
    private void ViewTelemetryLog()
    {
        if (!File.Exists(_telemetry.LogPath)) File.WriteAllText(_telemetry.LogPath, "");
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_telemetry.LogPath}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SettingsMessage = $"Couldn't open the log: {ex.Message}";
        }
    }

    private void SetConsent(bool share, string source)
    {
        var changed = Settings.ShareUsageStats != share;
        Settings.ShareUsageStats = share;
        SaveSettings();
        ShowTelemetryBanner = false;
        if (ShareUsageStats != share) ShareUsageStats = share;
        _telemetry.DetailedEnabled = share;
        if (share && changed)
        {
            _telemetry.Track("telemetry_enabled", new Dictionary<string, object?> { ["source"] = source });
            TrackAppStarted("consent");
        }
    }

    /// <summary>Called once per process by the app: install cohort, launch count, and version upgrades.</summary>
    public void RecordLaunch(string launchKind)
    {
        var now = DateTimeOffset.Now;
        var firstRun = Settings.InstallDate is null;
        Settings.InstallDate ??= now;
        Settings.InstallId ??= Guid.NewGuid().ToString("N");
        Settings.LaunchCount++;
        var previous = Settings.LastRunVersion;
        Settings.LastRunVersion = CurrentVersion;
        SaveSettings();

        TrackAppStarted(launchKind, firstRun);
        TrackActivePing();
        if (previous is not null && previous != CurrentVersion)
            _telemetry.Track("app_upgraded", new Dictionary<string, object?>
            {
                ["from"] = previous,
                ["to"] = CurrentVersion,
                ["method"] = launchKind == "updated" ? "auto_update" : "manual",
            });
    }

    /// <summary>
    /// The install ping. Basic tier is minimal and anonymous (version, OS, a random install id); when the user has also
    /// opted into detailed stats, the same event carries the full environment and their settings/counts.
    /// </summary>
    private void TrackAppStarted(string launchKind, bool firstRun = false)
    {
        if (!_telemetry.BasicEnabled && !_telemetry.DetailedEnabled) return;
        var env = TelemetryEnvironment.Snapshot();
        var props = new Dictionary<string, object?>
        {
            ["install_id"] = Settings.InstallId,
            ["launch"] = launchKind,
            ["first_run"] = firstRun,
            ["launches_total"] = Settings.LaunchCount,
            ["install_week"] = InstallWeek(),
            ["days_since_install"] = DaysSinceInstall(),
            ["windows"] = env.GetValueOrDefault("windows"),
            ["os_build"] = env.GetValueOrDefault("os_build"),
            ["arch"] = env.GetValueOrDefault("arch"),
            ["dotnet"] = env.GetValueOrDefault("dotnet"),
            ["detailed_stats"] = _telemetry.DetailedEnabled,
        };
        if (_telemetry.DetailedEnabled)
        {
            foreach (var (k, v) in env) props[k] = v;
            props["refresh_minutes"] = Settings.EffectiveRefreshMinutes;
            props["auto_refresh"] = Settings.AutoRefresh;
            props["toasts"] = Settings.ToastNotifications;
            props["ntfy"] = !string.IsNullOrWhiteSpace(Settings.NtfyTopicUrl);
            props["images"] = Settings.ShowImages;
            props["tray"] = Settings.MinimizeToTray;
            props["start_with_windows"] = Settings.StartWithWindows;
            props["start_minimized"] = Settings.StartMinimized;
            props["check_updates"] = Settings.CheckForUpdates;
            props["welcome_done"] = Settings.FirstRunComplete;
            props["patient_profile"] = !Profile.Current.IsEmpty;
            props["card_expiry"] = ExpiryBucket(Profile.Current.DaysUntilExpiry(DateTime.Today));
            props["watches"] = _watches.Count;
            props["stores_monitored"] = EnabledKeys().Count;
            props["feeds"] = _feeds.Sources.Count;
        }
        _telemetry.TrackBasic("app_started", props);
    }

    /// <summary>Anonymous once-a-day "still running" ping so a long-lived install counts as active. Basic tier.</summary>
    public void TrackActivePing()
    {
        if (!_telemetry.BasicEnabled) return;
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Settings.LastActivePing == today) return;
        Settings.LastActivePing = today;
        SaveSettings();
        _telemetry.TrackBasic("app_active", new Dictionary<string, object?>
        {
            ["install_id"] = Settings.InstallId,
            ["days_since_install"] = DaysSinceInstall(),
        });
    }

    public void TrackSessionEnded()
    {
        _telemetry.Track("session_ended", new Dictionary<string, object?>
        {
            ["minutes"] = Math.Round((DateTimeOffset.Now - _sessionStart).TotalMinutes),
            ["refreshes"] = _refreshesThisSession,
            ["tabs_viewed"] = _sessionOnce.Count(k => k.StartsWith("tab:")),
        });
    }

    public void TrackEvent(string name, Dictionary<string, object?>? props = null) => _telemetry.Track(name, props);

    // ---- Interaction events -------------------------------------------------------------------

    private void TrackTab(int index)
    {
        string[] names = ["inventory", "watches", "changes", "news", "stores", "patient", "settings"];
        var name = index >= 0 && index < names.Length ? names[index] : index.ToString(CultureInfo.InvariantCulture);
        if (_sessionOnce.Add($"tab:{name}"))
            _telemetry.Track("tab_viewed", new Dictionary<string, object?> { ["tab"] = name });
    }

    private void TrackFilter(string dimension, string? value)
    {
        if (value is null || !_sessionOnce.Add($"filter:{dimension}:{value}")) return;
        _telemetry.Track("filter_used", new Dictionary<string, object?> { ["filter"] = dimension, ["value"] = value });
    }

    private string _lastSearch = "";

    private void TrackSearch(string text)
    {
        if (_lastSearch.Length == 0 && text.Length > 0)
        {
            _telemetry.Increment("searches");
            if (_sessionOnce.Add("search")) _telemetry.Track("search_used");
        }
        _lastSearch = text;
    }

    private void TrackSetting(string setting, object value) =>
        _telemetry.Track("setting_changed", new Dictionary<string, object?> { ["setting"] = setting, ["value"] = value });

    private static Dictionary<string, object?> ItemProps(InventoryItem item, StoreInfo store) => new()
    {
        ["operator"] = store.Operator,
        ["store"] = store.DisplayName,
        ["platform"] = store.Provider.ToString(),
        ["pharmacy"] = store.IsPharmacyPartner,
        ["category"] = item.Category.ToString(),
        ["product"] = item.Name,
        ["brand"] = item.Brand,
        ["size"] = item.Size,
        ["price"] = item.EffectivePrice,
        ["quantity"] = item.Quantity,
        ["in_stock"] = item.InStock,
        ["on_sale"] = item.SalePrice is not null,
    };

    [RelayCommand]
    private void OpenProduct(ItemRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        _telemetry.TrackLimited("product_opened", 40, ItemProps(row.Item, row.Store));
        OpenUrl(row.Url ?? row.Store.MenuUrl);
    }

    [RelayCommand]
    private void OpenStoreMenu(StoreRow? row)
    {
        row ??= SelectedStoreRow;
        if (row is null) return;
        _telemetry.TrackLimited("store_menu_opened", 20, new Dictionary<string, object?>
        {
            ["store"] = row.Store.DisplayName, ["operator"] = row.Store.Operator, ["platform"] = row.Store.Provider.ToString(),
        });
        OpenUrl(row.Store.MenuUrl);
    }

    private void TrackRefresh(RefreshResult result, IReadOnlyList<(ChangeEvent Event, WatchRule Rule)> matches, TimeSpan elapsed)
    {
        _refreshesThisSession++;
        _telemetry.Increment("refreshes");
        _telemetry.Increment("refresh_seconds", Math.Round(elapsed.TotalSeconds, 1));
        _telemetry.Increment("refresh_store_failures", result.StoresFailed);
        foreach (var group in result.Events.Where(e => e.Kind != ChangeKind.QuantityChanged).GroupBy(e => e.Kind))
            _telemetry.Increment($"changes_{group.Key}", group.Count());
        _telemetry.Increment("alerts", matches.Count);

        // A store platform breaking (e.g. Dutchie rotating its query hash) shows up here within a day.
        foreach (var store in _inventory.Stores.Where(s => EnabledKeys().Contains(s.Key)))
        {
            if (_inventory.GetStatus(store.Key)?.Error is not { } error) continue;
            var errorClass = ErrorClass(error);
            _telemetry.TrackOnce($"platform_error:{store.Provider}:{errorClass}", Day, "platform_error", new Dictionary<string, object?>
            {
                ["platform"] = store.Provider.ToString(),
                ["operator"] = store.Operator,
                ["store"] = store.DisplayName,
                ["error"] = errorClass,
            });
        }

        foreach (var (e, rule) in matches.DistinctBy(m => (m.Event.ItemKey, m.Event.Kind)))
        {
            var store = _inventory.GetStore(e.StoreKey);
            _telemetry.TrackLimited("alert_sent", 30, new Dictionary<string, object?>
            {
                ["kind"] = e.Kind.ToString(),
                ["operator"] = e.Operator,
                ["store"] = e.StoreName,
                ["platform"] = store?.Provider.ToString(),
                ["category"] = e.Category.ToString(),
                ["product"] = e.Name,
                ["brand"] = e.Brand,
                ["size"] = e.Size,
                ["price"] = e.NewPrice ?? e.OldPrice,
                ["quantity"] = e.NewQuantity,
                ["watch_keywords"] = rule.Keywords.Count > 0,
                ["watch_category"] = rule.Category?.ToString(),
                ["toast"] = Settings.ToastNotifications,
                ["ntfy"] = !string.IsNullOrWhiteSpace(Settings.NtfyTopicUrl),
            });
        }
    }

    private void TrackWatchSaved(WatchRule rule, string source, bool isNew)
    {
        var (terms, custom) = KeywordVocabulary.Classify(rule.Keywords);
        var fromProduct = source is "product_here" or "product_anywhere";
        var props = new Dictionary<string, object?>
        {
            ["action"] = isNew ? "created" : "edited",
            ["source"] = source,
            ["category"] = rule.Category?.ToString() ?? "any",
            ["operator"] = rule.Operator ?? "any",
            ["keyword_count"] = rule.Keywords.Count,
            ["keyword_terms"] = terms.Count > 0 ? string.Join(",", terms) : null,
            ["custom_keywords"] = fromProduct ? 0 : custom,
            // "Watch this product" keywords are a public catalog name, not something the user typed.
            ["product"] = fromProduct ? rule.Keywords.FirstOrDefault() : null,
            ["store_scope"] = rule.StoreKeys.Count switch { 0 => "any", 1 => "1", <= 5 => "2-5", _ => "6+" },
            ["store"] = rule.StoreKeys.Count == 1 ? _inventory.GetStore(rule.StoreKeys[0])?.DisplayName : null,
            ["max_price"] = rule.MaxPrice,
            ["low_stock"] = rule.LowStockThreshold,
            ["notify_available"] = rule.NotifyAvailable,
            ["notify_restock"] = rule.NotifyRestock,
            ["notify_price_drop"] = rule.NotifyPriceDrop,
            ["notify_sold_out"] = rule.NotifySoldOut,
            ["enabled"] = rule.Enabled,
            ["matching_now"] = _allRows.Count(r => r.InStock && WatchMatcher.Matches(rule, r.Item, r.Store)),
            ["watches_total"] = _watches.Count,
        };
        _telemetry.Track("watch_saved", props);
    }

    private void TrackStoreDiscovered(StoreInfo store) =>
        _telemetry.Track("store_discovered", new Dictionary<string, object?>
        {
            ["store"] = store.DisplayName, ["operator"] = store.Operator, ["platform"] = store.Provider.ToString(),
            ["pharmacy"] = store.IsPharmacyPartner, ["city"] = store.City,
        });

    // ---- Daily summary ------------------------------------------------------------------------

    /// <summary>
    /// One event per install per day with the full picture: watches, which stores are monitored (one numeric prop
    /// per store, so Aptabase can sum installs per store), what's in stock, and the day's counters.
    /// </summary>
    private void MaybeSendDailySummary()
    {
        if (!_telemetry.DetailedEnabled) return;
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Settings.LastSummaryDay == today) return;
        Settings.LastSummaryDay = today;
        SaveSettings();

        var enabled = EnabledKeys().ToHashSet();
        var stores = _inventory.Stores.Where(s => enabled.Contains(s.Key)).ToList();
        var inStock = _allRows.Where(r => r.InStock).ToList();
        var images = _thumbnails.GetStats();

        var props = new Dictionary<string, object?>
        {
            ["days_since_install"] = DaysSinceInstall(),
            ["install_week"] = InstallWeek(),
            ["uptime_hours"] = Math.Round((DateTimeOffset.Now - _sessionStart).TotalHours, 1),
            ["watches"] = _watches.Count,
            ["watches_enabled"] = _watches.Count(w => w.Enabled),
            ["watches_keywords"] = _watches.Count(w => w.Keywords.Count > 0),
            ["watches_category"] = _watches.Count(w => w.Category is not null),
            ["watches_concentrate"] = _watches.Count(w => w.Category == ProductCategory.Concentrate
                                                          || KeywordVocabulary.Classify(w.Keywords).Recognized.Any(IsConcentrateTerm)),
            ["watches_store_filter"] = _watches.Count(w => w.StoreKeys.Count > 0),
            ["watches_low_stock"] = _watches.Count(w => w.LowStockThreshold is not null),
            ["stores_monitored"] = stores.Count,
            ["dispensaries_monitored"] = stores.Count(s => !s.IsPharmacyPartner),
            ["pharmacies_monitored"] = stores.Count(s => s.IsPharmacyPartner),
            ["items_total"] = _allRows.Count,
            ["items_in_stock"] = inStock.Count,
            ["feeds"] = _feeds.Sources.Count,
            ["news_posts"] = NewsRows.Count,
            ["news_unread"] = NewsRows.Count(n => n.IsUnread),
            ["images_cached"] = images.Images,
            ["images_mb"] = Math.Round(images.Bytes / 1024.0 / 1024.0, 1),
            ["images_changed"] = images.Changed,
            ["images_removed"] = images.Removed,
            ["image_links_changed"] = images.LinksChanged,
            ["curl_hosts"] = string.Join(",", _inventory.CurlFallbackHosts.Order()),
            ["browser_hosts"] = string.Join(",", _inventory.BrowserFallbackHosts.Order()),
            ["patient_profile"] = !Profile.Current.IsEmpty,
            ["card_expiry"] = ExpiryBucket(Profile.Current.DaysUntilExpiry(DateTime.Today)),
        };
        foreach (var group in stores.GroupBy(s => s.Operator))
            props[$"op_{group.Key}"] = group.Count();
        foreach (var store in stores)
            props[$"st_{StoreSlug(store)}"] = 1;
        foreach (var group in inStock.GroupBy(r => r.Item.Category))
            props[$"stock_{group.Key}"] = group.Count();
        foreach (var (key, value) in _telemetry.TakeCounters())
            props[key] = value;

        _telemetry.Track("daily_summary", props);
    }

    /// <summary>Short, stable name for per-store props (Aptabase keys are capped at 40 characters).</summary>
    internal static string StoreSlug(StoreInfo store)
    {
        var name = store.IsPharmacyPartner ? store.Name : $"{store.Operator} {store.Name}";
        var slug = TelemetryScrubber.Key(name);
        return slug.Length <= 37 ? slug : slug[..37];
    }

    private static bool IsConcentrateTerm(string term) =>
        CategoryNormalizer.Normalize(null, term) == ProductCategory.Concentrate;

    private string InstallWeek()
    {
        var date = (Settings.InstallDate ?? DateTimeOffset.Now).LocalDateTime;
        return $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):00}";
    }

    private int DaysSinceInstall() => (int)(DateTimeOffset.Now - (Settings.InstallDate ?? DateTimeOffset.Now)).TotalDays;

    private static string ExpiryBucket(int? days) => days switch
    {
        null => "none",
        < 0 => "expired",
        <= 30 => "under_30d",
        <= 90 => "30_90d",
        _ => "over_90d",
    };

    internal static string ErrorClass(string error)
    {
        var e = error.ToLowerInvariant();
        if (e.Contains("persisted query")) return "dutchie_query_hash";
        if (e.Contains("webview2 runtime")) return "webview2_missing";
        if (e.Contains("cloudflare")) return "cloudflare_blocked";
        if (e.Contains("edge webview2")) return "webview2_failed";
        if (e.Contains("http 403")) return "http_403";
        if (e.Contains("http 404")) return "http_404";
        if (e.Contains("http 429")) return "http_429";
        if (e.Contains("http 5")) return "http_5xx";
        if (e.Contains("curl")) return "curl_failed";
        if (e.Contains("empty")) return "empty_menu";
        if (e.Contains("non-json") || e.Contains("unexpected")) return "bad_response";
        if (e.Contains("timed out") || e.Contains("timeout") || e.Contains("canceled")) return "timeout";
        if (e.Contains("graphql")) return "graphql_error";
        return "other";
    }
}
