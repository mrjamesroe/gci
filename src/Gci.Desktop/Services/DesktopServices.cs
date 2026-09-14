using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Desktop.Services;

// Platform-service implementations for the cross-platform (Avalonia) app. The engine (Gci.Core) and the shared
// ViewModels (Gci.Presentation) are fully wired here; the pieces that still need native work are honest placeholders,
// each labelled with the phase that fills it in. The Windows (WPF) app keeps its own richer implementations.

/// <summary>A UI-thread ticker backed by Avalonia's <see cref="DispatcherTimer"/> — drives the auto-refresh loop.</summary>
public sealed class DesktopTicker : ITicker
{
    private readonly DispatcherTimer _timer;

    public DesktopTicker(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public event Action? Tick;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}

/// <summary>Non-identifying facts about the Mac/build, for telemetry. Portable equivalent of the WPF TelemetryEnvironment.</summary>
public sealed class DesktopSystemSnapshot : ISystemSnapshot
{
    public static TelemetrySystemInfo SystemInfo()
    {
        var asm = typeof(DesktopSystemSnapshot).Assembly;
        var version = asm.GetName().Version?.ToString(3) ?? "0.0.0";
        var os = RuntimeInformation.OSDescription.Trim();
        var model = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsLinux() ? "linux" : "desktop";
#if DEBUG
        const bool isDebug = true;
#else
        var isDebug = Environment.GetEnvironmentVariable("GCI_TELEMETRY_DEBUG") == "1";
#endif
        return new TelemetrySystemInfo(os, CultureInfo.CurrentCulture.Name, version, null, model, isDebug);
    }

    public Dictionary<string, object?> Snapshot() => new()
    {
        ["platform"] = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "desktop",
        ["os"] = RuntimeInformation.OSDescription.Trim(),
        ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
        ["dotnet"] = Environment.Version.ToString(3),
        ["cpu_count"] = Environment.ProcessorCount,
        ["culture"] = CultureInfo.CurrentCulture.Name,
        ["ui_culture"] = CultureInfo.CurrentUICulture.Name,
        ["timezone"] = TimeZoneInfo.Local.Id,
    };
}

/// <summary>Desktop notifications land in Phase 4 (native macOS notifications + ntfy). For now, silent.</summary>
public sealed class NoopNotifier : INotifier
{
    public Task NotifyAsync(IReadOnlyList<(ChangeEvent Event, WatchRule Rule)> matches, AppSettings settings) => Task.CompletedTask;
    public Task NotifyPostsAsync(IReadOnlyList<(FeedPost Post, string Source, string? Watch)> posts, AppSettings settings) => Task.CompletedTask;
    public void ShowToast(string title, string body, string? attribution, string? url, string? tag, string openLabel = "Open menu",
        (string StoreKey, string Name)? preorder = null) { }
    public Task<string?> SendNtfyAsync(string topicUrl, string title, string body, string? url, string actionLabel = "Open") =>
        Task.FromResult<string?>("Phone alerts aren't available in the macOS build yet.");
}

/// <summary>Product thumbnails land in Phase 4 (SkiaSharp decode). For now, the cache is inert.</summary>
public sealed class NoopThumbnailCache : IThumbnailCache
{
    public Task SweepAsync(IReadOnlyCollection<ImageRef> images, bool force = false) => Task.CompletedTask;
    public ImageCacheStats GetStats() => new(0, 0, 0, 0, 0, 0, 0, 0);
    public void Clear() { }
}

/// <summary>Encrypted patient storage lands in Phase 4 (macOS Keychain). For now, nothing is persisted.</summary>
public sealed class NoopProfileStore : IProfileStore
{
    public PatientProfile Load() => new();
    public void Save(PatientProfile profile) { }
    public void Delete() { }
}

/// <summary>WKWebView-backed Cloudflare fallback / Preorder lands in Phase 4.</summary>
public sealed class NoopEmbeddedBrowser : IEmbeddedBrowser
{
    public bool IsAvailable => false;
}

/// <summary>Mac self-update lands in Phase 5; until then the release page is the path.</summary>
public sealed class NoopUpdateInstaller : IUpdateInstaller
{
    public bool CanSelfUpdate(out string reason)
    {
        reason = "In-app updates aren't available in the macOS build yet; download the latest from the releases page.";
        return false;
    }

    public void InstallAndRestart(string downloadedExe, IEnumerable<string> args) =>
        throw new NotSupportedException("Self-update is not supported on macOS yet.");
}

/// <summary>"Launch at login" (a macOS LaunchAgent) lands in Phase 4.</summary>
public sealed class NoopStartupRegistration : IStartupRegistration
{
    public void Apply(bool enabled) { }
}

/// <summary>Clipboard access is wired with the phone-setup dialog in a later step.</summary>
public sealed class NoopClipboard : IClipboard
{
    public void SetText(string text) { }
}
