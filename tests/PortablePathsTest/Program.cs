using System.Diagnostics;
using System.Text.Json;
using TorVpnForWindows.Core;

/// <summary>Executes the real path resolver from a different working directory and a relocated app folder.</summary>
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Contains("--inspect"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { AppPaths.Root, AppPaths.SettingsFile, AppPaths.LogDir }));
            return 0;
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "portable-path-fixture");
        Directory.CreateDirectory(fixture);
        try
        {
            CheckChild(Environment.ProcessPath!, fixture, AppPaths.Root);
            var copied = Path.Combine(fixture, "moved application");
            Directory.CreateDirectory(copied);
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            {
                File.Copy(file, Path.Combine(copied, Path.GetFileName(file)));
            }
            CheckChild(Path.Combine(copied, Path.GetFileName(Environment.ProcessPath!)), fixture, copied);
            return 0;
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    /// <summary>Checks the application's data roots after starting its actual executable in a new context.</summary>
    static void CheckChild(string executable, string workingDirectory, string expectedRoot)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--inspect")
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the path probe.");
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(errors);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.GetProperty("Root").GetString() != expectedRoot ||
            root.GetProperty("SettingsFile").GetString() != Path.Combine(expectedRoot, "UserData", "settings.json") ||
            root.GetProperty("LogDir").GetString() != Path.Combine(expectedRoot, "AppCache", "logs"))
        {
            throw new InvalidOperationException($"Unexpected portable paths: {output}");
        }
        Console.WriteLine($"PASS portable paths follow the executable at {expectedRoot}, independently of the working directory");
    }
}
