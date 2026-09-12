using System.Windows;
using WinForms = System.Windows.Forms;

namespace Gci.App.Services;

/// <summary>Notification-area icon that keeps GCI refreshing while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _pauseItem;

    public TrayIcon(Action show, Action refresh, Action togglePause, Action exit)
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/gci.ico"))!.Stream;
        _pauseItem = new WinForms.ToolStripMenuItem("Pause auto-refresh", null, (_, _) => togglePause());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open GCI", null, (_, _) => show()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new WinForms.ToolStripMenuItem("Refresh now", null, (_, _) => refresh()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "GCI - Georgia Cannabis Inventory",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) show(); };
    }

    public void SetStatus(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void SetPaused(bool paused) => _pauseItem.Text = paused ? "Resume auto-refresh" : "Pause auto-refresh";

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
