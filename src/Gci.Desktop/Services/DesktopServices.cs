using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Desktop.Services;

// Platform-service implementations for the cross-platform (Avalonia) app. The engine (Gci.Core) and the shared
// ViewModels (Gci.Presentation) are fully wired here. Native desktop notifications, thumbnails, encrypted profile
// storage, the embedded browser, self-update and launch-at-login are honest placeholders, each labelled with the
// phase that fills it in. ntfy phone push and the clipboard are portable, so they work now.

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

/// <summary>
/// Sends watch/news alerts to the phone via ntfy (portable HTTP, so it works on macOS today). Local desktop
/// notifications ("toasts") arrive with native macOS notifications in Phase 4; for now they're silent.
/// </summary>
public sealed class DesktopNotifier : INotifier
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task NotifyAsync(IReadOnlyList<(ChangeEvent Event, WatchRule Rule)> matches, AppSettings settings)
    {
        if (matches.Count == 0 || string.IsNullOrWhiteSpace(settings.NtfyTopicUrl)) return;
        foreach (var (e, _) in matches.DistinctBy(m => (m.Event.ItemKey, m.Event.Kind)).Take(8))
            await SendNtfyAsync(settings.NtfyTopicUrl!, Title(e), $"{Body(e)}\n{e.StoreName}", e.Url, ActionLabel(e));
    }

    public async Task NotifyPostsAsync(IReadOnlyList<(FeedPost Post, string Source, string? Watch)> posts, AppSettings settings)
    {
        if (posts.Count == 0 || string.IsNullOrWhiteSpace(settings.NtfyTopicUrl)) return;
        foreach (var (post, source, watch) in posts.Take(8))
            await SendNtfyAsync(settings.NtfyTopicUrl!, $"{(watch is null ? "" : "★ ")}{source}: {post.Title}",
                post.Summary ?? post.Title, post.Link, "Read post");
    }

    // Native macOS notifications land in Phase 4; the Changes/News tabs already have everything.
    public void ShowToast(string title, string body, string? attribution, string? url, string? tag, string openLabel = "Open menu",
        (string StoreKey, string Name)? preorder = null) { }

    public async Task<string?> SendNtfyAsync(string topicUrl, string title, string body, string? url, string actionLabel = "Open")
    {
        try
        {
            using var req = BuildNtfyRequest(topicUrl, title, body, url, actionLabel);
            using var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode ? null : $"ntfy returned HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ActionLabel(ChangeEvent e) => e.Kind == ChangeKind.SoldOut ? "View" : "Order now";

    private static HttpRequestMessage BuildNtfyRequest(string topicUrl, string title, string body, string? url, string actionLabel)
    {
        var text = url is null ? body : $"{body}\n{actionLabel}: {url}";
        var req = new HttpRequestMessage(HttpMethod.Post, topicUrl)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        };
        req.Headers.TryAddWithoutValidation("Title", IsAscii(title) ? title : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}?=");
        req.Headers.TryAddWithoutValidation("Tags", "leaves");
        if (url is not null && IsAscii(url))
        {
            req.Headers.TryAddWithoutValidation("Click", url);
            var quoted = url.IndexOfAny([',', ';']) >= 0 ? $"\"{url}\"" : url;
            req.Headers.TryAddWithoutValidation("Actions", $"action=view, label={actionLabel}, url={quoted}, clear=true");
        }
        return req;
    }

    private static string Title(ChangeEvent e) => e.Kind switch
    {
        ChangeKind.NewProduct => $"New: {e.Name}",
        ChangeKind.BackInStock => $"Back in stock: {e.Name}",
        ChangeKind.Restocked => $"Restocked: {e.Name}",
        ChangeKind.SoldOut => $"Sold out: {e.Name}",
        ChangeKind.PriceDrop => $"Price drop: {e.Name}",
        ChangeKind.QuantityChanged => $"Low stock: {e.Name}",
        _ => e.Name,
    };

    private static string Body(ChangeEvent e)
    {
        var parts = new List<string>();
        if (e.Size is not null) parts.Add(e.Size);
        if (e.Kind == ChangeKind.PriceDrop) parts.Add($"{e.OldPrice:C0} → {e.NewPrice:C0}");
        else if (e.NewPrice is { } p) parts.Add(p.ToString("C0"));
        if (e.Kind == ChangeKind.QuantityChanged) parts.Add($"only {e.NewQuantity} left");
        else if (e.Kind == ChangeKind.Restocked && e.OldQuantity is { } o && e.NewQuantity is { } n && n > o) parts.Add($"{o} → {n} units");
        else if (e.NewQuantity is { } q && e.Kind != ChangeKind.SoldOut) parts.Add($"{q} available");
        if (e.Detail is not null) parts.Add(e.Detail);
        return string.Join(" · ", parts);
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);
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

/// <summary>The system clipboard, via the main window's Avalonia clipboard.</summary>
public sealed class DesktopClipboard : IClipboard
{
    public void SetText(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            _ = window.Clipboard?.SetTextAsync(text);
    }
}
