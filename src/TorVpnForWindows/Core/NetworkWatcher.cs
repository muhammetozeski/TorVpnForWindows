using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TorVpnForWindows.Core;

/// <summary>
/// Notices when the machine's real network changes: an adapter gets an address and a gateway, loses
/// them, or gets different ones.
///
/// Tor does not notice this by itself in time. A bridge that failed while there was no network is
/// retried on Tor's own schedule, and on 14.09.2026 a session started with the Wi-Fi off made no
/// attempt at all for the minute after the Wi-Fi came up. The same morning the phone's hotspot
/// dropped twice within a minute of joining, and each drop killed whatever Tor was trying at that
/// moment. Starting the session over when the network settles is what gets it going again.
///
/// The tunnel adapter is left out of the picture, so bringing the tunnel up or down does not look
/// like a network change. So are IPv6 addresses, which Windows rotates on its own schedule; their
/// gateways are enough to see an IPv6 network arrive or leave.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    /// <summary>
    /// How long the network has to stay quiet before a change is reported. Joining a Wi-Fi network
    /// produces a burst of address events over a second or two; acting on the first one would start
    /// the session over while the address is still being configured.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    private readonly Func<string> _tunnelName;
    private readonly Lock _gate = new();
    private readonly Timer _timer;

    private string _baseline;
    private bool _disturbed;
    private bool _disposed;

    /// <summary>
    /// Raised once the network has settled after a change, off the UI thread. The argument says
    /// whether a usable network is present now.
    /// </summary>
    public event Action<bool>? Changed;

    public NetworkWatcher(Func<string> tunnelName)
    {
        _tunnelName = tunnelName;
        _baseline = Fingerprint();
        _timer = new Timer(OnSettled, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityEvent;
    }

    /// <summary>Whether any real adapter has an address and a gateway to send traffic through.</summary>
    public bool HasUsableNetwork => Fingerprint().Length > 0;

    private void OnAvailabilityEvent(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkEvent(sender, e);

    private void OnNetworkEvent(object? sender, EventArgs e)
    {
        try
        {
            var now = Fingerprint();

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // A drop that comes back with the same address inside the settle time still broke
                // every connection Tor had open on it, so the dip itself counts as a change.
                if (!string.Equals(now, _baseline, StringComparison.Ordinal))
                {
                    _disturbed = true;
                }

                _timer.Change(SettleTime, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Handling a network change event failed", ex);
        }
    }

    private void OnSettled(object? state)
    {
        bool changed;
        bool usable;

        try
        {
            var now = Fingerprint();

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                changed = _disturbed || !string.Equals(now, _baseline, StringComparison.Ordinal);
                usable = now.Length > 0;

                if (changed)
                {
                    Log.App($"The network changed: [{Describe(_baseline)}] -> [{Describe(now)}]");
                }

                _baseline = now;
                _disturbed = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading the network after a change failed", ex);
            return;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            Changed?.Invoke(usable);
        }
        catch (Exception ex)
        {
            Log.Error("A network change handler threw", ex);
        }
    }

    /// <summary>
    /// One line per adapter that can carry traffic: its identity, its IPv4 addresses and its
    /// gateways. Empty when there is no such adapter.
    /// </summary>
    private string Fingerprint()
    {
        var tunnel = _tunnelName();
        var parts = new List<string>();

        NetworkInterface[] adapters;

        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Log.Error("Listing the network adapters failed", ex);
            return string.Empty;
        }

        foreach (var nic in adapters)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    nic.Name.Equals(tunnel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var properties = nic.GetIPProperties();

                var gateways4 = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                    .Select(a => a.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                var gateways6 = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.Equals(IPAddress.IPv6Any))
                    .Select(a => a.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                var addresses4 = properties.UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                var hasGlobal6 = properties.UnicastAddresses
                    .Select(u => u.Address)
                    .Any(a => a.AddressFamily == AddressFamily.InterNetworkV6 &&
                              !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !IPAddress.IsLoopback(a));

                var usable4 = gateways4.Length > 0 && addresses4.Length > 0;
                var usable6 = gateways6.Length > 0 && hasGlobal6;

                // An adapter with no gateway is not carrying internet traffic: a virtual switch, a
                // cable to another machine, a Wi-Fi that has associated but not yet got an address.
                if (!usable4 && !usable6)
                {
                    continue;
                }

                parts.Add(
                    $"{nic.Name}|{nic.Id}|{string.Join(",", addresses4)}|{string.Join(",", gateways4)}|{string.Join(",", gateways6)}");
            }
            catch (NetworkInformationException)
            {
                // The adapter went away while it was being read. The event that follows covers it.
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    /// <summary>The fingerprint without the adapter identifiers, which only clutter the log.</summary>
    private static string Describe(string fingerprint)
    {
        if (fingerprint.Length == 0)
        {
            return "no network";
        }

        return string.Join("; ", fingerprint.Split(';').Select(part =>
        {
            var fields = part.Split('|');
            return fields.Length >= 5
                ? $"{fields[0]} {fields[2]} via {fields[3]}{(fields[4].Length > 0 ? $" / {fields[4]}" : string.Empty)}"
                : part;
        }));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityEvent;
        _timer.Dispose();
    }
}
