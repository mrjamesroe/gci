using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Gci.App.ViewModels;

namespace Gci.Desktop.Views;

/// <summary>First-run consent shown before the main window. <see cref="Accepted"/> is true only when the user ticks
/// the box and agrees; Decline (or closing it) leaves it false, and the app then quits without storing anything.</summary>
public partial class DisclaimerWindow : Window
{
    public bool Accepted { get; private set; }

    public DisclaimerWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAgreeChanged(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Button>("AcceptButton") is { } accept && this.FindControl<CheckBox>("AgreeCheck") is { } agree)
            accept.IsEnabled = agree.IsChecked == true;
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnDecline(object? sender, RoutedEventArgs e) => Close();

    private void OnPrivacy(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Legal.PrivacyUrl) { UseShellExecute = true }); }
        catch (Exception) { /* ignore: the disclaimer text already covers the essentials */ }
    }
}
