using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Gci.App.ViewModels;
using Gci.Core.Models;

namespace Gci.Desktop.Views;

public partial class MainWindow : Window
{
    private static readonly IBrush OutOfStock = new SolidColorBrush(Color.Parse("#9AA39D"));

    private MainViewModel? _vm;

    /// <summary>True once the user chose Exit from the tray, so closing really quits.</summary>
    public bool AllowClose { get; set; }

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

        // The "keep running in the menu bar / start minimized" options only apply where there's a tray (not macOS).
        if (this.FindControl<Border>("WindowSettingsCard") is { } card) card.IsVisible = !OperatingSystem.IsMacOS();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // On Windows, closing keeps GCI running in the tray when the setting is on (the tray's Exit sets AllowClose).
    // On macOS there's no tray, so closing quits — nothing lingers in the Dock.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && _vm?.MinimizeToTray == true && !OperatingSystem.IsMacOS())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowImages)) ApplyImageSetting();
    }

    /// <summary>DataGrid columns aren't in the visual tree, so the image column is toggled here rather than bound.</summary>
    private void ApplyImageSetting()
    {
        if (_vm is null) return;
        var on = _vm.ShowImages;
        ToggleImages(this.FindControl<DataGrid>("InventoryGrid"), on);
        ToggleImages(this.FindControl<DataGrid>("ChangeGrid"), on);
    }

    // Column layout for both grids: [0] watch ★, [1] product image, then the data columns.
    private static void ToggleImages(DataGrid? grid, bool on)
    {
        if (grid is null) return;
        if (grid.Columns.Count > 1) grid.Columns[1].IsVisible = on;
        grid.RowHeight = on ? 48 : 30;
    }

    // Grey out-of-stock / sold-out rows (rows recycle on scroll, so clear the override for normal rows).
    private void OnInventoryRowLoading(object? sender, DataGridRowEventArgs e) =>
        SetRowDim(e.Row, e.Row.DataContext is ItemRow { InStock: false });

    private void OnChangeRowLoading(object? sender, DataGridRowEventArgs e) =>
        SetRowDim(e.Row, e.Row.DataContext is ChangeRow { Event.Kind: ChangeKind.SoldOut });

    private static void SetRowDim(DataGridRow row, bool dim)
    {
        if (dim) row.SetValue(TextElement.ForegroundProperty, OutOfStock);
        else row.ClearValue(TextElement.ForegroundProperty);
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
