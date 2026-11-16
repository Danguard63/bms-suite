using System.Globalization;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Un teatro di BMS come lo descrive la sua .tdf (elencata in Data\TerrData\TheaterDefinition\theater.lst):
/// cartelle della campagna, del database (objectdir), dei modelli 3D (3ddatadir) e del terreno. Solo lettura.
/// </summary>
public sealed class BmsTheater
{
    public string Name { get; init; } = "";
    public string Tdf { get; init; } = "";
    public string DataDir { get; init; } = "";
    public string CampaignDir { get; init; } = "";
    public string ObjectDir { get; init; } = "";
    public string ModelsDir { get; init; } = "";
    public string TerrainDir { get; init; } = "";
    public string NewTerrainDir { get; init; } = "";
    /// <summary>Cartella "artdir" della .tdf (grafica dell'interfaccia: bandiere, icone, mappa 2D).</summary>
    public string ArtDir { get; init; } = "";
    public string TheaterTxt => Path.Combine(NewTerrainDir, "Theater.txt");
    public bool IsNewTerrain => File.Exists(TheaterTxt);
    /// <summary>Cartella dell'add-on in Data (es. "Add-On Mideast128"); "BMS" per i teatri della 4.38 (TerrData\TheaterDefinition di BMS).</summary>
    public string AddOnFolder
    {
        get
        {
            string rel = Path.GetRelativePath(DataDir, Tdf);
            string first = rel.Split(Path.DirectorySeparatorChar, '/', '\\')[0];
            return first.Equals("TerrData", StringComparison.OrdinalIgnoreCase) || first == ".." ? "BMS" : first;
        }
    }
    /// <summary>Riga sotto il nome negli elenchi dei teatri: cartella dell'add-on e tipo di terreno.</summary>
    public string Subtitle => AddOnFolder + " · " + (IsNewTerrain ? "New Terrain" : "classic terrain");
    public override string ToString() => Name;

    /// <summary>Teatri installati (theater.lst), nell'ordine del file. Le righe con la .tdf mancante vengono saltate.</summary>
    public static List<BmsTheater> List(string dataDir)
    {
        var res = new List<BmsTheater>();
        string lst = Path.Combine(dataDir, "TerrData", "TheaterDefinition", "theater.lst");
        if (!File.Exists(lst)) return res;
        foreach (var raw in File.ReadAllLines(lst))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            string tdf = Path.Combine(dataDir, Norm(line));
            if (!File.Exists(tdf)) continue;
            try { res.Add(Read(dataDir, tdf)); } catch (IOException) { }
        }
        return res;
    }

    /// <summary>Le .tdf usano '\\': su Windows non cambia nulla.</summary>
    private static string Norm(string p) => p.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    public static BmsTheater Read(string dataDir, string tdf)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(tdf))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) continue;
            kv.TryAdd(line[..sp].Trim(), line[(sp + 1)..].Trim());
        }
        string D(string key, string def) => Path.GetFullPath(Path.Combine(dataDir, Norm(kv.TryGetValue(key, out var v) && v.Length > 0 ? v : def)));
        string terrain = D("terraindir", @"Terrdata\korea");
        return new BmsTheater
        {
            Name = kv.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(tdf),
            Tdf = tdf, DataDir = dataDir,
            CampaignDir = D("campaigndir", "Campaign"),
            ObjectDir = D("objectdir", @"Terrdata\objects"),
            ModelsDir = D("3ddatadir", @"Terrdata\objects"),
            ArtDir = D("artdir", "Art"),
            TerrainDir = terrain,
            NewTerrainDir = kv.TryGetValue("newterraindir", out var nt) && nt.Length > 0 ? Path.GetFullPath(Path.Combine(dataDir, Norm(nt))) : Path.Combine(terrain, "NewTerrain"),
        };
    }
}

