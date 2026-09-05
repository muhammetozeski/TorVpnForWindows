using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TorVpnForWindows.Core;

/// <summary>
/// Blocks all outbound traffic at the Windows Filtering Platform, permitting only what the tunnel
/// needs, so nothing can leave the machine unless it goes through Tor.
///
/// Routing alone is not enough for a kill switch. A program that binds to a specific adapter, or
/// anything that installs a more specific route, travels around the tunnel without ever consulting
/// the default route. These filters sit at the connect layer, below routing, so that cannot happen.
///
/// The engine session is opened as dynamic: if this process dies for any reason, Windows removes
/// every filter it added. A crash therefore restores the machine's networking rather than leaving
/// it cut off.
/// </summary>
public sealed class KillSwitchGuard : IDisposable
{
    // Layers. Outbound connections are authorised here, before any routing decision is applied.
    private static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    // Conditions.
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionInterfaceIndex = new("667fd755-d695-434a-8af5-d3835a1259bc");

    private const uint ConditionFlagIsLoopback = 0x00000001;

    private const uint SessionFlagDynamic = 0x00000001;
    private const uint ActionBlock = 0x00001001;
    private const uint ActionPermit = 0x00001002;

    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;

    // FWP_DATA_TYPE. These have to be exact: the value is a tagged union, so a wrong tag makes WFP
    // read the payload as the wrong kind. Tagging a plain number as FWP_UINT64, whose union member
    // is a pointer, makes it dereference the number itself and take the process down with an access
    // violation.
    private const uint TypeUInt8 = 1;
    private const uint TypeUInt32 = 3;
    private const uint TypeByteBlob = 12;

    private const uint ErrorSuccess = 0;

    // Weights inside our own sublayer. The block sits at the bottom; every permit outranks it.
    private const byte WeightBlock = 1;
    private const byte WeightPermitLoopback = 8;
    private const byte WeightPermitTunnel = 9;
    private const byte WeightPermitApp = 12;

    private readonly Guid _subLayerKey = Guid.NewGuid();
    private readonly List<ulong> _tunnelFilterIds = [];
    private readonly Lock _gate = new();

    private nint _engine;
    private bool _armed;
    private bool _disposed;

