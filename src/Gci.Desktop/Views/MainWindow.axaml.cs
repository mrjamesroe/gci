using System.ComponentModel;
using System.Linq;
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

        ApplyImageSetting();
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowImages)) ApplyImageSetting();
    }

    /// <summary>DataGrid columns aren't in the visual tree, so the image column is toggled here rather than bound.</summary>
    private void ApplyImageSetting()
    {
        if (_vm is null || this.FindControl<DataGrid>("InventoryGrid") is not { } grid) return;
        var on = _vm.ShowImages;
        if (grid.Columns.OfType<DataGridTemplateColumn>().FirstOrDefault() is { } imageColumn)
            imageColumn.IsVisible = on;
        grid.RowHeight = on ? 48 : 30;
    }

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
