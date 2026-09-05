using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TorVpnForWindows.Core;

public sealed record TorReply(int Code, IReadOnlyList<string> Lines)
{
    public bool IsOk => Code is >= 200 and < 300;

    public string Text => string.Join(Environment.NewLine, Lines);
}

public sealed record BootstrapStatus(int Progress, string Tag, string Summary);

/// <summary>
/// Speaks Tor's control protocol over the loopback control port. Used for bootstrap progress,
/// byte counters, country lookups against Tor's own GeoIP data, and requesting a new circuit.
/// </summary>
public sealed partial class TorControlClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Queue<TaskCompletionSource<TorReply>> _pending = new();
    private readonly Lock _pendingGate = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    public bool IsConnected => _client?.Connected == true;

    public event Action<BootstrapStatus>? BootstrapChanged;
    public event Action<string>? NoticeReceived;
    public event Action? Disconnected;

    public async Task ConnectAsync(int controlPort, string cookieFilePath, CancellationToken cancellationToken)
    {
        var cookie = await ReadCookieAsync(cookieFilePath, cancellationToken).ConfigureAwait(false);

        _client = new TcpClient();
        await _client.ConnectAsync(IPAddress.Loopback, controlPort, cancellationToken).ConfigureAwait(false);

        _stream = _client.GetStream();
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token), CancellationToken.None);

        var hex = Convert.ToHexString(cookie);
        var auth = await SendAsync($"AUTHENTICATE {hex}", cancellationToken).ConfigureAwait(false);
        if (!auth.IsOk)
        {
            throw new InvalidOperationException($"Tor rejected the control cookie: {auth.Code} {auth.Text}");
        }

        var events = await SendAsync("SETEVENTS STATUS_CLIENT NOTICE WARN ERR", cancellationToken).ConfigureAwait(false);
        if (!events.IsOk)
        {
            Log.App($"SETEVENTS was refused: {events.Code} {events.Text}");
        }

        Log.App($"Control port authenticated on 127.0.0.1:{controlPort}");
    }

    private static async Task<byte[]> ReadCookieAsync(string path, CancellationToken cancellationToken)
    {
        // Tor writes the cookie as it finishes starting up, so the file can lag the port opening.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(path))
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
                // Tor still has the file open for writing; try again shortly.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new FileNotFoundException($"Tor did not create its control cookie at {path}.", path);
    }

    public async Task<TorReply> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("The control connection is not open.");
        }

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var tcs = new TaskCompletionSource<TorReply>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_pendingGate)
            {
                _pending.Enqueue(tcs);
            }

            await _writer.WriteLineAsync(command).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            // An abandoned entry deliberately stays in the queue. A late reply then dequeues it,
            // TrySetResult returns false and the reply is dropped, which keeps replies and
            // commands aligned instead of shifting every later reply by one.
            using var registration = timeout.Token.Register(static state =>
                ((TaskCompletionSource<TorReply>)state!).TrySetCanceled(), tcs);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>Returns the value of a single-key GETINFO, or null when Tor does not know it.</summary>
    public async Task<string?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await SendAsync($"GETINFO {key}", cancellationToken).ConfigureAwait(false);
            if (!reply.IsOk)
            {
                return null;
            }

            foreach (var line in reply.Lines)
            {
                var prefix = key + "=";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line[prefix.Length..];
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"GETINFO {key} failed", ex);
            return null;
        }
    }

    public async Task<long> GetTrafficAsync(bool read, CancellationToken cancellationToken = default)
    {
        var value = await GetInfoAsync(read ? "traffic/read" : "traffic/written", cancellationToken).ConfigureAwait(false);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? bytes : 0;
    }

    /// <summary>Two-letter country code for an address, resolved from Tor's bundled GeoIP database.</summary>
    public async Task<string?> LookupCountryAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var value = await GetInfoAsync($"ip-to-country/{ipAddress}", cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(value) || value.Equals("??", StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }

    public async Task<bool> NewIdentityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await SendAsync("SIGNAL NEWNYM", cancellationToken).ConfigureAwait(false);
            return reply.IsOk;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("SIGNAL NEWNYM failed", ex);
            return false;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        using var reader = new StreamReader(_stream, new UTF8Encoding(false));
        var buffer = new List<string>();
        var code = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length < 4 || !int.TryParse(line[..3], out var lineCode))
                {
                    // Continuation of a multi-line data block (the "250+key=" form).
                    buffer.Add(line);
                    continue;
                }

                code = lineCode;
                var separator = line[3];
                var payload = line.Length > 4 ? line[4..] : string.Empty;

                if (separator is '-' or '+')
                {
                    buffer.Add(payload);
                    continue;
                }

                // A space in the fourth position marks the final line of the reply.
                buffer.Add(payload);
                var reply = new TorReply(code, buffer.ToArray());
                buffer.Clear();

                if (code == 650)
                {
                    DispatchAsyncEvent(reply);
                }
                else
                {
                    CompleteNextPending(reply);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error("The control connection read loop stopped", ex);
        }
        finally
        {
            FailAllPending(new IOException("The control connection closed."));

            try
            {
                Disconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("A Disconnected handler threw", ex);
            }
        }
    }

    private void DispatchAsyncEvent(TorReply reply)
    {
        foreach (var line in reply.Lines)
        {
            if (line.Contains("BOOTSTRAP", StringComparison.Ordinal))
            {
                var match = BootstrapPattern().Match(line);
                if (match.Success)
                {
                    var progress = int.Parse(match.Groups["progress"].Value, CultureInfo.InvariantCulture);
                    var tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : string.Empty;
                    var summary = match.Groups["summary"].Success ? match.Groups["summary"].Value : string.Empty;

                    try
                    {
                        BootstrapChanged?.Invoke(new BootstrapStatus(progress, tag, summary));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("A BootstrapChanged handler threw", ex);
                    }
                }
            }

            try
            {
                NoticeReceived?.Invoke(line);
            }
            catch (Exception ex)
            {
                Log.Error("A NoticeReceived handler threw", ex);
            }
        }
    }

    private void CompleteNextPending(TorReply reply)
    {
        TaskCompletionSource<TorReply>? tcs = null;

        lock (_pendingGate)
        {
            if (_pending.Count > 0)
            {
                tcs = _pending.Dequeue();
            }
        }

        tcs?.TrySetResult(reply);
    }

    private void FailAllPending(Exception exception)
    {
        lock (_pendingGate)
        {
            while (_pending.Count > 0)
            {
                _pending.Dequeue().TrySetException(exception);
            }
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Teardown can arrive from the connect path and from a process-exit handler at the same
        // time; only the first caller does the work.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_readerCts is not null)
            {
                await _readerCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Cancelling the control reader failed", ex);
        }

        try
        {
            if (_readerTask is not null)
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The reader is blocked on a dead socket; disposing the stream below releases it.
        }
        catch (Exception ex)
        {
            Log.Error("Waiting for the control reader failed", ex);
        }

        _writer?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _readerCts?.Dispose();
        _commandGate.Dispose();
    }

    [GeneratedRegex(
        """BOOTSTRAP\s+PROGRESS=(?<progress>\d+)(?:\s+TAG=(?<tag>\S+))?(?:\s+SUMMARY="(?<summary>[^"]*)")?""",
        RegexOptions.ExplicitCapture)]
    private static partial Regex BootstrapPattern();
}
