using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Tipi di terreno (GroundType) di BMS, dal manuale di Terrain Builder: sono i valori della LandCoverMap.
/// </summary>
public static class GroundTypes
{
    public const byte NoData = 0, Water = 1, River = 2, Swamp = 3, Plains = 4, Brush = 5, ThinForest = 6, ThickForest = 7,
        Rocky = 8, Urban = 9, Road = 10, Rail = 11, Bridge = 12, Runway = 13, Airbase = 14, Object = 15, Shrubs = 16,
        SnowIce = 17, Cropland = 18, MossLichen = 19;

    /// <summary>Nome, densità bush/shrub/rock/grass, grassSizeFactor, grassOnGreenOnly. Valori della Korea, BRUSH ridotto per il deserto.</summary>
    public static readonly (string Name, double Bush, double Shrub, double Rock, double Grass, double GrassSize, bool GreenOnly)[] Defaults =
    {
        ("NODATA",                0,    0,     0,   0.25,  1,     true),
        ("WATER (Ocean, Sea)",    0,    0,     0,   0.125, 1,     true),
        ("RIVER (In-Land Water)", 1,    1,     0,   0.5,   1,     true),
        ("SWAMP",                 1,    1,     0,   0.5,   1,     true),
        ("PLAINS",                1,    1,     1,   0.25,  0.75,  true),
        ("BRUSH",                 0.1,  0.25,  0.5, 0.05,  0.75,  true),
        ("THIN FOREST",           1,    1,     0.5, 0.5,   0.75,  false),
        ("THICK FOREST",          1,    1,     0.5, 0.5,   0.75,  false),
        ("ROCKY",                 0,    1,     0,   0.1,   0.75,  false),
        ("URBAN",                 0,    0,     0,   0.1,   1,     true),
        ("ROAD",                  0,    0,     0,   0,     1,     false),
        ("RAIL",                  0,    0,     0,   0,     1,     false),
        ("BRIDGE",                0,    0,     0,   0,     1,     false),
        ("RUNWAY",                0,    0,     0,   0,     1,     false),
        ("AIRBASE",               0,    0.065, 0,   1,     0.375, true),
        ("OBJECT",                0,    0,     0,   0,     1,     false),
        ("SHRUBS",                0.5,  1,     1,   0.75,  1,     false),
        ("SNOW AND ICE",          0,    0,     0,   0,     1,     false),
        ("CROPLAND",              1,    1,     0.5, 0.25,  1,     true),
        ("MOSS_LICHEN",           0.5,  0.5,   0.5, 0.5,   0.75,  true),
    };

    /// <summary>Colori per l'anteprima, uno per tipo.</summary>
    public static readonly (byte R, byte G, byte B)[] Colors =
    {
        (0,0,0), (20,60,140), (60,130,220), (70,120,110), (170,200,100), (215,195,140), (80,160,60), (20,100,30),
        (130,120,110), (200,40,40), (255,255,255), (120,120,120), (255,200,0), (40,40,40), (255,0,255), (128,0,128),
        (150,170,80), (240,240,255), (230,220,60), (160,200,180),
    };

    /// <summary>ESA WorldCover (più 200 = mare) → GroundType. Gli alberi si dividono in rado/fitto con la copertura Hansen.</summary>
    public static byte FromEsa(byte esa, byte treeCover255) => esa switch
    {
        10 => treeCover255 >= 128 ? ThickForest : ThinForest,
        20 => Shrubs,
        30 => Plains,
        40 => Cropland,
        50 => Urban,
        60 => Brush,
        70 => SnowIce,
        80 => River,
        90 or 95 => Swamp,
        100 => MossLichen,
        200 => Water,
        _ => Brush, // nessun dato: nel deserto il tipo più probabile
    };

