using System.Diagnostics;

namespace TorVpnForWindows.Core;

/// <summary>A program that is running now, by the exact path of its executable.</summary>
public sealed record RunningProgram(string Path, string Name);

/// <summary>
/// Lists the executables behind the running processes, one entry per file however many processes it
/// has. Each path is resolved the same way the lists store it, so what is picked here is exactly what
/// the rules will match.
/// </summary>
public static class RunningPrograms
{
    public static IReadOnlyList<RunningProgram> Enumerate()
    {
        var byPath = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // The system and idle processes have no image; protected ones refuse to say.
                    var path = ExecutablePaths.ImagePathOfProcess(process.Id);

                    if (path is null || byPath.ContainsKey(path))
                    {
                        continue;
                    }

                    byPath[path] = new RunningProgram(path, DisplayNameOf(path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited while being read.
                }
            }
        }

        return byPath.Values
            .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(program => program.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The name the program gives itself, or its file name when it gives none.</summary>
    public static string DisplayNameOf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();

                if (!string.IsNullOrEmpty(description))
                {
                    return description;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // Unreadable version data just means the file name is used.
        }

        return System.IO.Path.GetFileName(path);
    }
}
