using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Theater;

/// <summary>One airport of OurAirports with its longest hard runway (feet, 0 = none).</summary>
public sealed record WorldAirport(string Ident, string Icao, string Name, string Type, string Country, double Lat, double Lon,
    double LongestHardFt, bool Military, bool Scheduled);

/// <summary>What to keep from OurAirports for a theater.</summary>
public sealed class AirportFilter
{
    /// <summary>Shortest hard runway kept (feet). 6000 ft ≈ 1830 m: what a fighter needs.</summary>
    public double MinRunwayFt { get; set; } = 6000;
    public bool Civil { get; set; } = true;
    public bool Military { get; set; } = true;
    /// <summary>Also small airfields (OurAirports "small_airport") when their runway is long enough.</summary>
    public bool Small { get; set; }
    /// <summary>Distance kept from the theater edge (km).</summary>
    public double EdgeMarginKm { get; set; } = 30;
    /// <summary>How many airbases get the Main tier (the rest are Secondary): military first, then large airports, longest runway first.</summary>
    public int MaxMain { get; set; } = 40;
}

/// <summary>
/// The OurAirports open data (public domain, ourairports.com): airports.csv, runways.csv and countries.csv,
/// downloaded once into a cache folder and read from there.
/// </summary>
public sealed class OurAirports
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static readonly string[] Files = { "airports.csv", "runways.csv", "countries.csv" };
    private static readonly string[] Mirrors =
    {
        "https://davidmegginson.github.io/ourairports-data/{0}",
        "https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/{0}",
    };
    /// <summary>Data older than this is downloaded again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(120);

    private static readonly Regex Hard = new(@"^(ASP|CON|PEM|BIT|PAV|BET|TAR|MAC|BRI|ASFALT|HARD|SEALED|CEMENT|ASFALTO)|ASPH|CONC", RegexOptions.IgnoreCase);
    private static readonly Regex MilitaryName = new(
        @"\b(air ?base|AB|AFB|air force|air station|naval air|army air|army airfield|military|airbase|air field|base aérienne|base aerea|base aérea|luftwaffe|fliegerhorst|NAS|MCAS|RAF|ANG)\b",
        RegexOptions.IgnoreCase);

    public List<WorldAirport> Airports { get; } = new();
    /// <summary>ISO country code → country name (countries.csv).</summary>
    public Dictionary<string, string> Countries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when all three files are in the folder and not too old.</summary>
    public static bool IsFresh(string dir) =>
        Files.All(f => File.Exists(Path.Combine(dir, f)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dir, f)) < MaxAge);

    /// <summary>Downloads the three files into <paramref name="dir"/> (each one written only when complete).</summary>
    public static async Task DownloadAsync(string dir, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        foreach (var f in Files)
        {
            Exception? last = null;
            foreach (var m in Mirrors)
            {
                string url = string.Format(Ci, m, f);
                try
                {
                    log?.Invoke($"Downloading {url}");
                    var bytes = await http.GetByteArrayAsync(url, ct);
                    if (bytes.Length < 1000) throw new InvalidDataException($"{f}: only {bytes.Length} bytes");
                    string tmp = Path.Combine(dir, f + ".part");
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, Path.Combine(dir, f), true);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException && !ct.IsCancellationRequested)
                {
                    last = ex;
                }
            }
            if (last != null) throw new IOException($"{f} could not be downloaded from OurAirports: {last.Message}", last);
        }
    }

    /// <summary>Reads the files of the cache folder (only airports with a hard runway, or with an unknown surface if large).</summary>
    public static OurAirports Load(string dir)
    {
        var res = new OurAirports();
        var countries = res.Countries;
        foreach (var r in ReadCsv(Path.Combine(dir, "countries.csv")))
            if (r.TryGetValue("code", out var c) && r.TryGetValue("name", out var n)) countries[c] = n;

        var longest = new Dictionary<string, double>();
        foreach (var r in ReadCsv(Path.Combine(dir, "runways.csv")))
        {
            if (r.GetValueOrDefault("closed") == "1") continue;
            string id = r.GetValueOrDefault("airport_ref", "");
            if (!double.TryParse(r.GetValueOrDefault("length_ft"), NumberStyles.Float, Ci, out double len) || len <= 0) continue;
            string surf = r.GetValueOrDefault("surface", "").Trim();
            // surface unknown: counted as hard (checked again below with the airport type)
            double v = Hard.IsMatch(surf) ? len : surf.Length == 0 || surf.Equals("UNK", StringComparison.OrdinalIgnoreCase) ? -len : 0;
            if (v == 0) continue;
            longest[id] = longest.TryGetValue(id, out var old) ? (Math.Abs(v) > Math.Abs(old) ? v : old) : v;
        }

        foreach (var r in ReadCsv(Path.Combine(dir, "airports.csv")))
        {
            string type = r.GetValueOrDefault("type", "");
            if (type is not ("large_airport" or "medium_airport" or "small_airport")) continue;
            if (!longest.TryGetValue(r.GetValueOrDefault("id", ""), out double rw)) continue;
            if (rw < 0) { if (type != "large_airport") continue; rw = -rw; }   // unknown surface: trusted only for large airports
            if (!double.TryParse(r.GetValueOrDefault("latitude_deg"), NumberStyles.Float, Ci, out double lat) ||
                !double.TryParse(r.GetValueOrDefault("longitude_deg"), NumberStyles.Float, Ci, out double lon)) continue;
            string name = r.GetValueOrDefault("name", "").Trim();
            string icao = r.GetValueOrDefault("icao_code", "");
            if (icao.Length == 0) icao = r.GetValueOrDefault("gps_code", "");
            string ident = r.GetValueOrDefault("ident", "");
            if (icao.Length == 0 && Regex.IsMatch(ident, "^[A-Z]{4}$")) icao = ident;
            string cc = r.GetValueOrDefault("iso_country", "");
            bool mil = MilitaryName.IsMatch(name) || Regex.IsMatch(r.GetValueOrDefault("keywords", ""), @"\bmilitary\b|\bair ?base\b", RegexOptions.IgnoreCase);
            res.Airports.Add(new WorldAirport(ident, icao, name, type, countries.GetValueOrDefault(cc, cc), lat, lon, rw, mil,
                r.GetValueOrDefault("scheduled_service") == "yes"));
        }
        return res;
    }

    /// <summary>Airports inside the theater (with the edge margin) that pass the filter, as airbases of the list (tab 2).</summary>
    public List<Airbase> Select(TheaterConfig theater, AirportFilter f)
    {
        var g = theater.CreateGrid();
        double size = theater.SizeMeters, m = f.EdgeMarginKm * 1000;
        // quick pre-filter on latitude/longitude (the theater square is never wider than its diagonal)
        double rKm = size / 1000 * 0.75;
        double dLat = rKm / 111.0, dLon = rKm / (111.0 * Math.Max(0.15, Math.Cos(theater.CenterLat * Math.PI / 180)));
        var list = new List<(Airbase B, double Rank)>();
        foreach (var a in Airports)
        {
            if (Math.Abs(a.Lat - theater.CenterLat) > dLat) continue;
            double dl = Math.Abs(((a.Lon - theater.CenterLon) % 360 + 540) % 360 - 180);
            if (dl > dLon) continue;
            if (a.LongestHardFt < f.MinRunwayFt) continue;
            if (a.Military ? !f.Military : !f.Civil) continue;
            if (a.Type == "small_airport" && !f.Small) continue;
            var (x, y) = g.ToTheater(a.Lat, a.Lon);
            if (x < m || y < m || x > size - m || y > size - m) continue;
            list.Add((new Airbase
            {
                Name = WorldPlaces.Ascii(a.Name), Country = WorldPlaces.Ascii(a.Country), Icao = a.Icao,
                Lat = a.Lat, Lon = a.Lon, Status = "ok", Tier = "Secondary",
            }, Rank(a)));
        }
        // the best ones are Main: military airbases first, then large airports, then the longest runways
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<Airbase>();
        foreach (var (b, _) in list.OrderByDescending(t => t.Rank))
        {
            if (res.Count < f.MaxMain) b.Tier = "Main";
            string n = b.Name;
            for (int k = 2; !used.Add(b.Name); k++) b.Name = $"{n} ({k})";
            res.Add(b);
        }
        return res;
    }

    /// <summary>Importance of an airport for a campaign: military, then large airports, then runway length.</summary>
    private static double Rank(WorldAirport a) =>
        (a.Military && a.LongestHardFt >= 8000 ? 3e6 : 0) + (a.Type == "large_airport" ? 2e6 : 0) + (a.Military ? 1e6 : 0) + a.LongestHardFt;

    /// <summary>CSV with a header line, quoted fields ("" inside quotes), commas.</summary>
    public static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var rd = new StreamReader(path, Encoding.UTF8);
        string? headLine = rd.ReadLine();
        if (headLine == null) yield break;
        var head = Split(headLine);
        string? line;
        while ((line = rd.ReadLine()) != null)
        {
            // a quoted field can span lines
            while (line.Count(c => c == '"') % 2 == 1 && rd.Peek() >= 0) line += "\n" + rd.ReadLine();
            var f = Split(line);
            var d = new Dictionary<string, string>(head.Count);
            for (int i = 0; i < head.Count && i < f.Count; i++) d[head[i]] = f[i];
            yield return d;
        }
    }

    private static List<string> Split(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }
}

/// <summary>Outline of a theater square on the globe (lat/lon), for drawing it on a world map.</summary>
public static class TheaterOutline
{
    /// <summary>The four edges of the square, <paramref name="perEdge"/> points each, starting from the south-west corner.</summary>
    public static List<(double Lat, double Lon)> Points(TheaterConfig t, int perEdge = 24)
    {
        var g = t.CreateGrid();
        double s = t.SizeMeters;
        var res = new List<(double, double)>();
        for (int e = 0; e < 4; e++)
            for (int i = 0; i < perEdge; i++)
            {
                double k = (double)i / perEdge * s;
                var (x, y) = e switch { 0 => (k, 0.0), 1 => (s, k), 2 => (s - k, s), _ => (0.0, s - k) };
                res.Add(g.ToLatLon(x, y));
            }
        return res;
    }

    /// <summary>Highest latitude a theater can be centred at here (the projection stretches too much beyond it).</summary>
    public const double MaxCenterLat = 70;
}
