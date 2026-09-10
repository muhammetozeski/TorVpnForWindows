using System.Text.RegularExpressions;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

/// <summary>A domain that can stand in front of the meek server, and what it actually is.</summary>
public sealed record MeekFront(string Domain, string Description);

/// <summary>
/// The names meek can hide behind.
///
/// meek reaches its server through a content delivery network. The connection is made to some other
/// customer of that same network and carries that customer's name in the handshake, while the real
/// destination travels inside the encrypted request. Anyone watching sees a connection to an
/// ordinary site.
///
/// The list Tor publishes names exactly one such site, www.phpmyadmin.net, and that name is in a
/// public file every censor can read. A connection to it is therefore a fair guess at meek. Any
/// other customer of the same network works just as well, so several are offered and the choice is
/// the user's.
///
/// Every entry here was checked against the live meek server: a request was made to the candidate
/// with the real destination in the Host header, and the meek server's own reply came back. A name
/// that merely resolves is not enough, so nothing goes in this list without that reply.
///
/// Advertising networks are deliberately absent. Some of the busiest customers of this network are
/// advertising and adult advertising services; a connection to one of those draws more attention on
/// a corporate network than the thing it is meant to hide, which defeats the purpose.
/// </summary>
public static partial class MeekFronts
{
    /// <summary>Value that means "use all of them and let the transport shuffle".</summary>
    public const string Mixed = "mixed";

    /// <summary>Verified against the live meek server on 2026-09-10.</summary>
    public static IReadOnlyList<MeekFront> All { get; } =
    [
        new("www.phpmyadmin.net", "phpMyAdmin"),
        new("www.datapacket.com", "DataPacket"),
        new("app.datapacket.com", "DataPacket"),
        new("www.cdn77.com", "CDN77"),
        new("client.cdn77.com", "CDN77"),
        new("assets.plesk.com", "Plesk"),
        new("cdn.userway.org", "UserWay"),
        new("cdn.sendpulse.com", "SendPulse"),
        new("cdn.fluidplayer.com", "Fluid Player")
    ];

    public static bool IsKnown(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) &&
        All.Any(f => f.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites meek bridge lines to use the chosen front or fronts.
    ///
    /// The transport takes either a single url and front pair, or a "targets" argument holding a url
    /// with several fronts joined by a plus sign. Given more than one it shuffles them into a random
    /// order itself and moves to the next when one fails, so the mixed setting is simply all of them
    /// handed over at once rather than a choice made here.
    ///
    /// Lines for other transports pass through untouched.
    /// </summary>
    public static List<string> ApplyTo(IReadOnlyList<string> bridgeLines, string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return [.. bridgeLines];
        }

        List<string> fronts;

        if (selection.Equals(Mixed, StringComparison.OrdinalIgnoreCase))
        {
            fronts = All.Select(f => f.Domain).ToList();
        }
        else if (IsKnown(selection))
        {
            fronts = [selection];
        }
        else
        {
            fronts = [];
        }

        if (fronts.Count == 0)
        {
            Log.App($"Unknown meek front '{selection}'; leaving the bridge line as published");
            return [.. bridgeLines];
        }

        var rewritten = new List<string>(bridgeLines.Count);

        foreach (var line in bridgeLines)
        {
            rewritten.Add(Rewrite(line, fronts));
        }

        return rewritten;
    }

    private static string Rewrite(string line, IReadOnlyList<string> fronts)
    {
        if (!line.TrimStart().StartsWith("meek", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var url = UrlArgument().Match(line);
        if (!url.Success)
        {
            // A line already using "targets" carries its own fronts and is left alone rather than
            // guessed at.
            return line;
        }

        var withoutFrontArgs = FrontArgument().Replace(line, string.Empty);
        withoutFrontArgs = UrlArgument().Replace(withoutFrontArgs, string.Empty);

        var targets = $"targets={url.Groups[1].Value}|{string.Join('+', fronts)}";
        var parts = withoutFrontArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The transport name and address come first, then the arguments, so the new one goes after
        // the address rather than at the end where a fingerprint may already sit.
        var rebuilt = new List<string>(parts.Length + 1);
        rebuilt.AddRange(parts.Take(2));
        rebuilt.Add(targets);
        rebuilt.AddRange(parts.Skip(2));

        var result = string.Join(' ', rebuilt);

        Log.App($"meek is using {fronts.Count} front(s): {string.Join(", ", fronts)}");

        return result;
    }

    [GeneratedRegex(@"\burl=(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlArgument();

    [GeneratedRegex(@"\bfronts?=\S+", RegexOptions.IgnoreCase)]
    private static partial Regex FrontArgument();
}