/// <summary>
/// Dove lavora l'editor della campagna: database, mappa, cartella dove salva, cartelle da cui legge.
/// Due casi: il progetto del nuovo terreno (Mideast: CampaignEdits + Output\Install, installa nell'add-on)
/// oppure un teatro qualsiasi di BMS (legge dalla sua cartella Campaign, salva sempre fuori da BMS, in C:\TerrainTool\Theaters\&lt;teatro&gt;).
/// </summary>
public sealed class CampaignWorkspace
{
    public string Name { get; init; } = "";
    public TheaterConfig Theater { get; init; } = new();
    /// <summary>True se la dimensione e il centro vengono da Theater.txt (altrimenti sono stimati e le lat/lon non sono affidabili).</summary>
    public bool TheaterKnown { get; init; }
    public List<string> DbDirs { get; init; } = new();
    public string? PhotoDir { get; init; }
    /// <summary>Dove "Save" scrive (mai dentro BMS).</summary>
    public string EditsFolder { get; init; } = "";
    /// <summary>Seconda copia scritta a ogni salvataggio (progetto: Output\Install\&lt;add-on&gt;\Campaign), o null.</summary>
    public string? InstallCopyDir { get; init; }
    /// <summary>Cartelle (sola lettura) con i file originali: campagne, .pri, stations+ils.dat, scenari delle missioni TE.</summary>
    public List<string> SourceCampaignDirs { get; init; } = new();
    /// <summary>Cartella Campaign della Korea di BMS (KTO): il riferimento per la logica della campagna (sola lettura).</summary>
    public string? ReferenceCampaignDir { get; init; }
    public TheaterProject? Project { get; init; }
    public BmsTheater? BmsTheater { get; init; }
    public bool IsProject => Project != null;

    /// <summary>Cartelle della grafica dell'interfaccia, nell'ordine in cui cercarvi (teatro, poi BMS).</summary>
    public List<string> ArtDirs { get; init; } = new();

    /// <summary>Tutte le cartelle in cui cercare un file della campagna, nell'ordine: modifiche, copia Install, originali.</summary>
    public IEnumerable<string> SearchDirs()
    {
        yield return EditsFolder;
        if (InstallCopyDir != null) yield return InstallCopyDir;
        foreach (var d in SourceCampaignDirs) yield return d;
    }