    /// <summary>
    /// LandCoverTypes.txt nel formato proto di TB (come quello della Korea). Ogni tipo ha come landcoverMapIDs il proprio id:
    /// la mappa contiene già i GroundType, così il file è coerente in qualunque modo il motore lo usi.
    /// </summary>
    public static string LandCoverTypesTxt(int defaultIndex)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("defaultIndex: ").Append(defaultIndex).Append('\n');
        for (int i = 0; i < Defaults.Length; i++)
        {
            var d = Defaults[i];
            sb.Append("protoLandCoverType {\n");
            sb.Append("  name: \"").Append(d.Name).Append("\"\n");
            sb.Append("  id: ").Append(i).Append('\n');
            sb.Append("  landcoverMapIDs: ").Append(i).Append('\n');
            sb.Append("  bushIDs: \"All\"\n  shrubIDs: \"All\"\n  rockIDs: \"All\"\n");
            sb.Append("  bushDensity: ").Append(d.Bush.ToString(ci)).Append('\n');
            sb.Append("  shrubDensity: ").Append(d.Shrub.ToString(ci)).Append('\n');
            sb.Append("  rockDensity: ").Append(d.Rock.ToString(ci)).Append('\n');
            sb.Append("  grassDensity: ").Append(d.Grass.ToString(ci)).Append('\n');
            sb.Append("  grassSizeFactor: ").Append(d.GrassSize.ToString(ci)).Append('\n');
            sb.Append("  grassNoiseTilingFactor: 32\n");
            sb.Append("  grassOnGreenOnly: ").Append(d.GreenOnly ? "true" : "false").Append('\n');
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    /// <summary>ForestTypes.txt: un solo tipo a foglia larga (texture 1–5 come la Korea), usato ovunque.</summary>
    public static string ForestTypesTxt() =>
        "defaultIndex: 0\n" +
        "protoForestType {\n" +
        "  name: \"Broad leaf\"\n" +
        "  id: 0\n" +
        "  landcoverMapIDs: 6\n" +
        "  landcoverMapIDs: 7\n" +
        "  treesModelIDs: 1\n  treesModelIDs: 2\n  treesModelIDs: 3\n  treesModelIDs: 4\n  treesModelIDs: 5\n" +
        "  randomSize {\n    minSize: 30\n    maxSize: 60\n  }\n" +
        "  maxDensity: 20\n" +
        "}\n";
}

// ───────────────────────── LandCover ─────────────────────────

public sealed class LandCoverJob : TextureJob
{
    public override string Id => "landcover";
    public override string Name => "LandCoverMap.raw";
    public override string Description => "Replaces TB LandCover mode: ESA land cover (tab 4) → BMS ground types 0–19, sea and inland water from the Watermap, roads and bridges burned in. Full size, 8-bit. Also writes LandCoverTypes.txt.";
    public override string Output => @"GroundTypes\LandCoverMap.raw";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("LandCover (tab 4)", c.FindTool("LandCover.raw"), true),
        c.Input("Watermap (tab 4)", c.FindTool("Watermap.raw"), false),
        c.Input("Tree cover (tab 4, thin/thick forest)", c.FindTool("TreesCover.raw"), false),
        c.Input("Roads (tab 4)", c.FindTool("Roads.raw"), false),
        c.Input("Bridges (tab 4)", c.FindTool("Bridges.raw"), false),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        int size = inp[0].Size;
        var notes = new List<string>();
        IChannelSource? Opt(JobInput i, Resample m) => i.Path == null ? null : new RawChannel(i.Path, size, m);
        using var lc = new RawChannel(inp[0].Path!, size, Resample.Nearest);
        using var wm = Opt(inp[1], Resample.Nearest);
        using var tc = Opt(inp[2], Resample.Average);
        using var rd = Opt(inp[3], Resample.Nearest);
        using var br = Opt(inp[4], Resample.Nearest);
        foreach (var i in inp.Skip(1).Where(i => i.Path == null)) notes.Add("not used (missing): " + i.Role);

