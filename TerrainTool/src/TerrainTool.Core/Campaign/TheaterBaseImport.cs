using System.Globalization;
using System.Text.RegularExpressions;
using TerrainTool.Core.Theater;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Airbases (objective type Airbase or Airstrip) of an installed BMS theater, for the airbase list of a new project.
/// Positions come from the theater's Campaign\CampObjData.XML; with a New Terrain theater (NewTerrain\Theater.txt)
/// they are turned into latitude/longitude, otherwise only the names are imported. Read-only.
/// </summary>
public sealed class TheaterBaseImport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public const int AirbaseType = 1, AirstripType = 2;

    public List<Airbase> Bases { get; } = new();
    /// <summary>Size, centre and heights of the theater (from Theater.txt), or null for a classic terrain.</summary>
    public TheaterConfig? Theater { get; private set; }
    public bool Georeferenced => Theater != null;
    public List<string> Notes { get; } = new();

    /// <summary>Reads the airbases of the theater. Throws with a readable message when the needed files are missing.</summary>
    public static TheaterBaseImport FromTheater(BmsTheater t)
    {
        var r = new TheaterBaseImport();
        string? camp = FindFile(t.CampaignDir, "CampObjData.XML")
            ?? throw new FileNotFoundException($"{t.Name}: CampObjData.XML not found in {t.CampaignDir}.");
        string? ct = FindFile(t.ObjectDir, "Falcon4_CT.xml");
        if (ct == null)
        {
            ct = FindFile(Path.Combine(t.DataDir, "Terrdata", "objects"), "Falcon4_CT.xml");
            if (ct != null) r.Notes.Add("The theater has no Falcon4_CT.xml of its own: objective types taken from BMS (Korea).");
        }
        if (ct == null) throw new FileNotFoundException($"{t.Name}: Falcon4_CT.xml not found in {t.ObjectDir} or in BMS.");

        // OCD → objective type (CT entries with EntityType 3)
        var ocdType = new Dictionary<int, int>();
        foreach (Match m in Regex.Matches(File.ReadAllText(ct), @"<CT Num=""\d+"">(.*?)</CT>", RegexOptions.Singleline))
        {
            string b = m.Groups[1].Value;
            int G(string tag) { var x = Regex.Match(b, $"<{tag}>(-?\\d+)</{tag}>"); return x.Success ? int.Parse(x.Groups[1].Value, Ci) : -1; }
            if (G("EntityType") == 3) ocdType.TryAdd(G("EntityIdx"), G("Type"));
        }

        TheaterGrid? grid = null;
        string? txt = FindFile(t.NewTerrainDir, "Theater.txt");
        if (txt != null)
        {
            try { r.Theater = TheaterConfig.ParseTheaterTxt(File.ReadAllText(txt)); grid = r.Theater.CreateGrid(); }
            catch (Exception ex) when (ex is FormatException or OverflowException) { r.Notes.Add($"Theater.txt could not be read ({ex.Message}): only the names are imported."); r.Theater = null; }
        }
        else r.Notes.Add("Classic terrain (no NewTerrain\\Theater.txt): the theater has no latitude/longitude, so only the names are imported. Enter the real position of each base in tab 2.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (Match m in Regex.Matches(File.ReadAllText(camp), @"<CampObj CampId=""\d+"">(.*?)</CampObj>", RegexOptions.Singleline))
        {
            string b = m.Groups[1].Value;
            string S(string tag) { var x = Regex.Match(b, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline); return x.Success ? x.Groups[1].Value.Trim() : ""; }
            if (!int.TryParse(S("OcdIndex"), NumberStyles.Integer, Ci, out int ocd)) continue;
            int type = ocdType.GetValueOrDefault(ocd, -1);
            if (type is not (AirbaseType or AirstripType)) continue;
            string name = System.Net.WebUtility.HtmlDecode(S("CampName")).Replace(';', ',').Trim();
            if (name.Length == 0) name = $"Airbase {ocd}";
            string unique = name;
            for (int k = 2; !used.Add(unique); k++) unique = $"{name} ({k})";

            var a = new Airbase { Name = unique, Tier = type == AirbaseType ? "Main" : "Secondary", Status = "to verify" };
            if (grid != null && double.TryParse(S("PositionX"), NumberStyles.Float, Ci, out double north) && double.TryParse(S("PositionY"), NumberStyles.Float, Ci, out double east))
            {
                // CampObjData: X = feet north, Y = feet east of the south-west corner
                double xm = east * 0.3048, ym = north * 0.3048;
                if (grid.Contains(xm, ym))
                {
                    var (lat, lon) = grid.ToLatLon(xm, ym);
                    a.Lat = a.LatOld = Math.Round(lat, 6);
                    a.Lon = a.LonOld = Math.Round(lon, 6);
                    a.Status = "ok";
                }
                else skipped++;
            }
            r.Bases.Add(a);
        }
        if (skipped > 0) r.Notes.Add($"{skipped} bases lie outside the theater map: imported without position.");
        if (r.Bases.Count == 0) r.Notes.Add("No objective of type Airbase or Airstrip found in the campaign.");
        return r;
    }

    /// <summary>File in a folder, ignoring letter case (BMS folders are written in many ways).</summary>
    private static string? FindFile(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        string direct = Path.Combine(dir, name);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }
}
