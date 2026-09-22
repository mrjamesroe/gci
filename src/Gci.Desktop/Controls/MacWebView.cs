using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Gci.Desktop.Services;
using static Gci.Desktop.Services.MacInterop;

namespace Gci.Desktop.Controls;

/// <summary>
/// An embedded native WKWebView, hosted in the Avalonia visual tree via <see cref="NativeControlHost"/>. Used by the
/// macOS Preorder window to show a store's checkout inside GCI. On any non-macOS platform it falls back to the base
/// placeholder, so the Avalonia app still builds and runs on Windows/Linux for development.
///
/// It injects the same prefill.js/cart.js the Windows app ships, ahead of a small shim that maps WebView2's
/// <c>window.chrome.webview</c> messaging API onto WKWebView's <c>window.webkit.messageHandlers</c>. A single custom
/// Objective-C class (built once) serves as both the navigation delegate and the script-message handler, routing
/// callbacks back to the right managed instance by the WKWebView pointer.
///
/// Written on Windows; verify on a real Mac. The msgSend signatures (CGRect-by-value, the IMP registration) are the
/// parts most likely to need adjustment.
/// </summary>
public sealed class MacWebView : NativeControlHost
{
    private IntPtr _webView;
    private IntPtr _handler;   // the shared delegate/message-handler instance (retained)
    private string? _pendingUrl;

    /// <summary>Raised after a top-level navigation finishes (main thread).</summary>
    public event Action? NavigationFinished;

    /// <summary>Raised with the raw JSON string a page posted via the injected scripts (main thread).</summary>
    public event Action<string>? ScriptMessage;

    public bool IsAvailable => OperatingSystem.IsMacOS() && _webView != IntPtr.Zero;

    /// <summary>Loads a URL (queued until the native control exists).</summary>
    public void Navigate(string url)
    {
        _pendingUrl = url;
        if (OperatingSystem.IsMacOS() && _webView != IntPtr.Zero) LoadUrl(url);
    }

    /// <summary>Runs JavaScript in the page, discarding the result (fire-and-forget). No-op off macOS.</summary>
    public void EvaluateJavaScript(string js)
    {
        if (!OperatingSystem.IsMacOS() || _webView == IntPtr.Zero) return;
        SendVoid(_webView, Sel("evaluateJavaScript:completionHandler:"), NSString(js), IntPtr.Zero);
    }

    /// <summary>Delivers a message object to the page (the native→JS direction of the WebView2 shim).</summary>
    public void PostMessage(string json) => EvaluateJavaScript($"window.__gciDeliver && window.__gciDeliver({json})");

    /// <summary>The page's current absolute URL, or null (off macOS or before first load).</summary>
    public string? CurrentUrl
    {
        get
        {
            if (!OperatingSystem.IsMacOS() || _webView == IntPtr.Zero) return null;
            var nsUrl = Send(_webView, Sel("URL"));
            return nsUrl == IntPtr.Zero ? null : FromNSString(Send(nsUrl, Sel("absoluteString")));
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS()) return base.CreateNativeControlCore(parent);
        return CreateMac();
    }

