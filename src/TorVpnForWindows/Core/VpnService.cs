using TorVpnForWindows.Config;

namespace TorVpnForWindows.Core;

public enum VpnState
{
    Disconnected,

    /// <summary>
    /// A connection is wanted but no network is up. Tor is not started until one is, and the block
    /// stays on meanwhile if the kill switch setting asks for it.
    /// </summary>
    WaitingForNetwork,

    /// <summary>Unpacking the runtime and starting Tor.</summary>
    Preparing,

    /// <summary>Tor is building its first circuits.</summary>
    Bootstrapping,

    /// <summary>Tor is ready; the TUN adapter is being created and the routes moved.</summary>
    EstablishingTunnel,

    Connected,

    Disconnecting,

    /// <summary>
    /// The session broke and is being started over. With the kill switch on, traffic stays blocked
    /// until the new session is up.
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
/// Traffic Tor has carried this session, and how fast it is moving right now.
///
/// The totals come from Tor's own counters, so they include its protocol overhead rather than only
/// the payload an application sees. The rates are the change between two readings a second apart.
/// </summary>
public sealed record TrafficSnapshot(
    long TotalRead,
    long TotalWritten,
    double ReadPerSecond,
    double WritePerSecond)
{
    public static readonly TrafficSnapshot Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Drives the whole session: Tor first, then the tunnel, then the exit check. Everything the user
/// interface shows comes from here.
///
/// A single supervisor loop owns the session for as long as a connection is wanted. It starts an
/// attempt, and whenever that attempt ends, for whatever reason, it tears it down and starts the
/// next one. Everything that used to restart the session on its own path now asks the supervisor
/// instead: the retry button, a child process exiting, the health check.
///
/// It is built this way because the separate paths fought each other. A retry pressed while Tor was
/// still bootstrapping cancelled the connect, and the connect's own cancellation handler took the
/// kill switch down before the retry put it back up, so for a moment the machine was online without
/// Tor. A disconnect pressed during the same bootstrap waited for the connect to finish, which with
/// no network it never did. With one owner, a restart cancels the attempt and the block is left
/// exactly as it was; only an explicit disconnect removes it.
/// </summary>
public sealed class VpnService : IAsyncDisposable
{
    private readonly JobObject _job = new();
    private readonly KillSwitchGuard _killSwitch = new();
    private readonly NetworkWatcher _network;

    /// <summary>Serializes connect, disconnect and retry against each other.</summary>
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private readonly SemaphoreSlim _teardownGate = new(1, 1);

    /// <summary>Guards the attempt token and the reason it is being ended.</summary>
    private readonly Lock _attemptGate = new();

    /// <summary>True while the kill switch is holding traffic back after an unplanned drop.</summary>
    public bool TrafficBlocked => _killSwitch.IsArmed && State != VpnState.Connected;

    private volatile bool _wantConnected;
    private Task? _supervisor;
    private CancellationTokenSource? _attemptCts;
    private AttemptEnd _pendingEnd;
    private string? _pendingReason;

    /// <summary>Failed attempts in a row, which sets how long the supervisor waits before the next.</summary>
    private int _consecutiveFailures;

    private TorRunner? _tor;
    private SingBoxRunner? _singBox;
    private Task? _statsTask;

    private int _bootstrapProgress;
    private string? _bootstrapSummary;
    private ExitInfo? _exit;
    private string? _message;

    public VpnState State { get; private set; } = VpnState.Disconnected;

    public AppSettings Settings { get; }

    public long BytesRead { get; private set; }

    public long BytesWritten { get; private set; }

    public TrafficSnapshot Traffic { get; private set; } = TrafficSnapshot.Empty;

    public SessionEndpoints? Endpoints => _tor?.Endpoints;

    public event Action<VpnStatus>? StatusChanged;

    public event Action<TrafficSnapshot>? TrafficChanged;

    public VpnService(AppSettings settings)
    {
        Settings = settings;

        _network = new NetworkWatcher(() => Settings.TunInterfaceName);
        _network.Changed += OnNetworkChanged;
    }

    public VpnStatus CurrentStatus =>
        new(State, _bootstrapProgress, _bootstrapSummary, _exit, _message);

    /// <summary>
    /// Whether the user has asked for a connection and not yet asked to disconnect. While this is
    /// true the supervisor keeps working on a connection, whatever state the current attempt is in.
    /// </summary>
    public bool WantsConnection => _wantConnected;

    /// <summary>
    /// How an attempt is being ended. Ordered by precedence: a stop outranks a restart, and a restart
    /// outranks a failure, because what the user asked for is what counts.
    /// </summary>
    private enum AttemptEnd
    {
        None,

        /// <summary>Something went wrong on its own. The next attempt waits a little.</summary>
        Failure,

        /// <summary>Start over now: the retry button, or a change that makes the current attempt stale.</summary>
        Restart,

        /// <summary>Disconnect or exit. No next attempt.</summary>
        Stop
    }

    /// <summary>
    /// Blocks traffic straight away, before anything is connected, and leaves it blocked.
    ///
    /// Called at startup so the intended sequence works: open the application with the network
    /// down, bring the network up, and nothing reaches it until Tor is carrying the traffic.
    /// Arming only when connect is pressed left that whole window open, and a machine that joins a
    /// network before the user presses anything went out in the clear.
    /// </summary>
    public bool ArmStandbyBlock()
    {
        if (!Settings.KillSwitch)
        {
            return false;
        }

        try
        {
            PayloadExtractor.EnsureExtracted();

            var binaries = Binaries.Resolve();
            var excluded = ExclusionList.Read();

            if (!_killSwitch.Arm(KillSwitchGuard.BuildPermitList(binaries, excluded)))
            {
                Log.App("The kill switch could not be armed at startup; traffic is not being blocked");
                return false;
            }

            Log.App("Traffic is blocked until Tor is connected");
            RaiseStatus();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Arming the kill switch at startup failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Asks for a connection. Returns as soon as the supervisor is running; the state changes report
    /// how it goes from there.
    /// </summary>
    public async Task ConnectAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);

        try
        {
            StartSupervisor();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Stops whatever is happening and starts over from nothing: Tor, its bridges and the tunnel.
    /// Available in every state and as often as it is pressed. With the kill switch on, traffic stays
    /// blocked the whole time; with it off, nothing is blocked.
    /// </summary>
    public async Task RetryAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_supervisor is { IsCompleted: false } && _wantConnected)
            {
                EndAttempt(AttemptEnd.Restart, "Restarting the connection.");
                return;
            }

            StartSupervisor();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Starts the session over if a connection is wanted. Used when something the running session was
    /// built from has changed, so that the change takes effect now instead of on the next connect.
    /// </summary>
    public void RequestRestart(string reason)
    {
        if (!_wantConnected)
        {
            return;
        }

        EndAttempt(AttemptEnd.Restart, reason);
    }

    public async Task DisconnectAsync()
    {
        await _commandGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!_wantConnected && _supervisor is null && State == VpnState.Disconnected)
            {
                return;
            }

            await StopSupervisorAsync().ConfigureAwait(false);

            // An explicit disconnect is the one case where the block comes off.
            _killSwitch.Disarm();
            SetState(VpnState.Disconnected, null);
        }
        catch (Exception ex)
        {
            Log.Error("Disconnect failed", ex);
            SetState(VpnState.Failed, ex.Message);
        }
        finally
        {
            _commandGate.Release();
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

        var token = CurrentAttemptToken();

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

    // ------------------------------------------------------------------ supervisor

    private void StartSupervisor()
    {
        _wantConnected = true;

        if (_supervisor is { IsCompleted: false })
        {
            return;
        }

        lock (_attemptGate)
        {
            _pendingEnd = AttemptEnd.None;
            _pendingReason = null;
        }

        _consecutiveFailures = 0;
        _supervisor = Task.Run(SuperviseAsync);
    }

    private async Task StopSupervisorAsync()
    {
        _wantConnected = false;

        var supervisor = _supervisor;

        if (supervisor is not null && !supervisor.IsCompleted)
        {
            SetState(VpnState.Disconnecting, null);
            EndAttempt(AttemptEnd.Stop, null);

            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("The connection supervisor failed while stopping", ex);
            }
        }

        _supervisor = null;

        // Nothing should be left, but a supervisor that died on an unexpected exception may not have
        // reached its own teardown.
        await TearDownSessionAsync().ConfigureAwait(false);
    }

    private async Task SuperviseAsync()
    {
        var delay = TimeSpan.Zero;

        try
        {
            while (_wantConnected)
            {
                var token = BeginAttempt();
                string? message = null;

                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        Log.App($"Trying again in {delay.TotalSeconds:0} s");
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }

                    await RunSessionAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var (end, reason) = TakeAttemptEnd();

                    switch (end)
                    {
                        case AttemptEnd.Stop:
                            delay = TimeSpan.Zero;
                            break;

                        case AttemptEnd.Restart:
                            Log.App($"Starting the connection over: {reason}");
                            _consecutiveFailures = 0;
                            delay = TimeSpan.Zero;
                            message = reason;
                            break;

                        default:
                            message = reason ?? ex.Message;

                            if (reason is not null)
                            {
                                Log.App($"The session ended: {reason}");
                            }
                            else
                            {
                                Log.Error("The connection attempt failed", ex);
                            }

                            _consecutiveFailures++;
                            delay = RetryDelay(_consecutiveFailures);
                            break;
                    }
                }

                // Whatever ended the attempt, everything it started goes now: the statistics loop,
                // the tunnel verification and the bridge refresh all hang off this token.
                CancelCurrentAttempt();
                await TearDownSessionAsync().ConfigureAwait(false);

                if (_wantConnected)
                {
                    SetState(VpnState.Interrupted, message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("The connection supervisor stopped", ex);
        }
        finally
        {
            lock (_attemptGate)
            {
                _attemptCts?.Dispose();
                _attemptCts = null;
            }
        }
    }

    /// <summary>
    /// Waits a little longer after each failure in a row, so an attempt that cannot work (a missing
    /// executable, a bridge that is gone) does not spin, while the first retry still comes quickly.
    /// </summary>
    private static TimeSpan RetryDelay(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        4 => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(30)
    };

    private CancellationToken BeginAttempt()
    {
        lock (_attemptGate)
        {
            _attemptCts?.Dispose();
            _attemptCts = new CancellationTokenSource();

            // A restart asked for while the previous attempt was being torn down cancelled a token
            // nobody was listening to any more. Honouring it here keeps it from being lost.
            if (_pendingEnd != AttemptEnd.None)
            {
                _attemptCts.Cancel();
            }

            return _attemptCts.Token;
        }
    }

    private void EndAttempt(AttemptEnd end, string? reason)
    {
        CancellationTokenSource? attempt;

        lock (_attemptGate)
        {
            if (end >= _pendingEnd)
            {
                _pendingEnd = end;
                _pendingReason = reason;
            }

            attempt = _attemptCts;
        }

        try
        {
            attempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt ended in between; BeginAttempt picks the pending end up instead.
        }
    }

    private (AttemptEnd End, string? Reason) TakeAttemptEnd()
    {
        lock (_attemptGate)
        {
            var result = (_pendingEnd, _pendingReason);
            _pendingEnd = AttemptEnd.None;
            _pendingReason = null;
            return result;
        }
    }

    private void CancelCurrentAttempt()
    {
        CancellationTokenSource? attempt;

        lock (_attemptGate)
        {
            attempt = _attemptCts;
        }

        try
        {
            attempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone, which is what was wanted.
        }
    }

    private CancellationToken CurrentAttemptToken()
    {
        lock (_attemptGate)
        {
            try
            {
                return _attemptCts?.Token ?? CancellationToken.None;
            }
            catch (ObjectDisposedException)
            {
                return CancellationToken.None;
            }
        }
    }

    // ------------------------------------------------------------------ one attempt

    /// <summary>
    /// Brings a session up and keeps it running until it stops carrying traffic, in which case it
    /// throws, or until the attempt is cancelled.
    /// </summary>
    private async Task RunSessionAsync(CancellationToken token)
    {
        _exit = null;
        _bootstrapProgress = 0;
        _bootstrapSummary = null;
        SetState(VpnState.Preparing, null);

        ChildProcessRegistry.KillLeftovers();
        PayloadExtractor.EnsureExtracted();

        // Resolved per attempt so a tool installed or updated since the last session is used.
        var binaries = Binaries.Resolve();
        foreach (var line in binaries.Describe())
        {
            Log.App(line);
        }

        var excluded = ExclusionList.Read();
        if (excluded.Count > 0)
        {
            Log.App($"Excluded from the tunnel: {string.Join(", ", excluded)}");
        }

        ApplyKillSwitchSetting(binaries, excluded);

        await WaitForNetworkAsync(token).ConfigureAwait(false);

        _tor = new TorRunner(_job);
        _tor.BootstrapChanged += OnBootstrapChanged;
        _tor.Exited += OnTorExited;
        _tor.ProcessStarted = pid =>
            _killSwitch.PermitProcessTreeAsync(pid, TimeSpan.FromSeconds(6), token);

        SetState(VpnState.Bootstrapping, null);
        await _tor.StartAsync(Settings, binaries, token).ConfigureAwait(false);
        await _tor.WaitForBootstrapAsync(token).ConfigureAwait(false);

        Log.App("Tor finished bootstrapping");

        var endpoints = _tor.Endpoints
            ?? throw new InvalidOperationException("Tor started without resolving its ports.");

        SetState(VpnState.EstablishingTunnel, null);

        // Read the machine's resolvers before the TUN takes over, otherwise the answer is the
        // tunnel's own address.
        var upstreamDns = NetworkProbe.GetUpstreamDnsServers();
        Log.App($"Upstream DNS for excluded traffic: {string.Join(", ", upstreamDns)}");
        Log.App($"Default interface before the tunnel: {NetworkProbe.GetDefaultInterfaceName() ?? "unknown"}");

        _singBox = new SingBoxRunner(_job);
        _singBox.Exited += OnSingBoxExited;
        await _singBox.StartAsync(Settings, binaries, endpoints, excluded, upstreamDns, token).ConfigureAwait(false);

        // Let traffic out again, but only through the tunnel adapter.
        if (_killSwitch.IsArmed)
        {
            var index = NetworkProbe.GetInterfaceIndex(Settings.TunInterfaceName);
            if (index is null)
            {
                throw new InvalidOperationException(
                    $"The tunnel adapter {Settings.TunInterfaceName} has no interface index, so the kill switch " +
                    "cannot be opened for it. Traffic would stay blocked.");
            }

            _killSwitch.OpenTunnel(index.Value);
        }

        token.ThrowIfCancellationRequested();

        _consecutiveFailures = 0;
        SetState(VpnState.Connected, null);

        StartStatsLoop(token);
        _ = VerifyTunnelAsync(endpoints, token);

        // Now that there is a connection, the bridge list can be refreshed from inside it. It
        // is never fetched before this point, because doing so would name the Tor Project on an
        // unprotected connection, which is what a bridge exists to avoid.
        _ = BridgeProvider.RefreshThroughTorAsync(Settings, endpoints.SocksPort, token);

        await MonitorHealthAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the attempt until a network is up.
    ///
    /// Starting Tor with no network only produces failures that Tor then takes its time to retry,
    /// which is how a session started with the Wi-Fi off stayed stuck after the Wi-Fi came up. The
    /// network watcher starts the attempt over the moment a network settles; the poll here is only a
    /// backstop in case that event never arrives.
    /// </summary>
    private async Task WaitForNetworkAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (_network.HasUsableNetwork)
        {
            return;
        }

        Log.App("No network is up; waiting for one before starting Tor");
        SetState(VpnState.WaitingForNetwork, null);

        while (!_network.HasUsableNetwork)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }

        Log.App("A network is up");
        SetState(VpnState.Preparing, null);
    }

    /// <summary>
    /// Starts the session over whenever the real network settles into a different shape: a network
    /// arriving, leaving, or coming back after a drop. Whatever Tor had open on the old one is gone
    /// either way, and a fresh start reaches the bridges immediately instead of on Tor's retry
    /// schedule.
    /// </summary>
    private void OnNetworkChanged(bool usable)
    {
        if (!_wantConnected)
        {
            return;
        }

        EndAttempt(AttemptEnd.Restart, usable ? "The network changed." : "The network went away.");
    }

    /// <summary>
    /// Puts the block on when the setting asks for it, and takes a block that is no longer wanted
    /// back off. Checked at the start of every attempt, so changing the setting takes effect the next
    /// time the session starts over.
    /// </summary>
    private void ApplyKillSwitchSetting(Binaries binaries, IReadOnlyList<string> excluded)
    {
        if (Settings.KillSwitch)
        {
            // The block goes on before Tor even starts. Everything except Tor's own processes and
            // the excluded applications is cut off from here until the tunnel is up, and stays cut
            // off if it later drops.
            if (!_killSwitch.Arm(KillSwitchGuard.BuildPermitList(binaries, excluded)))
            {
                Log.App("The kill switch could not be armed; continuing without it");
            }

            return;
        }

        if (_killSwitch.IsArmed)
        {
            Log.App("The kill switch setting is off; removing the block");
            _killSwitch.Disarm();
        }
    }

    /// <summary>
    /// Watches a live session and returns control to the supervisor, by throwing, the moment it
    /// stops carrying traffic.
    ///
    /// Without this the application reported Connected for as long as it was left alone. On one run
    /// the underlying network went away four minutes after connecting; Tor could no longer reach a
    /// single relay, and the session sat there showing green for four hours and forty minutes while
    /// nothing worked. Tor knows whether it has a usable circuit, so it is asked.
    /// </summary>
    private async Task MonitorHealthAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            // Asking Tor is not enough on its own. When the machine loses its address the adapter
            // stays up, Tor keeps reporting an established circuit from the ones it already had, and
            // the session sits there green while nothing reaches the network. sing-box notices
            // immediately, because every connection it tries to open has nowhere to go, so its
            // complaint is counted and treated as the failure it is.
            var routeFailures = _singBox?.TakeRouteFailures() ?? 0;

            if (routeFailures >= RouteFailuresBeforeGivingUp)
            {
                throw new InvalidOperationException(
                    $"The tunnel has no way out to the network ({routeFailures} failed connection(s) " +
                    "in the last fifteen seconds).");
            }

            var control = _tor?.Control;

            var healthy = control is { IsConnected: true } &&
                          await control.GetInfoAsync("status/circuit-established", cancellationToken)
                              .ConfigureAwait(false) == "1";

            if (healthy)
            {
                if (consecutiveFailures > 0)
                {
                    Log.App("Tor has a usable circuit again");
                }

                consecutiveFailures = 0;
                continue;
            }

            consecutiveFailures++;
            Log.App($"Tor reports no usable circuit ({consecutiveFailures} check(s) in a row)");

            if (consecutiveFailures >= HealthFailuresBeforeGivingUp)
            {
                throw new InvalidOperationException("The connection stopped carrying traffic.");
            }
        }
    }

