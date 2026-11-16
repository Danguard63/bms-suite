using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TerrainTool.Core.NewTerrain;

/// <summary>Avviso o errore trovato prima di partire (Error = non si parte).</summary>
public sealed record WaterDemCheck(bool Error, string Text);

/// <summary>Avanzamento: totale 0..1, passo corrente, avanzamento del passo 0..1.</summary>
public readonly record struct WaterDemProgress(double Total, string Step, double StepFraction);

public sealed class WaterDemOptions
{
    public string DemPath { get; set; } = "";
    public string WatermapPath { get; set; } = "";
    public string? CoastlinePath { get; set; }
    /// <summary>Punti della costa (CoastPoints.shp, tab 4): posizioni delle onde di riva nell'FFTMap. Serve Watermap.hdr per la georeferenza.</summary>
    public string? CoastPointsPath { get; set; }
    /// <summary>Cartella di uscita con la struttura di TB: HeightMaps, WaterMaps, GroundTypes.</summary>
    public string OutDir { get; set; } = "";
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);
    /// <summary>Profondita' del fondale sotto l'acqua lontano dalla riva (TB: 40 ft).</summary>
    public int DepthFt { get; set; } = 40;
    /// <summary>Quanto l'acqua sta sotto la terra dove non c'e' acqua (TB: ~300 ft).</summary>
    public int HiddenWaterFt { get; set; } = 300;
    /// <summary>Distanza minima dalla terra (in km, celle della CullingMap) per non disegnare il fondale (TB: ~10.5).</summary>
    public double CullingKm { get; set; } = 10.5;
    /// <summary>Quota minima e massima attese nel teatro (piedi): fuori, avviso.</summary>
    public int MinHeightFt { get; set; } = -1500;
    public int MaxHeightFt { get; set; } = 30000;
}

/// <summary>
/// Modalita' "Water DEM" di Terrain Builder rifatta in TerrainTool (regole ricavate dal confronto ingressi/uscite di TB:
/// docs\TB_WaterDEM_spec.txt). Dal DEM (int16 piedi) e dalla Watermap (0 terra, 25 acqua interna, 45 mare) scrive:
///   HeightMaps\Heightmap.raw (fondale sotto l'acqua), HeightMaps\WaterHeightmap.raw e WaterReflectionDemRefmap.raw (N/4),
///   WaterMaps\Depthmap_Red.raw, Depthmap_Blue.raw, GroundTypes\GlobalAlpha_Blue.raw (N), WaterMaps\CullingMap.raw, FFTMap.raw (2048).
/// Lavora a bande di righe in parallelo (con un bordo per le distanze dalla riva): la memoria resta contenuta anche a 32768.
/// </summary>
public static class WaterDem
{
    public const int Halo = 48;          // pixel di bordo per le distanze (le fasce piu' larghe sono ~21 px)
    public const int Band = 256;
    public const float ShoreSmoothPx = 3.5f;
    public const float SurfaceReachPx = 11f; // TB: i pixel di terra entro ~11 px dall'acqua portano la superficie (poi media 4x4)
    public const int Grid2048 = 2048;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>
    /// Profilo del fondale del mare dalla linea di costa (Coastline.raw; distanza in pixel → piedi sotto il DEM), dal run di TB
    /// sul Mideast. Lontano dalla costa, e nelle acque interne, il fondale e' DepthFt (40) sotto il DEM; il primo pixel accanto
    /// alla terra ~27.
    /// </summary>
    private static readonly (double D, double Ft)[] Ramp =
        { (0, 0), (1, 1), (3, 3), (5, 4), (7, 5), (9, 6), (11, 7), (13, 9), (15, 10), (17, 14), (19, 33), (21, 40) };

