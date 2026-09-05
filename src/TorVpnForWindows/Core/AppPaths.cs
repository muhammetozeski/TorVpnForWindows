namespace TorVpnForWindows.Core;

/// <summary>
/// Every path the application uses. Everything lives under a single per-user directory so the
/// executable itself stays portable and nothing is written next to it.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TorVpnForWindows");

    /// <summary>
    /// Extracted copy of the embedded payload, versioned so upgrades replace it.
    ///
    /// This lives under ProgramData rather than the per-user folder for one specific reason:
    /// Tor's ClientTransportPlugin directive splits its exec argument on whitespace, so the path to
    /// lyrebird.exe must not contain a space. A user account named "John Smith" would produce one
    /// under LocalApplicationData. ProgramData never does, and the application runs elevated
    /// anyway, so writing there is not an extra privilege.
    /// </summary>
    public static string Runtime => Path.Combine(SharedRoot, "runtime", PayloadVersion);

    public static string SharedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TorVpnForWindows");

    // The copies that ship inside the executable. BinaryResolver prefers a version on PATH and
    // falls back to these, so nothing outside Binaries should use them directly.
    public static string BundledTorExe => Path.Combine(Runtime, "tor", "tor.exe");
    public static string BundledLyrebirdExe => Path.Combine(Runtime, "tor", "pluggable_transports", "lyrebird.exe");
    public static string BundledSingBoxExe => Path.Combine(Runtime, "sing-box", "sing-box.exe");

    // Data files rather than programs: there is no PATH equivalent, so the bundled copies are used
    // whichever tor.exe ends up running.
    public static string GeoIpFile => Path.Combine(Runtime, "tor", "data", "geoip");
    public static string GeoIpV6File => Path.Combine(Runtime, "tor", "data", "geoip6");

    /// <summary>Records the child process identifiers so a crashed run can be cleaned up next time.</summary>
    public static string ChildProcessFile => Path.Combine(SessionDir, "children.json");

    /// <summary>Tor's DataDirectory. Kept out of the runtime folder so upgrades do not discard it.</summary>
    public static string TorData => Path.Combine(Root, "tor-data");

    public static string TorRcFile => Path.Combine(Root, "session", "torrc");
    public static string SingBoxConfigFile => Path.Combine(Root, "session", "sing-box.json");
    public static string SessionDir => Path.Combine(Root, "session");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ExclusionsFile => Path.Combine(Root, "exclusions.txt");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string AppLogFile => Path.Combine(LogDir, "app.log");

    /// <summary>
    /// Bumped whenever the embedded payload changes so a new build re-extracts instead of reusing
    /// stale binaries. Derived from the assembly version.
    /// </summary>
    public static string PayloadVersion { get; } =
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SharedRoot);
        Directory.CreateDirectory(TorData);
        Directory.CreateDirectory(SessionDir);
        Directory.CreateDirectory(LogDir);
    }
}
