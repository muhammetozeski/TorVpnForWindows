using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using TorVpnForWindows.Core;

namespace TorVpnForWindows.Localization;

/// <summary>
/// Drives localization for <see cref="Strings"/>. On startup it ensures <c>lang.tr.xml</c> exists
/// (writing the Turkish defaults so the user can edit them), extracts the shipped
/// <c>lang.en.xml</c>, and — when another language is chosen — loads <c>lang.&lt;code&gt;.xml</c>
/// over the fields via reflection.
///
/// Dropping another <c>lang.&lt;code&gt;.xml</c> into the configuration folder is enough to add a
/// language; it shows up in the picker without a code change.
/// </summary>
internal static class LocManager
{
    public const string SystemLanguage = "system";
    private const string EnglishResource = "TorVpnForWindows.lang.en.xml";

    /// <summary>Fires after the active language changes so open windows can re-apply their text.</summary>
    public static event Action? LanguageChanged;

    public static string Current { get; private set; } = "tr";

    private static string Folder => AppPaths.Root;

    public static (string Code, string Name)[] Available
    {
        get
        {
            try
            {
                if (!Directory.Exists(Folder))
                {
                    return [("tr", "Türkçe")];
                }

                var found = Directory.GetFiles(Folder, "lang.*.xml")
                    .Select(file =>
                    {
                        var parts = Path.GetFileName(file).Split('.');
                        var code = parts.Length > 1 ? parts[1] : "??";
                        var name = code.ToUpperInvariant();

                        try
                        {
                            var doc = XDocument.Load(file);
                            var languageName = doc.Root?.Element("LanguageName")?.Value;
                            if (!string.IsNullOrWhiteSpace(languageName))
                            {
                                name = languageName;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.App($"Could not read the language name from {Path.GetFileName(file)}: {ex.GetType().Name}");
                        }

                        return (Code: code, Name: name);
                    })
                    .OrderBy(item => item.Name, StringComparer.CurrentCulture)
                    .ToArray();

                return found.Length > 0 ? found : [("tr", "Türkçe")];
            }
            catch (Exception ex)
            {
                Log.Error("Could not list the available languages", ex);
                return [("tr", "Türkçe")];
            }
        }
    }

    private static FieldInfo[] StringFields() => typeof(Strings)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(string))
        .ToArray();

    private static string PathFor(string language) => Path.Combine(Folder, $"lang.{language}.xml");

    /// <summary>
    /// Prepares the language files and applies the configured language. Pass "system" to follow the
    /// Windows display language, which is what a first run does.
    /// </summary>
    public static void Init(string configuredLanguage)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            // Keep an editable Turkish baseline, written from the values compiled into Strings.
            var turkishPath = PathFor("tr");
            if (!File.Exists(turkishPath))
            {
                WriteXml(turkishPath, "Türkçe");
            }

            ExtractEnglishIfMissing();
        }
        catch (Exception ex)
        {
            Log.Error("Preparing the language files failed", ex);
        }

        Apply(configuredLanguage);
    }

    /// <summary>Loads a language over the <see cref="Strings"/> fields and notifies listeners.</summary>
    public static void Apply(string configuredLanguage)
    {
        var language = Resolve(configuredLanguage);

        try
        {
            if (language == "tr")
            {
                // Turkish is what the fields already hold, unless another language overwrote them.
                if (Current != "tr")
                {
                    ReadInto(PathFor("tr"));
                }
            }
            else
            {
                var path = PathFor(language);
                if (File.Exists(path))
                {
                    ReadInto(path);
                }
                else
                {
                    Log.App($"Language file not found: {path}; keeping the Turkish defaults.");
                    language = "tr";
                }
            }

            Current = language;

            var culture = new CultureInfo(language);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (Exception ex)
        {
            Log.Error($"Applying the language '{language}' failed", ex);
        }

        try
        {
            LanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("A LanguageChanged handler threw", ex);
        }
    }

    /// <summary>Turns "system" into a concrete language code against the Windows display language.</summary>
    public static string Resolve(string configuredLanguage)
    {
        var configured = (configuredLanguage ?? string.Empty).Trim().ToLowerInvariant();

        if (configured.Length > 0 && configured != SystemLanguage)
        {
            return configured;
        }

        try
        {
            var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (File.Exists(PathFor(system)))
            {
                return system;
            }

            // A first run happens before the files are written, so fall back to the two languages
            // that always exist.
            if (system is "tr" or "en")
            {
                return system;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the Windows display language", ex);
        }

        return "en";
    }

    /// <summary>Reflection-writes the current field values to an XML file.</summary>
    private static void WriteXml(string path, string languageName)
    {
        try
        {
            var doc = new XDocument(new XElement("strings",
                new XElement("LanguageName", languageName),
                StringFields().Select(f => new XElement("s",
                    new XAttribute("name", f.Name),
                    Escape((string?)f.GetValue(null) ?? string.Empty)))));

            File.WriteAllText(path, doc.ToString(), new UTF8Encoding(false));
            Log.App($"Wrote the language baseline {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("Writing the language baseline failed", ex);
        }
    }

    /// <summary>Reflection-loads values from an XML file over the matching fields.</summary>
    private static void ReadInto(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var map = StringFields().ToDictionary(f => f.Name, StringComparer.Ordinal);
            var count = 0;

            foreach (var element in doc.Root?.Elements("s") ?? [])
            {
                var name = element.Attribute("name")?.Value;
                if (name is not null && map.TryGetValue(name, out var field))
                {
                    field.SetValue(null, Unescape(element.Value));
                    count++;
                }
            }

            Log.App($"Loaded {count} localized string(s) from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error($"Reading {Path.GetFileName(path)} failed", ex);
        }
    }

    private static void ExtractEnglishIfMissing()
    {
        var path = PathFor("en");
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EnglishResource);
            if (stream is null)
            {
                Log.App($"The embedded resource {EnglishResource} is missing; English is unavailable.");
                return;
            }

            using var file = File.Create(path);
            stream.CopyTo(file);
            Log.App("Wrote lang.en.xml");
        }
        catch (Exception ex)
        {
            Log.Error("Extracting lang.en.xml failed", ex);
        }
    }

    // Multi-line strings are stored single-line with a literal \n so the XML stays clean/indentable.
    private static string Escape(string s) => s.Replace("\r\n", "\n").Replace("\n", "\\n");

    private static string Unescape(string s) => s.Replace("\\n", "\n");
}
