using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.NewTerrain;

/// <summary>
/// Tessere Photoreal\4K del progetto per l'editor delle tessere: lettura a piena risoluzione, reimportazione dopo la
/// modifica in un programma esterno (DXT1 con tutte le mipmap, come Photoreal4KJob) con copia dell'originale in
/// Output\TileEdits\Originals, ripristino. Scrive solo nel progetto (Output\NewTerrain e Output\TileEdits).
/// Numerazione come BMS: da 1, riga per riga da nord-ovest (indice = ty * N + tx + 1).
/// </summary>
public sealed class PhotoTileEdits
{
    public TheaterProject Project { get; }
    public PhotoTiles Tiles { get; }
    public string TileDir { get; }
    public string EditDir { get; }
    public string OriginalDir => Path.Combine(EditDir, "Originals");
    public int N => Tiles.N;
    public double SizeFt { get; }
    public double TileFt => SizeFt / N;

    public PhotoTileEdits(TheaterProject p)
    {
        Project = p;
        TileDir = Path.Combine(p.OutputNewTerrain, "Photoreal", "4K");
        if (!Directory.Exists(TileDir)) throw new DirectoryNotFoundException($"{TileDir} not found: run 'Photoreal 4K tiles' in tab 5 first");
        EditDir = Path.Combine(p.ProjectFolder, "Output", "TileEdits");
        Tiles = new PhotoTiles(TileDir, p.Theater.SizeMeters);
        SizeFt = p.Theater.SizeMeters * TerrainSampler.FtPerM;
        if (p.IsProtectedPath(TileDir) || p.IsProtectedPath(EditDir)) throw new InvalidOperationException("The tile folders are inside BMS or the old theater: not allowed");
    }

    public int Index(int tx, int ty) => ty * N + tx + 1;
    public (int Tx, int Ty) Cell(int index) => ((index - 1) % N, (index - 1) / N);
    public string TilePath(int index) { var (tx, ty) = Cell(index); return Tiles.TilePath(tx, ty); }
    public bool Exists(int index) => index >= 1 && index <= N * N && File.Exists(TilePath(index));
    public bool IsEdited(int index) => File.Exists(OriginalPath(index));
    public string OriginalPath(int index) => Path.Combine(OriginalDir, index + ".dds");
    public string PngPath(int index) => Path.Combine(EditDir, index + ".png");
    public string OverlayPngPath(int index) => Path.Combine(EditDir, index + "_objects.png");

    /// <summary>Tessera che contiene il punto (piedi del teatro: north da sud, east da ovest), o 0 se fuori.</summary>
    public int IndexAt(double north, double east)
    {
        if (north < 0 || east < 0 || north >= SizeFt || east >= SizeFt) return 0;
        return Index((int)(east / TileFt), (int)((SizeFt - north) / TileFt));
    }

    /// <summary>Angoli della tessera in piedi del teatro: ovest, est, sud, nord.</summary>
    public (double West, double East, double South, double North) Bounds(int index)
    {
        var (tx, ty) = Cell(index);
        return (tx * TileFt, (tx + 1) * TileFt, SizeFt - (ty + 1) * TileFt, SizeFt - ty * TileFt);
    }

    /// <summary>Tessera intera (livello 0, 4096²) in BGRA.</summary>
    public byte[] ReadTileBgra(int index)
    {
        var (tx, ty) = Cell(index);
        int side = PhotoTiles.TilePx, ch = PhotoTiles.Chunk, per = side / ch;
        var outp = new byte[(long)side * side * 4];
        lock (Tiles)
        {
            Tiles.Forget(tx, ty);
            for (int cy = 0; cy < per; cy++)
                for (int cx = 0; cx < per; cx++)
                {
                    var c = Tiles.GetChunk(tx, ty, 0, cx, cy, out int s) ?? throw new FileNotFoundException("Tile not readable", TilePath(index));
                    for (int r = 0; r < s; r++)
                        Buffer.BlockCopy(c, r * s * 4, outp, ((cy * ch + r) * side + cx * ch) * 4, s * 4);
                }
            Tiles.Forget(tx, ty);
        }
        return outp;
    }

    /// <summary>
    /// Riscrive la tessera dal disegno modificato (RGB 4096², riga 0 = nord). Prima volta: copia l'originale in Originals.
    /// Scrive su un file temporaneo e poi sostituisce, cosi' un errore non lascia la tessera a meta'.
    /// </summary>
    public void Import(int index, byte[] rgb, CancellationToken ct = default)
    {
        if (rgb.Length != (long)PhotoTiles.TilePx * PhotoTiles.TilePx * 3) throw new ArgumentException("the image must be 4096×4096");
        string dst = TilePath(index);
        Directory.CreateDirectory(OriginalDir);
        if (File.Exists(dst) && !File.Exists(OriginalPath(index))) File.Copy(dst, OriginalPath(index));
        string tmp = dst + ".tmp";
        MipComposer.Compose(tmp, rgb, PhotoTiles.TilePx, ColorDdsFormat.Dxt1, null, ct, makePreview: false);
        File.Move(tmp, dst, true);
        var (tx, ty) = Cell(index);
        lock (Tiles) Tiles.Forget(tx, ty);
    }

