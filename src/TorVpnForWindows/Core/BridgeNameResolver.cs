using System.Text;
using System.Text.RegularExpressions;

namespace TorVpnForWindows.Core;

/// <summary>
/// Resolves the names a bridge needs before Tor starts, over HTTPS, and puts the answers where
/// Windows will find them without asking anyone.
///
/// The transports look their destinations up through the ordinary system resolver, and there is no
/// setting on them to change that. meek needs the site it hides behind, snowflake needs its broker
/// and the servers that tell it its own address. Each of those lookups is a plaintext question to
/// the network's resolver naming something that appears in the Tor Project's public bridge file.
///
/// So the answers are put in the machine's own hosts file first, under a marked block. The resolver
/// reads that before it asks anybody, the transport gets its address, and nothing about the lookup
/// reaches the network. The block is removed when the session ends, and any block left behind by a
/// crash is removed at startup, so it never accumulates.
/// </summary>
public static partial class BridgeNameResolver
{
    private const string BlockStart = "# Tor VPN for Windows - begin (removed automatically)";
    private const string BlockEnd = "# Tor VPN for Windows - end";

    private static string HostsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");

    /// <summary>
    /// Pulls every hostname a set of bridge lines will look up.
    ///
    /// Covers the argument spellings the transports actually use: a single front, a comma separated
    /// list of them, the plus separated list inside "targets", the destination url, and the helper
    /// servers snowflake is given under "ice".
    /// </summary>
    public static IReadOnlyList<string> HostnamesIn(IReadOnlyList<string> bridgeLines)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in bridgeLines)
        {
            foreach (Match match in FrontArgument().Matches(line))
            {
                foreach (var name in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    Add(names, name);
                }
            }

            foreach (Match match in TargetsArgument().Matches(line))
            {
                // url|front+front,url|front — the fronts are what gets connected to.
                foreach (var target in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pipe = target.IndexOf('|');
                    if (pipe < 0)
                    {
                        continue;
                    }

                    Add(names, HostOf(target[..pipe]));

                    foreach (var front in target[(pipe + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                    {
                        Add(names, front);
                    }
                }
            }

            foreach (Match match in UrlArgument().Matches(line))
            {
                Add(names, HostOf(match.Groups[1].Value));
            }

            foreach (Match match in IceArgument().Matches(line))
            {
                foreach (var server in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    // stun:host:port
                    var parts = server.Split(':');
                    if (parts.Length >= 2)
                    {
                        Add(names, parts[1]);
                    }
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Resolves the names over HTTPS and writes them into the hosts file. Returns how many were
    /// written; zero means nothing was changed and the transports will resolve as they did before.
    /// </summary>
    public static async Task<int> PrepareAsync(IReadOnlyList<string> bridgeLines, CancellationToken cancellationToken)
    {
        var names = HostnamesIn(bridgeLines);

        if (names.Count == 0)
        {
            return 0;
        }

        var entries = new List<(string Name, System.Net.IPAddress Address)>();

        foreach (var name in names)
        {
            var addresses = await EncryptedDns.ResolveAsync(name, cancellationToken).ConfigureAwait(false);

            if (addresses.Count == 0)
            {
                Log.App($"Could not resolve {name} over encrypted DNS; it will be looked up the usual way");
                continue;
            }

            entries.Add((name, addresses[0]));
        }

        if (entries.Count == 0)
        {
            return 0;
        }

        if (!Write(entries))
        {
            return 0;
        }

        Log.App(
            $"Resolved {entries.Count} bridge name(s) over encrypted DNS: " +
            string.Join(", ", entries.Select(e => $"{e.Name} -> {e.Address}")));

        return entries.Count;
    }

    /// <summary>Takes the block back out. Safe to call when there is none.</summary>
    public static void Clear()
    {
        try
        {
            if (!File.Exists(HostsFile))
            {
                return;
            }

            var kept = LinesWithoutBlock(File.ReadAllLines(HostsFile), out var removed);

            if (!removed)
            {
                return;
            }

            File.WriteAllLines(HostsFile, kept, new UTF8Encoding(false));
            Log.App("Removed the bridge name entries from the hosts file");
        }
        catch (Exception ex)
        {
            Log.Error("Could not clean the hosts file", ex);
        }
    }

    private static bool Write(IReadOnlyList<(string Name, System.Net.IPAddress Address)> entries)
    {
        try
        {
            var existing = File.Exists(HostsFile)
                ? LinesWithoutBlock(File.ReadAllLines(HostsFile), out _)
                : [];

            var lines = new List<string>(existing);

            // A file that does not end with a blank line would otherwise join the marker onto
            // somebody else's entry.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(BlockStart);

            foreach (var (name, address) in entries)
            {
                lines.Add($"{address} {name}");
            }

            lines.Add(BlockEnd);

            File.WriteAllLines(HostsFile, lines, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            // Security software guards this file, and the tunnel still works without the entries;
            // the lookups simply happen the ordinary way. Say so rather than failing the connection.
            Log.Error("Could not write the bridge names to the hosts file", ex);
            return false;
        }
    }

    private static List<string> LinesWithoutBlock(IReadOnlyList<string> lines, out bool removed)
    {
        var kept = new List<string>(lines.Count);
        var inside = false;
        removed = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith(BlockStart, StringComparison.OrdinalIgnoreCase))
            {
                inside = true;
                removed = true;
                continue;
            }

            if (inside)
            {
                if (line.TrimStart().StartsWith(BlockEnd, StringComparison.OrdinalIgnoreCase))
                {
                    inside = false;
                }

                continue;
            }

            kept.Add(line);
        }

        // Drop the blank line the block was separated by, so repeated runs do not grow the file.
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept;
    }

    private static void Add(HashSet<string> names, string? candidate)
    {
        var name = candidate?.Trim();

        if (string.IsNullOrEmpty(name) || System.Net.IPAddress.TryParse(name, out _))
        {
            return;
        }

        // A placeholder address like 192.0.2.20 carries no name, and neither does a bare port.
        if (!name.Contains('.') || name.Contains('/'))
        {
            return;
        }

        names.Add(name);
    }

    private static string? HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return parsed.Host;
        }

        return null;
    }

    [GeneratedRegex(@"\bfronts?=([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FrontArgument();

    [GeneratedRegex(@"\btargets=([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetsArgument();

    [GeneratedRegex(@"\burl=([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlArgument();

    [GeneratedRegex(@"\bice=([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IceArgument();
}
