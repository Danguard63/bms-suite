using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Theater;

public sealed class Airbase
{
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public string Icao { get; set; } = "";
    /// <summary>Coordinate reali (da OurAirports). NaN se ancora da verificare.</summary>
    public double Lat { get; set; } = double.NaN;
    public double Lon { get; set; } = double.NaN;
    /// <summary>Coordinate del vecchio teatro (PDF navaids): sono quelle "di gioco", spostate rispetto al reale.</summary>
    public double LatOld { get; set; } = double.NaN;
    public double LonOld { get; set; } = double.NaN;
    public string Status { get; set; } = "";
    /// <summary>Main / Secondary / Micro: priorità di costruzione della base.</summary>
    public string Tier { get; set; } = "";

    public bool HasReal => !double.IsNaN(Lat) && !double.IsNaN(Lon);
    public bool HasOld => !double.IsNaN(LatOld) && !double.IsNaN(LonOld);
}

public static class AirbaseCsv
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>CSV separato da ';' con intestazione Name;Country;ICAO;Lat;Lon;LatOld;LonOld;Status;Tier.</summary>
    public static List<Airbase> Load(string path)
    {
        var list = new List<Airbase>();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return list;
        var head = lines[0].Split(';').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        // Accetta sia le intestazioni inglesi sia quelle italiane della prima versione
        int Col(params string[] names) => names.Select(n => Array.IndexOf(head, n.ToLowerInvariant())).FirstOrDefault(i => i >= 0, -1);
        int cName = Col("Name", "Nome"), cCountry = Col("Country", "Paese"), cIcao = Col("ICAO"), cLat = Col("Lat"), cLon = Col("Lon"),
            cLatO = Col("LatOld", "LatPDF"), cLonO = Col("LonOld", "LonPDF"), cSt = Col("Status", "Stato"), cTier = Col("Tier", "Livello");
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split(';');
            string Get(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
            list.Add(new Airbase
            {
                Name = Get(cName),
                Country = EnglishCountry(Get(cCountry)),
                Icao = Get(cIcao),
                Lat = ParseD(Get(cLat)),
                Lon = ParseD(Get(cLon)),
                LatOld = ParseD(Get(cLatO)),
                LonOld = ParseD(Get(cLonO)),
                Status = Get(cSt) == "da verificare" ? "to verify" : Get(cSt),
                Tier = Get(cTier),
            });
        }
        return list;
    }

    public static void Save(string path, IEnumerable<Airbase> bases)
    {
        var sb = new StringBuilder("Name;Country;ICAO;Lat;Lon;LatOld;LonOld;Status;Tier\n");
        foreach (var b in bases)
            sb.Append(string.Join(';', b.Name, b.Country, b.Icao, Fmt(b.Lat), Fmt(b.Lon), Fmt(b.LatOld), Fmt(b.LonOld), b.Status, b.Tier)).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string EnglishCountry(string c) => c switch
    {
        "Arabia/Golfo" => "Saudi Arabia/Gulf",
        "Giordania" => "Jordan",
        _ => c,
    };

    private static double ParseD(string s) =>
        double.TryParse(s, NumberStyles.Float, Ci, out var v) ? v : double.NaN;

    private static string Fmt(double v) => double.IsNaN(v) ? "" : v.ToString("0.######", Ci);
}

public static class Tiers
{
    public static readonly string[] All = { "Main", "Secondary", "Micro", "Server" };
    /// <summary>Base neutrale per l'aereo del server nelle missioni PvP (fazione in pace con rossi e blu).</summary>
    public const string Server = "Server";
}

public static class CenterFinder
{
    /// <summary>
    /// Trova il centro (lat/lon) tale che il riquadro delle basi, proiettato in TM con meridiano
    /// centrale nel centro stesso, sia centrato nel teatro. Arrotonda a 'decimals' decimali.
    /// </summary>
    public static (double Lat, double Lon) Fit(IEnumerable<(double Lat, double Lon)> points, int decimals = 2)
    {
        var pts = points.ToList();
        if (pts.Count == 0) throw new InvalidOperationException("No airbase with real coordinates.");
        double lat = (pts.Min(p => p.Lat) + pts.Max(p => p.Lat)) / 2;
        double lon = (pts.Min(p => p.Lon) + pts.Max(p => p.Lon)) / 2;
        for (int it = 0; it < 8; it++)
        {
            var tm = new Geo.TransverseMercator(lon);
            var (_, yc) = tm.Forward(lat, lon);
            var xy = pts.Select(p => tm.Forward(p.Lat, p.Lon)).ToList();
            double dx = (xy.Min(p => p.X) + xy.Max(p => p.X)) / 2;
            double dy = (xy.Min(p => p.Y) + xy.Max(p => p.Y)) / 2 - yc;
            (lat, lon) = tm.Inverse(dx, yc + dy);
        }
        return (Math.Round(lat, decimals), Math.Round(lon, decimals));
    }

    /// <summary>Margini minimi (km) tra le basi e i bordi del teatro: ovest, est, sud, nord.</summary>
    public static (double W, double E, double S, double N) Margins(TheaterGrid grid, IEnumerable<(double Lat, double Lon)> points)
    {
        var xy = points.Select(p => grid.ToTheater(p.Lat, p.Lon)).ToList();
        double s = grid.Config.SizeMeters;
        return (xy.Min(p => p.X) / 1000, (s - xy.Max(p => p.X)) / 1000, xy.Min(p => p.Y) / 1000, (s - xy.Max(p => p.Y)) / 1000);
    }
}
