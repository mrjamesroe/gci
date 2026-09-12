using System.Windows;
using Gci.App.ViewModels;

namespace Gci.App.Views;

public partial class WatchEditorWindow : Window
{
    public WatchEditorWindow(WatchEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
