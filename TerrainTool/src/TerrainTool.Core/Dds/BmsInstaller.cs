using System.Diagnostics;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Dds;

/// <summary>
/// "Install into BMS": copia nell'installazione BMS solo le parti che cambiano a ogni giro delle basi
/// (Terrdata\ObjectsNT) e le tessere Photoreal\4K nuove o cambiate, e cancella le cache che BMS rigenera all'avvio di una
/// campagna o di un TE (Campaign\*.terrain.binpb/txtpb, NewTerrain\HeightMaps\...): senza, userebbe gli oggetti vecchi.
/// I file della campagna (.cam, .tac, CampObjData.XML, stations+ils.dat ...) li installa solo il Campaign Editor.
/// Scrive SOLO dentro &lt;BMS&gt;\Data\&lt;add-on&gt; (mai nella Korea, in theater.lst o nel resto di BMS).
/// </summary>
public sealed class BmsInstaller
{
    public static readonly string[] HeightMapCaches = { "TerrainLevelingData.bin", "LandCoverData.binpb", "SpecialPolygonData.binpb" };
    /// <summary>Line-of-sight maps BMS writes from the heightmap: stale when the heightmap changes.</summary>
    public static readonly string[] LosCaches = { "LOS250.raw", "LOSMin.raw", "LOSMax.raw" };

    public string DataDir { get; }
    public string Target { get; }        // <BMS>\Data\<add-on>
    public string Source { get; }        // Output\Install\<add-on>
    public string BackupDir { get; }
    public string? NewTerrainRel { get; } // es. Terrdata\Mideast\NewTerrain
    public List<string> Problems { get; } = new();

    private readonly TheaterProject _p;

    /// <summary>Oggetti (+ pulizia delle cache) e/o tessere fotorealistiche.</summary>
    public bool Objects { get; }
    public bool Photoreal { get; }
    /// <summary>Mappe del terreno e dell'acqua (HeightMap, WaterHeightMap, DepthMap, CullingMap, FFTMap, GlobalAlpha) da Output\NewTerrain.</summary>
    public bool Water { get; }
    /// <summary>Art dell'interfaccia (mappa 2D, bandiere, sfondi, patch, Theater.irc) da Output\Art\Art in &lt;add-on&gt;\NT\Art.</summary>
    public bool Art { get; }
    /// <summary>Sim del teatro (copia della 4.38 + SimOverrides) da Output\Install\&lt;add-on&gt;\NT\Sim in &lt;add-on&gt;\NT\Sim,
    /// e riga simdatadir del .tdf _NT installato.</summary>
    public bool Sim { get; }

    /// <summary>File copiati dall'opzione Water (relativi a NewTerrain).</summary>
    public static readonly string[] WaterFiles =
    {
        @"HeightMaps\HeightMap.raw", @"HeightMaps\WaterHeightMap.raw", @"WaterMaps\DepthMap_nsrgb.dds", @"WaterMaps\CullingMap_nsrgb.dds",
        @"WaterMaps\FFTMap.raw", @"GroundTypes\GlobalAlpha_nsrgb.dds",
        // tipi di suolo e alberi: cambiano con l'acqua (acqua aggiunta o tolta a mano, alberi tolti dall'acqua)
        @"GroundTypes\LandCoverMap.raw", @"Trees\Maps\ForestMap.raw",
    };

