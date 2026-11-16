using System.Globalization;
using System.Text;

namespace TerrainTool.Core.NewTerrain;

/// <summary>
/// Analisi delle uscite della modalita' "Water DEM" di Terrain Builder confrontate con i suoi ingressi, per ricavarne gli algoritmi
/// (TB e' solo un eseguibile: non c'e' codice da leggere). Legge tutto a piena risoluzione, a bande di righe in parallelo, e scrive in
/// outDir un rapporto piccolo: summary.txt (istogrammi per tipo d'acqua, differenze DEM / Heightmap di TB, quote dell'acqua,
/// culling/FFT contro la quantita' d'acqua), le mappe ridotte a 2048 (campione centrale di ogni blocco) e tre ritagli a piena
/// risoluzione (costa, lago, acqua interna vicino al mare).
/// </summary>
public static class TbAnalysis
{
    public sealed record Map(string Key, string Path, int Size, int Bpp);

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Trova gli ingressi (cartella TB_Input del tool) e le uscite di TB; restituisce le mappe trovate e l'elenco di quelle che mancano.</summary>
    public static (List<Map> Maps, List<string> Missing) Find(string tbInput, string tbOutput)
    {
        var maps = new List<Map>(); var missing = new List<string>();
        string? F(string dir, params string[] names)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (var n in names)
            {
                var hit = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }
        void Add(string key, string? path, int bpp)
        {
            if (path == null) { missing.Add(key); return; }
            long px = new FileInfo(path).Length / bpp;
            int size = (int)Math.Round(Math.Sqrt(px));
            if ((long)size * size != px) { missing.Add($"{key} (not a square {bpp * 8}-bit RAW: {path})"); return; }
            maps.Add(new Map(key, path, size, bpp));
        }
        string? dem = Directory.Exists(tbInput)
            ? Directory.EnumerateFiles(tbInput).Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"_\d+\.raw$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && !Path.GetFileName(f).StartsWith("ForestMap", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("OceanDepth", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
            : null;
        Add("DEM", dem, 2);
        Add("Watermap", F(tbInput, "Watermap.raw"), 1);
        Add("Coastline", F(tbInput, "Coastline.raw"), 1);
        Add("TB_Heightmap", F(tbOutput, "Heightmap.raw"), 2);
        Add("TB_WaterHeightmap", F(tbOutput, "WaterHeightmap.raw"), 2);
        Add("TB_WaterReflection", F(tbOutput, "WaterReflectionDemRefmap.raw"), 2);
        Add("TB_DepthRed", F(tbOutput, "Depthmap_Red.raw"), 1);
        Add("TB_DepthBlue", F(tbOutput, "Depthmap_Blue.raw"), 1);
        Add("TB_GlobalAlphaBlue", F(tbOutput, "GlobalAlpha_Blue.raw"), 1);
        Add("TB_Culling", F(tbOutput, "CullingMap.raw"), 1);
        Add("TB_FFT", F(tbOutput, "FFTMap.raw"), 1);
        return (maps, missing);
    }

    private static int Cls(byte w) => w == 0 ? 0 : w == 25 ? 1 : w == 45 ? 2 : 3;
    private static readonly string[] ClsName = { "land(0)", "inland(25)", "ocean(45)", "other" };

