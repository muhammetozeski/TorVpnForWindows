namespace TorVpnForWindows.Core;

/// <summary>Imports legacy user data once without overwriting an existing portable installation.</summary>
public static class PortableDataMigration
{
    /// <summary>Copies settings, languages, Tor state and caches before retiring the old settings file.</summary>
    public static void Run()
    {
        var legacySettings = Path.Combine(AppPaths.LegacyRoot, "settings.json");
        if (File.Exists(AppPaths.SettingsFile) || !File.Exists(legacySettings))
        {
            return;
        }

        Log.App($"Migrating legacy user data from {AppPaths.LegacyRoot} to {AppPaths.UserData}");
        // Settings are copied last: an interrupted import remains eligible for a complete retry.
        foreach (var file in Directory.EnumerateFiles(AppPaths.LegacyRoot, "lang.*.xml*"))
        {
            CopyFile(file, Path.Combine(AppPaths.UserData, Path.GetFileName(file)));
        }
        foreach (var file in Directory.EnumerateFiles(AppPaths.LegacyRoot, "settings.json.*"))
        {
            CopyFile(file, Path.Combine(AppPaths.UserData, Path.GetFileName(file)));
        }
        CopyFile(Path.Combine(AppPaths.LegacyRoot, "exclusions.txt"), AppPaths.ExclusionsFile);
        CopyTree(Path.Combine(AppPaths.LegacyRoot, "tor-data"), AppPaths.TorData);
        CopyFile(Path.Combine(AppPaths.LegacyRoot, "bridges-cache.json"),
            Path.Combine(AppPaths.UserCache, "bridges-cache.json"));
        CopyFile(Path.Combine(AppPaths.LegacyRoot, "session", "children.json"), AppPaths.ChildProcessFile);

        var retired = legacySettings + ".migrated";
        if (File.Exists(retired))
        {
            retired += "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff");
        }
        // Rename, then copy: if copying fails, restore the exact original for the next attempt.
        File.Move(legacySettings, retired);
        try
        {
            CopyFile(retired, AppPaths.SettingsFile);
        }
        catch
        {
            File.Move(retired, legacySettings);
            throw;
        }
        Log.App($"Portable user data migration finished; original settings retained at {retired}");
    }

    /// <summary>Copies a present legacy file only when the destination does not already exist.</summary>
    static void CopyFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".migration.tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination);
    }

    /// <summary>Copies persistent Tor state while preserving any already imported files.</summary>
    static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }
}
