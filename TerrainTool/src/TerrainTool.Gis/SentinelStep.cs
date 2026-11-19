using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using OSGeo.GDAL;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Theater;

namespace TerrainTool.Gis;

/// <summary>
/// Photoreal 4K da Sentinel-2 cloudless 2016 di EOX (CC BY 4.0, 10 m, senza nuvole). Scarica le tessere web
/// (Web Mercator, livello 13 ≈ 17 m/px alle nostre latitudini) in Sources\Imagery\S2C2016\z\x\y.jpg — una volta sola,
/// riprende da dove si era fermato — e compone le tessere Photoreal\4K del teatro (4096² DXT1, una ogni 64 km,
/// numerate da 1 riga per riga da nord-ovest, come le fa il lavoro "Photoreal 4K tiles"). Le tessere di solo mare si
/// saltano. Ogni pixel: coordinate del teatro → lat/lon (con la georeferenziazione del vecchio teatro) → pixel web.
/// Con testArea = true solo le tessere attorno a Kuwait International, per la prova.
/// Attribuzione obbligatoria: "Sentinel-2 cloudless - https://s2maps.eu by EOX IT Services GmbH
/// (Contains modified Copernicus Sentinel data 2016)".
/// </summary>
public sealed class Sentinel2PhotorealStep : ProcessingStep
{
    private readonly bool _test;
    public Sentinel2PhotorealStep(bool testArea) { _test = testArea; }

    public override string Id => _test ? "s2photoreal_test" : "s2photoreal";
    public override string Name => _test ? "Sentinel-2 photoreal 4K (test: Kuwait)" : "Sentinel-2 photoreal 4K";
    public override string Description => (_test
        ? "TEST on the 4K tiles around the airbase nearest to the theater centre only, to check imagery and alignment. "
        : "Whole theater (about 100,000+ web tiles, several GB, hours the first time; it resumes where it stopped). ")
        + "Downloads the EOX Sentinel-2 cloudless mosaic of the year chosen above (2016: CC BY 4.0; 2018-2024: CC BY-NC-SA 4.0, non-commercial), 10 m, "
        + "into Sources\\Imagery\\S2C<year> and writes Output\\NewTerrain\\Photoreal\\4K (replaces the NASA placeholder tiles it covers). "
        + "Where that year has no imagery (black: small Gulf islands...) it fills from the other years, and remakes the tiles already made with black parts. "
        + "Credit: Sentinel-2 cloudless by EOX IT Services GmbH (Contains modified Copernicus Sentinel data <year>).";

    /// <summary>Years EOX publishes (layer s2cloudless-&lt;year&gt;_3857); 2016 is the CC BY one.</summary>
    public static readonly int[] Years = { 2016, 2018, 2019, 2020, 2021, 2022, 2023, 2024 };
    public static int YearOf(GisContext ctx) => Years.Contains(ctx.Project.PhotorealYear) ? ctx.Project.PhotorealYear : 2016;
    public static string LicenseOf(int year) => year == 2016 ? "CC BY 4.0" : "CC BY-NC-SA 4.0 (non-commercial, share-alike)";
    private static string[] TemplatesOf(int year) => year == 2016 ? Templates : new[] { FallbackTpl.Replace("{layer}", $"s2cloudless-{year}_3857") };
    /// <summary>Fill order for black parts: the nearest years first, 2016 last.</summary>
    private static string[] FallbacksOf(int year) => year == 2016 ? FallbackLayers
        : Years.Where(y => y != year).OrderBy(y => y == 2016 ? 1 : 0).ThenBy(y => Math.Abs(y - year)).ThenByDescending(y => y)
               .Select(y => $"s2cloudless-{y}_3857").ToArray();
    private static string[] _fallbacks = FallbackLayers;
    /// <summary>Work folder of the tiles marked done: one per year, so another year makes them again.</summary>
    private static string DoneName(int year) => year == 2016 ? "s2_done" : "s2_done_" + year;

