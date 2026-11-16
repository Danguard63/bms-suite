namespace TerrainTool.Core.Dds;

// ───────────────────────── Mappe colore (dalle immagini satellitari della scheda 4) ─────────────────────────

public sealed class GlobalColorMapJob : TextureJob
{
    public override string Id => "globalcolor";
    public override string Name => "GlobalColorMap.dds";
    public override string Description => "Satellite colour map of the whole theater (tab 4 'Satellite colour map') → DXT5 with all mipmaps, like the Korea file.";
    public override string Output => @"Photoreal\GlobalColorMap.dds";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[] { ColorInput(c) };

    internal static JobInput ColorInput(TextureContext c)
    {
        string? p = c.FindTool("ColorMap_RGB.raw");
        int size = 0;
        if (p != null) { long len = new FileInfo(p).Length; size = (int)Math.Round(Math.Sqrt(len / 3.0)); }
        return new JobInput("Satellite colour map (tab 4)", p, size, true);
    }

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var (rgb, size) = MipComposer.ReadRgbRaw(Inputs(c)[0].Path!);
        var prev = MipComposer.Compose(c.Out(Output), rgb, size, ColorDdsFormat.Dxt5, p, ct);
        return (prev, new List<string> { $"{size}² DXT5, {MipComposer.LevelCount(size)} mip levels" });
    }
}

public sealed class WaterColorMapJob : TextureJob
{
    public override string Id => "watercolor";
    public override string Name => "ColorMap.dds (water)";
    public override string Description => "Water colour: the satellite colour map blurred (as the TB manual asks), BC3 sRGB with all mipmaps (Korea uses BC7 sRGB).";
    public override string Output => @"WaterMaps\ColorMap.dds";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[] { GlobalColorMapJob.ColorInput(c) };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var (rgb, size) = MipComposer.ReadRgbRaw(Inputs(c)[0].Path!);
        // manuale TB: sfocatura raggio 20 su 32K → in proporzione alla dimensione reale
        int r = Math.Max(1, (int)Math.Round(20.0 * size / 32768));
        var blurred = MipComposer.Blur(rgb, size, r);
        var prev = MipComposer.Compose(c.Out(Output), blurred, size, ColorDdsFormat.Bc3SrgbDx10, p, ct);
        return (prev, new List<string> { $"{size}² BC3 sRGB, blur radius {r} px, {MipComposer.LevelCount(size)} mip levels" });
    }
}

// ───────────────────────── Neve ─────────────────────────

public sealed class SnowMapsJob : TextureJob
{
    public override string Id => "snow";
    public override string Name => "SnowMap*.raw";
    public override string Description => "Seasonal snow maps from the heightmap (Korea format: winter 2048², other seasons 512², 0–255). Snow above ~2000 m in winter, ~3300 m in spring/fall, ~4500 m in summer.";
    public override string Output => @"GroundTypes\SnowMap*.raw";

    /// <summary>Stagione, lato, quota (m) a cui la neve inizia e quota a cui è piena.</summary>
    private static readonly (string Name, int Size, double From, double Full)[] Seasons =
    {
        ("Winter", 2048, 1800, 2600),
        ("Spring", 512, 3000, 3800),
        ("Fall", 512, 3300, 4100),
        ("Summer", 512, 4300, 5000),
    };

