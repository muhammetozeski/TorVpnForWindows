using System.Net;
using System.Net.Sockets;
using System.Text;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Checks how the control client reads Tor's replies, and how the entry address is found from them,
/// against a stand-in for Tor's control port on 127.0.0.1 that answers with recorded reply shapes.
///
/// No Tor, no network, no privileges. The cookie file lives in a temporary folder that is removed.
/// </summary>
internal static class Program
{
    private const string Bridge = "F228C0A440F33D338C8A38C9313BE5587533CD2B";
    private const string Middle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Exit = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Relay = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    private static int _failures;

    private static async Task<int> Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ControlReplyTest-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        var cookie = Path.Combine(folder, "control_auth_cookie");
        await File.WriteAllBytesAsync(cookie, new byte[32]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);

        try
        {
            await using var control = new TorControlClient();
            await control.ConnectAsync(port, cookie, CancellationToken.None);

            var circuits = await control.GetInfoLinesAsync("circuit-status");
            Say($"circuit-status: {string.Join(" | ", circuits)}");

            Check("a data block keeps every line, including one that starts with a three digit number",
                circuits.Count == 3 && circuits[1].StartsWith("123 BUILT", StringComparison.Ordinal));

            Check("a data line sent with a doubled leading dot comes back with one",
                circuits.Count == 3 && circuits[2] == ".not the end");

            Check("the reply after a data block is still matched to its own command",
                await control.GetInfoAsync("status/circuit-established") == "1");

            Check("the first hop of the built circuit is the bridge",
                EntryNodeChecker.FirstHopOf(circuits) == Bridge);

            var withBridgeLine = await EntryNodeChecker.QueryAsync(control,
                [$"obfs4 86.94.218.55:9085 {Bridge} cert=abc iat-mode=0"], CancellationToken.None);

            Check("with the bridge line the entry is its address, and the country comes from Tor",
                withBridgeLine is { Address: "86.94.218.55:9085", CountryCode: "NL" });

            var fromDescriptor = await EntryNodeChecker.QueryAsync(control, [], CancellationToken.None);
            Check("without a bridge line the address comes from the bridge's descriptor",
                fromDescriptor is { Address: "86.94.218.55:9085", CountryCode: "NL" });

            Check("a snowflake bridge is shown by its transport, not its placeholder address",
                EntryNodeChecker.FromBridgeLines(Bridge, [$"snowflake 192.0.2.3:80 {Bridge} fingerprint={Bridge} url=https://x/"]) == "snowflake");

            Check("a meek bridge is shown as meek",
                EntryNodeChecker.FromBridgeLines(Bridge, [$"meek_lite 192.0.2.20:80 {Bridge} url=https://x/ front=y"]) == "meek");

            Check("a plain bridge line without a transport gives its address",
                EntryNodeChecker.FromBridgeLines(Bridge, [$"86.94.218.55:9085 {Bridge}"]) == "86.94.218.55:9085");

            Check("a relay's address comes from its network status entry",
                EntryNodeChecker.FromNetworkStatus([$"r guard AAAA BBBB 2026-09-14 09:00:00 1.2.3.4 9001 0", "s Fast Guard Running Stable Valid"]) == "1.2.3.4:9001");

            Check("a circuit that is only launched is not taken as the entry",
                EntryNodeChecker.FirstHopOf(["5 LAUNCHED BUILD_FLAGS=NEED_CAPACITY PURPOSE=GENERAL"]) is null);

            Check("an internal directory circuit is not taken as the entry",
                EntryNodeChecker.FirstHopOf([$"9 BUILT ${Relay}~dir BUILD_FLAGS=ONEHOP_TUNNEL,IS_INTERNAL PURPOSE=CONFLUX_UNLINKED"]) is null);
        }
        catch (Exception ex)
        {
            Fail($"run: {ex}");
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();

            try
            {
                await server;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // Stopped on purpose.
            }

            Directory.Delete(folder, recursive: true);
        }

        Say(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Answers one client the way Tor's control port does.</summary>
    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        while (!token.IsCancellationRequested)
        {
            var command = await reader.ReadLineAsync(token);
            if (command is null)
            {
                return;
            }

            var reply = command switch
            {
                _ when command.StartsWith("AUTHENTICATE", StringComparison.Ordinal) => "250 OK",
                _ when command.StartsWith("SETEVENTS", StringComparison.Ordinal) => "250 OK",

                "GETINFO circuit-status" =>
                    "250+circuit-status=\r\n" +
                    "7 LAUNCHED BUILD_FLAGS=NEED_CAPACITY PURPOSE=GENERAL\r\n" +
                    $"123 BUILT ${Bridge}~LibertyBridge,${Middle}~middle,${Exit}~exit BUILD_FLAGS=NEED_CAPACITY PURPOSE=CONFLUX_LINKED TIME_CREATED=2026-09-14T09:46:31.000000\r\n" +
                    "..not the end\r\n" +
                    ".\r\n" +
                    "250 OK",

                "GETINFO status/circuit-established" => "250-status/circuit-established=1\r\n250 OK",

                // A bridge is not in the consensus; Tor answers with an error.
                $"GETINFO ns/id/{Bridge}" => $"552 Unrecognized key \"ns/id/{Bridge}\"",

                $"GETINFO desc/id/{Bridge}" =>
                    $"250+desc/id/{Bridge}=\r\n" +
                    "router LibertyBridge 86.94.218.55 9085 0 0\r\n" +
                    "platform Tor 0.4.8.16 on Linux\r\n" +
                    ".\r\n" +
                    "250 OK",

                "GETINFO ip-to-country/86.94.218.55" => "250-ip-to-country/86.94.218.55=nl\r\n250 OK",

                _ => "510 Unrecognized command"
            };

            await writer.WriteLineAsync(reply);
        }
    }

    private static void Check(string name, bool passed)
    {
        if (passed)
        {
            Say($"PASS  {name}");
            return;
        }

        Fail(name);
    }

    private static void Fail(string name)
    {
        _failures++;
        Say($"FAIL  {name}");
    }

    private static void Say(string line) => Console.WriteLine(line);
}
