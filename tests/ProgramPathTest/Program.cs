using System.Diagnostics;
using System.Text.RegularExpressions;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Checks that every way of writing an executable's path resolves to one spelling, since the program
/// lists match by path and a second spelling of the same file would slip past them.
///
/// Touches nothing outside its own temporary folder and needs no privileges or network.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var canonical = ExecutablePaths.Canonicalize(self);

        Check("the running test resolves to a path", canonical is not null, self);
        Say($"canonical: {canonical}");

        if (canonical is null)
        {
            return Finish();
        }

        Check("the same file in upper case resolves to the same spelling",
            ExecutablePaths.Canonicalize(self.ToUpperInvariant()) == canonical,
            ExecutablePaths.Canonicalize(self.ToUpperInvariant()) ?? "<null>");

        Check("a quoted path resolves to the same spelling",
            ExecutablePaths.Canonicalize($"\"{self}\"") == canonical,
            ExecutablePaths.Canonicalize($"\"{self}\"") ?? "<null>");

        Check("the process image path matches the file path",
            ExecutablePaths.ImagePathOfProcess(Environment.ProcessId) == canonical,
            ExecutablePaths.ImagePathOfProcess(Environment.ProcessId) ?? "<null>");

        Check("a file that does not exist resolves to nothing",
            ExecutablePaths.Canonicalize(Path.Combine(Path.GetDirectoryName(self)!, "does-not-exist.exe")) is null,
            "a path came back for a missing file");

        var notepad = ExecutablePaths.Canonicalize(@"%WINDIR%\SYSTEM32\NOTEPAD.EXE");
        Check("an environment variable is expanded and the case taken from disk",
            notepad is not null && notepad.EndsWith(@"\System32\notepad.exe", StringComparison.Ordinal),
            notepad ?? "<null>");

        CheckShortName();
        CheckJunction(self, canonical);
        CheckRegex();

        return Finish();
    }

    /// <summary>A long name under Program Files, reached through its 8.3 short form.</summary>
    private static void CheckShortName()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidate = Directory.EnumerateDirectories(programFiles)
            .SelectMany(directory =>
            {
                try
                {
                    return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    return [];
                }
            })
            .FirstOrDefault();

        if (candidate is null)
        {
            Say("no executable directly under a Program Files folder; short name check skipped");
            return;
        }

        var canonical = ExecutablePaths.Canonicalize(candidate)!;
        var spellings = ExecutablePaths.SpellingsOf(canonical);
        Say($"spellings of {canonical}: {string.Join(" | ", spellings)}");

        if (spellings.Count < 2)
        {
            Say("the volume keeps no short names for it; short name check skipped");
            return;
        }

        Check("the 8.3 short form resolves back to the long spelling",
            ExecutablePaths.Canonicalize(spellings[1]) == canonical,
            ExecutablePaths.Canonicalize(spellings[1]) ?? "<null>");
    }

    /// <summary>The test's own folder reached through a junction made for the purpose.</summary>
    private static void CheckJunction(string self, string canonical)
    {
        var folder = Path.GetDirectoryName(self)!;
        var junction = Path.Combine(Path.GetTempPath(), $"ProgramPathTest-junction-{Environment.ProcessId}");

        try
        {
            var mklink = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", junction, folder },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;

            mklink.WaitForExit(10000);

            if (!Directory.Exists(junction))
            {
                Fail("a junction could be created for the check", mklink.StandardError.ReadToEnd());
                return;
            }

            var throughJunction = Path.Combine(junction, Path.GetFileName(self));
            Check("a path through a junction resolves to the real spelling",
                ExecutablePaths.Canonicalize(throughJunction) == canonical,
                ExecutablePaths.Canonicalize(throughJunction) ?? "<null>");

            Check("SameFile sees through the junction",
                ExecutablePaths.SameFile(throughJunction, self),
                "SameFile returned false");
        }
        finally
        {
            try
            {
                if (Directory.Exists(junction))
                {
                    // Deleting a junction removes the link, never what it points at.
                    Directory.Delete(junction);
                }
            }
            catch (Exception ex)
            {
                Fail("the junction was removed", ex.Message);
            }
        }
    }

    private static void CheckRegex()
    {
        const string path = @"C:\Program Files (x86)\A+B {1}\run.me$.exe";
        var expected = @"(?i)^C:\\Program Files \(x86\)\\A\+B \{1\}\\run\.me\$\.exe$";
        var pattern = ExecutablePaths.ToGoRegex(path);

        Check("the regex escapes only the metacharacters Go knows", pattern == expected, pattern);

        // .NET reads these constructs the same way Go does, so it stands in for sing-box here. Go's
        // case folding ignores the culture; without CultureInvariant .NET would apply the Turkish
        // rule on this machine, where "i" and "I" are not the same letter, and "Files" would not
        // match "FILES".
        const RegexOptions goLike = RegexOptions.CultureInvariant;

        Check("the regex matches the path in another case",
            Regex.IsMatch(path.ToUpperInvariant(), pattern, goLike), pattern);

        Check("the regex does not match a longer path",
            !Regex.IsMatch(path + ".bak", pattern, goLike), pattern);

        Check("the regex does not match the same name elsewhere",
            !Regex.IsMatch(@"C:\Other\run.me$.exe", pattern, goLike), pattern);
    }

    private static int Finish()
    {
        Say(_failures == 0 ? "RESULT: ALL CHECKS PASSED" : $"RESULT: {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            Say($"PASS  {name}");
        }
        else
        {
            Fail(name, detail);
        }
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Say($"FAIL  {name}: {detail}");
    }

    private static void Say(string line) => Console.WriteLine(line);
}
