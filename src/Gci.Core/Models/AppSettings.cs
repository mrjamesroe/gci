namespace Gci.Core.Models;

/// <summary>Which color theme the app uses. System follows the OS light/dark setting.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public const int MinimumRefreshMinutes = 5;

    /// <summary>Light/Dark/System color theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public int RefreshMinutes { get; set; } = 15;
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Stores to monitor. Null until first discovery, which then seeds defaults.</summary>
    public List<string>? EnabledStoreKeys { get; set; }

    public bool ToastNotifications { get; set; } = true;
    /// <summary>Optional ntfy topic URL (e.g. https://ntfy.sh/my-topic) for phone push.</summary>
    public string? NtfyTopicUrl { get; set; }
    /// <summary>The user said "Not now" to the phone-alerts reminder.</summary>
    public bool PhoneNudgeDismissed { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowImages { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Show the sponsor message in the header (hardware/accessory brands, for awareness). On by default.</summary>
    public bool ShowSponsor { get; set; } = true;

    /// <summary>
    /// Detailed, opt-in usage stats: null = not asked yet (the banner still shows), then the user's choice. The basic
    /// anonymous install ping (<see cref="BasicTelemetry"/>) is separate and does not wait on this.
    /// </summary>
    public bool? ShareUsageStats { get; set; }
    /// <summary>The anonymous install ping. On by default; the user can turn it off in Settings.</summary>
    public bool BasicTelemetry { get; set; } = true;
    /// <summary>Random, local, non-identifying id so installs and retention can be counted. Generated once.</summary>
    public string? InstallId { get; set; }
    public DateTimeOffset? InstallDate { get; set; }
    public int LaunchCount { get; set; }
    /// <summary>Local date the daily "still active" ping last fired, so it fires at most once per day.</summary>
    public string? LastActivePing { get; set; }
    public string? LastRunVersion { get; set; }
    public string? LastSummaryDay { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>Newest version already announced, so each release notifies once.</summary>
    public string? LastAnnouncedVersion { get; set; }

    /// <summary>Every store key GCI has seen, so newly opened stores can be announced and auto-enabled.</summary>
    public List<string>? KnownStoreKeys { get; set; }
    public DateTimeOffset? LastDiscovery { get; set; }
    public DateTime? LastExpiryReminder { get; set; }
    public bool FirstRunComplete { get; set; }
    /// <summary>The version tag of the disclaimer the user has accepted (null = never). Re-prompts when the text changes.</summary>
    public string? DisclaimerAcceptedVersion { get; set; }

    public int EffectiveRefreshMinutes => Math.Max(MinimumRefreshMinutes, RefreshMinutes);
}
