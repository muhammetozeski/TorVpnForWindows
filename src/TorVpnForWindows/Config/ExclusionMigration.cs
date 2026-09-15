using System.Diagnostics;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

/// <summary>
/// Moves the process names from the old exclusions.txt into the tunnel black list, as exact paths.
///
/// A name only becomes a path through a process running under it, so each name is looked up among
/// the running processes. A name with nothing running under it is left out and written to the log:
/// guessing a path for it is exactly what matching by path exists to avoid.
/// </summary>
public static class ExclusionMigration
{
    public static void Run(AppSettings settings)
    {
        if (settings.ExclusionsMigrated)
        {
            return;
        }

        try
        {
            if (File.Exists(AppPaths.ExclusionsFile))
            {
                MoveNames(settings, ExclusionList.Read());
            }

            settings.ExclusionsMigrated = true;
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Moving the old exclusion list into the tunnel lists failed", ex);
        }
    }

    private static void MoveNames(AppSettings settings, IReadOnlyList<string> names)
    {
        var lists = settings.TunnelLists;
        var added = new List<string>();

        foreach (var name in names)
        {
            var paths = RunningPathsOf(name);

            if (paths.Count == 0)
            {
                Log.App(
                    $"{name} from exclusions.txt is not running, so its path is unknown and it was not moved " +
                    "to the tunnel black list. Add it from the list screen.");
                continue;
            }

            foreach (var path in paths)
            {
                if (!lists.Blacklist.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    lists.Blacklist.Add(path);
                    added.Add(path);
                }
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        // The old list was always in force, so the black list it became is turned on with it.
        if (lists.Mode == ProgramListMode.Off)
        {
            lists.Mode = ProgramListMode.Blacklist;
        }

        Log.App($"Moved to the tunnel black list from exclusions.txt: {string.Join(", ", added)}");
    }

    private static List<string> RunningPathsOf(string name)
    {
        var paths = new List<string>();
        var bare = Path.GetFileNameWithoutExtension(name);

        if (bare.Length == 0)
        {
            return paths;
        }

        foreach (var process in Process.GetProcessesByName(bare))
        {
            using (process)
            {
                var path = ExecutablePaths.ImagePathOfProcess(process.Id);

                if (path is not null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }
}