        string dst = c.Out(Output), tmp = dst + ".tmp";
        var a = new byte[size]; var w = new byte[size]; var t = new byte[size]; var r = new byte[size]; var b = new byte[size];
        var counts = new long[256];
        long esaWaterOnLand = 0;
        const int ps = DdsComposer.PreviewSize;
        var preview = new byte[ps * ps * 3];
        int pstep = Math.Max(1, size / ps);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            for (int y = 0; y < size; y++)
            {
                ct.ThrowIfCancellationRequested();
                lc.ReadRow(y, a);
                if (wm != null) wm.ReadRow(y, w);
                if (tc != null) tc.ReadRow(y, t);
                if (rd != null) rd.ReadRow(y, r);
                if (br != null) br.ReadRow(y, b);
                for (int x = 0; x < size; x++)
                {
                    byte g = GroundTypes.FromEsa(a[x], tc != null ? t[x] : (byte)0);
                    if (wm != null)
                    {
                        if (w[x] == 45) g = GroundTypes.Water;
                        else if (w[x] == 25) g = GroundTypes.River;
                        // acqua per ESA ma terra per la Watermap (che decide dove BMS disegna l'acqua, pennello compreso):
                        // palude, non acqua, cosi' le celle della campagna non bloccano i mezzi dove l'acqua non c'e'
                        else if (g is GroundTypes.Water or GroundTypes.River) { g = GroundTypes.Swamp; esaWaterOnLand++; }
                    }
                    if (rd != null && r[x] != 0 && g != GroundTypes.Water && g != GroundTypes.River) g = GroundTypes.Road;
                    if (br != null && b[x] != 0) g = GroundTypes.Bridge;
                    a[x] = g;
                    counts[g]++;
                }
                fs.Write(a, 0, size);
                if (y % pstep == 0 && y / pstep < ps)
                    for (int px = 0; px < ps; px++)
                    {
                        var col = GroundTypes.Colors[Math.Min(a[px * pstep], (byte)19)];
                        int o = ((y / pstep) * ps + px) * 3;
                        preview[o] = col.R; preview[o + 1] = col.G; preview[o + 2] = col.B;
                    }
                if ((y & 127) == 0) p.Report((double)y / size);
            }
        }
        File.Move(tmp, dst, true);
        File.WriteAllText(c.Out(@"GroundTypes\LandCoverTypes.txt"), CustomOrDefault(c, "LandCoverTypes.txt", () => GroundTypes.LandCoverTypesTxt(GroundTypes.Brush)));

        double tot = (double)size * size;
        notes.Insert(0, $"{size}² ground types: " + string.Join(", ", Enumerable.Range(0, 20).Where(i => counts[i] > 0)
            .OrderByDescending(i => counts[i]).Select(i => $"{GroundTypes.Defaults[i].Name.Split(' ')[0]} {100 * counts[i] / tot:0.#}%")));
        if (esaWaterOnLand > 0)
            notes.Add($"ESA water where the Watermap has land (no water drawn by BMS there): {esaWaterOnLand:N0} pixels ({esaWaterOnLand * Math.Pow(c.Project.Theater.SizeMeters / size, 2) / 1e6:0} km²) set to SWAMP, so the campaign map does not block ground units there");
        notes.Add(File.Exists(Path.Combine(c.Project.ProjectFolder, "LandCoverTypes.txt"))
            ? "LandCoverTypes.txt copied from the project folder"
            : "LandCoverTypes.txt: default values (to customise, put your own LandCoverTypes.txt in the project folder)");
        return (preview, notes);
    }

    internal static string CustomOrDefault(TextureContext c, string name, Func<string> def)
    {
        string own = Path.Combine(c.Project.ProjectFolder, name);
        return File.Exists(own) ? File.ReadAllText(own) : def();
    }
}

// ───────────────────────── Forest ─────────────────────────