    private static readonly int[] Binom5 = { 1, 4, 6, 4, 1 };
    /// <summary>Media binomiale 5x5 (pesi 1-4-6-4-1, somma 256) troncata verso il basso; ai bordi si ripete il pixel del bordo.</summary>
    public static int ShoreSmooth(short[] hgt, int n, int rows, int x, int y)
    {
        long sum = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            int yy = Math.Clamp(y + dy, 0, rows - 1);
            for (int dx = -2; dx <= 2; dx++)
                sum += Binom5[dy + 2] * Binom5[dx + 2] * hgt[(long)yy * n + Math.Clamp(x + dx, 0, n - 1)];
        }
        return (int)Math.Floor(sum / 256.0);
    }

    public static int DepthAt(double dCoast, double dLand, int depthFt, bool sea)
    {
        int edge = dLand < 1.2 ? (int)Math.Round(27.0 * depthFt / 40) : depthFt;
        return sea ? Math.Min(edge, RampAt(dCoast, depthFt)) : edge;
    }

    public static int RampAt(double d, int depthFt)
    {
        if (d >= Ramp[^1].D) return depthFt;
        for (int i = 1; i < Ramp.Length; i++)
            if (d <= Ramp[i].D)
            {
                double t = (d - Ramp[i - 1].D) / (Ramp[i].D - Ramp[i - 1].D);
                double ft = Ramp[i - 1].Ft + t * (Ramp[i].Ft - Ramp[i - 1].Ft);
                return (int)Math.Round(ft * depthFt / 40.0);
            }
        return depthFt;
    }

    private static int SideOf(string path, int bpp)
    {
        long px = new FileInfo(path).Length / bpp;
        int s = (int)Math.Round(Math.Sqrt(px));
        return (long)s * s == px ? s : -1;
    }

    // ───────────────────────── controlli prima di partire ─────────────────────────

    public static List<WaterDemCheck> Preflight(WaterDemOptions o)
    {
        var res = new List<WaterDemCheck>();
        void E(string t) => res.Add(new WaterDemCheck(true, t));
        void W(string t) => res.Add(new WaterDemCheck(false, t));
        if (!File.Exists(o.DemPath)) { E($"DEM not found: {o.DemPath} (tab 4: Heightmap)"); return res; }
        if (!File.Exists(o.WatermapPath)) { E($"Watermap not found: {o.WatermapPath} (tab 4: Watermap)"); return res; }
        int n = SideOf(o.DemPath, 2), nw = SideOf(o.WatermapPath, 1);
        if (n < 0) E($"The DEM is not a square 16-bit RAW ({new FileInfo(o.DemPath).Length:N0} bytes)");
        if (nw < 0) E($"The Watermap is not a square 8-bit RAW ({new FileInfo(o.WatermapPath).Length:N0} bytes)");
        if (n > 0 && nw > 0 && n != nw) E($"DEM {n}x{n} and Watermap {nw}x{nw} have different sizes: rebuild them with the same size in tab 4");
        if (n > 0 && (n & (n - 1)) != 0) E($"Size {n} is not a power of two");
        if (n > 0 && n < Grid2048 * 4) W($"Size {n}: this is a test run (BMS needs 32768); CullingMap/FFTMap are still 2048");
        if (o.CoastlinePath == null || !File.Exists(o.CoastlinePath)) W("No Coastline.raw (tab 4: Coastline): the sea floor will have no gentle slope at the beaches");
        else if (SideOf(o.CoastlinePath, 1) != n) W("Coastline.raw has another size: not used (rebuild it in tab 4)");
        if (o.CoastPointsPath == null || !File.Exists(o.CoastPointsPath)) W("No CoastPoints.shp (tab 4: Coastline): FFTMap without shore waves");
        else if (ReadEnviGeo(o.WatermapPath) == null) W("Watermap.hdr missing or without 'map info': CoastPoints.shp cannot be placed (no shore waves)");
        if (string.IsNullOrWhiteSpace(o.OutDir)) E("No output folder");
        if (res.Any(r => r.Error)) return res;

        // spazio su disco: Heightmap 2 B/px, tre mappe a 1 B/px, due a N/4, due a 2048
        long need = (long)n * n * 5 + (long)(n / 4) * (n / 4) * 4 + 2L * Grid2048 * Grid2048;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(o.OutDir));
            if (root != null)
            {
                var di = new DriveInfo(root);
                long have = di.AvailableFreeSpace;
                foreach (var f in new[] { @"HeightMaps\Heightmap.raw", @"WaterMaps\Depthmap_Red.raw", @"WaterMaps\Depthmap_Blue.raw", @"GroundTypes\GlobalAlpha_Blue.raw" })
                {
                    var p = Path.Combine(o.OutDir, f);
                    if (File.Exists(p)) have += new FileInfo(p).Length;   // si sovrascrivono
                }
                if (have < need) E($"Not enough disk space on {root}: needs {need / 1073741824.0:0.0} GB, free {have / 1073741824.0:0.0} GB");
                else if (have < need * 1.2) W($"Disk {root} almost full after the run ({(have - need) / 1073741824.0:0.0} GB left)");
            }
        }
        catch (Exception ex) { W("Free disk space not checked: " + ex.Message); }

        // memoria: per thread una banda con bordo (DEM, acqua, distanze) + le griglie N/4 e 2048
        long perThread = (long)(Band + 2 * Halo) * n * 24 + (long)Band * n * 5;
        long fixedMem = (long)(n / 4) * (n / 4) * 16 + (long)Grid2048 * Grid2048 * 16;
        long avail = AvailableMemory();
        if (avail > 0)
        {
            int fit = (int)Math.Max(1, (avail * 0.7 - fixedMem) / perThread);
            if (fit < o.Threads) W($"RAM: {avail / 1073741824.0:0.0} GB free is enough for {fit} threads at a time, not {o.Threads}: {fit} will be used");
            if (avail * 0.9 < fixedMem + perThread) E($"Not enough free RAM ({avail / 1073741824.0:0.0} GB): close other programs");
        }

        // valori della Watermap (campione di righe) e quote del DEM
        var counts = new long[256];
        long sampled = 0; int lo = int.MaxValue, hi = int.MinValue;
        using (var fw = new FileStream(o.WatermapPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var fd = new FileStream(o.DemPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var rw = new byte[n]; var rd = new byte[n * 2];
            int step = Math.Max(1, n / 512);
            for (int y = 0; y < n; y += step)
            {
                fw.Position = (long)y * n; fw.ReadExactly(rw);
                fd.Position = (long)y * n * 2; fd.ReadExactly(rd);
                for (int x = 0; x < n; x++)
                {
                    counts[rw[x]]++;
                    int h = BitConverter.ToInt16(rd, x * 2);
                    if (h < lo) lo = h; if (h > hi) hi = h;
                }
                sampled += n;
            }
        }
        var odd = Enumerable.Range(0, 256).Where(v => v is not (0 or 25 or 45) && counts[v] > 0).ToList();
        if (odd.Count > 0)
            W($"Watermap has values other than 0/25/45 ({string.Join(", ", odd.Take(8).Select(v => $"{v}: {counts[v] * 100.0 / sampled:0.###}%"))}): treated as inland water if not 0");
        if (counts[25] + counts[45] == 0) W("The sampled rows of the Watermap have no water at all: check tab 4 (Watermap)");
        if (counts[45] == 0) W("No ocean (45) in the sampled rows: CullingMap will be empty and FFTMap without sea");
        if (lo < o.MinHeightFt || hi > o.MaxHeightFt) W($"DEM heights {lo}..{hi} ft are outside the theater range {o.MinHeightFt}..{o.MaxHeightFt} ft");
        if (hi - lo < 50) E($"The DEM is almost flat ({lo}..{hi} ft): wrong file?");
        res.Insert(0, new WaterDemCheck(false, $"OK: {n}x{n}, heights {lo}..{hi} ft (sampled), water {100.0 * (counts[25] + counts[45]) / sampled:0.0}% " +
                                                $"(ocean {100.0 * counts[45] / sampled:0.0}%), disk needed {need / 1073741824.0:0.0} GB, threads {o.Threads} of {Environment.ProcessorCount} cores"));
        return res;
    }

    /// <summary>Memoria libera (byte) o 0 se non si sa.</summary>
    public static long AvailableMemory()
    {
        try
        {
            var gc = GC.GetGCMemoryInfo();
            long total = gc.TotalAvailableMemoryBytes, load = gc.MemoryLoadBytes;
            return total > 0 && load > 0 ? Math.Max(0, total - load) : total;
        }
        catch { return 0; }
    }

    // ───────────────────────── lavoro ─────────────────────────

    public static List<string> Run(WaterDemOptions o, IProgress<WaterDemProgress> p, Action<string> log, CancellationToken ct)
    {
        var notes = new List<string>();
        var sw = Stopwatch.StartNew();
        var checks = Preflight(o);
        foreach (var c in checks) log((c.Error ? "ERROR: " : c.Text.StartsWith("OK") ? "" : "Warning: ") + c.Text);
        if (checks.Any(c => c.Error)) throw new InvalidOperationException(string.Join("\n", checks.Where(c => c.Error).Select(c => c.Text)));
        int n = SideOf(o.DemPath, 2), q = n / 4, g = Math.Min(Grid2048, n), f = n / g;
        int threads = o.Threads;
        long avail = AvailableMemory();
        long perThread = (long)(Band + 2 * Halo) * n * 24 + (long)Band * n * 5;
        if (avail > 0) threads = (int)Math.Clamp((avail * 0.7) / perThread, 1, o.Threads);
        log($"Water DEM: {n}x{n}, {threads} threads, output {o.OutDir}");
        foreach (var d in new[] { "HeightMaps", "WaterMaps", "GroundTypes" }) Directory.CreateDirectory(Path.Combine(o.OutDir, d));
        string hPath = Path.Combine(o.OutDir, "HeightMaps", "Heightmap.raw"), redPath = Path.Combine(o.OutDir, "WaterMaps", "Depthmap_Red.raw"),
               bluePath = Path.Combine(o.OutDir, "WaterMaps", "Depthmap_Blue.raw"), gaPath = Path.Combine(o.OutDir, "GroundTypes", "GlobalAlpha_Blue.raw");
        foreach (var (path, len) in new[] { (hPath, (long)n * n * 2), (redPath, (long)n * n), (bluePath, (long)n * n), (gaPath, (long)n * n) })
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(len);

        // statistiche a blocchi raccolte durante la passata grande
        var qMin = new short[(long)q * q]; var qWater = new byte[(long)q * q];
        // terra: per pixel, entro SurfaceReachPx dall'acqua vale la superficie vicina, oltre DEM - acqua nascosta; poi media sul blocco 4x4 (come TB)
        var qFarSum = new int[(long)q * q]; var qNearSum = new int[(long)q * q]; var qNear = new byte[(long)q * q];
        var gSea = new int[g * g]; var gWater = new int[g * g]; var gLand = new int[g * g];
        int bands = (n + Band - 1) / Band, done = 0;
        bool useCoast = o.CoastlinePath != null && File.Exists(o.CoastlinePath) && SideOf(o.CoastlinePath, 1) == n;
        if (!useCoast) log("Warning: no Coastline.raw of the same size: the sea floor has no gentle slope at the beaches (TB uses the coast line)");
        var lk = new object();
        long waterPx = 0, seaPx = 0, odd = 0;

        p.Report(new WaterDemProgress(0, "Heightmap, depth and alpha maps", 0));
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, () =>
            (Dem: new FileStream(o.DemPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
             Wat: new FileStream(o.WatermapPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20),
             H: new FileStream(hPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20),
             R: new FileStream(redPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20),
             B: new FileStream(bluePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20),
             A: new FileStream(gaPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20),
             Coast: useCoast ? new FileStream(o.CoastlinePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20) : null),
        (bi, _, s) =>
        {
            int y0 = bi * Band, h = Math.Min(Band, n - y0);
            int ya = Math.Max(0, y0 - Halo), yb = Math.Min(n, y0 + h + Halo), hh = yb - ya;
            var wat = new byte[(long)hh * n]; var demRaw = new byte[(long)hh * n * 2];
            s.Wat.Position = (long)ya * n; s.Wat.ReadExactly(wat);
            s.Dem.Position = (long)ya * n * 2; s.Dem.ReadExactly(demRaw);
            long localOdd = 0;
            for (int i = 0; i < wat.Length; i++) if (wat[i] is not (0 or 25 or 45)) { if (wat[i] != 0) { wat[i] = 25; localOdd++; } }
            // distanze (chamfer 1 / √2) entro il bordo: acqua → terra piu' vicina; terra → acqua piu' vicina (con il tipo)
            var dLand = Chamfer(wat, n, hh, v => v == 0, out byte[] _unusedCls);
            var dWater = Chamfer(wat, n, hh, v => v != 0, out var nearCls);
            float[]? dCoast = null; byte[]? coast = null;
            // acqua dolce entro 6 px dal mare: Red sfuma 23 per pixel (138 - 23 d), come TB
            var dSea = Chamfer(wat, n, hh, v => v == 45, out byte[] _unusedSea);
            if (s.Coast != null)
            {
                coast = new byte[(long)hh * n];
                s.Coast.Position = (long)ya * n; s.Coast.ReadExactly(coast);
                dCoast = Chamfer(coast, n, hh, v => v != 0, out byte[] _unusedCoast);
            }
            // quota finale prima della sfocatura della riva, anche nelle righe di bordo (serve alla media 5x5)
            var hAll = new short[(long)hh * n];
            for (long ib = 0; ib < hAll.Length; ib++)
            {
                int d0 = BitConverter.ToInt16(demRaw, (int)(ib * 2));
                byte w0 = wat[ib];
                hAll[ib] = (short)Math.Clamp(w0 != 0 ? d0 - DepthAt(dCoast != null ? dCoast[ib] : 1e9, dLand[ib], o.DepthFt, w0 == 45) : d0, short.MinValue, short.MaxValue);
            }
            var outH = new byte[(long)h * n * 2]; var outR = new byte[(long)h * n]; var outB = new byte[(long)h * n]; var outA = new byte[(long)h * n];
            long lw = 0, ls = 0;
            for (int y = 0; y < h; y++)
            {
                int ry = y0 + y - ya;
                for (int x = 0; x < n; x++)
                {
                    long ib = (long)ry * n + x, io = (long)y * n + x;
                    byte w = wat[ib];
                    short dem = BitConverter.ToInt16(demRaw, (int)(ib * 2));
                    int hv = hAll[ib];
                    if (w != 0)
                    {
                        lw++; if (w == 45) ls++;
                        outB[io] = (byte)(w == 45 ? 125 : 25);
                        outR[io] = (byte)(w == 45 ? 255 : Math.Clamp((int)Math.Round(138 - 23 * dSea[ib]), 0, 255));
                        outA[io] = (byte)(w == 45 ? 255 : 0);
                    }
                    else
                    {
                        float d = dWater[ib];
                        byte nc = nearCls[ib];
                        if (coast != null && coast[ib] != 0) outR[io] = 255; // terra sotto la linea di costa
                        // riva: TB addolcisce la terra vicino all'acqua verso il fondale (media binomiale 5x5, mai piu' alta del DEM)
                        if (d <= ShoreSmoothPx) hv = Math.Min(dem, ShoreSmooth(hAll, n, hh, x, ry));
                        if (d <= 3.6f) outB[io] = (byte)(nc == 45 ? 125 : 25);
                        if (nc == 45 && d < 5f)
                        {
                            // 25 gradini come TB (10, 20, 31, 41 … 249: floor(k * 255 / 24.5))
                            double a = Math.Clamp(1 - (d - 0.5) / 4.5, 0, 1);
                            int k = (int)Math.Round(a * 24);
                            outA[io] = (byte)Math.Min(255, (int)(k * 255 / 24.5));
                        }
                    }
                    hv = Math.Clamp(hv, short.MinValue, short.MaxValue);
                    outH[io * 2] = (byte)(hv & 0xFF); outH[io * 2 + 1] = (byte)((hv >> 8) & 0xFF);
                    // blocchi N/4 (quote dell'acqua) e 2048 (culling, onde)
                    long qi = (long)((y0 + y) / 4) * q + x / 4;
                    bool firstQ = (y0 + y) % 4 == 0 && x % 4 == 0;
                    if (firstQ || dem < qMin[qi]) qMin[qi] = dem;
                    if (w != 0) qWater[qi] = 1;
                    else if (dWater[ib] <= SurfaceReachPx) { qNear[qi]++; qNearSum[qi] += dem; }
                    else qFarSum[qi] += dem;
                    int gi = ((y0 + y) / f) * g + x / f;
                    if (w == 45) gSea[gi]++; if (w != 0) gWater[gi]++; else gLand[gi]++;
                }
            }
            void Put(FileStream fs, byte[] b, long at) { fs.Position = at; fs.Write(b); }
            Put(s.H, outH, (long)y0 * n * 2); Put(s.R, outR, (long)y0 * n); Put(s.B, outB, (long)y0 * n); Put(s.A, outA, (long)y0 * n);
            lock (lk) { waterPx += lw; seaPx += ls; odd += localOdd; }
            int dn = Interlocked.Increment(ref done);
            p.Report(new WaterDemProgress(0.85 * dn / bands, "Heightmap, depth and alpha maps", (double)dn / bands));
            return s;
        },
        s => { s.Dem.Dispose(); s.Wat.Dispose(); s.H.Dispose(); s.R.Dispose(); s.B.Dispose(); s.A.Dispose(); s.Coast?.Dispose(); });
        ct.ThrowIfCancellationRequested();
        log($"  full-resolution maps done in {sw.Elapsed:mm\\:ss}: water {100.0 * waterPx / ((long)n * n):0.00}% (ocean {100.0 * seaPx / ((long)n * n):0.00}%)" +
            (odd > 0 ? $", {odd:N0} pixels with odd Watermap values treated as inland water" : ""));

        // ── WaterHeightmap e WaterReflectionDemRefmap (N/4)
        p.Report(new WaterDemProgress(0.86, "Water heightmap", 0));
        // pelo dell'acqua = minimo dei minimi di blocco nelle 3x3 celle vicine; sulla terra vicina la superficie della cella d'acqua piu' bassa entro 2 celle
        var surf = new short[(long)q * q];
        Parallel.For(0, q, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, y =>
        {
            for (int x = 0; x < q; x++)
            {
                int m = short.MaxValue;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int yy = y + dy, xx = x + dx;
                    if (yy >= 0 && yy < q && xx >= 0 && xx < q) m = Math.Min(m, qMin[(long)yy * q + xx]);
                }
                surf[(long)y * q + x] = (short)m;
            }
        });
        var wh = new byte[(long)q * q * 2];
        Parallel.For(0, q, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, y =>
        {
            for (int x = 0; x < q; x++)
            {
                long i = (long)y * q + x;
                int v;
                if (qWater[i] != 0) v = surf[i];
                else
                {
                    int s2 = short.MaxValue;
                    if (qNear[i] > 0)
                        for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            if (yy >= 0 && yy < q && xx >= 0 && xx < q && qWater[(long)yy * q + xx] != 0) s2 = Math.Min(s2, surf[(long)yy * q + xx]);
                        }
                    double sum = s2 != short.MaxValue
                        ? (double)qNear[i] * s2 + qFarSum[i] - (16.0 - qNear[i]) * o.HiddenWaterFt
                        : (double)qNearSum[i] + qFarSum[i] - 16.0 * o.HiddenWaterFt;
                    v = (int)Math.Round(sum / 16);
                }
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                wh[i * 2] = (byte)(v & 0xFF); wh[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
        });
        File.WriteAllBytes(Path.Combine(o.OutDir, "HeightMaps", "WaterHeightmap.raw"), wh);
        // riflessi = pelo dell'acqua: in TB una riga su quattro coincide esattamente, le altre tre contengono dati residui (difetto di TB)
        File.WriteAllBytes(Path.Combine(o.OutDir, "HeightMaps", "WaterReflectionDemRefmap.raw"), wh);
        notes.Add("WaterReflectionDemRefmap.raw = WaterHeightmap.raw (TB writes the same values on one row in four; its other rows hold leftover data)");

        // ── CullingMap e FFTMap (2048)
        p.Report(new WaterDemProgress(0.95, "Culling and wave maps", 0));
        int full = f * f;
        // CullingMap: blocchi d'acqua a piu' di CullingKm blocchi (scacchiera) da ogni blocco con piu' di 4 pixel di terra (come TB, 2048: 1 blocco ~ 1 km)
        int landTol = Math.Max(0, full * 4 / 256);
        var notOpen = new byte[g * g];
        for (int i = 0; i < g * g; i++) if (gLand[i] > landTol) notOpen[i] = 1;
        var openDist = ChessDistance(notOpen, g, (int)Math.Floor(o.CullingKm) + 2, out _);
        var cull = new byte[g * g];
        for (int i = 0; i < g * g; i++) if (openDist[i] > (int)Math.Floor(o.CullingKm) && gSea[i] * 2 >= gWater[i]) cull[i] = 255;
        File.WriteAllBytes(Path.Combine(o.OutDir, "WaterMaps", "CullingMap.raw"), cull);
        log($"  CullingMap: {cull.Count(v => v != 0):N0} open-sea blocks of {g * g:N0}");

        // FFTMap: blocchi d'acqua 64 (acqua interna) / 128 (mare); la terra entro WaveSpreadBlocks da un blocco con almeno
        // 8 pixel d'acqua prende la classe dell'acqua piu' vicina; onde di riva (250): quadrati di 11 blocchi lungo CoastPoints.shp
        var fftSrc = new byte[g * g];
        int minWater = Math.Max(1, full * 8 / 256);
        for (int i = 0; i < g * g; i++) if (gWater[i] >= minWater) fftSrc[i] = (byte)(gSea[i] * 2 >= gWater[i] ? 128 : 64);
        var fftDist = ChessDistance(fftSrc, g, WaveSpreadBlocks + 1, out var fftNear);
        var fft = new byte[g * g];
        for (int i = 0; i < g * g; i++)
        {
            if (gWater[i] > 0) fft[i] = (byte)(gSea[i] * 2 >= gWater[i] ? 128 : 64);
            else if (fftDist[i] <= WaveSpreadBlocks) fft[i] = fftNear[i];
        }
        int stamps = StampCoastWaves(o, n, g, fft, log);
        log($"  FFTMap: {fft.Count(v => v == 128):N0} sea, {fft.Count(v => v == 64):N0} inland, {fft.Count(v => v == 250):N0} shore-wave blocks ({stamps} wave patches)");
        File.WriteAllBytes(Path.Combine(o.OutDir, "WaterMaps", "FFTMap.raw"), fft);
        p.Report(new WaterDemProgress(1, "Done", 1));
        notes.Add($"Water DEM done in {sw.Elapsed:hh\\:mm\\:ss} with {threads} threads → {o.OutDir}");
        return notes;
    }

    /// <summary>
    /// Distanza chamfer (1, √2) di ogni pixel dal pixel "sorgente" piu' vicino (isSource); i sorgenti hanno 0.
    /// cls riporta il valore (acqua 25/45) della sorgente piu' vicina.
    /// </summary>
    private static float[] Chamfer(byte[] w, int n, int h, Func<byte, bool> isSource, out byte[] cls)
    {
        const float A = 1f, B = 1.41421356f, Inf = 1e9f;
        var d = new float[(long)n * h];
        var c = new byte[(long)n * h];
        for (long i = 0; i < d.Length; i++) { bool s = isSource(w[i]); d[i] = s ? 0 : Inf; c[i] = s ? w[i] : (byte)0; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < n; x++)
            {
                long i = (long)y * n + x;
                if (d[i] == 0) continue;
                float best = d[i]; byte bc = c[i];
                if (x > 0 && d[i - 1] + A < best) { best = d[i - 1] + A; bc = c[i - 1]; }
                if (y > 0)
                {
                    long u = i - n;
                    if (d[u] + A < best) { best = d[u] + A; bc = c[u]; }
                    if (x > 0 && d[u - 1] + B < best) { best = d[u - 1] + B; bc = c[u - 1]; }
                    if (x < n - 1 && d[u + 1] + B < best) { best = d[u + 1] + B; bc = c[u + 1]; }
                }
                d[i] = best; c[i] = bc;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = n - 1; x >= 0; x--)
            {
                long i = (long)y * n + x;
                if (d[i] == 0) continue;
                float best = d[i]; byte bc = c[i];
                if (x < n - 1 && d[i + 1] + A < best) { best = d[i + 1] + A; bc = c[i + 1]; }
                if (y < h - 1)
                {
                    long dn = i + n;
                    if (d[dn] + A < best) { best = d[dn] + A; bc = c[dn]; }
                    if (x < n - 1 && d[dn + 1] + B < best) { best = d[dn + 1] + B; bc = c[dn + 1]; }
                    if (x > 0 && d[dn - 1] + B < best) { best = d[dn - 1] + B; bc = c[dn - 1]; }
                }
                d[i] = best; c[i] = bc;
            }
        cls = c;
        return d;
    }

    /// <summary>Distanza a scacchiera (in celle, fino a max; oltre = 255) delle celle senza acqua dalla cella con acqua piu' vicina.</summary>
    public const int WaveSpreadBlocks = 5;     // TB: l'acqua dell'FFTMap si estende 5 blocchi sulla terra
    public const int WaveStampRadius = 5;      // quadrati 11x11 di onde di riva
    public const int WaveStampSpacing = 80;    // un quadrato ogni ~80 blocchi di costa (densita' simile a TB, che li sceglie a caso)

    /// <summary>Distanza a scacchiera (8 vicini) dai blocchi con src != 0, fino a max; near = valore della sorgente piu' vicina.</summary>
    private static int[] ChessDistance(byte[] src, int g, int max, out byte[] near)
    {
        var d = new int[g * g]; near = new byte[g * g];
        Array.Fill(d, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < g * g; i++) if (src[i] != 0) { d[i] = 0; near[i] = src[i]; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), y = i / g, x = i % g;
            if (d[i] >= max) continue;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int yy = y + dy, xx = x + dx;
                if ((dy | dx) == 0 || yy < 0 || yy >= g || xx < 0 || xx >= g) continue;
                int j = yy * g + xx;
                if (d[j] != int.MaxValue) continue;
                d[j] = d[i] + 1; near[j] = near[i]; q.Enqueue(j);
            }
        }
        return d;
    }

    /// <summary>Georeferenza (angolo in alto a sinistra e pixel in metri) dal .hdr ENVI accanto alla Watermap.</summary>
    public static (double X0, double Y0, double Px, double Py)? ReadEnviGeo(string rawPath)
    {
        string hdr = Path.ChangeExtension(rawPath, ".hdr");
        if (!File.Exists(hdr)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(hdr), @"map info\s*=\s*\{([^}]*)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split(',').Select(t => t.Trim()).ToArray();
        if (parts.Length < 7) return null;
        double P(int k) => double.Parse(parts[k], CultureInfo.InvariantCulture);
        try { return (P(3) - (P(1) - 1) * P(5), P(4) + (P(2) - 1) * P(6), P(5), P(6)); } catch { return null; }
    }

    /// <summary>Onde di riva: legge i punti di CoastPoints.shp e mette un quadrato 250 ogni WaveStampSpacing blocchi lungo la costa.</summary>
    private static int StampCoastWaves(WaterDemOptions o, int n, int g, byte[] fft, Action<string> log)
    {
        if (o.CoastPointsPath == null || !File.Exists(o.CoastPointsPath)) { log("  Warning: no CoastPoints.shp: FFTMap without shore waves (250)"); return 0; }
        var geo = ReadEnviGeo(o.WatermapPath);
        if (geo == null) { log("  Warning: Watermap.hdr has no 'map info': CoastPoints.shp cannot be placed, FFTMap without shore waves"); return 0; }
        var (x0, y0, px, py) = geo.Value;
        double blockM = px * n / g;
        int stamps = 0, lastX = int.MinValue / 2, lastY = int.MinValue / 2, k = 0;
        using var fs = new FileStream(o.CoastPointsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var br = new BinaryReader(fs);
        if (fs.Length < 100) return 0;
        fs.Position = 32;
        if (br.ReadInt32() != 1) { log("  Warning: CoastPoints.shp is not a point shapefile: no shore waves"); return 0; }
        fs.Position = 100;
        while (fs.Position + 12 <= fs.Length)
        {
            fs.Position += 4;
            int lenWords = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(br.ReadInt32());
            long next = fs.Position + lenWords * 2L;
            if (lenWords >= 10 && br.ReadInt32() == 1 && k++ % 50 == 0)
            {
                double x = br.ReadDouble(), y = br.ReadDouble();
                int bx = (int)Math.Floor((x - x0) / blockM), by = (int)Math.Floor((y0 - y) / blockM);
                if (bx >= 0 && bx < g && by >= 0 && by < g && Math.Max(Math.Abs(bx - lastX), Math.Abs(by - lastY)) >= WaveStampSpacing)
                {
                    lastX = bx; lastY = by; stamps++;
                    for (int yy = Math.Max(0, by - WaveStampRadius); yy <= Math.Min(g - 1, by + WaveStampRadius); yy++)
                        for (int xx = Math.Max(0, bx - WaveStampRadius); xx <= Math.Min(g - 1, bx + WaveStampRadius); xx++) fft[yy * g + xx] = 250;
                }
            }
            if (next <= fs.Position && lenWords < 10) break;
            fs.Position = next;
        }
        return stamps;
    }

    // ───────────────────────── confronto con Terrain Builder ─────────────────────────

    /// <summary>
    /// Confronta le mappe di questa cartella con quelle di TB (stessi nomi): per ogni mappa pixel uguali, differenza media e massima,
    /// divisi per tipo d'acqua; scrive compare.txt e le differenze ridotte a 2048 (diff_*.raw, int16) in outDir.
    /// </summary>
    public static List<string> Compare(string ours, string tb, string watermap, string outDir, int threads, IProgress<WaterDemProgress> p, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder($"Water DEM: TerrainTool ({ours}) vs Terrain Builder ({tb})  {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        var names = new[] { @"HeightMaps\Heightmap.raw", @"HeightMaps\WaterHeightmap.raw", @"HeightMaps\WaterReflectionDemRefmap.raw",
                            @"WaterMaps\Depthmap_Red.raw", @"WaterMaps\Depthmap_Blue.raw", @"GroundTypes\GlobalAlpha_Blue.raw", @"WaterMaps\CullingMap.raw", @"WaterMaps\FFTMap.raw" };
        int nw = SideOf(watermap, 1);
        string Find(string root, string rel)
        {
            string p1 = Path.Combine(root, rel);
            if (File.Exists(p1)) return p1;
            var hit = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase)) : null;
            return hit ?? p1;
        }
        int k = 0;
        foreach (var rel in names)
        {
            ct.ThrowIfCancellationRequested();
            string a = Find(ours, rel), b = Find(tb, rel);
            p.Report(new WaterDemProgress((double)k++ / names.Length, "Comparing " + Path.GetFileName(rel), 0));
            if (!File.Exists(a) || !File.Exists(b)) { sb.AppendLine($"\n{rel}: missing ({(File.Exists(a) ? "" : "ours ")}{(File.Exists(b) ? "" : "TB")})"); continue; }
            int bpp = rel.Contains("Heightmap", StringComparison.OrdinalIgnoreCase) || rel.Contains("Refmap", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            int n = SideOf(a, bpp), nb = SideOf(b, bpp);
            if (n != nb || n < 0) { sb.AppendLine($"\n{rel}: different sizes ({n} / {nb})"); continue; }
            int fw = nw / n;    // pixel della Watermap per pixel della mappa
            var same = new long[4]; var cnt = new long[4]; var sumAbs = new double[4]; var maxAbs = new int[4]; var sumSigned = new double[4];
            int small = Math.Min(2048, n), st = n / small;
            var diff = new short[small * small];
            var lk = new object();
            int band = Math.Max(st, 64 / st * st); if (band <= 0) band = st;
            int bands = (n + band - 1) / band;
            Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, bi =>
            {
                int y0 = bi * band, h = Math.Min(band, n - y0);
                var ra = new byte[(long)h * n * bpp]; var rb = new byte[(long)h * n * bpp]; var rw = new byte[nw];
                using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) { fa.Position = (long)y0 * n * bpp; fa.ReadExactly(ra); }
                using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) { fb.Position = (long)y0 * n * bpp; fb.ReadExactly(rb); }
                var lsame = new long[4]; var lcnt = new long[4]; var lsum = new double[4]; var lmax = new int[4]; var lsig = new double[4];
                using var fwm = new FileStream(watermap, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                for (int y = 0; y < h; y++)
                {
                    fwm.Position = (long)((y0 + y) * fw + fw / 2) * nw; fwm.ReadExactly(rw);
                    for (int x = 0; x < n; x++)
                    {
                        long i = (long)y * n + x;
                        int va = bpp == 2 ? BitConverter.ToInt16(ra, (int)(i * 2)) : ra[i], vb = bpp == 2 ? BitConverter.ToInt16(rb, (int)(i * 2)) : rb[i];
                        byte w = rw[x * fw + fw / 2];
                        int c = w == 0 ? 0 : w == 25 ? 1 : w == 45 ? 2 : 3;
                        int d = va - vb;
                        lcnt[c]++; if (d == 0) lsame[c]++;
                        lsum[c] += Math.Abs(d); lsig[c] += d; if (Math.Abs(d) > lmax[c]) lmax[c] = Math.Abs(d);
                        int gy = y0 + y;
                        if (gy % st == st / 2 && x % st == st / 2) diff[(gy / st) * small + x / st] = (short)Math.Clamp(d, short.MinValue, short.MaxValue);
                    }
                }
                lock (lk) for (int c = 0; c < 4; c++) { same[c] += lsame[c]; cnt[c] += lcnt[c]; sumAbs[c] += lsum[c]; sumSigned[c] += lsig[c]; maxAbs[c] = Math.Max(maxAbs[c], lmax[c]); }
            });
            sb.AppendLine($"\n{rel} ({n}^2): ours - TB");
            string[] cn = { "land", "inland", "ocean", "other" };
            for (int c = 0; c < 4; c++)
                if (cnt[c] > 0)
                    sb.AppendLine($"  {cn[c],-7} same {100.0 * same[c] / cnt[c],6:0.00}%   mean |diff| {sumAbs[c] / cnt[c],8:0.00}   mean diff {sumSigned[c] / cnt[c],8:0.00}   max |diff| {maxAbs[c]}");
            var outb = new byte[diff.Length * 2];
            Buffer.BlockCopy(diff, 0, outb, 0, outb.Length);
            File.WriteAllBytes(Path.Combine(outDir, $"diff_{Path.GetFileNameWithoutExtension(rel)}_{small}_16bit.raw"), outb);
        }
        File.WriteAllText(Path.Combine(outDir, "compare.txt"), sb.ToString(), new UTF8Encoding(false));
        p.Report(new WaterDemProgress(1, "Done", 1));
        return new List<string> { $"Comparison written to {Path.Combine(outDir, "compare.txt")}" };
    }
}
