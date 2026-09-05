namespace TorVpnForWindows.Config;

/// <summary>
/// Approximate centre of each country, used to place the marker on the status map.
///
/// These are rough interior points rather than true centroids: for a marker a few pixels across on
/// a map 720 units wide, a country's rough middle is indistinguishable from its exact one, and a
/// true centroid can fall outside a country with an awkward shape.
/// </summary>
public static class CountryLocations
{
    private static readonly Dictionary<string, (double Latitude, double Longitude)> Centres =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ad"] = (42.5, 1.5),      ["ae"] = (24.0, 54.0),     ["al"] = (41.2, 20.0),
            ["am"] = (40.2, 45.0),     ["ar"] = (-35.0, -65.0),   ["at"] = (47.5, 14.5),
            ["au"] = (-25.0, 133.0),   ["az"] = (40.4, 47.9),     ["ba"] = (44.0, 18.0),
            ["bd"] = (23.7, 90.4),     ["be"] = (50.6, 4.6),      ["bg"] = (42.7, 25.2),
            ["br"] = (-10.0, -53.0),   ["by"] = (53.7, 28.0),     ["ca"] = (58.0, -100.0),
            ["ch"] = (46.8, 8.2),      ["cl"] = (-33.0, -71.0),   ["cn"] = (35.0, 105.0),
            ["co"] = (4.6, -74.1),     ["cr"] = (9.9, -84.1),     ["cy"] = (35.1, 33.4),
            ["cz"] = (49.8, 15.5),     ["de"] = (51.1, 10.4),     ["dk"] = (56.0, 9.5),
            ["ec"] = (-1.8, -78.2),    ["ee"] = (58.7, 25.5),     ["eg"] = (26.8, 30.8),
            ["es"] = (40.2, -3.6),     ["fi"] = (64.5, 26.0),     ["fr"] = (46.6, 2.5),
            ["gb"] = (54.0, -2.5),     ["ge"] = (42.3, 43.4),     ["gr"] = (39.1, 22.0),
            ["hk"] = (22.3, 114.2),    ["hr"] = (45.1, 15.5),     ["hu"] = (47.2, 19.4),
            ["id"] = (-2.5, 118.0),    ["ie"] = (53.2, -8.0),     ["il"] = (31.4, 35.0),
            ["in"] = (21.0, 78.0),     ["ir"] = (32.0, 53.0),     ["is"] = (64.9, -18.6),
            ["it"] = (42.8, 12.6),     ["jp"] = (36.5, 138.0),    ["ke"] = (0.2, 37.9),
            ["kr"] = (36.5, 127.9),    ["kz"] = (48.0, 68.0),     ["lt"] = (55.3, 23.9),
            ["lu"] = (49.8, 6.1),      ["lv"] = (56.9, 24.9),     ["ma"] = (31.8, -7.1),
            ["md"] = (47.2, 28.5),     ["me"] = (42.7, 19.4),     ["mk"] = (41.6, 21.7),
            ["mt"] = (35.9, 14.4),     ["mx"] = (23.6, -102.5),   ["my"] = (4.2, 102.0),
            ["ng"] = (9.1, 8.7),       ["nl"] = (52.2, 5.5),      ["no"] = (64.5, 12.0),
            ["nz"] = (-41.0, 173.0),   ["pa"] = (8.5, -80.8),     ["pe"] = (-9.2, -75.0),
            ["ph"] = (12.9, 122.0),    ["pk"] = (30.4, 69.3),     ["pl"] = (52.1, 19.4),
            ["pt"] = (39.6, -8.0),     ["qa"] = (25.3, 51.2),     ["ro"] = (45.9, 25.0),
            ["rs"] = (44.0, 21.0),     ["ru"] = (60.0, 90.0),     ["sa"] = (24.0, 45.0),
            ["se"] = (62.8, 16.7),     ["sg"] = (1.35, 103.8),    ["si"] = (46.1, 14.8),
            ["sk"] = (48.7, 19.5),     ["th"] = (15.0, 101.0),    ["tn"] = (34.0, 9.5),
            ["tr"] = (39.0, 35.2),     ["tw"] = (23.7, 121.0),    ["ua"] = (48.9, 31.4),
            ["us"] = (39.5, -98.5),    ["uy"] = (-32.5, -55.8),   ["ve"] = (7.1, -66.0),
            ["vn"] = (16.0, 107.0),    ["za"] = (-29.0, 24.0)
        };

    /// <summary>
    /// Position of a country on the status map, in the map's own coordinates, or null when the
    /// country is not in the table.
    /// </summary>
    public static (double X, double Y)? Project(string? countryCode, double mapWidth, double mapHeight)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || !Centres.TryGetValue(countryCode.Trim(), out var centre))
        {
            return null;
        }

        // Equirectangular, matching how tools\Build-WorldMap.ps1 projected the outlines.
        var x = (centre.Longitude + 180.0) / 360.0 * mapWidth;
        var y = (90.0 - centre.Latitude) / 180.0 * mapHeight;

        return (x, y);
    }
}
