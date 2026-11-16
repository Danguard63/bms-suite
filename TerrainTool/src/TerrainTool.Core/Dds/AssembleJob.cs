using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Monta il teatro di prova come "sovrapposizione" del vecchio add-on (stessa struttura di Add-On Israel):
///   Output\Install\&lt;Add-On&gt;\Terrdata\&lt;teatro&gt;\NewTerrain\...   (nostri file + file generici copiati dalla Korea)
///   Output\Install\&lt;Add-On&gt;\Terrdata\theaterdefinition\&lt;nome&gt;_NT.tdf
/// Non scrive mai in BMS: l'installazione la fa l'utente seguendo INSTALL.txt.
/// </summary>
public sealed class AssembleJob : TextureJob
{
    public override string Id => "assemble";
    public override string Name => "Build theater";
    public override string Description => "Builds Output\\Install: the NewTerrain folder (our files + generic art copied read-only from Korea), Theater.txt and a new .tdf that reuses the old add-on's campaign and objects. Tab 9 installs it into BMS (or follow INSTALL.txt).";
    public override string Output => @"..\Install";

    /// <summary>File e cartelle generici (non legati alla geografia) presi dalla Korea.</summary>
    private static readonly string[] KoreaGeneric =
    {
        "Foam.dds", "TerrainFractalNoise_nsrgb.dds", @"ArtTextures", @"Trees\Art",
        @"GroundTypes\Types.txt", @"WaterMaps\UnderWaterColor.dds",
    };

    /// <summary>File nostri obbligatori per partire.</summary>
    private static readonly string[] Required =
    {
        @"HeightMaps\HeightMap.raw", @"HeightMaps\WaterHeightMap.raw", @"WaterMaps\DepthMap_nsrgb.dds", @"WaterMaps\CullingMap_nsrgb.dds",
        @"WaterMaps\FFTMap.raw", @"WaterMaps\ColorMap.dds", @"GroundTypes\GlobalAlpha_nsrgb.dds", @"GroundTypes\LandCoverMap.raw",
        @"GroundTypes\LandCoverTypes.txt", @"Photoreal\GlobalColorMap.dds", @"Photoreal\4K",
    };

    public override IReadOnlyList<JobInput> Inputs(TextureContext c)
    {
        var list = new List<JobInput>();
        int missing = Required.Count(r => !File.Exists(Path.Combine(c.NewTerrain, r)) && !Directory.Exists(Path.Combine(c.NewTerrain, r)));
        list.Add(new JobInput(missing == 0 ? "Our NewTerrain files (all present)" : $"Our NewTerrain files ({missing} missing: run the jobs above)",
                              missing == 0 ? c.NewTerrain : null, 0, true));
        list.Add(new JobInput("Korea NewTerrain (generic art, read-only)", Directory.Exists(c.Project.KoreaNewTerrain) ? c.Project.KoreaNewTerrain : null, 0, true));
        list.Add(new JobInput("Old theater .tdf", OldTdf(c), 0, true));
        return list;
    }

