using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;

namespace Gci.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        vm.ShowWatchEditor = async editor => await new WatchEditorWindow(editor).ShowDialog<bool>(this);
        vm.ShowPhoneSetup = async setup => await new PhoneSetupWindow(setup).ShowDialog(this);
        vm.Confirm = message => Dialogs.ConfirmAsync(this, message);
        // ShowPreorder is left unset until the WKWebView-backed Preorder window lands (Phase 4);
        // the VM then falls back to opening the store page in the default browser.
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e) => Run(_vm?.OpenProductCommand);
    private void OnWatchDoubleTapped(object? sender, TappedEventArgs e) => Run(_vm?.EditWatchCommand, _vm?.SelectedWatch);
    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e) => Run(_vm?.OpenUrlCommand, _vm?.SelectedChange?.Url);
    private void OnStoreDoubleTapped(object? sender, TappedEventArgs e) => Run(_vm?.OpenStoreMenuCommand);
    private void OnNewsDoubleTapped(object? sender, TappedEventArgs e) => Run(_vm?.OpenPostCommand);

    private static void Run(System.Windows.Input.ICommand? command, object? parameter = null)
    {
        if (command is not null && command.CanExecute(parameter)) command.Execute(parameter);
    }
}
