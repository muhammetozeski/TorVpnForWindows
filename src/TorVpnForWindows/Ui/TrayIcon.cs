using System.Drawing;
using System.Windows.Forms;
using TorVpnForWindows.Core;
using TorVpnForWindows.Localization;

namespace TorVpnForWindows.Ui;

/// <summary>
/// The notification area icon and its menu. Kept separate from the window so closing to the tray
/// does not have to keep any window state alive.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _connectItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly ToolStripMenuItem _exitItem;

    public event Action? ShowRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _showItem = new ToolStripMenuItem(Strings.TrayShow);
        _showItem.Click += (_, _) => ShowRequested?.Invoke();

        _connectItem = new ToolStripMenuItem(Strings.TrayConnect);
        _connectItem.Click += (_, _) => ConnectRequested?.Invoke();

        _disconnectItem = new ToolStripMenuItem(Strings.TrayDisconnect);
        _disconnectItem.Click += (_, _) => DisconnectRequested?.Invoke();

        _exitItem = new ToolStripMenuItem(Strings.TrayExit);
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_connectItem);
        menu.Items.Add(_disconnectItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            Icon = StateIcons.ForTray(VpnState.Disconnected),
            Text = Strings.TrayTipDisconnected,
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void ApplyStrings()
    {
        _showItem.Text = Strings.TrayShow;
        _connectItem.Text = Strings.TrayConnect;
        _disconnectItem.Text = Strings.TrayDisconnect;
        _exitItem.Text = Strings.TrayExit;
    }

    public void Update(bool connected, VpnState state)
    {
        // The tooltip is capped at 63 characters by the shell, so keep it to the essentials.
        var text = connected ? Strings.TrayTipConnected : Strings.TrayTipDisconnected;
        _icon.Text = text.Length > 62 ? text[..62] : text;

        try
        {
            _icon.Icon = StateIcons.ForTray(state);
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the notification area icon", ex);
        }

        _connectItem.Enabled = state is VpnState.Disconnected or VpnState.Failed or VpnState.Interrupted;
        _disconnectItem.Enabled = state is not (VpnState.Disconnected or VpnState.Disconnecting);
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Disposing the notification area icon failed", ex);
        }
    }
}
