using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;

namespace Gci.Desktop.Views;

public partial class WatchEditorWindow : Window
{
    public WatchEditorWindow() => InitializeComponent();

    public WatchEditorWindow(WatchEditorViewModel vm) : this() => DataContext = vm;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
