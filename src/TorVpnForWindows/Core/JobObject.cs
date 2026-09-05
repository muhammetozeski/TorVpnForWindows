using System.Runtime.InteropServices;

namespace TorVpnForWindows.Core;

/// <summary>
/// A Windows job object configured to kill its members when the last handle closes. Every child
/// process is assigned to it, so tor.exe and sing-box.exe cannot outlive the application even if
/// it is terminated from Task Manager. Without this a crash would leave the TUN adapter in place
/// and the machine without working networking.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private nint _handle;
    private bool _disposed;

    public JobObject()
    {
        _handle = CreateJobObjectW(nint.Zero, null);
        if (_handle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = nint.Zero;
                throw new InvalidOperationException($"SetInformationJobObject failed (Win32 error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool Assign(System.Diagnostics.Process process)
    {
        if (_handle == nint.Zero)
        {
            return false;
        }

        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
            {
                return true;
            }

            Log.App($"AssignProcessToJobObject failed for PID {process.Id} (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not assign PID {process.Id} to the job object", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != nint.Zero)
        {
            CloseHandle(_handle);
            _handle = nint.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~JobObject() => Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint hJob, int infoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);
}
