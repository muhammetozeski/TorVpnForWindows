using System.Text.Json;
using System.Text.Json.Nodes;
using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

/// <summary>
/// Produces the sing-box configuration that turns the machine's default route into the Tor tunnel.
///
/// The shape of it:
///   * A TUN interface takes over the default route for IPv4 and IPv6.
///   * Every TCP connection is handed to Tor's SOCKS port. When sniffing recovers the hostname it
///     is passed to Tor as a name, so Tor resolves it at the exit and nothing leaks locally.
///   * DNS is hijacked into sing-box's resolver, which forwards to Tor's DNSPort.
///   * UDP has nowhere to go, because Tor carries TCP only, so it is refused rather than leaked.
///   * Tor's own process, and anything the user excluded, is sent straight out of the physical
///     adapter; without that, tor.exe would route its traffic into the tunnel it is providing.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunTag = "tun-in";
    public const string TorOutboundTag = "tor-out";
    public const string DirectOutboundTag = "direct-out";
    public const string TorDnsTag = "tor-dns";
    public const string DirectDnsTag = "direct-dns";

    public const string TunIpv4 = "172.19.0.1/30";
    public const string TunIpv6 = "fdfe:dcba:9876::1/126";

    /// <summary>
    /// Processes whose traffic must never enter the tunnel they are creating. The resolved binary
    /// names are added to this at build time, so a differently named build picked up from PATH is
    /// covered as well.
    /// </summary>
    private static readonly string[] BaseInfrastructureProcesses =
    [
        "tor.exe",
        "lyrebird.exe",
        "conjure-client.exe",
        "sing-box.exe"
    ];

    private static readonly string[] PrivateRangesV4 =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
        "224.0.0.0/4",
        "255.255.255.255/32"
    ];

    private static readonly string[] PrivateRangesV6 =
    [
        "fc00::/7",
        "fe80::/10"
    ];

    public static string Build(
        AppSettings settings,
        SessionEndpoints endpoints,
        IReadOnlyList<string> excludedProcesses,
        IReadOnlyList<string> upstreamDnsServers,
        IReadOnlyList<string>? resolvedBinaryNames = null,
        bool forceIpv4Only = false)
    {
        var infrastructure = BaseInfrastructureProcesses
            .Concat(resolvedBinaryNames ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = false
            },
            ["dns"] = BuildDns(endpoints, infrastructure, excludedProcesses, upstreamDnsServers),
            ["inbounds"] = new JsonArray(BuildTun(settings, forceIpv4Only)),
            ["outbounds"] = new JsonArray(
                BuildTorOutbound(endpoints),
                new JsonObject
                {
                    ["type"] = "direct",
                    ["tag"] = DirectOutboundTag
                }),
            ["route"] = BuildRoute(settings, infrastructure, excludedProcesses)
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildDns(
        SessionEndpoints endpoints,
        IReadOnlyList<string> infrastructureProcesses,
        IReadOnlyList<string> excludedProcesses,
        IReadOnlyList<string> upstreamDnsServers)
    {
        var servers = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = TorDnsTag,
                ["server"] = "127.0.0.1",
                ["server_port"] = endpoints.DnsPort
            }
        };

        // Name resolution for everything that bypasses the tunnel. Under strict routing the WFP
        // filters stop those processes from reaching port 53 themselves, so their queries still
        // arrive here and are answered from the machine's normal resolver through the physical
        // adapter.
        //
        // No detour is set. Since sing-box 1.12 a DNS server carries its own dialer, and pointing
        // one at a plain direct outbound is rejected as meaningless. The dialer picks up
        // route.auto_detect_interface, which is what keeps these queries on the physical adapter
        // instead of looping back into the tunnel.
        var upstream = upstreamDnsServers.Count > 0 ? upstreamDnsServers[0] : "1.1.1.1";
        servers.Add(new JsonObject
        {
            ["type"] = "udp",
            ["tag"] = DirectDnsTag,
            ["server"] = upstream
        });

        var bypassProcesses = infrastructureProcesses.Concat(excludedProcesses)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rules = new JsonArray
        {
            new JsonObject
            {
                ["process_name"] = ToJsonArray(bypassProcesses),
                ["action"] = "route",
                ["server"] = DirectDnsTag
            }
        };

        return new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = TorDnsTag,

            // Tor's exit support for IPv6 is uneven, and a half-working AAAA answer is worse than
            // none: the connection would be handed to an exit that cannot complete it. Answering
            // A records only keeps applications on the path that works. IPv6 is still routed into
            // the tunnel, so a literal IPv6 destination cannot escape around it.
            ["strategy"] = "ipv4_only"
        };
    }

    private static JsonObject BuildTun(AppSettings settings, bool forceIpv4Only)
    {
        var addresses = new JsonArray(TunIpv4);

        if (forceIpv4Only)
        {
            Log.App("Building an IPv4-only tunnel after the IPv6 address was refused");
        }
        else if (NetworkProbe.HasUsableIpv6())
        {
            // Routing IPv6 into the tunnel is what stops it escaping around the IPv4 routes.
            addresses.Add(TunIpv6);
        }
        else
        {
            Log.App("IPv6 is not bound on this machine; the tunnel is configured for IPv4 only");
        }

        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = TunTag,
            ["interface_name"] = settings.TunInterfaceName,
            ["address"] = addresses,
            ["mtu"] = settings.Mtu,
            ["auto_route"] = true,
            ["strict_route"] = settings.StrictRoute,
            ["stack"] = "system"
        };

        if (settings.AllowLan)
        {
            // Keep local network traffic off the tunnel entirely rather than pulling it in and
            // sending it back out, so printers, NAS boxes and the router stay reachable.
            var excluded = new JsonArray();
            foreach (var range in PrivateRangesV4.Concat(PrivateRangesV6))
            {
                excluded.Add(range);
            }

            tun["route_exclude_address"] = excluded;
        }

        return tun;
    }

    private static JsonObject BuildTorOutbound(SessionEndpoints endpoints) => new()
    {
        ["type"] = "socks",
        ["tag"] = TorOutboundTag,
        ["server"] = "127.0.0.1",
        ["server_port"] = endpoints.SocksPort,
        ["version"] = "5",

        // Tor's SOCKS port carries TCP only. Declaring that here makes sing-box fail the
        // connection immediately instead of holding a UDP association that can never work.
        ["network"] = "tcp"
    };

    private static JsonObject BuildRoute(
        AppSettings settings,
        IReadOnlyList<string> infrastructureProcesses,
        IReadOnlyList<string> excludedProcesses)
    {
        var rules = new JsonArray
        {
            // Recover the hostname from the first packet. Tor then resolves it at the exit, which
            // is both faster and less revealing than resolving it here first.
            new JsonObject { ["action"] = "sniff" },

            // Everything that looks like DNS goes to the resolver above, including queries an
            // application sends to a hardcoded server such as 8.8.8.8.
            new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            },

            // Tor and its transports must reach the internet directly. auto_detect_interface binds
            // this outbound to the physical adapter, which is what breaks the loop.
            new JsonObject
            {
                ["process_name"] = ToJsonArray(infrastructureProcesses),
                ["action"] = "route",
                ["outbound"] = DirectOutboundTag
            }
        };

        if (excludedProcesses.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["process_name"] = ToJsonArray(excludedProcesses),
                ["action"] = "route",
                ["outbound"] = DirectOutboundTag
            });
        }

        // Addresses Tor handed out for .onion names. This has to win over the local network rule
        // below, otherwise onion traffic would be sent to the physical adapter and dropped.
        rules.Add(new JsonObject
        {
            ["ip_cidr"] = new JsonArray(SessionEndpoints.OnionMapRange),
            ["action"] = "route",
            ["outbound"] = TorOutboundTag
        });

        if (settings.AllowLan)
        {
            rules.Add(new JsonObject
            {
                ["ip_is_private"] = true,
                ["action"] = "route",
                ["outbound"] = DirectOutboundTag
            });
        }

        // Tor cannot carry UDP. Refusing it with an ICMP unreachable makes applications fall back
        // to TCP straight away; dropping it silently would leave them waiting for a timeout.
        // no_drop keeps that behaviour instead of degrading to silent drops under load, which
        // matters for QUIC-heavy browsing where the fallback fires constantly.
        rules.Add(new JsonObject
        {
            ["network"] = "udp",
            ["action"] = "reject",
            ["method"] = "default",
            ["no_drop"] = true
        });

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = TorOutboundTag,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = DirectDnsTag,

            // Process matching is what keeps Tor out of its own tunnel, so it must stay on.
            ["find_process"] = true
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