    public override IReadOnlyList<JobInput> Inputs(TextureContext c)
    {
        string hm = Path.Combine(c.NewTerrain, "HeightMaps", "HeightMap.raw");
        string? p = File.Exists(hm) ? hm : c.FindTb("Heightmap.raw", "HeightMap.raw");
        int size = p == null ? 0 : (int)Math.Round(Math.Sqrt(new FileInfo(p).Length / 2.0));
        return new[] { new JobInput("Heightmap (after TB Water DEM)", p, size, true) };
    }

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c)[0];
        int src = inp.Size;
        const int grid = 2048;
        if (src < grid || src % grid != 0) throw new InvalidDataException($"Heightmap size {src} is not a multiple of {grid}");
        int f = src / grid;
        // massima quota (piedi) per ogni cella della griglia 2048²: la neve sta sulle cime
        var maxFt = new short[grid * grid];
        Array.Fill(maxFt, short.MinValue);
        using (var fs = new FileStream(inp.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22))
        {
            var row = new byte[src * 2];
            for (int y = 0; y < src; y++)
            {
                ct.ThrowIfCancellationRequested();
                fs.ReadExactly(row);
                int gy = y / f;
                for (int x = 0; x < src; x++)
                {
                    short h = (short)(row[2 * x] | (row[2 * x + 1] << 8));
                    int gi = gy * grid + x / f;
                    if (h > maxFt[gi]) maxFt[gi] = h;
                }
                if ((y & 511) == 0) p.Report(0.9 * y / src);
            }
        }
        var notes = new List<string>();
        byte[]? preview = null;
        foreach (var s in Seasons)
        {
            int k = grid / s.Size;
            var map = new byte[s.Size * s.Size];
            int snowy = 0;
            for (int y = 0; y < s.Size; y++)
                for (int x = 0; x < s.Size; x++)
                {
                    // quota media delle celle (k×k) che finiscono in questo pixel
                    double sum = 0;
                    for (int dy = 0; dy < k; dy++)
                        for (int dx = 0; dx < k; dx++) sum += maxFt[(y * k + dy) * grid + x * k + dx];
                    double m = sum / (k * k) * 0.3048;
                    double t = Math.Clamp((m - s.From) / (s.Full - s.From), 0, 1);
                    map[y * s.Size + x] = (byte)Math.Round(255 * t);
                    if (t > 0) snowy++;
                }
            File.WriteAllBytes(c.Out($@"GroundTypes\SnowMap{s.Name}.raw"), map);
            notes.Add($"SnowMap{s.Name}.raw {s.Size}²: snow on {100.0 * snowy / (s.Size * s.Size):0.##}% (from {s.From:0} m)");
            if (s.Name == "Winter")
            {
                int ps = DdsComposer.PreviewSize, st = s.Size / ps;
                preview = new byte[ps * ps * 3];
                for (int y = 0; y < ps; y++)
                    for (int x = 0; x < ps; x++)
                    {
                        byte v = map[y * st * s.Size + x * st];
                        double hm = Math.Clamp(maxFt[(y * st) * grid + x * st] * 0.3048 / 4000.0, 0, 1);
                        int o = (y * ps + x) * 3;
                        byte g = (byte)(40 + 120 * hm);
                        preview[o] = (byte)Math.Max(g, v); preview[o + 1] = (byte)Math.Max(g, v); preview[o + 2] = (byte)Math.Max((byte)(g + 20), v);
                    }
            }
        }
        p.Report(1);
        return (preview, notes);
    }
}

// ───────────────────────── Tessere photoreal 4K ─────────────────────────

/// <summary>
/// Tessere Photoreal\4K (obbligatorie: senza, BMS si ferma con "No Photoreal textures"). Una tessera ogni 64 km
/// (16×16 per 1024 km come Korea e Israel, 32×32 per 2048 km), 4096² DXT1 con tutte le mipmap, numerate da 1
/// riga per riga da nord-ovest. Le tessere di solo mare si saltano, come in Israel.
/// Provvisorie: sono la mappa colore satellitare ingrandita, finché non avremo immagini ad alta risoluzione.
/// </summary>
public sealed class Photoreal4KJob : TextureJob
{
    public override string Id => "photoreal4k";
    public override string Name => "Photoreal 4K tiles";
    public override string Description => "Required by BMS: one 4096² DXT1 tile (all mipmaps) every 64 km, numbered from the north-west; sea-only tiles are skipped. Placeholder made by enlarging the satellite colour map until high-resolution imagery is added. Needs ~10 GB.";
    public override string Output => @"Photoreal\4K";

    public const int TilePx = 4096;
    public const double TileKm = 64;

