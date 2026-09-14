using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Gci.Core.Providers;
using Microsoft.Web.WebView2.Core;

namespace Gci.App.Services;

/// <summary>
/// Reads menu APIs through a hidden Microsoft Edge WebView2 when a host's bot protection rejects both .NET and curl.
/// Each request is a same-origin fetch() from a blank page served on the API's own host, so it goes out with Edge's
/// own connection fingerprint and cookies and needs no CORS. If a fetch meets a Cloudflare challenge, the host's home
/// page is opened (invisibly) so the challenge can clear the way it does in a browser, then the fetch is retried.
/// The WebView starts only when first needed and closes after a few idle minutes.
/// </summary>
public sealed class BrowserTransport : IBrowserTransport, IEmbeddedBrowser, IDisposable
{
    private const string BlankPath = "/__gci_blank__";
    private const int Width = 1024;
    private const int Height = 768;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(25);
    private static readonly string[] SkippedHeaders = ["User-Agent", "Accept-Encoding", "Host", "Content-Length", "Connection"];

    private readonly Dispatcher _dispatcher;
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly DispatcherTimer _idle;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private HwndSource? _host;
    private string? _origin;
    private bool _disposed;

    public BrowserTransport(string userDataFolder, Dispatcher dispatcher)
    {
        _userDataFolder = userDataFolder;
        _dispatcher = dispatcher;
        _idle = new DispatcherTimer(IdleTimeout, DispatcherPriority.Background, (_, _) => Close(), dispatcher) { IsEnabled = false };
    }

    /// <summary>Whether the Cloudflare fallback / Preorder browser can run here (the WebView2 Runtime is installed).</summary>
    public bool IsAvailable => RuntimeVersion() is not null;