    /// <summary>Three checks fifteen seconds apart, so a brief hiccup is not treated as a failure.</summary>
    private const int HealthFailuresBeforeGivingUp = 3;

    /// <summary>
    /// A single relay that cannot be reached is normal and produces one of these. A machine with no
    /// route produces hundreds a minute, so the line between the two is not a fine one.
    /// </summary>
    private const int RouteFailuresBeforeGivingUp = 10;

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
                SetConnectedMessage(TunnelUnverifiedMessage, cancellationToken);
                return;
            }

            // Tor works, the tunnel does not: something is filtering below the routing layer.
            var cause = ConflictDetector.DescribeLikelyCause(Settings.TunInterfaceName);

            Log.App("Tor is reachable through its SOCKS port, but traffic sent through the tunnel is not getting out.");
            if (cause is not null)
            {
                Log.App(cause);
            }

            SetConnectedMessage(cause ?? TunnelBlockedMessage, cancellationToken);
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

    /// <summary>
    /// Attaches a warning to the Connected state, unless the session has already moved on. The
    /// verification runs in the background, and reporting Connected over a session that is being
    /// torn down would put a green light on a tunnel that no longer exists.
    /// </summary>
    private void SetConnectedMessage(string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || State != VpnState.Connected)
        {
            return;
        }

        SetState(VpnState.Connected, message);
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

            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
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

            if (info is not null && !cancellationToken.IsCancellationRequested)
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
            var lastRead = 0L;
            var lastWritten = 0L;
            var lastAt = DateTime.UtcNow;
            var haveBaseline = false;

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
                    var now = DateTime.UtcNow;

                    // The rate needs two readings, so the first pass only records where to measure
                    // from. Reporting a rate against a zero baseline would show the whole session's
                    // traffic as if it had all arrived in one second.
                    var elapsed = (now - lastAt).TotalSeconds;
                    var readRate = 0d;
                    var writeRate = 0d;

                    if (haveBaseline && elapsed > 0.1)
                    {
                        readRate = Math.Max(0, read - lastRead) / elapsed;
                        writeRate = Math.Max(0, written - lastWritten) / elapsed;
                    }

                    lastRead = read;
                    lastWritten = written;
                    lastAt = now;
                    haveBaseline = true;

                    BytesRead = read;
                    BytesWritten = written;
                    Traffic = new TrafficSnapshot(read, written, readRate, writeRate);

                    TrafficChanged?.Invoke(Traffic);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Reading the traffic counters failed", ex);

                    try
                    {
                        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
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
        Log.App($"Tor stopped unexpectedly (exit code {exitCode})");
        EndAttempt(AttemptEnd.Failure, $"Tor stopped unexpectedly (exit code {exitCode}).");
    }

    private void OnSingBoxExited(int exitCode)
    {
        Log.App($"The tunnel stopped unexpectedly (exit code {exitCode})");
        EndAttempt(AttemptEnd.Failure, $"The tunnel stopped unexpectedly (exit code {exitCode}).");
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>
    /// Brings the session down and leaves the kill switch exactly as it is. Serialized because it
    /// can be reached from the supervisor and from stopping at the same time, and two teardowns
    /// racing each other used to trip over the half-disposed control connection.
    /// </summary>
    private async Task TearDownSessionAsync()
    {
        await _teardownGate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Revoked first: the moment the tunnel is going away, nothing should be allowed out
            // through it any more.
            _killSwitch.CloseTunnel();

            await TearDownCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _teardownGate.Release();
        }
    }

    private async Task TearDownCoreAsync()
    {
        // The tunnel goes first: while it is up the routes are in place, so nothing can escape
        // during the window where Tor is already gone.
        var singBox = Interlocked.Exchange(ref _singBox, null);
        if (singBox is not null)
        {
            singBox.Exited -= OnSingBoxExited;
            await singBox.DisposeAsync().ConfigureAwait(false);
        }

        var tor = Interlocked.Exchange(ref _tor, null);
        if (tor is not null)
        {
            tor.BootstrapChanged -= OnBootstrapChanged;
            tor.Exited -= OnTorExited;
            await tor.DisposeAsync().ConfigureAwait(false);
        }

        var statsTask = Interlocked.Exchange(ref _statsTask, null);
        if (statsTask is not null)
        {
            try
            {
                await statsTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The loop is already unwinding.
            }
            catch (Exception ex)
            {
                Log.Error("Waiting for the statistics loop failed", ex);
            }
        }

        // The bridge names were only put in the hosts file so the transports could start without
        // asking the network. Nothing needs them once the session is over.
        BridgeNameResolver.Clear();

        ChildProcessRegistry.Clear();

        BytesRead = 0;
        BytesWritten = 0;
        Traffic = TrafficSnapshot.Empty;
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
        _network.Changed -= OnNetworkChanged;
        _network.Dispose();

        try
        {
            await StopSupervisorAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the session during shutdown failed", ex);
        }

        _killSwitch.Dispose();
        _job.Dispose();
        _commandGate.Dispose();
        _teardownGate.Dispose();
    }
}
