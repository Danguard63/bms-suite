using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Un obiettivo con il luogo vero trovato in OpenStreetMap (citta', paese, villaggio, quartiere, isola) con lo stesso nome vicino al
/// punto dell'add-on. X nord, Y est in piedi del teatro (come gli obiettivi). Use = false: la riga resta ma Run Objects la ignora.
/// </summary>
public sealed record RealPlace(int CampId, string Name, string OsmName, string OsmKind, double Lat, double Lon, double X, double Y,
    double DistKm, double Score, bool Use = true);

/// <summary>
/// real_places.csv del progetto: luoghi veri (OSM, servizio Overpass) per gli obiettivi che l'add-on ha messo in acqua.
/// Run Objects porta l'obiettivo sul luogo vero (e da li', se serve, verso terra) invece di spostarlo solo verso la terra piu' vicina.
/// Gli obiettivi dell'add-on sono sbagliati anche di diversi km (le basi aeree spostate sulle piste vere: da 1 a 8 km), quindi
/// con la costa vera molti finiscono in mare.
/// </summary>
public static class RealPlaces
{
    public const string FileName = "real_places.csv";
    private const double FtPerM = 3.280839895;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const string Header = "CampId;Name;OSM name;OSM kind;Lat;Lon;X (ft);Y (ft);Distance (km);Name match;Use";

    /// <summary>Parole del tipo di obiettivo da togliere dal nome prima del confronto ("Ajman City" → "ajman").</summary>
    private static readonly Regex TypeWords = new(
        @"\b(city|town|village|port|factory|power ?plant|power ?station|power|plant|station|depot|army ?base|armybase|base|refinery|chemical|radar|radio ?tower|tower|" +
        @"sam ?site|site|intersection|junction|border|crossing|checkpoint|harts|industrial|area|complex|airbase|airport|airstrip|naval|navy|irgc|oil|terminal|" +
        @"transformer|manufacturer|settlement|camp|hq|bridge|dam|the|of|sector|nuke|missile|hawk|gosp|ngl|maintenenance|maintenance|\d+)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex Articles = new(@"\b(al|el|ar|as|ash|az|ad|an|at|ad|ye|e|bandar|jazireh|jazirat|jazeerat|jezireh|ras|khawr|khor|umm|abu|bin|bani|ibn)\b", RegexOptions.IgnoreCase);

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    /// <summary>Nome ridotto per il confronto: minuscolo, senza accenti, senza tipo e articoli, solo lettere.</summary>
    public static string Key(string name, bool keepArticles = false)
    {
        string s = name.Normalize(NormalizationForm.FormD);
        s = new string(s.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
        s = Regex.Replace(s, @"[-'’`_.,()]", " ");
        s = TypeWords.Replace(s, " ");
        if (!keepArticles) s = Articles.Replace(s, " ");
        s = Regex.Replace(s.ToLowerInvariant(), @"[^a-z ]", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Somiglianza 0..1 tra due nomi ridotti (Levenshtein normalizzato, senza spazi; contenimento = 0.9).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3) return 0;
        string x = a.Replace(" ", ""), y = b.Replace(" ", "");
        if (x == y) return 1;
        if ((x.Length >= 5 && y.Contains(x)) || (y.Length >= 5 && x.Contains(y))) return 0.9;
        var d = new int[x.Length + 1, y.Length + 1];
        for (int i = 0; i <= x.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= y.Length; j++) d[0, j] = j;
        for (int i = 1; i <= x.Length; i++)
            for (int j = 1; j <= y.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
        return 1 - (double)d[x.Length, y.Length] / Math.Max(x.Length, y.Length);
    }

    private sealed record OsmPlace(string Kind, double Lat, double Lon, List<string> Names);

    /// <summary>
    /// Cerca in OSM (Overpass) i luoghi con nome entro radiusKm da ogni obiettivo e sceglie quello col nome piu' simile (>= minScore),
    /// a parita' il piu' vicino. Obiettivi senza un nome utile (es. "HAWK Site SAM Site") restano senza luogo.
    /// </summary>
    public static async Task<(List<RealPlace> Found, List<string> Log, HashSet<int> Unanswered)> FindAsync(TheaterProject p,
        IReadOnlyList<(int Id, string Name, double X, double Y)> objs, double radiusKm, double minScore, CancellationToken ct, IProgress<string>? progress = null)
    {
        var log = new List<string>();
        var grid = p.Theater.CreateGrid();
        var pos = objs.Select(o => { var (la, lo) = grid.ToLatLon(o.Y / FtPerM, o.X / FtPerM); return (o, la, lo, key: Key(o.Name)); }).ToList();
        var ask = pos.Where(q => q.key.Length >= 3).ToList();
        log.Add($"{objs.Count} objectives, {ask.Count} with a usable name (the others, like 'HAWK Site SAM Site', keep the move to land)");
        if (ask.Count == 0) return (new List<RealPlace>(), log, new HashSet<int>());

        // a gruppi di obiettivi: una richiesta enorme fa scadere Overpass (e si aspetterebbero minuti senza notizie)
        var places = new List<OsmPlace>();
        const int batch = 12;
        int failed = 0;
        var unanswered = new HashSet<int>();
        // coda di gruppi: un gruppo senza risposta si riprova un obiettivo alla volta (le richieste piccole passano anche col server carico)
        var queue = new Queue<List<(int Id, string Name, double X, double Y)>>();
        var askList = ask.Select(x => x.o).ToList();
        for (int i = 0; i < askList.Count; i += batch) queue.Enqueue(askList.Skip(i).Take(batch).ToList());
        int nb = queue.Count, done = 0, singles = 0;
        var llOf = ask.ToDictionary(x => x.o.Id, x => (x.la, x.lo));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var grp = queue.Dequeue();
            string what = grp.Count == 1 ? $"single objective {grp[0].Name} ({++singles})" : $"group {++done} of {nb}";
            progress?.Report($"Asking OpenStreetMap: {what}…");
            var q = new StringBuilder($"[out:json][timeout:{(grp.Count == 1 ? 60 : 90)}];(");
            foreach (var o in grp)
            {
                var (la, lo) = llOf[o.Id];
                double dLa = radiusKm / 111.0, dLo = radiusKm / (111.0 * Math.Cos(la * Math.PI / 180));
                string bb = string.Format(Ci, "({0:0.#####},{1:0.#####},{2:0.#####},{3:0.#####})", la - dLa, lo - dLo, la + dLa, lo + dLo);
                q.Append("node[\"place\"][\"name\"]").Append(bb).Append(';');
                q.Append("way[\"place\"~\"^(island|islet)$\"][\"name\"]").Append(bb).Append(';');
                q.Append("way[\"natural\"=\"island\"][\"name\"]").Append(bb).Append(';');
                // le isole grandi (Kharg, Abadan, Abu Ali) sono relazioni multipoligono, non vie
                q.Append("relation[\"place\"~\"^(island|islet)$\"][\"name\"]").Append(bb).Append(';');
                q.Append("relation[\"natural\"=\"island\"][\"name\"]").Append(bb).Append(';');
            }
            q.Append(");out center tags;");
            string json;
            try { json = await Overpass(q.ToString(), ct, host => progress?.Report($"Asking OpenStreetMap: {what}, server {host}…")); }
            catch (HttpRequestException ex)
            {
                if (grp.Count > 1) { foreach (var o in grp) queue.Enqueue(new List<(int, string, double, double)> { o }); log.Add($"{what}: not answered, asking its objectives one at a time"); }
                else { failed++; unanswered.Add(grp[0].Id); log.Add($"{grp[0].Name}: not answered ({ex.Message})"); }
                continue;
            }
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
            {
                double la, lo;
                if (el.TryGetProperty("lat", out var a) && el.TryGetProperty("lon", out var bq)) { la = a.GetDouble(); lo = bq.GetDouble(); }
                else if (el.TryGetProperty("center", out var c)) { la = c.GetProperty("lat").GetDouble(); lo = c.GetProperty("lon").GetDouble(); }
                else continue;
                if (!el.TryGetProperty("tags", out var tags)) continue;
                var names = new List<string>();
                string kind = "";
                foreach (var t in tags.EnumerateObject())
                {
                    if (t.Name is "place") kind = t.Value.GetString() ?? "";
                    else if (t.Name is "natural" && kind.Length == 0) kind = t.Value.GetString() ?? "";
                    else if (t.Name is "name" or "name:en" or "int_name" or "alt_name" or "old_name" or "name:en:alt" or "official_name:en" or "alt_name:en")
                        foreach (var n in (t.Value.GetString() ?? "").Split(';')) if (n.Trim().Length > 0) names.Add(n.Trim());
                }
                if (names.Count > 0) places.Add(new OsmPlace(kind, la, lo, names));
            }
        }
        if (failed == ask.Count) throw new HttpRequestException("OpenStreetMap (Overpass) did not answer: try again in a few minutes");
        log.Add($"OpenStreetMap: {places.Count} named places and islands around them" + (failed > 0 ? $" ({failed} objectives not answered: run it again later for those)" : ""));

        var res = new List<RealPlace>();
        foreach (var (o, la, lo, key) in ask)
        {
            OsmPlace? best = null; string bestName = ""; double bestScore = 0, bestDist = 0;
            foreach (var pl in places)
            {
                double dist = Km(la, lo, pl.Lat, pl.Lon);
                if (dist > radiusKm) continue;
                foreach (var n in pl.Names)
                {
                    double s = Similarity(key, Key(n));
                    // le isole contano come isole: "Khark Island SAM Site" va sull'isola di Kharg anche se c'e' un villaggio omonimo
                    if (s >= minScore && Regex.IsMatch(o.Name, @"\bisland\b", RegexOptions.IgnoreCase) && pl.Kind is "island" or "islet") s += 0.05;
                    if (s > bestScore + 1e-9 || (Math.Abs(s - bestScore) < 1e-9 && best != null && dist < bestDist))
                    { best = pl; bestName = n; bestScore = s; bestDist = dist; }
                }
            }
            if (best == null || bestScore < minScore) continue;
            // un villaggio non e' la citta' da cui prende il nome ("Basrah Village" non va nel centro di Bassora)
            if (Regex.IsMatch(o.Name, @"\bvillage\b", RegexOptions.IgnoreCase) && best.Kind is "city") { log.Add($"{o.Name}: matched the city {bestName}, not used"); continue; }
            // un porto non va nel centro del paese di cui porta il nome: solo un'isola (Farsi Island IRGC Navy Port)
            if (Regex.IsMatch(o.Name, @"\bport\b", RegexOptions.IgnoreCase) && !UsableFor("Port", best.Kind)) { log.Add($"{o.Name}: matched the {best.Kind} {bestName}, not used for a port"); continue; }
            var (xm, ym) = grid.ToTheater(best.Lat, best.Lon);   // metri, x est, y nord
            res.Add(new RealPlace(o.Id, o.Name, bestName, best.Kind, best.Lat, best.Lon, ym * FtPerM, xm * FtPerM, Math.Round(bestDist, 2), Math.Round(Math.Min(1, bestScore), 2)));
        }
        // piu' obiettivi sullo stesso luogo (le tre "Dubai City", Al Ruwais citta' e porto, i due SAM di Khark): il piu' vicino va
        // sul luogo, gli altri si spostano della stessa quantita' e restano dov'erano rispetto a lui (non uno sopra l'altro)
        var byId = objs.ToDictionary(o => o.Id);
        foreach (var g in res.GroupBy(r => (r.Lat, r.Lon)).Where(g => g.Count() > 1).ToList())
        {
            var first = g.OrderBy(r => r.DistKm).First();
            var o0 = byId[first.CampId];
            double dN = first.X - o0.X, dE = first.Y - o0.Y;
            foreach (var r in g.Where(r => r != first))
            {
                var o = byId[r.CampId];
                int i = res.IndexOf(r);
                res[i] = r with { X = o.X + dN, Y = o.Y + dE, OsmName = r.OsmName + " (same shift as " + first.Name + ")" };
            }
            log.Add($"{g.Count()} objectives on {first.OsmName}: {first.Name} on the place, the others moved by the same amount");
        }
        log.Add($"{res.Count} objectives matched to a real place (name match {minScore:0.##} or better, within {radiusKm:0} km)");
        return (res.Where(r => !unanswered.Contains(r.CampId)).ToList(), log, unanswered);
    }

    /// <summary>Un luogo vero va bene per questo tipo di obiettivo? Un porto solo su un'isola: il centro del paese omonimo e' a terra.</summary>
    public static bool UsableFor(string objectiveType, string osmKind) =>
        !string.Equals(objectiveType, "Port", StringComparison.OrdinalIgnoreCase) || osmKind is "island" or "islet";

    /// <summary>
    /// Piu' obiettivi sullo stesso luogo (anche trovati in giri diversi): il piu' vicino resta sul luogo, gli altri si spostano della
    /// stessa quantita' (restano dov'erano rispetto a lui). orig = posizione dell'add-on (X nord, Y est, piedi).
    /// </summary>
    public static void SpreadShared(Dictionary<int, RealPlace> real, Func<int, (double X, double Y)?> orig)
    {
        foreach (var g in real.Values.GroupBy(r => (Math.Round(r.Lat, 6), Math.Round(r.Lon, 6))).Where(g => g.Count() > 1).ToList())
        {
            var first = g.OrderBy(r => r.DistKm).First();
            if (orig(first.CampId) is not { } o0) continue;
            var grid = (dN: first.X - o0.X, dE: first.Y - o0.Y);
            // il primo deve stare proprio sul luogo: se era gia' stato spostato da un giro precedente lo si lascia com'e'
            foreach (var r in g.Where(r => r != first))
            {
                if (orig(r.CampId) is not { } o) continue;
                string baseName = Regex.Replace(r.OsmName, @"\s*\(same shift as .*\)$", "");
                real[r.CampId] = r with { X = o.X + grid.dN, Y = o.Y + grid.dE, OsmName = baseName + " (same shift as " + first.Name + ")" };
            }
        }
    }

    private static double Km(double la1, double lo1, double la2, double lo2)
    {
        double r = Math.PI / 180, dla = (la2 - la1) * r, dlo = (lo2 - lo1) * r;
        double h = Math.Sin(dla / 2) * Math.Sin(dla / 2) + Math.Cos(la1 * r) * Math.Cos(la2 * r) * Math.Sin(dlo / 2) * Math.Sin(dlo / 2);
        return 6371 * 2 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    internal static async Task<string> Overpass(string query, CancellationToken ct, Action<string>? status = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TerrainTool-FalconBMS/1.0");
        var errors = new List<string>();
        for (int attempt = 0; attempt < 1; attempt++)
        {
            foreach (var ep in OsmRunways.Endpoints)
            {
                status?.Invoke(new Uri(ep).Host);
                try
                {
                    using var resp = await http.PostAsync(ep, new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) }), ct);
                    if (!resp.IsSuccessStatusCode) { errors.Add($"{new Uri(ep).Host}: {(int)resp.StatusCode} {resp.ReasonPhrase}"); continue; }
                    return await resp.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add($"{new Uri(ep).Host}: {ex.Message}");
                }
            }
            if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new HttpRequestException("OpenStreetMap (Overpass) servers busy or unreachable, try again in a few minutes: " + string.Join("; ", errors));
    }

    public static Dictionary<int, RealPlace> Load(string projectFolder)
    {
        var res = new Dictionary<int, RealPlace>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length < 10 || !int.TryParse(c[0], NumberStyles.Integer, Ci, out int id)) continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, Ci, out var v) ? v : 0;
            bool use = c.Length < 11 || !Regex.IsMatch(c[10].Trim(), "^(no|n|0|false|ignore|ignora)$", RegexOptions.IgnoreCase);
            res[id] = new RealPlace(id, c[1], c[2], c[3], D(4), D(5), D(6), D(7), D(8), D(9), use);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<RealPlace> rows)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.CampId).Append(';').Append(r.Name.Replace(';', ',')).Append(';').Append(r.OsmName.Replace(';', ',')).Append(';').Append(r.OsmKind).Append(';')
              .Append(r.Lat.ToString("0.000000", Ci)).Append(';').Append(r.Lon.ToString("0.000000", Ci)).Append(';')
              .Append(r.X.ToString("0.0", Ci)).Append(';').Append(r.Y.ToString("0.0", Ci)).Append(';')
              .Append(r.DistKm.ToString("0.00", Ci)).Append(';').Append(r.Score.ToString("0.00", Ci)).Append(';').Append(r.Use ? "YES" : "NO").Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
    }
}