    public override IReadOnlyList<JobInput> Inputs(TextureContext c)
    {
        string lc = Path.Combine(c.NewTerrain, "GroundTypes", "LandCoverMap.raw");
        return new[]
        {
            GlobalColorMapJob.ColorInput(c),
            new JobInput("LandCoverMap (to skip sea tiles)", File.Exists(lc) ? lc : null, File.Exists(lc) ? RawChannel.SizeOf(lc) : 0, false),
        };
    }

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        var (rgb, size) = MipComposer.ReadRgbRaw(inp[0].Path!);
        int n = (int)Math.Round(c.Project.Theater.SizeKm / TileKm);
        var notes = new List<string>();

        // 1) quali tessere hanno terra (dalla LandCoverMap: 1 = mare)
        var land = new bool[n * n];
        if (inp[1].Path != null)
        {
            int ls = inp[1].Size, per = ls / n, step = Math.Max(1, per / 256);
            using var fs = new FileStream(inp[1].Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22);
            var row = new byte[ls];
            for (int y = 0; y < ls; y += step)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = (long)y * ls;
                fs.ReadExactly(row);
                int ty = y / per;
                for (int x = 0; x < ls; x += step)
                    if (row[x] != GroundTypes.Water) land[ty * n + x / per] = true;
                if ((y & 1023) == 0) p.Report(0.05 * y / ls);
            }
        }
        else { Array.Fill(land, true); notes.Add("LandCoverMap missing: sea tiles are not skipped"); }

        // 2) le tessere: ingrandimento bilineare con coordinate globali (niente cuciture tra tessere)
        string dir = Path.Combine(c.NewTerrain, "Photoreal", "4K");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.dds")) File.Delete(old);
        double scale = (double)size / (n * TilePx); // pixel sorgente per pixel di tessera
        var tile = new byte[(long)TilePx * TilePx * 3];
        int made = 0, total = land.Count(b => b), skipped = n * n - total;
        for (int ty = 0; ty < n; ty++)
            for (int tx = 0; tx < n; tx++)
            {
                if (!land[ty * n + tx]) continue;
                ct.ThrowIfCancellationRequested();
                Parallel.For(0, TilePx, new ParallelOptions { CancellationToken = ct }, v =>
                {
                    double sy = ((long)ty * TilePx + v + 0.5) * scale - 0.5;
                    int y0 = Math.Clamp((int)Math.Floor(sy), 0, size - 1), y1 = Math.Min(y0 + 1, size - 1);
                    double fy = Math.Clamp(sy - Math.Floor(sy), 0, 1);
                    for (int u = 0; u < TilePx; u++)
                    {
                        double sx = ((long)tx * TilePx + u + 0.5) * scale - 0.5;
                        int x0 = Math.Clamp((int)Math.Floor(sx), 0, size - 1), x1 = Math.Min(x0 + 1, size - 1);
                        double fx = Math.Clamp(sx - Math.Floor(sx), 0, 1);
                        long o = ((long)v * TilePx + u) * 3;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            double a = rgb[((long)y0 * size + x0) * 3 + ch] * (1 - fx) + rgb[((long)y0 * size + x1) * 3 + ch] * fx;
                            double b = rgb[((long)y1 * size + x0) * 3 + ch] * (1 - fx) + rgb[((long)y1 * size + x1) * 3 + ch] * fx;
                            tile[o + ch] = (byte)Math.Round(a * (1 - fy) + b * fy);
                        }
                    }
                });
                int idx = ty * n + tx + 1;
                MipComposer.Compose(Path.Combine(dir, idx + ".dds"), tile, TilePx, ColorDdsFormat.Dxt1, null, ct, makePreview: false);
                made++;
                p.Report(0.05 + 0.95 * made / total);
            }

        // anteprima: quali tessere esistono
        int ps = DdsComposer.PreviewSize;
        var prev = new byte[ps * ps * 3];
        for (int y = 0; y < ps; y++)
            for (int x = 0; x < ps; x++)
            {
                int ty = y * n / ps, tx = x * n / ps;
                long so = (((long)y * size / ps) * size + (long)x * size / ps) * 3;
                int o = (y * ps + x) * 3;
                bool edge = (y * n) % ps < n || (x * n) % ps < n;
                for (int ch = 0; ch < 3; ch++)
                    prev[o + ch] = edge ? (byte)90 : land[ty * n + tx] ? rgb[so + ch] : (byte)(rgb[so + ch] / 3);
            }
        notes.Insert(0, $"{n}×{n} grid, {made} tiles written, {skipped} sea-only tiles skipped ({made * 11184952L / 1073741824.0:0.0} GB)");
        notes.Add($"Placeholder: satellite colour map enlarged {1 / scale:0}× ({TileKm * 1000 / TilePx:0.#} m/px tiles from {c.Project.Theater.SizeKm * 1000.0 / size:0} m/px imagery)");
        return (prev, notes);
    }
}

