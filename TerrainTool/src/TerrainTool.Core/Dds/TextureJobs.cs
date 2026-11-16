using TerrainTool.Core.Project;

namespace TerrainTool.Core.Dds;

/// <summary>Un ingresso di un lavoro: file trovato (o no) e la sua dimensione in pixel.</summary>
public sealed record JobInput(string Role, string? Path, int Size, bool Required);

/// <summary>
/// Lavori al posto di Photoshop: composizione delle DDS dai RAW di Terrain Builder e copia dei RAW
/// finali nella struttura NewTerrain (Output\NewTerrain del progetto, mai l'installazione BMS).
/// </summary>
public abstract class TextureJob
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    /// <summary>Percorso di uscita relativo a NewTerrain.</summary>
    public abstract string Output { get; }
    public abstract IReadOnlyList<JobInput> Inputs(TextureContext ctx);
    /// <summary>Esegue il lavoro; restituisce un'anteprima RGB (o null) e delle note.</summary>
    public abstract (byte[]? Preview, List<string> Notes) Run(TextureContext ctx, IProgress<double> p, CancellationToken ct);

    public string? Missing(TextureContext ctx)
    {
        var miss = Inputs(ctx).Where(i => i.Required && i.Path == null).Select(i => i.Role).ToList();
        return miss.Count == 0 ? null : string.Join(", ", miss);
    }

    public static IReadOnlyList<TextureJob> All { get; } = new TextureJob[]
    {
        new DepthMapJob(), new CullingMapJob(), new GlobalAlphaJob(), new CopyRawJob(), new LandCoverJob(), new ForestJob(),
        new GlobalColorMapJob(), new WaterColorMapJob(), new Photoreal4KJob(), new SnowMapsJob(), new CampMapJob(), new CampMapJob(testPoints: true), new TheaterArtJob(), new ObjectsJob(), new AssembleJob(), new ProbeJob(),
    };
}

public sealed class TextureContext
{
    public TheaterProject Project { get; }
    /// <summary>Cartella di uscita scelta in Terrain Builder.</summary>
    public string TbOutput { get; }
    /// <summary>Le uscite di questo tool (scheda Processing).</summary>
    public string ToolOutput => Path.Combine(Project.ProjectFolder, "Output", "TB_Input");
    public string NewTerrain => Project.OutputNewTerrain;

    public TextureContext(TheaterProject project, string tbOutput)
    {
        Project = project;
        TbOutput = tbOutput;
        // an installed theater's NewTerrain is only read (Objective Studio): the texture jobs never run on it
        if (!project.IsInstalledTheater && project.IsProtectedPath(NewTerrain)) throw new InvalidOperationException("NewTerrain output folder is protected.");
    }

    /// <summary>Cerca un file per nome (senza distinzione maiuscole) nella cartella di TB, sottocartelle comprese.</summary>
    public string? FindTb(params string[] names) =>
        (Project.UseToolWaterDem && names.Any(n => WaterDemNames.Contains(n)) ? Find(Project.WaterDemOutput, names) : null) ?? Find(TbOutput, names);

    /// <summary>Le mappe scritte dalla modalita' Water DEM (di TB o del tool).</summary>
    public static readonly HashSet<string> WaterDemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Heightmap.raw", "WaterHeightmap.raw", "WaterReflectionDemRefmap.raw", "Depthmap_Red.raw", "Depthmap_Blue.raw",
        "GlobalAlpha_Blue.raw", "CullingMap.raw", "FFTMap.raw",
    };
    public string? FindTool(params string[] names) => Find(ToolOutput, names);

    private static string? Find(string dir, string[] names)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        foreach (var n in names)
        {
            var hit = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    public JobInput Input(string role, string? path, bool required) =>
        new(role, path, path == null ? 0 : RawChannel.SizeOf(path), required);

    public string Out(string rel)
    {
        string p = System.IO.Path.Combine(NewTerrain, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        return p;
    }
}

// ───────────────────────── DepthMap ─────────────────────────

public sealed class DepthMapJob : TextureJob
{
    public override string Id => "depth";
    public override string Name => "DepthMap_nsrgb.dds";
    public override string Description => "R = TB DepthMap_Red (average), G = ocean depth from tab 4, B = TB DepthMap_Blue (nearest). 16384² DXT1, no mipmaps.";
    public override string Output => @"WaterMaps\DepthMap_nsrgb.dds";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("TB DepthMap_Red.raw", c.FindTb("DepthMap_Red.raw"), true),
        c.Input("OceanDepth (tab 4)", c.FindTool("OceanDepth_16384.raw"), true),
        c.Input("TB DepthMap_Blue.raw", c.FindTb("DepthMap_Blue.raw"), true),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        const int size = 16384;
        using var r = new RawChannel(inp[0].Path!, size, Resample.Average);
        using var g = new RawChannel(inp[1].Path!, size, Resample.Average);
        using var b = new RawChannel(inp[2].Path!, size, Resample.Nearest);
        var prev = DdsComposer.ComposeDxt1(c.Out(Output), size, r, g, b, p, ct);
        return (prev, new List<string> { $"Sources: R {inp[0].Size}², G {inp[1].Size}², B {inp[2].Size}² → {size}²" });
    }
}