    /// <summary>Nome del teatro in BMS: quello del progetto (DisplayName), altrimenti il nome della .tdf del vecchio teatro, altrimenti il nome del progetto.</summary>
    public static string TheaterDisplayName(TextureContext c, IReadOnlyDictionary<string, string>? oldTdf = null)
    {
        if (!string.IsNullOrWhiteSpace(c.Project.Theater.DisplayName)) return c.Project.Theater.DisplayName.Trim();
        if (oldTdf == null && OldTdf(c) is { } f) oldTdf = ParseTdf(File.ReadAllLines(f));
        if (oldTdf != null && oldTdf.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n)) return n.Trim();
        return c.Project.Theater.Name;
    }

    private static string? OldTdf(TextureContext c)
    {
        string dir = Path.Combine(c.Project.OldTheaterFolder, "TerrData", "TheaterDefinition");
        if (!Directory.Exists(dir)) return null;
        // il .tdf "base" (non le varianti con il trattino, es. -fl)
        return Directory.EnumerateFiles(dir, "*.tdf").OrderBy(f => Path.GetFileName(f).Contains('-') ? 1 : 0).ThenBy(f => f.Length).FirstOrDefault();
    }

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var notes = new List<string>();
        string tdfPath = OldTdf(c)!;
        var tdf = ParseTdf(File.ReadAllLines(tdfPath));
        string addOn = new DirectoryInfo(c.Project.OldTheaterFolder).Name;          // es. "Add-On Mideast128"
        string terrDir = tdf.GetValueOrDefault("terraindir") ?? $@"{addOn}\Terrdata\{c.Project.Theater.Name}";
        // percorso relativo alla cartella Data → relativo alla radice dell'add-on
        string relTerr = terrDir.StartsWith(addOn + "\\", StringComparison.OrdinalIgnoreCase) ? terrDir[(addOn.Length + 1)..] : terrDir;

        string install = Path.Combine(c.Project.ProjectFolder, "Output", "Install");
        string root = Path.Combine(install, addOn);
        string nt = Path.Combine(root, relTerr, "NewTerrain");
        if (c.Project.IsProtectedPath(install)) throw new InvalidOperationException("Install folder is protected.");
        Directory.CreateDirectory(nt);

        // 1) i nostri file
        var ours = Directory.EnumerateFiles(c.NewTerrain, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp")).ToList();
        // 2) i file generici della Korea
        var generic = new List<(string Src, string Rel)>();
        foreach (var g in KoreaGeneric)
        {
            string src = Path.Combine(c.Project.KoreaNewTerrain, g);
            if (File.Exists(src)) generic.Add((src, g));
            else if (Directory.Exists(src))
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    generic.Add((f, Path.Combine(g, Path.GetRelativePath(src, f))));
            else notes.Add("not found in Korea: " + g);
        }
        long total = ours.Sum(f => new FileInfo(f).Length) + generic.Sum(g => new FileInfo(g.Src).Length), done = 0;
        int copied = 0, skipped = 0, linked = 0;
        // I nostri file (molti GB) diventano hard link quando possibile: nessuno spazio in più e nessuna attesa.
        // I file della Korea si copiano sempre, così niente in Output è collegato all'installazione di BMS.
        void Copy(string src, string dst, bool allowLink)
        {
            ct.ThrowIfCancellationRequested();
            var si = new FileInfo(src);
            var di = new FileInfo(dst);
            if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) skipped++;
            else
            {
                Directory.CreateDirectory(di.DirectoryName!);
                if (di.Exists) File.Delete(dst);
                if (allowLink && OperatingSystem.IsWindows() && CreateHardLink(dst, src, IntPtr.Zero)) linked++;
                else
                {
                    File.Copy(src, dst, true);
                    File.SetLastWriteTimeUtc(dst, si.LastWriteTimeUtc);
                    copied++;
                }
            }
            done += si.Length;
            p.Report(0.98 * done / Math.Max(1, total));
        }
        // tessere photoreal non più prodotte (es. dopo un cambio di griglia): via dalla copia
        string pr = Path.Combine(nt, "Photoreal", "4K");
        if (Directory.Exists(pr))
            foreach (var f in Directory.EnumerateFiles(pr, "*.dds"))
                if (!File.Exists(Path.Combine(c.NewTerrain, "Photoreal", "4K", Path.GetFileName(f)))) File.Delete(f);
        foreach (var f in ours) Copy(f, Path.Combine(nt, Path.GetRelativePath(c.NewTerrain, f)), true);
        foreach (var g in generic)
        {
            string dst = Path.Combine(nt, g.Rel);
            if (File.Exists(Path.Combine(c.NewTerrain, g.Rel))) continue; // un nostro file ha la precedenza
            Copy(g.Src, dst, false);
        }
        // 3) Theater.txt
        File.WriteAllText(Path.Combine(nt, "Theater.txt"), c.Project.Theater.ToTheaterTxt());

        // 4) nuovo .tdf: stesse cartelle del vecchio add-on, nome diverso
        string tdfName = Path.GetFileNameWithoutExtension(tdfPath) + "_NT";
        string defDir = Path.Combine(root, "Terrdata", "theaterdefinition");
        Directory.CreateDirectory(defDir);
        // la mappa 2D (campmap) va in <add-on>\NT\Art\resource: artdir punta lì, il resto dell'interfaccia resta quello standard
        string artSrc = CampMapJob.ArtFolder(c);
        bool hasArt = File.Exists(Path.Combine(artSrc, "Art", "resource", "campmap.rsc")) || File.Exists(Path.Combine(artSrc, "Art", "_FHD", "Main.idx"));
        if (hasArt)
            foreach (var f in Directory.EnumerateFiles(artSrc, "*", SearchOption.AllDirectories))
                Copy(f, Path.Combine(root, "NT", Path.GetRelativePath(artSrc, f)), true);
        // 5) correzioni alla campagna del vecchio add-on (copie nella cartella Install, mai in BMS):
        //    - la 4.38 con il New Terrain apre Te_New_Nt.tac per un TE nuovo; senza, il TE si blocca
        //      ("Cannot open a file we just decided existed")
        //    - le liste del TE Builder (aerei, unita', navi) della 4.36 hanno tipi che nella 4.38 non esistono
        //      o hanno cambiato codice ("Type conflict while creating unit"): si usano quelle della 4.38
        string oldCamp = Path.Combine(c.Project.OldTheaterFolder, "Campaign");
        string newCamp = Path.Combine(root, "Campaign");
        if (Directory.Exists(oldCamp))
        {
            foreach (var (from, to) in new[] { ("te_new.tac", "Te_New_Nt.tac"), ("Te_New.l16.txtpb", "Te_New_Nt.l16.txtpb") })
            {
                string src = Path.Combine(oldCamp, from);
                // il lavoro Objects scrive un Te_New_Nt.tac con le basi spostate: non va sovrascritto
                if (File.Exists(Path.Combine(newCamp, "relocated.txt")) && File.Exists(Path.Combine(newCamp, to))) continue;
                if (File.Exists(src) && !File.Exists(Path.Combine(oldCamp, to)))
                {
                    // il .tac con i file interni rinominati (Te_New_Nt.cmp ...): BMS li cerca con il nome dell'archivio
                    if (to.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)) CampaignArchive.CopyAs(src, Path.Combine(newCamp, to));
                    else Copy(src, Path.Combine(newCamp, to), false);
                    notes.Add($@"Campaign\{to} ← {from} (new TE template for New Terrain)");
                }
            }
            // il modello dei TE nel formato 110 della 4.38: BMS risalva un TE nel formato del file da cui parte, e un TE 108
            // con i voli gia' nel formato nuovo non mostra le rotte quando si apre per volarlo
            foreach (var t in new[] { "Te_New_Nt.tac", "te_new.tac" })
            {
                string tp = Path.Combine(newCamp, t);
                if (!File.Exists(tp)) continue;
                try { if (Campaign.CampaignAlign.UpgradeTo110(tp, tp, _ => null) is { } up) notes.Add($@"Campaign\{t}: upgraded to the 4.38 format (110): {string.Join(", ", up)}"); }
                catch (Exception ex) { notes.Add($@"Campaign\{t}: not upgraded to 110 ({ex.Message})"); }
            }
            string? dataDir = FindDataDir(c.Project.KoreaNewTerrain);
            if (dataDir != null)
                foreach (var lst in new[] { "TePlanes.lst", "TeUnits.lst", "TeNavalUnits.lst" })
                {
                    string src = Path.Combine(dataDir, "Campaign", lst);
                    if (!File.Exists(src)) continue;
                    // stesso nome (anche con maiuscole diverse) del file del vecchio add-on, così la copia lo sostituisce
                    string name = Directory.EnumerateFiles(oldCamp, lst).Select(Path.GetFileName).FirstOrDefault() ?? lst;
                    Copy(src, Path.Combine(newCamp, name!), false);
                }
            notes.Add(@"Campaign: TE Builder lists (TePlanes, TeUnits, TeNavalUnits) replaced with the BMS 4.38 ones");
            if (dataDir != null)
            {
                // file uguali per ogni teatro, presi dalla Korea 4.38 (stesso nome del vecchio add-on se c'e')
                string kc = Path.Combine(dataDir, "Campaign");
                string NameIn(string dir, string n) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).FirstOrDefault(x => x!.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? n : n;
                string? KFile(string n) => Directory.Exists(kc) ? Directory.EnumerateFiles(kc).FirstOrDefault(x => Path.GetFileName(x).Equals(n, StringComparison.OrdinalIgnoreCase)) : null;
                var took = new List<string>();
                foreach (var n in Campaign.CampaignAlign.FromKorea)
                    if (KFile(n) is { } src) { Copy(src, Path.Combine(newCamp, NameIn(oldCamp, n)), false); took.Add(n); }
                if (took.Count > 0) notes.Add(@"Campaign: from the Korea 4.38: " + string.Join(", ", took) + " (debriefing layout, ECM defaults)");
                // Ppt.ini: parte comune della Korea 4.38 + nomi locali del vecchio add-on
                string? kp = KFile("Ppt.ini"); string op = Path.Combine(oldCamp, NameIn(oldCamp, "Ppt.ini"));
                if (kp != null && File.Exists(op) && Campaign.CampaignAlign.MergePpt(kp, op, out int loc) is { } ppt)
                {
                    // raggi delle minacce corretti dal progetto (PptEdits.txt), come le gittate dei SAM in DbEdits.txt
                    var pptNotes = new List<string>();
                    ppt = Campaign.CampaignAlign.ApplyPptEdits(ppt, Path.Combine(c.Project.ProjectFolder, "PptEdits.txt"), pptNotes);
                    Directory.CreateDirectory(newCamp);
                    File.WriteAllText(Path.Combine(newCamp, NameIn(oldCamp, "Ppt.ini")), ppt, Encoding.Latin1);
                    notes.Add($@"Campaign\Ppt.ini: threats and steerpoint types of the Korea 4.38 + {loc} local names of the old add-on (Korean leftovers dropped)");
                    if (pptNotes.Count > 0) { notes.Add($@"Campaign\Ppt.ini: {pptNotes.Count} threat ranges from PptEdits.txt:"); notes.AddRange(pptNotes); }
                }
                // Strings.txt in UTF-8 → Windows-1252 come quello della Korea
                string os = Path.Combine(oldCamp, NameIn(oldCamp, "Strings.txt"));
                if (File.Exists(os) && Campaign.CampaignAlign.FixAnsi(os) is { } ansi)
                {
                    Directory.CreateDirectory(newCamp);
                    File.WriteAllBytes(Path.Combine(newCamp, Path.GetFileName(os)), ansi);
                    notes.Add(@"Campaign\Strings.txt: converted from UTF-8 to Windows-1252 (as the Korea one; quotes and apostrophes show correctly)");
                }
            }
            // Meteo sereno per il TE: il "Sunny" di BMS e' in realta' FAIR con cumuli (densita' 5) in ogni cella.
            // Clear.fmap = Sunny.fmap della campagna con tutte le celle SUNNY, cumuli al minimo, niente torri ne' rovesci.
            string sunny = Path.Combine(oldCamp, "Sunny.fmap");
            if (File.Exists(sunny) && WriteClearFmap(sunny, Path.Combine(newCamp, "Clear.fmap")))
                notes.Add(@"Campaign\Clear.fmap: clear-sky weather map for the TE (BMS 'Sunny' still has scattered cumulus)");
        }
        // file salvati dall'editor di campagna: se sono piu' recenti della copia in Install, vanno al loro posto
        string campEdits = ObjectsJob.CampaignEditsFolder(c.Project);
        if (Directory.Exists(campEdits))
            foreach (var f in Directory.EnumerateFiles(campEdits).Where(f => f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase)
                                                                             || Path.GetFileName(f).Equals("stations+ils.dat", StringComparison.OrdinalIgnoreCase)
                                                                             || new[] { "Falcon4.aii", "TePlanes.lst", "TeUnits.lst", "TeNavalUnits.lst" }.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                                                             || Regex.IsMatch(Path.GetFileName(f), @"^MissionData_\w+\.xml$", RegexOptions.IgnoreCase)))
            {
                string dst = Path.Combine(newCamp, Path.GetFileName(f));
                if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(f)) continue;
                Directory.CreateDirectory(newCamp);
                File.Copy(f, dst, true);
                notes.Add($@"Campaign\{Path.GetFileName(f)} ← campaign editor save");
            }
        // I dati di gioco dei vecchi add-on (4.36/4.37: Art, objects, Sim, campagna) non sono compatibili con la 4.38
        // (interfaccia rotta, crash ATC "bad airbase branch", crash lettura aerei): per la prova del terreno si usano
        // quelli standard della 4.38 e del vecchio add-on si prende solo il terreno.
        string showName = TheaterDisplayName(c, tdf);
        string showDesc = !string.IsNullOrWhiteSpace(c.Project.Theater.Description) ? c.Project.Theater.Description.Trim()
            : $"{showName} - {c.Project.Theater.SizeKm} km theater, New Terrain for BMS 4.38. Restart BMS after any theatre change.";
        var sb = new StringBuilder();
        sb.Append("# ").Append(tdfName).Append(".tdf - written by BMS New Terrain Tool\n");
        sb.Append("# New Terrain from ").Append(terrDir).Append(" (made from ").Append(Path.GetFileName(tdfPath)).Append(")\n\n");
        sb.Append("name ").Append(showName).Append('\n');
        sb.Append("desc ").Append(showDesc).Append('\n');
        if (tdf.TryGetValue("bitmap", out var bm)) sb.Append("bitmap ").Append(bm).Append('\n');
        sb.Append("terraindir ").Append(terrDir).Append('\n');
        // la campagna Korea della 4.38 presume un mondo da 1024 km e sposta le posizioni in Instant Action:
        // serve quella dell'add-on (BMS ne rigenera i dati del terreno al primo caricamento)
        sb.Append("campaigndir ").Append(addOn).Append("\\Campaign\n");
        sb.Append("artdir ").Append(hasArt ? $@"{addOn}\NT" : "Art").Append('\n');
        // oggetti: la cartella fusa del lavoro "Objects" se c'e' (database 4.38 + disposizioni delle basi del vecchio add-on),
        // altrimenti quella della 4.38; i modelli 3D sono sempre quelli della 4.38
        bool hasObj = Directory.Exists(Path.Combine(root, "Terrdata", ObjectsJob.FolderName, "ObjectiveRelatedData"));
        sb.Append("objectdir ").Append(hasObj ? $@"{addOn}\Terrdata\{ObjectsJob.FolderName}" : @"Terrdata\objects").Append('\n');
        sb.Append("3ddatadir Terrdata\\objects\nmisctexdir Terrdata\\misctex\n");
        // Sim: propria del teatro (copia completa della 4.38 + SimOverrides del progetto) se il progetto ha la cartella SimOverrides
        bool ownSim = false;
        string? simData = FindDataDir(c.Project.KoreaNewTerrain);
        if (TheaterSimData.Enabled(c.Project) && simData != null)
        {
            notes.AddRange(TheaterSimData.BuildSim(Path.Combine(simData, "Sim"), Path.Combine(root, TheaterSimData.SimRel), TheaterSimData.SimOverridesFolder(c.Project)));
            ownSim = true;
        }
        sb.Append("simdatadir ").Append(ownSim ? $@"{addOn}\{TheaterSimData.SimRel}" : "Sim").Append('\n');
        sb.Append("sounddir Sounds\nmintacan 1\ndoubleres2dmap 1\nmagneticdeclination 0\n");
        File.WriteAllText(Path.Combine(defDir, tdfName + ".tdf"), sb.ToString());
        string? bmp = tdf.GetValueOrDefault("bitmap");
        notes.Add($"{linked} files linked, {copied} copied, {skipped} already up to date ({total / 1073741824.0:0.0} GB in total)");
        notes.Add($"Theater definition: {addOn}\\Terrdata\\theaterdefinition\\{tdfName}.tdf (bitmap: {bmp ?? "none"})");
        notes.Add(hasObj ? $@"Objects: {addOn}\Terrdata\{ObjectsJob.FolderName} (airbase layouts of the old add-on)" : "Objects: BMS 4.38 (Korean airbase layouts): run the 'Objects' job first");
        notes.Add(hasArt ? $"2D map: {addOn}\\NT\\Art\\resource\\campmap.rsc (artdir {addOn}\\NT)" : "2D map not made yet (job '2D map (campmap)'): the UI will show the Korea map");

        // 5) istruzioni
        string lstLine = $@"{addOn}\Terrdata\theaterdefinition\{tdfName}.tdf";
        File.WriteAllText(Path.Combine(install, "INSTALL.txt"),
$@"INSTALL - {showName} (New Terrain)
(Tab 9 'Install into BMS' of the TerrainTool does these steps for you, with backups. To do them by hand:)

1. If '{addOn}' is not already in your BMS Data folder, copy the whole folder
      {c.Project.OldTheaterFolder}
   to
      <BMS>\Data\{addOn}
2. Copy the folder
      {root}
   onto <BMS>\Data\{addOn}  (merge folders and overwrite: it adds {relTerr}\NewTerrain, NT, Terrdata\{ObjectsJob.FolderName}, theaterdefinition\{tdfName}.tdf
   and replaces a few Campaign files: Te_New_Nt.tac and the TE Builder lists from BMS 4.38).
   Before copying, delete <BMS>\Data\{addOn}\{relTerr}\NewTerrain so BMS rebuilds its cache files.
3. Make a backup copy of <BMS>\Data\TerrData\TheaterDefinition\theater.lst, then add this line at the end:
      {lstLine}
4. Open the BMS 4.38 Launcher, select the theater ""{showName}"" and start BMS.
   The first start is slow: BMS generates LOS250, TerrainLevelingData, LandCoverData and the other cache files.

To undo: remove the line from theater.lst (or restore the backup) and delete the NewTerrain folder and the _NT.tdf.
", Encoding.UTF8);
        notes.Add("Instructions: Output\\Install\\INSTALL.txt");
        p.Report(1);
        return (null, notes);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Legge un .tdf: chiave = prima parola, valore = resto della riga; i commenti sono ignorati.</summary>
    public static List<KeyValuePair<string, string>> ParseTdfList(IEnumerable<string> lines)
    {
        var res = new List<KeyValuePair<string, string>>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            int sp = l.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0) continue;
            res.Add(new(l[..sp].ToLowerInvariant(), l[(sp + 1)..].Trim()));
        }
        return res;
    }

    /// <summary>
    /// Scrive una mappa meteo serena partendo da una .fmap (versione 8: dati per campo, una matrice XCELLS×YCELLS per campo;
    /// 0 condizione, 1 pressione, 2 temperatura, 3-12 vento, 13-22 direzione, 23 quota cumuli, 24 densita', 25 dimensione,
    /// 26 torri, 27 rovesci, 28-29 nebbia). Restituisce false se il formato non e' quello atteso.
    /// </summary>
    public static bool WriteClearFmap(string src, string dst)
    {
        var b = File.ReadAllBytes(src);
        if (b.Length < 12) return false;
        int ver = BitConverter.ToInt32(b, 0), x = BitConverter.ToInt32(b, 4), y = BitConverter.ToInt32(b, 8);
        long n = (long)x * y;
        if (ver != 8 || n <= 0 || 44 + n * 4 * 30 != b.Length) return false;
        void SetI(int field, int v) { for (long i = 0; i < n; i++) BitConverter.TryWriteBytes(b.AsSpan((int)(44 + 4 * (n * field + i)), 4), v); }
        void SetF(int field, float v) { for (long i = 0; i < n; i++) BitConverter.TryWriteBytes(b.AsSpan((int)(44 + 4 * (n * field + i)), 4), v); }
        SetI(0, 1);     // SUNNY (1, come in tutte le celle del Sunny.fmap della Korea; 0 non e' un tipo di tempo)
        SetI(24, 1);    // densita' cumuli minima
        SetF(25, 0f);   // dimensione cumuli
        SetI(26, 0);    // torri
        SetI(27, 0);    // rovesci
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, b);
        return true;
    }

    /// <summary>Risale dalla cartella NewTerrain della Korea fino alla cartella Data di BMS (quella con Campaign e Terrdata).</summary>
    public static string? FindDataDir(string start)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Campaign")) && Directory.Exists(Path.Combine(d.FullName, "Terrdata"))) return d.FullName;
        return null;
    }

    private static Dictionary<string, string> ParseTdf(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParseTdfList(lines)) d[kv.Key] = kv.Value;
        return d;
    }
}
