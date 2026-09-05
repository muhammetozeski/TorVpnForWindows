using System.Diagnostics;
using System.Text;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Checks that helper executables are located by walking PATH and nothing else.
///
/// The traditional Windows lookup — where.exe, and the Win32 SearchPath function underneath it —
/// starts with the directory of the running program and the current working directory. A resolver
/// built on either would return a file that merely happens to sit next to the application and
/// report it as a PATH hit. This sets up a decoy in the working directory and proves the resolver
/// ignores it, while where.exe does not.
///
/// Run it with no arguments; it creates its own fixtures in a temporary folder and cleans up.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    /// <summary>
    /// Printed when this program is invoked as the stand-in tool. The fixture is a copy of the test
    /// itself rather than a system utility: a renamed cmd.exe cannot find its message resources and
    /// prints a localisation error instead of its version, which would make the fixture, not the
    /// resolver, decide the result.
    /// </summary>
    private const string ProbeBanner = "probe-tool ready";

    private const string ProbeArgument = "--emit-probe-banner";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ProbeArgument)
        {
            Console.WriteLine(ProbeBanner);
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;

        var root = Path.Combine(Path.GetTempPath(), "TorVpnForWindows-PathTest-" + Guid.NewGuid().ToString("N")[..8]);
        var workingDirectory = Path.Combine(root, "cwd");
        var pathDirectory = Path.Combine(root, "onpath");
        var brokenDirectory = Path.Combine(root, "broken");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalWorkingDirectory = Environment.CurrentDirectory;

        try
        {
            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(pathDirectory);
            Directory.CreateDirectory(brokenDirectory);

            // A file that is not a real program, placed where the traditional lookup would find it.
            File.WriteAllText(Path.Combine(workingDirectory, "probe-tool.exe"), "decoy in the working directory");

            // A working stand-in on PATH: a copy of this test, which answers the version probe with
            // a known banner.
            InstallProbeTool(pathDirectory);

            // A file with the right name on PATH that is not the program it claims to be. It comes
            // first, so the resolver has to skip it and carry on to the working one.
            File.WriteAllText(Path.Combine(brokenDirectory, "probe-tool.exe"), "not an executable");

            Environment.CurrentDirectory = workingDirectory;
            var bundled = Path.Combine(root, "bundled", "probe-tool.exe");

            RunCase(
                "a decoy in the working directory is ignored while a PATH entry is used",
                $"{pathDirectory};{Environment.SystemDirectory}",
                bundled,
                expectedSource: BinarySource.SystemPath,
                expectedPath: Path.Combine(pathDirectory, "probe-tool.exe"),
                workingDirectory);

            RunCase(
                "with nothing on PATH the bundled copy is used, never the working directory",
                Environment.SystemDirectory,
                bundled,
                expectedSource: BinarySource.Bundled,
                expectedPath: bundled,
                workingDirectory);

            RunCase(
                "a PATH entry that fails the version probe is skipped",
                $"{brokenDirectory};{pathDirectory};{Environment.SystemDirectory}",
                bundled,
                expectedSource: BinarySource.SystemPath,
                expectedPath: Path.Combine(pathDirectory, "probe-tool.exe"),
                workingDirectory);

            RunCase(
                "a relative PATH entry is ignored because it resolves against the working directory",
                $".;{Environment.SystemDirectory}",
                bundled,
                expectedSource: BinarySource.Bundled,
                expectedPath: bundled,
                workingDirectory);

            CheckWhereExeStillPrefersTheWorkingDirectory(pathDirectory, workingDirectory);
        }
        catch (Exception ex)
        {
            Fail("the test itself", $"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex);
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
            Environment.SetEnvironmentVariable("PATH", originalPath);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"could not clean up {root}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECKS FAILED");

        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Copies this test into <paramref name="directory"/> as probe-tool.exe.
    ///
    /// The apphost has the name of its managed assembly baked in, so the companion files keep their
    /// original names; only the executable is renamed. Renaming the assembly as well leaves the
    /// apphost looking for a file that is not there.
    /// </summary>
    private static void InstallProbeTool(string directory)
    {
        var sourceExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("The path of the running test could not be determined.");

        var sourceDirectory = Path.GetDirectoryName(sourceExe)!;

        File.Copy(sourceExe, Path.Combine(directory, "probe-tool.exe"), overwrite: true);

        foreach (var companion in Directory.GetFiles(sourceDirectory))
        {
            if (companion.Equals(sourceExe, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(companion, Path.Combine(directory, Path.GetFileName(companion)), overwrite: true);
        }

        // Confirm the fixture actually answers before any resolver check depends on it, so a broken
        // fixture is never mistaken for a broken resolver.
        var banner = BinaryResolver.ProbeVersion(
            Path.Combine(directory, "probe-tool.exe"), ProbeArgument, ProbeBanner);

        if (banner is null)
        {
            throw new InvalidOperationException(
                $"The stand-in tool in {directory} did not print its banner, so the fixture is broken.");
        }

        Console.WriteLine($"    fixture ready: {banner}");
    }

    private static void RunCase(
        string name,
        string path,
        string bundledPath,
        BinarySource expectedSource,
        string expectedPath,
        string workingDirectory)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name}");

        Environment.SetEnvironmentVariable("PATH", path);

        var resolved = BinaryResolver.Resolve(
            "probe-tool.exe",
            bundledPath,
            candidate => BinaryResolver.ProbeVersion(candidate, ProbeArgument, ProbeBanner));

        Console.WriteLine($"    resolved to {resolved.Source}: {resolved.Path}");

        Check(name + " - source", resolved.Source == expectedSource, $"got {resolved.Source}");
        Check(name + " - path",
            string.Equals(resolved.Path, expectedPath, StringComparison.OrdinalIgnoreCase),
            $"got {resolved.Path}, expected {expectedPath}");

        var resolvedDirectory = Path.GetDirectoryName(resolved.Path)?.TrimEnd('\\');
        Check(name + " - never the working directory",
            !string.Equals(resolvedDirectory, workingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
            $"resolved inside the working directory: {resolved.Path}");
    }

    /// <summary>
    /// Records the behaviour this resolver exists to avoid. If a future Windows stops returning the
    /// working directory here, the check reports it rather than silently passing.
    /// </summary>
    private static void CheckWhereExeStillPrefersTheWorkingDirectory(string pathDirectory, string workingDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("--- baseline: where.exe returns the working directory copy");

        Environment.SetEnvironmentVariable("PATH", $"{pathDirectory};{Environment.SystemDirectory}");

        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "where.exe"), "probe-tool.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.WriteLine("    where.exe could not be started; skipping the baseline");
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            Console.WriteLine($"    where.exe -> {first ?? "<nothing>"}");

            if (first is null)
            {
                Console.WriteLine("    where.exe found nothing; the baseline cannot be shown");
                return;
            }

            var sameAsWorkingDirectory = string.Equals(
                Path.GetDirectoryName(first)?.TrimEnd('\\'),
                workingDirectory.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            Console.WriteLine(sameAsWorkingDirectory
                ? "    confirmed: the traditional lookup would have picked the decoy"
                : "    note: where.exe no longer prefers the working directory on this system");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    baseline check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(string name, bool condition, string detail)
    {
        _checks++;

        if (condition)
        {
            Console.WriteLine($"    PASS  {name}");
            return;
        }

        Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Console.WriteLine($"    FAIL  {name}");
        Console.WriteLine($"          {detail}");
    }
}
