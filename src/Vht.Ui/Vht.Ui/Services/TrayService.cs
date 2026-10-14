using System;
using System.Drawing;
using System.Windows.Forms;

namespace Vht.Ui.Services;

/// <summary>
/// The notification-area presence: minimize-to-tray, restore, and a status menu that stays
/// usable while the main window is closed. Uses <see cref="NotifyIcon"/> from WinForms
/// (in-box, MIT) - no third-party tray package, per docs/ARCHITECTURE.md section 2.2.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _ownedIcon;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly Func<bool> _isRunning;
    private readonly string _baseTooltip;
    private bool _disposed;
    private bool _hinted;

    public TrayService(
        string tooltip,
        Func<bool> isRunning,
        Action show,
        Action start,
        Action stop,
        Action showLog,
        Action about,
        Action exit)
    {
        _baseTooltip = tooltip;
        _isRunning = isRunning;
        _ownedIcon = Branding.CreateTrayIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open " + tooltip + " window", null, (_, _) => show()));
        menu.Items.Add(new ToolStripSeparator());

        _startItem = new ToolStripMenuItem("Start", null, (_, _) => start());
        _stopItem = new ToolStripMenuItem("Stop", null, (_, _) => stop());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Show log", null, (_, _) => showLog()));
        menu.Items.Add(new ToolStripMenuItem("About " + tooltip, null, (_, _) => about()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _ownedIcon,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => show();
        Update();
    }

    /// <summary>Refreshes tooltip and menu state from the current service state.</summary>
    public void Update()
    {
        if (_disposed)
        {
            return;
        }

        bool running = _isRunning();
        _notifyIcon.Text = Truncate(_baseTooltip + (running ? " - running" : " - stopped"), 63);
        _startItem.Enabled = !running;
        _stopItem.Enabled = running;
    }

    /// <summary>Explains, once per process, that closing the window did not exit.</summary>
    public void HintStillRunning()
    {
        if (_disposed || _hinted)
        {
            return;
        }

        _hinted = true;
        _notifyIcon.BalloonTipTitle = Branding.ProductName;
        _notifyIcon.BalloonTipText =
            "Still running in the notification area. Double-click the tray icon to reopen, " +
            "or choose Exit to close it.";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 3)] + "...";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedIcon.Dispose();
    }
}
