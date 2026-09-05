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
    ///
    /// A client whose own adapter is up is reported first: it is the one most likely to be
    /// filtering right now. The others still matter, because a kill switch is usually designed to
    /// keep blocking after its tunnel goes down, which is exactly when it is least expected.
    /// </summary>
    public static IReadOnlyList<ConflictReport> FindRunningVpnClients()
    {
        var found = new List<ConflictReport>();
        var activeAdapters = ActiveAdapterNames();

        foreach (var client in KnownClients)
        {
            try
            {
                var processes = Process.GetProcessesByName(client.ProcessName);

                foreach (var process in processes)
                {
                    process.Dispose();
                }

                if (processes.Length == 0 || found.Any(r => r.Product == client.Product))
                {
                    continue;
                }

                var adapterUp = activeAdapters.Any(name =>
                    name.Contains(client.Product.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));

                found.Add(new ConflictReport(
                    client.Product,
                    adapterUp ? $"{client.Product} (connected)" : client.Product));
            }
            catch (Exception ex)
            {
                Log.Error($"Could not check for {client.Product}", ex);
            }
        }

        // Connected first: that is the one to try turning off.
        return found
            .OrderByDescending(r => r.Explanation.EndsWith("(connected)", StringComparison.Ordinal))
            .ToList();
    }

    private static IReadOnlyList<string> ActiveAdapterNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(nic => nic.Name + " " + nic.Description)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Could not list active adapters", ex);
            return [];
        }
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
    /// Kept to one sentence plus the list, because it goes on the status screen under the state.
    /// </summary>
    public static string? DescribeLikelyCause(string ownAdapterName)
    {
        var clients = FindRunningVpnClients();

        if (clients.Count > 0)
        {
            var names = string.Join(", ", clients.Select(c => c.Explanation));
            return $"Traffic is being blocked before it leaves the machine, most likely by a kill switch in {names}. " +
                   "Turn that off, or disconnect it, and connect again.";
        }

        var adapters = FindOtherTunnelAdapters(ownAdapterName);

        if (adapters.Count > 0)
        {
            return $"Traffic is being blocked before it leaves the machine. Another tunnel is active " +
                   $"({string.Join(", ", adapters)}); if its client has a kill switch, turn that off and connect again.";
        }

        return null;
    }
}
