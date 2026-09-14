using System.Net;

namespace TorVpnForWindows.Core;

/// <summary>
/// Blocks all outbound traffic at the Windows Filtering Platform, permitting only what the tunnel
/// needs, so nothing can leave the machine unless it goes through Tor.
///
/// Routing alone is not enough for a kill switch. A program that binds to a specific adapter, or
/// anything that installs a more specific route, travels around the tunnel without ever consulting
/// the default route. These filters sit at the connect layer, below routing, so that cannot happen.
///
/// The filters live in a dynamic <see cref="WfpSession"/>: if this process dies for any reason,
/// Windows removes every filter it added. A crash therefore restores the machine's networking rather
/// than leaving it cut off.
/// </summary>
public sealed class KillSwitchGuard : IDisposable
{
    private const ushort DhcpServerPort = 67;
    private const ushort DhcpV6ServerPort = 547;
    private const ushort HttpsPort = 443;

    // Weights inside our own sublayer. The block sits at the bottom; every permit outranks it.
    private const byte WeightBlock = 1;
    private const byte WeightPermitDhcp = 6;
    private const byte WeightPermitEncryptedDns = 7;
    private const byte WeightPermitLoopback = 8;
    private const byte WeightPermitTunnel = 9;
    private const byte WeightPermitApp = 12;

    private readonly List<ulong> _tunnelFilterIds = [];
    private readonly Lock _gate = new();

    private WfpSession? _session;
    private bool _disposed;

    /// <summary>What the filters in place were built from, so a changed list rebuilds them.</summary>
    private string? _armedConfiguration;