public sealed class ForestJob : TextureJob
{
    public override string Id => "forest";
    public override string Name => "ForestMap.raw";
    public override string Description => "Replaces TB Forest mode: Hansen tree cover (tab 4) → 16384² map, 4 high bits = density 0–15, 4 low bits = forest type (0). No trees on water or below 10 % cover. Also writes ForestTypes.txt.";
    public override string Output => @"Trees\Maps\ForestMap.raw";

    /// <summary>Copertura minima (0–255) sotto la quale non si mettono alberi: 10 %.</summary>
    public int MinCover { get; set; } = 26;

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("Tree cover (tab 4)", c.FindTool("ForestMap16384.raw", "TreesCover.raw"), true),
        c.Input("Watermap (tab 4)", c.FindTool("Watermap.raw"), false),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        const int size = 16384;
        var notes = new List<string>();
        using var tc = new RawChannel(inp[0].Path!, size, Resample.Average);
        using var wm = inp[1].Path == null ? null : new RawChannel(inp[1].Path!, size, Resample.Average);
        if (wm == null) notes.Add("Watermap missing: trees are not removed from water");

        string dst = c.Out(Output), tmp = dst + ".tmp";
        var t = new byte[size]; var w = new byte[size];
        long trees = 0;
        var dens = new long[16];
        const int ps = DdsComposer.PreviewSize;
        var preview = new byte[ps * ps * 3];
        int pstep = size / ps;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            for (int y = 0; y < size; y++)
            {
                ct.ThrowIfCancellationRequested();
                tc.ReadRow(y, t);
                if (wm != null) wm.ReadRow(y, w);
                for (int x = 0; x < size; x++)
                {
                    int cov = t[x];
                    // media della Watermap sul blocco 2×2: qualunque acqua toglie gli alberi
                    if (cov < MinCover || (wm != null && w[x] != 0)) { t[x] = 0; continue; }
                    int d = Math.Clamp((cov * 15 + 127) / 255, 1, 15);
                    t[x] = (byte)(d << 4); // tipo 0
                    dens[d]++; trees++;
                }
                fs.Write(t, 0, size);
                if (y % pstep == 0)
                    for (int px = 0; px < ps; px++)
                    {
                        int d = t[px * pstep] >> 4, o = ((y / pstep) * ps + px) * 3;
                        if (d == 0) { preview[o] = 235; preview[o + 1] = 225; preview[o + 2] = 200; }
                        else { preview[o] = (byte)(120 - d * 7); preview[o + 1] = (byte)(200 - d * 8); preview[o + 2] = (byte)(90 - d * 5); }
                    }
                if ((y & 127) == 0) p.Report((double)y / size);
            }
        }
        File.Move(tmp, dst, true);
        File.WriteAllText(c.Out(@"Trees\Maps\ForestTypes.txt"), LandCoverJob.CustomOrDefault(c, "ForestTypes.txt", GroundTypes.ForestTypesTxt));

        notes.Insert(0, $"{size}²: trees on {100.0 * trees / ((double)size * size):0.##}% of the theater (cover ≥ {MinCover * 100 / 255} %), density 1–15");
        notes.Add(File.Exists(Path.Combine(c.Project.ProjectFolder, "ForestTypes.txt"))
            ? "ForestTypes.txt copied from the project folder"
            : "ForestTypes.txt: one broad-leaf type (tree textures 1–5); to customise, put your own ForestTypes.txt in the project folder");
        return (preview, notes);
    }
}

// ───────────────────────── FFTMap (onde) ─────────────────────────

/// <summary>
/// FFTMap.raw (2048², 1 km/px): dove BMS simula le onde. Regola ricavata dalla Korea: 128 = mare aperto,
/// 250 = fascia costiera (circa 5 km a cavallo della riva), 0/64 = terraferma. TB in alcune baie basse (es. Kuwait)
/// mette 0: acqua piatta con bordi a scalini. Qui mare e costa si ricalcolano dalla Watermap; la terraferma resta quella di TB.
/// </summary>
public sealed class FftMapJob : TextureJob
{
    public override string Id => "fft";
    public override string Name => "FFTMap.raw (waves)";
    public override string Description => "Wave map 2048² rebuilt from the Watermap with the Korea rule: 128 open sea, 250 coastal band (~5 km), land kept from TB. Fixes flat, stair-edged water in shallow bays.";
    public override string Output => @"WaterMaps\FFTMap.raw";

