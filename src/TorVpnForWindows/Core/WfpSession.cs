using System.Net;
using System.Runtime.InteropServices;

namespace TorVpnForWindows.Core;

/// <summary>
/// One dynamic Windows Filtering Platform session with a sublayer of its own, and the few kinds of
/// filter this application writes into it.
///
/// The session is opened as dynamic: if this process dies for any reason, Windows removes every
/// filter it added. Closing it removes them too, which is how a whole set of filters is replaced
/// without deleting them one by one.
///
/// Shared by the kill switch and the internet lists. The structure layouts below are the part that
/// is easy to get wrong and fatal when wrong, so they live in one place.
/// </summary>
internal sealed class WfpSession : IDisposable
{
    // Layers. Outbound connections are authorised here, before any routing decision is applied.
    public static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    // Conditions.
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionInterfaceIndex = new("667fd755-d695-434a-8af5-d3835a1259bc");
    private static readonly Guid ConditionIpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid ConditionIpRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid ConditionIpRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    private const uint ConditionFlagIsLoopback = 0x00000001;

    public const byte ProtocolUdp = 17;

    private const uint SessionFlagDynamic = 0x00000001;
    public const uint ActionBlock = 0x00001001;
    public const uint ActionPermit = 0x00001002;

    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;

    // FWP_DATA_TYPE. These have to be exact: the value is a tagged union, so a wrong tag makes WFP
    // read the payload as the wrong kind. Tagging a plain number as FWP_UINT64, whose union member
    // is a pointer, makes it dereference the number itself and take the process down with an access
    // violation.
    private const uint TypeUInt8 = 1;
    private const uint TypeUInt16 = 2;
    private const uint TypeUInt32 = 3;
    private const uint TypeByteBlob = 12;

    private const uint ErrorSuccess = 0;

    private readonly string _displayName;
    private readonly Guid _subLayerKey = Guid.NewGuid();
    private nint _engine;

    private WfpSession(string displayName) => _displayName = displayName;

