using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TorVpnForWindows.Core;

/// <summary>
/// Notices when the machine's real network changes in a way that breaks Tor's connections: a
/// network becomes usable where there was none, or an address or gateway that was in use goes away,
/// even for a moment.
///
/// Tor does not notice this by itself in time. A bridge that failed while there was no network is
/// retried on Tor's own schedule, and on 14.09.2026 a session started with the Wi-Fi off made no
/// attempt at all for the minute after the Wi-Fi came up. The same morning the phone's hotspot
/// dropped twice within a minute of joining, and each drop killed whatever Tor was trying at that
/// moment. Starting the session over when the network settles is what gets it going again.
///
/// Something only being added is not reported. Joining a network brings its IPv4 address first and
/// its IPv6 gateway a few seconds later, and plugging in a cable adds a second adapter; neither
/// breaks a connection Tor already has, and starting over for each would only slow the first
/// connect down.
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

    private HashSet<string> _baseline;
    private bool _lostSomething;
    private bool _disposed;

    /// <summary>
    /// Raised once the network has settled after a change that breaks connections, off the UI thread.
    /// The first argument says whether a usable network is present now; the second whether an address
    /// or gateway that was there went away, as opposed to a network only arriving.
    /// </summary>
    public event Action<bool, bool>? Changed;

    public NetworkWatcher(Func<string> tunnelName)
    {
        _tunnelName = tunnelName;
        _baseline = Snapshot();
        _timer = new Timer(OnSettled, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityEvent;
    }

    /// <summary>Whether any real adapter has an address and a gateway to send traffic through.</summary>
    public bool HasUsableNetwork => Snapshot().Count > 0;

    private void OnAvailabilityEvent(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkEvent(sender, e);

    private void OnNetworkEvent(object? sender, EventArgs e)
    {
        try
        {
            var now = Snapshot();

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // A drop that comes back with the same address inside the settle time still broke
                // every connection Tor had open on it, so the dip itself counts.
                if (_baseline.Any(atom => !now.Contains(atom)))
                {
                    _lostSomething = true;
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
        bool lost;

        try
        {
            var now = Snapshot();

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                lost = _lostSomething || _baseline.Any(atom => !now.Contains(atom));
                var arrived = _baseline.Count == 0 && now.Count > 0;

                changed = lost || arrived;
                usable = now.Count > 0;

                if (changed)
                {
                    Log.App($"The network changed: [{Describe(_baseline)}] -> [{Describe(now)}]");
                }

                _baseline = now;
                _lostSomething = false;
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
            Changed?.Invoke(usable, lost);
        }
        catch (Exception ex)
        {
            Log.Error("A network change handler threw", ex);
        }
    }

    /// <summary>
    /// Every IPv4 address, IPv4 gateway and IPv6 gateway of the adapters that can carry traffic, one
    /// entry each, tagged with the adapter. Empty when there is no such adapter.
    /// </summary>
    private HashSet<string> Snapshot()
    {
        var tunnel = _tunnelName();
        var atoms = new HashSet<string>(StringComparer.Ordinal);

        NetworkInterface[] adapters;

        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Log.Error("Listing the network adapters failed", ex);
            return atoms;
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
                    .ToArray();

                var gateways6 = properties.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.Equals(IPAddress.IPv6Any))
                    .ToArray();

                var addresses4 = properties.UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray();

                var hasGlobal6 = properties.UnicastAddresses
                    .Select(u => u.Address)
                    .Any(a => a.AddressFamily == AddressFamily.InterNetworkV6 &&
                              !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !IPAddress.IsLoopback(a));

                // An adapter with no gateway is not carrying internet traffic: a virtual switch, a
                // cable to another machine, a Wi-Fi that has associated but not yet got an address.
                if (!(gateways4.Length > 0 && addresses4.Length > 0) && !(gateways6.Length > 0 && hasGlobal6))
                {
                    continue;
                }

                foreach (var address in addresses4)
                {
                    atoms.Add($"{nic.Name}|address|{address}");
                }

                foreach (var gateway in gateways4.Concat(gateways6))
                {
                    atoms.Add($"{nic.Name}|gateway|{gateway}");
                }
            }
            catch (NetworkInformationException)
            {
                // The adapter went away while it was being read. The event that follows covers it.
            }
        }

        return atoms;
    }

    private static string Describe(HashSet<string> atoms) => atoms.Count == 0
        ? "no network"
        : string.Join(", ", atoms.Order(StringComparer.Ordinal).Select(atom => atom.Replace('|', ' ')));

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
