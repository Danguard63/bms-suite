using System.Globalization;
using System.Text;
using System.Text.Json;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.NewTerrain;

/// <summary>Esito per un'isola di OSM: c'e' nel terreno finito, manca nella Watermap, o la Watermap la ha ma BMS la disegna sommersa.</summary>
public enum IslandState { Present, MissingInWatermap, Flooded, Outside }

public sealed record IslandRow(string Name, string Kind, string OsmType, long OsmId, double Lat, double Lon, double SizeKm, double X, double Y,
    int Samples, int LandNt, int LandWatermap, IslandState State);

/// <summary>
/// Isole di OpenStreetMap (place=island/islet, natural=island) dentro il teatro confrontate con il terreno finito: per ognuna si
/// guardano alcuni punti nella parte centrale (centro e riquadro ridotto a meta') nel NewTerrain (acqua come la disegna BMS) e nella
/// Watermap. Solo isole con un nome o grandi almeno MinKm2. Legge soltanto; scrive islands_check.csv nella cartella del progetto.
/// Serve a capire se il problema di Farsi (isola assente nel terreno) e' isolato o riguarda tante isole.
/// </summary>
public static class IslandCheck
{
    public const string FileName = "islands_check.csv";
    public const double MinKm2 = 1.0;
    private const double FtPerM = 3.280839895;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static async Task<(List<IslandRow> Rows, List<string> Summary)> RunAsync(TheaterProject p, string watermapPath,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var summary = new List<string>();
        var grid = p.Theater.CreateGrid();
        var corners = grid.Corners();
        double la0 = corners.Min(c => c.Lat), la1 = corners.Max(c => c.Lat), lo0 = corners.Min(c => c.Lon), lo1 = corners.Max(c => c.Lon);
        double sizeM = p.Theater.SizeMeters;

        // 1) isole di OSM, a riquadri (una richiesta sola su tutto il teatro scadrebbe)
        var islands = new Dictionary<(string, long), (string Name, string Kind, double Lat, double Lon, double SizeKm, bool Point)>();
        const int nt = 4;
        for (int i = 0; i < nt; i++)
            for (int j = 0; j < nt; j++)
            {
                ct.ThrowIfCancellationRequested();
                double a0 = la0 + (la1 - la0) * i / nt, a1 = la0 + (la1 - la0) * (i + 1) / nt, b0 = lo0 + (lo1 - lo0) * j / nt, b1 = lo0 + (lo1 - lo0) * (j + 1) / nt;
                string bb = string.Format(Ci, "({0:0.####},{1:0.####},{2:0.####},{3:0.####})", a0, b0, a1, b1);
                string q = "[out:json][timeout:120];(" +
                           $"node[\"place\"~\"^(island|islet)$\"]{bb};" +
                           $"way[\"place\"~\"^(island|islet)$\"]{bb};relation[\"place\"~\"^(island|islet)$\"]{bb};" +
                           $"way[\"natural\"=\"island\"][\"name\"]{bb};relation[\"natural\"=\"island\"]{bb};" +
                           ");out bb tags;";
                int k = i * nt + j + 1;
                string json;
                try { json = await RealPlaces.Overpass(q, ct, host => progress?.Report($"Islands from OpenStreetMap: area {k} of {nt * nt}, server {host}…")); }
                catch (HttpRequestException ex) { summary.Add($"area {k}: not answered ({ex.Message})"); continue; }
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
                {
                    string type = el.GetProperty("type").GetString() ?? "";
                    long id = el.GetProperty("id").GetInt64();
                    string name = "", kind = "";
                    if (el.TryGetProperty("tags", out var tags))
                        foreach (var t in tags.EnumerateObject())
                        {
                            if (t.Name == "name:en" || (t.Name == "name" && name.Length == 0)) name = t.Value.GetString() ?? "";
                            if (t.Name == "place") kind = t.Value.GetString() ?? "";
                            if (t.Name == "natural" && kind.Length == 0) kind = t.Value.GetString() ?? "";
                        }
                    double lat, lon, size = 0; bool point = false;
                    if (el.TryGetProperty("bounds", out var b))
                    {
                        double mnla = b.GetProperty("minlat").GetDouble(), mxla = b.GetProperty("maxlat").GetDouble();
                        double mnlo = b.GetProperty("minlon").GetDouble(), mxlo = b.GetProperty("maxlon").GetDouble();
                        lat = (mnla + mxla) / 2; lon = (mnlo + mxlo) / 2;
                        double w = (mxlo - mnlo) * 111.32 * Math.Cos(lat * Math.PI / 180), h = (mxla - mnla) * 110.57;
                        size = Math.Sqrt(Math.Max(0, w * h));
                    }
                    else if (el.TryGetProperty("lat", out var la) && el.TryGetProperty("lon", out var lo)) { lat = la.GetDouble(); lon = lo.GetDouble(); point = true; }
                    else continue;
                    if (name.Length == 0 && size * size < MinKm2) continue;
                    islands[(type, id)] = (name, kind, lat, lon, size, point);
                }
            }
        // un'isola segnata sia come punto sia come contorno: si tiene il contorno
        var named = islands.Where(x => !x.Value.Point && x.Value.Name.Length > 0).Select(x => x.Value).ToList();
        var list = islands.Where(x => !(x.Value.Point && named.Any(n => n.Name == x.Value.Name && Math.Abs(n.Lat - x.Value.Lat) < 0.1 && Math.Abs(n.Lon - x.Value.Lon) < 0.1))).ToList();
        progress?.Report($"Checking {list.Count} islands on the terrain…");

        // 2) confronto col terreno
        using var probe = new WaterProbe(p);
        if (!probe.HasWater) throw new InvalidOperationException("No NewTerrain WaterHeightMap: build the terrain first");
        FileStream? wm = null; int wmSide = 0;
        if (File.Exists(watermapPath))
        {
            long len = new FileInfo(watermapPath).Length; int s = (int)Math.Round(Math.Sqrt(len));
            if ((long)s * s == len) { wm = new FileStream(watermapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096); wmSide = s; }
        }
        double sizeFt = sizeM * FtPerM;
        bool WmLand(double n, double e)
        {
            if (wm == null) return false;
            int r = Math.Clamp((int)((sizeFt - n) / sizeFt * wmSide), 0, wmSide - 1), c = Math.Clamp((int)(e / sizeFt * wmSide), 0, wmSide - 1);
            wm.Position = (long)r * wmSide + c;
            return wm.ReadByte() == 0;
        }
        var rows = new List<IslandRow>();
        try
        {
            foreach (var ((type, id), v) in list)
            {
                var (xm, ym) = grid.ToTheater(v.Lat, v.Lon);
                double n = ym * FtPerM, e = xm * FtPerM;
                if (xm < 0 || ym < 0 || xm >= sizeM || ym >= sizeM)
                {
                    rows.Add(new IslandRow(v.Name, v.Kind, type, id, v.Lat, v.Lon, v.SizeKm, n, e, 0, 0, 0, IslandState.Outside));
                    continue;
                }
                // centro + 8 punti a un quarto del lato (dentro la parte centrale dell'isola)
                double r = Math.Max(30, v.SizeKm * 1000 / 4) * FtPerM;
                var pts = new List<(double, double)> { (n, e) };
                if (!v.Point)
                    for (int a = -1; a <= 1; a++) for (int c = -1; c <= 1; c++) if (a != 0 || c != 0) pts.Add((n + a * r, e + c * r));
                int ntLand = pts.Count(q => !probe.IsWater(q.Item1, q.Item2)), wmLand = pts.Count(q => WmLand(q.Item1, q.Item2));
                var state = ntLand > 0 ? IslandState.Present : wm != null && wmLand > 0 ? IslandState.Flooded : IslandState.MissingInWatermap;
                rows.Add(new IslandRow(v.Name, v.Kind, type, id, v.Lat, v.Lon, Math.Round(v.SizeKm, 2), n, e, pts.Count, ntLand, wmLand, state));
            }
        }
        finally { wm?.Dispose(); }

        var inside = rows.Where(r => r.State != IslandState.Outside).ToList();
        summary.Insert(0, $"{inside.Count} OSM islands in the theater (named, or at least {MinKm2:0} km²): {inside.Count(r => r.State == IslandState.Present)} present, " +
                          $"{inside.Count(r => r.State == IslandState.MissingInWatermap)} missing in the Watermap, {inside.Count(r => r.State == IslandState.Flooded)} in the Watermap but under water in the terrain");
        foreach (var r in inside.Where(r => r.State != IslandState.Present).OrderByDescending(r => r.SizeKm).Take(40))
            summary.Add($"   {(r.Name.Length > 0 ? r.Name : "(no name)")} ({r.Kind}, {(r.SizeKm > 0 ? $"~{r.SizeKm:0.0} km" : "point only")}, {r.Lat:0.000} {r.Lon:0.000}): " +
                        (r.State == IslandState.Flooded ? "in the Watermap, under water in the terrain" : "missing in the Watermap"));
        Save(Path.Combine(p.ProjectFolder, FileName), rows);
        summary.Add("List: " + Path.Combine(p.ProjectFolder, FileName));
        return (rows, summary);
    }