    [SupportedOSPlatform("macos")]
    private IPlatformHandle CreateMac()
    {
        EnsureWebKit();

        var config = New("WKWebViewConfiguration");
        var ucc = Send(config, Sel("userContentController"));
        AddUserScript(ucc, ShimJs);
        AddUserScript(ucc, Script("prefill.js"));
        AddUserScript(ucc, Script("cart.js"));

        _handler = Send(Send(DelegateClass(), Sel("alloc")), Sel("init"));
        Send(_handler, Sel("retain"));
        Send(ucc, Sel("addScriptMessageHandler:name:"), _handler, NSString("gci"));

        var alloc = Send(GetClass("WKWebView"), Sel("alloc"));
        _webView = SendFrame(alloc, Sel("initWithFrame:configuration:"), CGRect.Zero, config);
        Send(_webView, Sel("retain"));
        Send(_webView, Sel("setNavigationDelegate:"), _handler);

        Instances[_webView] = this;
        if (_pendingUrl is { } url) LoadUrl(url);
        return new MacViewHandle(_webView);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsMacOS() && _webView != IntPtr.Zero)
        {
            Instances.TryRemove(_webView, out _);
            Send(_webView, Sel("release"));
            if (_handler != IntPtr.Zero) Send(_handler, Sel("release"));
            _webView = _handler = IntPtr.Zero;
        }
        else
        {
            base.DestroyNativeControlCore(control);
        }
    }

    [SupportedOSPlatform("macos")]
    private void LoadUrl(string url)
    {
        var nsUrl = Send(GetClass("NSURL"), Sel("URLWithString:"), NSString(url));
        if (nsUrl == IntPtr.Zero) return;
        var request = Send(GetClass("NSURLRequest"), Sel("requestWithURL:"), nsUrl);
        Send(_webView, Sel("loadRequest:"), request);
    }

    [SupportedOSPlatform("macos")]
    private static void AddUserScript(IntPtr ucc, string source)
    {
        var alloc = Send(GetClass("WKUserScript"), Sel("alloc"));
        // injectionTime 0 = WKUserScriptInjectionTimeAtDocumentStart; main frame only.
        var script = SendUserScript(alloc, Sel("initWithSource:injectionTime:forMainFrameOnly:"), NSString(source), 0, true);
        Send(ucc, Sel("addUserScript:"), script);
    }

    // ---- The shared native delegate class (nav delegate + script-message handler) ----------------------------------

    private static readonly ConcurrentDictionary<IntPtr, MacWebView> Instances = new();

    private static IntPtr _delegateClass;
    private static readonly object DelegateClassLock = new();

    /// <summary>The shared delegate class, built once on first use (only ever reached on macOS).</summary>
    [SupportedOSPlatform("macos")]
    private static IntPtr DelegateClass()
    {
        lock (DelegateClassLock)
        {
            if (_delegateClass == IntPtr.Zero) _delegateClass = BuildDelegateClass();
            return _delegateClass;
        }
    }

    [SupportedOSPlatform("macos")]
    private static unsafe IntPtr BuildDelegateClass()
    {
        var cls = objc_allocateClassPair(GetClass("NSObject"), "GciWebDelegate", IntPtr.Zero);
        class_addMethod(cls, Sel("webView:didFinishNavigation:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidFinishNavigation, "v@:@@");
        class_addMethod(cls, Sel("userContentController:didReceiveScriptMessage:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDidReceiveScriptMessage, "v@:@@");
        objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("macos")]
    private static void OnDidFinishNavigation(IntPtr self, IntPtr cmd, IntPtr webView, IntPtr navigation)
    {
        if (Instances.TryGetValue(webView, out var view)) view.NavigationFinished?.Invoke();
    }

    [UnmanagedCallersOnly]
    [SupportedOSPlatform("macos")]
    private static void OnDidReceiveScriptMessage(IntPtr self, IntPtr cmd, IntPtr ucc, IntPtr message)
    {
        var webView = Send(message, Sel("webView"));
        var body = FromNSString(Send(message, Sel("body")));
        if (body is not null && Instances.TryGetValue(webView, out var view)) view.ScriptMessage?.Invoke(body);
    }

    // Adapts the WebView2 messaging API (window.chrome.webview) that prefill.js/cart.js use onto WKWebView. Injected
    // before them, at document start. postMessage → webkit handler; native pushes objects back via window.__gciDeliver.
    private const string ShimJs = """
        (function () {
          if (window.__gciShim) return; window.__gciShim = 1;
          var handlers = [];
          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage: function (s) { try { window.webkit.messageHandlers.gci.postMessage(String(s)); } catch (e) {} },
            addEventListener: function (t, h) { if (t === 'message') handlers.push(h); },
            removeEventListener: function (t, h) { if (t === 'message') { var i = handlers.indexOf(h); if (i >= 0) handlers.splice(i, 1); } }
          };
          window.__gciDeliver = function (obj) { for (var i = 0; i < handlers.length; i++) { try { handlers[i]({ data: obj }); } catch (e) {} } };
        })();
        """;

    private static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing embedded {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Tells Avalonia the native handle is an NSView so it parents the WKWebView correctly.
    private sealed class MacViewHandle(IntPtr handle) : IPlatformHandle
    {
        public IntPtr Handle { get; } = handle;
        public string HandleDescriptor => "NSView";
    }
}
