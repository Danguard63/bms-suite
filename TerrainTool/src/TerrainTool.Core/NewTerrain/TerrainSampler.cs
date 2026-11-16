using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.NewTerrain;

/// <summary>
/// Legge a pezzi il terreno finito del progetto (Output\NewTerrain): quote da HeightMaps\HeightMap.raw (int16 piedi,
/// riga 0 = nord), pelo dell'acqua da HeightMaps\WaterHeightMap.raw (N/4), foto dalle tessere Photoreal\4K.
/// Coordinate del teatro in piedi: north (da sud) ed east (da ovest), come X/Y degli obiettivi della campagna.
/// Solo lettura; usata dall'anteprima del terreno.
/// </summary>
public sealed class TerrainSampler
{
    public const double FtPerM = 3.280839895;
    public TheaterProject Project { get; }
    public string HeightPath { get; }
    public string WaterPath { get; }
    public int HeightSide { get; }
    public int WaterSide { get; }
    public double SizeFt { get; }
    public double HeightPxFt => SizeFt / HeightSide;
    public PhotoTiles? Photo { get; }
    public List<string> Warnings { get; } = new();

    public TerrainSampler(TheaterProject p)
    {
        Project = p;
        SizeFt = p.Theater.SizeMeters * FtPerM;
        HeightPath = Path.Combine(p.OutputNewTerrain, "HeightMaps", "HeightMap.raw");
        WaterPath = Path.Combine(p.OutputNewTerrain, "HeightMaps", "WaterHeightMap.raw");
        if (!File.Exists(HeightPath)) throw new FileNotFoundException("HeightMap.raw not found: run the texture jobs of tab 5 first (Copy TB RAW outputs)", HeightPath);
        HeightSide = Side(HeightPath);
        if (HeightSide <= 0) throw new InvalidDataException("HeightMap.raw is not a square 16-bit RAW");
        if (File.Exists(WaterPath)) { WaterSide = Side(WaterPath); if (WaterSide <= 0) Warnings.Add("WaterHeightMap.raw is not square: no water drawn"); }
        else Warnings.Add("WaterHeightMap.raw not found: no water drawn");
        string photo = Path.Combine(p.OutputNewTerrain, "Photoreal", "4K");
        if (Directory.Exists(photo)) Photo = new PhotoTiles(photo, p.Theater.SizeMeters);
        else Warnings.Add("Photoreal\\4K not found: the terrain is drawn with plain colours");
    }

    private static int Side(string path)
    {
        long px = new FileInfo(path).Length / 2;
        int s = (int)Math.Round(Math.Sqrt(px));
        return (long)s * s == px ? s : -1;
    }

    /// <summary>Griglia n×n di valori (piedi) del quadrato di lato 2·half centrato in (north, east), riga 0 = nord; bilineare.</summary>
    private static float[,]? Window(string path, int side, double sizeFt, double north, double east, double half, int n)
    {
        if (side <= 0 || !File.Exists(path)) return null;
        double pxFt = sizeFt / side;
        int c0 = Math.Clamp((int)Math.Floor((east - half) / pxFt) - 1, 0, side - 1), c1 = Math.Clamp((int)Math.Ceiling((east + half) / pxFt) + 1, 0, side - 1);
        int r0 = Math.Clamp((int)Math.Floor((sizeFt - (north + half)) / pxFt) - 1, 0, side - 1), r1 = Math.Clamp((int)Math.Ceiling((sizeFt - (north - half)) / pxFt) + 1, 0, side - 1);
        int w = c1 - c0 + 1, h = r1 - r0 + 1;
        if (w < 2 || h < 2) return null;
        var raw = new short[w * h];
        var buf = new byte[w * 2];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            for (int r = 0; r < h; r++)
            {
                fs.Position = ((long)(r0 + r) * side + c0) * 2;
                fs.ReadExactly(buf);
                Buffer.BlockCopy(buf, 0, raw, r * w * 2, w * 2);
            }
        var res = new float[n, n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double no = north + half - 2 * half * j / (n - 1), ea = east - half + 2 * half * i / (n - 1);
                double fc = ea / pxFt - 0.5 - c0, fr = (sizeFt - no) / pxFt - 0.5 - r0;
                int ic = Math.Clamp((int)Math.Floor(fc), 0, w - 2), ir = Math.Clamp((int)Math.Floor(fr), 0, h - 2);
                double tc = Math.Clamp(fc - ic, 0, 1), tr = Math.Clamp(fr - ir, 0, 1);
                double a = raw[ir * w + ic], b = raw[ir * w + ic + 1], c = raw[(ir + 1) * w + ic], d = raw[(ir + 1) * w + ic + 1];
                res[j, i] = (float)((a * (1 - tc) + b * tc) * (1 - tr) + (c * (1 - tc) + d * tc) * tr);
            }
        return res;
    }

    public float[,]? Heights(double north, double east, double half, int n) => Window(HeightPath, HeightSide, SizeFt, north, east, half, n);
    public float[,]? WaterSurface(double north, double east, double half, int n) => Window(WaterPath, WaterSide, SizeFt, north, east, half, n);

