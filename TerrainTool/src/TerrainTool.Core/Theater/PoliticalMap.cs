using System.Globalization;
using System.Text.Json;

namespace TerrainTool.Core.Theater;

/// <summary>
/// Mappa "politica" del teatro per gli editor: terre emerse, laghi, confini di stato e nomi degli stati, dai dati Natural Earth
/// 1:10m (pubblico dominio) che il pacchetto porta in Data\NaturalEarth. Tutto ritagliato sul teatro e proiettato in km del teatro
/// (est, nord) con la griglia del teatro, quindi vale per qualsiasi teatro.
/// </summary>
public sealed class PoliticalMap
{
    /// <summary>Anelli dei poligoni delle terre emerse (km est, km nord).</summary>
    public List<(double E, double N)[]> Land { get; } = new();
    public List<(double E, double N)[]> Lakes { get; } = new();
    /// <summary>Confini di stato su terra (spezzate).</summary>
    public List<(double E, double N)[]> Borders { get; } = new();
    public List<(string Name, double E, double N)> Labels { get; } = new();

    public const string FolderName = "NaturalEarth";

    /// <summary>Cartella dei dati: Data\NaturalEarth risalendo dalla cartella del programma (struttura del pacchetto "BMS Suite").</summary>
    public static string? FindDataDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            string p = Path.Combine(d.FullName, "Data", FolderName);
            if (File.Exists(Path.Combine(p, "ne_10m_land.geojson"))) return p;
            d = d.Parent;
        }
        return null;
    }

    public static PoliticalMap? Load(TheaterGrid grid, out string? error)
    {
        error = null;
        string? dir = FindDataDir();
        if (dir == null) { error = $"Data\\{FolderName} not found next to the tools"; return null; }
        double size = grid.Config.SizeMeters;
        // riquadro lat/lon del teatro, un po' allargato
        var c = grid.Corners();
        double la0 = c.Min(x => x.Lat), la1 = c.Max(x => x.Lat), lo0 = c.Min(x => x.Lon), lo1 = c.Max(x => x.Lon);
        double padLa = (la1 - la0) * 0.08, padLo = (lo1 - lo0) * 0.08;
        la0 -= padLa; la1 += padLa; lo0 -= padLo; lo1 += padLo;
        bool InBox(double lat, double lon) => lat >= la0 && lat <= la1 && lon >= lo0 && lon <= lo1;
        double minStep = size / 1000 / 4000;       // punti piu' vicini di ~0,5 km (teatro di 2048 km) si saltano

        (double, double)[]? Project(JsonElement ring, bool closed)
        {
            var pts = new List<(double, double)>();
            bool any = false;
            (double E, double N) last = (double.NaN, double.NaN);
            foreach (var p in ring.EnumerateArray())
            {
                double lon = p[0].GetDouble(), lat = p[1].GetDouble();
                if (InBox(lat, lon)) any = true;
                var (x, y) = grid.ToTheater(lat, lon);
                (double E, double N) q = (x / 1000, y / 1000);
                if (!double.IsNaN(last.E) && Math.Abs(q.E - last.E) + Math.Abs(q.N - last.N) < minStep) continue;
                pts.Add(q); last = q;
            }
            return any && pts.Count >= (closed ? 3 : 2) ? pts.ToArray() : null;
        }

        void Read(string file, Action<JsonElement, string> geom)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var g = f.GetProperty("geometry");
                if (g.ValueKind != JsonValueKind.Object) continue;
                geom(g.GetProperty("coordinates"), g.GetProperty("type").GetString() ?? "");
            }
        }

        var map = new PoliticalMap();
        try
        {
            void Polys(JsonElement co, string type, List<(double, double)[]> into)
            {
                IEnumerable<JsonElement> polys = type == "Polygon" ? new[] { co } : type == "MultiPolygon" ? co.EnumerateArray() : Array.Empty<JsonElement>();
                foreach (var poly in polys)
                    foreach (var ring in poly.EnumerateArray())
                        if (Project(ring, true) is { } r) into.Add(r);
            }
            Read("ne_10m_land.geojson", (co, t) => Polys(co, t, map.Land));
            Read("ne_10m_lakes.geojson", (co, t) => Polys(co, t, map.Lakes));
            Read("ne_10m_admin_0_boundary_lines_land.geojson", (co, t) =>
            {
                IEnumerable<JsonElement> lines = t == "LineString" ? new[] { co } : t == "MultiLineString" ? co.EnumerateArray() : Array.Empty<JsonElement>();
                foreach (var l in lines) if (Project(l, false) is { } r) map.Borders.Add(r);
            });
            string lab = Path.Combine(dir, "country_labels.csv");
            if (File.Exists(lab))
                foreach (var line in File.ReadLines(lab))
                {
                    if (line.StartsWith('#')) continue;
                    var v = line.Split(';');
                    if (v.Length < 3 || !double.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                        || !double.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                    var (x, y) = grid.ToTheater(lat, lon);
                    if (x >= 0 && y >= 0 && x <= size && y <= size) map.Labels.Add((v[0], x / 1000, y / 1000));
                }
        }
        catch (Exception ex) { error = ex.Message; return null; }
        return map;
    }
}

/// <summary>Country borders and country names of the whole world (Natural Earth, latitude/longitude), for the world map.</summary>
public sealed class WorldBorders
{
    /// <summary>Border lines on land, each with its latitude/longitude box (for skipping lines off screen).</summary>
    public List<((double Lat, double Lon)[] Pts, double S, double W, double N, double E)> Lines { get; } = new();
    public List<(string Name, double Lat, double Lon)> Labels { get; } = new();

    /// <summary>Reads Data\NaturalEarth (null when the folder is not found).</summary>
    public static WorldBorders? Load()
    {
        string? dir = PoliticalMap.FindDataDir();
        if (dir == null) return null;
        var res = new WorldBorders();
        string path = Path.Combine(dir, "ne_10m_admin_0_boundary_lines_land.geojson");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var g = f.GetProperty("geometry");
                if (g.ValueKind != JsonValueKind.Object) continue;
                var co = g.GetProperty("coordinates");
                string t = g.GetProperty("type").GetString() ?? "";
                IEnumerable<JsonElement> lines = t == "LineString" ? new[] { co } : t == "MultiLineString" ? co.EnumerateArray() : Array.Empty<JsonElement>();
                foreach (var l in lines)
                {
                    var pts = l.EnumerateArray().Select(p => (Lat: p[1].GetDouble(), Lon: p[0].GetDouble())).ToArray();
                    if (pts.Length < 2) continue;
                    res.Lines.Add((pts, pts.Min(p => p.Lat), pts.Min(p => p.Lon), pts.Max(p => p.Lat), pts.Max(p => p.Lon)));
                }
            }
        }
        string lab = Path.Combine(dir, "country_labels.csv");
        if (File.Exists(lab))
            foreach (var line in File.ReadLines(lab))
            {
                if (line.StartsWith('#')) continue;
                var v = line.Split(';');
                if (v.Length >= 3 && double.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                    && double.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                    res.Labels.Add((v[0], lat, lon));
            }
        return res;
    }
}