    /// <summary>True while everything except the permitted traffic is blocked.</summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>
    /// Starts blocking. Traffic from the given executables is permitted so Tor itself can reach the
    /// network and build the circuits the rest of the machine is waiting for.
    ///
    /// Arming again with a different configuration rebuilds the filters. The new set goes in before
    /// the old one comes out, so a changed list never opens a gap.
    /// </summary>
    /// <param name="blockOnly">
    /// Null blocks everything that is not permitted. A list blocks only those executables instead:
    /// that is the tunnel white list, where only the listed programs belong to Tor and every other
    /// program leaves through the normal connection whether Tor is up or not.
    /// </param>
    public bool Arm(IReadOnlyList<string> permittedExecutables, IReadOnlyList<string>? blockOnly = null)
    {
        lock (_gate)
        {
            var configuration = DescribeConfiguration(permittedExecutables, blockOnly);
            var wasArmed = _session is not null;

            if (wasArmed && configuration == _armedConfiguration)
            {
                return true;
            }

            WfpSession? next = null;

            try
            {
                next = WfpSession.Open(
                    "Tor VPN for Windows",
                    "Kill switch filters, removed when the process exits.",
                    "Kill switch");

                // Order does not matter to WFP, only weight, but the block goes in first so a
                // failure part way through leaves the machine blocked rather than half open.
                if (blockOnly is null)
                {
                    next.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionBlock, WeightBlock, "block all IPv4");
                    next.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionBlock, WeightBlock, "block all IPv6");
                }
                else
                {
                    foreach (var executable in blockOnly)
                    {
                        AddApplicationBlock(next, executable);
                    }
                }

                next.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermitLoopback,
                    "permit IPv4 loopback", WfpSession.Loopback());
                next.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermitLoopback,
                    "permit IPv6 loopback", WfpSession.Loopback());

                AddDhcpPermits(next);

                var self = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(self))
                {
                    foreach (var resolver in EncryptedDns.ResolverAddresses)
                    {
                        AddEncryptedDnsPermit(next, self, resolver);
                    }
                }

                foreach (var executable in permittedExecutables)
                {
                    AddApplicationPermit(next, executable);
                }

                // Only now does the previous set go. Closing a dynamic session removes its filters.
                var previous = _session;
                _session = next;
                next = null;
                previous?.Dispose();

                _tunnelFilterIds.Clear();
                _armedConfiguration = configuration;

                Log.App(blockOnly is null
                    ? $"Kill switch {(wasArmed ? "rebuilt" : "armed")}; {permittedExecutables.Count} executable(s) permitted"
                    : $"Kill switch {(wasArmed ? "rebuilt" : "armed")} for the tunnel white list; " +
                      $"{blockOnly.Count} executable(s) kept to the tunnel, {permittedExecutables.Count} permitted");

                return true;
            }
            catch (Exception ex)
            {
                // A previous set that was blocking stays exactly as it was: failing to change the
                // lists must not take the protection down.
                Log.Error("Arming the kill switch failed", ex);
                return wasArmed;
            }
            finally
            {
                // Only set when the new set did not make it into place.
                next?.Dispose();
            }
        }
    }

    /// <summary>
    /// Opens the block for the tunnel adapter once it exists. Called when the tunnel comes up, and
    /// reversed by <see cref="CloseTunnel"/> when it goes down, which is what makes the block bite
    /// again the moment Tor stops.
    /// </summary>
    public bool OpenTunnel(int interfaceIndex)
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return false;
            }

            try
            {
                CloseTunnelCore();

                _tunnelFilterIds.Add(_session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit,
                    WeightPermitTunnel, "permit IPv4 on the tunnel", WfpSession.InterfaceIndex(interfaceIndex)));
                _tunnelFilterIds.Add(_session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit,
                    WeightPermitTunnel, "permit IPv6 on the tunnel", WfpSession.InterfaceIndex(interfaceIndex)));

                Log.App($"Kill switch: traffic allowed on interface {interfaceIndex}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Permitting the tunnel adapter failed", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Adds permits for a process and everything it started, polling briefly because a launcher
    /// starts the real program a moment after it starts itself.
    ///
    /// Without this a shim on PATH is permitted while the executable it launches is not, and Tor
    /// sits at zero per cent forever with no indication why.
    /// </summary>
    public async Task PermitProcessTreeAsync(int rootProcessId, TimeSpan window, CancellationToken cancellationToken)
    {
        if (!IsArmed)
        {
            return;
        }

        var permitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var path in ProcessTree.GetImagePaths(rootProcessId))
            {
                if (!permitted.Add(path))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_session is null)
                    {
                        return;
                    }

                    try
                    {
                        AddApplicationPermit(_session, path);
                        Log.App($"Kill switch: also permitting {Path.GetFileName(path)} ({path})");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not permit {path}", ex);
                    }
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Revokes the tunnel permit, so only Tor's own processes can still reach the network.</summary>
    public void CloseTunnel()
    {
        lock (_gate)
        {
            try
            {
                if (CloseTunnelCore())
                {
                    Log.App("Kill switch: the tunnel permit was revoked, traffic is blocked again");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Revoking the tunnel permit failed", ex);
            }
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            try
            {
                DisarmCore();
            }
            catch (Exception ex)
            {
                Log.Error("Disarming the kill switch failed", ex);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    private static string DescribeConfiguration(IReadOnlyList<string> permitted, IReadOnlyList<string>? blockOnly) =>
        string.Join("|", permitted.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal)) + "#" +
        (blockOnly is null
            ? "all"
            : string.Join("|", blockOnly.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal)));

    /// <summary>
    /// Lets the machine renew its address lease while everything else stays blocked.
    ///
    /// The DHCP client runs inside svchost.exe, which is not on the permit list, so the block caught
    /// its renewal. A lease is not forever: a phone hotspot hands out an hour. The renewal at the
    /// half hour was dropped, the lease then expired, Windows released the address, the default
    /// route went with it and every connection failed with "no route to internet" while the adapter
    /// still showed as connected. Closing the application fixed it, which is what made it look like
    /// the tunnel had died rather than the address.
    ///
    /// The permit is written as narrowly as the layer allows: outbound UDP to the DHCP server port
    /// and nothing else. It does not name svchost.exe, because that would open every other thing
    /// that process does, the resolver included.
    /// </summary>
    private static void AddDhcpPermits(WfpSession session)
    {
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermitDhcp, "permit IPv4 DHCP",
            WfpSession.Protocol(WfpSession.ProtocolUdp), WfpSession.RemotePort(DhcpServerPort));
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermitDhcp, "permit IPv6 DHCP",
            WfpSession.Protocol(WfpSession.ProtocolUdp), WfpSession.RemotePort(DhcpV6ServerPort));
    }

    /// <summary>
    /// Lets one named program reach one numeric address on the HTTPS port, and nothing else.
    ///
    /// This is what carries the encrypted name lookups a bridge needs before Tor exists. It is
    /// written with all three conditions on purpose: naming the program alone would let it reach
    /// anything, and naming the address alone would let anything on the machine reach it.
    /// </summary>
    private static void AddEncryptedDnsPermit(WfpSession session, string executablePath, string resolverAddress)
    {
        if (!IPAddress.TryParse(resolverAddress, out var parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Log.App($"Kill switch: '{resolverAddress}' is not an IPv4 address, no permit added");
            return;
        }

        using var appId = WfpSession.AppId.For(executablePath);
        if (appId is null)
        {
            return;
        }

        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermitEncryptedDns,
            $"permit encrypted DNS to {resolverAddress}",
            appId.Condition, WfpSession.RemoteAddressV4(parsed), WfpSession.RemotePort(HttpsPort));
    }

    /// <summary>
    /// Blocks one executable everywhere except where a heavier permit applies: loopback, and the
    /// tunnel adapter once it is open. Used for the tunnel white list.
    /// </summary>
    private static void AddApplicationBlock(WfpSession session, string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            // Nothing can run from a path with no file behind it; the next arm picks it up if the
            // program comes back.
            return;
        }

        using var appId = WfpSession.AppId.For(executablePath)
            ?? throw new InvalidOperationException(
                $"Could not build an application identifier for {executablePath}, so it cannot be kept to the tunnel.");

        var name = Path.GetFileName(executablePath);
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionBlock, WeightBlock,
            $"keep {name} to the tunnel (IPv4)", appId.Condition);
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionBlock, WeightBlock,
            $"keep {name} to the tunnel (IPv6)", appId.Condition);
    }

    private static void AddApplicationPermit(WfpSession session, string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            Log.App($"Kill switch: not permitting {executablePath}, the file does not exist");
            return;
        }

        using var appId = WfpSession.AppId.For(executablePath);
        if (appId is null)
        {
            return;
        }

        var name = Path.GetFileName(executablePath);
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermitApp,
            $"permit IPv4 for {name}", appId.Condition);
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermitApp,
            $"permit IPv6 for {name}", appId.Condition);
    }

    private bool CloseTunnelCore()
    {
        if (_tunnelFilterIds.Count == 0 || _session is null)
        {
            return false;
        }

        foreach (var id in _tunnelFilterIds)
        {
            _session.DeleteFilter(id);
        }

        _tunnelFilterIds.Clear();
        return true;
    }

    private void DisarmCore()
    {
        _tunnelFilterIds.Clear();
        _armedConfiguration = null;

        var session = _session;
        _session = null;

        if (session is null)
        {
            return;
        }

        // Closing a dynamic session removes every filter and the sublayer with it.
        session.Dispose();
        Log.App("Kill switch disarmed; normal networking restored");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disarm();
        GC.SuppressFinalize(this);
    }

    ~KillSwitchGuard() => Dispose();

    /// <summary>
    /// Executables that must keep working while the block is on: Tor and its transports, and every
    /// spelling of the programs the tunnel black list sends around the tunnel. Leaving those out
    /// would block the very programs that were meant to bypass it.
    /// </summary>
    public static List<string> BuildPermitList(Binaries binaries, IReadOnlyList<string> tunnelBypassPaths)
    {
        // This application is deliberately not here.
        //
        // It used to be, from when it fetched the bridge list from the Tor Project before Tor
        // existed. That fetch now happens through Tor, and everything else it does is either
        // loopback, which the loopback rule already covers, or goes over the tunnel adapter, which
        // the tunnel rule covers once it is up. Leaving the permit in place would keep open a hole
        // through which the application could reach the network directly, and a hole that nothing
        // uses is a hole waiting for the next feature to walk through it.
        var list = new List<string> { binaries.Tor.Path, binaries.Lyrebird.Path, binaries.SingBox.Path };

        // Paths, not names: the permit applies to that file and nothing else, whether or not the
        // program is running yet.
        list.AddRange(tunnelBypassPaths);

        return list.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
