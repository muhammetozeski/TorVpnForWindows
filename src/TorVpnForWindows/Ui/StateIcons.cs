using System.Drawing;
using System.Reflection;
using System.Windows.Media.Imaging;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Ui;

/// <summary>
/// The icon for each connection state, so a glance at the notification area says whether traffic is
/// going through Tor without opening the window.
///
///   not connected : a spring onion, still all leaves and no bulb
///   connecting    : a half grown onion with its stalk still on
///   connected     : a full onion, no stalk
/// </summary>
public static class StateIcons
{
    private static readonly Dictionary<VpnState, Icon> IconCache = [];
    private static readonly Dictionary<VpnState, BitmapImage> ImageCache = [];
    private static readonly Lock Gate = new();

    /// <summary>Which artwork a state uses. Several states share one.</summary>
    private static string ResourceFor(VpnState state) => state switch
    {
        VpnState.Connected => "TorVpnForWindows.icon-connected.ico",
        VpnState.Preparing or VpnState.Bootstrapping or VpnState.EstablishingTunnel or VpnState.Disconnecting
            => "TorVpnForWindows.icon-connecting.ico",
        _ => "TorVpnForWindows.icon-disconnected.ico"
    };

    /// <summary>Notification area icon. Falls back to the shield if the resource cannot be read.</summary>
    public static Icon ForTray(VpnState state)
    {
        lock (Gate)
        {
            if (IconCache.TryGetValue(state, out var cached))
            {
                return cached;
            }

            var name = ResourceFor(state);

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The embedded icon {name} is missing.");

                // The notification area asks for a small size; letting the Icon constructor pick it
                // from the multi-size file keeps it sharp instead of scaling the large one down.
                var icon = new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
                IconCache[state] = icon;
                return icon;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not load the icon for {state}", ex);
                return SystemIcons.Shield;
            }
        }
    }

    /// <summary>Window icon, used for the title bar and the task bar.</summary>
    public static BitmapImage? ForWindow(VpnState state)
    {
        lock (Gate)
        {
            if (ImageCache.TryGetValue(state, out var cached))
            {
                return cached;
            }

            var name = ResourceFor(state);

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The embedded icon {name} is missing.");

                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                ImageCache[state] = image;
                return image;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not load the window icon for {state}", ex);
                return null;
            }
        }
    }
}
