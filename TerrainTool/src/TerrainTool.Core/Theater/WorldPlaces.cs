using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Theater;

/// <summary>A place of the theater that can become a campaign objective (kind = BMS objective type name).</summary>
public sealed record Place(string Kind, string Name, string Country, double Lat, double Lon, string Info, string Source);

/// <summary>Kinds of places, named like the BMS objective types they will become.</summary>
public static class PlaceKinds
{
    public const string City = "City", Town = "Town", Port = "Port", PowerPlant = "Power plant", Nuclear = "Nuclear",
        Refinery = "Refinery", ArmyBase = "Army base";
    public static readonly string[] All = { City, Town, Port, PowerPlant, Nuclear, Refinery, ArmyBase };
}

/// <summary>What to look for in the area.</summary>
public sealed class PlaceFilter
{
    public int CityMinPopulation { get; set; } = 250_000;
    public int TownMinPopulation { get; set; } = 100_000;
    public bool Ports { get; set; } = true;
    public bool PowerPlants { get; set; } = true;
    /// <summary>Smallest power plant kept when its output is known (MW).</summary>
    public double PowerMinMw { get; set; } = 300;
    public bool Refineries { get; set; } = true;
    public bool ArmyBases { get; set; }
    public double EdgeMarginKm { get; set; } = 30;
}

