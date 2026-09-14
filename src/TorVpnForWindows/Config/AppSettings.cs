using System.Text.Json;
using System.Text.Json.Serialization;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

public enum BridgeMode
{
    None,
    Obfs4,
    Snowflake,
    Meek,
    Custom
}

/// <summary>Which list of a <see cref="ProgramLists"/> pair is in force.</summary>
public enum ProgramListMode
{
    /// <summary>Neither list is in force.</summary>
    Off,

    Whitelist,

    Blacklist
}

/// <summary>
/// A white list and a black list of executables, each entry an exact path. The two lists keep their
/// own entries, and at most one of them is in force: both can be off, but turning one on turns the
/// other off. That is why the choice is one value rather than two switches that could both be on.
/// </summary>
public sealed class ProgramLists
{
    public ProgramListMode Mode { get; set; } = ProgramListMode.Off;

    public List<string> Whitelist { get; set; } = [];

    public List<string> Blacklist { get; set; } = [];

    /// <summary>The entries of whichever list is in force; empty when neither is.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ActiveEntries => Mode switch
    {
        ProgramListMode.Whitelist => Whitelist,
        ProgramListMode.Blacklist => Blacklist,
        _ => []
    };

    internal void Normalize()
    {
        if (!Enum.IsDefined(Mode))
        {
            Mode = ProgramListMode.Off;
        }

        Whitelist = Clean(Whitelist);
        Blacklist = Clean(Blacklist);
    }

    private static List<string> Clean(List<string>? entries) => (entries ?? [])
        .Select(entry => entry?.Trim() ?? string.Empty)
        .Where(entry => entry.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// User-visible configuration, persisted as JSON. Defaults are chosen so that a first run with no
/// interaction gives a working, leak-free tunnel.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"system", "en" or "tr". Resolved to a concrete language on first start.</summary>
    public string Language { get; set; } = "system";

    /// <summary>Two-letter country code for the Tor exit relay, or null for no restriction.</summary>
    public string? ExitCountry { get; set; }

    public BridgeMode BridgeMode { get; set; } = BridgeMode.None;

    /// <summary>
    /// Which name meek hides behind: one of <see cref="MeekFronts"/>, "mixed" for all of them, or
    /// null to use the line exactly as the Tor Project publishes it.
    ///
    /// Kept here rather than in the bridge cache because it is a choice, not a fetched fact. The
    /// cache is replaced wholesale every few days; this has to survive that.
    /// </summary>
    public string? MeekFront { get; set; }

    public List<string> CustomBridges { get; set; } = [];

    /// <summary>
    /// Keep the tunnel's routes in place when Tor drops, so traffic fails instead of falling back
    /// to the unprotected connection.
    /// </summary>
    public bool KillSwitch { get; set; } = true;

    /// <summary>
    /// Enables sing-box strict routing: blocks DNS on other interfaces and makes unsupported
    /// networks unreachable. Turning it off can help software that dislikes the WFP filters.
    /// </summary>
    public bool StrictRoute { get; set; } = true;

    /// <summary>Let traffic to private address ranges bypass the tunnel (printers, NAS, router).</summary>
    public bool AllowLan { get; set; } = true;

    /// <summary>
    /// Which programs may reach anything outside this machine at all, like a firewall. In force
    /// whenever the application is running, connected or not.
    /// </summary>
    public ProgramLists InternetLists { get; set; } = new();

    /// <summary>Which programs go through the Tor tunnel and which leave through the normal connection.</summary>
    public ProgramLists TunnelLists { get; set; } = new();

    /// <summary>Set once the names in the old exclusions.txt have been moved into the tunnel black list.</summary>
    public bool ExclusionsMigrated { get; set; }

    /// <summary>Connect as soon as the application starts.</summary>
    public bool AutoConnect { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public string TunInterfaceName { get; set; } = "TorVPN";

    public int Mtu { get; set; } = 1420;

    /// <summary>Preferred loopback ports. A busy port is replaced with a free one at connect time.</summary>
    public int TorSocksPort { get; set; } = 9250;

    public int TorDnsPort { get; set; } = 9253;

    public int TorControlPort { get; set; } = 9251;

    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }

                Log.App("settings.json deserialized to null, using defaults");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings.json, using defaults", ex);

            try
            {
                var backup = AppPaths.SettingsFile + ".broken";
                File.Copy(AppPaths.SettingsFile, backup, overwrite: true);
                Log.App($"Unreadable settings file kept at {backup}");
            }
            catch (Exception copyEx)
            {
                Log.Error("Could not preserve the unreadable settings file", copyEx);
            }
        }

        var fresh = new AppSettings { IsFirstRun = true };
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var json = JsonSerializer.Serialize(this, SerializerOptions);

            // Write to a temporary file first so a crash mid-write cannot corrupt the settings.
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private void Normalize()
    {
        if (Mtu is < 576 or > 9000)
        {
            Mtu = 1420;
        }

        if (string.IsNullOrWhiteSpace(TunInterfaceName))
        {
            TunInterfaceName = "TorVPN";
        }

        if (!string.IsNullOrWhiteSpace(ExitCountry))
        {
            ExitCountry = ExitCountry.Trim().ToLowerInvariant();
            if (ExitCountry.Length != 2)
            {
                ExitCountry = null;
            }
        }
        else
        {
            ExitCountry = null;
        }

        CustomBridges = CustomBridges
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !b.StartsWith('#'))
            .ToList();

        // Absent from settings written by an older version, and null if the file says so.
        InternetLists ??= new ProgramLists();
        TunnelLists ??= new ProgramLists();
        InternetLists.Normalize();
        TunnelLists.Normalize();

        if (!string.IsNullOrWhiteSpace(MeekFront))
        {
            MeekFront = MeekFront.Trim();

            if (!MeekFront.Equals(MeekFronts.Mixed, StringComparison.OrdinalIgnoreCase) &&
                !MeekFronts.IsKnown(MeekFront))
            {
                Log.App($"Unknown meek front '{MeekFront}' in settings; falling back to the published one");
                MeekFront = null;
            }
        }
        else
        {
            MeekFront = null;
        }

        if (BridgeMode == BridgeMode.Custom && CustomBridges.Count == 0)
        {
            Log.App("Bridge mode is Custom but no bridge lines are configured; bridges disabled");
            BridgeMode = BridgeMode.None;
        }
    }
}
