using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;

namespace Gci.Desktop.Views;

public partial class PhoneSetupWindow : Window
{
    private PhoneSetupViewModel? _vm;

    public PhoneSetupWindow() => InitializeComponent();

    public PhoneSetupWindow(PhoneSetupViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    private void OnTurnOff(object? sender, RoutedEventArgs e)
    {
        _vm?.TurnOffCommand.Execute(null);
        Close();
    }
}
