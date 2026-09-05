using System.IO.Compression;
using System.Reflection;

namespace TorVpnForWindows.Core;

/// <summary>
/// Unpacks the Tor / sing-box / Wintun binaries that are embedded in the executable.
/// </summary>
public static class PayloadExtractor
{
    private const string ResourceName = "TorVpnForWindows.payload.zip";
    private const string StampFileName = ".extracted";

    /// <summary>
    /// Extracts the payload into <see cref="AppPaths.Runtime"/> if it is not already there.
    /// Returns true when files were written, false when the existing copy was reused.
    /// </summary>
    public static bool EnsureExtracted(IProgress<string>? progress = null)
    {
        var target = AppPaths.Runtime;
        var stamp = Path.Combine(target, StampFileName);

        if (File.Exists(stamp) && File.Exists(AppPaths.BundledSingBoxExe) && File.Exists(AppPaths.BundledTorExe))
        {
            Log.App($"Runtime already extracted at {target}");
            return false;
        }

        Log.App($"Extracting embedded runtime to {target}");
        progress?.Report("runtime");

        // A partially extracted folder from an interrupted previous run must not be trusted.
        if (Directory.Exists(target))
        {
            TryDeleteDirectory(target);
        }

        Directory.CreateDirectory(target);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing. The build did not include the payload.");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var zipEntry in archive.Entries)
        {
            if (string.IsNullOrEmpty(zipEntry.Name))
            {
                continue; // directory entry
            }

            var destination = Path.GetFullPath(Path.Combine(target, zipEntry.FullName));

            // Guard against a crafted archive escaping the target directory.
            if (!destination.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Payload entry '{zipEntry.FullName}' resolves outside the runtime directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            zipEntry.ExtractToFile(destination, overwrite: true);
        }

        File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));

        CleanUpOldRuntimes();

        Log.App("Runtime extraction finished");
        return true;
    }

    /// <summary>Removes runtime folders left behind by previous versions of the application.</summary>
    private static void CleanUpOldRuntimes()
    {
        try
        {
            var runtimeRoot = Path.Combine(AppPaths.SharedRoot, "runtime");
            var current = Path.GetFileName(AppPaths.Runtime);

            foreach (var dir in Directory.GetDirectories(runtimeRoot))
            {
                if (!string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase))
                {
                    Log.App($"Removing stale runtime {Path.GetFileName(dir)}");
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not clean up old runtimes", ex);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                Log.App($"Delete of {path} failed ({ex.GetType().Name}), retrying");
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                // Leaving the folder behind is survivable: extraction overwrites files anyway.
                Log.Error($"Could not delete {path}", ex);
                return;
            }
        }
    }
}
