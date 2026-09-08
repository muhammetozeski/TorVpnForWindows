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
public static partial class Log
{
    private const int MaxInMemory = 2000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly ConcurrentQueue<LogEntry> Buffer = new();
    private static readonly Lock FileGate = new();
    private static bool _fileReady;

    private const int WritesBetweenSizeChecks = 500;
    private static int _writesSinceSizeCheck;

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

        var text = message.TrimEnd();

        if (ShouldSuppressAsRepeat(source, text, out var summary))
        {
            if (summary is null)
            {
                return;
            }

            text = summary;
        }

        var entry = new LogEntry(DateTime.Now, source, text);

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

    // Collapses a message that keeps repeating.
    //
    // A network that goes away while the tunnel is up makes sing-box report an unreachable route for
    // every connection Tor attempts. One such night produced 323,692 identical lines and a 347 MB
    // file, which buried the handful of lines that actually said what happened.
    //
    // The first ten are written as they arrive. After that the message becomes a single running line
    // carrying the count and the span it covers, rewritten as the count grows, so the information is
    // all still there and takes one line instead of hundreds of thousands.
    private const int RepeatsBeforeCollapsing = 10;
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromSeconds(5);

    private static string? _lastKey;
    private static string? _lastText;
    private static LogSource _lastSource;
    private static int _repeatCount;
    private static DateTime _repeatFirstAt;
    private static DateTime _repeatLastAt;
    private static DateTime _lastSummaryAt = DateTime.MinValue;
    private static readonly Lock RepeatGate = new();

    private static string RepeatSummary(string text, int count, DateTime from, DateTime to) =>
        $"{text}   [bu log {from:yyyy.MM.dd HH.mm.ss} tarihinden {to:yyyy.MM.dd HH.mm.ss} tarihine kadar {count} adet atildi]";

    private static bool ShouldSuppressAsRepeat(LogSource source, string text, out string? summary)
    {
        summary = null;
        var now = DateTime.Now;

        // Child process lines carry a timestamp and a connection id, so compare what is left after
        // the numbers are stripped; otherwise every repeat looks unique.
        var key = source + "|" + DigitRun().Replace(text, "#");

        lock (RepeatGate)
        {
            if (key != _lastKey)
            {
                FlushPendingRepeat();

                _lastKey = key;
                _lastText = text;
                _lastSource = source;
                _repeatCount = 1;
                _repeatFirstAt = now;
                _repeatLastAt = now;
                _lastSummaryAt = DateTime.MinValue;
                return false;
            }

            _repeatCount++;
            _repeatLastAt = now;

            if (_repeatCount <= RepeatsBeforeCollapsing)
            {
                return false;
            }

            // Rewriting the running total on every single repeat would put the flood back, so it is
            // refreshed on an interval. The final count is written when the burst ends.
            if (now - _lastSummaryAt < RepeatSummaryInterval)
            {
                return true;
            }

            _lastSummaryAt = now;
            summary = RepeatSummary(text, _repeatCount, _repeatFirstAt, _repeatLastAt);
            return true;
        }
    }

    /// <summary>
    /// Writes the closing count for a burst that has just ended. Call inside <see cref="RepeatGate"/>.
    /// </summary>
    private static void FlushPendingRepeat()
    {
        if (_repeatCount <= RepeatsBeforeCollapsing || _lastText is null)
        {
            return;
        }

        AppendToFile(new LogEntry(
            _repeatLastAt,
            _lastSource,
            RepeatSummary(_lastText, _repeatCount, _repeatFirstAt, _repeatLastAt)));

        _repeatCount = 0;
        _lastText = null;
    }

    /// <summary>Writes any outstanding repeat count, so a burst still running at exit is recorded.</summary>
    public static void Flush()
    {
        try
        {
            lock (RepeatGate)
            {
                FlushPendingRepeat();
                _lastKey = null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Flushing the log failed: {ex.Message}");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
    private static partial System.Text.RegularExpressions.Regex DigitRun();

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

                // Checked while running, not only at startup. Rolling once on open meant a long
                // session wrote into a single file until it stopped, however large it became.
                if (++_writesSinceSizeCheck >= WritesBetweenSizeChecks)
                {
                    _writesSinceSizeCheck = 0;
                    RollIfTooLarge();
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
