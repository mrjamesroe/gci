using Gci.App.Services;

namespace Gci.Desktop.Services;

/// <summary>
/// On macOS an embedded browser is available (WKWebView, always present in the OS), so Preorder can open the store's
/// checkout inside a GCI window. On other platforms it isn't, and Preorder falls back to the system browser.
/// </summary>
public sealed class MacEmbeddedBrowser : IEmbeddedBrowser
{
    public bool IsAvailable => OperatingSystem.IsMacOS();
}
