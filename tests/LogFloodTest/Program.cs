using System.Text;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Feeds the log the kind of flood that produced a 347 MB file overnight and checks that it is now
/// collapsed into a running count, that the count is accurate, and that lines from the application
/// itself are still written one by one.
/// </summary>
internal static class Program
{
    private const int FloodSize = 200_000;

    private static int _failures;
    private static int _checks;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var logFile = AppPaths.AppLogFile;

        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);

            // Start from a known state so the sizes below mean something.
            foreach (var stale in new[] { logFile, logFile + ".1" })
            {
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not clear the log files: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"log file: {logFile}");
        Console.WriteLine($"writing {FloodSize:N0} identical lines plus a few distinct ones");
        Console.WriteLine();

        var started = DateTime.Now;

        Log.App("session start marker");

        // The exact shape of the flood: identical text apart from a connection id and an address.
        for (var i = 0; i < FloodSize; i++)
        {
            Log.SingBox(
                $"ERROR[16797] [{1000000 + i} 19ms] connection: open connection to 192.42.116.72:443 " +
                "using outbound/direct[direct-out]: dial tcp 192.42.116.72:443: no route to internet");
        }

        Log.App("session end marker");
        Log.Flush();

        var elapsed = DateTime.Now - started;
        var info = new FileInfo(logFile);
        var lines = File.ReadAllLines(logFile);
        var text = string.Join("\n", lines);

        Console.WriteLine($"took       : {elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"file size  : {info.Length:N0} bytes");
        Console.WriteLine($"line count : {lines.Length:N0}");
        Console.WriteLine();

        Check("the flood did not produce one line each",
            lines.Length < 1000,
            $"{lines.Length:N0} lines were written for {FloodSize:N0} messages");

        Check("the file stayed small",
            info.Length < 1024 * 1024,
            $"the file is {info.Length:N0} bytes");

        Check("the application's own lines are not collapsed",
            text.Contains("session start marker", StringComparison.Ordinal) &&
            text.Contains("session end marker", StringComparison.Ordinal),
            "a marker written by the application is missing");

        var summaryLines = lines.Where(l => l.Contains("adet atildi", StringComparison.Ordinal)).ToList();

        Check("a summary line was written",
            summaryLines.Count > 0,
            "no line carrying the repeat count was found");

        if (summaryLines.Count > 0)
        {
            var last = summaryLines[^1];
            Console.WriteLine("last summary line:");
            Console.WriteLine($"  {last}");
            Console.WriteLine();

            var match = System.Text.RegularExpressions.Regex.Match(
                last, @"tarihine kadar (\d+) adet atildi");

            Check("the final count matches what was written",
                match.Success && int.Parse(match.Groups[1].Value) == FloodSize,
                match.Success
                    ? $"the summary says {match.Groups[1].Value}, expected {FloodSize}"
                    : "the count could not be read from the summary line");

            Check("the summary carries a time range",
                System.Text.RegularExpressions.Regex.IsMatch(
                    last, @"\d{4}\.\d{2}\.\d{2} \d{2}\.\d{2}\.\d{2} tarihinden \d{4}\.\d{2}\.\d{2} \d{2}\.\d{2}\.\d{2} tarihine kadar"),
                "the from and to timestamps are not both present");
        }

        var firstTen = lines.Count(l =>
            l.Contains("no route to internet", StringComparison.Ordinal) &&
            !l.Contains("adet atildi", StringComparison.Ordinal));

        Console.WriteLine($"uncollapsed copies of the repeated line: {firstTen}");
        Check("the first few repeats are written in full",
            firstTen is >= 10 and <= 12,
            $"{firstTen} plain copies were written, expected about 10");

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECKS FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition, string detail)
    {
        _checks++;

        if (condition)
        {
            Console.WriteLine($"PASS  {name}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL  {name}");
        Console.WriteLine($"      {detail}");
    }
}
