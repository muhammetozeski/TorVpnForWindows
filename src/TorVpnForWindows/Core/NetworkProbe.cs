using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TorVpnForWindows.Core;

/// <summary>
/// Reads the state of the machine's real network adapters. This has to happen before the TUN
/// adapter is created, because afterwards the TUN is the default route and the answers change.
/// </summary>
public static class NetworkProbe
{
    /// <summary>
    /// DNS servers configured on the interface that currently carries the default route.
    /// Used to give excluded applications working name resolution while the tunnel is up.
    /// </summary>
    public static IReadOnlyList<string> GetUpstreamDnsServers()
    {
        var result = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();

                // An interface with no gateway is not carrying general internet traffic.
                if (properties.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                foreach (var dns in properties.DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue; // Keep the list IPv4-only: it is only a fallback resolver.
                    }

                    if (IPAddress.IsLoopback(dns))
                    {
                        continue;
                    }

                    var text = dns.ToString();
                    if (!result.Contains(text))
                    {
                        result.Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read upstream DNS servers", ex);
        }

        if (result.Count == 0)
        {
            // Nothing usable was found; fall back to a public resolver so excluded applications
            // are not left without name resolution.
            Log.App("No upstream DNS server detected, falling back to 1.1.1.1");
            result.Add("1.1.1.1");
        }

        return result;
    }

    /// <summary>
    /// Whether the machine has IPv6 in a usable state.
    ///
    /// Giving the TUN an IPv6 address fails outright with "element not found" when IPv6 is
    /// unbound or disabled, and that failure stops the whole tunnel from starting. There is
    /// nothing to leak in that case either, since the machine cannot send IPv6 at all, so the
    /// address is simply left off.
    /// </summary>
    public static bool HasUsableIpv6()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (!nic.Supports(NetworkInterfaceComponent.IPv6))
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        continue;
                    }

                    // A link-local address alone does not prove the stack is carrying traffic, but
                    // it does prove IPv6 is bound to the adapter, which is all the TUN needs.
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not determine IPv6 availability", ex);
        }

        return false;
    }

    /// <summary>
    /// Waits for an adapter with the given name to disappear.
    ///
    /// Wintun removes the adapter when the process that created it exits, but the removal is not
    /// instant. Creating a second adapter with the same name while the previous one is still on its
    /// way out makes the address configuration fail, so a new session waits for the old one first.
    /// </summary>
    public static async Task<bool> WaitForAdapterGoneAsync(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reported = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!AdapterExists(name))
            {
                if (reported)
                {
                    Log.App($"The previous {name} adapter is gone");
                }

                return true;
            }

            if (!reported)
            {
                Log.App($"An adapter named {name} is still present from a previous session; waiting for it to go");
                reported = true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public static bool AdapterExists(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Error($"Could not check whether the adapter {name} exists", ex);
            return false;
        }
    }

    /// <summary>Name of the interface that currently owns the default route, for diagnostics.</summary>
    public static string? GetDefaultInterfaceName()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                foreach (var gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !gateway.Address.Equals(IPAddress.Any))
                    {
                        return nic.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not determine the default interface", ex);
        }

        return null;
    }

    /// <summary>Reserves a free loopback TCP port by binding and immediately releasing it.</summary>
    public static int FindFreeTcpPort(int preferred)
    {
        if (preferred > 0 && IsTcpPortFree(preferred))
        {
            return preferred;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsTcpPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            return false;
        }

        // Tor binds the DNS port on UDP with the same number, so check both families.
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException)
        {
            return false;
        }

        return true;
    }
}
