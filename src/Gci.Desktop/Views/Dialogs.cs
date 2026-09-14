using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Gci.Desktop.Views;

/// <summary>Small modal dialogs Avalonia doesn't ship a built-in for (WPF's MessageBox has no direct equivalent).</summary>
public static class Dialogs
{
    /// <summary>A yes/no confirmation. Returns true when the user chooses Yes.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message)
    {
        var result = false;

        var yes = new Button { Content = "Yes", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = "No", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        yes.Classes.Add("primary");

        var dialog = new Window
        {
            Title = "GCI",
            SizeToContent = SizeToContent.Height,
            Width = 420,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes },
                    },
                },
            },
        };

        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }
}