    public BmsInstaller(TheaterProject p, bool objects = true, bool photoreal = true, bool water = false, bool art = false, bool sim = false)
    {
        _p = p;
        Objects = objects; Photoreal = photoreal; Water = water; Art = art; Sim = sim;
        if (!objects && !photoreal && !water && !art && !sim) Problems.Add("Nothing selected");
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        var d = new DirectoryInfo(p.KoreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        DataDir = d?.FullName ?? "";
        Target = Path.Combine(DataDir, addOn);
        Source = Path.Combine(p.ProjectFolder, "Output", "Install", addOn);
        BackupDir = Path.Combine(p.ProjectFolder, "Output", "BmsBackup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        if (d == null) Problems.Add($"BMS Data folder not found above {p.KoreaNewTerrain}");
        else if (!Directory.Exists(Target)) Problems.Add($"{Target} does not exist (copy the add-on into BMS first)");
        if (objects && !Directory.Exists(Path.Combine(Source, "Terrdata", ObjectsJob.FolderName))) Problems.Add($"{Source}\\Terrdata\\{ObjectsJob.FolderName} missing: run 'Objects' and 'Build theater' first");
        if (objects && Directory.Exists(Target))
        {
            // il database nuovo non deve avere meno obiettivi (voci CT con EntityType 3) di quello in BMS: le campagne usano quelli
            // aggiunti in fondo (copie delle disposizioni di basi e ponti); senza, BMS va in crash caricando gli obiettivi.
            // Si contano solo gli obiettivi: le voci importate da un vecchio add-on (aerei, armi, squadroni) vengono dopo e
            // possono cambiare di numero da un giro all'altro.
            static int Objectives(string f) => File.Exists(f)
                ? System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"<CT Num=""\d+"">(?:(?!</CT>).)*?<EntityType>3</EntityType>",
                    System.Text.RegularExpressions.RegexOptions.Singleline).Count
                : 0;
            int now = Objectives(Path.Combine(ObjectsTarget, "Falcon4_CT.xml")), nw = Objectives(Path.Combine(Source, "Terrdata", ObjectsJob.FolderName, "Falcon4_CT.xml"));
            if (nw > 0 && nw < now)
                Problems.Add($"The new Falcon4_CT.xml has {nw} objective records, the one in BMS {now}: the campaign files use the missing ones (layout copies of bases/bridges) and BMS would crash. " +
                             "Usually 'Run Objects' ran without BMS's campaign terrain cache: start a campaign of the theater once in BMS, then Run Objects again.");
        }
        if (art && !Directory.Exists(ArtSource)) Problems.Add($"{ArtSource} missing: run '2D map (campmap)' and/or 'Theater art' first");
        if (sim && !Directory.Exists(SimSource)) Problems.Add($"{SimSource} missing: create the project folder SimOverrides and run 'Build theater' first");
        if (sim && Directory.Exists(Target) && InstalledTdf() == null) Problems.Add($"No *_NT.tdf in {Target}\\Terrdata\\theaterdefinition: install the theater first (INSTALL.txt)");
        if (IsBmsRunning()) Problems.Add("Falcon BMS is running: close it first");

        if (Directory.Exists(Source))
        {
            var nt = Directory.EnumerateDirectories(Source, "NewTerrain", SearchOption.AllDirectories).FirstOrDefault();
            if (nt != null) NewTerrainRel = Path.GetRelativePath(Source, nt);
        }
        if (NewTerrainRel == null && Directory.Exists(Target))
        {
            var nt = Directory.EnumerateDirectories(Path.Combine(Target, "Terrdata"), "NewTerrain", SearchOption.AllDirectories).FirstOrDefault();
            if (nt != null) NewTerrainRel = Path.GetRelativePath(Target, nt);
        }
    }

    private string? NewTerrainTarget => NewTerrainRel == null ? null : Path.Combine(Target, NewTerrainRel);

    /// <summary>File delle mappe del terreno/acqua diversi da quelli in BMS (dimensione o data).</summary>
    private List<string> ChangedWaterFiles()
    {
        var res = new List<string>();
        if (NewTerrainTarget == null) return res;
        foreach (var rel in WaterFiles)
        {
            var s = new FileInfo(Path.Combine(_p.OutputNewTerrain, rel));
            if (!s.Exists) continue;
            var t = new FileInfo(Path.Combine(NewTerrainTarget, rel));
            if (!t.Exists || t.Length != s.Length || t.LastWriteTimeUtc != s.LastWriteTimeUtc) res.Add(rel);
        }
        return res;
    }

    // Tessere fotorealistiche: dalla cartella NewTerrain del progetto, solo quelle nuove o cambiate
    private string PhotoSource => Path.Combine(_p.OutputNewTerrain, "Photoreal", "4K");
    private string? PhotoTarget => NewTerrainRel == null ? null : Path.Combine(Target, NewTerrainRel, "Photoreal", "4K");

    private List<string> ChangedPhotoTiles()
    {
        var res = new List<string>();
        if (PhotoTarget == null || !Directory.Exists(PhotoSource)) return res;
        foreach (var f in Directory.EnumerateFiles(PhotoSource, "*.dds"))
        {
            var t = new FileInfo(Path.Combine(PhotoTarget, Path.GetFileName(f)));
            var s = new FileInfo(f);
            if (!t.Exists || t.Length != s.Length || s.LastWriteTimeUtc > t.LastWriteTimeUtc) res.Add(f);
        }
        return res;
    }

    // Art dell'interfaccia: Output\Art\Art del progetto → <add-on>\NT\Art (la cartella artdir che scrive Assemble)
    private string ArtSource => Path.Combine(_p.ProjectFolder, "Output", "Art", "Art");
    private string ArtTarget => Path.Combine(Target, "NT", "Art");

    /// <summary>File dell'Art diversi da quelli in BMS (dimensione o contenuto), relativi a Output\Art\Art.</summary>
    private List<string> ChangedArtFiles()
    {
        var res = new List<string>();
        if (!Directory.Exists(ArtSource)) return res;
        foreach (var f in Directory.EnumerateFiles(ArtSource, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(ArtSource, f);
            var t = new FileInfo(Path.Combine(ArtTarget, rel));
            if (!t.Exists || t.Length != new FileInfo(f).Length || !SameContent(f, t.FullName)) res.Add(rel);
        }
        return res;
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        while (true)
        {
            int na = fa.Read(ba, 0, ba.Length), nb = fb.Read(bb, 0, bb.Length);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    // Sim del teatro
    private string SimSource => Path.Combine(Source, TheaterSimData.SimRel);
    private string SimTarget => Path.Combine(Target, TheaterSimData.SimRel);
    private string SimDataDirValue => $@"{new DirectoryInfo(Target).Name}\{TheaterSimData.SimRel}";

    /// <summary>Il .tdf scritto da Assemble (nome *_NT.tdf) nella cartella theaterdefinition dell'add-on in BMS.</summary>
    private string? InstalledTdf()
    {
        var dir = Directory.Exists(Target) ? Directory.EnumerateDirectories(Target, "theaterdefinition", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true, MaxRecursionDepth = 2 }).FirstOrDefault() : null;
        if (dir == null) return null;
        string? want = Directory.Exists(Source) ? Directory.EnumerateFiles(Source, "*_NT.tdf", SearchOption.AllDirectories).Select(Path.GetFileName).FirstOrDefault() : null;
        return Directory.EnumerateFiles(dir, "*.tdf").FirstOrDefault(f => want != null ? Path.GetFileName(f).Equals(want, StringComparison.OrdinalIgnoreCase) : Path.GetFileNameWithoutExtension(f).EndsWith("_NT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>File della Sim da copiare (nuovi o diversi) e file in BMS che non ci sono piu' nella copia del progetto.</summary>
    private (List<string> Changed, List<string> Extra) SimDiff()
    {
        var ch = new List<string>(); var ex = new List<string>();
        if (!Directory.Exists(SimSource)) return (ch, ex);
        var src = Directory.EnumerateFiles(SimSource, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(SimSource, f)).ToList();
        foreach (var rel in src)
        {
            var t = new FileInfo(Path.Combine(SimTarget, rel));
            if (!t.Exists || t.Length != new FileInfo(Path.Combine(SimSource, rel)).Length || !SameContent(Path.Combine(SimSource, rel), t.FullName)) ch.Add(rel);
        }
        if (Directory.Exists(SimTarget))
        {
            var set = new HashSet<string>(src, StringComparer.OrdinalIgnoreCase);
            ex.AddRange(Directory.EnumerateFiles(SimTarget, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(SimTarget, f)).Where(r => !set.Contains(r)));
        }
        return (ch, ex);
    }

    public static bool IsBmsRunning() =>
        Process.GetProcesses().Any(pr => { try { return pr.ProcessName.StartsWith("Falcon BMS", StringComparison.OrdinalIgnoreCase); } catch { return false; } });

    private string ObjectsTarget => Path.Combine(Target, "Terrdata", ObjectsJob.FolderName);
    private string CampaignTarget => Path.Combine(Target, "Campaign");

    private IEnumerable<string> TerrainCaches() =>
        Directory.Exists(CampaignTarget)
            ? Directory.EnumerateFiles(CampaignTarget, "*.terrain.binpb").Concat(Directory.EnumerateFiles(CampaignTarget, "*.terrain.txtpb"))
            : Enumerable.Empty<string>();

    private IEnumerable<string> HeightCaches() =>
        NewTerrainRel == null ? Enumerable.Empty<string>()
            : HeightMapCaches.Select(f => Path.Combine(Target, NewTerrainRel, "HeightMaps", f)).Where(File.Exists);

    /// <summary>
    /// File di ObjectsNT che non entrano nelle cache del terreno: armi, mezzi, unita', radar, sensori, rack. Se cambiano solo
    /// questi (es. una gittata da DbEdits.txt) le cache Campaign\*.terrain.binpb e HeightMaps restano valide e non si
    /// cancellano, cosi' BMS non le ricostruisce all'avvio. Tutto il resto (CT, obiettivi, posti, livellamento) le cancella.
    /// </summary>
    public static readonly HashSet<string> NoTerrainFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Falcon4_WCD.xml", "Falcon4_VCD.xml", "Falcon4_UCD.xml", "Falcon4_RCD.xml", "Falcon4_ICD.xml", "Falcon4_SWD.xml",
        "Falcon4_ACD.xml", "Falcon4_RKT.xml", "Falcon4_RWD.xml", "Falcon4_SSD.xml", "Falcon4_DDP.xml", "Falcon4_VSD.xml",
        "Falcon4_WLD.xml", "BmsRack.dat", "Rack.dat",
    };

    /// <summary>File di ObjectsNT diversi tra Output\Install e BMS (i FALCON4.* che BMS si genera da solo non contano).</summary>
    private List<string> ChangedObjectFiles()
    {
        string src = Path.Combine(Source, "Terrdata", ObjectsJob.FolderName);
        var res = new List<string>();
        if (!Directory.Exists(src)) return res;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f); seen.Add(rel);
            string to = Path.Combine(ObjectsTarget, rel);
            if (!File.Exists(to) || !SameContent(f, to)) res.Add(rel);
        }
        if (Directory.Exists(ObjectsTarget))
            foreach (var f in Directory.EnumerateFiles(ObjectsTarget, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(ObjectsTarget, f);
                if (seen.Contains(rel) || BmsGenerated(rel)) continue;
                res.Add(rel);
            }
        return res;
    }

    /// <summary>File che BMS scrive da solo in ObjectsNT: FALCON4.* (database binario) e TerrainRelatedData\SPD_*.SPB.</summary>
    private static bool BmsGenerated(string rel)
    {
        string name = Path.GetFileName(rel), dir = Path.GetDirectoryName(rel) ?? "";
        return (dir.Length == 0 && name.StartsWith("FALCON4.", StringComparison.Ordinal))
            || (dir.Equals("TerrainRelatedData", StringComparison.OrdinalIgnoreCase) && name.StartsWith("SPD_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".SPB", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>true se gli oggetti nuovi cambiano solo armi/mezzi/radar: le cache del terreno restano.</summary>
    private bool ObjectsKeepTerrainCaches(List<string> changed) => changed.All(r =>
        NoTerrainFiles.Contains(r)
        // the suite's own reports (Airbases_leveling.csv…): BMS does not read them
        || (Path.GetDirectoryName(r) is "" && r.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        // the CT with the same pieces and objectives: only aircraft, vehicles, weapons or units changed (e.g. an import)
        || (r.Equals("Falcon4_CT.xml", StringComparison.OrdinalIgnoreCase) && SameTerrainCt()));

    private bool? _sameTerrainCt;

    /// <summary>True when the new CT and BMS's have the same records of pieces (EntityType 1) and objectives (3).</summary>
    private bool SameTerrainCt()
    {
        if (_sameTerrainCt is bool b) return b;
        static string Terrain(string f)
        {
            var sb = new System.Text.StringBuilder();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"<CT Num=""\d+"">.*?</CT>",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
                if (m.Value.Contains("<EntityType>1</EntityType>") || m.Value.Contains("<EntityType>3</EntityType>")) sb.Append(m.Value).Append('\n');
            return sb.ToString();
        }
        string a = Path.Combine(Source, "Terrdata", ObjectsJob.FolderName, "Falcon4_CT.xml"), t = Path.Combine(ObjectsTarget, "Falcon4_CT.xml");
        try { _sameTerrainCt = File.Exists(a) && File.Exists(t) && Terrain(a) == Terrain(t); }
        catch (IOException) { _sameTerrainCt = false; }
        return _sameTerrainCt.Value;
    }

    /// <summary>Testo per la finestra di conferma.</summary>
    private List<string> _photo = new();

    public string Plan()
    {
        _photo = Photoreal ? ChangedPhotoTiles() : new List<string>();
        var lines = new List<string> { $"Target: {Target}", "" };
        if (Objects)
        {
            var ch = ChangedObjectFiles();
            if (ObjectsKeepTerrainCaches(ch))
                lines.Add($"1. Copy the changed files into {ObjectsTarget} (BMS rebuilds its FALCON4.* from them)");
            else
            {
                lines.Add($"1. Delete {ObjectsTarget} (also removes the SPD_*.SPB that BMS never rebuilds)");
                lines.Add("   and copy the new one from Output\\Install");
            }
            lines.Add($"   Changed files: {string.Join(", ", ch.Take(12).DefaultIfEmpty("none"))}{(ch.Count > 12 ? $" … and {ch.Count - 12} more" : "")}");
            if (ObjectsKeepTerrainCaches(ch))
                lines.Add("2. Only aircraft / weapon / vehicle / unit / radar data changed (pieces and objectives the same): the terrain caches and SPD_*.SPB are KEPT (no long reload in BMS)");
            else
                lines.Add($"2. Delete the caches BMS rebuilds when a campaign or TE starts: {string.Join(", ", TerrainCaches().Concat(HeightCaches()).Select(Path.GetFileName).DefaultIfEmpty("(none present)"))}");
            lines.Add("   Campaign files (.cam, .tac, CampObjData.XML, stations+ils.dat …) are not copied: install them from the Campaign Editor.");
        }
        else lines.Add(Water ? "Objects: not selected" : "Objects: not selected (caches are kept)");
        if (Photoreal)
            lines.Add(PhotoTarget == null ? "3. Photoreal 4K: NewTerrain folder not found in BMS, skipped"
                : $"3. Copy {_photo.Count} new or changed Photoreal 4K tiles ({_photo.Sum(f => new FileInfo(f).Length) / 1073741824.0:0.0} GB) → {PhotoTarget}");
        else lines.Add("Photoreal tiles: not selected");
        if (Water)
        {
            var w = ChangedWaterFiles();
            if (NewTerrainTarget == null) lines.Add("Terrain & water maps: NewTerrain folder not found in BMS, skipped");
            else
            {
                lines.Add($"4. Copy {w.Count} terrain/water maps ({w.Sum(r => new FileInfo(Path.Combine(_p.OutputNewTerrain, r)).Length) / 1073741824.0:0.0} GB) → {NewTerrainTarget}:");
                foreach (var r in w) lines.Add("     " + r);
                lines.Add($"   The files replaced in BMS are saved first in {BackupDir} (copy them back to undo).");
                lines.Add($"   Then delete the caches BMS rebuilds: {string.Join(", ", TerrainCaches().Concat(HeightCaches()).Select(Path.GetFileName).DefaultIfEmpty("(none present)"))}");
            }
        }
        if (Art)
        {
            var a = ChangedArtFiles();
            lines.Add($"5. Copy {a.Count} new or changed UI art files ({a.Sum(r => new FileInfo(Path.Combine(ArtSource, r)).Length) / 1048576.0:0} MB) → {ArtTarget}:");
            foreach (var r in a.Take(30)) lines.Add("     " + r);
            if (a.Count > 30) lines.Add($"     … and {a.Count - 30} more");
            lines.Add($"   The files replaced in BMS are saved first in {BackupDir} (copy them back to undo).");
            lines.Add("   The theater's .tdf must have 'artdir <add-on>\\NT' (Build theater writes it).");
        }
        if (Sim)
        {
            var (ch, ex) = SimDiff();
            lines.Add($"6. Theater Sim: copy {ch.Count} new or changed files → {SimTarget}" + (ex.Count > 0 ? $", remove {ex.Count} files no longer in the project copy" : ""));
            foreach (var r in ch.Take(20)) lines.Add("     " + r);
            if (ch.Count > 20) lines.Add($"     … and {ch.Count - 20} more");
            string? tdf = InstalledTdf();
            lines.Add($"   Set 'simdatadir {SimDataDirValue}' in {tdf}");
            lines.Add($"   The replaced or removed files and the .tdf are saved first in {BackupDir} (copy them back to undo).");
            lines.Add("   The BMS Data\\Sim folder (used by every other theater) is NOT touched.");
        }
        lines.Add("");
        lines.Add(Sim ? "Nothing else in BMS is touched (no Korea, no theater.lst, no Data\\Sim; only the simdatadir line of the _NT.tdf)."
                      : "Nothing else in BMS is touched (no Korea, no theater.lst, no .tdf).");
        if (Objects && File.Exists(Path.Combine(ObjectsTarget, Database.UnitsConversion.PicksFile)))
            lines.Add("The aircraft and vehicles converted into this theater with the 4.37 Units Converter are converted again after the new database is installed (its own backup first).");
        if (Objects) lines.Add("If Objects moved bases, install the campaign files too (Campaign Editor) and create a NEW TE scenario.");
        return string.Join("\n", lines);
    }

    public void Run(Action<string> log)
    {
        if (Problems.Count > 0) throw new InvalidOperationException(string.Join("\n", Problems));
        if (Objects) RunObjects(log);
        if (Objects)
        {
            // the 4.37 Units Converter's conversions (the theater's picks file, kept from the database just replaced)
            try
            {
                foreach (var l in Database.UnitsConversion.Reapply(DataDir, ObjectsTarget, Path.Combine(_p.ProjectFolder, "Output", "UnitsConverter"),
                                                                       Path.Combine(_p.ProjectFolder, "Output", "BmsBackup", "UnitsConverter"))) log("  " + l);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or System.Xml.XmlException)
            { log("  4.37 Units Converter: the converted aircraft and vehicles were NOT converted again: " + ex.Message + " (open the Units Converter and convert them again)"); }
        }
        if (Photoreal) RunPhotoreal(log);
        if (Water) RunWater(log);
        if (Art) RunArt(log);
        if (Sim) RunSim(log);
        log("  Done." + (Objects ? " Install the campaign files from the Campaign Editor if Objects changed them, then start BMS." : ""));
    }

    private void RunObjects(Action<string> log)
    {
        // 1) ObjectsNT (prima si guarda cosa cambia: serve per decidere sulle cache)
        var changed = ChangedObjectFiles();
        bool keepCaches = ObjectsKeepTerrainCaches(changed);
        log($"  Changed object files: {string.Join(", ", changed.DefaultIfEmpty("none"))}");
        if (keepCaches)
        {
            // solo armi/mezzi/radar: si copiano i file cambiati e si tengono gli SPD_*.SPB e le cache del terreno;
            // via solo i FALCON4.* (database binario che BMS rifa' dagli XML)
            string src = Path.Combine(Source, "Terrdata", ObjectsJob.FolderName);
            foreach (var rel in changed)
            {
                string to = Path.Combine(ObjectsTarget, rel);
                Check(to);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(src, rel), to, true);
                log($"  Copied {rel}");
            }
            if (Directory.Exists(ObjectsTarget))
                foreach (var f in Directory.EnumerateFiles(ObjectsTarget, "FALCON4.*").Where(f => Path.GetFileName(f).StartsWith("FALCON4.", StringComparison.Ordinal)).ToList())
                { Delete(f); log($"  Deleted {f}"); }
            log("  Only weapon / vehicle / unit / radar data changed: terrain caches and SPD_*.SPB kept");
            return;
        }
        // the 4.37 Units Converter's lists (what was converted into the theater) stay: Run converts it again from them
        var converter = Directory.Exists(ObjectsTarget)
            ? Directory.EnumerateFiles(ObjectsTarget, "UnitsConverter*.csv").ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes)
            : new Dictionary<string, byte[]>();
        if (Directory.Exists(ObjectsTarget)) { Delete(ObjectsTarget); log($"  Deleted {ObjectsTarget}"); }
        int no = CopyTree(Path.Combine(Source, "Terrdata", ObjectsJob.FolderName), ObjectsTarget);
        log($"  Copied {no} files → {ObjectsTarget}");
        foreach (var (name, bytes) in converter) File.WriteAllBytes(Path.Combine(ObjectsTarget, name), bytes);

        // 2) cache che BMS ricostruisce all'avvio di una campagna o di un TE (con gli oggetti nuovi)
        KeepTerrainCache(log);
        foreach (var f in TerrainCaches().Concat(HeightCaches()).ToList()) { Delete(f); log($"  Deleted {f}"); }
    }

    private void RunWater(Action<string> log)
    {
        if (NewTerrainTarget == null) { log("  Terrain & water maps: NewTerrain folder not found in BMS, skipped"); return; }
        var list = ChangedWaterFiles();
        foreach (var rel in list)
        {
            string from = Path.Combine(_p.OutputNewTerrain, rel), to = Path.Combine(NewTerrainTarget, rel);
            Check(to);
            if (File.Exists(to))
            {
                string bak = Path.Combine(BackupDir, NewTerrainRel!, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Copy(to, bak, true);
                log($"  Backup {rel} → {bak}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            log($"  Copied {rel} ({new FileInfo(from).Length / 1048576.0:0} MB)");
        }
        if (list.Count == 0) log("  Terrain & water maps: already up to date in BMS");
        else { KeepTerrainCache(log); foreach (var f in TerrainCaches().Concat(HeightCaches()).ToList()) { Delete(f); log($"  Deleted cache {f}"); } }
    }

    private void RunArt(Action<string> log)
    {
        var list = ChangedArtFiles();
        foreach (var rel in list)
        {
            string from = Path.Combine(ArtSource, rel), to = Path.Combine(ArtTarget, rel);
            Check(to);
            if (File.Exists(to))
            {
                string bak = Path.Combine(BackupDir, "NT", "Art", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Copy(to, bak, true);
                // file nuovo, non scritto dentro un eventuale collegamento fisico (hard link) ai file del progetto
                File.SetAttributes(to, FileAttributes.Normal);
                File.Delete(to);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, false);
            log($"  Copied Art\\{rel} ({new FileInfo(from).Length / 1048576.0:0.0} MB)");
        }
        log(list.Count == 0 ? "  UI art: already up to date in BMS" : $"  UI art: {list.Count} files → {ArtTarget}" + (Directory.Exists(BackupDir) ? $" (replaced files saved in {BackupDir})" : ""));
    }

    /// <summary>Copia nel progetto la cache del terreno della campagna (*.terrain.binpb) prima di cancellarla: serve al lavoro
    /// Objects (celle dei ponti) finche' BMS non la rifa'.</summary>
    private void KeepTerrainCache(Action<string> log)
    {
        string keep = ObjectsJob.KeptTerrainCacheFolder(_p);
        foreach (var f in TerrainCaches().Where(f => f.EndsWith(".binpb", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(keep);
            string to = Path.Combine(keep, Path.GetFileName(f));
            File.Copy(f, to, true);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(f));
            log($"  Kept a copy of {Path.GetFileName(f)} in {keep} (for 'Run Objects' until BMS rebuilds it)");
        }
    }

    private void RunSim(Action<string> log)
    {
        var (ch, ex) = SimDiff();
        foreach (var rel in ch.Concat(ex))
        {
            string to = Path.Combine(SimTarget, rel);
            Check(to);
            if (!File.Exists(to)) continue;
            string bak = Path.Combine(BackupDir, TheaterSimData.SimRel, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
            File.Copy(to, bak, true);
            File.SetAttributes(to, FileAttributes.Normal);
            File.Delete(to);
        }
        foreach (var rel in ch)
        {
            string to = Path.Combine(SimTarget, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(SimSource, rel), to, false);
        }
        log(ch.Count + ex.Count == 0 ? "  Theater Sim: already up to date in BMS" : $"  Theater Sim: {ch.Count} files copied, {ex.Count} removed → {SimTarget}");
        foreach (var r in ch.Where(r => File.Exists(Path.Combine(TheaterSimData.SimOverridesFolder(_p), r)))) log($"    (from SimOverrides) {r}");

        string tdf = InstalledTdf()!;
        Check(tdf);
        string? txt = TheaterSimData.SetSimDataDir(File.ReadAllText(tdf), SimDataDirValue);
        if (txt == null) { log($"  {Path.GetFileName(tdf)}: simdatadir already {SimDataDirValue}"); return; }
        string tb = Path.Combine(BackupDir, Path.GetFileName(tdf));
        Directory.CreateDirectory(BackupDir);
        File.Copy(tdf, tb, true);
        File.WriteAllText(tdf, txt, new System.Text.UTF8Encoding(false));
        log($"  {Path.GetFileName(tdf)}: simdatadir {SimDataDirValue} (old file saved in {tb})");
    }

    private void RunPhotoreal(Action<string> log)
    {
        // 6) tessere fotorealistiche nuove o cambiate
        if (PhotoTarget != null)
        {
            var list = ChangedPhotoTiles();
            int k = 0;
            foreach (var f in list)
            {
                string to = Path.Combine(PhotoTarget, Path.GetFileName(f));
                Check(to);
                Directory.CreateDirectory(PhotoTarget);
                File.Copy(f, to, true);
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(f));
                if (++k % 50 == 0) log($"  Photoreal 4K: {k}/{list.Count}");
            }
            log($"  Photoreal 4K: {list.Count} tiles copied → {PhotoTarget}");
        }
    }

    // Ogni scrittura passa da qui: deve stare dentro <BMS>\Data\<add-on>, mai nella Korea.
    private void Check(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetFullPath(Target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string korea = Path.GetFullPath(_p.KoreaNewTerrain).TrimEnd('\\', '/');
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(korea, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Write outside {Target} blocked: {full}");
    }

    private void Delete(string path)
    {
        Check(path);
        if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
    }

    private int CopyTree(string src, string dst)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Check(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true); n++;
        }
        return n;
    }
}
