using System.Net;
using System.Text.Json;

namespace TorVpnForWindows.Core;

/// <summary>
/// The address traffic left from.
///
/// <paramref name="ConfirmedTor"/> is only meaningful when <paramref name="TorStatusReported"/> is
/// true. The Tor Project's endpoint checks the address against its own list of exit relays and
/// answers directly; the plain address services used as fallbacks cannot say anything about it, and
/// showing their silence as "not confirmed" would read as a warning where there is none.
/// </summary>
public sealed record ExitInfo(string IpAddress, string? CountryCode, bool ConfirmedTor, bool TorStatusReported);

/// <summary>
/// Asks the Tor Project's own endpoint which address the traffic came out of. Going through the
/// SOCKS port rather than the TUN means the answer is available before the tunnel is up, and the
/// reply also states whether the request really arrived over Tor.
/// </summary>
public static class ExitIpChecker
{
    /// <summary>
    /// The Tor Project's endpoint is tried first because it is the only one that can confirm the
    /// request really arrived over Tor. The plain-text services after it only report an address,
    /// and exist so a blocked or unavailable first endpoint does not leave the display empty.
    /// </summary>
    private static readonly (string Url, bool ReportsTorStatus)[] Endpoints =
    [
        ("https://check.torproject.org/api/ip", true),
        ("https://icanhazip.com", false),
        ("https://api.ipify.org", false)
    ];

    public static async Task<ExitInfo?> QueryAsync(
        int socksPort,
        TorControlClient? control,
        CancellationToken cancellationToken)
    {
        foreach (var (url, reportsTorStatus) in Endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await QueryEndpointAsync(url, reportsTorStatus, socksPort, cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    continue;
                }

                var (ip, isTor) = result.Value;

                string? country = null;
                if (control is { IsConnected: true })
                {
                    // Tor ships the same GeoIP database it uses for relay selection, so the country
                    // is resolved locally instead of asking a third-party geolocation service.
                    country = await control.LookupCountryAsync(ip, cancellationToken).ConfigureAwait(false);
                }

                Log.App(
                    $"Exit address {ip}{(country is null ? string.Empty : $" ({country.ToUpperInvariant()})")}" +
                    $", confirmed Tor: {(reportsTorStatus ? isTor.ToString() : "not reported by this endpoint")}");

                return new ExitInfo(ip, country, isTor, reportsTorStatus);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.App($"The exit check against {url} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log.App("Every exit address endpoint failed");
        return null;
    }

    private static async Task<(string Ip, bool IsTor)?> QueryEndpointAsync(
        string url,
        bool reportsTorStatus,
        int socksPort,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            UseCookies = false
        };

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TorVpnForWindows/1.0");

        var body = (await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();

        if (!reportsTorStatus)
        {
            return body.Length is > 0 and <= 45 ? (body, false) : null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("IP", out var ipElement))
        {
            Log.App($"{url} returned an unexpected payload: {body}");
            return null;
        }

        var ip = ipElement.GetString();
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var isTor = root.TryGetProperty("IsTor", out var torElement) && torElement.ValueKind == JsonValueKind.True;
        return (ip, isTor);
    }
}