    /// <summary>Rimette la tessera originale (copiata alla prima importazione).</summary>
    public bool Restore(int index)
    {
        string o = OriginalPath(index);
        if (!File.Exists(o)) return false;
        File.Copy(o, TilePath(index), true);
        File.Delete(o);
        var (tx, ty) = Cell(index);
        lock (Tiles) Tiles.Forget(tx, ty);
        return true;
    }
}

/// <summary>Obiettivo per la mappa delle tessere: posizione in piedi (north, east), nome, tipo, sagome dei pezzi.</summary>
public sealed record TileObjective(int CampId, string Name, int Type, double North, double East);

/// <summary>
/// Obiettivi, collegamenti e disposizioni delle basi da mostrare sopra le tessere: l'uscita del progetto (Output\Install:
/// Objects / Object Editor e Campaign Editor), cioe' quello che va in BMS. Solo lettura.
/// </summary>
public sealed class TileOverlay
{
    public List<TileObjective> Objectives { get; } = new();
    public List<(int A, int B)> Links { get; } = new();
    public List<string> Notes { get; } = new();
    public string? LinksSource { get; private set; }
    private readonly EditorData? _ed;
    private readonly Dictionary<int, EdObjective> _byId = new();

    public TileOverlay(TheaterProject p)
    {
        try
        {
            _ed = new EditorData(p);
            foreach (var o in _ed.Objectives)
            {
                Objectives.Add(new TileObjective(o.Id, o.Name, o.Type, o.X, o.Y));
                _byId[o.Id] = o;
            }
            Notes.Add($"{Objectives.Count} objectives from {_ed.CampObjSource}");
        }
        catch (Exception ex) { Notes.Add("Objectives not available: " + ex.Message); }

        // collegamenti: dal modello del TE dell'uscita del progetto, altrimenti dal vecchio add-on
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        var dirs = new[] { Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign"), Path.Combine(p.OldTheaterFolder, "Campaign") };
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            string? f = new[] { "Te_New_Nt.tac", "te_new.tac", "save0.cam" }
                .Select(n => Directory.EnumerateFiles(d).FirstOrDefault(x => string.Equals(Path.GetFileName(x), n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(x => x != null);
            if (f == null) continue;
            try
            {
                var files = CampaignArchive.Read(f);
                var obj = files.FirstOrDefault(x => x.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)).Data;
                var ver = files.FirstOrDefault(x => x.Name.EndsWith(".ver", StringComparison.OrdinalIgnoreCase)).Data;
                if (obj == null) continue;
                var list = CampaignDoc.ReadObjectives(obj, ver != null ? UniFile.ReadVersion(ver) : 0);
                var camp = list.ToDictionary(o => o.Id, o => (int)o.CampId);
                var seen = new HashSet<(int, int)>();
                foreach (var o in list)
                    foreach (var l in o.Links)
                        if (camp.TryGetValue(l.To, out int b))
                        {
                            int a = o.CampId;
                            var key = a < b ? (a, b) : (b, a);
                            if (seen.Add(key)) Links.Add(key);
                        }
                LinksSource = f;
                Notes.Add($"{Links.Count} links from {f}");
                break;
            }
            catch (Exception ex) { Notes.Add($"Links not read from {f}: {ex.Message}"); }
        }
        if (LinksSource == null) Notes.Add("No campaign file with objectives found: links not shown");
    }

    public static string TypeName(int t) => EditorData.TypeName(t);

    /// <summary>Sagome dei pezzi di un obiettivo (rettangoli girati, 4 angoli north/east) e percorsi (piste, vie di rullaggio).</summary>
    public (List<(double N, double E)[]> Boxes, List<List<(double N, double E)>> Paths, List<(double N, double E, string Name)> Labels) Layout(int campId)
    {
        var boxes = new List<(double, double)[]>(); var paths = new List<List<(double, double)>>(); var labels = new List<(double, double, string)>();
        if (_ed == null || !_byId.TryGetValue(campId, out var o)) return (boxes, paths, labels);
        EdLayout lay;
        try { lay = _ed.LayoutOf(o); } catch { return (boxes, paths, labels); }
        foreach (var f in lay.Features)
        {
            var (cn, ce) = EditorData.ToWorld(o, f.N, f.E);
            double h = (o.Heading + f.Heading) * Math.PI / 180;
            double fn = Math.Cos(h), fe = Math.Sin(h), rn = -Math.Sin(h), re = Math.Cos(h);
            if (f.MaxX - f.MinX < 1 && f.MaxY - f.MinY < 1) continue;
            (double, double) P(double a, double b) => (cn + a * fn + b * rn, ce + a * fe + b * re);
            boxes.Add(new[] { P(f.MinX, f.MinY), P(f.MaxX, f.MinY), P(f.MaxX, f.MaxY), P(f.MinX, f.MaxY) });
            if (Regex.IsMatch(f.Name, @"rwy|runway|tower|hangar|shelter", RegexOptions.IgnoreCase)) labels.Add((cn, ce, f.Name));
        }
        foreach (var pth in lay.Paths)
            paths.Add(pth.Points.Select(pt => EditorData.ToWorld(o, pt.N, pt.E)).ToList());
        return (boxes, paths, labels);
    }
}
