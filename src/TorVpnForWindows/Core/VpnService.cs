using System.Diagnostics;
using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

public enum VpnState
{
    Disconnected,

    /// <summary>Unpacking the runtime and starting Tor.</summary>
    Preparing,

    /// <summary>Tor is building its first circuits.</summary>
    Bootstrapping,

    /// <summary>Tor is ready; the TUN adapter is being created and the routes moved.</summary>
    EstablishingTunnel,

    Connected,

    Disconnecting,

    /// <summary>
    /// The tunnel went down on its own. With the kill switch on, the routes stay in place, so
    /// traffic is blocked rather than falling back to the unprotected connection.
    /// </summary>
    Interrupted,

    Failed
}

public sealed record VpnStatus(
    VpnState State,
    int BootstrapProgress,
    string? BootstrapSummary,
    ExitInfo? Exit,
    string? Message);

/// <summary>
/// Drives the whole session: Tor first, then the tunnel, then the exit check. Everything the user
/// interface shows comes from here.
/// </summary>
public sealed class VpnService : IAsyncDisposable
{
    private readonly JobObject _job = new();
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    private TorRunner? _tor;
    private SingBoxRunner? _singBox;
    private CancellationTokenSource? _sessionCts;
    private Task? _statsTask;

    private int _bootstrapProgress;
    private string? _bootstrapSummary;
    private ExitInfo? _exit;
    private string? _message;

    public VpnState State { get; private set; } = VpnState.Disconnected;

    public AppSettings Settings { get; }

    public long BytesRead { get; private set; }

    public long BytesWritten { get; private set; }

    public SessionEndpoints? Endpoints => _tor?.Endpoints;

    public event Action<VpnStatus>? StatusChanged;

    public event Action<long, long>? TrafficChanged;

    public VpnService(AppSettings settings) => Settings = settings;

    public VpnStatus CurrentStatus =>
        new(State, _bootstrapProgress, _bootstrapSummary, _exit, _message);

    public bool CanConnect => State is VpnState.Disconnected or VpnState.Failed or VpnState.Interrupted;

    public bool CanDisconnect => State is VpnState.Connected or VpnState.Interrupted
        or VpnState.Bootstrapping or VpnState.EstablishingTunnel or VpnState.Preparing;

