using System.Diagnostics;
using System.Text.Json;

namespace TorVpnForWindows.Core;

/// <summary>
/// Records the child processes a session started, so a run that ended without cleaning up can be
/// tidied by the next one.
///
/// Matching on the executable path alone is not good enough now that the binaries can come from
/// PATH: a system-wide tor.exe could just as easily belong to Tor Browser, and killing that would
/// be a rude surprise. Each entry therefore carries the process identifier together with its exact
/// start time, and both have to match before anything is terminated.
/// </summary>
public static class ChildProcessRegistry
{
    private sealed record Entry(int Id, string Name, long StartTicks);

    private static readonly Lock Gate = new();
    private static readonly List<Entry> Current = [];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static void Track(Process process)
    {
        try
        {
            var entry = new Entry(process.Id, process.ProcessName, process.StartTime.Ticks);

            lock (Gate)
            {
                Current.RemoveAll(e => e.Id == entry.Id);
                Current.Add(entry);
                Persist();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not record a child process", ex);
        }
    }

    public static void Forget(int processId)
    {
        try
        {
            lock (Gate)
            {
                Current.RemoveAll(e => e.Id == processId);
                Persist();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove a child process record", ex);
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                Current.Clear();
                Persist();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not clear the child process records", ex);
        }
    }

    /// <summary>
    /// Terminates any process still running from a previous session. Called before connecting, so
    /// an abandoned tunnel cannot fight the new one over the default route.
    /// </summary>
    public static void KillLeftovers()
    {
        List<Entry> entries;

        try
        {
            if (!File.Exists(AppPaths.ChildProcessFile))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.ChildProcessFile);
            entries = JsonSerializer.Deserialize<List<Entry>>(json, SerializerOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the child process records", ex);
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                using var process = Process.GetProcessById(entry.Id);

                if (!string.Equals(process.ProcessName, entry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // The identifier was reused by something else.
                }

                if (process.StartTime.Ticks != entry.StartTicks)
                {
                    continue; // Same name, different process.
                }

                Log.App($"Stopping {entry.Name} (PID {entry.Id}) left behind by a previous run");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (ArgumentException)
            {
                // The process is already gone, which is the normal case.
            }
            catch (Exception ex)
            {
                Log.Error($"Could not stop the leftover process {entry.Id}", ex);
            }
        }

        Clear();
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SessionDir);
            var temp = AppPaths.ChildProcessFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, SerializerOptions));
            File.Move(temp, AppPaths.ChildProcessFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not persist the child process records", ex);
        }
    }
}