    /// <summary>Quota del terreno (piedi) in un punto.</summary>
    public double GroundAt(double north, double east) => Heights(north, east, HeightPxFt, 2) is { } g ? (g[0, 0] + g[0, 1] + g[1, 0] + g[1, 1]) / 4 : 0;
    /// <summary>Pelo dell'acqua (piedi) in un punto, o null se manca la mappa.</summary>
    public double? WaterAt(double north, double east) => WaterSide > 0 && WaterSurface(north, east, SizeFt / WaterSide, 2) is { } w ? (w[0, 0] + w[0, 1] + w[1, 0] + w[1, 1]) / 4 : null;

    /// <summary>Foto (BGRA px×px) del quadrato di lato 2·half centrato in (north, east), riga 0 = nord; null senza tessere.</summary>
    public byte[]? PhotoArea(double north, double east, double half, int px)
    {
        if (Photo == null) return null;
        var ph = Photo;
        double pixFt = 2 * half / px;
        double baseFt = ph.TileM * FtPerM / PhotoTiles.TilePx;
        int mip = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, pixFt / baseFt))), 0, 8);
        int w = PhotoTiles.TilePx >> mip, side = Math.Min(PhotoTiles.Chunk, w), per = w / side;
        double chunkFt = ph.TileM * FtPerM / per, sizeFt = ph.SizeM * FtPerM;
        var outp = new byte[px * px * 4];
        var local = new Dictionary<(int, int), (byte[]? Px, int S)>();
        lock (ph)
            for (int j = 0; j < px; j++)
                for (int i = 0; i < px; i++)
                {
                    double no = north + half - (j + 0.5) * pixFt, ea = east - half + (i + 0.5) * pixFt;
                    double fromTop = sizeFt - no;
                    int o = (j * px + i) * 4;
                    if (ea < 0 || fromTop < 0 || ea >= sizeFt || fromTop >= sizeFt) { outp[o] = 60; outp[o + 1] = 60; outp[o + 2] = 60; outp[o + 3] = 255; continue; }
                    int cc = (int)(ea / chunkFt), rr = (int)(fromTop / chunkFt);
                    if (!local.TryGetValue((cc, rr), out var hit))
                    {
                        var got = ph.GetChunk(cc / per, rr / per, mip, cc % per, rr % per, out int gs);
                        local[(cc, rr)] = hit = (got, gs);
                    }
                    var (ch, s) = hit;
                    if (ch == null) { outp[o] = 120; outp[o + 1] = 70; outp[o + 2] = 30; outp[o + 3] = 255; continue; } // tessera di solo mare: blu scuro
                    int u = Math.Clamp((int)((ea - cc * chunkFt) / chunkFt * s), 0, s - 1), v = Math.Clamp((int)((fromTop - rr * chunkFt) / chunkFt * s), 0, s - 1);
                    Buffer.BlockCopy(ch, (v * s + u) * 4, outp, o, 4);
                    outp[o + 3] = 255;
                }
        return outp;
    }

    /// <summary>
    /// Mappa d'insieme (BGRA px×px, riga 0 = nord): rilievo ombreggiato dalla HeightMap, acqua in blu dove il pelo
    /// dell'acqua sta sopra il terreno. Legge una riga ogni HeightSide/px.
    /// </summary>
    public byte[] Overview(int px, CancellationToken ct = default)
    {
        var h = new float[px, px];
        int step = HeightSide / px;
        var row = new byte[HeightSide * 2];
        using (var fs = new FileStream(HeightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            for (int y = 0; y < px; y++)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = (long)(y * step + step / 2) * HeightSide * 2;
                fs.ReadExactly(row);
                for (int x = 0; x < px; x++) h[y, x] = BitConverter.ToInt16(row, (x * step + step / 2) * 2);
            }
        float[,]? wsurf = null;
        if (WaterSide > 0)
        {
            wsurf = new float[px, px];
            int ws = WaterSide / px;
            var wr = new byte[WaterSide * 2];
            using var fw = new FileStream(WaterPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            for (int y = 0; y < px; y++)
            {
                fw.Position = (long)(y * ws + ws / 2) * WaterSide * 2;
                fw.ReadExactly(wr);
                for (int x = 0; x < px; x++) wsurf[y, x] = BitConverter.ToInt16(wr, (x * ws + ws / 2) * 2);
            }
        }
        var outp = new byte[px * px * 4];
        double cell = SizeFt / px;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                double dzx = h[y, Math.Min(px - 1, x + 1)] - h[y, Math.Max(0, x - 1)], dzy = h[Math.Min(px - 1, y + 1), x] - h[Math.Max(0, y - 1), x];
                double shade = Math.Clamp(0.75 + (-dzx + dzy) / (2 * cell) * 6, 0.35, 1.25);
                double e = Math.Clamp(h[y, x] / 9000.0, 0, 1);
                double r = 170 + 60 * e, g = 160 + 40 * e - 30 * (1 - e), b = 120 + 70 * e;
                int o = (y * px + x) * 4;
                bool water = wsurf != null && wsurf[y, x] > h[y, x] - 1;
                if (water) { outp[o] = 170; outp[o + 1] = 110; outp[o + 2] = 40; }
                else { outp[o] = (byte)Math.Clamp(b * shade, 0, 255); outp[o + 1] = (byte)Math.Clamp(g * shade, 0, 255); outp[o + 2] = (byte)Math.Clamp(r * shade, 0, 255); }
                outp[o + 3] = 255;
            }
        return outp;
    }
}
