using System.Diagnostics;
using System.Windows;
using Gci.App.ViewModels;

namespace Gci.App.Views;

/// <summary>First-run consent shown before the main window. Returns true only when the user ticks the box and agrees;
/// Decline (or closing it) returns false, and the app then quits without storing anything.</summary>
public partial class DisclaimerWindow : Window
{
    public DisclaimerWindow() => InitializeComponent();

    private void OnAgreeChanged(object sender, RoutedEventArgs e) => AcceptButton.IsEnabled = AgreeCheck.IsChecked == true;

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDecline(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPrivacy(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Legal.PrivacyUrl) { UseShellExecute = true }); }
        catch (Exception) { /* ignore: the disclaimer text already covers the essentials */ }
    }
}
