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

        if (BridgeMode == BridgeMode.Custom && CustomBridges.Count == 0)
        {
            Log.App("Bridge mode is Custom but no bridge lines are configured; bridges disabled");
            BridgeMode = BridgeMode.None;
        }
    }
}
