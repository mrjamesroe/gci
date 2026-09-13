using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Gci.App.ViewModels;
using Gci.App.Views;

namespace Gci.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _toldAboutTray;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.ShowWatchEditor = editor => new WatchEditorWindow(editor) { Owner = this }.ShowDialog() == true;
        vm.ShowPhoneSetup = setup => new PhoneSetupWindow(setup) { Owner = this }.ShowDialog();
        vm.ShowPreorder = preorder =>
        {
            // One Preorder window, reused, so a store sign-in stays put; not owned, so it can stay open with GCI in the tray.
            if (_preorder is not null)
            {
                _preorder.Open(preorder);
                return;
            }
            _preorder = new PreorderWindow(preorder, vm.PreorderBrowserFolder);
            _preorder.Closed += (_, _) => _preorder = null;
            _preorder.Show();
        };
        vm.Confirm = message => MessageBox.Show(this, message, "GCI", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        ApplyImageSetting();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ShowImages)) ApplyImageSetting(); };
    }

    /// <summary>DataGrid columns aren't in the visual tree, so the image column is toggled here rather than bound.</summary>
    private void ApplyImageSetting()
    {
        ImageColumn.Visibility = _vm.ShowImages ? Visibility.Visible : Visibility.Collapsed;
        InventoryGrid.RowHeight = _vm.ShowImages ? 48 : 30;
    }

    /// <summary>True once the user chose Exit, so closing really quits.</summary>
    public bool AllowClose { get; set; }

    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _vm.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                HiddenToTray?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        base.OnClosing(e);
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedRow is { } row) _vm.OpenProductCommand.Execute(row);
    }

    private PreorderWindow? _preorder;

    private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedChange is { } row) _vm.OpenUrlCommand.Execute(row.Url);
    }

    private void OnStoreDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the checkbox column toggles instead of opening the menu.
        if (e.OriginalSource is FrameworkElement { DataContext: StoreRow } fe && fe.TemplatedParent is System.Windows.Controls.CheckBox) return;
        if (_vm.SelectedStoreRow is { } row) _vm.OpenStoreMenuCommand.Execute(row);
    }

    private void OnNewsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedNews is { } row) _vm.OpenPostCommand.Execute(row);
    }

    private void OnWatchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedWatch is { } row) _vm.EditWatchCommand.Execute(row);
    }
}