    /// <summary>Test area: the airbase of the project list nearest to the theater centre (Lat/Lon columns), else the centre.</summary>
    private static (double Lat, double Lon) TestPoint(GisContext ctx)
    {
        var t = ctx.Theater;
        (double, double) best = (t.CenterLat, t.CenterLon);
        try
        {
            string csv = ctx.Project.AirbasesCsv;
            if (!File.Exists(csv)) return best;
            var lines = File.ReadAllLines(csv);
            if (lines.Length < 2) return best;
            var h = lines[0].TrimStart('\uFEFF').Split(';');
            int cLa = Array.FindIndex(h, x => x.Trim().Equals("Lat", StringComparison.OrdinalIgnoreCase));
            int cLo = Array.FindIndex(h, x => x.Trim().Equals("Lon", StringComparison.OrdinalIgnoreCase));
            if (cLa < 0 || cLo < 0) return best;
            double bd = double.MaxValue;
            foreach (var l in lines.Skip(1))
            {
                var f = l.Split(';');
                if (f.Length <= Math.Max(cLa, cLo)) continue;
                if (!double.TryParse(f[cLa], NumberStyles.Float, CultureInfo.InvariantCulture, out double la) ||
                    !double.TryParse(f[cLo], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)) continue;
                double d = Math.Pow(la - t.CenterLat, 2) + Math.Pow((lo - t.CenterLon) * Math.Cos(t.CenterLat * Math.PI / 180), 2);
                if (d < bd) { bd = d; best = (la, lo); }
            }
        }
        catch (IOException) { }
        return best;
    }

    public const int Zoom = 13;
    private const int TilePx = Photoreal4KJob.TilePx;
    private const double TileKm = Photoreal4KJob.TileKm;
    private const int Grid = 64;   // lat/lon calcolati ogni 64 pixel e interpolati

    /// <summary>Strato 2016 (CC BY): si prova il primo che risponde. {z}/{y}/{x} come WMTS GoogleMapsCompatible.</summary>
    public static readonly string[] Templates =
    {
        "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg",
        "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2016_3857/default/g/{z}/{y}/{x}.jpg",
    };

    /// <summary>
    /// Anni successivi di EOX (CC BY-NC-SA 4.0: uso non commerciale, attribuzione, stessa licenza) usati SOLO dove il 2016
    /// non ha immagine: pixel neri (EOX mette nero dove non ha dati: piccole isole del Golfo, mare aperto) o tessera web che manca.
    /// In ordine: il primo che ha l'immagine riempie i pixel neri.
    /// </summary>
    public static readonly string[] FallbackLayers =
        { "s2cloudless-2020_3857", "s2cloudless-2018_3857", "s2cloudless-2021_3857", "s2cloudless-2022_3857", "s2cloudless-2023_3857", "s2cloudless-2019_3857" };
    private const string FallbackTpl = "https://tiles.maps.eox.at/wmts/1.0.0/{layer}/default/g/{z}/{y}/{x}.jpg";
    /// <summary>Pixel nero = nessun dato di EOX (con un po' di margine per il JPEG).</summary>
    private static bool IsBlack(byte[] t, int k) => t[k] <= 8 && t[k + 1] <= 8 && t[k + 2] <= 8;
    /// <summary>Sotto questo numero di pixel neri in una tessera web non si cerca altro (ombre, rumore).</summary>
    private const int MinBlack = 64;

    private static HttpClient? _http;
    /// <summary>Al massimo 4 scaricamenti insieme (piu' richieste insieme EOX le rifiuta).</summary>
    private static readonly SemaphoreSlim DownloadGate = new(4);
    private static string? _sourcesDir;
    private static CancellationToken _token;
    private static int _fallbackTiles, _stillBlackTiles, _fallbackFailed;
    private static long _fallbackPixels;
    private static readonly ConcurrentDictionary<string, int> FallbackByLayer = new();

