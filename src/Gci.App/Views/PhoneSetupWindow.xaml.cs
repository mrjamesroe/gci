using System.Windows;
using Gci.App.ViewModels;

namespace Gci.App.Views;

public partial class PhoneSetupWindow : Window
{
    private readonly PhoneSetupViewModel _vm;

    public PhoneSetupWindow(PhoneSetupViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnTurnOff(object sender, RoutedEventArgs e)
    {
        _vm.TurnOffCommand.Execute(null);
        DialogResult = false;
    }
}