    public static CampaignWorkspace FromProject(TheaterProject p)
    {
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string photo = Path.Combine(p.OutputNewTerrain, "Photoreal", "4K");
        return new CampaignWorkspace
        {
            Name = p.Theater.Name,
            Theater = p.Theater, TheaterKnown = true,
            DbDirs = new List<string>
            {
                Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Terrdata", ObjectsJob.FolderName),
                Path.Combine(p.OldTheaterFolder, "Terrdata", ObjectsJob.FolderName),
                BmsObjectsDir(p.KoreaNewTerrain) ?? "",
                Path.Combine(p.OldTheaterFolder, "Terrdata", "Objects"),
            },
            PhotoDir = Directory.Exists(photo) ? photo : null,
            EditsFolder = ObjectsJob.CampaignEditsFolder(p),
            InstallCopyDir = Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign"),
            SourceCampaignDirs = new List<string> { Path.Combine(p.OldTheaterFolder, "Campaign") },
            ReferenceCampaignDir = BmsDataDir(p.KoreaNewTerrain) is { } dd ? Path.Combine(dd, "Campaign") : null,
            Project = p,
            ArtDirs = new List<string?>
            {
                Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "NT"),
                BmsDataDir(p.KoreaNewTerrain) is { } d1 ? Path.Combine(d1, addOn, "NT") : null,
                Path.Combine(p.OldTheaterFolder, "Art"),
                BmsDataDir(p.KoreaNewTerrain) is { } d2 ? Path.Combine(d2, "Art") : null,
            }.Where(x => x != null).Select(x => x!).ToList(),
        };
    }

    /// <summary>Teatro di BMS: legge dalla sua cartella Campaign e dal suo database, salva in workRoot\&lt;teatro&gt;\Campaign.</summary>
    public static CampaignWorkspace FromTheater(BmsTheater t, string workRoot)
    {
        TheaterConfig cfg;
        bool known = false;
        if (t.IsNewTerrain)
        {
            try { cfg = TheaterConfig.ParseTheaterTxt(File.ReadAllText(t.TheaterTxt)); known = true; }
            catch (Exception) { cfg = new TheaterConfig { SizeKm = 1024 }; }
        }
        else cfg = new TheaterConfig { SizeKm = 1024 };
        if (!known)
        {
            // terreno classico: la dimensione la dice il .cmp delle campagne (km), arrotondata alla potenza di 2
            int km = Directory.Exists(t.CampaignDir)
                ? Directory.EnumerateFiles(t.CampaignDir, "*.cam").Concat(Directory.EnumerateFiles(t.CampaignDir, "*.tac")).Take(3).Select(CampaignDoc.ReadTheaterSizeKm).DefaultIfEmpty(0).Max()
                : 0;
            if (km > 0) { int p2 = 256; while (p2 < km) p2 *= 2; cfg.SizeKm = p2; }
        }
        cfg.Name = t.Name;
        string photo = Path.Combine(t.NewTerrainDir, "Photoreal", "4K");
        var dirs = new List<string> { t.ObjectDir };
        if (!dirs.Contains(t.ModelsDir, StringComparer.OrdinalIgnoreCase)) dirs.Add(t.ModelsDir);
        string stock = Path.GetFullPath(Path.Combine(t.DataDir, "Terrdata", "objects"));
        if (!dirs.Contains(stock, StringComparer.OrdinalIgnoreCase)) dirs.Add(stock);
        return new CampaignWorkspace
        {
            Name = t.Name,
            Theater = cfg, TheaterKnown = known,
            DbDirs = dirs,
            PhotoDir = known && Directory.Exists(photo) ? photo : null,
            EditsFolder = Path.Combine(workRoot, SafeName(t.Name), "Campaign"),
            SourceCampaignDirs = new List<string> { t.CampaignDir },
            ReferenceCampaignDir = Path.Combine(t.DataDir, "Campaign"),
            BmsTheater = t,
            ArtDirs = new List<string> { t.ArtDir, Path.Combine(t.DataDir, "Art") },
        };
    }

    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return r.Length == 0 ? "Theater" : r;
    }

    public static string? BmsObjectsDir(string koreaNewTerrain)
    {
        if (string.IsNullOrWhiteSpace(koreaNewTerrain)) return null;
        var d = new DirectoryInfo(koreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        return d == null ? null : Path.Combine(d.FullName, "Terrdata", "objects");
    }

    /// <summary>Cartella Data di BMS dedotta dal progetto (KoreaNewTerrain), o null.</summary>
    public static string? BmsDataDir(string koreaNewTerrain)
    {
        if (string.IsNullOrWhiteSpace(koreaNewTerrain)) return null;
        var d = new DirectoryInfo(koreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        return d?.FullName;
    }

    /// <summary>Vero se il percorso e' dentro l'installazione di BMS (cartella sopra Data) o nel teatro di origine: li' non si scrive mai.</summary>
    public bool IsProtected(string path)
    {
        string full = Path.GetFullPath(path);
        if (Project != null && Project.IsProtectedPath(full)) return true;
        if (BmsTheater != null)
        {
            var root = Directory.GetParent(BmsTheater.DataDir)?.FullName ?? BmsTheater.DataDir;
            if (full.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Il file della campagna con quel nome: prima la copia salvata, poi la copia Install, poi l'originale (null se non c'e').</summary>
    public string? FindCampaignFile(string name)
    {
        foreach (var d in SearchDirs())
        {
            if (!Directory.Exists(d)) continue;
            var f = Directory.EnumerateFiles(d).FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>Lo stesso file nella Korea di BMS (riferimento), o null.</summary>
    public string? FindReferenceFile(string name) =>
        ReferenceCampaignDir != null && Directory.Exists(ReferenceCampaignDir)
            ? Directory.EnumerateFiles(ReferenceCampaignDir).FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>Salva un file della campagna fuori da BMS: nella cartella di salvataggio e (progetto) nella copia Install.</summary>
    public string SaveCampaignFile(string name, Action<string> write)
    {
        Directory.CreateDirectory(EditsFolder);
        string dst = Path.Combine(EditsFolder, name);
        if (IsProtected(dst)) throw new InvalidOperationException("Refused: " + dst + " is inside BMS (read only).");
        if (File.Exists(dst)) File.Copy(dst, dst + ".bak", true);
        write(dst);
        if (InstallCopyDir != null)
        {
            Directory.CreateDirectory(InstallCopyDir);
            File.Copy(dst, Path.Combine(InstallCopyDir, name), true);
        }
        return dst;
    }

    /// <summary>Campagne e missioni TE da aprire: le copie salvate (prima) e gli originali.</summary>
    public List<(string Path, bool Edited)> CampaignFiles()
    {
        var res = new List<(string, bool)>();
        foreach (var (dir, edited) in SearchDirs().Select(d => (d, d == EditsFolder)))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                res.Add((f, edited));
        }
        return res;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Theater.SizeKm} km)");
}
