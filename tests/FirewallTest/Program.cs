using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Checks the filter code the kill switch and the internet lists share, without touching any traffic
/// but its own.
///
/// Every filter here names a copy of this test in a temporary folder, and every connection it tries
/// goes to a listener on 127.0.0.1 inside this process. No packet leaves the machine, no other
/// program's traffic is matched, and the filters live in dynamic sessions that close with the test.
/// A white list is not applied, because its block-all rule would hold every program on the machine
/// for as long as it is in place.
///
/// Needs administrator rights, since adding filters does.
/// </summary>
internal static class Program
{
    private const int Connected = 0;
    private const int AccessDenied = 10;
    private const int OtherError = 11;
    private const int TimedOut = 12;

    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--probe")
        {
            return await ProbeAsync(int.Parse(args[1])).ConfigureAwait(false);
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("run this from an elevated prompt; adding WFP filters needs administrator rights");
                return 2;
            }
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var accepting = AcceptAsync(listener, stop.Token);

        var folder = Path.Combine(Path.GetTempPath(), $"FirewallTest probe folder {Environment.ProcessId}");
        var probe = MakeCopy(folder);
        var canonical = ExecutablePaths.Canonicalize(probe) ?? probe;

        try
        {
            Console.WriteLine($"probe: {canonical}");
            Console.WriteLine($"listener: 127.0.0.1:{port}");

            Check("without filters the probe reaches the listener", await RunProbeAsync(probe, port) == Connected);

            // The shared session code on its own: a block on the probe's path with nothing exempted.
            using (var session = WfpSession.Open("TorVpnForWindows FirewallTest", "FirewallTest filters", "FirewallTest"))
            {
                using var appId = WfpSession.AppId.For(canonical)
                    ?? throw new InvalidOperationException("no application identifier for the probe");

                session.AddFilter(WfpSession.LayerAleAuthConnectV4, WfpSession.ActionBlock, 1, "block the probe", appId.Condition);

                Check("a block on the probe's path refuses its connection", await RunProbeAsync(probe, port) == AccessDenied);

                var spellings = ExecutablePaths.SpellingsOf(canonical);
                if (spellings.Count > 1)
                {
                    Console.WriteLine($"short spelling: {spellings[1]}");
                    Check("the block also holds when the probe is started through its 8.3 short path",
                        await RunProbeAsync(spellings[1], port) == AccessDenied);
                }
                else
                {
                    Console.WriteLine("the folder has no 8.3 short name; short path check skipped");
                }
            }

            Check("closing the session removes the block", await RunProbeAsync(probe, port) == Connected);

            // The internet lists: a black-listed program stays blocked outside the machine, but its
            // connections inside the machine are left alone.
            using (var firewall = new InternetFirewall())
            {
                var lists = new ProgramLists { Mode = ProgramListMode.Blacklist, Blacklist = { canonical } };
                firewall.Apply(lists, Binaries.Resolve());

                Check("a black-listed program can still connect inside this machine",
                    await RunProbeAsync(probe, port) == Connected);

                // The same list again changes nothing and must not throw.
                firewall.Apply(lists, Binaries.Resolve());

                firewall.Apply(new ProgramLists(), Binaries.Resolve());
                Check("turning the lists off leaves the probe connecting", await RunProbeAsync(probe, port) == Connected);
            }
        }
        catch (Exception ex)
        {
            Fail("run", ex.ToString());
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            listener.Stop();

            try
            {
                await accepting.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The listener was stopped on purpose.
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"could not remove {folder}: {ex.Message}");
            }
        }

        Console.WriteLine(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task<int> ProbeAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return Connected;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return AccessDenied;
        }
        catch (OperationCanceledException)
        {
            return TimedOut;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"probe: {ex.SocketErrorCode} {ex.Message}");
            return OtherError;
        }
    }

    private static async Task<int> RunProbeAsync(string executable, int port)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--probe", port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("the probe did not start");

        await process.WaitForExitAsync().ConfigureAwait(false);
        Console.WriteLine($"    probe exit code {process.ExitCode} ({Describe(process.ExitCode)})");
        return process.ExitCode;
    }

    private static string Describe(int code) => code switch
    {
        Connected => "connected",
        AccessDenied => "refused by a filter",
        TimedOut => "timed out",
        _ => "other error"
    };

    private static async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>A copy of this test in a folder whose long name gets an 8.3 short form.</summary>
    private static string MakeCopy(string folder)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        Directory.CreateDirectory(folder);

        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source)!))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(folder, Path.GetFileName(source));
    }

    private static void Check(string name, bool passed)
    {
        if (passed)
        {
            Console.WriteLine($"PASS  {name}");
            return;
        }

        Fail(name, string.Empty);
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Console.WriteLine($"FAIL  {name} {detail}".TrimEnd());
    }
}
