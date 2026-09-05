namespace TorVpnForWindows.Core;

/// <summary>
/// The executables the session actually runs. Resolved once per connect so that a tool the machine
/// keeps updated is preferred over the copy shipped inside this application, and a change to PATH
/// is picked up without restarting.
/// </summary>
public sealed record Binaries(ResolvedBinary Tor, ResolvedBinary Lyrebird, ResolvedBinary SingBox)
{
    public static Binaries Resolve()
    {
        var tor = BinaryResolver.Resolve(
            "tor.exe",
            AppPaths.BundledTorExe,
            path => BinaryResolver.ProbeVersion(path, "--version", "Tor version"));

        // lyrebird is only started by Tor when bridges are in use. It has no version flag that
        // prints a recognisable banner, so the name match on PATH is all there is to go on.
        var lyrebird = BinaryResolver.Resolve("lyrebird.exe", AppPaths.BundledLyrebirdExe);

        var singBox = BinaryResolver.Resolve(
            "sing-box.exe",
            AppPaths.BundledSingBoxExe,
            path => BinaryResolver.ProbeVersion(path, "version", "sing-box"));

        return new Binaries(tor, lyrebird, singBox);
    }

    public IEnumerable<string> Describe()
    {
        yield return Tor.Describe();
        yield return Lyrebird.Describe();
        yield return SingBox.Describe();
    }

    /// <summary>
    /// Process names used by the routing rules that keep this infrastructure out of its own tunnel.
    /// Derived from the resolved paths so a differently named build on PATH is still excluded.
    /// </summary>
    public IReadOnlyList<string> ProcessNames() =>
    [
        Path.GetFileName(Tor.Path),
        Path.GetFileName(Lyrebird.Path),
        Path.GetFileName(SingBox.Path)
    ];
}