    /// <summary>
    /// Opens a session and adds its sublayer. Every filter added through the session goes into that
    /// sublayer, and every filter carries <paramref name="displayName"/> as its name, which is what
    /// shows up in "netsh wfp show filters".
    /// </summary>
    public static WfpSession Open(string displayName, string sessionDescription, string subLayerDescription)
    {
        VerifyStructureSizes();

        var session = new WfpSession(displayName);

        try
        {
            session.OpenEngine(sessionDescription);
            session.AddSubLayer(subLayerDescription);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Adds a filter to this session's sublayer and returns its identifier.</summary>
    public ulong AddFilter(Guid layer, uint action, byte weight, string description, params Condition[] conditions)
    {
        if (_engine == nint.Zero)
        {
            throw new ObjectDisposedException(nameof(WfpSession));
        }

        var namePtr = Marshal.StringToHGlobalUni(_displayName);
        var descPtr = Marshal.StringToHGlobalUni(description);

        var conditionSize = Marshal.SizeOf<FwpmFilterCondition0>();
        var conditionArray = conditions.Length == 0
            ? nint.Zero
            : Marshal.AllocHGlobal(conditionSize * conditions.Length);

        try
        {
            for (var i = 0; i < conditions.Length; i++)
            {
                Marshal.StructureToPtr(conditions[i].Native, conditionArray + (i * conditionSize), fDeleteOld: false);
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

    /// <summary>Removes one filter. Returns false and logs when WFP refuses.</summary>
    public bool DeleteFilter(ulong filterId)
    {
        if (_engine == nint.Zero)
        {
            return false;
        }

        var result = FwpmFilterDeleteById0(_engine, filterId);
        if (result != ErrorSuccess)
        {
            Log.App($"Removing WFP filter {filterId} returned 0x{result:X8}");
            return false;
        }

        return true;
    }

    /// <summary>Closes the session, which removes every filter and the sublayer with it.</summary>
    public void Dispose()
    {
        var engine = Interlocked.Exchange(ref _engine, nint.Zero);

        if (engine == nint.Zero)
        {
            return;
        }

        var result = FwpmEngineClose0(engine);
        if (result != ErrorSuccess)
        {
            Log.App($"FwpmEngineClose0 returned 0x{result:X8}");
        }
    }

    // ------------------------------------------------------------------ conditions

    /// <summary>One condition of a filter, built by the factory methods below.</summary>
    public readonly struct Condition
    {
        internal Condition(FwpmFilterCondition0 native) => Native = native;

        internal FwpmFilterCondition0 Native { get; }
    }

    /// <summary>Traffic that stays on this machine.</summary>
    public static Condition Loopback() => new(new FwpmFilterCondition0
    {
        FieldKey = ConditionFlags,
        MatchType = MatchFlagsAllSet,

        // A 32 bit condition value lives inside the union, not behind a pointer.
        ConditionValue = new FwpConditionValue0 { Type = TypeUInt32, Value = ConditionFlagIsLoopback }
    });

    public static Condition Protocol(byte protocol) => new(new FwpmFilterCondition0
    {
        FieldKey = ConditionIpProtocol,
        MatchType = MatchEqual,
        ConditionValue = new FwpConditionValue0 { Type = TypeUInt8, Value = protocol }
    });

    public static Condition RemotePort(ushort port) => new(new FwpmFilterCondition0
    {
        FieldKey = ConditionIpRemotePort,
        MatchType = MatchEqual,
        ConditionValue = new FwpConditionValue0 { Type = TypeUInt16, Value = port }
    });

    public static Condition InterfaceIndex(int interfaceIndex) => new(new FwpmFilterCondition0
    {
        FieldKey = ConditionInterfaceIndex,
        MatchType = MatchEqual,
        ConditionValue = new FwpConditionValue0 { Type = TypeUInt32, Value = (uint)interfaceIndex }
    });

    /// <summary>An IPv4 address. Throws for anything else.</summary>
    public static Condition RemoteAddressV4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{address} is not an IPv4 address.", nameof(address));
        }

        // WFP takes an IPv4 address as a number in host order, not in the order it travels on the
        // wire, so the octets are assembled rather than copied.
        var octets = address.GetAddressBytes();
        var value = ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];

        return new Condition(new FwpmFilterCondition0
        {
            FieldKey = ConditionIpRemoteAddress,
            MatchType = MatchEqual,
            ConditionValue = new FwpConditionValue0 { Type = TypeUInt32, Value = value }
        });
    }

    /// <summary>
    /// The identifier WFP gives an executable, which a filter condition refers to by pointer. It has
    /// to stay alive until the filters using it have been added, and is freed on dispose.
    /// </summary>
    public sealed class AppId : IDisposable
    {
        private nint _blob;

        private AppId(nint blob) => _blob = blob;

        /// <summary>Null when the file does not exist or WFP cannot build an identifier for it.</summary>
        public static AppId? For(string executablePath)
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            var result = FwpmGetAppIdFromFileName0(executablePath, out var blob);
            if (result != ErrorSuccess || blob == nint.Zero)
            {
                Log.App($"Could not build a WFP application identifier for {executablePath} (0x{result:X8})");
                return null;
            }

            return new AppId(blob);
        }

        /// <summary>Traffic from this executable.</summary>
        public Condition Condition => new(new FwpmFilterCondition0
        {
            FieldKey = ConditionAleAppId,
            MatchType = MatchEqual,

            // A byte blob, unlike a 32 bit number, is referenced by pointer.
            ConditionValue = new FwpConditionValue0 { Type = TypeByteBlob, Value = (ulong)_blob }
        });

        public void Dispose()
        {
            if (_blob != nint.Zero)
            {
                FwpmFreeMemory0(ref _blob);
                _blob = nint.Zero;
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
            throw new InvalidOperationException($"The WFP structure layout is wrong: {detail}.");
        }
    }

    private void OpenEngine(string description)
    {
        var session = new FwpmSession0
        {
            Flags = SessionFlagDynamic,
            DisplayData = new FwpmDisplayData0
            {
                Name = Marshal.StringToHGlobalUni(_displayName),
                Description = Marshal.StringToHGlobalUni(description)
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

    private void AddSubLayer(string description)
    {
        var namePtr = Marshal.StringToHGlobalUni(_displayName);
        var descPtr = Marshal.StringToHGlobalUni(description);

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
    internal struct FwpConditionValue0
    {
        public uint Type;
        private readonly uint _padding;
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FwpmFilterCondition0
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
