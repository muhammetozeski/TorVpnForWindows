using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TorVpnForWindows.Core;

/// <summary>
/// Resolves a name without letting anyone on the network read the question.
///
/// A bridge exists to hide that Tor is in use, and the ordinary resolver undoes that before the
/// first packet of the tunnel is sent: meek has to look up the site it hides behind, snowflake has
/// to look up its broker and its helper servers, and each of those questions travels to the
/// network's own resolver in the clear, where it is answered and logged. The names in question sit
/// in a public file, so a logged query for one is as good as a signed statement.
///
/// The question is asked over HTTPS instead. What leaves the machine is one encrypted request to a
/// well known public resolver, which is what a great many programs do all day and says nothing
/// about what was asked.
///
/// The resolvers are addressed by number rather than by name. Asking the ordinary resolver where
/// the encrypted resolver lives would put the whole exercise back where it started.
/// </summary>
public static class EncryptedDns
{
    /// <summary>
    /// Public resolvers, addressed numerically. Cloudflare answers the simple query form directly on
    /// its addresses; Quad9 serves the same thing but refuses anything below HTTP/2, so the request
    /// asks for that and falls back in order.
    /// </summary>
    private static readonly string[] Resolvers = ["1.1.1.1", "1.0.0.1", "9.9.9.9"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Looks the name up and returns its addresses, or an empty list when every resolver failed.
    /// Never throws for a failed lookup: the caller has a fallback and a thrown exception here would
    /// take down a connection attempt over something that is allowed to fail.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostname,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return [];
        }

        foreach (var resolver in Resolvers)
        {
            try
            {
                var addresses = await QueryAsync(resolver, hostname, cancellationToken).ConfigureAwait(false);

                if (addresses.Count > 0)
                {
                    return addresses;
                }

                Log.App($"Encrypted DNS: {resolver} returned no address for {hostname}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.App($"Encrypted DNS: {resolver} failed for {hostname}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return [];
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(
        string resolver,
        string hostname,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            // Nothing here should ever consult the ordinary resolver, and a proxy would be a second
            // way for that to happen.
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        using var http = new HttpClient(handler) { Timeout = Timeout };

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{resolver}/dns-query?name={Uri.EscapeDataString(hostname)}&type=A")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        request.Headers.Accept.ParseAdd("application/dns-json");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var answer = await response.Content
            .ReadFromJsonAsync<DnsJsonResponse>(cancellationToken)
            .ConfigureAwait(false);

        if (answer?.Answer is null)
        {
            return [];
        }

        var addresses = new List<IPAddress>();

        foreach (var record in answer.Answer)
        {
            // Type 1 is a plain address record. A name that is an alias for another answers with
            // type 5 as well, and those entries carry a name rather than an address.
            if (record.Type != 1 || string.IsNullOrWhiteSpace(record.Data))
            {
                continue;
            }

            if (IPAddress.TryParse(record.Data, out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private sealed class DnsJsonResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("Status")]
        public int Status { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Answer")]
        public List<DnsJsonRecord>? Answer { get; set; }
    }

    private sealed class DnsJsonRecord
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public int Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string? Data { get; set; }
    }
}