    /// <summary>Installed WebView2 Runtime version, or null if there is none.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            return await _dispatcher.InvokeAsync(() => SendOnUiThreadAsync(method, url, json, headers, ct)).Task.Unwrap();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(int Status, string Body)> SendOnUiThreadAsync(HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        _idle.Stop();
        try
        {
            var web = await EnsureWebViewAsync();
            var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
            if (_origin != origin)
            {
                await NavigateAsync(web, origin + BlankPath, ct);
                _origin = origin;
            }

            var result = await FetchAsync(web, method, url, json, headers, ct);
            if (ProviderHttp.IsBlocked(result))
            {
                await PassChallengeAsync(web, origin, ct);
                await NavigateAsync(web, origin + BlankPath, ct);
                result = await FetchAsync(web, method, url, json, headers, ct);
            }
            return result;
        }
        catch (Exception ex) when (ex is not (BrowserUnavailableException or OperationCanceledException or ProviderException))
        {
            // Start fresh next time in case the WebView got into a bad state.
            Close();
            throw new ProviderException($"Edge WebView2 couldn't read {new Uri(url).Host}: {ex.Message}");
        }
        finally
        {
            if (!_disposed) _idle.Start();
        }
    }

    private async Task<CoreWebView2> EnsureWebViewAsync()
    {
        if (_controller is not null) return _controller.CoreWebView2;
        if (RuntimeVersion() is null)
            throw new BrowserUnavailableException("the Microsoft Edge WebView2 Runtime isn't installed");

        _environment ??= await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
        // A never-shown popup window hosts the WebView; nothing appears on screen or in the taskbar.
        _host ??= new HwndSource(new HwndSourceParameters("GCI menu reader")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
            ExtendedWindowStyle = 0x80 | 0x08000000,  // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            PositionX = -32000,
            PositionY = -32000,
            Width = Width,
            Height = Height,
        });
        var controller = await _environment.CreateCoreWebView2ControllerAsync(_host.Handle);
        controller.Bounds = new System.Drawing.Rectangle(0, 0, Width, Height);
        controller.IsVisible = true; // hidden pages get their timers throttled, which slows Cloudflare challenges

        var web = controller.CoreWebView2;
        var settings = web.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        web.IsMuted = true;
        web.NewWindowRequested += (_, e) => e.Handled = true;
        web.DownloadStarting += (_, e) => e.Cancel = true;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.ProcessFailed += (_, _) => _dispatcher.BeginInvoke(Close);
        web.WebMessageReceived += OnWebMessage;

        // The blank page each fetch runs from: served locally, so only the fetch itself touches the network.
        web.AddWebResourceRequestedFilter("*" + BlankPath, CoreWebView2WebResourceContext.Document);
        web.WebResourceRequested += (_, e) =>
        {
            if (!e.Request.Uri.EndsWith(BlankPath, StringComparison.Ordinal) || _environment is null) return;
            var page = new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><title>GCI</title>"));
            e.Response = _environment.CreateWebResourceResponse(page, 200, "OK", "Content-Type: text/html; charset=utf-8");
        };

        _controller = controller;
        return web;
    }

    private static async Task NavigateAsync(CoreWebView2 web, string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        web.NavigationCompleted += OnCompleted;
        try
        {
            web.Navigate(url);
            await done.Task.WaitAsync(NavigationTimeout, ct);
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>Opens the host's home page and waits (briefly) for a Cloudflare challenge page to clear itself.</summary>
    private static async Task PassChallengeAsync(CoreWebView2 web, string origin, CancellationToken ct)
    {
        try
        {
            await NavigateAsync(web, origin + "/", ct);
        }
        catch (TimeoutException)
        {
            // A heavy home page may still be loading; the challenge check below is what matters.
        }

        const string isChallenge = """
            (() => /just a moment|checking your browser|attention required|verify you are human/i.test(document.title || '')
                || !!window._cf_chl_opt
                || !!document.querySelector('#challenge-form, #challenge-stage, .cf-turnstile, #cf-wrapper, .cf-error-details'))()
            """;
        var until = DateTime.UtcNow + ChallengeTimeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(750, ct);
            try
            {
                if (await web.ExecuteScriptAsync(isChallenge) == "false") return;
            }
            catch (Exception)
            {
                // The page is mid-navigation (a solved challenge reloads it); check again.
            }
        }
    }

    private async Task<(int Status, string Body)> FetchAsync(CoreWebView2 web, HttpMethod method, string url, string? json,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await web.ExecuteScriptAsync(FetchScript(id, method, url, json, headers));
            var message = await reply.Task.WaitAsync(FetchTimeout, ct);
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new ProviderException($"Edge WebView2 couldn't reach {new Uri(url).Host}: {error.GetString()}");
            return (root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal static string FetchScript(string id, HttpMethod method, string url, string? json, IDictionary<string, string>? headers)
    {
        var sendHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
            if (!SkippedHeaders.Contains(k, StringComparer.OrdinalIgnoreCase)) sendHeaders[k] = v;
        if (json is not null && !sendHeaders.ContainsKey("Content-Type")) sendHeaders["Content-Type"] = "application/json";

        var init = new Dictionary<string, object?>
        {
            ["method"] = method.Method,
            ["headers"] = sendHeaders,
            ["credentials"] = "include",
            ["cache"] = "no-store",
        };
        if (json is not null) init["body"] = json;

        var timeoutMs = (int)FetchTimeout.TotalMilliseconds - 5000;
        return $$"""
            (() => {
              const id = {{JsonSerializer.Serialize(id)}};
              const abort = new AbortController();
              setTimeout(() => abort.abort(), {{timeoutMs}});
              fetch({{JsonSerializer.Serialize(url)}}, Object.assign({{JsonSerializer.Serialize(init)}}, { signal: abort.signal }))
                .then(r => r.text().then(body => ({ id, status: r.status, body })))
                .catch(e => ({ id, error: String(e) }))
                .then(m => window.chrome.webview.postMessage(JSON.stringify(m)));
            })();
            """;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return; // not a string message, so not ours
        }
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } key && _pending.TryRemove(key, out var reply))
                reply.TrySetResult(message);
        }
        catch (JsonException)
        {
            // Some page script posted something else; ignore it.
        }
    }

    private void Close()
    {
        _idle.Stop();
        foreach (var reply in _pending.Values) reply.TrySetCanceled();
        _pending.Clear();
        _controller?.Close();
        _controller = null;
        _environment = null;
        _origin = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dispatcher.CheckAccess()) DisposeOnUiThread();
        else _dispatcher.Invoke(DisposeOnUiThread);
    }

    private void DisposeOnUiThread()
    {
        Close();
        _host?.Dispose();
        _host = null;
    }
}
