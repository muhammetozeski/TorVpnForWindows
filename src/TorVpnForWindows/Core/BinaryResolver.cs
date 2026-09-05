using System.Diagnostics;
using System.Text;

namespace TorVpnForWindows.Core;

public enum BinarySource
{
    /// <summary>Found in a directory listed in the PATH environment variable.</summary>
    SystemPath,

    /// <summary>The copy that was extracted from this application's own payload.</summary>
    Bundled
}

public sealed record ResolvedBinary(string Path, BinarySource Source, string? Version)
{
    public string Describe() =>
        $"{System.IO.Path.GetFileName(Path)} <- {(Source == BinarySource.SystemPath ? "PATH" : "bundled")}" +
        $" ({Path}){(Version is null ? string.Empty : $", {Version}")}";
}

/// <summary>
/// Finds the helper executables, preferring a copy the machine already keeps up to date over the
/// one shipped inside this application.
///
/// The lookup deliberately does not use where.exe or the Win32 SearchPath function. Both follow the
/// traditional search order, which starts with the directory of the running executable and the
/// current working directory, so either would happily return a file sitting next to the program and
/// call it a PATH hit. This walks the PATH entries itself and looks nowhere else.
/// </summary>
public static class BinaryResolver
{
    /// <summary>
    /// Resolves <paramref name="fileName"/> from PATH, falling back to <paramref name="bundledPath"/>.
    ///
    /// <paramref name="versionProbe"/> runs the candidate and returns its version string, or null if
    /// the file is not the program it claims to be. A PATH entry that fails the probe is skipped, so
    /// an unrelated or broken executable with a matching name cannot break the session.
    /// </summary>
    public static ResolvedBinary Resolve(
        string fileName,
        string bundledPath,
        Func<string, string?>? versionProbe = null)
    {
        foreach (var candidate in EnumeratePathCandidates(fileName))
        {
            if (string.Equals(candidate, bundledPath, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Already covered by the fallback.
            }

            if (versionProbe is null)
            {
                Log.App($"Using {fileName} from PATH: {candidate}");
                return new ResolvedBinary(candidate, BinarySource.SystemPath, null);
            }

            string? version;
            try
            {
                version = versionProbe(candidate);
            }
            catch (Exception ex)
            {
                Log.App($"Ignoring {candidate}: the version probe threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (version is null)
            {
                Log.App($"Ignoring {candidate}: it did not identify itself as {fileName}");
                continue;
            }

            Log.App($"Using {fileName} from PATH: {candidate} ({version})");
            return new ResolvedBinary(candidate, BinarySource.SystemPath, version);
        }

        Log.App($"Using the bundled {fileName}: {bundledPath}");
        return new ResolvedBinary(bundledPath, BinarySource.Bundled, null);
    }

    /// <summary>
    /// Every existing file matching <paramref name="fileName"/> in a PATH directory, in PATH order.
    ///
    /// Relative PATH entries such as "." are skipped on purpose: they resolve against the current
    /// working directory, which is the behaviour this lookup exists to avoid.
    /// </summary>
    public static IEnumerable<string> EnumeratePathCandidates(string fileName)
    {
        var raw = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in raw.Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');

            if (directory.Length == 0)
            {
                continue;
            }

            if (!Path.IsPathRooted(directory))
            {
                Log.App($"Skipping the relative PATH entry '{directory}'; it would resolve against the working directory");
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(directory, fileName));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue; // A malformed PATH entry is not worth failing over.
            }

            if (!seen.Add(full))
            {
                continue;
            }

            bool exists;
            try
            {
                exists = File.Exists(full);
            }
            catch (Exception ex)
            {
                Log.App($"Could not inspect '{full}': {ex.GetType().Name}");
                continue;
            }

            if (exists)
            {
                yield return full;
            }
        }
    }

    /// <summary>
    /// Runs a candidate with the given arguments and returns the first output line that contains
    /// <paramref name="expectedToken"/>, or null. Used to confirm a PATH hit really is the tool.
    /// </summary>
    public static string? ProbeVersion(string executablePath, string arguments, string expectedToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not stop the hung version probe for {executablePath}", ex);
                }

                return null;
            }

            foreach (var line in (stdout + Environment.NewLine + stderr).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains(expectedToken, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Length > 120 ? trimmed[..120] : trimmed;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.App($"Version probe for {executablePath} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
