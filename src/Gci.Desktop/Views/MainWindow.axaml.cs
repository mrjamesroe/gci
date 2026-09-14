using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;
using Gci.Core.Models;
using Gci.Core.Providers;

namespace Gci.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Touch Gci.Core and the shared presentation layer so both are proven to link and run on this OS.
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var userAgent = new ProviderConfig().UserAgent;
        var probe = new Option<ProductCategory?>(null, "All categories"); // a shared-VM type, compiled for this platform
        this.FindControl<TextBlock>("Engine")!.Text =
            $"v{version} · {RuntimeInformation.OSDescription.Trim()} · shared Gci.Core + Gci.Presentation loaded " +
            $"({userAgent}; {probe.Label}).";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