// ───────────────────────── Mappa 2D dell'interfaccia (campmap) ─────────────────────────

/// <summary>
/// Mappa 2D di Instant Action / Tactical Engagement / campagna: Art\resource\campmap.rsc + .idx, nel formato della 4.38
/// (ricavato da Add-On Israel): 8 immagini a 255 colori (palette RGB555) — BIG_MAP_ID 16384², BIG_AWACS_MAP_ID e
/// BIG_MAP_ID_SCENARIO_0…5 a 4096². Riga 0 = nord, l'immagine copre tutto il teatro.
/// </summary>
public sealed class CampMapJob : TextureJob
{
    private readonly bool _testPoints;
    /// <param name="testPoints">true: segna sulla mappa i punti di prova (cerchio rosso numerato).</param>
    public CampMapJob(bool testPoints = false) => _testPoints = testPoints;

    public override string Id => _testPoints ? "campmap_test" : "campmap";
    public override string Name => _testPoints ? "2D map + test points" : "2D map (campmap)";
    public override string Description => _testPoints
        ? "Same as '2D map (campmap)' but with the test points marked (numbered red circles). Points from testpoints.csv in the project folder (Name;Lat;Lon), else the built-in list. Run the plain '2D map' again for the final version."
        : "UI map for Instant Action, TE and campaign, made from the satellite colour map: Art\\resource\\campmap.rsc/.idx in the 4.38 format (16384² + 7 × 4096², 255 colours), plus the small Instant Action map. Without it the UI shows the Korea map.";

    /// <summary>Punti di prova predefiniti (come nella mappa Mideast_test_points.png).</summary>
    public static readonly (string Name, double Lat, double Lon)[] DefaultPoints =
    {
        ("Kuwait coast / Shatt al-Arab", 29.70, 48.30), ("Bahrain / Qatar", 26.10, 50.80), ("Strait of Hormuz / Musandam", 26.30, 56.30),
        ("Zagros, Zard Kuh", 32.40, 50.00), ("Baghdad / Lake Tharthar", 33.70, 43.80), ("Damavand", 35.95, 52.11),
        ("Red Sea coast, Jeddah", 21.50, 39.20), ("Rub al Khali", 19.00, 50.00), ("Al Azraq (Server base)", 31.83, 36.82),
    };

