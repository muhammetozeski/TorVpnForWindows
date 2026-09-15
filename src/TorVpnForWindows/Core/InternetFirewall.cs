using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

/// <summary>
/// Enforces the internet lists like a firewall: which programs may connect to anything outside this
/// machine at all, whether Tor is connected or not, and whether or not connect has been pressed.
///
/// It is in force for as long as the application runs, in a dynamic WFP session of its own, so
/// quitting or a crash removes it with everything else. It works beside the kill switch rather than
/// through it. Windows lets a block in any sublayer win over a permit in another, so a program these
/// lists block stays blocked even where the kill switch would let it through the tunnel, which is
/// the order the lists are meant to have: whether a program may go out at all comes first, and only
/// then where its traffic goes.
///
/// Some traffic is always allowed, whatever the lists say: connections that stay on this machine,
/// Tor and everything it starts, sing-box, this application, and the address lease the network
/// adapter needs. A white list also lets the Windows name resolver ask for names, because every
/// program resolves through it; without that the listed programs could not find anything either.
/// </summary>
public sealed class InternetFirewall : IDisposable
{
    private const ushort DhcpServerPort = 67;
    private const ushort DhcpV6ServerPort = 547;
    private const ushort DnsPort = 53;

    // Inside the firewall's own sublayer. Every permit outranks the blocks.
    private const byte WeightBlock = 1;
    private const byte WeightPermit = 15;

    private readonly Lock _gate = new();

    /// <summary>
    /// Executables discovered at run time that belong to the tunnel, such as the real tor.exe behind
    /// a launcher on PATH. Kept so that rebuilding the rules for a changed list permits them again.
    /// </summary>
    private readonly HashSet<string> _runtimePermits = new(StringComparer.OrdinalIgnoreCase);

    private WfpSession? _session;
    private ProgramLists? _lists;
    private Binaries? _binaries;
    private string? _appliedConfiguration;
    private bool _disposed;

