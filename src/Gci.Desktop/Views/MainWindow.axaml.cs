using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;

namespace Gci.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this() => DataContext = vm;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Double-clicking an inventory row opens the product page (the row is already the grid's SelectedItem).
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenProductCommand.CanExecute(null))
            vm.OpenProductCommand.Execute(null);
    }
}
