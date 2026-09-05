using System.Text.Json;
using System.Text.Json.Serialization;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

public enum BridgeOrigin
{
    /// <summary>Fetched from the Tor Project during this session.</summary>
    Live,

    /// <summary>Read from a previous fetch stored on disk.</summary>
    Cached,

    /// <summary>The list compiled into this build, used when the Tor Project cannot be reached.</summary>
    Embedded,

    /// <summary>Lines the user pasted in themselves.</summary>
    User
}

public sealed record BridgeSet(IReadOnlyList<string> Lines, BridgeOrigin Origin, DateTimeOffset? FetchedAt);

/// <summary>
/// Supplies the bridge lines Tor should use.
///
/// The Tor Project publishes its built-in bridges at a stable endpoint, and those bridges are
/// retired and replaced over time. Shipping a copy compiled into the application would mean the
/// list ages with the release rather than with the network, so the current list is fetched and
/// cached instead. The compiled-in copy is only the last resort, for the case where the endpoint
/// itself is unreachable, which is exactly when someone needs bridges most.
/// </summary>
public static class BridgeProvider
{
    private const string BuiltinUrl = "https://bridges.torproject.org/moat/circumvention/builtin";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(3);

    private static string CacheFile => Path.Combine(AppPaths.Root, "bridges-cache.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Resolves the bridge lines for the configured mode. Never throws: a failure to reach the Tor
    /// Project falls back to the cache and then to the compiled-in list.
    /// </summary>
    public static async Task<BridgeSet> ResolveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.BridgeMode == BridgeMode.None)
        {
            return new BridgeSet([], BridgeOrigin.User, null);
        }

        if (settings.BridgeMode == BridgeMode.Custom)
        {
            var lines = settings.CustomBridges
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();

            return new BridgeSet(lines, BridgeOrigin.User, null);
        }

        var cache = ReadCache();

        if (cache is not null && DateTimeOffset.UtcNow - cache.FetchedAt < CacheLifetime)
        {
            var cached = Select(cache, settings.BridgeMode);
            if (cached.Count > 0)
            {
                Log.App($"Using cached built-in bridges from {cache.FetchedAt:yyyy-MM-dd HH:mm} UTC");
                return new BridgeSet(cached, BridgeOrigin.Cached, cache.FetchedAt);
            }
        }

        var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (fetched is not null)
        {
            WriteCache(fetched);

            var live = Select(fetched, settings.BridgeMode);
            if (live.Count > 0)
            {
                Log.App($"Fetched {live.Count} built-in {settings.BridgeMode} bridge(s) from the Tor Project");
                return new BridgeSet(live, BridgeOrigin.Live, fetched.FetchedAt);
            }

            Log.App($"The Tor Project returned no {settings.BridgeMode} bridges");
        }

        // The endpoint was unreachable. An expired cache is still far better than a list frozen at
        // build time, so try it before falling back to the compiled-in copy.
        if (cache is not null)
        {
            var stale = Select(cache, settings.BridgeMode);
            if (stale.Count > 0)
            {
                Log.App($"Could not reach the Tor Project; using the cached list from {cache.FetchedAt:yyyy-MM-dd HH:mm} UTC");
                return new BridgeSet(stale, BridgeOrigin.Cached, cache.FetchedAt);
            }
        }

        Log.App("Could not reach the Tor Project and no cache is available; using the built-in list");
        return new BridgeSet(BuiltInBridges.For(settings.BridgeMode).ToList(), BridgeOrigin.Embedded, null);
    }

    private static async Task<BridgeCache?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TorVpnForWindows/1.0");

            var json = await http.GetStringAsync(BuiltinUrl, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var cache = new BridgeCache { FetchedAt = DateTimeOffset.UtcNow };

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var lines = property.Value.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line!.Trim())
                    .ToList();

                if (lines.Count > 0)
                {
                    cache.Transports[property.Name] = lines;
                }
            }

            return cache.Transports.Count > 0 ? cache : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.App($"Fetching the built-in bridge list failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static List<string> Select(BridgeCache cache, BridgeMode mode)
    {
        // The endpoint keys its lists by transport name; meek is published under "meek-azure" as
        // well as "meek", so more than one key is accepted per mode.
        string[] keys = mode switch
        {
            BridgeMode.Obfs4 => ["obfs4"],
            BridgeMode.Snowflake => ["snowflake"],
            BridgeMode.Meek => ["meek-azure", "meek"],
            _ => []
        };

        foreach (var key in keys)
        {
            if (cache.Transports.TryGetValue(key, out var lines) && lines.Count > 0)
            {
                return lines;
            }
        }

        return [];
    }

    private static BridgeCache? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
            {
                return null;
            }

            var json = File.ReadAllText(CacheFile);
            return JsonSerializer.Deserialize<BridgeCache>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the bridge cache", ex);
            return null;
        }
    }

    private static void WriteCache(BridgeCache cache)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = CacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, SerializerOptions));
            File.Move(temp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write the bridge cache", ex);
        }
    }

    private sealed class BridgeCache
    {
        public DateTimeOffset FetchedAt { get; set; }

        public Dictionary<string, List<string>> Transports { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