    public const int Grid = 2048;
    public int CoastBand { get; set; } = 4;

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("Watermap (tab 4)", c.FindTool("Watermap.raw"), true),
        c.Input("TB FFTMap.raw (for land)", c.FindTb("FFTMap.raw"), false),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        int src = inp[0].Size, f = src / Grid;
        if (src % Grid != 0) throw new InvalidDataException($"Watermap size {src} is not a multiple of {Grid}");
        var sea = new int[Grid * Grid];
        using (var fs = new FileStream(inp[0].Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22))
        {
            var row = new byte[src];
            for (int y = 0; y < src; y++)
            {
                ct.ThrowIfCancellationRequested();
                fs.ReadExactly(row);
                int gy = y / f;
                for (int x = 0; x < src; x++) if (row[x] == 45) sea[gy * Grid + x / f]++;
                if ((y & 511) == 0) p.Report(0.8 * y / src);
            }
        }
        int full = f * f;
        // distanza (a scacchiera) dalle celle di costa, cioè con mare parziale o mare accanto a terra
        var dist = new int[Grid * Grid];
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                int i = y * Grid + x, s = sea[i];
                bool coast = s > 0 && s < full;
                if (!coast && s == full)
                    for (int dy = -1; dy <= 1 && !coast; dy++)
                        for (int dx = -1; dx <= 1 && !coast; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < Grid && ny < Grid && sea[ny * Grid + nx] == 0) coast = true;
                        }
                if (coast) { dist[i] = 0; q.Enqueue(i); }
            }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), d = dist[i];
            if (d >= CoastBand) continue;
            int x = i % Grid, y = i / Grid;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= Grid || ny >= Grid) continue;
                    int j = ny * Grid + nx;
                    if (dist[j] > d + 1) { dist[j] = d + 1; q.Enqueue(j); }
                }
        }
        var tb = inp[1].Path != null && new FileInfo(inp[1].Path!).Length == (long)Grid * Grid ? File.ReadAllBytes(inp[1].Path!) : new byte[Grid * Grid];
        var outMap = new byte[Grid * Grid];
        long nSea = 0, nCoast = 0, changed = 0;
        for (int i = 0; i < outMap.Length; i++)
        {
            byte v = dist[i] <= CoastBand ? (byte)250 : sea[i] == full ? (byte)128 : sea[i] > 0 ? (byte)250 : tb[i];
            if (v == 250) nCoast++; else if (v == 128) nSea++;
            if (v != tb[i]) changed++;
            outMap[i] = v;
        }
        File.WriteAllBytes(c.Out(Output), outMap);

        const int ps = DdsComposer.PreviewSize;
        var prev = new byte[ps * ps * 3];
        int st = Grid / ps;
        for (int y = 0; y < ps; y++)
            for (int x = 0; x < ps; x++)
            {
                byte v = outMap[y * st * Grid + x * st];
                var col = v == 128 ? (30, 90, 170) : v == 250 ? (240, 240, 240) : v == 64 ? (120, 110, 90) : (60, 55, 45);
                int o = (y * ps + x) * 3; prev[o] = (byte)col.Item1; prev[o + 1] = (byte)col.Item2; prev[o + 2] = (byte)col.Item3;
            }
        p.Report(1);
        return (prev, new List<string>
        {
            $"{Grid}²: open sea {100.0 * nSea / outMap.Length:0.#}%, coastal band {100.0 * nCoast / outMap.Length:0.#}%, {changed} cells differ from TB",
            inp[1].Path == null ? "TB FFTMap not found: land set to 0" : "land cells kept from the TB FFTMap",
        });
    }
}