    public static List<string> Run(string tbInput, string tbOutput, string outDir, int threads, IProgress<(double F, string Msg)> p, CancellationToken ct)
    {
        var notes = new List<string>();
        var (maps, missing) = Find(tbInput, tbOutput);
        Map? M(string k) => maps.FirstOrDefault(m => m.Key == k);
        var dem = M("DEM"); var water = M("Watermap");
        if (dem == null || water == null) throw new InvalidOperationException("Needs at least the DEM (TB_Input\\<theater>_<size>.RAW) and Watermap.raw");
        int N = dem.Size;
        if (water.Size != N) throw new InvalidOperationException($"Watermap {water.Size} and DEM {N} have different sizes");
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        sb.AppendLine($"Terrain Builder analysis  {DateTime.Now:yyyy-MM-dd HH:mm}   threads {threads}");
        foreach (var m in maps) sb.AppendLine($"  {m.Key,-20} {m.Size,6}^2 {m.Bpp * 8,2}-bit  {m.Path}");
        foreach (var m in missing) sb.AppendLine($"  missing: {m}");

        var full = maps.Where(m => m.Size == N).ToList();          // mappe alla risoluzione del DEM
        var u8full = full.Where(m => m.Bpp == 1 && m.Key != "Watermap").ToList();
        var tbh = M("TB_Heightmap") is { } th && th.Size == N ? th : null;
        int small = 2048, step = N / small;
        int w8 = M("TB_WaterHeightmap")?.Size ?? 0, f8 = w8 > 0 ? N / w8 : 0;

        // ── passata A: istogrammi a piena risoluzione e mappe ridotte
        var histU8 = u8full.ToDictionary(m => m.Key, _ => new long[4, 256]);
        const int DR = 3000;                                        // differenze in piedi tra -DR e +DR
        var histDelta = new long[4, 2 * DR + 1];
        var histTbh = new long[4, 2 * DR + 1];
        var histDem = new long[4, 2 * DR + 1];
        var classCount = new long[4];
        var smallMaps = full.ToDictionary(m => m.Key, m => new byte[small * small * m.Bpp]);
        var water8 = f8 > 0 ? new byte[(long)w8 * w8] : null;       // acqua alla risoluzione della WaterHeightmap
        var dem8min = f8 > 0 ? new short[(long)w8 * w8] : null;
        var tbh8min = f8 > 0 && tbh != null ? new short[(long)w8 * w8] : null;
        var wcount = new int[small * small]; var ocount = new int[small * small];
        int band = (int)Math.Ceiling(128.0 / step) * step;          // multiplo di step (e di f8): ogni banda scrive righe sue
        int bands = (N + band - 1) / band, done = 0;
        var lk = new object();
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, () =>
            (Streams: full.ToDictionary(m => m.Key, m => new FileStream(m.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)),
             U8: u8full.ToDictionary(m => m.Key, _ => new long[4, 256]), D: new long[4, 2 * DR + 1], T: new long[4, 2 * DR + 1], E: new long[4, 2 * DR + 1], C: new long[4]),
        (bi, _, loc) =>
        {
            int y0 = bi * band, h = Math.Min(band, N - y0);
            var buf = new Dictionary<string, byte[]>();
            foreach (var m in full)
            {
                var b = new byte[(long)h * N * m.Bpp];
                var fs = loc.Streams[m.Key];
                fs.Position = (long)y0 * N * m.Bpp;
                fs.ReadExactly(b);
                buf[m.Key] = b;
            }
            var wb = buf["Watermap"]; var db = buf["DEM"]; var tb = tbh != null ? buf[tbh.Key] : null;
            var u8b = u8full.Select(m => buf[m.Key]).ToArray(); var u8h = u8full.Select(m => loc.U8[m.Key]).ToArray();
            for (int y = 0; y < h; y++)
            {
                int gy = y0 + y;
                for (int x = 0; x < N; x++)
                {
                    long i = (long)y * N + x;
                    int c = Cls(wb[i]);
                    loc.C[c]++;
                    short dv = BitConverter.ToInt16(db, (int)(i * 2));
                    loc.E[c, Math.Clamp((int)dv, -DR, DR) + DR]++;
                    for (int k = 0; k < u8b.Length; k++) u8h[k][c, u8b[k][i]]++;
                    if (tb != null)
                    {
                        short tv = BitConverter.ToInt16(tb, (int)(i * 2));
                        loc.D[c, Math.Clamp(tv - dv, -DR, DR) + DR]++;
                        loc.T[c, Math.Clamp((int)tv, -DR, DR) + DR]++;
                    }
                    if (gy % step == step / 2 && x % step == step / 2)
                    {
                        int si = (gy / step) * small + x / step;
                        foreach (var m in full)
                            if (m.Bpp == 1) smallMaps[m.Key][si] = buf[m.Key][i];
                            else { smallMaps[m.Key][si * 2] = buf[m.Key][i * 2]; smallMaps[m.Key][si * 2 + 1] = buf[m.Key][i * 2 + 1]; }
                    }
                    if (wb[i] != 0) { int si = (gy / step) * small + x / step; Interlocked.Increment(ref wcount[si]); if (c == 2) Interlocked.Increment(ref ocount[si]); }
                    if (water8 != null)
                    {
                        long j = (long)(gy / f8) * w8 + x / f8;
                        bool first = gy % f8 == 0 && x % f8 == 0;
                        if (gy % f8 == f8 / 2 && x % f8 == f8 / 2) water8[j] = wb[i];
                        if (first || dv < dem8min![j]) dem8min![j] = dv;
                        if (tbh8min != null) { short tv = BitConverter.ToInt16(tb!, (int)(i * 2)); if (first || tv < tbh8min[j]) tbh8min[j] = tv; }
                    }
                }
            }
            int d = Interlocked.Increment(ref done);
            p.Report((0.75 * d / bands, $"Reading full-resolution maps {d}/{bands}"));
            return loc;
        },
        loc =>
        {
            foreach (var s in loc.Streams.Values) s.Dispose();
            lock (lk)
            {
                for (int c = 0; c < 4; c++)
                {
                    classCount[c] += loc.C[c];
                    for (int k = 0; k <= 2 * DR; k++) { histDelta[c, k] += loc.D[c, k]; histTbh[c, k] += loc.T[c, k]; histDem[c, k] += loc.E[c, k]; }
                    foreach (var m in u8full) for (int k = 0; k < 256; k++) histU8[m.Key][c, k] += loc.U8[m.Key][c, k];
                }
            }
        });
        ct.ThrowIfCancellationRequested();

