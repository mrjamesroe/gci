using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;
using Gci.Core.Services;
using Gci.Desktop.Controls;

namespace Gci.Desktop.Views;

/// <summary>
/// The macOS Preorder window: a store's own checkout inside GCI, in an embedded WKWebView. The chosen product is put in
/// the bag (Trulieve / Botanical Sciences) and the patient's saved details are filled into checkout — the same
/// prefill.js/cart.js the Windows app uses, driven over the WKWebView message bridge. Nothing is submitted by GCI; the
/// patient reviews and places the order on the page. Details are only ever sent to HTTPS pages on store sites.
/// </summary>
public partial class PreorderWindow : Window
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private MacWebView? _web;
    private PreorderViewModel _vm = null!;
    private bool _cartStarted;

    public PreorderWindow() => InitializeComponent();

    public PreorderWindow(PreorderViewModel vm) : this()
    {
        _web = this.FindControl<MacWebView>("Web");
        if (_web is not null)
        {
            _web.NavigationFinished += OnNavigationFinished;
            _web.ScriptMessage += OnScriptMessage;
        }
        Bind(vm);
        Opened += (_, _) => _web?.Navigate(vm.Request.ProductUrl);
        Closed += (_, _) => _vm.FillRequested -= OnFillRequested;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Reuses the open window for another product.</summary>
    public void Open(PreorderViewModel vm)
    {
        Bind(vm);
        _cartStarted = false;
        _web?.Navigate(vm.Request.ProductUrl);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Bind(PreorderViewModel vm)
    {
        if (_vm is not null) _vm.FillRequested -= OnFillRequested;
        _vm = vm;
        DataContext = vm;
        vm.FillRequested += OnFillRequested;
    }

    private void OnNavigationFinished()
    {
        _vm.OnPageLoaded();
        if (_cartStarted || _vm.CartRecipe is not { } recipe) return;
        _cartStarted = true;
        _web?.EvaluateJavaScript($"window.__gciCart && window.__gciCart.{recipe}()");
    }

    private void OnScriptMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "gci-ready":
                    SendFill(manual: false);
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
            // Not one of ours / malformed.
        }
    }

    private void OnFillRequested()
    {
        if (!SendFill(manual: true))
            _vm.HintText = "GCI only fills your details into store checkout pages (on the store's own site).";
    }

    /// <summary>Sends the patient's details to the page, only when it's an HTTPS page on a store site.</summary>
    private bool SendFill(bool manual)
    {
        var prefill = _vm.Prefill;
        if (prefill.FieldCount == 0 || _web is null) return false;
        if (!Uri.TryCreate(_vm.Request.ProductUrl, UriKind.Absolute, out var product)) return false;
        if (_web.CurrentUrl is not { } current || !Uri.TryCreate(current, UriKind.Absolute, out var page)
            || !PreorderSites.MayFill(page, product)) return false;
        _web.PostMessage(JsonSerializer.Serialize(new { type = "gci-fill", data = prefill, manual }, Json));
        return true;
    }
}
