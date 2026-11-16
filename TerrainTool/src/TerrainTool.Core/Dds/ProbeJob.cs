using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Diagnostica: ritaglia attorno ad alcuni punti (lat;lon) le mappe grandi del terreno (HeightMap, WaterHeightMap,
/// Watermap di TB_Input, LandCoverMap) in file piccoli in Output\Probe, da controllare senza spostare i GB.
/// Punti in Projects\&lt;progetto&gt;\probe.txt (righe "nome;lat;lon;raggio km"), altrimenti Muharraq e Kuwait.
/// </summary>
public sealed class ProbeJob : TextureJob
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public override string Id => "probe";
    public override string Name => "Probe points (diagnostic)";
    public override string Description => "Cuts small windows of HeightMap, WaterHeightMap, Watermap and LandCoverMap around the points in probe.txt (name;lat;lon;radius km) into Output\\Probe, for checking. Default: Muharraq and Kuwait.";
    public override string Output => @"..\Probe";

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        new JobInput("HeightMap.raw", Exists(Path.Combine(c.NewTerrain, "HeightMaps", "HeightMap.raw")), 0, false),
    };

    private static string? Exists(string p) => File.Exists(p) ? p : null;

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var notes = new List<string>();
        var grid = c.Project.Theater.CreateGrid();
        double size = c.Project.Theater.SizeMeters;
        string outDir = Path.Combine(c.Project.ProjectFolder, "Output", "Probe");
        Directory.CreateDirectory(outDir);
        var pts = new List<(string Name, double Lat, double Lon, double Km)>();
        string pf = Path.Combine(c.Project.ProjectFolder, "probe.txt");
        if (File.Exists(pf))
            foreach (var l in File.ReadAllLines(pf))
            {
                var f = l.Split(';');
                if (f.Length >= 3 && double.TryParse(f[1], NumberStyles.Float, Ci, out var la) && double.TryParse(f[2], NumberStyles.Float, Ci, out var lo))
                    pts.Add((f[0].Trim(), la, lo, f.Length > 3 && double.TryParse(f[3], NumberStyles.Float, Ci, out var k) ? k : 10));
            }
        if (pts.Count == 0) pts.AddRange(new[] { ("Muharraq", 26.2708, 50.6336, 15.0), ("Kuwait", 29.2266, 47.9689, 10.0) });

        var maps = new (string Label, string? Path, int Bytes)[]
        {
            ("HeightMap", Exists(Path.Combine(c.NewTerrain, "HeightMaps", "HeightMap.raw")), 2),
            ("WaterHeightMap", Exists(Path.Combine(c.NewTerrain, "HeightMaps", "WaterHeightMap.raw")), 2),
            ("Watermap_TBInput", Exists(Path.Combine(c.ToolOutput, "Watermap.raw")), 1),
            ("LandCoverMap", Exists(Path.Combine(c.NewTerrain, "GroundTypes", "LandCoverMap.raw")), 1),
            ("TBInput_DEM", Exists(Path.Combine(c.ToolOutput, c.Project.Theater.Name + "_" + c.Project.Theater.MapSizePixels + ".RAW")), 2),
        };
        var sb = new StringBuilder("point;map;mapSize;row0;col0;w;h;bytes;file\n");
        foreach (var (name, lat, lon, km) in pts)
        {
            var (x, y) = grid.ToTheater(lat, lon);
            notes.Add($"{name}: theater x={x:0} m, y={y:0} m");
            foreach (var (label, path, bytes) in maps)
            {
                if (path == null) continue;
                long len = new FileInfo(path).Length;
                int ms = (int)Math.Round(Math.Sqrt(len / bytes));
                double px = size / ms;
                int col = (int)(x / px), row = (int)((size - y) / px), r = (int)Math.Ceiling(km * 1000 / px);
                int c0 = Math.Max(0, col - r), r0 = Math.Max(0, row - r), w = Math.Min(ms, col + r) - c0, h = Math.Min(ms, row + r) - r0;
                if (w <= 0 || h <= 0) continue;
                var buf = new byte[(long)w * h * bytes];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    for (int j = 0; j < h; j++)
                    {
                        fs.Position = ((long)(r0 + j) * ms + c0) * bytes;
                        fs.ReadExactly(buf, j * w * bytes, w * bytes);
                    }
                string fn = $"{name}_{label}_{w}x{h}.bin";
                File.WriteAllBytes(Path.Combine(outDir, fn), buf);
                sb.Append($"{name};{label};{ms};{r0};{c0};{w};{h};{bytes};{fn}\n");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "probe.csv"), sb.ToString());
        notes.Add($"Windows written to {outDir} (probe.csv lists them)");
        return (null, notes);
    }
}