        sb.AppendLine().AppendLine($"Pixels by watermap class: " + string.Join("  ", Enumerable.Range(0, 4).Select(c => $"{ClsName[c]} {classCount[c]:N0}")));
        void HistU8(string key)
        {
            sb.AppendLine().AppendLine($"== {key}: value → pixels, by watermap class (values with >0 pixels)");
            var h = histU8[key];
            for (int c = 0; c < 4; c++)
            {
                if (classCount[c] == 0) continue;
                var parts = Enumerable.Range(0, 256).Where(v => h[c, v] > 0).Select(v => $"{v}:{h[c, v]}");
                sb.AppendLine($"  {ClsName[c]}: " + string.Join(" ", parts));
            }
        }
        foreach (var m in u8full) HistU8(m.Key);
        void HistI(string title, long[,] h)
        {
            sb.AppendLine().AppendLine($"== {title} (feet, clamped to ±{DR}; value:pixels, only >0)");
            for (int c = 0; c < 4; c++)
            {
                if (classCount[c] == 0) continue;
                var parts = Enumerable.Range(0, 2 * DR + 1).Where(k => h[c, k] > 0).Select(k => $"{k - DR}:{h[c, k]}").ToList();
                sb.AppendLine($"  {ClsName[c]} ({parts.Count} distinct): " + string.Join(" ", parts.Count > 400 ? parts.Take(200).Append("…").Concat(parts.TakeLast(200)) : parts));
            }
        }
        HistI("Input DEM", histDem);
        if (tbh != null) { HistI("TB Heightmap minus input DEM", histDelta); HistI("TB Heightmap", histTbh); }

