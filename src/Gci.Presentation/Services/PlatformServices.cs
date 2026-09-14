using Gci.Core.Models;
using Gci.Core.Services;

// These interfaces keep the shared ViewModels UI- and OS-agnostic: the WPF app implements them with Windows toasts,
// DPAPI, the registry and WebView2; the macOS (Avalonia) app implements the same contracts with its own platform APIs.
// They live in the Gci.App.Services namespace so the ViewModels' existing usings resolve unchanged after the move.
namespace Gci.App.Services;

/// <summary>Delivers watch/news alerts as desktop notifications and optional ntfy phone pushes.</summary>
public interface INotifier
{
    Task NotifyAsync(IReadOnlyList<(ChangeEvent Event, WatchRule Rule)> matches, AppSettings settings);
    Task NotifyPostsAsync(IReadOnlyList<(FeedPost Post, string Source, string? Watch)> posts, AppSettings settings);
    void ShowToast(string title, string body, string? attribution, string? url, string? tag, string openLabel = "Open menu",
        (string StoreKey, string Name)? preorder = null);
    Task<string?> SendNtfyAsync(string topicUrl, string title, string body, string? url, string actionLabel = "Open");
}

/// <summary>The product-image cache, as the ViewModels use it (decoding to platform bitmaps stays in the UI layer).</summary>
public interface IThumbnailCache
{
    Task SweepAsync(IReadOnlyCollection<ImageRef> images, bool force = false);
    ImageCacheStats GetStats();
    void Clear();
}

/// <summary>Reads and writes the patient profile to encrypted local storage (DPAPI on Windows, Keychain on macOS).</summary>
public interface IProfileStore
{
    PatientProfile Load();
    void Save(PatientProfile profile);
    void Delete();
}

/// <summary>The "start GCI when I sign in" entry (HKCU Run on Windows, a LaunchAgent on macOS).</summary>
public interface IStartupRegistration
{
    void Apply(bool enabled);
}

/// <summary>
/// Whether an embedded browser is available for the Cloudflare fallback and Preorder. On Windows this is the Edge
/// WebView2 Runtime (which may be missing); on macOS WKWebView is always present.
/// </summary>
public interface IEmbeddedBrowser
{
    bool IsAvailable { get; }
}

/// <summary>Replaces the running copy with a downloaded build and restarts (self-update).</summary>
public interface IUpdateInstaller
{
    bool CanSelfUpdate(out string reason);
    void InstallAndRestart(string downloadedExe, IEnumerable<string> args);
}

/// <summary>Non-identifying facts about the machine and build, for telemetry.</summary>
public interface ISystemSnapshot
{
    Dictionary<string, object?> Snapshot();
}

/// <summary>The system clipboard.</summary>
public interface IClipboard
{
    void SetText(string text);
}

/// <summary>A periodic timer that raises <see cref="Tick"/> on the UI thread.</summary>
public interface ITicker
{
    event Action Tick;
    void Start();
    void Stop();
}
