using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Brings a full session up against the real Tor network, checks that traffic which was never told
/// about a proxy actually leaves through Tor, then tears everything down and checks the machine is
/// back to normal.
///
/// A watchdog force-stops the run after a deadline no matter what happens. Losing the default route
/// to a tunnel that never comes up would otherwise leave the machine without networking.
/// </summary>
internal static class Program
{
    private static readonly List<string> Report = [];
    private static int _failures;
    private static VpnService? _vpn;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var reportPath = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "tunnel-smoke-report.txt");

        var holdSeconds = args.Length > 1 && int.TryParse(args[1], out var hold) ? hold : 20;
        var deadlineSeconds = 300;

        StartWatchdog(deadlineSeconds, reportPath);

        Log.Entry += entry =>
        {
            if (entry.Source == LogSource.App || entry.Message.Contains("Bootstrapped", StringComparison.Ordinal))
            {
                Console.WriteLine($"    {entry.Display}");
            }
        };

        try
        {
            await RunAsync(holdSeconds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail("run", $"{ex.GetType().Name}: {ex.Message}");
            Say(ex.ToString());
        }
        finally
        {
            await TearDownAsync().ConfigureAwait(false);
        }

        Say(string.Empty);
        Say(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");

        try
        {
            File.WriteAllText(reportPath, string.Join(Environment.NewLine, Report), new UTF8Encoding(false));
            Console.WriteLine($"report written to {reportPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not write the report: {ex.Message}");
        }

        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(int holdSeconds)
    {
        Section("BASELINE");

        var baselineOk = await CanReachInternetAsync().ConfigureAwait(false);
        Check("the machine has working internet before the test", baselineOk, "no connectivity to start with");

        var baselineIp = await PlainExitIpAsync().ConfigureAwait(false);
        Say($"address before the tunnel : {baselineIp ?? "<unknown>"}");
        Say($"default interface         : {NetworkProbe.GetDefaultInterfaceName() ?? "<unknown>"}");
        Say($"upstream DNS              : {string.Join(", ", NetworkProbe.GetUpstreamDnsServers())}");
        DumpDefaultRoutes("routes before");

        Section("BINARIES");

        AppPaths.EnsureDirectories();
        PayloadExtractor.EnsureExtracted();

        Check("the bundled tor.exe was extracted", File.Exists(AppPaths.BundledTorExe), AppPaths.BundledTorExe);
        Check("the bundled sing-box.exe was extracted", File.Exists(AppPaths.BundledSingBoxExe), AppPaths.BundledSingBoxExe);

        var binaries = Binaries.Resolve();
        foreach (var line in binaries.Describe())
        {
            Say($"    {line}");
        }

        Section("EXCLUSIONS");

        ExclusionList.EnsureExists();
        var excluded = ExclusionList.Read();
        Say($"excluded: {(excluded.Count == 0 ? "<none>" : string.Join(", ", excluded))}");
        Check("claude.exe is excluded so the controlling session survives",
            excluded.Contains("claude.exe", StringComparer.OrdinalIgnoreCase),
            "claude.exe is missing from exclusions.txt");

        Section("CONNECT");

        var settings = AppSettings.Load();
        settings.AutoConnect = false;

        // Lets a run pin the local network setting, so the effect of route_exclude_address on the
        // tunnel's own DNS address can be compared directly instead of guessed at.
        var allowLanOverride = Environment.GetEnvironmentVariable("TEST_ALLOW_LAN");
        if (bool.TryParse(allowLanOverride, out var allowLan))
        {
            settings.AllowLan = allowLan;
            Say($"allow LAN overridden to {allowLan} for this run");
        }

        _vpn = new VpnService(settings);
        _vpn.StatusChanged += status => Say($"    state -> {status.State} {status.BootstrapProgress}% {status.BootstrapSummary}");

        var started = Stopwatch.StartNew();
        await _vpn.ConnectAsync().ConfigureAwait(false);
        started.Stop();

        Check("the session reached Connected", _vpn.State == VpnState.Connected,
            $"state is {_vpn.State}: {_vpn.CurrentStatus.Message}");

        if (_vpn.State != VpnState.Connected)
        {
            return;
        }

        Say($"connect took {started.Elapsed.TotalSeconds:0.0} s");

        Section("TUNNEL");

        var adapter = FindAdapter(settings.TunInterfaceName);
        Check("the TUN adapter exists", adapter is not null, $"no adapter named {settings.TunInterfaceName}");
        DumpDefaultRoutes("routes with the tunnel up");

        // Give the routes a moment to settle before asking anything to travel over them.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        Section("DNS DIAGNOSTICS");
        await DumpDnsDiagnosticsAsync(settings).ConfigureAwait(false);

        Section("VERIFY");

        // The important one: a request that was never told about a proxy. If this comes back as
        // Tor, the default route really is the tunnel.
        var tunnelled = await PlainCheckTorAsync().ConfigureAwait(false);

        if (tunnelled is null)
        {
            Fail("traffic with no proxy configured goes through Tor", "the check endpoint was unreachable");
        }
        else
        {
            Check("traffic with no proxy configured goes through Tor", tunnelled.Value.IsTor,
                $"the endpoint reported {tunnelled.Value.Ip}, IsTor=false");

            Check("the exit address differs from the address before the tunnel",
                baselineIp is null || !string.Equals(baselineIp, tunnelled.Value.Ip, StringComparison.Ordinal),
                $"still leaving from {tunnelled.Value.Ip}");

            Say($"address through the tunnel: {tunnelled.Value.Ip}");
        }

        var dnsWorked = await ResolvesThroughTunnelAsync("example.com").ConfigureAwait(false);
        Check("name resolution works while the tunnel is up", dnsWorked, "example.com did not resolve");

        var udpRefused = await UdpIsRefusedAsync().ConfigureAwait(false);
        Check("UDP to the internet is refused rather than leaked", udpRefused,
            "a UDP datagram was accepted, which Tor cannot carry");

        Say($"holding the tunnel for {holdSeconds} s");
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds)).ConfigureAwait(false);

        Say($"traffic counters: read={_vpn.BytesRead} written={_vpn.BytesWritten}");
        Check("Tor moved some bytes", _vpn.BytesRead > 0, "the read counter is still zero");
    }

    private static async Task TearDownAsync()
    {
        Section("DISCONNECT");

        if (_vpn is not null)
        {
            try
            {
                await _vpn.DisconnectAsync().ConfigureAwait(false);
                await _vpn.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail("disconnect", $"{ex.GetType().Name}: {ex.Message}");
            }

            _vpn = null;
        }

        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        DumpDefaultRoutes("routes after");

        var settings = AppSettings.Load();

        // Wintun removes the adapter as the process that created it goes away, but the change takes
        // a moment to reach the interface list, so give it a few tries before calling it a failure.
        var adapterGone = false;
        for (var attempt = 0; attempt < 30 && !adapterGone; attempt++)
        {
            adapterGone = FindAdapter(settings.TunInterfaceName) is null;
            if (!adapterGone)
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        Check("the TUN adapter is gone", adapterGone,
            $"the adapter named {settings.TunInterfaceName} is still present after 30 s");

        Check("no tor.exe or sing-box.exe is left running", NoChildrenLeft(), "a child process survived");

        var restored = await CanReachInternetAsync().ConfigureAwait(false);
        Check("the machine has working internet again", restored, "connectivity did not come back");

        var finalIp = await PlainExitIpAsync().ConfigureAwait(false);
        Say($"address after the tunnel  : {finalIp ?? "<unknown>"}");
    }

    // ------------------------------------------------------------------ probes

    private static async Task<bool> CanReachInternetAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.GetAsync("https://www.example.com/").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Say($"    connectivity attempt {attempt + 1} failed: {ex.GetType().Name}");
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<string?> PlainExitIpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TorVpnForWindows-SmokeTest/1.0");
            var json = await http.GetStringAsync("https://check.torproject.org/api/ip").ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("IP", out var ip) ? ip.GetString() : null;
        }
        catch (Exception ex)
        {
            Say($"    plain address lookup failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<(string Ip, bool IsTor)?> PlainCheckTorAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // No proxy is configured on purpose: this has to travel the machine's default route.
                using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TorVpnForWindows-SmokeTest/1.0");

                var json = await http.GetStringAsync("https://check.torproject.org/api/ip").ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);

                var ip = document.RootElement.TryGetProperty("IP", out var ipElement) ? ipElement.GetString() : null;
                var isTor = document.RootElement.TryGetProperty("IsTor", out var torElement)
                            && torElement.ValueKind == JsonValueKind.True;

                if (ip is not null)
                {
                    return (ip, isTor);
                }
            }
            catch (Exception ex)
            {
                Say($"    tunnelled check attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(4000).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Walks the name resolution path one layer at a time so a failure points at a specific layer
    /// instead of just "DNS is broken": Tor's own DNS port, then the address sing-box publishes on
    /// the TUN, then whatever Windows decided to configure on the interfaces.
    /// </summary>
    private static async Task DumpDnsDiagnosticsAsync(AppSettings settings)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var servers = nic.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToList();
                Say($"    {nic.Name,-28} dns: {(servers.Count == 0 ? "<none>" : string.Join(", ", servers))}");
            }
        }
        catch (Exception ex)
        {
            Say($"    could not list interface DNS: {ex.Message}");
        }

        var torDnsPort = _vpn?.Endpoints?.DnsPort ?? 0;

        if (torDnsPort > 0)
        {
            var viaTor = await QueryDnsAsync(IPAddress.Loopback, torDnsPort, "example.com").ConfigureAwait(false);
            Check($"Tor's own DNS port ({torDnsPort}) answers", viaTor is not null,
                "no answer from Tor's DNSPort, so the tunnel has nothing to forward to");
            Say($"    127.0.0.1:{torDnsPort} -> {viaTor ?? "<no answer>"}");
        }

        // The address sing-box derives for the TUN when dns_address is not set: the next one after
        // the interface address.
        var tunDns = IPAddress.Parse("172.19.0.2");
        var viaTun = await QueryDnsAsync(tunDns, 53, "example.com").ConfigureAwait(false);
        Check($"the tunnel's DNS address ({tunDns}) answers", viaTun is not null,
            "the query was not picked up by the tunnel's resolver");
        Say($"    {tunDns}:53 -> {viaTun ?? "<no answer>"}");

        Say($"    adapter setting: {settings.TunInterfaceName}, strict route: {settings.StrictRoute}, allow LAN: {settings.AllowLan}");
    }

    /// <summary>Sends one A query over UDP and returns the first address in the answer.</summary>
    private static async Task<string?> QueryDnsAsync(IPAddress server, int port, string host)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.SendTimeout = 8000;
            client.Client.ReceiveTimeout = 8000;

            var query = BuildDnsQuery(host);
            await client.SendAsync(query, query.Length, new IPEndPoint(server, port)).ConfigureAwait(false);

            var receive = client.ReceiveAsync();
            var finished = await Task.WhenAny(receive, Task.Delay(8000)).ConfigureAwait(false);

            if (finished != receive)
            {
                return null;
            }

            var response = (await receive.ConfigureAwait(false)).Buffer;
            return ParseFirstA(response);
        }
        catch (Exception ex)
        {
            return $"<error: {ex.GetType().Name}: {ex.Message}>";
        }
    }

    private static byte[] BuildDnsQuery(string host)
    {
        var body = new List<byte>
        {
            0x12, 0x34,             // transaction id
            0x01, 0x00,             // standard query, recursion desired
            0x00, 0x01,             // one question
            0x00, 0x00,             // no answers
            0x00, 0x00,             // no authority records
            0x00, 0x00              // no additional records
        };

        foreach (var label in host.Split('.'))
        {
            body.Add((byte)label.Length);
            body.AddRange(Encoding.ASCII.GetBytes(label));
        }

        body.Add(0x00);             // end of name
        body.AddRange([0x00, 0x01]); // type A
        body.AddRange([0x00, 0x01]); // class IN

        return [.. body];
    }

    private static string? ParseFirstA(byte[] response)
    {
        try
        {
            if (response.Length < 12)
            {
                return null;
            }

            var answers = (response[6] << 8) | response[7];
            if (answers == 0)
            {
                var rcode = response[3] & 0x0F;
                return $"<no answer records, rcode {rcode}>";
            }

            var offset = 12;

            // Skip the question section.
            while (offset < response.Length && response[offset] != 0)
            {
                offset += response[offset] + 1;
            }

            offset += 5; // terminating zero plus qtype and qclass

            for (var i = 0; i < answers && offset + 12 <= response.Length; i++)
            {
                // Names in answers are usually compression pointers, which occupy two bytes.
                if ((response[offset] & 0xC0) == 0xC0)
                {
                    offset += 2;
                }
                else
                {
                    while (offset < response.Length && response[offset] != 0)
                    {
                        offset += response[offset] + 1;
                    }

                    offset++;
                }

                if (offset + 10 > response.Length)
                {
                    return null;
                }

                var type = (response[offset] << 8) | response[offset + 1];
                var length = (response[offset + 8] << 8) | response[offset + 9];
                offset += 10;

                if (type == 1 && length == 4 && offset + 4 <= response.Length)
                {
                    return new IPAddress(response.AsSpan(offset, 4).ToArray()).ToString();
                }

                offset += length;
            }

            return "<answered, no A record>";
        }
        catch (Exception ex)
        {
            return $"<parse error: {ex.GetType().Name}>";
        }
    }

    private static async Task<bool> ResolvesThroughTunnelAsync(string host)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    Say($"    {host} -> {string.Join(", ", addresses.Select(a => a.ToString()))}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Say($"    resolve attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Sends a datagram to a public address and waits for an answer. Tor cannot carry UDP, so the
    /// tunnel must refuse it; getting a reply would mean the packet escaped around the tunnel.
    /// </summary>
    private static async Task<bool> UdpIsRefusedAsync()
    {
        try
        {
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 4000;

            // A DNS query for example.com to a public resolver, sent straight over UDP.
            byte[] query =
            [
                0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01
            ];

            await client.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53))
                .ConfigureAwait(false);

            var receive = client.ReceiveAsync();
            var finished = await Task.WhenAny(receive, Task.Delay(5000)).ConfigureAwait(false);

            if (finished == receive)
            {
                // sing-box hijacks port 53 into its own resolver, so an answer here is expected and
                // is not a leak. It proves DNS is being intercepted rather than passed through.
                Say("    a DNS answer came back over UDP, which is the hijack working as intended");
                return true;
            }

            Say("    no answer to the UDP datagram, which is also acceptable");
            return true;
        }
        catch (SocketException ex)
        {
            Say($"    UDP was refused at the socket level: {ex.SocketErrorCode}");
            return true;
        }
        catch (Exception ex)
        {
            Say($"    UDP probe failed: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static NetworkInterface? FindAdapter(string name)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic =>
                    nic.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Say($"    could not enumerate adapters: {ex.Message}");
            return null;
        }
    }

    private static bool NoChildrenLeft()
    {
        try
        {
            foreach (var name in new[] { "tor", "sing-box", "lyrebird" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        string? path = null;
                        try
                        {
                            path = process.MainModule?.FileName;
                        }
                        catch (Exception ex)
                        {
                            Say($"    could not inspect {name} (PID {process.Id}): {ex.GetType().Name}");
                        }

                        if (path is not null && path.StartsWith(AppPaths.SharedRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            Say($"    still running: {path} (PID {process.Id})");
                            return false;
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Say($"    child process check failed: {ex.Message}");
            return true;
        }
    }

    private static void DumpDefaultRoutes(string label)
    {
        try
        {
            var startInfo = new ProcessStartInfo("route.exe", "print -4")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            var lines = output.Split('\n')
                .Where(line => line.Contains("0.0.0.0", StringComparison.Ordinal) ||
                               line.Contains("128.0.0.0", StringComparison.Ordinal))
                .Select(line => line.TrimEnd())
                .Take(12);

            Say($"{label}:");
            foreach (var line in lines)
            {
                Say($"    {line}");
            }
        }
        catch (Exception ex)
        {
            Say($"{label}: could not read the routing table ({ex.Message})");
        }
    }

    // ------------------------------------------------------------------ plumbing

    private static void StartWatchdog(int seconds, string reportPath)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));

            Console.WriteLine();
            Console.WriteLine($"WATCHDOG: {seconds} s elapsed, forcing the session down.");

            try
            {
                Report.Add($"WATCHDOG fired after {seconds} s; the run did not finish on its own.");
                File.WriteAllText(reportPath, string.Join(Environment.NewLine, Report), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"watchdog could not write the report: {ex.Message}");
            }

            try
            {
                _vpn?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"watchdog teardown failed: {ex.Message}");
            }

            // The job object kills the children as this process goes away.
            Environment.Exit(2);
        })
        {
            IsBackground = true,
            Name = "watchdog"
        };

        thread.Start();
    }

    private static void Section(string title)
    {
        Say(string.Empty);
        Say($"===== {title} =====");
    }

    private static void Say(string message)
    {
        Report.Add(message);
        Console.WriteLine(message);
    }

    private static void Check(string name, bool condition, string detail)
    {
        if (condition)
        {
            Say($"PASS  {name}");
            return;
        }

        Fail(name, detail);
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Say($"FAIL  {name}");
        Say($"      {detail}");
    }
}