// ───────────────────────── CullingMap ─────────────────────────

public sealed class CullingMapJob : TextureJob
{
    public override string Id => "culling";
    public override string Name => "CullingMap_nsrgb.dds";
    public override string Description => "TB CullingMap.raw copied to R, G and B (values 0/255). 500 m/px: 2048² for 1024 km, 4096² for 2048 km. DXT1, no mipmaps.";
    public override string Output => @"WaterMaps\CullingMap_nsrgb.dds";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("TB CullingMap.raw", c.FindTb("CullingMap.raw"), true),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c)[0];
        int target = c.Project.Theater.SizeKm * 2; // 500 m/px
        int size = inp.Size >= target ? target : inp.Size;
        using var r = new RawChannel(inp.Path!, size, Resample.Nearest);
        using var g = new RawChannel(inp.Path!, size, Resample.Nearest);
        using var b = new RawChannel(inp.Path!, size, Resample.Nearest);
        var prev = DdsComposer.ComposeDxt1(c.Out(Output), size, r, g, b, p, ct);
        return (prev, new List<string> { $"Source {inp.Size}² → {size}²" });
    }
}

// ───────────────────────── GlobalAlpha ─────────────────────────

public sealed class GlobalAlphaJob : TextureJob
{
    public override string Id => "globalalpha";
    public override string Name => "GlobalAlpha_nsrgb.dds";
    public override string Description => "R = 255 (or TB hires blend map), G = 0 (unused in 4.38; TB GlobalAlpha_Green.raw if present), B = TB GlobalAlpha_Blue (underwater blend). 16384² DXT1.";
    public override string Output => @"GroundTypes\GlobalAlpha_nsrgb.dds";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        c.Input("Hires blend (optional)", c.FindTb("GlobalAlpha_Red.raw", "HiresBlend.raw"), false),
        c.Input("TB GlobalAlpha_Green.raw (optional)", c.FindTb("GlobalAlpha_Green.raw"), false),
        c.Input("TB GlobalAlpha_Blue.raw", c.FindTb("GlobalAlpha_Blue.raw"), true),
    };

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        const int size = 16384;
        var notes = new List<string>();
        using IChannelSource r = inp[0].Path != null ? new RawChannel(inp[0].Path!, size, Resample.Average) : (IChannelSource)new ConstantChannel(255);
        using IChannelSource g = inp[1].Path != null ? new RawChannel(inp[1].Path!, size, Resample.Average) : (IChannelSource)new ConstantChannel(0);
        using var b = new RawChannel(inp[2].Path!, size, Resample.Average);
        if (inp[0].Path == null) notes.Add("R = 255 everywhere (no hires tiles yet)");
        if (inp[1].Path == null) notes.Add("G = 0 (the TB manual for 4.38 marks the green channel as unused)");
        var prev = DdsComposer.ComposeDxt1(c.Out(Output), size, r, g, b, p, ct);
        return (prev, notes);
    }
}

// ───────────────────────── Copia RAW ─────────────────────────

public sealed class CopyRawJob : TextureJob
{
    private static readonly (string Role, string[] Names, string Dest, bool FromTool)[] Files =
    {
        ("Heightmap (TB, after Water DEM)", new[] { "Heightmap.raw", "HeightMap.raw" }, @"HeightMaps\HeightMap.raw", false),
        ("Water heightmap (TB)", new[] { "WaterHeightmap.raw", "WaterHeightMap.raw" }, @"HeightMaps\WaterHeightMap.raw", false),
        ("FFT map (TB)", new[] { "FFTMap.raw" }, @"WaterMaps\FFTMap.raw", false),
    };

    public override string Id => "copy";
    public override string Name => "Copy TB RAW outputs";
    public override string Description => "Copies the finished RAW/TXT files from the TB output folder into Output\\NewTerrain with the names BMS expects (HeightMap.raw, WaterHeightMap.raw, FFTMap.raw). Land cover and forest maps are made by the two jobs below, without TB.";
    public override string Output => @"(several files)";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) =>
        Files.Select(f => c.Input(f.Role, c.FindTb(f.Names), false)).ToList();

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var notes = new List<string>();
        int i = 0;
        foreach (var f in Files)
        {
            ct.ThrowIfCancellationRequested();
            string? src = c.FindTb(f.Names);
            if (src == null) { notes.Add($"skipped (not found): {f.Role}"); continue; }
            string dst = c.Out(f.Dest);
            // il file .raw della heightmap dell'ingresso (Mideast_32768.RAW) NON va copiato: serve quello uscito dal Water DEM
            File.Copy(src, dst, true);
            notes.Add($"{f.Dest} ← {src} ({new FileInfo(src).Length / 1048576.0:0} MB, {File.GetLastWriteTime(src):dd/MM HH:mm})");
            p.Report(++i / (double)Files.Length);
        }
        return (null, notes);
    }
}