    /// <summary>
    /// Puts the lists in force, replacing whatever was in force. The new rules go in before the old
    /// ones come out. Lists that are off remove every rule. When the new rules cannot be built, the
    /// previous ones stay.
    /// </summary>
    public void Apply(ProgramLists lists, Binaries binaries)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lists = lists;
            _binaries = binaries;
            ApplyCore();
        }
    }

    /// <summary>
    /// Permits a process and everything it starts, polling briefly because a launcher starts the real
    /// program a moment after itself. Only a white list needs it, but the paths are remembered either
    /// way so switching to a white list later keeps Tor working.
    /// </summary>
    public async Task PermitProcessTreeAsync(int rootProcessId, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var path in ProcessTree.GetImagePaths(rootProcessId))
            {
                lock (_gate)
                {
                    if (_disposed || !_runtimePermits.Add(path))
                    {
                        continue;
                    }

                    if (_session is not null && _lists?.Mode == ProgramListMode.Whitelist)
                    {
                        try
                        {
                            AddPermit(_session, path, "tunnel process");
                            Log.App($"Internet lists: also permitting {Path.GetFileName(path)} ({path})");
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Internet lists: could not permit {path}", ex);
                        }
                    }
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyCore()
    {
        var lists = _lists;
        var binaries = _binaries;

        if (lists is null || binaries is null)
        {
            return;
        }

        var entries = ProgramListRules.WithoutInfrastructure(ProgramListRules.SpellingsOf(lists.ActiveEntries), binaries);
        var configuration = $"{lists.Mode}#" + string.Join("|", entries.Select(e => e.ToUpperInvariant()).Order(StringComparer.Ordinal));

        if (configuration == _appliedConfiguration)
        {
            return;
        }

        WfpSession? next = null;

        try
        {
            if (lists.Mode != ProgramListMode.Off)
            {
                next = WfpSession.Open(
                    "Tor VPN for Windows",
                    "Internet list filters, removed when the process exits.",
                    "Internet lists");

                if (lists.Mode == ProgramListMode.Blacklist)
                {
                    BuildBlacklist(next, entries);
                }
                else
                {
                    BuildWhitelist(next, entries, binaries);
                }
            }

            var previous = _session;
            _session = next;
            next = null;
            previous?.Dispose();

            _appliedConfiguration = configuration;
            Log.App(ProgramListRules.Describe("Internet lists in force", lists));
        }
        catch (Exception ex)
        {
            Log.Error("Applying the internet lists failed; the previous rules stay in force", ex);
        }
        finally
        {
            next?.Dispose();
        }
    }

    /// <summary>The listed programs cannot connect anywhere but this machine itself.</summary>
    private static void BuildBlacklist(WfpSession session, IReadOnlyList<string> entries)
    {
        foreach (var entry in entries)
        {
            if (!File.Exists(entry))
            {
                continue;
            }

            using var appId = WfpSession.AppId.For(entry)
                ?? throw new InvalidOperationException($"Could not build an application identifier for {entry}, so it cannot be blocked.");

            var name = Path.GetFileName(entry);
            session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionBlock, WeightBlock, $"block {name} (IPv4)", appId.Condition);
            session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionBlock, WeightBlock, $"block {name} (IPv6)", appId.Condition);
        }

        AddLoopbackPermits(session);
    }

    /// <summary>Only the listed programs, and the traffic that is always allowed, can connect out.</summary>
    private void BuildWhitelist(WfpSession session, IReadOnlyList<string> entries, Binaries binaries)
    {
        // The block goes in first so a failure part way through leaves the machine blocked rather
        // than open.
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionBlock, WeightBlock, "block all IPv4");
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionBlock, WeightBlock, "block all IPv6");

        AddLoopbackPermits(session);

        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermit, "permit IPv4 DHCP",
            WfpSession.Protocol(WfpSession.ProtocolUdp), WfpSession.RemotePort(DhcpServerPort));
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermit, "permit IPv6 DHCP",
            WfpSession.Protocol(WfpSession.ProtocolUdp), WfpSession.RemotePort(DhcpV6ServerPort));

        // The Windows resolver asks on behalf of every program. Only its queries are let out, and
        // only to the name server port.
        var svchost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
        using (var resolver = WfpSession.AppId.For(svchost))
        {
            if (resolver is not null)
            {
                session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermit,
                    "permit the Windows resolver (IPv4)", resolver.Condition, WfpSession.RemotePort(DnsPort));
                session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermit,
                    "permit the Windows resolver (IPv6)", resolver.Condition, WfpSession.RemotePort(DnsPort));
            }
        }

        var always = new[] { binaries.Tor.Path, binaries.Lyrebird.Path, binaries.SingBox.Path, Environment.ProcessPath }
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .Concat(_runtimePermits);

        foreach (var path in ProgramListRules.SpellingsOf(always))
        {
            AddPermit(session, path, "tunnel process");
        }

        foreach (var entry in entries)
        {
            AddPermit(session, entry, "white list");
        }
    }

    private static void AddLoopbackPermits(WfpSession session)
    {
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermit, "permit IPv4 loopback", WfpSession.Loopback());
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermit, "permit IPv6 loopback", WfpSession.Loopback());
    }

    private static void AddPermit(WfpSession session, string path, string reason)
    {
        using var appId = WfpSession.AppId.For(path);
        if (appId is null)
        {
            return;
        }

        var name = Path.GetFileName(path);
        session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionPermit, WeightPermit, $"permit {name}, {reason} (IPv4)", appId.Condition);
        session.AddFilter(WfpSession.LayerAleAuthConnectV6, WfpSession.ActionPermit, WeightPermit, $"permit {name}, {reason} (IPv6)", appId.Condition);
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

            var session = _session;
            _session = null;

            if (session is not null)
            {
                session.Dispose();
                Log.App("Internet lists removed");
            }
        }
    }
}