    private static void Save(string path, List<IslandRow> rows)
    {
        var sb = new StringBuilder("Name;Kind;OSM;OSM id;Lat;Lon;Size (km);X (ft);Y (ft);Points;Land in terrain;Land in Watermap;State\r\n");
        foreach (var r in rows.OrderBy(r => r.State).ThenByDescending(r => r.SizeKm))
            sb.Append(r.Name.Replace(';', ',')).Append(';').Append(r.Kind).Append(';').Append(r.OsmType).Append(';').Append(r.OsmId).Append(';')
              .Append(r.Lat.ToString("0.00000", Ci)).Append(';').Append(r.Lon.ToString("0.00000", Ci)).Append(';').Append(r.SizeKm.ToString("0.00", Ci)).Append(';')
              .Append(r.X.ToString("0", Ci)).Append(';').Append(r.Y.ToString("0", Ci)).Append(';').Append(r.Samples).Append(';').Append(r.LandNt).Append(';')
              .Append(r.LandWatermap).Append(';').Append(r.State switch
              {
                  IslandState.Present => "present",
                  IslandState.MissingInWatermap => "missing in the Watermap",
                  IslandState.Flooded => "in the Watermap, under water in the terrain",
                  _ => "outside the theater",
              }).Append("\r\n");
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
