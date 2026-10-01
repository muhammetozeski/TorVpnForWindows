using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

/// <summary>A named, independent snapshot of user-editable settings.</summary>
public sealed class SettingsProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profile 1";
    public SettingsValues Settings { get; set; } = new();
}

public sealed partial class AppSettings
{
    public const string DefaultProfileId = "default";
    static readonly PropertyInfo[] ValueProperties = typeof(SettingsValues).GetProperties()
        .Where(property => property.CanWrite).ToArray();

    public List<SettingsProfile> Profiles { get; set; } = [];
    public string ActiveProfileId { get; set; } = "profile-1";

    [JsonIgnore]
    public bool IsDefaultProfile => ActiveProfileId == DefaultProfileId;

    /// <summary>Upgrades old settings into Profile 1 and restores the selected profile.</summary>
    void InitializeProfiles()
    {
        Profiles ??= [];
        Profiles.RemoveAll(profile => profile is null || profile.Id == DefaultProfileId);
        if (Profiles.Count == 0)
        {
            Profiles.Add(new SettingsProfile { Id = "profile-1", Settings = Snapshot() });
            ActiveProfileId = "profile-1";
        }

        foreach (var profile in Profiles)
        {
            profile.Settings ??= new();
            profile.Settings.Normalize();
        }

        if (IsDefaultProfile)
        {
            CopyValues(new SettingsValues());
        }
        else
        {
            var active = Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId) ?? Profiles[0];
            ActiveProfileId = active.Id;
            CopyValues(active.Settings);
        }
    }

    /// <summary>Copies settings without sharing mutable bridge or program lists.</summary>
    SettingsValues Snapshot() => JsonSerializer.Deserialize<SettingsValues>(
        JsonSerializer.Serialize<SettingsValues>(this, SerializerOptions), SerializerOptions)!;

    /// <summary>Updates the live object held by the VPN from an independent snapshot.</summary>
    void CopyValues(SettingsValues values)
    {
        var copy = JsonSerializer.Deserialize<SettingsValues>(
            JsonSerializer.Serialize(values, SerializerOptions), SerializerOptions)!;
        foreach (var property in ValueProperties)
        {
            property.SetValue(this, property.GetValue(copy));
        }
    }

    /// <summary>Records only editable profiles; Default is always generated from shipped defaults.</summary>
    void CaptureActiveProfile()
    {
        if (!IsDefaultProfile)
        {
            var active = Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId);
            if (active is not null)
            {
                active.Settings = Snapshot();
            }
        }
    }

    /// <summary>Saves the old profile and applies the requested profile to the live settings.</summary>
    /// <param name="id">Default or an existing user profile identifier.</param>
    /// <returns>Whether the requested profile exists.</returns>
    public bool SelectProfile(string id)
    {
        var next = Profiles.FirstOrDefault(profile => profile.Id == id);
        if (id != DefaultProfileId && next is null)
        {
            return false;
        }

        CaptureActiveProfile();
        ActiveProfileId = id;
        CopyValues(next?.Settings ?? new SettingsValues());
        Save();
        Log.App($"Selected settings profile {id}");
        return true;
    }

    /// <summary>Creates and selects a new editable copy of Default, with an unused name.</summary>
    /// <returns>The newly selected profile.</returns>
    public SettingsProfile CreateProfile()
    {
        var number = 1;
        while (Profiles.Any(profile => profile.Name.Equals($"Profile {number}", StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        var profile = new SettingsProfile { Name = $"Profile {number}" };
        Profiles.Add(profile);
        SelectProfile(profile.Id);
        return profile;
    }

    /// <summary>Renames an editable profile, rejecting empty, reserved and duplicate names.</summary>
    /// <param name="name">The requested display name.</param>
    /// <returns>Whether the name was accepted.</returns>
    public bool RenameActiveProfile(string name)
    {
        name = name.Trim();
        var active = Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId);
        if (active is null || name.Length == 0 || name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
            Profiles.Any(profile => profile != active && profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        active.Name = name;
        Save();
        Log.App($"Renamed settings profile {ActiveProfileId} to '{name}'");
        return true;
    }

    /// <summary>Resets one setting to Default while retaining the user's personal bridges.</summary>
    /// <param name="name">The property name of a single user setting.</param>
    /// <returns>Whether an editable setting was reset.</returns>
    public bool ResetSetting(string name)
    {
        var property = ValueProperties.FirstOrDefault(property => property.Name == name);
        if (IsDefaultProfile || property is null || name == nameof(CustomBridges) ||
            name == nameof(InternetLists) || name == nameof(TunnelLists))
        {
            return false;
        }

        property.SetValue(this, property.GetValue(new SettingsValues()));
        Save();
        Log.App($"Reset setting {name} in profile {ActiveProfileId}");
        return true;
    }
}
