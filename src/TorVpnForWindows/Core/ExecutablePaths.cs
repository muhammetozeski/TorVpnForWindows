using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TorVpnForWindows.Core;

/// <summary>
/// Resolves an executable's path to the single spelling the file system itself leads to, and lists
/// the other spellings a running process can still report for that same file.
///
/// The program lists match by path, never by name. A name matches every copy of a program anywhere
/// on the disk, including one put somewhere on purpose to borrow another program's permissions. A
/// path only helps if it is compared in the form the matching engine sees, so paths are resolved
/// through the file system rather than compared as typed: letter case, 8.3 short names, junctions
/// and symbolic links all collapse to the file they actually lead to.
/// </summary>
public static partial class ExecutablePaths
{
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// The final path of an existing file, or null when there is no such file. Junctions and
    /// symbolic links are followed, short names are expanded, and the letter case is the one stored
    /// on disk.
    /// </summary>
    public static string? Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(full))
        {
            return null;
        }

        try
        {
            // Read access is enough to ask for the final path, and a running executable can always
            // be opened for reading by others.
            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var final = FinalPathOf(handle);
            if (final is not null)
            {
                return final;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some protected folders refuse even that. The long form below still resolves the case
            // and the short names; only a link inside such a folder would stay unfollowed.
            Log.App($"Could not open {full} to resolve its final path ({ex.GetType().Name}); using its long form");
        }

        return LongFormOf(full) ?? full;
    }

    /// <summary>
    /// Every spelling of an executable a process may report: the final path, and its 8.3 short form
    /// when the volume keeps short names and it differs. Case is not a spelling; every match made
    /// with these ignores case.
    /// </summary>
    public static IReadOnlyList<string> SpellingsOf(string canonicalPath)
    {
        var spellings = new List<string> { canonicalPath };

        var shortForm = ShortFormOf(canonicalPath);
        if (shortForm is not null && !shortForm.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            spellings.Add(shortForm);
        }

        return spellings;
    }

    /// <summary>Whether two paths lead to the same file.</summary>
    public static bool SameFile(string first, string second) =>
        string.Equals(Canonicalize(first) ?? first, Canonicalize(second) ?? second, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The image path of a running process, resolved like any other path, or null when the process
    /// has exited or does not let its image be read.
    ///
    /// Asked through QueryFullProcessImageName rather than the process's main module. Reading the
    /// module list needs far more access and fails for a process of another bitness, while this
    /// needs only the limited query right that Windows grants for almost every process.
    /// </summary>
    public static string? ImagePathOfProcess(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);

        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[1024];
        var size = (uint)buffer.Length;

        if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
        {
            return null;
        }

        var reported = new string(buffer, 0, (int)size);
        return Canonicalize(reported) ?? reported;
    }

    /// <summary>
    /// A case-insensitive, whole-string regular expression for one path, in the syntax sing-box
    /// uses. Go's syntax rejects escapes it does not know, so only its own metacharacters are
    /// escaped: an escaped space, as .NET's Regex.Escape writes it, would make the configuration
    /// fail to load.
    /// </summary>
    public static string ToGoRegex(string path)
    {
        var builder = new StringBuilder("(?i)^", path.Length + 16);

        foreach (var c in path)
        {
            if (@"\.+*?()|[]{}^$".Contains(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
        }

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        return StripDevicePrefix(new string(buffer, 0, (int)length));
    }

    /// <summary>
    /// GetFinalPathNameByHandle answers in the \\?\ form. The prefix is dropped so the result reads
    /// like the paths processes report and people type.
    /// </summary>
    private static string StripDevicePrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    private static string? LongFormOf(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private static string? ShortFormOf(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);
}
