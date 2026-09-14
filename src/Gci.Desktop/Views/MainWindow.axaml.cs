using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Gci.Core.Providers;

namespace Gci.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Touch Gci.Core so the shared engine is proven to link and run on this OS.
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var userAgent = new ProviderConfig().UserAgent;
        this.FindControl<TextBlock>("Engine")!.Text =
            $"v{version} · {RuntimeInformation.OSDescription.Trim()} · shared Gci.Core loaded ({userAgent}).";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
