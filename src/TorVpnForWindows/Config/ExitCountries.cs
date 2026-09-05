using System.Globalization;

namespace TorVpnForWindows.Config;

public sealed record ExitCountry(string? Code, string DisplayName);

/// <summary>
/// Countries that host a meaningful number of Tor exit relays. Display names come from
/// <see cref="RegionInfo"/> so they follow the language the user picked, and the list is not
/// duplicated per language.
/// </summary>
public static class ExitCountries
{
    private static readonly string[] Codes =
    [
        "at", "au", "be", "bg", "br", "ca", "ch", "cz", "de", "dk",
        "ee", "es", "fi", "fr", "gb", "hk", "hu", "ie", "is", "it",
        "jp", "lt", "lu", "lv", "md", "nl", "no", "nz", "pl", "pt",
        "ro", "se", "sg", "sk", "ua", "us"
    ];

    /// <summary>
    /// The country list for the picker, sorted by display name, with an "automatic" entry first.
    /// </summary>
    public static List<ExitCountry> Build(string anyLabel)
    {
        var items = new List<ExitCountry>();

        foreach (var code in Codes)
        {
            string name;
            try
            {
                name = new RegionInfo(code.ToUpperInvariant()).DisplayName;
            }
            catch (ArgumentException)
            {
                // Windows does not know this region; fall back to the raw code rather than dropping it.
                name = code.ToUpperInvariant();
            }

            items.Add(new ExitCountry(code, name));
        }

        items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
        items.Insert(0, new ExitCountry(null, anyLabel));
        return items;
    }

    public static string DisplayNameOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        try
        {
            return new RegionInfo(code.ToUpperInvariant()).DisplayName;
        }
        catch (ArgumentException)
        {
            return code.ToUpperInvariant();
        }
    }
}
