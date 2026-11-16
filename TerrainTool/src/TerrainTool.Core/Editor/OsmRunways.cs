using System.Globalization;
using System.Text;
using System.Text.Json;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Piste reali da OpenStreetMap (aeroway=runway, servizio Overpass) per le basi che non hanno le testate in OurAirports:
/// righe nuove in airbase_targets.csv (stesso formato), cosi' il lavoro Objects le raddrizza e le sposta sulla pista vera.
/// Le righe gia' presenti non si toccano.
/// </summary>
public static class OsmRunways
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    /// <summary>Server Overpass: il principale e due copie pubbliche, provati in ordine (il principale a volte risponde 502/504 se e' carico).</summary>
    public static readonly string[] Endpoints =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
    };
    private const double FtPerM = 3.280839895;

    public sealed record OsmRunway(double LeLat, double LeLon, double HeLat, double HeLon, double LengthM, string Ref);

    /// <summary>Richiesta unica a Overpass con un riquadro per base. Restituisce tutte le piste trovate.</summary>
    public static async Task<List<OsmRunway>> FetchAsync(IEnumerable<(double Lat, double Lon)> centres, double radiusKm, CancellationToken ct)
    {
        var q = new StringBuilder("[out:json][timeout:180];(");
        foreach (var (la, lo) in centres)
        {
            double dLa = radiusKm / 111.0, dLo = radiusKm / (111.0 * Math.Cos(la * Math.PI / 180));
            q.Append(Ci, $"way[\"aeroway\"=\"runway\"]({la - dLa:0.#####},{lo - dLo:0.#####},{la + dLa:0.#####},{lo + dLo:0.#####});");
        }
        q.Append(");out geom;");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TerrainTool-FalconBMS/1.0");
        string? json = null;
        var errors = new List<string>();
        for (int attempt = 0; attempt < 2 && json == null; attempt++)
            foreach (var ep in Endpoints)
            {
                try
                {
                    using var resp = await http.PostAsync(ep, new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", q.ToString()) }), ct);
                    if (!resp.IsSuccessStatusCode) { errors.Add($"{new Uri(ep).Host}: {(int)resp.StatusCode} {resp.ReasonPhrase}"); continue; }
                    json = await resp.Content.ReadAsStringAsync(ct);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add($"{new Uri(ep).Host}: {ex.Message}");
                }
                if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        if (json == null) throw new HttpRequestException("OpenStreetMap (Overpass) servers busy or unreachable, try again in a few minutes: " + string.Join("; ", errors));
        using var doc = JsonDocument.Parse(json);
        var res = new List<OsmRunway>();
        foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (!el.TryGetProperty("geometry", out var g)) continue;
            var pts = g.EnumerateArray().Select(p => (La: p.GetProperty("lat").GetDouble(), Lo: p.GetProperty("lon").GetDouble())).ToList();
            if (pts.Count < 2) continue;
            // estremi: i due punti piu' lontani (la via di solito va da testata a testata)
            double D((double La, double Lo) a, (double La, double Lo) b)
            {
                double y = (a.La - b.La) * 111195, x = (a.Lo - b.Lo) * 111195 * Math.Cos((a.La + b.La) / 2 * Math.PI / 180);
                return Math.Sqrt(x * x + y * y);
            }
            var (pa, pb) = pts.SelectMany(a => pts.Select(b => (a, b))).MaxBy(t => D(t.a, t.b));
            string rf = el.TryGetProperty("tags", out var tg) && tg.TryGetProperty("ref", out var r) ? r.GetString() ?? "" : "";
            res.Add(new OsmRunway(pa.La, pa.Lo, pb.La, pb.Lo, D(pa, pb), rf));
        }
        return res;
    }

    /// <summary>
    /// Per ogni base (tipo airbase/airstrip, non incrocio) senza riga in airbase_targets.csv: la pista OSM piu' lunga tra quelle
    /// dell'aeroporto piu' vicino (entro <paramref name="radiusKm"/> dal centro della pista principale della base), aggiunta al file.
    /// </summary>
    public static async Task<List<string>> AddMissingTargetsAsync(EditorData d, double radiusKm, CancellationToken ct)
    {
        var log = new List<string>();
        var p = d.Project;
        string csv = Path.Combine(p.ProjectFolder, "airbase_targets.csv");
        var lines = File.Exists(csv) ? File.ReadAllLines(csv).ToList() : new List<string> { "CampId;Name;Icao;Airport;LeLat;LeLon;HeLat;HeLon;Note" };
        var head = lines[0].TrimStart('﻿').Split(';').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int cId = head.IndexOf("campid");
        if (cId < 0) { log.Add("airbase_targets.csv: no CampId column, nothing done"); return log; }
        // le righe messe da OpenStreetMap in un giro precedente si rifanno (quelle di OurAirports o scritte a mano restano)
        int cNote = head.IndexOf("note");
        int before = lines.Count;
        lines = lines.Take(1).Concat(lines.Skip(1).Where(l => { var f = l.Split(';'); return !(cNote >= 0 && f.Length > cNote && f[cNote].StartsWith("OSM", StringComparison.OrdinalIgnoreCase)); })).ToList();
        int redo = before - lines.Count;
        var have = lines.Skip(1).Select(l => l.Split(';')).Where(f => f.Length > cId && int.TryParse(f[cId], NumberStyles.Integer, Ci, out _))
            .Select(f => int.Parse(f[cId], Ci)).ToHashSet();
        var grid = p.Theater.CreateGrid();
        // si cerca attorno alla pista del vecchio add-on (posizione e disposizione originali), non a quella gia' spostata
        var old = new Dictionary<int, (double X, double Y, double H)>();
        string oldCamp = Path.Combine(p.OldTheaterFolder, "Campaign", "CampObjData.XML");
        if (File.Exists(oldCamp))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(oldCamp),
                @"<CampObj CampId=""(\d+)"">.*?<Heading>(.*?)</Heading>\s*<PositionX>(.*?)</PositionX>\s*<PositionY>(.*?)</PositionY>", System.Text.RegularExpressions.RegexOptions.Singleline))
                old[int.Parse(m.Groups[1].Value, Ci)] = (double.Parse(m.Groups[3].Value, Ci), double.Parse(m.Groups[4].Value, Ci), double.Parse(m.Groups[2].Value, Ci));
        var todo = new List<(EdObjective O, double Lat, double Lon, double Hdg, double LenM)>();
        foreach (var o in d.Objectives.Where(o => !o.Junction && o.Type is 1 or 2 && !have.Contains(o.Id)))
        {
            Runway main;
            string od = d.LayoutPath(o.OrigOcd);
            var orws = old.TryGetValue(o.Id, out var op) && Directory.Exists(od) ? TerrainTool.Core.Dds.ObjectsJob.LayoutRunways(od).Where(r => r.Len >= 2500).ToList() : null;
            if (orws != null && orws.Count > 0)
            {
                var r = orws.MaxBy(r => r.Len);
                double h = op.H * Math.PI / 180;
                main = new Runway(op.X + r.X * Math.Cos(h) - r.Y * Math.Sin(h), op.Y + r.X * Math.Sin(h) + r.Y * Math.Cos(h), ((r.Hdg + op.H) % 360 + 360) % 360, r.Len, r.Num);
            }
            else
            {
                var rws = d.WorldRunways(o);
                main = rws.Count > 0 ? rws.MaxBy(r => r.Len) : new Runway(o.X, o.Y, double.NaN, 0, 0);
            }
            var (la, lo) = grid.ToLatLon(main.Y / FtPerM, main.X / FtPerM);
            todo.Add((o, la, lo, main.Hdg, main.Len / FtPerM));
        }
        if (todo.Count == 0) { log.Add("All airbases already have a row in airbase_targets.csv"); return log; }
        log.Add($"Asking OpenStreetMap (Overpass) for the runways of {todo.Count} airbases without real runway data...");
        var osm = await FetchAsync(todo.Select(t => (t.Lat, t.Lon)), radiusKm, ct);
        log.Add($"{osm.Count} OSM runways received");
        int added = 0, replaced = 0;
        var add = new List<string>();
        foreach (var (o, la, lo, hdg, lenM) in todo)
        {
            double Km(double a1, double o1, double a2, double o2) { double y = (a1 - a2) * 111.195, x = (o1 - o2) * 111.195 * Math.Cos(a1 * Math.PI / 180); return Math.Sqrt(x * x + y * y); }
            // piste troppo corte (piste per elicotteri, aviosuperfici vicine) non contano: almeno 1200 m o meta' della pista del gioco
            double minLen = lenM > 0 ? Math.Min(1200, lenM * 0.5) : 800;
            var near = osm.Select(r => (R: r, Dist: Km(la, lo, (r.LeLat + r.HeLat) / 2, (r.LeLon + r.HeLon) / 2))).Where(t => t.Dist <= radiusKm && t.R.LengthM >= minLen).ToList();
            if (near.Count == 0) { log.Add($"   {o.Name}: no runway in OSM within {radiusKm:0} km, left as it is"); continue; }
            // la pista lunga e vicina: lunghezza meno 300 m per km di distanza (una pista principale a 5 km batte una corta a 1 km)
            var best = near.OrderByDescending(t => t.R.LengthM - 300 * t.Dist).First();
            var r = best.R;
            var (te1, tn1) = grid.ToTheater(r.LeLat, r.LeLon); var (te2, tn2) = grid.ToTheater(r.HeLat, r.HeLon);
            double osmHdg = Math.Atan2(te2 - te1, tn2 - tn1) * 180 / Math.PI;   // nel sistema del teatro, come la pista del gioco
            double turn = double.IsNaN(hdg) ? double.NaN : ((osmHdg - hdg) % 180 + 180) % 180;
            if (turn > 90) turn -= 180;
            add.Add(string.Join(";", o.Id.ToString(Ci), o.Name.Replace(';', ','), "", "", r.LeLat.ToString("0.000000", Ci), r.LeLon.ToString("0.000000", Ci),
                r.HeLat.ToString("0.000000", Ci), r.HeLon.ToString("0.000000", Ci), $"OSM runway {r.Ref} {r.LengthM:0} m"));
            added++;
            string what = "";
            if (o.SwapOcd >= 0 && d.SwapBase(o) is KoreaBase k)
            {
                // base coreana gia' scelta: la si rimette subito sulla pista vera (la posizione salvata vincerebbe sul lavoro Objects)
                var (e1, n1) = grid.ToTheater(r.LeLat, r.LeLon); var (e2, n2) = grid.ToTheater(r.HeLat, r.HeLon);
                var t = new Runway((n1 + n2) / 2 * FtPerM, (e1 + e2) / 2 * FtPerM, (Math.Atan2(e2 - e1, n2 - n1) * 180 / Math.PI + 360) % 360, r.LengthM * FtPerM, 0);
                if (KoreaBases.PlaceMain(new[] { t }, k) is Placement pl) { d.ApplySwap(o, k, pl); replaced++; what = $"; Korean base {k.Name} put on it"; }
            }
            else if (o.HadEdit) what = "; WARNING: it has a saved editor edit, which wins: use 'Forget saved edit' to use the OSM runway";
            log.Add($"   {o.Name}: OSM runway {r.Ref} {r.LengthM:0} m, {best.Dist:0.0} km from the game runway" + (double.IsNaN(turn) ? "" : $", turn {turn:+0.0;-0.0}°") + what);
        }
        if (added > 0)
        {
            if (File.Exists(csv)) File.Copy(csv, csv + ".bak", true);
            File.WriteAllLines(csv, lines.Concat(add), new UTF8Encoding(false));
        }
        if (replaced > 0) d.SaveEdits();
        log.Insert(0, $"airbase_targets.csv: {added} airbases from OpenStreetMap ({todo.Count - added} without an OSM runway{(redo > 0 ? $", {redo} OSM rows of the previous run redone" : "")}). Run Objects to move and turn them.");
        return log;
    }
}