    public static List<(string Name, double Lat, double Lon)> LoadPoints(TextureContext c)
    {
        string f = Path.Combine(c.Project.ProjectFolder, "testpoints.csv");
        if (!File.Exists(f)) return DefaultPoints.ToList();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var res = new List<(string, double, double)>();
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length >= 3 && double.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out var la)
                              && double.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out var lo)) res.Add((p[0], la, lo));
        }
        return res;
    }

    // cifre 0–9 in un font 5×7
    private static readonly string[] Digits =
    {
        "01110100011001110101110011000101110", "00100011000010000100001000010001110", "01110100010000100010001000100011111",
        "11111000100010000010000011000101110", "00010001100101010010111110001000010", "11111100001111000001000011000101110",
        "00110010001000011110100011000101110", "11111000010001000100010000100001000", "01110100011000101110100011000101110",
        "01110100011000101111000010001001100",
    };

    /// <summary>Disegna i punti (cerchio rosso bordato di bianco con il numero) su una copia dell'immagine.</summary>
    private static byte[] MarkPoints(byte[] rgb, int size, TextureContext c, List<string> notes)
    {
        var img = (byte[])rgb.Clone();
        var grid = c.Project.Theater.CreateGrid();
        double S = c.Project.Theater.SizeMeters;
        int r = size / 70, cell = Math.Max(1, r / 6); // raggio del cerchio e lato di un "pixel" del font
        var pts = LoadPoints(c);
        void Put(int x, int y, byte R, byte G, byte B)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            long o = ((long)y * size + x) * 3; img[o] = R; img[o + 1] = G; img[o + 2] = B;
        }
        for (int k = 0; k < pts.Count; k++)
        {
            var (tx, ty) = grid.ToTheater(pts[k].Lat, pts[k].Lon);
            int cx = (int)Math.Round(tx / S * size), cy = (int)Math.Round((1 - ty / S) * size);
            for (int dy = -r - cell * 2; dy <= r + cell * 2; dy++)
                for (int dx = -r - cell * 2; dx <= r + cell * 2; dx++)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= r) Put(cx + dx, cy + dy, 215, 25, 25);
                    else if (d <= r + cell * 2) Put(cx + dx, cy + dy, 255, 255, 255);
                }
            string num = (k + 1).ToString();
            int gw = 5 * cell, gap = cell, totalW = num.Length * gw + (num.Length - 1) * gap;
            int x0 = cx - totalW / 2, y0 = cy - 7 * cell / 2;
            for (int i = 0; i < num.Length; i++)
            {
                string g = Digits[num[i] - '0'];
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                        if (g[gy * 5 + gx] == '1')
                            for (int py = 0; py < cell; py++)
                                for (int px = 0; px < cell; px++)
                                    Put(x0 + i * (gw + gap) + gx * cell + px, y0 + gy * cell + py, 255, 255, 255);
            }
            notes.Add($"  {k + 1}. {pts[k].Name} ({pts[k].Lat:0.00}N {pts[k].Lon:0.00}E)");
        }
        return img;
    }
    public override string Output => @"..\Art\resource\campmap.rsc";

    private static readonly string[] Names =
    {
        "BIG_MAP_ID", "BIG_AWACS_MAP_ID", "BIG_MAP_ID_SCENARIO_0", "BIG_MAP_ID_SCENARIO_1", "BIG_MAP_ID_SCENARIO_2",
        "BIG_MAP_ID_SCENARIO_3", "BIG_MAP_ID_SCENARIO_4", "BIG_MAP_ID_SCENARIO_5",
    };

    public static string ArtFolder(TextureContext c) => Path.Combine(c.Project.ProjectFolder, "Output", "Art");

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[] { GlobalColorMapJob.ColorInput(c) };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var (rgb, size) = MipComposer.ReadRgbRaw(Inputs(c)[0].Path!);
        var pointNotes = new List<string>();
        if (_testPoints) rgb = MarkPoints(rgb, size, c, pointNotes);
        // 1) palette di 255 colori (median cut sull'istogramma RGB555)
        var hist = new long[32768];
        for (long i = 0; i < (long)size * size; i += 3) hist[To555(rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2])]++;
        var pal = MedianCut(hist, 255);
        var lut = new byte[32768];
        Parallel.For(0, 32768, k =>
        {
            int r = (k >> 10) & 31, g = (k >> 5) & 31, b = k & 31, best = 0; long bd = long.MaxValue;
            for (int i = 0; i < pal.Length; i++)
            {
                int pr = (pal[i] >> 10) & 31, pg = (pal[i] >> 5) & 31, pb = pal[i] & 31;
                long d = 3L * (pr - r) * (pr - r) + 4L * (pg - g) * (pg - g) + 2L * (pb - b) * (pb - b);
                if (d < bd) { bd = d; best = i; }
            }
            lut[k] = (byte)best;
        });
        var palBytes = new byte[pal.Length * 2];
        for (int i = 0; i < pal.Length; i++) BitConverter.TryWriteBytes(palBytes.AsSpan(2 * i, 2), pal[i]);

        string dir = Path.Combine(ArtFolder(c), "Art", "resource");
        Directory.CreateDirectory(dir);
        string rsc = Path.Combine(dir, "campmap.rsc"), tmp = rsc + ".tmp";
        uint stamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = new List<(string Name, int W, long Off, long PalOff)>();
        long pos = 0;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            fs.Write(new byte[8]); // intestazione: dimensione dati + timestamp, scritta alla fine
            for (int e = 0; e < Names.Length; e++)
            {
                int w = e == 0 ? 16384 : 4096;
                long off = pos;
                var row = new byte[w];
                double scale = (double)size / w;
                for (int y = 0; y < w; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    double sy = (y + 0.5) * scale - 0.5;
                    int y0 = Math.Clamp((int)Math.Floor(sy), 0, size - 1), y1 = Math.Min(y0 + 1, size - 1);
                    double fy = Math.Clamp(sy - Math.Floor(sy), 0, 1);
                    Parallel.For(0, w, x =>
                    {
                        double sx = (x + 0.5) * scale - 0.5;
                        int x0 = Math.Clamp((int)Math.Floor(sx), 0, size - 1), x1 = Math.Min(x0 + 1, size - 1);
                        double fx = Math.Clamp(sx - Math.Floor(sx), 0, 1);
                        Span<int> v = stackalloc int[3];
                        for (int ch = 0; ch < 3; ch++)
                        {
                            double a = rgb[((long)y0 * size + x0) * 3 + ch] * (1 - fx) + rgb[((long)y0 * size + x1) * 3 + ch] * fx;
                            double b = rgb[((long)y1 * size + x0) * 3 + ch] * (1 - fx) + rgb[((long)y1 * size + x1) * 3 + ch] * fx;
                            v[ch] = (int)Math.Round(a * (1 - fy) + b * fy);
                        }
                        row[x] = lut[To555(v[0], v[1], v[2])];
                    });
                    fs.Write(row);
                    if ((y & 255) == 0) p.Report((e == 0 ? 0.0 : 0.6 + 0.4 * (e - 1) / 7) + (e == 0 ? 0.6 : 0.4 / 7) * y / w);
                }
                pos += (long)w * w;
                long palOff = pos;
                fs.Write(palBytes);
                pos += palBytes.Length;
                entries.Add((Names[e], w, off, palOff));
            }
            fs.Position = 0;
            fs.Write(BitConverter.GetBytes((uint)pos));
            fs.Write(BitConverter.GetBytes(stamp));
        }
        File.Move(tmp, rsc, true);

        // indice: dimensione, timestamp, poi 60 byte per immagine
        var idx = new byte[8 + 60 * entries.Count];
        BitConverter.TryWriteBytes(idx.AsSpan(0, 4), (uint)(idx.Length - 8));
        BitConverter.TryWriteBytes(idx.AsSpan(4, 4), stamp);
        for (int i = 0; i < entries.Count; i++)
        {
            var s = idx.AsSpan(8 + 60 * i, 60);
            var (name, w, off, palOff) = entries[i];
            BitConverter.TryWriteBytes(s.Slice(0, 4), 100u);              // immagine
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(s.Slice(4, 32));
            BitConverter.TryWriteBytes(s.Slice(36, 4), 1u);               // 8 bit con palette
            BitConverter.TryWriteBytes(s.Slice(40, 2), (ushort)(w / 2)); // centro
            BitConverter.TryWriteBytes(s.Slice(42, 2), (ushort)(w / 2));
            BitConverter.TryWriteBytes(s.Slice(44, 2), (ushort)w);
            BitConverter.TryWriteBytes(s.Slice(46, 2), (ushort)w);
            BitConverter.TryWriteBytes(s.Slice(48, 4), (uint)off);
            BitConverter.TryWriteBytes(s.Slice(52, 4), (uint)pal.Length);
            BitConverter.TryWriteBytes(s.Slice(56, 4), (uint)palOff);
        }
        File.WriteAllBytes(Path.Combine(dir, "campmap.idx"), idx);

        // 2) MAP_SMALL (512², 16 bit RGB555) della schermata Instant Action: sta in Art\_FHD\Main.rsc della 4.38.
        //    Si copia quello standard (solo lettura) e si sostituisce soltanto quell'immagine.
        string smallNote = PatchMapSmall(c, rgb, size, ct);

        // anteprima con i colori della palette, per vedere la quantizzazione
        int ps = DdsComposer.PreviewSize, st = size / ps;
        var prev = new byte[ps * ps * 3];
        for (int y = 0; y < ps; y++)
            for (int x = 0; x < ps; x++)
            {
                long o = ((long)y * st * size + (long)x * st) * 3;
                ushort pc = pal[lut[To555(rgb[o], rgb[o + 1], rgb[o + 2])]];
                int d = (y * ps + x) * 3;
                prev[d] = (byte)(((pc >> 10) & 31) * 255 / 31); prev[d + 1] = (byte)(((pc >> 5) & 31) * 255 / 31); prev[d + 2] = (byte)((pc & 31) * 255 / 31);
            }
        return (prev, new List<string>
        {
            $"campmap.rsc {new FileInfo(rsc).Length / 1048576.0:0} MB: 16384² + 7 × 4096², {pal.Length} colours",
            @"Output\Art\Art\resource (Build theater copies it into the add-on and sets artdir)",
            smallNote,
        }.Concat(_testPoints ? new[] { "TEST version with marked points (run '2D map (campmap)' for the clean one):" }.Concat(pointNotes) : Array.Empty<string>()).ToList());
    }

    private static int To555(int r, int g, int b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    /// <summary>Copia Art\_FHD\Main.rsc/.idx della 4.38 e ci scrive dentro la mappa piccola (MAP_SMALL) dell'Instant Action.</summary>
    private static string PatchMapSmall(TextureContext c, byte[] rgb, int size, CancellationToken ct)
    {
        // ...\Data\TerrData\Korea\NewTerrain → ...\Data
        var data = new DirectoryInfo(c.Project.KoreaNewTerrain).Parent?.Parent?.Parent;
        string? srcIdx = data == null ? null : Path.Combine(data.FullName, "Art", "_FHD", "Main.idx");
        string? srcRsc = data == null ? null : Path.Combine(data.FullName, "Art", "_FHD", "Main.rsc");
        string outDir = Path.Combine(ArtFolder(c), "Art", "_FHD");
        // Main.rsc già fatto (anche con le bandiere del lavoro Theater art): si riscrive solo MAP_SMALL, al suo posto
        bool inPlace = File.Exists(Path.Combine(outDir, "Main.idx")) && File.Exists(Path.Combine(outDir, "Main.rsc"));
        if (inPlace) { srcIdx = Path.Combine(outDir, "Main.idx"); srcRsc = Path.Combine(outDir, "Main.rsc"); }
        if (srcIdx == null || !File.Exists(srcIdx) || !File.Exists(srcRsc)) return "Instant Action map: BMS Art\\_FHD\\Main.rsc not found, not changed";
        var idx = File.ReadAllBytes(srcIdx);
        int n = (idx.Length - 8) / 60, at = -1;
        for (int i = 0; i < n; i++)
        {
            string name = System.Text.Encoding.ASCII.GetString(idx, 8 + 60 * i + 4, 32).Split('\0')[0];
            if (name == "MAP_SMALL") { at = 8 + 60 * i; break; }
        }
        if (at < 0) return "Instant Action map: MAP_SMALL not found in Main.idx, not changed";
        uint flags = BitConverter.ToUInt32(idx, at + 36);
        int w = BitConverter.ToUInt16(idx, at + 44), h = BitConverter.ToUInt16(idx, at + 46);
        long off = BitConverter.ToUInt32(idx, at + 48);
        if (flags != 2) return $"Instant Action map: unexpected MAP_SMALL format ({flags}), not changed";

        Directory.CreateDirectory(outDir);
        string dstRsc = Path.Combine(outDir, "Main.rsc"), dstIdx = Path.Combine(outDir, "Main.idx");
        // file nuovi (non scritti dentro collegamenti fisici a copie installate)
        string tmpRsc = dstRsc + ".tmp", tmpIdx = dstIdx + ".tmp";
        File.Copy(srcRsc, tmpRsc, true);
        File.Copy(srcIdx, tmpIdx, true);
        File.SetAttributes(tmpRsc, FileAttributes.Normal);
        File.SetAttributes(tmpIdx, FileAttributes.Normal);
        var img = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                // media dei pixel sorgente che finiscono in questo pixel
                int x0 = x * size / w, x1 = Math.Max(x0 + 1, (x + 1) * size / w), y0 = y * size / h, y1 = Math.Max(y0 + 1, (y + 1) * size / h);
                long r = 0, g = 0, b = 0, k = 0;
                for (int yy = y0; yy < y1; yy += 2)
                    for (int xx = x0; xx < x1; xx += 2) { long o = ((long)yy * size + xx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; k++; }
                BitConverter.TryWriteBytes(img.AsSpan((y * w + x) * 2, 2), (ushort)To555((int)(r / k), (int)(g / k), (int)(b / k)));
            }
        }
        using (var fs = new FileStream(tmpRsc, FileMode.Open, FileAccess.Write))
        {
            fs.Position = 8 + off; // i dati iniziano dopo l'intestazione di 8 byte
            fs.Write(img);
        }
        ResourceBundle.BreakLink(dstRsc); ResourceBundle.BreakLink(dstIdx);
        File.Move(tmpRsc, dstRsc); File.Move(tmpIdx, dstIdx);
        return $"Instant Action map: MAP_SMALL {w}×{h} written into a copy of Art\\_FHD\\Main.rsc";
    }

    /// <summary>Median cut sull'istogramma a 15 bit: restituisce colori RGB555.</summary>
    private static ushort[] MedianCut(long[] hist, int n)
    {
        var boxes = new List<List<int>> { Enumerable.Range(0, 32768).Where(k => hist[k] > 0).ToList() };
        while (boxes.Count < n)
        {
            // dividi la scatola con più pixel (pesati per estensione) che abbia almeno 2 colori
            int bi = -1; double bestScore = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var (ext, _) = Extent(boxes[i]);
                double score = boxes[i].Sum(k => (double)hist[k]) * ext;
                if (score > bestScore) { bestScore = score; bi = i; }
            }
            if (bi < 0) break;
            var box = boxes[bi];
            var (_, ch) = Extent(box);
            int shift = ch == 0 ? 10 : ch == 1 ? 5 : 0;
            box.Sort((a, b) => ((a >> shift) & 31).CompareTo((b >> shift) & 31));
            long total = box.Sum(k => hist[k]), acc = 0; int cut = 1;
            for (int i = 0; i < box.Count - 1; i++) { acc += hist[box[i]]; if (acc >= total / 2) { cut = i + 1; break; } }
            boxes[bi] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }
        return boxes.Select(b =>
        {
            double w = 0, r = 0, g = 0, bl = 0;
            foreach (var k in b) { double h = hist[k]; w += h; r += h * ((k >> 10) & 31); g += h * ((k >> 5) & 31); bl += h * (k & 31); }
            return (ushort)(((int)Math.Round(r / w) << 10) | ((int)Math.Round(g / w) << 5) | (int)Math.Round(bl / w));
        }).ToArray();
    }

    private static (int Extent, int Channel) Extent(List<int> box)
    {
        int best = -1, ch = 0;
        for (int c = 0; c < 3; c++)
        {
            int shift = c == 0 ? 10 : c == 1 ? 5 : 0, lo = 31, hi = 0;
            foreach (var k in box) { int v = (k >> shift) & 31; if (v < lo) lo = v; if (v > hi) hi = v; }
            if (hi - lo > best) { best = hi - lo; ch = c; }
        }
        return (best, ch);
    }
}
