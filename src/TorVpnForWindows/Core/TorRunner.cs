using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

/// <summary>
/// Owns the tor.exe process: writes its configuration, starts it, follows its bootstrap and keeps a
/// control connection open for the lifetime of the session.
/// </summary>
public sealed partial class TorRunner : IAsyncDisposable
{
    private readonly JobObject _job;
    private Process? _process;
    private TorControlClient? _control;

    public TorRunner(JobObject job) => _job = job;

    public SessionEndpoints? Endpoints { get; private set; }

    public TorControlClient? Control => _control;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<BootstrapStatus>? BootstrapChanged;

    /// <summary>Raised when tor.exe exits without being asked to.</summary>
    public event Action<int>? Exited;

    private bool _stopRequested;
    private int _lastReportedProgress = -1;

    public async Task StartAsync(AppSettings settings, Binaries binaries, CancellationToken cancellationToken)
    {
        var torExe = binaries.Tor.Path;

        if (!File.Exists(torExe))
        {
            throw new FileNotFoundException($"tor.exe is missing at {torExe}.", torExe);
        }

        Endpoints = ResolveEndpoints(settings);
        Log.App($"Tor ports: socks={Endpoints.SocksPort} dns={Endpoints.DnsPort} control={Endpoints.ControlPort}");

        AppPaths.EnsureDirectories();

        // A cookie from a previous run would be read before the new Tor overwrites it, and the
        // authentication would then fail with a confusing message.
        var cookiePath = Path.Combine(AppPaths.TorData, "control_auth_cookie");
        TryDeleteStaleCookie(cookiePath);

        var bridges = await BridgeProvider.ResolveAsync(settings, cancellationToken).ConfigureAwait(false);
        if (bridges.Lines.Count > 0)
        {
            Log.App($"Using {bridges.Lines.Count} bridge line(s), source: {bridges.Origin}");
        }

        var torrc = TorRcBuilder.Build(settings, Endpoints, bridges.Lines, binaries.Lyrebird.Path);
        await File.WriteAllTextAsync(AppPaths.TorRcFile, torrc, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = torExe,
            WorkingDirectory = Path.GetDirectoryName(torExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(AppPaths.TorRcFile);

        _stopRequested = false;
        _lastReportedProgress = -1;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += OnExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("tor.exe could not be started.");
        }

        _job.Assign(_process);
        ChildProcessRegistry.Track(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Log.App($"tor.exe started, PID {_process.Id}");

        await WaitForControlPortAsync(Endpoints.ControlPort, cancellationToken).ConfigureAwait(false);

        _control = new TorControlClient();
        _control.BootstrapChanged += status => ReportBootstrap(status);
        _control.NoticeReceived += line => Log.Tor(line);

        await _control.ConnectAsync(Endpoints.ControlPort, cookiePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Blocks until Tor reports 100% bootstrap, it exits, or the caller cancels.</summary>
    public async Task WaitForBootstrapAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProgress(BootstrapStatus status)
        {
            if (status.Progress >= 100)
            {
                completion.TrySetResult();
            }
        }

        BootstrapChanged += OnProgress;

        try
        {
            // The 100% event can arrive before this method is called, so poll once up front.
            if (await IsBootstrappedAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            using var registration = cancellationToken.Register(static state =>
                ((TaskCompletionSource)state!).TrySetCanceled(), completion);

            // Tor occasionally finishes without a final event reaching us; poll as a backstop
            // rather than waiting forever on an event that already fired.
            while (!completion.Task.IsCompleted)
            {
                var finished = await Task.WhenAny(completion.Task, Task.Delay(2000, cancellationToken))
                    .ConfigureAwait(false);

                if (finished == completion.Task)
                {
                    break;
                }

                if (_process is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"tor.exe exited with code {_process.ExitCode} before finishing its bootstrap.");
                }

                if (await IsBootstrappedAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            BootstrapChanged -= OnProgress;
        }
    }

    private async Task<bool> IsBootstrappedAsync(CancellationToken cancellationToken)
    {
        if (_control is null)
        {
            return false;
        }

        var phase = await _control.GetInfoAsync("status/bootstrap-phase", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(phase))
        {
            return false;
        }

        var match = BootstrapProgressPattern().Match(phase);
        if (!match.Success)
        {
            return false;
        }

        var progress = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var summaryMatch = SummaryPattern().Match(phase);
        ReportBootstrap(new BootstrapStatus(progress, string.Empty, summaryMatch.Success ? summaryMatch.Groups[1].Value : string.Empty));

        return progress >= 100;
    }

    /// <summary>
    /// Stops Tor. Safe to call more than once and from more than one thread: the fields are taken
    /// over by the first caller, so a second one finds nothing left to do rather than tripping over
    /// a half-disposed connection.
    /// </summary>
    public async Task StopAsync()
    {
        _stopRequested = true;

        var control = Interlocked.Exchange(ref _control, null);
        var process = Interlocked.Exchange(ref _process, null);

        if (control is not null)
        {
            try
            {
                // A clean shutdown lets Tor flush its state; the process exits on its own.
                await control.SendAsync("SIGNAL HALT").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.App($"SIGNAL HALT did not go through ({ex.GetType().Name}), terminating instead");
            }

            try
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Closing the control connection failed", ex);
            }
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                if (!process.WaitForExit(3000))
                {
                    Log.App("tor.exe did not stop on request, killing it");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }

            ChildProcessRegistry.Forget(process.Id);
            Log.App("tor.exe stopped");
        }
        catch (Exception ex)
        {
            Log.Error("Stopping tor.exe failed", ex);

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Log.Error("Terminating tor.exe also failed", killEx);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static SessionEndpoints ResolveEndpoints(AppSettings settings)
    {
        var socks = NetworkProbe.FindFreeTcpPort(settings.TorSocksPort);
        var control = NetworkProbe.FindFreeTcpPort(settings.TorControlPort == socks ? 0 : settings.TorControlPort);
        var dns = NetworkProbe.FindFreeTcpPort(
            settings.TorDnsPort == socks || settings.TorDnsPort == control ? 0 : settings.TorDnsPort);

        return new SessionEndpoints(socks, dns, control);
    }

    private static void TryDeleteStaleCookie(string cookiePath)
    {
        try
        {
            if (File.Exists(cookiePath))
            {
                File.Delete(cookiePath);
            }
        }
        catch (Exception ex)
        {
            // Tor rewrites it on startup anyway; the retry loop in the control client copes.
            Log.App($"Could not remove the previous control cookie ({ex.GetType().Name})");
        }
    }

    private static async Task WaitForControlPortAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var listener in listeners)
            {
                if (listener.Port == port && IPAddress.IsLoopback(listener.Address))
                {
                    return;
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Tor did not open its control port on 127.0.0.1:{port} within 30 seconds.");
    }

    private void ReportBootstrap(BootstrapStatus status)
    {
        if (status.Progress == _lastReportedProgress)
        {
            return;
        }

        _lastReportedProgress = status.Progress;

        try
        {
            BootstrapChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Log.Error("A BootstrapChanged handler threw", ex);
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        Log.Tor(e.Data);

        // The control connection is the primary source of progress, but stdout still carries it
        // during the window before the control port is authenticated.
        var match = StdoutBootstrapPattern().Match(e.Data);
        if (match.Success)
        {
            var progress = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var summary = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            ReportBootstrap(new BootstrapStatus(progress, string.Empty, summary));
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var code = -1;
        try
        {
            code = _process?.ExitCode ?? -1;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the tor.exe exit code", ex);
        }

        Log.App($"tor.exe exited with code {code}");

        if (_stopRequested)
        {
            return;
        }

        try
        {
            Exited?.Invoke(code);
        }
        catch (Exception ex)
        {
            Log.Error("An Exited handler threw", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    [GeneratedRegex(@"Bootstrapped (\d+)%(?:\s*\([^)]*\))?(?::\s*(.*))?")]
    private static partial Regex StdoutBootstrapPattern();

    [GeneratedRegex(@"PROGRESS=(\d+)")]
    private static partial Regex BootstrapProgressPattern();

    [GeneratedRegex("SUMMARY=\"([^\"]*)\"")]
    private static partial Regex SummaryPattern();
}