    public async Task ConnectAsync()
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!CanConnect)
            {
                Log.App($"Connect ignored, current state is {State}");
                return;
            }

            // A previous session that ended badly can leave the routes in place.
            await TearDownAsync().ConfigureAwait(false);

            _sessionCts = new CancellationTokenSource();
            var token = _sessionCts.Token;

            _exit = null;
            _bootstrapProgress = 0;
            _bootstrapSummary = null;
            SetState(VpnState.Preparing, null);

            ChildProcessRegistry.KillLeftovers();
            PayloadExtractor.EnsureExtracted();

            // Resolved per connect so a tool installed or updated since the last session is used.
            var binaries = Binaries.Resolve();
            foreach (var line in binaries.Describe())
            {
                Log.App(line);
            }

            _tor = new TorRunner(_job);
            _tor.BootstrapChanged += OnBootstrapChanged;
            _tor.Exited += OnTorExited;

            SetState(VpnState.Bootstrapping, null);
            await _tor.StartAsync(Settings, binaries, token).ConfigureAwait(false);
            await _tor.WaitForBootstrapAsync(token).ConfigureAwait(false);

            Log.App("Tor finished bootstrapping");

            var endpoints = _tor.Endpoints
                ?? throw new InvalidOperationException("Tor started without resolving its ports.");

            SetState(VpnState.EstablishingTunnel, null);

            var excluded = ExclusionList.Read();
            if (excluded.Count > 0)
            {
                Log.App($"Excluded from the tunnel: {string.Join(", ", excluded)}");
            }

            // Read the machine's resolvers before the TUN takes over, otherwise the answer is the
            // tunnel's own address.
            var upstreamDns = NetworkProbe.GetUpstreamDnsServers();
            Log.App($"Upstream DNS for excluded traffic: {string.Join(", ", upstreamDns)}");
            Log.App($"Default interface before the tunnel: {NetworkProbe.GetDefaultInterfaceName() ?? "unknown"}");

            _singBox = new SingBoxRunner(_job);
            _singBox.Exited += OnSingBoxExited;
            await _singBox.StartAsync(Settings, binaries, endpoints, excluded, upstreamDns, token).ConfigureAwait(false);

            SetState(VpnState.Connected, null);

            StartStatsLoop(token);
            _ = VerifyTunnelAsync(endpoints, token);
        }
        catch (OperationCanceledException)
        {
            Log.App("Connect was cancelled");
            await TearDownAsync().ConfigureAwait(false);
            SetState(VpnState.Disconnected, null);
        }
        catch (Exception ex)
        {
            Log.Error("Connect failed", ex);
            await TearDownAsync().ConfigureAwait(false);
            SetState(VpnState.Failed, ex.Message);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _transitionGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (State == VpnState.Disconnected)
            {
                return;
            }

            SetState(VpnState.Disconnecting, null);
            await TearDownAsync().ConfigureAwait(false);
            SetState(VpnState.Disconnected, null);
        }
        catch (Exception ex)
        {
            Log.Error("Disconnect failed", ex);
            SetState(VpnState.Failed, ex.Message);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Asks Tor for fresh circuits, then re-checks the exit address.</summary>
    public async Task<bool> NewIdentityAsync()
    {
        var control = _tor?.Control;
        if (control is not { IsConnected: true })
        {
            return false;
        }

        var ok = await control.NewIdentityAsync().ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        Log.App("Requested a new circuit");

        _exit = null;
        RaiseStatus();

        var token = _sessionCts?.Token ?? CancellationToken.None;

        // Existing circuits are kept for connections already open, so the new exit only shows up
        // once a fresh circuit is built. A short pause avoids reporting the old address again.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        await RefreshExitInfoAsync(token).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Confirms that traffic really is flowing through the tunnel, and explains it when it is not.
    ///
    /// Two requests are made. The first goes to Tor's SOCKS port directly, which does not touch the
    /// tunnel; the second goes out with no proxy at all, so it travels the machine's default route
    /// and therefore the tunnel. If the first succeeds and the second does not, Tor is fine and
    /// something below routing is dropping the tunnel's packets, which in practice is another VPN
    /// client's kill switch.
    /// </summary>
    private async Task VerifyTunnelAsync(SessionEndpoints endpoints, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshExitInfoAsync(cancellationToken).ConfigureAwait(false);

            var throughTunnel = await ReachableWithoutProxyAsync(cancellationToken).ConfigureAwait(false);
            if (throughTunnel)
            {
                Log.App("Verified: traffic with no proxy configured reaches the internet through the tunnel");
                return;
            }

            if (_exit is null)
            {
                Log.App("Neither the direct path nor Tor's SOCKS port could reach the internet yet");
                SetState(VpnState.Connected, TunnelUnverifiedMessage);
                return;
            }

            // Tor works, the tunnel does not: something is filtering below the routing layer.
            var cause = ConflictDetector.DescribeLikelyCause(Settings.TunInterfaceName);

            Log.App("Tor is reachable through its SOCKS port, but traffic sent through the tunnel is not getting out.");
            if (cause is not null)
            {
                Log.App(cause);
            }

            SetState(VpnState.Connected, cause ?? TunnelBlockedMessage);
        }
        catch (OperationCanceledException)
        {
            // The session ended while verifying.
        }
        catch (Exception ex)
        {
            Log.Error("Verifying the tunnel failed", ex);
        }
    }

    private const string TunnelUnverifiedMessage =
        "The tunnel is up but nothing has been able to reach the internet through it yet.";

    private const string TunnelBlockedMessage =
        "Tor is working, but traffic sent through the tunnel is being dropped before it leaves the machine. " +
        "Another security product is filtering it.";

    private static async Task<bool> ReachableWithoutProxyAsync(CancellationToken cancellationToken)
    {
        // Addressed by IP so a broken resolver cannot be mistaken for a blocked tunnel. One.one.one.one
        // answers TLS on 1.1.1.1 and is reachable from Tor exits.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var handler = new System.Net.Http.HttpClientHandler { UseProxy = false, Proxy = null };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TorVpnForWindows/1.0");

                using var response = await http.GetAsync("https://1.1.1.1/", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode || (int)response.StatusCode < 500)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.App($"Tunnel verification attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return false;
    }

    public async Task RefreshExitInfoAsync(CancellationToken cancellationToken)
    {
        var endpoints = _tor?.Endpoints;
        if (endpoints is null)
        {
            return;
        }

        try
        {
            var info = await ExitIpChecker.QueryAsync(endpoints.SocksPort, _tor?.Control, cancellationToken)
                .ConfigureAwait(false);

            if (info is not null)
            {
                _exit = info;
                RaiseStatus();
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended while the check was in flight.
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the exit address failed", ex);
        }
    }

    private void StartStatsLoop(CancellationToken cancellationToken)
    {
        _statsTask = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                    var control = _tor?.Control;
                    if (control is not { IsConnected: true })
                    {
                        continue;
                    }

                    var read = await control.GetTrafficAsync(read: true, cancellationToken).ConfigureAwait(false);
                    var written = await control.GetTrafficAsync(read: false, cancellationToken).ConfigureAwait(false);

                    if (read != BytesRead || written != BytesWritten)
                    {
                        BytesRead = read;
                        BytesWritten = written;
                        TrafficChanged?.Invoke(read, written);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Reading the traffic counters failed", ex);
                    await Task.Delay(5000, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
    }

    private void OnBootstrapChanged(BootstrapStatus status)
    {
        _bootstrapProgress = status.Progress;
        _bootstrapSummary = status.Summary;

        if (State is VpnState.Preparing or VpnState.Bootstrapping)
        {
            RaiseStatus();
        }
    }

    private void OnTorExited(int exitCode)
    {
        if (State is VpnState.Disconnecting or VpnState.Disconnected)
        {
            return;
        }

        Log.App($"Tor stopped unexpectedly (exit code {exitCode})");

        if (Settings.KillSwitch && _singBox is { IsRunning: true })
        {
            // The tunnel stays up on purpose. Its routes still own the default route, so traffic
            // fails instead of quietly falling back to the unprotected connection.
            SetState(VpnState.Interrupted, "Tor stopped. Traffic is blocked by the kill switch.");
            return;
        }

        _ = Task.Run(async () =>
        {
            await TearDownAsync().ConfigureAwait(false);
            SetState(VpnState.Failed, $"Tor stopped unexpectedly (exit code {exitCode}).");
        });
    }

    private void OnSingBoxExited(int exitCode)
    {
        if (State is VpnState.Disconnecting or VpnState.Disconnected)
        {
            return;
        }

        // While the tunnel is still being set up, StartAsync already turns this into an exception
        // and the connect path handles it. Reacting here as well would run a second teardown
        // alongside the first.
        if (State is VpnState.EstablishingTunnel or VpnState.Preparing or VpnState.Bootstrapping)
        {
            Log.App($"The tunnel exited while starting (exit code {exitCode}); the connect path is handling it");
            return;
        }

        Log.App($"The tunnel stopped unexpectedly (exit code {exitCode})");

        // Without sing-box there is no tunnel and no way to hold traffic back, so the session is
        // shut down completely rather than left in a state that looks protected but is not.
        _ = Task.Run(async () =>
        {
            await TearDownAsync().ConfigureAwait(false);
            SetState(VpnState.Failed, $"The tunnel stopped unexpectedly (exit code {exitCode}).");
        });
    }

    private readonly SemaphoreSlim _teardownGate = new(1, 1);

    /// <summary>
    /// Brings the session down. Serialized because it can be reached from the connect path and from
    /// a child process exiting at the same time, and two teardowns racing each other used to trip
    /// over the half-disposed control connection.
    /// </summary>
    private async Task TearDownAsync()
    {
        await _teardownGate.WaitAsync().ConfigureAwait(false);

        try
        {
            await TearDownCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _teardownGate.Release();
        }
    }

    private async Task TearDownCoreAsync()
    {
        try
        {
            if (_sessionCts is not null)
            {
                await _sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Cancelling the session failed", ex);
        }

        // The tunnel goes first: while it is up the routes are in place, so nothing can escape
        // during the window where Tor is already gone.
        if (_singBox is not null)
        {
            _singBox.Exited -= OnSingBoxExited;
            await _singBox.DisposeAsync().ConfigureAwait(false);
            _singBox = null;
        }

        if (_tor is not null)
        {
            _tor.BootstrapChanged -= OnBootstrapChanged;
            _tor.Exited -= OnTorExited;
            await _tor.DisposeAsync().ConfigureAwait(false);
            _tor = null;
        }

        if (_statsTask is not null)
        {
            try
            {
                await _statsTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The loop is already unwinding.
            }
            catch (Exception ex)
            {
                Log.Error("Waiting for the statistics loop failed", ex);
            }

            _statsTask = null;
        }

        _sessionCts?.Dispose();
        _sessionCts = null;

        ChildProcessRegistry.Clear();

        BytesRead = 0;
        BytesWritten = 0;
        _exit = null;
        _bootstrapProgress = 0;
        _bootstrapSummary = null;
    }

    private void SetState(VpnState state, string? message)
    {
        State = state;
        _message = message;
        RaiseStatus();
        Log.App($"State: {state}{(message is null ? string.Empty : $" ({message})")}");
    }

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(CurrentStatus);
        }
        catch (Exception ex)
        {
            Log.Error("A StatusChanged handler threw", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        _job.Dispose();
        _transitionGate.Dispose();
        _teardownGate.Dispose();
    }
}
