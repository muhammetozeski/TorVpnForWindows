using System.Collections.Concurrent;
using System.Text;

namespace TorVpnForWindows.Core;

public enum LogSource
{
    App,
    Tor,
    SingBox
}

public sealed record LogEntry(DateTime Timestamp, LogSource Source, string Message)
{
    public string Display => $"{Timestamp:HH:mm:ss}  [{Source,-7}]  {Message}";
}

/// <summary>
/// Single log sink for the application and both child processes. Keeps a bounded in-memory ring
/// for the UI and appends everything to a rolling file on disk.
/// </summary>
public static class Log
{
    private const int MaxInMemory = 2000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly Lock FileGate = new();
    private static bool _fileReady;

    public static event Action<LogEntry>? Entry;

    public static IReadOnlyList<LogEntry> Snapshot() => Buffer.ToArray();

    public static void App(string message) => Write(LogSource.App, message);

    public static void Tor(string message) => Write(LogSource.Tor, message);

    public static void SingBox(string message) => Write(LogSource.SingBox, message);

    public static void Error(string message, Exception ex) =>
        Write(LogSource.App, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Write(LogSource source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, source, message.TrimEnd());

        Buffer.Enqueue(entry);
        while (Buffer.Count > MaxInMemory && Buffer.TryDequeue(out _))
        {
        }

        AppendToFile(entry);

        try
        {
            Entry?.Invoke(entry);
        }
        catch
        {
            // A misbehaving UI subscriber must never take down logging.
        }
    }

    private static void AppendToFile(LogEntry entry)
    {
        try
        {
            lock (FileGate)
            {
                if (!_fileReady)
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    RollIfTooLarge();
                    _fileReady = true;
                }

                File.AppendAllText(
                    AppPaths.AppLogFile,
                    $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Source}] {entry.Message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Disk logging is best effort; the in-memory log remains available in the UI.
        }
    }

    private static void RollIfTooLarge()
    {
        try
        {
            var file = new FileInfo(AppPaths.AppLogFile);
            if (!file.Exists || file.Length < MaxFileBytes)
            {
                return;
            }

            var previous = AppPaths.AppLogFile + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            file.MoveTo(previous);
        }
        catch
        {
            // If rolling fails the log simply keeps growing; not worth failing startup over.
        }
    }
}