    public override string? MissingInputs(GisContext ctx) => null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        var grid = ctx.Theater.CreateGrid();
        double sizeM = ctx.SizeMeters;
        int n = (int)Math.Round(ctx.Theater.SizeKm / TileKm);
        double px = sizeM / (n * TilePx);
        int year = YearOf(ctx);
        _fallbacks = FallbacksOf(year);
        string cache = Path.Combine(ctx.SourcesDir, "Imagery", "S2C" + year);
        res.Notes.Add($"Imagery: EOX Sentinel-2 cloudless {year} ({LicenseOf(year)})");
        string outDir = Path.Combine(ctx.Project.OutputNewTerrain, "Photoreal", "4K");
        Directory.CreateDirectory(outDir);

        // 1) quali tessere: terra (LandCoverMap) oppure, in prova, quelle attorno a Kuwait International
        var todo = new List<(int Tx, int Ty)>();
        if (_test)
        {
            var (tla, tlo) = TestPoint(ctx);
            var (kx, ky) = grid.ToTheater(tla, tlo);
            double tm = TileKm * 1000;
            for (int ty = 0; ty < n; ty++)
                for (int tx = 0; tx < n; tx++)
                {
                    double x0 = tx * tm, x1 = x0 + tm, yTop = sizeM - ty * tm, yBot = yTop - tm;
                    if (kx + 8000 > x0 && kx - 8000 < x1 && ky + 8000 > yBot && ky - 8000 < yTop) todo.Add((tx, ty));
                }
        }
        else
        {
            var land = LandTiles(ctx, n);
            for (int ty = 0; ty < n; ty++)
                for (int tx = 0; tx < n; tx++)
                    if (land == null || land[ty * n + tx]) todo.Add((tx, ty));
            if (land == null) res.Notes.Add("No water map of this grid yet (Watermap step): every tile is made, open sea too. Run the Watermap step first to skip the sea tiles");
            else res.Notes.Add($"{n * n - todo.Count} tiles of open sea skipped");
        }
        res.Notes.Add($"{todo.Count} tiles of 64 km to make ({px:0.##} m/px), web zoom {Zoom}");

        // 2) lat/lon dei punti di griglia di ogni tessera e tessere web necessarie
        var need = new HashSet<(int X, int Y)>();
        var lat = new Dictionary<(int, int), (double[] La, double[] Lo)>();
        int gpts = TilePx / Grid + 1;
        foreach (var (tx, ty) in todo)
        {
            var la = new double[gpts * gpts]; var lo = new double[gpts * gpts];
            for (int j = 0; j < gpts; j++)
                for (int i = 0; i < gpts; i++)
                {
                    double x = (tx * TilePx + i * Grid) * px, y = sizeM - (ty * TilePx + j * Grid) * px;
                    var (a, b) = grid.ToLatLon(x, y);
                    la[j * gpts + i] = a; lo[j * gpts + i] = b;
                    var (wx, wy) = WebPixel(a, b);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            need.Add(((int)Math.Floor(wx / 256) + dx, (int)Math.Floor(wy / 256) + dy));
                }
            lat[(tx, ty)] = (la, lo);
        }

        // 3) scaricamento (solo quelle che mancano)
        string tpl = Download(ctx, cache, need, p, res);

