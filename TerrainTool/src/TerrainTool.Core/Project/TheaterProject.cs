using System.Text.Json;
using TerrainTool.Core.Theater;

namespace TerrainTool.Core.Project;

/// <summary>Progetto salvato come JSON (es. C:\TerrainTool\Progetti\Mideast\Mideast.ttproj).</summary>
public sealed class TheaterProject
{
    public int Version { get; set; } = 1;
    public TheaterConfig Theater { get; set; } = new();
    /// <summary>Cartella di lavoro del progetto (sorgenti, intermedi, output).</summary>
    public string ProjectFolder { get; set; } = "";
    /// <summary>CSV delle basi usato per centro e verifica.</summary>
    public string AirbasesCsv { get; set; } = "";
    /// <summary>NewTerrain della Korea, solo lettura, come riferimento.</summary>
    public string KoreaNewTerrain { get; set; } = @"C:\Falcon BMS 4.38\Data\TerrData\Korea\NewTerrain";
    public string TerrainBuilderFolder { get; set; } = @"C:\TerrainBuilder";
    /// <summary>Vecchio teatro usato come base (solo lettura).</summary>
    public string OldTheaterFolder { get; set; } = @"E:\Add-On Mideast128";

    public string OutputNewTerrain => !string.IsNullOrWhiteSpace(NewTerrainFolder) ? NewTerrainFolder : Path.Combine(ProjectFolder, "Output", "NewTerrain");

    // ───────── theater installed in BMS (Objective Studio): the project is only the work folder of that theater ─────────
    /// <summary>NewTerrain of an installed theater (read only); empty = the project's Output\NewTerrain.</summary>
    public string NewTerrainFolder { get; set; } = "";
    /// <summary>.tdf of the installed theater this work folder belongs to; empty = a TerrainTool project (theater conversion).</summary>
    public string InstalledTheaterTdf { get; set; } = "";
    /// <summary>Objects database of the installed theater (objectdir of the .tdf: Falcon4_CT.xml, ObjectiveRelatedData…), read only.</summary>
    public string SourceObjectsDir { get; set; } = "";
    /// <summary>Campaign folder of the installed theater (campaigndir of the .tdf), read only.</summary>
    public string SourceCampaignDir { get; set; } = "";
    /// <summary>BMS Data folder of the installed theater.</summary>
    public string BmsDataDir { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInstalledTheater => !string.IsNullOrWhiteSpace(InstalledTheaterTdf);
    /// <summary>Cartella di uscita scelta in Terrain Builder (vuota = Output\TB_Output del progetto).</summary>
    public string TbOutputFolder { get; set; } = "";
    /// <summary>Uscita della modalita' Water DEM rifatta nel tool (Output\WaterDem).</summary>
    public string WaterDemOutput => Path.Combine(ProjectFolder, "Output", "WaterDem");
    /// <summary>Se vero, i lavori delle texture prendono le mappe dell'acqua da WaterDemOutput invece che dall'uscita di Terrain Builder.</summary>
    public bool UseToolWaterDem { get; set; }
    /// <summary>Year of the EOX Sentinel-2 cloudless mosaic for the photoreal tiles: 2016 (CC BY 4.0) or a later year
    /// (2018-2024, CC BY-NC-SA 4.0: non-commercial, share-alike). Each year has its own download cache.</summary>
    public int PhotorealYear { get; set; } = 2016;
    public string TbOutputOrDefault => string.IsNullOrWhiteSpace(TbOutputFolder) ? Path.Combine(ProjectFolder, "Output", "TB_Output") : TbOutputFolder;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));

    /// <summary>
    /// Legge il progetto. Se la cartella del progetto è stata spostata (es. tutti i tool messi in C:\BMS Suite), ProjectFolder
    /// e i percorsi che stavano dentro la vecchia cartella vengono riportati alla cartella dove si trova ora il file .ttproj.
    /// </summary>
    public static TheaterProject Load(string path)
    {
        var p = JsonSerializer.Deserialize<TheaterProject>(File.ReadAllText(path), Opts) ?? new TheaterProject();
        string here = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string old = p.ProjectFolder;
        if (!string.IsNullOrWhiteSpace(old) && !string.Equals(Path.GetFullPath(old).TrimEnd('\\', '/'), here.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            && (!Directory.Exists(old) || Directory.Exists(Path.Combine(here, "Output"))))
        {
            string Rebase(string v) =>
                !string.IsNullOrWhiteSpace(v) && v.StartsWith(old.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(here, v[(old.TrimEnd('\\', '/').Length + 1)..]) : v;
            p.AirbasesCsv = Rebase(p.AirbasesCsv);
            p.TbOutputFolder = Rebase(p.TbOutputFolder);
            p.ProjectFolder = here;
        }
        else if (string.IsNullOrWhiteSpace(old)) p.ProjectFolder = here;
        return p;
    }

    /// <summary>
    /// Percorso di un file di progetto ricordato in un elenco (progetti recenti): se non c'è più, prova lo stesso percorso sotto la
    /// cartella che oggi contiene i tool (la cartella sopra quella del programma con build.bat), dalla parte "TerrainTool\..." in poi.
    /// Serve quando le cartelle TerrainTool, ObjectEditor, CampaignEditor, ModelEditor sono state spostate insieme altrove.
    /// </summary>
    public static string FindMoved(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return path;
            string? suite = SuiteLayout.ProgramsRoot();
            if (suite == null) return path;
            int i = path.IndexOf("\\TerrainTool\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return path;
            string cand = Path.Combine(suite, path[(i + 1)..]);
            return File.Exists(cand) ? cand : path;
        }
        catch (Exception) { return path; }
    }

    /// <summary>
    /// Impedisce di scrivere dentro l'installazione di BMS, nella Korea o nel vecchio teatro.
    /// </summary>
    public bool IsProtectedPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var p in ProtectedRoots())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string root = Path.GetFullPath(p).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private IEnumerable<string> ProtectedRoots()
    {
        yield return OldTheaterFolder;
        yield return TerrainBuilderFolder;
        yield return NewTerrainFolder;
        yield return SourceObjectsDir;
        yield return SourceCampaignDir;
        if (!string.IsNullOrWhiteSpace(BmsDataDir)) yield return Directory.GetParent(Path.GetFullPath(BmsDataDir).TrimEnd('\\', '/'))?.FullName ?? BmsDataDir;
        // Risale dalla Korea alla radice dell'installazione BMS (la cartella sopra "Data")
        if (!string.IsNullOrWhiteSpace(KoreaNewTerrain))
        {
            var d = new DirectoryInfo(KoreaNewTerrain);
            while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
            yield return d?.Parent?.FullName ?? KoreaNewTerrain;
        }
    }

    /// <summary>Scrive Theater.txt nella cartella di output del progetto (mai nell'installazione BMS).</summary>
    public string WriteTheaterTxt()
    {
        string dir = OutputNewTerrain;
        if (IsProtectedPath(dir)) throw new InvalidOperationException("Protected folder (BMS / Korea / old theater): write blocked.");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Theater.txt");
        File.WriteAllText(path, Theater.ToTheaterTxt());
        return path;
    }
}
