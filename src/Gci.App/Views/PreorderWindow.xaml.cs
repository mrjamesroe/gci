using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Gci.App.ViewModels;
using Gci.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace Gci.App.Views;

/// <summary>
/// A store's own website in a GCI window, with the chosen product put in the bag and the patient's details filled into
/// checkout. It keeps its own Edge WebView2 profile so a store sign-in is remembered between preorders. Details are
/// only ever sent to top-level HTTPS pages on store sites (<see cref="PreorderSites"/>), and nothing is submitted by GCI.
/// </summary>
public partial class PreorderWindow : Window
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> PrefillScript = new(() => ReadAsset("prefill.js"));
    private static readonly Lazy<string> CartScript = new(() => ReadAsset("cart.js"));

    private readonly string _userDataFolder;
    private PreorderViewModel _vm;
    private bool _cartStarted;
    private bool _ready;

    public PreorderWindow(PreorderViewModel vm, string userDataFolder)
    {
        InitializeComponent();
        _vm = vm;
        _userDataFolder = userDataFolder;
        DataContext = vm;
        vm.FillRequested += OnFillRequested;
        // Hardware-rendered WPF content next to WebView2's GPU surface can come out black; this window's WPF part is
        // just a header, so software rendering costs nothing.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
                source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        };
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Web.Dispose();
    }

    /// <summary>Reuses the open window for another product.</summary>
    public void Open(PreorderViewModel vm)
    {
        _vm.FillRequested -= OnFillRequested;
        _vm = vm;
        DataContext = vm;
        vm.FillRequested += OnFillRequested;
        _cartStarted = false;
        if (_ready) Web.CoreWebView2.Navigate(vm.Request.ProductUrl);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            ShowUnavailable(ex is WebView2RuntimeNotFoundException
                ? "The Preorder window needs Microsoft Edge WebView2, which isn't installed on this PC. Use Open in browser for now, or install WebView2 from the banner in GCI's main window."
                : $"The Preorder window couldn't start Microsoft Edge WebView2 ({ex.Message}). Use Open in browser instead.");
            return;
        }

        var web = Web.CoreWebView2;
        var settings = web.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = false;
        // Links that open a new window (e.g. a store's policy pages) stay in this one.
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) web.Navigate(e.Uri);
        };
        web.DownloadStarting += (_, e) => e.Cancel = true;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.WebMessageReceived += OnWebMessage;
        web.NavigationCompleted += OnNavigationCompleted;
        await web.AddScriptToExecuteOnDocumentCreatedAsync(PrefillScript.Value);
        await web.AddScriptToExecuteOnDocumentCreatedAsync(CartScript.Value);
        _ready = true;
        web.Navigate(_vm.Request.ProductUrl);
    }

    private void ShowUnavailable(string message)
    {
        Web.Visibility = Visibility.Collapsed;
        Unavailable.Text = message;
        Unavailable.Visibility = Visibility.Visible;
        _vm.StatusText = "Preorder isn't available on this PC yet.";
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        _vm.OnPageLoaded();
        if (_cartStarted || _vm.CartRecipe is not { } recipe) return;
        _cartStarted = true;
        try
        {
            await Web.CoreWebView2.ExecuteScriptAsync($"window.__gciCart && window.__gciCart.{recipe}()");
        }
        catch (Exception)
        {
            _vm.OnCartStep("no-button");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "gci-ready":
                    SendFill(e.Source, manual: false);
                    break;
                case "gci-filled":
                    var fields = root.GetProperty("fields").EnumerateArray().Select(f => f.GetString() ?? "").ToList();
                    _vm.OnFilled(fields, root.TryGetProperty("manual", out var m) && m.GetBoolean());
                    break;
                case "gci-cart":
                    _vm.OnCartStep(root.GetProperty("step").GetString() ?? "");
                    break;
            }
        }
        catch (Exception)
        {
            // Not one of ours.
        }
    }

    private void OnFillRequested()
    {
        if (!_ready) return;
        if (!SendFill(Web.CoreWebView2.Source, manual: true))
            _vm.HintText = "GCI only fills your details into store checkout pages (on the store's own site).";
    }

    /// <summary>Sends the patient's details to the page, only if it (still) is an HTTPS page on a store site.</summary>
    private bool SendFill(string source, bool manual)
    {
        var prefill = _vm.Prefill;
        if (prefill.FieldCount == 0 || !_ready) return false;
        if (!Uri.TryCreate(_vm.Request.ProductUrl, UriKind.Absolute, out var product)) return false;
        foreach (var page in new[] { source, Web.CoreWebView2.Source })
            if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || !PreorderSites.MayFill(uri, product)) return false;
        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "gci-fill", data = prefill, manual }, Json));
        return true;
    }

    private static string ReadAsset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Gci.App.Assets.{name}")
                           ?? throw new InvalidOperationException($"Missing embedded {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
