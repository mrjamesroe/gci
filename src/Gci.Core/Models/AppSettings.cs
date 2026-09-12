namespace Gci.Core.Models;

public sealed class AppSettings
{
    public const int MinimumRefreshMinutes = 5;

    public int RefreshMinutes { get; set; } = 15;
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Stores to monitor. Null until first discovery, which then seeds defaults.</summary>
    public List<string>? EnabledStoreKeys { get; set; }

    public bool ToastNotifications { get; set; } = true;
    /// <summary>Optional ntfy topic URL (e.g. https://ntfy.sh/my-topic) for phone push.</summary>
    public string? NtfyTopicUrl { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowImages { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>Newest version already announced, so each release notifies once.</summary>
    public string? LastAnnouncedVersion { get; set; }

    /// <summary>Every store key GCI has seen, so newly opened stores can be announced and auto-enabled.</summary>
    public List<string>? KnownStoreKeys { get; set; }
    public DateTimeOffset? LastDiscovery { get; set; }
    public DateTime? LastExpiryReminder { get; set; }
    public bool FirstRunComplete { get; set; }

    public int EffectiveRefreshMinutes => Math.Max(MinimumRefreshMinutes, RefreshMinutes);
}