    /// <summary>True while everything except the permitted traffic is blocked.</summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _armed;
            }
        }
    }

    /// <summary>
    /// Starts blocking. Traffic from the given executables is permitted so Tor itself can reach the
    /// network and build the circuits the rest of the machine is waiting for.
    /// </summary>
    public bool Arm(IReadOnlyList<string> permittedExecutables)
    {
        lock (_gate)
        {
            if (_armed)
            {
                return true;
            }

            try
            {
                VerifyStructureSizes();

                OpenEngine();
                AddSubLayer();

                // Order does not matter to WFP, only weight, but the block goes in first so a
                // failure part way through leaves the machine blocked rather than half open.
                AddBlockFilter(LayerAleAuthConnectV4, "block all IPv4");
                AddBlockFilter(LayerAleAuthConnectV6, "block all IPv6");

                AddLoopbackPermit(LayerAleAuthConnectV4, "permit IPv4 loopback");
                AddLoopbackPermit(LayerAleAuthConnectV6, "permit IPv6 loopback");

                foreach (var executable in permittedExecutables)
                {
                    AddApplicationPermit(executable);
                }

                _armed = true;
                Log.App($"Kill switch armed; {permittedExecutables.Count} executable(s) permitted");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Arming the kill switch failed", ex);

                try
                {
                    DisarmCore();
                }
                catch (Exception cleanupEx)
                {
                    Log.Error("Cleaning up after a failed arm also failed", cleanupEx);
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Opens the block for the tunnel adapter once it exists. Called when the tunnel comes up, and
    /// reversed by <see cref="CloseTunnel"/> when it goes down, which is what makes the block bite
    /// again the moment Tor stops.
    /// </summary>
    public bool OpenTunnel(int interfaceIndex)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return false;
            }

            try
            {
                CloseTunnelCore();

                _tunnelFilterIds.Add(AddInterfacePermit(LayerAleAuthConnectV4, interfaceIndex, "permit IPv4 on the tunnel"));
                _tunnelFilterIds.Add(AddInterfacePermit(LayerAleAuthConnectV6, interfaceIndex, "permit IPv6 on the tunnel"));

                Log.App($"Kill switch: traffic allowed on interface {interfaceIndex}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Permitting the tunnel adapter failed", ex);
                return false;
            }
        }
    }

    /// <summary>Revokes the tunnel permit, so only Tor's own processes can still reach the network.</summary>
    public void CloseTunnel()
    {
        lock (_gate)
        {
            try
            {
                if (CloseTunnelCore())
                {
                    Log.App("Kill switch: the tunnel permit was revoked, traffic is blocked again");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Revoking the tunnel permit failed", ex);
            }
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            try
            {
                DisarmCore();
            }
            catch (Exception ex)
            {
                Log.Error("Disarming the kill switch failed", ex);
            }
        }
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Compares the marshalled sizes against the layout the 64-bit headers describe.
    ///
    /// A field of the wrong width here does not fail cleanly; it shifts everything after it and
    /// hands WFP a pointer built from the wrong bytes, which ends the process with an access
    /// violation and no managed stack. Checking first turns that into a readable message.
    /// </summary>
    private static void VerifyStructureSizes()
    {
        (string Name, int Actual, int Expected)[] sizes =
        [
            (nameof(FwpmDisplayData0), Marshal.SizeOf<FwpmDisplayData0>(), 16),
            (nameof(FwpByteBlob), Marshal.SizeOf<FwpByteBlob>(), 16),
            (nameof(FwpValue0), Marshal.SizeOf<FwpValue0>(), 16),
            (nameof(FwpConditionValue0), Marshal.SizeOf<FwpConditionValue0>(), 16),
            (nameof(FwpmFilterCondition0), Marshal.SizeOf<FwpmFilterCondition0>(), 40),
            (nameof(FwpmAction0), Marshal.SizeOf<FwpmAction0>(), 20),
            // 66 bytes of fields, rounded up to 72 by the eight byte alignment the pointers impose.
            (nameof(FwpmSubLayer0), Marshal.SizeOf<FwpmSubLayer0>(), 72),
            (nameof(FwpmFilter0), Marshal.SizeOf<FwpmFilter0>(), 200)
        ];

        var wrong = sizes.Where(s => s.Actual != s.Expected).ToArray();

        if (wrong.Length > 0)
        {
            var detail = string.Join(", ", wrong.Select(w => $"{w.Name} is {w.Actual}, expected {w.Expected}"));
            throw new InvalidOperationException($"The kill switch structure layout is wrong: {detail}.");
        }
    }

    private void OpenEngine()
    {
        if (_engine != nint.Zero)
        {
            return;
        }

        var session = new FwpmSession0
        {
            Flags = SessionFlagDynamic,
            DisplayData = new FwpmDisplayData0
            {
                Name = Marshal.StringToHGlobalUni("Tor VPN for Windows"),
                Description = Marshal.StringToHGlobalUni("Kill switch filters, removed when the process exits.")
            }
        };

        try
        {
            var result = FwpmEngineOpen0(null, 10 /* RPC_C_AUTHN_WINNT */, nint.Zero, ref session, out _engine);
            if (result != ErrorSuccess)
            {
                throw new InvalidOperationException($"FwpmEngineOpen0 failed with 0x{result:X8}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(session.DisplayData.Name);
            Marshal.FreeHGlobal(session.DisplayData.Description);
        }
    }

    private void AddSubLayer()
    {
        var namePtr = Marshal.StringToHGlobalUni("Tor VPN for Windows");
        var descPtr = Marshal.StringToHGlobalUni("Kill switch");

        try
        {
            var subLayer = new FwpmSubLayer0
            {
                SubLayerKey = _subLayerKey,
                DisplayData = new FwpmDisplayData0 { Name = namePtr, Description = descPtr },
                Weight = 0xFFFF
            };

            var result = FwpmSubLayerAdd0(_engine, ref subLayer, nint.Zero);
            if (result != ErrorSuccess)
            {
                throw new InvalidOperationException($"FwpmSubLayerAdd0 failed with 0x{result:X8}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(descPtr);
        }
    }

    private ulong AddBlockFilter(Guid layer, string description) =>
        AddFilter(layer, ActionBlock, WeightBlock, description, []);

    private ulong AddLoopbackPermit(Guid layer, string description)
    {
        // A 32 bit condition value lives inside the union, not behind a pointer.
        var condition = new FwpmFilterCondition0
        {
            FieldKey = ConditionFlags,
            MatchType = MatchFlagsAllSet,
            ConditionValue = new FwpConditionValue0 { Type = TypeUInt32, Value = ConditionFlagIsLoopback }
        };

        return AddFilter(layer, ActionPermit, WeightPermitLoopback, description, [condition]);
    }

    private ulong AddInterfacePermit(Guid layer, int interfaceIndex, string description)
    {
        var condition = new FwpmFilterCondition0
        {
            FieldKey = ConditionInterfaceIndex,
            MatchType = MatchEqual,
            ConditionValue = new FwpConditionValue0 { Type = TypeUInt32, Value = (uint)interfaceIndex }
        };

        return AddFilter(layer, ActionPermit, WeightPermitTunnel, description, [condition]);
    }

    private void AddApplicationPermit(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            Log.App($"Kill switch: not permitting {executablePath}, the file does not exist");
            return;
        }

        var result = FwpmGetAppIdFromFileName0(executablePath, out var appIdPtr);
        if (result != ErrorSuccess || appIdPtr == nint.Zero)
        {
            Log.App($"Kill switch: could not build an application identifier for {executablePath} (0x{result:X8})");
            return;
        }

        try
        {
            // A byte blob, unlike a 32 bit number, is referenced by pointer.
            var condition = new FwpmFilterCondition0
            {
                FieldKey = ConditionAleAppId,
                MatchType = MatchEqual,
                ConditionValue = new FwpConditionValue0 { Type = TypeByteBlob, Value = (ulong)appIdPtr }
            };

            var name = Path.GetFileName(executablePath);
            AddFilter(LayerAleAuthConnectV4, ActionPermit, WeightPermitApp, $"permit IPv4 for {name}", [condition]);
            AddFilter(LayerAleAuthConnectV6, ActionPermit, WeightPermitApp, $"permit IPv6 for {name}", [condition]);
        }
        finally
        {
            FwpmFreeMemory0(ref appIdPtr);
        }
    }

    private ulong AddFilter(
        Guid layer,
        uint action,
        byte weight,
        string description,
        FwpmFilterCondition0[] conditions)
    {
        var namePtr = Marshal.StringToHGlobalUni("Tor VPN for Windows");
        var descPtr = Marshal.StringToHGlobalUni(description);

        var conditionSize = Marshal.SizeOf<FwpmFilterCondition0>();
        var conditionArray = conditions.Length == 0
            ? nint.Zero
            : Marshal.AllocHGlobal(conditionSize * conditions.Length);

        try
        {
            for (var i = 0; i < conditions.Length; i++)
            {
                Marshal.StructureToPtr(conditions[i], conditionArray + (i * conditionSize), fDeleteOld: false);
            }

            var filter = new FwpmFilter0
            {
                FilterKey = Guid.NewGuid(),
                DisplayData = new FwpmDisplayData0 { Name = namePtr, Description = descPtr },
                LayerKey = layer,
                SubLayerKey = _subLayerKey,
                Weight = new FwpValue0 { Type = TypeUInt8, Value = weight },
                NumFilterConditions = (uint)conditions.Length,
                FilterCondition = conditionArray,
                Action = new FwpmAction0 { Type = action }
            };

            var result = FwpmFilterAdd0(_engine, ref filter, nint.Zero, out var filterId);
            if (result != ErrorSuccess)
            {
                throw new InvalidOperationException($"FwpmFilterAdd0 ({description}) failed with 0x{result:X8}.");
            }

            return filterId;
        }
        finally
        {
            if (conditionArray != nint.Zero)
            {
                Marshal.FreeHGlobal(conditionArray);
            }

            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(descPtr);
        }
    }

    private bool CloseTunnelCore()
    {
        if (_tunnelFilterIds.Count == 0 || _engine == nint.Zero)
        {
            return false;
        }

        foreach (var id in _tunnelFilterIds)
        {
            var result = FwpmFilterDeleteById0(_engine, id);
            if (result != ErrorSuccess)
            {
                Log.App($"Kill switch: removing filter {id} returned 0x{result:X8}");
            }
        }

        _tunnelFilterIds.Clear();
        return true;
    }

    private void DisarmCore()
    {
        _tunnelFilterIds.Clear();

        if (_engine != nint.Zero)
        {
            // Closing a dynamic session removes every filter and the sublayer with it.
            var result = FwpmEngineClose0(_engine);
            if (result != ErrorSuccess)
            {
                Log.App($"FwpmEngineClose0 returned 0x{result:X8}");
            }

            _engine = nint.Zero;
        }

        if (_armed)
        {
            Log.App("Kill switch disarmed; normal networking restored");
        }

        _armed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disarm();
        GC.SuppressFinalize(this);
    }

    ~KillSwitchGuard() => Dispose();

    /// <summary>
    /// Executables that must keep working while the block is on: Tor and its transports, this
    /// application, and anything the user listed as excluded from the tunnel. Leaving the excluded
    /// applications out would block the very programs that were meant to bypass the tunnel.
    /// </summary>
    public static List<string> BuildPermitList(Binaries binaries, IReadOnlyList<string> excludedProcessNames)
    {
        var list = new List<string> { binaries.Tor.Path, binaries.Lyrebird.Path, binaries.SingBox.Path };

        var self = Environment.ProcessPath;
        if (self is not null)
        {
            list.Add(self);
        }

        try
        {
            // The framework dependent build runs through the apphost, but a debugger or a test host
            // runs it through dotnet, so permit that as well when it is what is hosting us.
            var host = Process.GetCurrentProcess().MainModule?.FileName;
            if (host is not null)
            {
                list.Add(host);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not determine the host process image", ex);
        }

        list.AddRange(ResolveExecutablePaths(excludedProcessNames));

        return list.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Turns the executable names from the exclusion list into full paths, which is what a filter
    /// condition needs. Only running processes can be resolved, so an excluded program that is
    /// started later is picked up on the next connect.
    /// </summary>
    private static List<string> ResolveExecutablePaths(IReadOnlyList<string> processNames)
    {
        var paths = new List<string>();

        foreach (var name in processNames)
        {
            var bare = Path.GetFileNameWithoutExtension(name);
            if (bare.Length == 0)
            {
                continue;
            }

            try
            {
                foreach (var process in Process.GetProcessesByName(bare))
                {
                    using (process)
                    {
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (path is not null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                paths.Add(path);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.App($"Could not read the image path of {bare} (PID {process.Id}): {ex.GetType().Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not resolve a path for the excluded process {bare}", ex);
            }

            if (!paths.Any(p => Path.GetFileNameWithoutExtension(p).Equals(bare, StringComparison.OrdinalIgnoreCase)))
            {
                Log.App($"Kill switch: {name} is excluded from the tunnel but is not running, so no permit was added for it");
            }
        }

        return paths;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public nint Name;
        public nint Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSession0
    {
        public Guid SessionKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public uint TxnWaitTimeoutInMSec;
        public uint ProcessId;
        public nint Sid;
        public nint Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid SubLayerKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public nint ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpValue0
    {
        public uint Type;
        private readonly uint _padding;
        public ulong Value;
    }

    /// <summary>
    /// The union is eight bytes wide and holds either an inline number or a pointer, depending on
    /// <see cref="Type"/>. It is declared as a plain integer so both cases can be written without a
    /// second overload.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpConditionValue0
    {
        public uint Type;
        private readonly uint _padding;
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition0
    {
        public Guid FieldKey;
        public uint MatchType;
        public FwpConditionValue0 ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmAction0
    {
        public uint Type;
        public Guid FilterOrCalloutKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter0
    {
        public Guid FilterKey;
        public FwpmDisplayData0 DisplayData;
        public uint Flags;
        public nint ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue0 Weight;
        public uint NumFilterConditions;
        public nint FilterCondition;
        public FwpmAction0 Action;

        // A union of UINT64 rawContext and GUID providerContextKey, so it occupies sixteen bytes,
        // not eight. Declaring it as a single UINT64 shifts every field after it and corrupts the
        // heap, which shows up as a bare "Fatal error" with no managed stack.
        public ulong RawContextOrProviderContextKeyLow;
        public ulong ProviderContextKeyHigh;

        public nint Reserved;
        public ulong FilterId;
        public FwpValue0 EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        nint authIdentity,
        ref FwpmSession0 session,
        out nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(nint engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(nint engineHandle, ref FwpmSubLayer0 subLayer, nint sd);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(nint engineHandle, ref FwpmFilter0 filter, nint sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDeleteById0(nint engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out nint appId);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFreeMemory0(ref nint p);
}
