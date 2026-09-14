using System.Text;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Config;

/// <summary>
/// The old plain-text list of process names whose traffic bypassed the tunnel. The tunnel lists in
/// the settings replaced it; it is only read once, by <see cref="ExclusionMigration"/>, to carry its
/// names over as paths.
/// </summary>
public static class ExclusionList
{
    private const string DefaultContent = """
        # Tor VPN for Windows - excluded applications
        # Tor VPN for Windows - tunel disi uygulamalar
        #
        # One executable name per line. Traffic from a listed process leaves through the normal
        # internet connection instead of the Tor tunnel, and it resolves names with the machine's
        # usual DNS servers.
        #
        # Her satira bir program adi yazilir. Listedeki bir surecin trafigi Tor tuneli yerine
        # normal internet baglantisindan cikar ve isim cozumlemesini makinenin olagan DNS
        # sunucularindan yapar.
        #
        # Lines starting with # are ignored. Changes take effect on the next connect.
        # # ile baslayan satirlar yok sayilir. Degisiklikler bir sonraki baglantida gecerli olur.

        claude.exe
        """;

    /// <summary>Creates the file with its default content when it does not exist yet.</summary>
    public static void EnsureExists()
    {
        try
        {
            if (File.Exists(AppPaths.ExclusionsFile))
            {
                return;
            }

            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.ExclusionsFile, DefaultContent, new UTF8Encoding(false));
            Log.App($"Created {AppPaths.ExclusionsFile}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the exclusions file", ex);
        }
    }

    /// <summary>Process names to route around the tunnel, normalised and de-duplicated.</summary>
    public static IReadOnlyList<string> Read()
    {
        var names = new List<string>();

        try
        {
            EnsureExists();

            if (!File.Exists(AppPaths.ExclusionsFile))
            {
                return names;
            }

            foreach (var rawLine in File.ReadAllLines(AppPaths.ExclusionsFile))
            {
                var line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // Accept a full path as well; sing-box matches on the file name.
                var name = Path.GetFileName(line);
                if (name.Length == 0)
                {
                    continue;
                }

                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".exe";
                }

                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the exclusions file", ex);
        }

        return names;
    }
}
