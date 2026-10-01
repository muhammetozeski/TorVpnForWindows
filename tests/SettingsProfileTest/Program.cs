using System.Text.Json;
using System.Text.Json.Serialization;
using TorVpnForWindows.Config;

namespace TorVpnForWindows.Tests
{
    /// <summary>Checks migration, independent profiles, protected defaults and individual resets.</summary>
    internal static class Program
    {
        static readonly JsonSerializerOptions Options = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS {message}");
        }

        static string Values(SettingsValues settings) => JsonSerializer.Serialize(settings, Options);

        static void Main()
        {
            var folder = TorVpnForWindows.Core.AppPaths.Root;
            Directory.CreateDirectory(TorVpnForWindows.Core.AppPaths.UserData);
            try
            {
                File.Delete(TorVpnForWindows.Core.AppPaths.SettingsFile);
                var fresh = AppSettings.Load();
                Check(fresh.Profiles.Count == 1 && fresh.Profiles[0].Name == "Profile 1" && !fresh.IsDefaultProfile,
                    "First start selects editable Profile 1");
                Check(Values(fresh) == Values(new SettingsValues()), "Profile 1 starts with shipped defaults");

                var legacy = new SettingsValues
                {
                    Language = "tr", BridgeMode = BridgeMode.Custom, MeekFront = "mixed",
                    CustomBridges = ["webtunnel test-bridge"], AllowLan = false, AutoConnect = true,
                    Mtu = 1300, TunInterfaceName = "SavedAdapter",
                    TunnelLists = new ProgramLists { Mode = ProgramListMode.Blacklist, Blacklist = ["saved.exe"] }
                };
                File.WriteAllText(TorVpnForWindows.Core.AppPaths.SettingsFile, Values(legacy));
                var settings = AppSettings.Load();
                Check(Values(settings) == Values(legacy), "Legacy settings migrate without losing any values");
                settings.Save();
                Check(Values(AppSettings.Load()) == Values(legacy), "Migrated profile survives a save and reload");

                settings.SelectProfile(AppSettings.DefaultProfileId);
                Check(Values(settings) == Values(new SettingsValues()), "Default contains only shipped defaults");
                Check(!settings.RenameActiveProfile("Changed") && !settings.ResetSetting(nameof(AppSettings.AllowLan)),
                    "Default rejects rename and reset");
                var other = settings.CreateProfile();
                Check(other.Name == "Profile 2" && Values(settings) == Values(new SettingsValues()),
                    "New profile is a fresh copy of Default");
                settings.TunnelLists.Blacklist.Add("other.exe");
                settings.CustomBridges.Add("other-bridge");
                settings.AllowLan = false;
                Check(settings.RenameActiveProfile("Work"), "User profile accepts its own name");
                Check(!settings.RenameActiveProfile("Default") && !settings.RenameActiveProfile("Profile 1") &&
                    !settings.RenameActiveProfile("  "), "Reserved, duplicate and empty names are rejected");

                settings.SelectProfile("profile-1");
                Check(Values(settings) == Values(legacy), "Other profile edits do not mutate Profile 1");
                Check(settings.ResetSetting(nameof(AppSettings.Mtu)) && settings.Mtu == 1420,
                    "One advanced setting resets independently");
                Check(settings.AutoConnect && !settings.AllowLan && settings.TunInterfaceName == "SavedAdapter" &&
                    settings.TunnelLists.Blacklist.SequenceEqual(["saved.exe"]), "Reset keeps unrelated settings");
                Check(settings.ResetSetting(nameof(AppSettings.BridgeMode)) && settings.BridgeMode == BridgeMode.None &&
                    settings.CustomBridges.SequenceEqual(["webtunnel test-bridge"]), "Bridge reset preserves personal bridge lines");
                Check(!settings.ResetSetting(nameof(AppSettings.CustomBridges)) &&
                    !settings.ResetSetting(nameof(AppSettings.TunnelLists)), "Personal bridges and whole list groups cannot be reset");

                settings.SelectProfile(AppSettings.DefaultProfileId);
                settings.Save();
                var reloaded = AppSettings.Load();
                Check(reloaded.IsDefaultProfile && Values(reloaded) == Values(new SettingsValues()),
                    "Default selection persists and stays unchanged");
                reloaded.SelectProfile(other.Id);
                Check(reloaded.Profiles.First(profile => profile.Id == other.Id).Name == "Work" &&
                    reloaded.CustomBridges.SequenceEqual(["other-bridge"]) &&
                    reloaded.TunnelLists.Blacklist.SequenceEqual(["other.exe"]), "Named profile and its independent lists survive reload");

                reloaded.Save();
                var expected = Values(AppSettings.Load());
                File.WriteAllText(TorVpnForWindows.Core.AppPaths.SettingsFile, "{ damaged settings");
                var recovered = AppSettings.Load();
                Check(Values(recovered) == expected && !recovered.IsFirstRun,
                    "A corrupt primary recovers the complete previous profile");
                Check(File.ReadAllText(TorVpnForWindows.Core.AppPaths.SettingsFile + ".broken") == "{ damaged settings",
                    "The unreadable original is preserved");
                recovered.Save();
                Check(Values(AppSettings.Load()) == expected, "Saving after recovery keeps a readable backup");
                var tempFile = TorVpnForWindows.Core.AppPaths.SettingsFile + ".tmp";
                Directory.CreateDirectory(tempFile);
                var failed = false;
                try { recovered.Save(); } catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && Values(AppSettings.Load()) == expected, "Failed save is reported and does not damage persisted settings");
                Directory.Delete(tempFile);

                File.Delete(TorVpnForWindows.Core.AppPaths.SettingsBackupFile);
                File.WriteAllText(TorVpnForWindows.Core.AppPaths.SettingsFile, "invalid");
                failed = false;
                try { AppSettings.Load(); } catch (InvalidDataException) { failed = true; }
                Check(failed, "Unrecoverable settings stop initialization instead of silently replacing user data");

                Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(TorVpnForWindows.Core.AppPaths.LegacyRoot);
                File.WriteAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "settings.json"), Values(legacy));
                File.WriteAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "lang.custom.xml"), "custom language");
                File.WriteAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "bridges-cache.json"), "bridge cache");
                var torState = Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "tor-data", "state");
                Directory.CreateDirectory(Path.GetDirectoryName(torState)!);
                File.WriteAllText(torState, "saved guards");
                TorVpnForWindows.Core.PortableDataMigration.Run();
                Check(Values(AppSettings.Load()) == Values(legacy), "Portable migration preserves every legacy setting");
                Check(File.ReadAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.TorData, "state")) == "saved guards" &&
                    File.ReadAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.UserData, "lang.custom.xml")) == "custom language" &&
                    File.ReadAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.UserCache, "bridges-cache.json")) == "bridge cache",
                    "Portable migration retains Tor state, language files and bridge cache");
                Check(!File.Exists(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "settings.json")) &&
                    File.Exists(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "settings.json.migrated")),
                    "Legacy settings remain as a backup without being reimported on a fresh start");
                File.WriteAllText(Path.Combine(TorVpnForWindows.Core.AppPaths.LegacyRoot, "settings.json"), "old copy");
                TorVpnForWindows.Core.PortableDataMigration.Run();
                Check(Values(AppSettings.Load()) == Values(legacy), "Migration does not overwrite an existing portable profile");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}

namespace TorVpnForWindows.Core
{
    /// <summary>Isolates settings I/O inside this test's build output.</summary>
    internal static class AppPaths
    {
        public static string Root => Path.Combine(AppContext.BaseDirectory, "settings-fixture");
        public static string UserData => Path.Combine(Root, "UserData");
        public static string UserCache => Path.Combine(Root, "UserCache");
        public static string LegacyRoot => Path.Combine(Root, "legacy");
        public static string SettingsFile => Path.Combine(UserData, "settings.json");
        public static string SettingsBackupFile => SettingsFile + ".previous";
        public static string ExclusionsFile => Path.Combine(UserData, "exclusions.txt");
        public static string TorData => Path.Combine(UserData, "tor-data");
        public static string ChildProcessFile => Path.Combine(Root, "AppCache", "session", "children.json");
    }

    /// <summary>Collects production diagnostics without opening the real application's log.</summary>
    internal static class Log
    {
        public static void App(string message) => Console.WriteLine(message);
        public static void Error(string message, Exception exception) => Console.WriteLine($"EXPECTED ERROR {message}: {exception.GetType().Name}");
    }
}
