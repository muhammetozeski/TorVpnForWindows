using System.Diagnostics;
using System.Text;
using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

/// <summary>
/// Owns the sing-box process, which creates the TUN adapter, takes over the default route and
/// forwards everything to Tor.
/// </summary>
public sealed class SingBoxRunner : IAsyncDisposable
{
    private readonly JobObject _job;
    private Process? _process;
    private bool _stopRequested;
    private TaskCompletionSource? _started;

    /// <summary>Set when sing-box reports that it could not put an IPv6 address on the adapter.</summary>
    private bool _ipv6ConfigurationFailed;

    public SingBoxRunner(JobObject job) => _job = job;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised when sing-box exits without being asked to.</summary>
    public event Action<int>? Exited;

    public async Task StartAsync(
        AppSettings settings,
        Binaries binaries,
        SessionEndpoints endpoints,
        IReadOnlyList<string> excludedProcesses,
        IReadOnlyList<string> upstreamDnsServers,
        CancellationToken cancellationToken)
    {
        var singBoxExe = binaries.SingBox.Path;

        if (!File.Exists(singBoxExe))
        {
            throw new FileNotFoundException($"sing-box.exe is missing at {singBoxExe}.", singBoxExe);
        }

        // A leftover adapter from a previous session makes the address configuration fail, so give
        // Wintun a chance to finish removing it before asking for a new one with the same name.
        if (!await NetworkProbe.WaitForAdapterGoneAsync(
                settings.TunInterfaceName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
        {
            Log.App(
                $"An adapter named {settings.TunInterfaceName} is still present after 15 s. " +
                "Starting anyway; if this fails, the previous adapter has to be removed first.");
        }

        // Some machines refuse an IPv6 address on a freshly created adapter, and that aborts the
        // whole start. Rather than deciding up front, the full configuration is tried first and the
        // IPv4-only one is used only when that specific step is what failed.
        try
        {
            await StartOnceAsync(settings, binaries, endpoints, excludedProcesses, upstreamDnsServers,
                forceIpv4Only: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_ipv6ConfigurationFailed && !cancellationToken.IsCancellationRequested)
        {
            Log.App($"The adapter refused an IPv6 address ({ex.GetType().Name}); retrying without it");

            await StopAsync().ConfigureAwait(false);
            _ipv6ConfigurationFailed = false;

            await NetworkProbe.WaitForAdapterGoneAsync(
                settings.TunInterfaceName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            await StartOnceAsync(settings, binaries, endpoints, excludedProcesses, upstreamDnsServers,
                forceIpv4Only: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartOnceAsync(
        AppSettings settings,
        Binaries binaries,
        SessionEndpoints endpoints,
        IReadOnlyList<string> excludedProcesses,
        IReadOnlyList<string> upstreamDnsServers,
        bool forceIpv4Only,
        CancellationToken cancellationToken)
    {
        var singBoxExe = binaries.SingBox.Path;

        var config = SingBoxConfigBuilder.Build(
            settings, endpoints, excludedProcesses, upstreamDnsServers, binaries.ProcessNames(), forceIpv4Only);
        await File.WriteAllTextAsync(AppPaths.SingBoxConfigFile, config, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        Log.App($"Tunnel configuration written to {AppPaths.SingBoxConfigFile}");

        var startInfo = new ProcessStartInfo
        {
            FileName = singBoxExe,

            // The working directory matters: sing-box loads wintun.dll from next to its own
            // executable, and the bundled copy sits there.
            WorkingDirectory = Path.GetDirectoryName(singBoxExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(AppPaths.SingBoxConfigFile);

        _stopRequested = false;
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += OnExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("sing-box.exe could not be started.");
        }

        _job.Assign(_process);
        ChildProcessRegistry.Track(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Log.App($"sing-box.exe started, PID {_process.Id}");

        await WaitUntilServingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for sing-box to report that it finished starting. If it fails instead, for example
    /// because the adapter could not be created, this surfaces that as an exception rather than
    /// letting the caller believe the tunnel is up.
    /// </summary>
    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
        var started = _started ?? throw new InvalidOperationException("The process was not started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var registration = timeout.Token.Register(static state =>
            ((TaskCompletionSource)state!).TrySetCanceled(), started);

        try
        {
            await started.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"sing-box.exe exited with code {_process.ExitCode} while creating the tunnel. " +
                    "The log pane holds the reason it reported.");
            }

            throw new TimeoutException("sing-box did not finish creating the tunnel within 30 seconds.");
        }
    }

    /// <summary>
    /// Stops the tunnel. Safe to call more than once and from more than one thread: the first
    /// caller takes the process handle, so a later call has nothing left to dispose twice.
    /// </summary>
    public async Task StopAsync()
    {
        _stopRequested = true;

        var process = Interlocked.Exchange(ref _process, null);

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // A GUI process has no console, so there is no way to deliver the Ctrl+Break that
                // sing-box treats as a shutdown request. Terminating it is safe: the Wintun adapter
                // is bound to the process and disappears with it, taking its routes along, and the
                // WFP filters live in a dynamic session that the kernel tears down when the handle
                // closes. The smoke test checks that the routes and the adapter really do go.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }

            ChildProcessRegistry.Forget(process.Id);
            Log.App("sing-box.exe stopped");
        }
        catch (Exception ex)
        {
            Log.Error("Stopping sing-box.exe failed", ex);

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception killEx)
            {
                Log.Error("Terminating sing-box.exe also failed", killEx);
            }
        }
        finally
        {
            process.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        Log.SingBox(e.Data);

        if (e.Data.Contains("set ipv6 address", StringComparison.OrdinalIgnoreCase))
        {
            _ipv6ConfigurationFailed = true;
        }

        if (e.Data.Contains("sing-box started", StringComparison.OrdinalIgnoreCase))
        {
            _started?.TrySetResult();
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
            Log.Error("Could not read the sing-box exit code", ex);
        }

        Log.App($"sing-box.exe exited with code {code}");

        _started?.TrySetException(new InvalidOperationException($"sing-box.exe exited with code {code}."));

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

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
