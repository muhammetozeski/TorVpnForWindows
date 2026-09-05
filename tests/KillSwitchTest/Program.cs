using System.Diagnostics;
using System.Text;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Exercises the kill switch on its own, without Tor or the tunnel.
///
/// It arms the block, confirms that a permitted process still reaches the network while an
/// unpermitted one does not, then disarms and confirms the machine is back to normal. Keeping this
/// separate from the tunnel test means a mistake in the filters shows up in fifteen seconds instead
/// of after a two minute bootstrap, and the blocking window stays short.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "--probe")
        {
            // Child mode: report whether this process can reach the internet. Run from a copy in a
            // temporary folder so it is a different executable from the permitted parent.
            return await CanReachAsync().ConfigureAwait(false) ? 0 : 1;
        }

        Log.Entry += entry => Console.WriteLine($"    {entry.Display}");

        var guard = new KillSwitchGuard();
        var unpermitted = string.Empty;

        try
        {
            Console.WriteLine("=== before ===");
            Check("the machine has internet before the test", await CanReachAsync().ConfigureAwait(false), "no connectivity to start with");

            AppPaths.EnsureDirectories();
            ExclusionList.EnsureExists();

            var excluded = ExclusionList.Read();
            var binaries = Binaries.Resolve();
            var permits = KillSwitchGuard.BuildPermitList(binaries, excluded);

            Console.WriteLine();
            Console.WriteLine("permitted executables:");
            foreach (var permit in permits)
            {
                Console.WriteLine($"    {permit}");
            }

            unpermitted = MakeUnpermittedCopy();
            Console.WriteLine($"unpermitted copy: {unpermitted}");

            Console.WriteLine();
            Console.WriteLine("=== arming ===");
            Check("the kill switch arms", guard.Arm(permits), "Arm returned false");
            Check("it reports itself as armed", guard.IsArmed, "IsArmed is false after arming");

            Console.WriteLine($"filters installed: {CountOurFilters()}");

            await Task.Delay(1500).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("=== while armed ===");

            Check("a permitted process still reaches the internet",
                await CanReachAsync().ConfigureAwait(false),
                "this test is on the permit list but was blocked anyway");

            Check("an unpermitted process is blocked",
                !await ChildCanReachAsync(unpermitted).ConfigureAwait(false),
                "a copy that is not on the permit list still reached the internet");
        }
        catch (Exception ex)
        {
            Fail("run", $"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex);
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("=== disarming ===");
            guard.Disarm();
            guard.Dispose();

            await Task.Delay(1500).ConfigureAwait(false);

            Check("nothing of ours is left in the filter table", CountOurFilters() == 0,
                $"{CountOurFilters()} filter(s) still present");

            Check("the machine has internet again", await CanReachAsync().ConfigureAwait(false),
                "connectivity did not come back");

            if (unpermitted.Length > 0)
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(unpermitted)!, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"could not clean up the copy: {ex.Message}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? $"ALL {_checks} CHECKS PASSED" : $"{_failures} OF {_checks} CHECKS FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task<bool> CanReachAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await http.GetAsync("https://1.1.1.1/").ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Copies this test somewhere else so it is a different image than the permitted one.</summary>
    private static string MakeUnpermittedCopy()
    {
        var source = Environment.ProcessPath
            ?? throw new InvalidOperationException("The path of the running test could not be determined.");

        var sourceDirectory = Path.GetDirectoryName(source)!;
        var target = Path.Combine(Path.GetTempPath(), "TorVpnKillSwitchProbe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(target, Path.GetFileName(source));
    }

    private static async Task<bool> ChildCanReachAsync(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable, "--probe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.WriteLine("    the unpermitted copy could not be started");
                return false;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            Console.WriteLine($"    unpermitted copy exit code: {process.ExitCode} (0 means it got through)");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    could not run the unpermitted copy: {ex.Message}");
            return false;
        }
    }

    /// <summary>Counts filters carrying this application's display name, via netsh.</summary>
    private static int CountOurFilters()
    {
        try
        {
            var xml = Path.Combine(Path.GetTempPath(), "killswitch-filters.xml");

            var startInfo = new ProcessStartInfo("netsh.exe", $"wfp show filters file=\"{xml}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var process = Process.Start(startInfo))
            {
                process?.StandardOutput.ReadToEnd();
                process?.WaitForExit(20000);
            }

            if (!File.Exists(xml))
            {
                return -1;
            }

            var text = File.ReadAllText(xml);
            File.Delete(xml);

            var count = 0;
            var index = 0;
            const string marker = "<name>Tor VPN for Windows</name>";

            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }

            return count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    could not count filters: {ex.Message}");
            return -1;
        }
    }

    private static void Check(string name, bool condition, string detail)
    {
        _checks++;

        if (condition)
        {
            Console.WriteLine($"PASS  {name}");
            return;
        }

        Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"      {detail}");
    }
}