        // ── mappe a 8192 (quote dell'acqua): confronto con acqua e DEM dello stesso blocco
        foreach (var key in new[] { "TB_WaterHeightmap", "TB_WaterReflection" })
        {
            if (M(key) is not { } m || m.Size != w8 || water8 == null) continue;
            p.Report((0.8, "Reading " + key));
            var all = File.ReadAllBytes(m.Path);
            var hv = new long[4, 2 * DR + 1]; var hd = new long[4, 2 * DR + 1]; var ht = new long[4, 2 * DR + 1];
            var sm = new byte[small * small * 2]; int s8 = Math.Max(1, w8 / small); int sn = Math.Min(small, w8);
            for (long j = 0; j < (long)w8 * w8; j++)
            {
                short v = BitConverter.ToInt16(all, (int)(j * 2));
                int c = Cls(water8[j]);
                hv[c, Math.Clamp((int)v, -DR, DR) + DR]++;
                hd[c, Math.Clamp(v - dem8min![j], -DR, DR) + DR]++;
                if (tbh8min != null) ht[c, Math.Clamp(v - tbh8min[j], -DR, DR) + DR]++;
                long y = j / w8, x = j % w8;
                if (y % s8 == s8 / 2 && x % s8 == s8 / 2) { long si = (y / s8) * sn + x / s8; sm[si * 2] = all[j * 2]; sm[si * 2 + 1] = all[j * 2 + 1]; }
            }
            HistI($"{key} value ({w8}^2; class = watermap at the centre of the cell)", hv);
            HistI($"{key} minus min input DEM of the cell", hd);
            if (tbh8min != null) HistI($"{key} minus min TB Heightmap of the cell", ht);
            smallMaps[key] = sm;
        }

        // ── mappe a 2048 (culling, FFT): valore contro quantita' d'acqua nel blocco e distanza dalla terra
        var dist = DistanceToLand(wcount, step * step, small);
        foreach (var key in new[] { "TB_Culling", "TB_FFT" })
        {
            if (M(key) is not { } m || m.Size != small) continue;
            var all = File.ReadAllBytes(m.Path);
            smallMaps[key] = all;
            var tbl = new SortedDictionary<(int V, int Frac, int Dist, int Ocean), long>();
            for (int i = 0; i < small * small; i++)
            {
                int frac = (int)Math.Round(10.0 * wcount[i] / (step * step));
                int dd = Math.Min(dist[i], 99);
                int oc = ocount[i] * 2 >= wcount[i] && wcount[i] > 0 ? 1 : 0;
                var k = (all[i], frac, dd, oc);
                tbl[k] = tbl.GetValueOrDefault(k) + 1;
            }
            sb.AppendLine().AppendLine($"== {key}: value | water fraction of the 16x16 block (tenths) | distance to the nearest block with land (blocks, max 99) | mostly ocean → cells");
            foreach (var g in tbl.GroupBy(kv => kv.Key.V))
            {
                sb.AppendLine($"  value {g.Key}: {g.Sum(kv => kv.Value):N0} cells");
                foreach (var kv in g.OrderBy(kv => kv.Key.Dist).ThenBy(kv => kv.Key.Frac).Take(120))
                    sb.Append($"    f{kv.Key.Frac} d{kv.Key.Dist} o{kv.Key.Ocean}:{kv.Value}");
                sb.AppendLine();
            }
        }

        // ── mappe ridotte
        foreach (var (k, b) in smallMaps)
        {
            int bpp = b.Length / (small * small);
            File.WriteAllBytes(Path.Combine(outDir, $"small_{k}_{small}_{bpp * 8}bit.raw"), b);
        }
        File.WriteAllBytes(Path.Combine(outDir, $"small_WaterFractionCount_{small}_8bit.raw"), wcount.Select(v => (byte)Math.Min(255, v)).ToArray());

