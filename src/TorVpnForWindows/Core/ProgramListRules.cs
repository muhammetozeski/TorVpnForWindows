using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

/// <summary>
/// Turns the entries of a program list into what the enforcing parts need: every spelling of each
/// executable, with Tor's own processes and this application taken out, because those are always
/// allowed whatever a list says.
/// </summary>
public static class ProgramListRules
{
    /// <summary>
    /// Every spelling of the entries: each as stored, as the file system resolves it now, and the
    /// short form of that. The stored path is kept even when the file is gone, so a program that is
    /// reinstalled to the same place is covered again without touching the list.
    /// </summary>
    public static IReadOnlyList<string> SpellingsOf(IEnumerable<string> entries)
    {
        var spellings = new List<string>();

        foreach (var entry in entries)
        {
            Add(spellings, entry);

            var canonical = ExecutablePaths.Canonicalize(entry);
            if (canonical is null)
            {
                continue;
            }

            foreach (var spelling in ExecutablePaths.SpellingsOf(canonical))
            {
                Add(spellings, spelling);
            }
        }

        return spellings;
    }

    /// <summary>
    /// The spellings with Tor, its transports, sing-box and this application removed. Blocking any of
    /// them, or sending them into the tunnel they provide, would take the whole connection down.
    /// </summary>
    public static IReadOnlyList<string> WithoutInfrastructure(IReadOnlyList<string> spellings, Binaries binaries)
    {
        var infrastructure = new[] { binaries.Tor.Path, binaries.Lyrebird.Path, binaries.SingBox.Path, Environment.ProcessPath }
            .Where(path => !string.IsNullOrEmpty(path))
            .SelectMany(path => SpellingsOf([path!]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = new List<string>();

        foreach (var spelling in spellings)
        {
            if (infrastructure.Contains(spelling))
            {
                Log.App($"{spelling} is part of the tunnel itself and is always allowed; its list entry is ignored");
                continue;
            }

            kept.Add(spelling);
        }

        return kept;
    }

    /// <summary>A short description of a list for the log.</summary>
    public static string Describe(string name, ProgramLists lists) => lists.Mode switch
    {
        ProgramListMode.Whitelist => $"{name}: white list, {lists.Whitelist.Count} program(s)",
        ProgramListMode.Blacklist => $"{name}: black list, {lists.Blacklist.Count} program(s)",
        _ => $"{name}: off"
    };

    private static void Add(List<string> spellings, string spelling)
    {
        if (!spellings.Contains(spelling, StringComparer.OrdinalIgnoreCase))
        {
            spellings.Add(spelling);
        }
    }
}
