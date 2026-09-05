using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TorVpnForWindows.Core;

/// <summary>
/// Walks a process and everything it started.
///
/// Needed because a launcher on PATH is often not the program itself. A scoop shim, for example, is
/// a small executable that starts the real tool as a separate process, so permitting the shim in
/// the kill switch permits nothing that actually talks to the network.
/// </summary>
public static partial class ProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int MaxExeFile = 260;

    /// <summary>
    /// Image paths of a process and all of its descendants. Paths that cannot be read, which
    /// happens for processes that exit while being enumerated, are skipped.
    /// </summary>
    public static IReadOnlyList<string> GetImagePaths(int rootProcessId)
    {
        var paths = new List<string>();
        var pids = CollectTree(rootProcessId);

        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var path = process.MainModule?.FileName;

                if (path is not null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Already gone.
            }
            catch (Exception ex)
            {
                Log.App($"Could not read the image path of PID {pid}: {ex.GetType().Name}");
            }
        }

        return paths;
    }

    private static List<int> CollectTree(int rootProcessId)
    {
        var result = new List<int> { rootProcessId };

        Dictionary<int, List<int>> childrenByParent;

        try
        {
            childrenByParent = BuildParentMap();
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate the process table", ex);
            return result;
        }

        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();

            if (!childrenByParent.TryGetValue(pid, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (result.Contains(child))
                {
                    continue; // Guard against a cycle from a reused identifier.
                }

                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static Dictionary<int, List<int>> BuildParentMap()
    {
        var map = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);

        if (snapshot == nint.Zero || snapshot == -1)
        {
            throw new InvalidOperationException(
                $"CreateToolhelp32Snapshot failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                var parent = (int)entry.ParentProcessId;
                var pid = (int)entry.ProcessId;

                if (!map.TryGetValue(parent, out var list))
                {
                    list = [];
                    map[parent] = list;
                }

                list.Add(pid);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxExeFile)]
        public string ExeFile;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