        // ── ritagli a piena risoluzione: costa, lago, acqua interna vicino al mare (scelti sulla mappa ridotta)
        var wsm = smallMaps["Watermap"];
        int win = Math.Min(64, small);
        (int X, int Y) Best(Func<int, int, double> score)
        {
            double best = -1; (int, int) at = (0, 0);
            for (int y = 0; y + win <= small; y += 16)
                for (int x = 0; x + win <= small; x += 16)
                {
                    double s = score(x, y);
                    if (s > best) { best = s; at = (x, y); }
                }
            return at;
        }
        double Count(int x0, int y0, Func<byte, bool> f) { int n = 0; for (int y = y0; y < y0 + win; y++) for (int x = x0; x < x0 + win; x++) if (f(wsm[y * small + x])) n++; return n; }
        double Edges(int x0, int y0, byte a, byte b)
        {
            int n = 0;
            for (int y = y0; y < y0 + win - 1; y++)
                for (int x = x0; x < x0 + win - 1; x++)
                {
                    byte v = wsm[y * small + x], r = wsm[y * small + x + 1], d = wsm[(y + 1) * small + x];
                    if (v == a && (r == b || d == b) || v == b && (r == a || d == a)) n++;
                }
            return n;
        }
        var c1 = Best((x, y) => Edges(x, y, 0, 45)); var c2 = Best((x, y) => Count(x, y, v => v == 25) * (Count(x, y, v => v == 0) > win * win / 4 ? 1 : 0.1));
        var c3 = Best((x, y) => Math.Min(Count(x, y, v => v == 25), Count(x, y, v => v == 45)) + Edges(x, y, 25, 45) * 4);
        var crops = new List<(string Name, int X, int Y)> { ("coast", c1.X, c1.Y), ("lake", c2.X, c2.Y), ("inland_near_sea", c3.X, c3.Y) };
        int cs = win * step;
        sb.AppendLine().AppendLine($"== Full-resolution crops ({cs}^2 at {N}; 8192 maps {cs / Math.Max(1, f8)}^2; 2048 maps {win}^2)");
        int ci = 0;
        foreach (var (name, sx, sy) in crops)
        {
            ct.ThrowIfCancellationRequested();
            p.Report((0.85 + 0.15 * ci++ / crops.Count, "Writing crop " + name));
            int x0 = sx * step, y0 = sy * step;
            sb.AppendLine($"  {name}: pixel x {x0}..{x0 + cs - 1}, y {y0}..{y0 + cs - 1} (rows from the top) at {N}");
            foreach (var m in maps)
            {
                int f = N / m.Size, ox = x0 / f, oy = y0 / f, w = cs / f;
                var outb = new byte[(long)w * w * m.Bpp];
                using var fs = new FileStream(m.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                for (int y = 0; y < w; y++)
                {
                    fs.Position = ((long)(oy + y) * m.Size + ox) * m.Bpp;
                    fs.ReadExactly(outb, (int)((long)y * w * m.Bpp), w * m.Bpp);
                }
                File.WriteAllBytes(Path.Combine(outDir, $"crop_{name}_{m.Key}_{w}_{m.Bpp * 8}bit.raw"), outb);
            }
        }
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sb.ToString(), new UTF8Encoding(false));
        notes.Add($"Analysis written to {outDir} ({Directory.GetFiles(outDir).Length} files)");
        if (missing.Count > 0) notes.Add("Missing: " + string.Join(", ", missing));
        return notes;
    }

    /// <summary>Distanza (in blocchi, metrica a scacchiera) di ogni blocco dal blocco piu' vicino che contiene terra.</summary>
    private static int[] DistanceToLand(int[] wcount, int full, int n)
    {
        var d = new int[n * n];
        var q = new Queue<int>();
        for (int i = 0; i < n * n; i++) { if (wcount[i] < full) { d[i] = 0; q.Enqueue(i); } else d[i] = int.MaxValue; }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % n, y = i / n;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                    int j = yy * n + xx;
                    if (d[j] > d[i] + 1) { d[j] = d[i] + 1; q.Enqueue(j); }
                }
        }
        return d;
    }
}