/// <summary>
/// Cities and towns from GeoNames (cities15000, CC BY 4.0, downloaded once) and targets from OpenStreetMap (Overpass):
/// ports, power plants, refineries, military bases. Names are written in plain Latin letters (the BMS fonts have no others).
/// </summary>
public sealed class WorldPlaces
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public const string CitiesFile = "cities15000.txt";
    private const string CitiesUrl = "https://download.geonames.org/export/dump/cities15000.zip";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    public sealed record City(string Name, string Country, double Lat, double Lon, int Population, bool Capital);
    public List<City> Cities { get; } = new();

    public static bool IsFresh(string dir) =>
        File.Exists(Path.Combine(dir, CitiesFile)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dir, CitiesFile)) < MaxAge;

    public static async Task DownloadAsync(string dir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        byte[] zip;
        try { zip = await http.GetByteArrayAsync(CitiesUrl, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IOException("GeoNames cities could not be downloaded: " + ex.Message, ex);
        }
        using var za = new ZipArchive(new MemoryStream(zip));
        var entry = za.GetEntry(CitiesFile) ?? throw new IOException("cities15000.txt not found in the GeoNames download");
        string tmp = Path.Combine(dir, CitiesFile + ".part");
        entry.ExtractToFile(tmp, true);
        File.Move(tmp, Path.Combine(dir, CitiesFile), true);
    }

    /// <summary>Reads the GeoNames file; <paramref name="countries"/> turns ISO codes into names.</summary>
    public static WorldPlaces Load(string dir, IReadOnlyDictionary<string, string>? countries = null)
    {
        var res = new WorldPlaces();
        foreach (var line in File.ReadLines(Path.Combine(dir, CitiesFile), Encoding.UTF8))
        {
            var f = line.Split('\t');
            if (f.Length < 15) continue;
            string code = f[7];
            if (!code.StartsWith("PPL") || code is "PPLX" or "PPLH" or "PPLQ" or "PPLW" or "PPLCH") continue;
            if (!double.TryParse(f[4], NumberStyles.Float, Ci, out double lat) || !double.TryParse(f[5], NumberStyles.Float, Ci, out double lon)) continue;
            int.TryParse(f[14], NumberStyles.Integer, Ci, out int pop);
            string name = Ascii(f[2].Length > 0 ? f[2] : f[1]);
            if (name.Length == 0) continue;
            res.Cities.Add(new City(name, countries != null && countries.TryGetValue(f[8], out var cn) ? cn : f[8], lat, lon, pop, code == "PPLC"));
        }
        return res;
    }

    /// <summary>Cities and towns inside the theater.</summary>
    public List<Place> SelectCities(TheaterConfig t, PlaceFilter f)
    {
        var g = t.CreateGrid();
        double s = t.SizeMeters, m = f.EdgeMarginKm * 1000;
        var (dLat, dLon) = Window(t);
        var res = new List<Place>();
        foreach (var c in Cities.OrderByDescending(c => c.Population))
        {
            if (Math.Abs(c.Lat - t.CenterLat) > dLat || LonDiff(c.Lon, t.CenterLon) > dLon) continue;
            string? kind = c.Capital || c.Population >= f.CityMinPopulation ? PlaceKinds.City : c.Population >= f.TownMinPopulation ? PlaceKinds.Town : null;
            if (kind == null) continue;
            var (x, y) = g.ToTheater(c.Lat, c.Lon);
            if (x < m || y < m || x > s - m || y > s - m) continue;
            res.Add(new Place(kind, c.Name, c.Country, c.Lat, c.Lon, c.Population.ToString("N0", Ci) + " inhabitants" + (c.Capital ? ", capital" : ""), "GeoNames"));
        }
        return res;
    }

    /// <summary>Latitude/longitude half-window around the centre that surely holds the whole theater.</summary>
    private static (double DLat, double DLon) Window(TheaterConfig t)
    {
        double r = t.SizeMeters / 1000 * 0.75;
        return (r / 111.0, r / (111.0 * Math.Max(0.15, Math.Cos(t.CenterLat * Math.PI / 180))));
    }
    private static double LonDiff(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    /// <summary>Latitude/longitude box of the theater (south, west, north, east) for the Overpass query.</summary>
    public static (double S, double W, double N, double E) Bbox(TheaterConfig t)
    {
        var pts = TheaterOutline.Points(t, 16);
        return (pts.Min(p => p.Lat), pts.Min(p => p.Lon), pts.Max(p => p.Lat), pts.Max(p => p.Lon));
    }

    // ───────────────────────── OpenStreetMap ─────────────────────────

    /// <summary>One element found in OpenStreetMap (centre and tags).</summary>
    public sealed record OsmThing(string Kind, double Lat, double Lon, Dictionary<string, string> Tags, string Id = "");

    /// <summary>
    /// Asks Overpass for ports, power plants, refineries and military bases inside the box. The box is cut into cells
    /// (a whole 2048 km theater in one request is refused by the servers); a cell that still fails is cut in four.
    /// Each cell answered is kept in <paramref name="cacheDir"/> for a month, so a search stopped halfway goes on from there.
    /// </summary>
    public static async Task<List<OsmThing>> FetchOsmAsync((double S, double W, double N, double E) b, string? cacheDir = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        const double cellDeg = 4;
        int ny = Math.Max(1, (int)Math.Ceiling((b.N - b.S) / cellDeg)), nx = Math.Max(1, (int)Math.Ceiling((b.E - b.W) / cellDeg));
        var cells = new Queue<((double S, double W, double N, double E) Box, int Depth)>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
                cells.Enqueue(((b.S + (b.N - b.S) * iy / ny, b.W + (b.E - b.W) * ix / nx, b.S + (b.N - b.S) * (iy + 1) / ny, b.W + (b.E - b.W) * (ix + 1) / nx), 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        var all = new Dictionary<string, OsmThing>();
        int done = 0, total = cells.Count, ep = 0;
        while (cells.Count > 0)
        {
            var (cell, depth) = cells.Dequeue();
            progress?.Report($"OpenStreetMap: area {done + 1} of {total}…");
            string? json = null;
            string? cache = cacheDir == null ? null : Path.Combine(cacheDir, string.Create(Ci, $"osm_{cell.S:0.###}_{cell.W:0.###}_{cell.N:0.###}_{cell.E:0.###}.json"));
            if (cache != null && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(30)) json = await File.ReadAllTextAsync(cache, ct);
            var errors = new List<string>();
            for (int attempt = 0; json == null && attempt < Editor.OsmRunways.Endpoints.Length * 2; attempt++)
            {
                string url = Editor.OsmRunways.Endpoints[ep % Editor.OsmRunways.Endpoints.Length];
                try
                {
                    using var resp = await http.PostAsync(url, new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", Query(cell)) }), ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync(ct);
                        if (!body.Contains("runtime error", StringComparison.OrdinalIgnoreCase) || body.Length > 200_000) { json = body; break; }
                        errors.Add($"{new Uri(url).Host}: query too big");
                    }
                    else errors.Add($"{new Uri(url).Host}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    if ((int)resp.StatusCode == 429) await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add($"{new Uri(url).Host}: {ex.Message}");
                }
                ep++;
                // busy server or too big an area: a smaller cell is tried before insisting
                if (depth < 2 && attempt >= 1) break;
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            if (json == null)
            {
                if (depth >= 2) throw new HttpRequestException($"OpenStreetMap (Overpass) servers busy or unreachable ({done} of {total} areas done, they are kept: " +
                                                               "press the button again later to go on): " + string.Join("; ", errors.Distinct()));
                double ms = (cell.S + cell.N) / 2, mw = (cell.W + cell.E) / 2;
                cells.Enqueue(((cell.S, cell.W, ms, mw), depth + 1)); cells.Enqueue(((cell.S, mw, ms, cell.E), depth + 1));
                cells.Enqueue(((ms, cell.W, cell.N, mw), depth + 1)); cells.Enqueue(((ms, mw, cell.N, cell.E), depth + 1));
                total += 3;
                continue;
            }
            if (cache != null) { try { Directory.CreateDirectory(cacheDir!); await File.WriteAllTextAsync(cache, json, ct); } catch (IOException) { } }
            foreach (var t in ParseOsm(json)) all.TryAdd(t.Id.Length > 0 ? t.Id : $"{t.Kind}{t.Lat}{t.Lon}", t);
            done++;
        }
        progress?.Report($"OpenStreetMap: {all.Count} elements found in {total} areas.");
        return all.Values.ToList();
    }

    private static string Query((double S, double W, double N, double E) c)
    {
        string bb = string.Create(Ci, $"{c.S:0.####},{c.W:0.####},{c.N:0.####},{c.E:0.####}");
        return $"[out:json][timeout:180][bbox:{bb}];(" +
               "nwr[\"power\"=\"plant\"][\"plant:source\"!~\"solar|wind|biomass|biogas|waste\"];" +
               "nwr[\"industrial\"=\"refinery\"];" +
               "nwr[\"landuse\"=\"port\"];nwr[\"industrial\"=\"port\"];" +
               "nwr[\"military\"~\"^(base|naval_base|barracks)$\"][\"name\"];" +
               ");out center tags;";
    }

    public static List<OsmThing> ParseOsm(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("remark", out var rem) && (rem.GetString() ?? "").Contains("runtime error", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("OpenStreetMap (Overpass): " + rem.GetString());
        var res = new List<OsmThing>();
        foreach (var el in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            double lat, lon;
            if (el.TryGetProperty("center", out var c)) { lat = c.GetProperty("lat").GetDouble(); lon = c.GetProperty("lon").GetDouble(); }
            else if (el.TryGetProperty("lat", out var la) && el.TryGetProperty("lon", out var lo)) { lat = la.GetDouble(); lon = lo.GetDouble(); }
            else continue;
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el.TryGetProperty("tags", out var tg)) foreach (var p in tg.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
            string kind =
                tags.GetValueOrDefault("power") == "plant" ? (tags.GetValueOrDefault("plant:source", "").Contains("nuclear") ? PlaceKinds.Nuclear : PlaceKinds.PowerPlant) :
                tags.GetValueOrDefault("industrial") == "refinery" ? PlaceKinds.Refinery :
                tags.GetValueOrDefault("landuse") == "port" || tags.GetValueOrDefault("industrial") == "port" ? PlaceKinds.Port :
                tags.ContainsKey("military") ? PlaceKinds.ArmyBase : "";
            string id = (el.TryGetProperty("type", out var ty) ? ty.GetString() : "") + (el.TryGetProperty("id", out var idv) ? idv.GetRawText() : "");
            if (kind.Length > 0) res.Add(new OsmThing(kind, lat, lon, tags, id));
        }
        return res;
    }

    private static readonly Regex Mw = new(@"([\d.,]+)\s*(GW|MW|kW|W)?", RegexOptions.IgnoreCase);

    /// <summary>Electric output in MW from plant:output:electricity ("1200 MW", "1.2 GW"), NaN if unknown.</summary>
    public static double OutputMw(Dictionary<string, string> tags)
    {
        if (!tags.TryGetValue("plant:output:electricity", out var v)) return double.NaN;
        var m = Mw.Match(v);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, Ci, out double x)) return double.NaN;
        return m.Groups[2].Value.ToUpperInvariant() switch { "GW" => x * 1000, "KW" => x / 1000, "W" => x / 1e6, _ => x };
    }

    /// <summary>The OpenStreetMap targets inside the theater: filtered, merged when close, named in Latin letters.</summary>
    public List<Place> SelectOsm(IEnumerable<OsmThing> things, TheaterConfig t, PlaceFilter f)
    {
        var g = t.CreateGrid();
        double s = t.SizeMeters, m = f.EdgeMarginKm * 1000;
        var cands = new List<(Place P, double Weight, double MergeKm)>();
        foreach (var o in things)
        {
            bool want = o.Kind switch
            {
                PlaceKinds.Port => f.Ports,
                PlaceKinds.PowerPlant or PlaceKinds.Nuclear => f.PowerPlants,
                PlaceKinds.Refinery => f.Refineries,
                PlaceKinds.ArmyBase => f.ArmyBases,
                _ => false,
            };
            if (!want) continue;
            var (x, y) = g.ToTheater(o.Lat, o.Lon);
            if (x < m || y < m || x > s - m || y > s - m) continue;
            string info = "";
            double weight = 0;
            if (o.Kind is PlaceKinds.PowerPlant or PlaceKinds.Nuclear)
            {
                double mw = OutputMw(o.Tags);
                string src = o.Tags.GetValueOrDefault("plant:source", "");
                if (!double.IsNaN(mw))
                {
                    if (mw < f.PowerMinMw && o.Kind != PlaceKinds.Nuclear) continue;
                }
                else if (!Regex.IsMatch(src, "nuclear|coal|gas|oil") || !HasName(o.Tags)) continue;
                info = (src.Length > 0 ? src + ", " : "") + (double.IsNaN(mw) ? "output unknown" : mw.ToString("0", Ci) + " MW");
                weight = double.IsNaN(mw) ? 100 : mw;
            }
            else weight = HasName(o.Tags) ? 1 : 0;
            double merge = o.Kind switch { PlaceKinds.Port => 6, PlaceKinds.Refinery => 4, PlaceKinds.ArmyBase => 3, _ => 2 };
            var near = NearestCity(o.Lat, o.Lon, 150);
            string name = OsmName(o.Tags) ?? (near != null ? $"{o.Kind} near {near.Name}" : $"{o.Kind} {o.Lat:0.00} {o.Lon:0.00}");
            cands.Add((new Place(o.Kind, name, near?.Country ?? "", Math.Round(o.Lat, 6), Math.Round(o.Lon, 6), info, "OpenStreetMap"), weight, merge));
        }
        // close duplicates (one port or plant mapped as several areas): the biggest one stays
        var res = new List<Place>();
        foreach (var c in cands.OrderByDescending(c => c.Weight))
        {
            if (res.Any(r => r.Kind == c.P.Kind && Km(r.Lat, r.Lon, c.P.Lat, c.P.Lon) < c.MergeKm)) continue;
            res.Add(c.P);
        }
        // same name twice: numbered
        foreach (var grp in res.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            int k = 1;
            foreach (var p in grp.ToList()) { int i = res.IndexOf(p); res[i] = p with { Name = $"{p.Name} {k++}" }; }
        }
        return res;
    }

    private City? NearestCity(double lat, double lon, double maxKm)
    {
        City? best = null; double bd = maxKm;
        foreach (var c in Cities)
        {
            if (Math.Abs(c.Lat - lat) > 1.5 || LonDiff(c.Lon, lon) > 2.5) continue;
            double d = Km(lat, lon, c.Lat, c.Lon);
            // the bigger city wins when nearly as close
            d -= Math.Log10(Math.Max(10, c.Population)) * 2;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    private static bool HasName(Dictionary<string, string> t) => OsmName(t) != null;
    private static string? OsmName(Dictionary<string, string> t)
    {
        foreach (var k in new[] { "name:en", "int_name", "name:latin", "name" })
            if (t.TryGetValue(k, out var v) && Ascii(v) is { Length: > 2 } a && Latin(v)) return a;
        return null;
    }

    private static bool Latin(string s) => s.Normalize(NormalizationForm.FormD).All(c => c < 0x250 || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark);

    public static double Km(double la1, double lo1, double la2, double lo2)
    {
        double y = (la1 - la2) * 111.195, x = LonDiff(lo1, lo2) * 111.195 * Math.Cos((la1 + la2) / 2 * Math.PI / 180);
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Plain ASCII: accents removed, special letters spelled out; other scripts dropped.</summary>
    public static string Ascii(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            string? r = ch switch
            {
                'ß' => "ss", 'ø' => "o", 'Ø' => "O", 'ł' => "l", 'Ł' => "L", 'đ' => "d", 'Đ' => "D", 'ı' => "i", 'æ' => "ae", 'Æ' => "AE",
                'œ' => "oe", 'Œ' => "OE", 'þ' => "th", 'ð' => "d", '‘' or '’' or 'ʼ' or 'ʻ' => "'", '–' or '—' => "-", _ => null,
            };
            if (r != null) sb.Append(r);
            else if (ch < 128 && ch != ';' && ch != '"') sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}

/// <summary>places.csv of the project: the places chosen for the campaign objectives.</summary>
public static class PlacesCsv
{
    public const string FileName = "places.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void Save(string path, IEnumerable<Place> places)
    {
        var sb = new StringBuilder("Kind;Name;Country;Lat;Lon;Info;Source\n");
        foreach (var p in places)
            sb.Append(string.Join(';', p.Kind, p.Name, p.Country, p.Lat.ToString("0.######", Ci), p.Lon.ToString("0.######", Ci), p.Info.Replace(';', ','), p.Source)).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static List<Place> Load(string path)
    {
        var res = new List<Place>();
        if (!File.Exists(path)) return res;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(';');
            if (f.Length < 5 || !double.TryParse(f[3], NumberStyles.Float, Ci, out double la) || !double.TryParse(f[4], NumberStyles.Float, Ci, out double lo)) continue;
            res.Add(new Place(f[0], f[1], f[2], la, lo, f.Length > 5 ? f[5] : "", f.Length > 6 ? f[6] : ""));
        }
        return res;
    }
}
