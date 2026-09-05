using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TorVpnForWindows.Core;

public sealed record ConflictReport(string Product, string Explanation);

/// <summary>
/// Looks for another VPN client whose kill switch is filtering traffic at the kernel level.
///
/// Products such as ProtonVPN, Mullvad and Windscribe install Windows Filtering Platform rules that
/// block every connection not going through their own adapter, with an exemption for their own
/// executable. Those rules sit below routing, so this tunnel can own the default route, hand packets
/// to its adapter, and still have them dropped before they arrive. The symptom is a tunnel that
/// reports connected while nothing can reach anything, which is impossible to work out from the
/// application's own logs alone. Naming the other product turns that into a one-line answer.
/// </summary>
public static class ConflictDetector
{
    private sealed record KnownClient(string ProcessName, string Product);

    private static readonly KnownClient[] KnownClients =
    [
        new("ProtonVPN.Client", "ProtonVPN"),
        new("ProtonVPNService", "ProtonVPN"),
        new("mullvad-vpn", "Mullvad"),
        new("mullvad-daemon", "Mullvad"),
        new("Windscribe", "Windscribe"),
        new("WindscribeService", "Windscribe"),
        new("nordvpn-service", "NordVPN"),
        new("NordVPN", "NordVPN"),
        new("ExpressVPN", "ExpressVPN"),
        new("expressvpnd", "ExpressVPN"),
        new("Surfshark", "Surfshark"),
        new("MozillaVPN", "Mozilla VPN"),
        new("ivpn-service", "IVPN"),
        new("wireguard", "WireGuard")
    ];

    /// <summary>
    /// Returns the other VPN clients currently running. Presence alone is not a fault, so this is
    /// only used to explain a tunnel that came up but cannot pass traffic.
    /// </summary>
    public static IReadOnlyList<ConflictReport> FindRunningVpnClients()
    {
        var found = new List<ConflictReport>();

        foreach (var client in KnownClients)
        {
            try
            {
                var processes = Process.GetProcessesByName(client.ProcessName);

                foreach (var process in processes)
                {
                    process.Dispose();
                }

                if (processes.Length > 0 && found.All(r => r.Product != client.Product))
                {
                    found.Add(new ConflictReport(
                        client.Product,
                        $"{client.Product} is running. If it has a kill switch enabled, it blocks traffic on every " +
                        "adapter except its own, including this tunnel. Turn its kill switch off, or disconnect it, " +
                        "and connect again."));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not check for {client.Product}", ex);
            }
        }

        return found;
    }

    /// <summary>Tunnel adapters other than this application's, for the diagnostic message.</summary>
    public static IReadOnlyList<string> FindOtherTunnelAdapters(string ownAdapterName)
    {
        var adapters = new List<string>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.Name.Equals(ownAdapterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var looksLikeTunnel =
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase);

                if (looksLikeTunnel)
                {
                    adapters.Add($"{nic.Name} ({nic.Description})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate tunnel adapters", ex);
        }

        return adapters;
    }

    /// <summary>
    /// Builds the message shown when the tunnel is up but the first request through it failed.
    /// </summary>
    public static string? DescribeLikelyCause(string ownAdapterName)
    {
        var clients = FindRunningVpnClients();
        var adapters = FindOtherTunnelAdapters(ownAdapterName);

        if (clients.Count == 0 && adapters.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();

        foreach (var client in clients)
        {
            parts.Add(client.Explanation);
        }

        if (clients.Count == 0 && adapters.Count > 0)
        {
            parts.Add(
                $"Another tunnel adapter is active ({string.Join(", ", adapters)}). If its client has a kill switch, " +
                "it blocks traffic on every adapter except its own, including this one.");
        }

        return string.Join(" ", parts);
    }
}
