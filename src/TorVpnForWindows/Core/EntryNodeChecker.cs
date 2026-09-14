using System.Net;

namespace TorVpnForWindows.Core;

/// <summary>
/// Where the connection enters the Tor network.
/// </summary>
/// <param name="Address">
/// The first hop's address and port. For a transport whose bridge address is only a placeholder,
/// such as snowflake or meek, the transport's name, since no single address stands for it.
/// </param>
/// <param name="CountryCode">The first hop's country from Tor's own GeoIP data, when it has an address.</param>
public sealed record EntryInfo(string Address, string? CountryCode);

/// <summary>
/// Asks Tor which relay or bridge its circuits start at.
///
/// This is the address the local network sees the connection going to. With bridges it is one of
/// the bridges from the settings; without them it is Tor's guard relay. It is never the address a
/// website sees, which is the exit's.
/// </summary>
public static class EntryNodeChecker
{
    /// <summary>
    /// Transports whose bridge line carries a documentation address instead of a real one. The
    /// traffic goes to a front, a broker or a volunteer, not to that address.
    /// </summary>
    private static readonly string[] PlaceholderTransports = ["meek", "meek_lite", "snowflake", "webtunnel", "conjure"];

    public static async Task<EntryInfo?> QueryAsync(
        TorControlClient control,
        IReadOnlyList<string> bridgeLines,
        CancellationToken cancellationToken)
    {
        var circuits = await control.GetInfoLinesAsync("circuit-status", cancellationToken).ConfigureAwait(false);
        var fingerprint = FirstHopOf(circuits);

        if (fingerprint is null)
        {
            return null;
        }

        var address = FromBridgeLines(fingerprint, bridgeLines);

        if (address is null)
        {
            var status = await control.GetInfoLinesAsync($"ns/id/{fingerprint}", cancellationToken).ConfigureAwait(false);
            address = FromNetworkStatus(status);
        }

        if (address is null)
        {
            // A bridge is not in the consensus, but Tor keeps the descriptor it fetched from it.
            var descriptor = await control.GetInfoLinesAsync($"desc/id/{fingerprint}", cancellationToken).ConfigureAwait(false);
            address = FromDescriptor(descriptor);
        }

        if (address is null)
        {
            Log.App($"The first hop ${fingerprint} has no address Tor could report");
            return null;
        }

        string? country = null;
        if (TryHostOf(address, out var host))
        {
            country = await control.LookupCountryAsync(host, cancellationToken).ConfigureAwait(false);
        }

        return new EntryInfo(address, country?.ToUpperInvariant());
    }

    /// <summary>
    /// The identity fingerprint of the first hop of a built general-purpose circuit, from the lines of
    /// "GETINFO circuit-status". A conflux leg counts, since it carries ordinary traffic too.
    /// </summary>
    public static string? FirstHopOf(IEnumerable<string> circuitLines)
    {
        foreach (var line in circuitLines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // <id> BUILT <path> [KEY=VALUE ...]
            if (fields.Length < 3 || fields[1] != "BUILT" || fields[2].Contains('='))
            {
                continue;
            }

            var purpose = fields.FirstOrDefault(f => f.StartsWith("PURPOSE=", StringComparison.Ordinal));
            if (purpose is not null && purpose != "PURPOSE=GENERAL" && purpose != "PURPOSE=CONFLUX_LINKED")
            {
                continue;
            }

            // $FINGERPRINT~nickname or $FINGERPRINT=nickname
            var first = fields[2].Split(',')[0].TrimStart('$');
            var end = first.IndexOfAny(['~', '=']);
            var fingerprint = end < 0 ? first : first[..end];

            if (fingerprint.Length == 40 && fingerprint.All(Uri.IsHexDigit))
            {
                return fingerprint.ToUpperInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// The address of the configured bridge line carrying this fingerprint: "IP:port", or the
    /// transport's name when the line's address is only a placeholder.
    /// </summary>
    public static string? FromBridgeLines(string fingerprint, IEnumerable<string> bridgeLines)
    {
        foreach (var line in bridgeLines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (!fields.Any(f => f.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // [transport] address:port fingerprint [arguments]
            var hasTransport = fields.Length > 1 && !fields[0].Contains(':');
            var transport = hasTransport ? fields[0] : null;
            var address = hasTransport ? fields[1] : fields[0];

            if (transport is not null && PlaceholderTransports.Contains(transport, StringComparer.OrdinalIgnoreCase))
            {
                return transport.Replace("_lite", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return address;
        }

        return null;
    }

    /// <summary>"IP:ORPort" from the "r" line of a network status entry.</summary>
    public static string? FromNetworkStatus(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            // r nickname identity digest date time IP ORPort DirPort
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 8 && fields[0] == "r" && IPAddress.TryParse(fields[6], out _))
            {
                return $"{fields[6]}:{fields[7]}";
            }
        }

        return null;
    }

    /// <summary>"IP:ORPort" from the "router" line of a server descriptor.</summary>
    public static string? FromDescriptor(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            // router nickname IP ORPort SOCKSPort DirPort
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 4 && fields[0] == "router" && IPAddress.TryParse(fields[2], out _))
            {
                return $"{fields[2]}:{fields[3]}";
            }
        }

        return null;
    }

    private static bool TryHostOf(string address, out string host)
    {
        host = string.Empty;

        // [IPv6]:port, IPv4:port, or a transport name with no address at all.
        if (address.StartsWith('['))
        {
            var close = address.IndexOf(']');
            host = close > 0 ? address[1..close] : string.Empty;
        }
        else
        {
            var colon = address.LastIndexOf(':');
            host = colon > 0 ? address[..colon] : address;
        }

        return IPAddress.TryParse(host, out _);
    }
}