        // 4) composizione delle tessere
        _sourcesDir = ctx.SourcesDir; _token = ctx.Token;
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        _fallbackTiles = _stillBlackTiles = _fallbackFailed = 0; _fallbackPixels = 0; FallbackByLayer.Clear();
        // tessere 4K gia' fatte ma con parti nere (2016 senza dati): si rifanno, con gli anni successivi dove serve
        int redo = 0;
        if (!_test)
        {
            var have = new TerrainTool.Core.Editor.PhotoTiles(outDir, sizeM);
            foreach (var (tx, ty) in todo)
            {
                ctx.Token.ThrowIfCancellationRequested();
                int id = ty * n + tx + 1;
                string ok = Path.Combine(ctx.Work(DoneName(year)), id + ".ok");
                if (!File.Exists(ok) || ctx.Project.Theater.OlderThanGrid(ok)) continue;
                byte[]? px0 = null;
                try { px0 = have.GetChunk(tx, ty, 3, 0, 0, out _); } catch (Exception) { }
                if (px0 == null) continue;
                int black = 0;
                for (int k = 0; k < px0.Length; k += 4) if (px0[k] <= 8 && px0[k + 1] <= 8 && px0[k + 2] <= 8) black++;
                if (black > 64) { File.Delete(ok); redo++; }
            }
            if (redo > 0) res.Notes.Add($"{redo} tiles already made had black parts (no {year} imagery): made again, filling them from the other years");
        }
        int made = 0;
        var tile = new byte[(long)TilePx * TilePx * 3];
        var dec = new ConcurrentDictionary<(int, int), byte[]?>();
        long missingPx = 0;
        _decodeErrors = 0; while (DecodeMsgs.TryDequeue(out _)) { }
        string doneDir = ctx.Work(DoneName(year));
        Directory.CreateDirectory(doneDir);
        var incomplete = new List<string>();
        int skipped = 0;
        foreach (var (tx, ty) in todo)
        {
            ctx.Token.ThrowIfCancellationRequested();
            int idx0 = ty * n + tx + 1;
            // tessere gia' complete in un giro precedente: non si rifanno
            // (made before the theater grid changed: made again)
            if (!_test && File.Exists(Path.Combine(doneDir, idx0 + ".ok")) && File.Exists(Path.Combine(outDir, idx0 + ".dds"))
                && !ctx.Project.Theater.OlderThanGrid(Path.Combine(doneDir, idx0 + ".ok")))
            { skipped++; made++; continue; }
            var (la, lo) = lat[(tx, ty)];
            long tileMiss = 0;
            Parallel.For(0, TilePx, new ParallelOptions { CancellationToken = ctx.Token }, v =>
            {
                int gj = Math.Min(v / Grid, gpts - 2); double fj = (v - gj * Grid) / (double)Grid;
                int miss = 0;
                for (int u = 0; u < TilePx; u++)
                {
                    int gi = Math.Min(u / Grid, gpts - 2); double fi = (u - gi * Grid) / (double)Grid;
                    int k = gj * gpts + gi;
                    double a = Bil(la, k, gpts, fi, fj), b = Bil(lo, k, gpts, fi, fj);
                    var (wx, wy) = WebPixel(a, b);
                    long o = ((long)v * TilePx + u) * 3;
                    if (!Sample(cache, dec, wx, wy, tile, o)) miss++;
                }
                if (miss > 0) Interlocked.Add(ref tileMiss, miss);
            });
            int idx = idx0;
            missingPx += tileMiss;
            if (tileMiss == 0) File.WriteAllText(Path.Combine(doneDir, idx + ".ok"), "");
            else { File.Delete(Path.Combine(doneDir, idx + ".ok")); incomplete.Add($"{idx} ({tileMiss * 100.0 / ((long)TilePx * TilePx):0.#}% grey)"); }
            MipComposer.Compose(Path.Combine(outDir, idx + ".dds"), tile, TilePx, ColorDdsFormat.Dxt1, null, ctx.Token, makePreview: false);
            if (made == 0 || _test) res.Preview = SavePreview(ctx, tile, $"S2_4K_{idx}");
            res.Outputs.Add(Path.Combine(outDir, idx + ".dds"));
            made++;
            // la memoria delle tessere web decodificate si libera ogni tanto
            if (dec.Count > 1200) dec.Clear();
            p.Report(new StepProgress(0.5 + 0.5 * made / todo.Count, $"Photoreal tile {made}/{todo.Count}"));
        }
        res.Notes.Add($"{made - skipped} 4K tiles written to {outDir}" + (skipped > 0 ? $", {skipped} already complete from a previous run" : "")
                      + (missingPx > 0 ? $" ({missingPx} pixels without imagery, left grey)" : ""));
        if (incomplete.Count > 0)
        {
            res.Notes.Add($"Tiles with grey parts ({incomplete.Count}): {string.Join(", ", incomplete.Take(40))}" + (incomplete.Count > 40 ? " ..." : ""));
            res.Notes.Add("Run the step again: unreadable web tiles were renamed .bad and are downloaded again, only these tiles are remade.");
        }
        if (_decodeErrors > 0) res.Notes.Add($"Unreadable web tiles: {_decodeErrors}. First errors: " + string.Join(" | ", DecodeMsgs));
        if (_fallbackTiles > 0)
        {
            res.Notes.Add($"Black areas (no 2016 imagery) filled from later EOX years: {_fallbackTiles} web tiles, {_fallbackPixels:N0} pixels (" +
                          string.Join(", ", FallbackByLayer.OrderBy(k => k.Key).Select(k => $"{k.Key.Replace("_3857", "")}: {k.Value}")) + ")");
            res.Notes.Add("Credit for those parts: Sentinel-2 cloudless - https://s2maps.eu by EOX IT Services GmbH (Contains modified Copernicus Sentinel data, years in the list above), CC BY 4.0 for 2016, CC BY-NC-SA 4.0 (non-commercial) for 2018 and later");
        }
        if (_stillBlackTiles > 0) res.Notes.Add($"{_stillBlackTiles} web tiles still have black parts: no EOX year has imagery there (open sea, or a download failed: {_fallbackFailed} failed downloads, run again to retry)");
        res.Notes.Add($"Source: {tpl.Replace("{z}/{y}/{x}.jpg", "")} (EOX Sentinel-2 cloudless 2016, CC BY 4.0)");
        res.Notes.Add("Credit to put in the add-on: Sentinel-2 cloudless - https://s2maps.eu by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016)");
        if (_test) res.Notes.Add("Then: Build theater (or copy these .dds into NewTerrain\\Photoreal\\4K in BMS) and look at Kuwait in 3D.");
        return res;
    }

    private static double Bil(double[] g, int k, int w, double fx, double fy) =>
        (g[k] * (1 - fx) + g[k + 1] * fx) * (1 - fy) + (g[k + w] * (1 - fx) + g[k + w + 1] * fx) * fy;

    /// <summary>lat/lon → pixel web Mercator al livello Zoom.</summary>
    private static (double X, double Y) WebPixel(double lat, double lon)
    {
        double w = 256.0 * (1 << Zoom);
        double s = Math.Sin(lat * Math.PI / 180);
        return ((lon + 180) / 360 * w, (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * w);
    }

    /// <summary>Bilineare sulle tessere web decodificate (256² RGB).</summary>
    private static bool Sample(string cache, ConcurrentDictionary<(int, int), byte[]?> dec, double wx, double wy, byte[] dst, long o)
    {
        wx -= 0.5; wy -= 0.5;
        int x0 = (int)Math.Floor(wx), y0 = (int)Math.Floor(wy);
        double fx = wx - x0, fy = wy - y0;
        double r = 0, g = 0, b = 0, wsum = 0;
        for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                int X = x0 + dx, Y = y0 + dy;
                var t = Tile(cache, dec, X >> 8, Y >> 8);
                if (t == null) continue;
                double w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                int k = ((Y & 255) * 256 + (X & 255)) * 3;
                r += t[k] * w; g += t[k + 1] * w; b += t[k + 2] * w; wsum += w;
            }
        if (wsum < 1e-6) { dst[o] = dst[o + 1] = dst[o + 2] = 128; return false; }
        dst[o] = (byte)Math.Round(r / wsum); dst[o + 1] = (byte)Math.Round(g / wsum); dst[o + 2] = (byte)Math.Round(b / wsum);
        return true;
    }

    private static string TilePath(string cache, int x, int y) => Path.Combine(cache, Zoom.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y + ".jpg");

    private static readonly object GdalLock = new();
    private static int _decodeErrors;
    private static readonly ConcurrentQueue<string> DecodeMsgs = new();

    private static byte[]? Tile(string cache, ConcurrentDictionary<(int, int), byte[]?> dec, int x, int y) =>
        dec.GetOrAdd((x, y), key => FillBlack(Primary(cache, key.Item1, key.Item2), key.Item1, key.Item2));

    /// <summary>
    /// Pixel neri della tessera 2016 (o tutta la tessera, se manca) presi dal primo anno successivo che li ha.
    /// Le tessere degli altri anni si scaricano qui, solo dove servono, in Sources\Imagery\&lt;layer&gt;\z\x\y.jpg.
    /// </summary>
    private static byte[]? FillBlack(byte[]? t, int x, int y)
    {
        int black = 0;
        if (t != null) for (int k = 0; k < t.Length; k += 3) if (IsBlack(t, k)) black++;
        if (t != null && black < MinBlack) return t;
        if (_sourcesDir == null || _http == null) return t;
        var res = t != null ? (byte[])t.Clone() : null;
        int filled = 0, emptyInARow = 0;
        foreach (var layer in _fallbacks)
        {
            var f = Fallback(layer, x, y);
            // due anni di fila senza niente (tutto nero o assente): mare aperto, inutile scaricare gli altri
            int fb = 0;
            if (f != null) for (int k = 0; k < f.Length; k += 3) if (IsBlack(f, k)) fb++;
            if (f == null || fb > f.Length / 3 - MinBlack) { if (++emptyInARow >= 2) break; if (f == null) continue; }
            else emptyInARow = 0;
            if (res == null) { res = new byte[256 * 256 * 3]; Array.Fill(res, (byte)0); }
            int here = 0, left = 0;
            for (int k = 0; k < res.Length; k += 3)
            {
                if (!IsBlack(res, k)) continue;
                if (!IsBlack(f, k)) { res[k] = f[k]; res[k + 1] = f[k + 1]; res[k + 2] = f[k + 2]; here++; }
                else left++;
            }
            if (here > 0) { filled += here; FallbackByLayer.AddOrUpdate(layer, 1, (_, v) => v + 1); }
            if (left < MinBlack) break;
        }
        if (filled > 0) { Interlocked.Increment(ref _fallbackTiles); Interlocked.Add(ref _fallbackPixels, filled); }
        if (res != null)
        {
            int left = 0;
            for (int k = 0; k < res.Length; k += 3) if (IsBlack(res, k)) left++;
            if (left >= MinBlack) Interlocked.Increment(ref _stillBlackTiles);
        }
        return res;
    }

    /// <summary>Tessera web di un anno successivo: dalla cache o scaricata ora; null se EOX non ce l'ha o non risponde.</summary>
    private static byte[]? Fallback(string layer, int x, int y)
    {
        string f = Path.Combine(_sourcesDir!, "Imagery", layer, Zoom.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y + ".jpg");
        if (File.Exists(f + ".none")) return null;
        if (!File.Exists(f))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            string url = FallbackTpl.Replace("{layer}", layer).Replace("{z}", Zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture)).Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            bool got = false;
            for (int attempt = 0; attempt < 5 && !got; attempt++)
            {
                DownloadGate.Wait(_token);
                try
                {
                    using var r = _http!.GetAsync(url, _token).GetAwaiter().GetResult();
                    if ((int)r.StatusCode == 429 || (int)r.StatusCode >= 500) { Thread.Sleep(3000 * (attempt + 1)); continue; }
                    if (r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.BadRequest) { File.WriteAllText(f + ".none", ""); return null; }
                    r.EnsureSuccessStatusCode();
                    if (!(r.Content.Headers.ContentType?.MediaType ?? "").StartsWith("image", StringComparison.OrdinalIgnoreCase)) { File.WriteAllText(f + ".none", ""); return null; }
                    var data = r.Content.ReadAsByteArrayAsync(_token).GetAwaiter().GetResult();
                    File.WriteAllBytes(f + ".tmp", data);
                    File.Move(f + ".tmp", f, true);
                    got = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { if (attempt < 4) Thread.Sleep(2000 * (attempt + 1)); }
                finally { DownloadGate.Release(); }
            }
            if (!got) { Interlocked.Increment(ref _fallbackFailed); return null; }
        }
        try { lock (GdalLock) return Decode(f); }
        catch (Exception) { try { File.Move(f, f + ".bad", true); } catch { } return null; }
    }

    private static byte[]? Primary(string cache, int x, int y)
    {
        {
            string f = TilePath(cache, x, y);
            if (!File.Exists(f)) return null;
            // prima senza blocco; se GDAL fallisce, di nuovo da solo (letture parallele) e poi si rinuncia
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt == 0) return Decode(f);
                    lock (GdalLock) return Decode(f);
                }
                catch (Exception ex)
                {
                    if (attempt == 1)
                    {
                        if (Interlocked.Increment(ref _decodeErrors) <= 5) DecodeMsgs.Enqueue($"{Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)}: {ex.Message}");
                        // file rovinato: si cancella, al prossimo giro si riscarica
                        try { File.Move(f, f + ".bad", true); } catch { }
                    }
                }
            }
            return null;
        }
    }

    /// <summary>JPEG 256² → RGB (anche tessere a 1 banda grigia o con alfa).</summary>
    private static byte[] Decode(string f)
    {
        using var ds = Gdal.Open(f, Access.GA_ReadOnly) ?? throw new IOException("cannot open");
        int w = ds.RasterXSize, h = ds.RasterYSize, nb = ds.RasterCount;
        if (nb < 1) throw new IOException("no bands");
        var rgb = new byte[256 * 256 * 3];
        var band = new byte[256 * 256];
        for (int c = 0; c < 3; c++)
        {
            // letto direttamente a 256² anche se la tessera avesse un'altra dimensione
            ds.GetRasterBand(Math.Min(c + 1, nb)).ReadRaster(0, 0, w, h, band, 256, 256, 0, 0);
            for (int i = 0; i < band.Length; i++) rgb[i * 3 + c] = band[i];
        }
        return rgb;
    }

    /// <summary>Scarica le tessere web mancanti (4 alla volta, con ripetizioni). Restituisce il modello di URL usato.</summary>
    private static string Download(GisContext ctx, string cache, HashSet<(int X, int Y)> need, IProgress<StepProgress> p, StepResult res)
    {
        var missing = need.Where(t => !File.Exists(TilePath(cache, t.X, t.Y)) && !File.Exists(TilePath(cache, t.X, t.Y) + ".none")).ToList();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        // primo modello che risponde con un'immagine
        string? tpl = null;
        var probe = need.First();
        int year = YearOf(ctx);
        foreach (var t in TemplatesOf(year))
        {
            try
            {
                using var r = http.GetAsync(Url(t, probe.X, probe.Y), ctx.Token).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode && (r.Content.Headers.ContentType?.MediaType ?? "").StartsWith("image", StringComparison.OrdinalIgnoreCase)) { tpl = t; break; }
            }
            catch (Exception) when (!ctx.Token.IsCancellationRequested) { }
        }
        if (tpl == null) throw new IOException($"EOX Sentinel-2 cloudless {year} tiles not reachable (tried: " + string.Join(", ", TemplatesOf(year)) + ")");
        res.Notes.Add($"{need.Count} web tiles needed, {missing.Count} to download");
        int done = 0, failed = 0, none = 0; long bytes = 0;
        Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ctx.Token }, t =>
        {
            string f = TilePath(cache, t.X, t.Y);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using var r = http.GetAsync(Url(tpl, t.X, t.Y), ctx.Token).GetAwaiter().GetResult();
                    if (r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                    { File.WriteAllText(f + ".none", ""); Interlocked.Increment(ref none); break; }
                    r.EnsureSuccessStatusCode();
                    var data = r.Content.ReadAsByteArrayAsync(ctx.Token).GetAwaiter().GetResult();
                    File.WriteAllBytes(f + ".tmp", data);
                    File.Move(f + ".tmp", f, true);
                    Interlocked.Add(ref bytes, data.Length);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (attempt == 3) { Interlocked.Increment(ref failed); break; }
                    Thread.Sleep(2000 * (attempt + 1));
                }
            }
            int d = Interlocked.Increment(ref done);
            if (d % 50 == 0 || d == missing.Count)
                p.Report(new StepProgress(0.5 * d / Math.Max(1, missing.Count), $"Downloading Sentinel-2 tiles {d}/{missing.Count} ({bytes / 1048576} MB)"));
        });
        res.Notes.Add($"Downloaded {done - failed - none} tiles ({bytes / 1048576.0:0} MB)" + (none > 0 ? $", {none} not available" : "") + (failed > 0 ? $", {failed} FAILED (run again to retry)" : ""));
        return tpl;
    }

    private static string Url(string tpl, int x, int y) =>
        tpl.Replace("{z}", Zoom.ToString(CultureInfo.InvariantCulture)).Replace("{x}", x.ToString(CultureInfo.InvariantCulture)).Replace("{y}", y.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Tessere con terra. Prima la Watermap di questa stessa scheda (passo 2, 45 = mare): c'e' gia' in un teatro nuovo, mentre la
    /// LandCoverMap la fa la scheda 5 piu' tardi. Senza nessuna delle due null (tutte le tessere, mare aperto compreso: lento).
    /// </summary>
    private static bool[]? LandTiles(GisContext ctx, int n)
    {
        string wm = ctx.Out("Watermap.raw");
        if (File.Exists(wm) && !ctx.Project.Theater.OlderThanGrid(wm)) return Tiles(wm, n, 45);
        string lc = Path.Combine(ctx.Project.OutputNewTerrain, "GroundTypes", "LandCoverMap.raw");
        if (File.Exists(lc) && !ctx.Project.Theater.OlderThanGrid(lc)) return Tiles(lc, n, GroundTypes.Water);
        return null;
    }

    private static bool[]? Tiles(string raw, int n, byte sea)
    {
        long len = new FileInfo(raw).Length;
        int ls = (int)Math.Round(Math.Sqrt(len));
        if ((long)ls * ls != len || ls < n) return null;
        int per = ls / n, step = Math.Max(1, per / 256);
        var land = new bool[n * n];
        using var fs = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22);
        var row = new byte[ls];
        for (int y = 0; y < per * n; y += step)
        {
            fs.Position = (long)y * ls;
            fs.ReadExactly(row);
            for (int x = 0; x < per * n; x += step)
                if (row[x] != sea) land[(y / per) * n + x / per] = true;
        }
        return land;
    }

    /// <summary>Anteprima PNG 1024² della tessera.</summary>
    private static string SavePreview(GisContext ctx, byte[] tile, string name)
    {
        const int ps = 1024, f = TilePx / ps;
        var drv = Gdal.GetDriverByName("MEM");
        using var mem = drv.Create("", ps, ps, 3, DataType.GDT_Byte, null);
        var band = new byte[ps * ps];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < ps; y++)
                for (int x = 0; x < ps; x++)
                    band[y * ps + x] = tile[(((long)y * f) * TilePx + (long)x * f) * 3 + c];
            mem.GetRasterBand(c + 1).WriteRaster(0, 0, ps, ps, band, ps, ps, 0, 0);
        }
        string png = Path.Combine(ctx.PreviewDir, name + ".png");
        if (File.Exists(png)) File.Delete(png);
        using var outDs = Gdal.GetDriverByName("PNG").CreateCopy(png, mem, 0, null, null, null);
        return png;
    }
}
