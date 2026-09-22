using System;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Gci.Desktop.Services;

namespace Gci.Desktop.Controls;

/// <summary>
/// An embedded native WKWebView, hosted in the Avalonia visual tree via <see cref="NativeControlHost"/>. Used by the
/// macOS Preorder window to show a store's checkout inside GCI. On any non-macOS platform it falls back to the base
/// placeholder, so the Avalonia app still builds and runs on Windows/Linux for development.
///
/// FIRST CUT: creates the WKWebView and can navigate + run JavaScript. Still to come (iteration 2): a WKNavigationDelegate
/// that raises <see cref="NavigationFinished"/>, and a WKScriptMessageHandler so injected prefill/cart scripts can post
/// results back (the macOS equivalent of WebView2's WebMessageReceived).
/// </summary>
public sealed class MacWebView : NativeControlHost
{
    private IntPtr _webView;
    private string? _pendingUrl;

    /// <summary>Raised after a top-level navigation finishes. Not yet wired on macOS (needs the nav delegate in cut 2).</summary>
#pragma warning disable CS0067
    public event Action? NavigationFinished;
#pragma warning restore CS0067

    public bool IsAvailable => OperatingSystem.IsMacOS() && _webView != IntPtr.Zero;

    /// <summary>Loads a URL (queued until the native control exists).</summary>
    public void Navigate(string url)
    {
        _pendingUrl = url;
        if (OperatingSystem.IsMacOS() && _webView != IntPtr.Zero) LoadUrl(url);
    }

    /// <summary>Runs JavaScript in the page, discarding the result (fire-and-forget).</summary>
    public void EvaluateJavaScript(string js)
    {
        if (!OperatingSystem.IsMacOS() || _webView == IntPtr.Zero) return;
        MacInterop.SendVoid(_webView, MacInterop.Sel("evaluateJavaScript:completionHandler:"), MacInterop.NSString(js), IntPtr.Zero);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS()) return base.CreateNativeControlCore(parent);

        MacInterop.EnsureWebKit();
        var config = MacInterop.New("WKWebViewConfiguration");
        var alloc = MacInterop.Send(MacInterop.GetClass("WKWebView"), MacInterop.Sel("alloc"));
        _webView = MacInterop.SendFrame(alloc, MacInterop.Sel("initWithFrame:configuration:"), MacInterop.CGRect.Zero, config);
        MacInterop.Send(_webView, MacInterop.Sel("retain")); // keep it alive for the lifetime of this host

        if (_pendingUrl is { } url) LoadUrl(url);
        return new MacViewHandle(_webView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsMacOS() && _webView != IntPtr.Zero)
        {
            MacInterop.Send(_webView, MacInterop.Sel("release"));
            _webView = IntPtr.Zero;
        }
        else
        {
            base.DestroyNativeControlCore(control);
        }
    }

    [SupportedOSPlatform("macos")]
    private void LoadUrl(string url)
    {
        var nsUrl = MacInterop.Send(MacInterop.GetClass("NSURL"), MacInterop.Sel("URLWithString:"), MacInterop.NSString(url));
        if (nsUrl == IntPtr.Zero) return;
        var request = MacInterop.Send(MacInterop.GetClass("NSURLRequest"), MacInterop.Sel("requestWithURL:"), nsUrl);
        MacInterop.Send(_webView, MacInterop.Sel("loadRequest:"), request);
    }

    // Tells Avalonia the native handle is an NSView so it parents the WKWebView correctly.
    private sealed class MacViewHandle(IntPtr handle) : IPlatformHandle
    {
        public IntPtr Handle { get; } = handle;
        public string HandleDescriptor => "NSView";
    }
}
