using System.Diagnostics;
using System.Text.Json.Nodes;
using TorVpnForWindows.Config;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Builds the sing-box configuration for each state of the tunnel lists and has sing-box itself
/// check it, so a misspelt rule field or a regular expression Go rejects shows up here rather than
/// as a tunnel that will not start.
///
/// "sing-box check" only parses and validates. It creates no adapter, changes no route and sends
/// nothing, so this runs safely next to a live session. The configurations are written to a
/// temporary folder that is removed at the end.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        var singBox = args.Length > 0 ? args[0] : AppPaths.BundledSingBoxExe;

        if (!File.Exists(singBox))
        {
            Console.WriteLine($"sing-box.exe not found at {singBox}; pass its path as the first argument");
            return 2;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"SingBoxConfigTest-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);

        try
        {
            var listed = new[]
            {
                @"C:\Program Files (x86)\Test App {1}\run+me.exe",
                Environment.ProcessPath!
            };

            var spellings = ProgramListRules.SpellingsOf(listed);

            Run(singBox, folder, "off", ProgramListMode.Off, []);
            Run(singBox, folder, "black list", ProgramListMode.Blacklist, spellings);
            Run(singBox, folder, "white list", ProgramListMode.Whitelist, spellings);
            Run(singBox, folder, "empty white list", ProgramListMode.Whitelist, []);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Console.WriteLine(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string singBox, string folder, string name, ProgramListMode mode, IReadOnlyList<string> paths)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {name}");

        var settings = new AppSettings { TunnelLists = new ProgramLists { Mode = mode } };
        var endpoints = new SessionEndpoints(9250, 9253, 9251);

        var json = SingBoxConfigBuilder.Build(settings, endpoints, paths, ["192.168.1.1"], ["tor.exe", "lyrebird.exe", "sing-box.exe"]);
        var file = Path.Combine(folder, $"{name.Replace(' ', '-')}.json");
        File.WriteAllText(file, json);

        var route = JsonNode.Parse(json)!["route"]!;
        Console.WriteLine($"final: {route["final"]}");

        foreach (var rule in route["rules"]!.AsArray())
        {
            Console.WriteLine($"    {rule!.ToJsonString()}");
        }

        var check = Process.Start(new ProcessStartInfo
        {
            FileName = singBox,
            ArgumentList = { "check", "-c", file },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;

        var output = check.StandardOutput.ReadToEnd() + check.StandardError.ReadToEnd();
        check.WaitForExit(30000);

        Check($"sing-box accepts the {name} configuration", check.ExitCode == 0, output.Trim());

        var rules = route["rules"]!.AsArray().Select(r => r!.ToJsonString()).ToList();
        var final = route["final"]!.GetValue<string>();

        switch (mode)
        {
            case ProgramListMode.Blacklist:
                Check("the black list sends its programs out directly",
                    rules.Any(r => r.Contains("process_path_regex") && r.Contains("direct-out")), string.Join(" ", rules));
                Check("everything else still goes through Tor", final == "tor-out", final);
                break;

            case ProgramListMode.Whitelist when paths.Count > 0:
                Check("the white list sends its programs through Tor",
                    rules.Any(r => r.Contains("process_path_regex") && r.Contains("tor-out")), string.Join(" ", rules));
                Check("the white list refuses only its own programs' UDP",
                    rules.Any(r => r.Contains("\"network\":\"udp\"") && r.Contains("process_path_regex")) &&
                    !rules.Any(r => r.Contains("\"network\":\"udp\"") && !r.Contains("process_path_regex")),
                    string.Join(" ", rules));
                Check("everything else leaves directly", final == "direct-out", final);
                break;

            case ProgramListMode.Whitelist:
                Check("an empty white list sends nothing through Tor", final == "direct-out" &&
                    !rules.Any(r => r.Contains("tor-out") && !r.Contains("198.18.0.0/15")), string.Join(" ", rules));
                break;

            default:
                Check("with the lists off everything goes through Tor", final == "tor-out", final);
                Check("with the lists off no path rule is written",
                    !rules.Any(r => r.Contains("process_path_regex")), string.Join(" ", rules));
                break;
        }
    }

    private static void Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            Console.WriteLine($"PASS  {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL  {name}: {detail}");
    }
}
