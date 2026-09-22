using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;
using Gci.Desktop.Controls;

namespace Gci.Desktop.Views;

/// <summary>
/// The macOS Preorder window: a store's own checkout inside GCI, in an embedded WKWebView. FIRST CUT loads the product
/// page and keeps the sign-in in-app; the patient adds to cart and checks out on the page, using Open in your browser
/// if anything misbehaves. Iteration 2 adds the automatic add-to-cart and detail autofill (the prefill/cart scripts).
/// </summary>
public partial class PreorderWindow : Window
{
    private MacWebView? _web;
    private PreorderViewModel _vm = null!;

    public PreorderWindow() => InitializeComponent();

    public PreorderWindow(PreorderViewModel vm) : this()
    {
        _web = this.FindControl<MacWebView>("Web");
        Bind(vm);
        Opened += (_, _) => _web?.Navigate(vm.Request.ProductUrl);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Reuses the open window for another product.</summary>
    public void Open(PreorderViewModel vm)
    {
        Bind(vm);
        _web?.Navigate(vm.Request.ProductUrl);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Bind(PreorderViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
    }
}
