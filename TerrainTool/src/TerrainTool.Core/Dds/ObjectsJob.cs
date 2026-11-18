using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using TerrainTool.Core.Editor;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Cartella oggetti "fusa" per il New Terrain (Output\Install\&lt;Add-On&gt;\Terrdata\ObjectsNT, mai in BMS):
///   - database della 4.38 (Falcon4_*.xml: unita', aerei, armi validi nella 4.38);
///   - disposizioni delle basi e degli obiettivi (FED/PDX/PHD) prese dal vecchio add-on, perche' quelle della
///     4.38 con lo stesso numero OCD sono le basi coreane (Muharraq = Gimpo, piste in mare, aerei sulla sabbia);
///     i codici delle strutture (FeatureCtIdx) che nella 4.38 indicano un altro oggetto vengono rimappati per nome;
///   - TerrainRelatedData: spianamento (TLD) e copertura "aeroporto" (LCD) generati per ogni base del vecchio teatro,
///     cosi' la 4.38 non applica quelli coreani (stesso numero di obiettivo, coordinate della Korea).
/// I modelli 3D restano quelli della 4.38 (3ddatadir Terrdata\objects).
/// </summary>
public sealed partial class ObjectsJob : TextureJob
{
    public override string Id => "objects";
    public override string Name => "Objects (airbase layouts)";
    public override string Description => "Builds Output\\Install\\<add-on>\\Terrdata\\ObjectsNT: the BMS 4.38 object database with the old add-on's airbase and objective layouts, plus terrain leveling (TLD) and airbase land cover (LCD) for every airbase, made from our heightmap. Run it before 'Build theater'.";
    public override string Output => @"..\Install";

    public const string FolderName = "ObjectsNT";
    /// <summary>Cartella del progetto dove l'editor di campagna salva i .tac/.cam modificati.</summary>
    public static string CampaignEditsFolder(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "CampaignEdits");
    /// <summary>Dove l'installazione salva la cache del terreno della campagna di BMS prima di cancellarla.</summary>
    public static string KeptTerrainCacheFolder(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "CampaignTerrainCache");
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const double FtPerM = 3.280839895;
    /// <summary>Margine attorno a piste, vie di rullaggio e parcheggi per lo spianamento (piedi).</summary>
    public double MarginFt { get; set; } = 600;

    private static string? BmsObjects(TextureContext c)
    {
        var data = AssembleJob.FindDataDir(c.Project.KoreaNewTerrain);
        string? d = data == null ? null : Path.Combine(data, "Terrdata", "objects");
        return d != null && File.Exists(Path.Combine(d, "Falcon4_CT.xml")) ? d : null;
    }
    private static string? OldObjects(TextureContext c)
    {
        string d = Path.Combine(c.Project.OldTheaterFolder, "Terrdata", "Objects");
        return File.Exists(Path.Combine(d, "Falcon4_CT.xml")) && Directory.Exists(Path.Combine(d, "ObjectiveRelatedData")) ? d : null;
    }
    private static string? OldCampObjData(TextureContext c)
    {
        string f = Path.Combine(c.Project.OldTheaterFolder, "Campaign", "CampObjData.XML");
        return File.Exists(f) ? f : null;
    }
    private static string? HeightMap(TextureContext c)
    {
        string f = Path.Combine(c.NewTerrain, "HeightMaps", "HeightMap.raw");
        return File.Exists(f) ? f : null;
    }

    public override IReadOnlyList<JobInput> Inputs(TextureContext c) => new[]
    {
        new JobInput("BMS 4.38 objects (database, read-only)", BmsObjects(c), 0, true),
        new JobInput("Old add-on objects (layouts, read-only)", OldObjects(c), 0, true),
        new JobInput("Old add-on Campaign\\CampObjData.XML (objective positions)", OldCampObjData(c), 0, true),
        new JobInput("Heightmap (NewTerrain\\HeightMaps\\HeightMap.raw)", HeightMap(c), 0, true),
    };

    // ───────────────────────── lettura XML ─────────────────────────

    private sealed record Ct(int EntityType, int EntityIdx, int Graphics, int Type = -1);

    private static Dictionary<int, Ct> LoadCt(string path)
    {
        var s = File.ReadAllText(path);
        var d = new Dictionary<int, Ct>();
        foreach (Match m in Regex.Matches(s, @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline))
        {
            string b = m.Groups[2].Value;
            int G(string t) { var x = Regex.Match(b, $"<{t}>(-?\\d+)</{t}>"); return x.Success ? int.Parse(x.Groups[1].Value, Ci) : -1; }
            d[int.Parse(m.Groups[1].Value, Ci)] = new Ct(G("EntityType"), G("EntityIdx"), G("GraphicsNormal"), G("Type"));
        }
        return d;
    }

    private static Dictionary<int, string> LoadFcdNames(string path)
    {
        var s = File.ReadAllText(path);
        var d = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(s, @"<FCD Num=""(\d+)"">(.*?)</FCD>", RegexOptions.Singleline))
        {
            var n = Regex.Match(m.Groups[2].Value, "<Name>(.*?)</Name>");
            d[int.Parse(m.Groups[1].Value, Ci)] = n.Success ? System.Net.WebUtility.HtmlDecode(n.Groups[1].Value).Trim() : "";
        }
        return d;
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    private static string FirstWord(string s) { var n = Norm(s); int i = n.IndexOf(' '); return i < 0 ? n : n[..i]; }

    private const int Feature = 1;

    /// <summary>
    /// Per ogni struttura (EntityType 1) del vecchio database: quale indice CT della 4.38 usare.
    /// Stesso indice se indica lo stesso oggetto (stesso modello, stesso nome, o stesso tipo: "Apartment AR6" → "Apartment"),
    /// altrimenti l'oggetto della 4.38 con lo stesso modello, poi con lo stesso nome o con il nome piu' simile; se non c'e', resta l'indice originale.
    /// </summary>
    private static Dictionary<int, int> FeatureMap(Dictionary<int, Ct> oldCt, Dictionary<int, string> oldFcd,
        Dictionary<int, Ct> newCt, Dictionary<int, string> newFcd, List<string> doubtful, out int remapped)
    {
        var map = new Dictionary<int, int>();
        remapped = 0;
        var newFeatures = newCt.Where(kv => kv.Value.EntityType == Feature)
            .Select(kv => (Idx: kv.Key, Name: newFcd.GetValueOrDefault(kv.Value.EntityIdx, ""))).ToList();
        var byName = newFeatures.GroupBy(f => Norm(f.Name)).ToDictionary(g => g.Key, g => g.Select(f => f.Idx).ToList());
        var byGfx = newCt.Where(kv => kv.Value.EntityType == Feature && kv.Value.Graphics > 0).GroupBy(kv => kv.Value.Graphics).ToDictionary(g => g.Key, g => g.Min(kv => kv.Key));
        foreach (var (idx, ct) in oldCt)
        {
            if (ct.EntityType != Feature) continue;
            string oldName = oldFcd.GetValueOrDefault(ct.EntityIdx, "");
            string newName = newCt.TryGetValue(idx, out var nc) && nc.EntityType == Feature ? newFcd.GetValueOrDefault(nc.EntityIdx, "") : "";
            // stesso modello allo stesso indice: e' lo stesso oggetto anche se la 4.38 l'ha rinominato (es. "Bridge 2 Ramp" della 4.36
            // e' "Bridge 1 Ramp" nella 4.38, stesso modello 1087): il nome non conta
            if (nc != null && nc.EntityType == Feature && nc.Graphics > 0 && nc.Graphics == ct.Graphics) { map[idx] = idx; continue; }
            if (newName.Length > 0 && Norm(newName) == Norm(oldName)) { map[idx] = idx; continue; }
            // altrove nella 4.38 c'e' lo stesso modello: quello
            if (ct.Graphics > 0 && byGfx.TryGetValue(ct.Graphics, out int sg)) { map[idx] = sg; remapped++; continue; }
            if (byName.TryGetValue(Norm(oldName), out var same) && same.Count > 0) { map[idx] = same[0]; remapped++; continue; }
            if (newName.Length > 0 && FirstWord(newName) == FirstWord(oldName)) { map[idx] = idx; continue; }
            // nome piu' simile tra quelli con la stessa prima parola
            var words = Norm(oldName).Split(' ').ToHashSet();
            var best = newFeatures.Where(f => f.Name.Length > 0 && FirstWord(f.Name) == FirstWord(oldName))
                .Select(f => (f.Idx, f.Name, Score: Norm(f.Name).Split(' ').Count(words.Contains)))
                .OrderByDescending(f => f.Score).ThenBy(f => f.Idx).FirstOrDefault();
            if (best.Name != null && best.Score > 0) { map[idx] = best.Idx; remapped++; continue; }
            map[idx] = idx;
            doubtful.Add($"{idx} '{oldName}' → 4.38 '{(newName.Length > 0 ? newName : "(not a feature)")}'");
        }
        return map;
    }

    // ───────────────────────── esecuzione ─────────────────────────

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var notes = new List<string>();
        _genericRunways.Clear();
        string bms = BmsObjects(c)!, old = OldObjects(c)!;
        string addOn = new DirectoryInfo(c.Project.OldTheaterFolder).Name;
        string install = Path.Combine(c.Project.ProjectFolder, "Output", "Install");
        string dst = Path.Combine(install, addOn, "Terrdata", FolderName);
        if (c.Project.IsProtectedPath(dst)) throw new InvalidOperationException("Output folder is protected.");
        // si riparte da zero: i file compilati da BMS (FALCON4.*) e i TLD vecchi non devono restare
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        Directory.CreateDirectory(dst);

        // 1) database della 4.38 (solo gli XML e i .dat: i FALCON4.* li ricompila BMS)
        int dbFiles = 0;
        foreach (var f in Directory.EnumerateFiles(bms))
        {
            string n = Path.GetFileName(f), ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".xml" or ".dat" or ".ini") { File.Copy(f, Path.Combine(dst, n)); dbFiles++; }
        }
        notes.Add($"BMS 4.38 database: {dbFiles} files");
        // modifiche del progetto al database (DbEdits.txt), rifatte a ogni giro sopra la copia della 4.38
        notes.AddRange(TheaterSimData.ApplyDbEdits(dst, TheaterSimData.DbEditsFile(c.Project)));
        p.Report(0.05);

        // 2) rimappatura delle strutture
        var oldCt = LoadCt(Path.Combine(old, "Falcon4_CT.xml"));
        var newCt = LoadCt(Path.Combine(bms, "Falcon4_CT.xml"));
        var oldFcd = LoadFcdNames(Path.Combine(old, "Falcon4_FCD.xml"));
        var newFcd = LoadFcdNames(Path.Combine(bms, "Falcon4_FCD.xml"));
        var doubtful = new List<string>();
        var fmap = FeatureMap(oldCt, oldFcd, newCt, newFcd, doubtful, out int remapped);
        _featureName = i => newCt.TryGetValue(i, out var r) && r.EntityType == Feature ? newFcd.GetValueOrDefault(r.EntityIdx, "") : "";
        _ctByName = newCt.Where(kv => kv.Value.EntityType == Feature).OrderBy(kv => kv.Key)
            .GroupBy(kv => newFcd.GetValueOrDefault(kv.Value.EntityIdx, "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        _swappedDirs.Clear(); _swapVar.Clear();
        var runwayLog = new List<string>();
        p.Report(0.15);

        // 3) ObjectiveRelatedData: tutte le cartelle della 4.38; dove il vecchio add-on ha la stessa OCD,
        //    disposizioni (FED/PDX/PHD) del vecchio add-on e niente alberi coreani (OTD)
        string oldOrd = Path.Combine(old, "ObjectiveRelatedData"), newOrd = Path.Combine(bms, "ObjectiveRelatedData");
        string outOrd = Path.Combine(dst, "ObjectiveRelatedData");
        var oldDirs = Directory.EnumerateDirectories(oldOrd, "OCD_*").ToDictionary(d => Path.GetFileName(d).ToUpperInvariant());
        var newDirs = Directory.EnumerateDirectories(newOrd, "OCD_*").ToDictionary(d => Path.GetFileName(d).ToUpperInvariant());
        var usedFeatures = new Dictionary<int, int>();   // vecchio indice CT → numero di usi (per le note)
        var branchFixes = new List<string>();
        int fromOld = 0, fromNew = 0, ctIdxDiff = 0, k = 0;
        var all = oldDirs.Keys.Union(newDirs.Keys).OrderBy(x => x).ToList();
        foreach (var name in all)
        {
            ct.ThrowIfCancellationRequested();
            string od = Path.Combine(outOrd, Path.GetFileName(newDirs.GetValueOrDefault(name) ?? oldDirs[name]));
            Directory.CreateDirectory(od);
            if (oldDirs.TryGetValue(name, out var src))
            {
                fromOld++;
                foreach (var f in Directory.EnumerateFiles(src))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.StartsWith("FED_", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(Path.Combine(od, fn), RemapFed(File.ReadAllText(f), fmap, usedFeatures), new UTF8Encoding(false));
                    else if (fn.StartsWith("PDX_", StringComparison.OrdinalIgnoreCase))
                    {
                        string? phdFile = Directory.EnumerateFiles(src, "PHD_*.XML").FirstOrDefault();
                        string xml = File.ReadAllText(f);
                        if (phdFile != null) xml = FixBranches(xml, File.ReadAllText(phdFile), name, branchFixes);
                        File.WriteAllText(Path.Combine(od, fn), xml, new UTF8Encoding(false));
                    }
                    else if (fn.StartsWith("PHD_", StringComparison.OrdinalIgnoreCase)) File.Copy(f, Path.Combine(od, fn));
                }
                // scheda dell'obiettivo: quella della 4.38 se c'e' (stesso formato), altrimenti la vecchia
                string? ocdNew = newDirs.TryGetValue(name, out var nd) ? Directory.EnumerateFiles(nd, "OCD_*.XML").FirstOrDefault() : null;
                string? ocdOld = Directory.EnumerateFiles(src, "OCD_*.XML").FirstOrDefault();
                if (ocdNew != null && ocdOld != null && CtIdx(ocdNew) != CtIdx(ocdOld)) { ctIdxDiff++; ocdNew = null; }
                string? ocd = ocdNew ?? ocdOld;
                if (ocd != null) File.Copy(ocd, Path.Combine(od, Path.GetFileName(ocd)));
                AddMissingRunways(od, runwayLog);
            }
            else
            {
                fromNew++;
                foreach (var f in Directory.EnumerateFiles(newDirs[name])) File.Copy(f, Path.Combine(od, Path.GetFileName(f)));
            }
            p.Report(0.15 + 0.55 * ++k / all.Count);
        }
        notes.Add($"ObjectiveRelatedData: {fromOld} layouts from {addOn}, {fromNew} from BMS 4.38 (OCD numbers the old add-on does not use)"
                  + (ctIdxDiff > 0 ? $", {ctIdxDiff} with a different CtIdx (old OCD record kept)" : ""));
        notes.Add($"Features: {remapped} codes remapped to the 4.38 object with the same model, or else the same name");
        if (runwayLog.Count > 0)
        {
            notes.Add($"Runways without 3D pieces (painted on the old terrain tiles): {runwayLog.Count}, generic 4.38 runway added:");
            foreach (var r in runwayLog.Take(40)) notes.Add("   " + r);
        }
        if (branchFixes.Count > 0)
        {
            notes.Add($"Taxiway branches: {branchFixes.Count} backward branches removed (BMS 4.38 stops with 'bad airbase branch setup' if a branch goes back along the list):");
            foreach (var b in branchFixes.Take(30)) notes.Add("   " + b);
        }
        var doubtfulUsed = doubtful.Where(d => usedFeatures.ContainsKey(int.Parse(d[..d.IndexOf(' ')], Ci))).ToList();
        if (doubtfulUsed.Count > 0)
        {
            notes.Add($"{doubtfulUsed.Count} feature types used by the layouts have no equivalent in 4.38 (custom {addOn} models): they show the 4.38 object with the same code:");
            foreach (var d in doubtfulUsed.Take(40)) notes.Add("   " + d + $" ({usedFeatures[int.Parse(d[..d.IndexOf(' ')], Ci)]}×)");
        }

        // 4) spianamento e copertura delle basi
        // 4a) basi spostate sulle coordinate reali (airbase_targets.csv del progetto): nuova CampObjData.XML
        var objs = LoadCampObjs(OldCampObjData(c)!);
        // the old add-on's positions as they are in its file (its grid): what the campaign files contain before Apply object changes
        var orig = objs.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var srcGrid = new TerrainTool.Core.Theater.TheaterGrid(c.Project.Theater);
        if (srcGrid.Reprojects)
        {
            objs = objs.Select(o => { var (n, e) = srcGrid.FromSourceFt(o.X, o.Y); return o with { X = n, Y = e }; }).ToList();
            double far = objs.Max(o => Math.Sqrt((o.X - orig[o.Id].X) * (o.X - orig[o.Id].X) + (o.Y - orig[o.Id].Y) * (o.Y - orig[o.Id].Y))) / FtPerM / 1000;
            int outside = objs.Count(o => o.X < 0 || o.Y < 0 || o.X > c.Project.Theater.SizeMeters * FtPerM || o.Y > c.Project.Theater.SizeMeters * FtPerM);
            notes.Add($"Old add-on positions carried over to the standard projection: {objs.Count} objectives (largest shift {far:0.0} km)"
                      + (outside > 0 ? $", {outside} now outside the theater" : ""));
        }
        _bake.Clear();
        _bridgeTld.Clear();
        _bridgeShift.Clear();
        string targets = Path.Combine(c.Project.ProjectFolder, "airbase_targets.csv");
        if (File.Exists(targets))
        {
            var before = objs.ToDictionary(o => o.Id);
            var ocdType = newCt.Values.Where(v => v.EntityType == 3).GroupBy(v => v.EntityIdx).ToDictionary(g => g.Key, g => g.First().Type);
            // basi tolte (basi_tieni_via.csv del progetto, decisione VIA): diventano incroci, i collegamenti restano
            var removed = RemoveObjectives(c, "basi_tieni_via.csv", new HashSet<int> { 1, 2 }, "Airbases", objs, newCt, ocdType, notes);
            var removedBases = removed.Keys.ToHashSet();
            // ponti non su fiumi veri e spiaggia (ponti_tieni_via.csv): incroci anche loro
            // ponti: bridges.csv (Optimize bridges dell'Object Editor, acqua del terreno nuovo) se c'e', altrimenti ponti_tieni_via.csv
            var bridgePlan = TerrainTool.Core.Editor.BridgePlan.Load(c.Project.ProjectFolder);
            if (bridgePlan != null)
            {
                var bj = bridgePlan.Values.Where(r => r.Final == TerrainTool.Core.Editor.BridgeAction.Junction).Select(r => r.CampId).ToHashSet();
                foreach (var kv in ConvertToJunctions(bj, t => t == 6, "Bridges without water (bridges.csv)", objs, newCt, ocdType, notes)) removed[kv.Key] = kv.Value;
                // le spiagge restano gestite da ponti_tieni_via.csv
                var beaches = ReadVia(c, "ponti_tieni_via.csv", notes);
                if (beaches.Count > 0) foreach (var kv in ConvertToJunctions(beaches, t => t == 4, "Beaches removed (ponti_tieni_via.csv)", objs, newCt, ocdType, notes)) removed[kv.Key] = kv.Value;
            }
            else foreach (var kv in RemoveObjectives(c, "ponti_tieni_via.csv", new HashSet<int> { 4, 6 }, "Bridges/beaches", objs, newCt, ocdType, notes)) removed[kv.Key] = kv.Value;
            // modifiche fatte a mano nell'editor degli obiettivi (objective_edits.csv): incroci, poi posizioni e direzioni
            var edits = TerrainTool.Core.Editor.ObjectiveEdits.Load(c.Project.ProjectFolder);
            var editJunctions = edits.Values.Where(e => e.Junction && !removed.ContainsKey(e.CampId)).Select(e => e.CampId).ToHashSet();
            if (editJunctions.Count > 0)
                foreach (var kv in ConvertToJunctions(editJunctions, _ => true, "Objective editor", objs, newCt, ocdType, notes)) removed[kv.Key] = kv.Value;
            // basi sostituite con una base coreana completa (base_swaps.csv, dall'editor): disposizione della 4.38 copiata con OCD nuovo
            var swaps = TerrainTool.Core.Editor.BaseSwaps.Load(c.Project.ProjectFolder);
            var swapped = swaps.Count > 0 ? SwapLayouts(swaps, objs, removed, newOrd, dst, outOrd, notes) : new Dictionary<int, (int Ct, int Features)>();
            var (moved, relRep, _) = Relocate(c, objs, targets, outOrd);
            int manual = ApplyEditPositions(objs, edits);
            if (edits.Count > 0) notes.Add($"Objective editor (objective_edits.csv): {edits.Count} edits, {manual} positions/headings applied, {editJunctions.Count} junctions");
            // ogni base spostata che condivide la disposizione con altri obiettivi riceve una sua copia (OCD + voce CT),
            // cosi' numeri di pista e ritocchi di una base non cambiano le altre
            var newType = CloneSharedLayouts(objs, objs.Where(o => before[o.Id] != o && !removed.ContainsKey(o.Id) && ocdType.GetValueOrDefault(o.Ocd) is 1 or 2)
                .Select(o => o.Id).ToHashSet(), dst, outOrd, notes);
            foreach (var kv in swapped) newType[kv.Key] = kv.Value.Ct;
            // disposizioni scelte a mano al posto di quella dell'obiettivo (layout_swaps.csv: ponte piu' lungo, porto → citta'...)
            var layoutSwaps = TerrainTool.Core.Editor.LayoutSwaps.Load(c.Project.ProjectFolder);
            var replaced = ReplaceLayouts(c, layoutSwaps, objs, removed, swapped, newOrd, dst, outOrd, ocdType, notes);
            foreach (var kv in replaced) newType[kv.Key] = kv.Value.Ct;
            // pezzi spostati/girati/tolti a mano nell'editor (piece_edits.csv): copia propria della disposizione se e' condivisa,
            // poi i pezzi; prima dello spostamento verso terra, che cosi' conta i pezzi dove sono davvero
            var pieceEdits = TerrainTool.Core.Editor.PieceEdits.Load(c.Project.ProjectFolder);
            var pointEdits = TerrainTool.Core.Editor.PointEdits.Load(c.Project.ProjectFolder);
            if (pieceEdits.Count > 0 || pointEdits.Count > 0)
            {
                var pids = pieceEdits.Where(kv => kv.Value.Values.Any(e => !e.IsNoOp)).Select(kv => kv.Key)
                    .Concat(pointEdits.Where(kv => kv.Value.Values.Any(e => !e.IsNoOp)).Select(kv => kv.Key))
                    .Where(id => !removed.ContainsKey(id) && objs.Any(o => o.Id == id)).ToHashSet();
                foreach (var kv in CloneSharedLayouts(objs, pids, dst, outOrd, notes, "objectives with pieces edited by hand")) newType[kv.Key] = kv.Value;
                ApplyPieceEdits(objs, pids.Where(pieceEdits.ContainsKey).ToHashSet(), pieceEdits, outOrd, notes);
                ApplyPointEdits(objs, pids.Where(pointEdits.ContainsKey).ToHashSet(), pointEdits, outOrd, notes);
            }
            // obiettivi (non basi) con gli edifici in acqua sul terreno nuovo: spostati verso la terra piu' vicina
            var preSnap = objs.ToDictionary(o => o.Id, o => (o.X, o.Y));
            var coastBefore = TerrainTool.Core.Editor.CoastalPlan.Load(c.Project.ProjectFolder);   // scelte KEEP degli incroci (SnapToLand riscrive il file)
            SnapToLand(c, objs, outOrd, ocdType, notes);
            MoveJunctions(c, objs, preSnap, removed, ocdType, outOrd, notes, coastBefore);
            ApplyEditPositions(objs, edits);   // la mano vince sullo spostamento automatico
            // ponti tenuti, a piu' pezzi: quote dei pezzi (OffsetZ) perche' l'impalcato sia continuo; copia della disposizione se e' condivisa
            if (bridgePlan != null)
                foreach (var kv in LevelBridges(c, objs, removed, ocdType, dst, outOrd, notes,
                             bridgePlan.Values.Where(r => r.LevelDecision == false).Select(r => r.CampId).ToHashSet(), newCt, Path.Combine(bms, "Models"))) newType[kv.Key] = kv.Value;
            // ponti: il punto dell'obiettivo nel km del fiume da attraversare (BMS segna come ponte solo quel km), pezzi fermi
            foreach (var kv in PlanBridgeCells(c, objs, removed, ocdType, dst, outOrd, notes)) newType[kv.Key] = kv.Value;
            // basi coreane con i numeri di pista scritti nei modelli e girate: piste generiche con i numeri dalla direzione
            if (swapped.Count > 0) GenericRunwaysForTurnedBases(objs, outOrd, swapped, notes);
            // obiettivi girati con una disposizione tutta loro: la rotazione si mette dentro la disposizione (BakeRotations, piu' avanti)
            // e nei file della campagna la direzione resta 0. Il Recon dell'interfaccia di BMS non applica bene la direzione
            // dell'obiettivo ai pezzi (posizioni sbagliate, tanto piu' quanto la base e' girata); il simulatore si'.
            PlanBake(objs.Where(o => before[o.Id] != o).Select(o => o.Id).ToHashSet(), objs, removed, outOrd);
            double Hd(CampObj o) => _bake.ContainsKey(o.Id) ? 0 : o.Heading;
            // Le modifiche che riguardano la campagna (CampObjData.XML, .cam, modello del TE, stations+ils.dat) non si scrivono qui:
            // vanno in Output\ObjectChanges.json e le applica il Campaign Editor ("Apply object changes").
            var changes = new TerrainTool.Core.Campaign.ObjectChangeSet();
            foreach (var o in objs)
            {
                var b = before[o.Id];
                if (o == b && !srcGrid.Reprojects) continue;
                var ob = orig.TryGetValue(o.Id, out var oo) ? oo : b;   // position in the campaign files (old grid when reprojecting)
                changes.Objectives.Add(new TerrainTool.Core.Campaign.ObjectiveChange(o.Id, o.Ocd, o.Name != b.Name ? o.Name : null, Hd(o), o.X, o.Y, edits.ContainsKey(o.Id)));
                changes.Moves.Add(removed.TryGetValue(o.Id, out int jct)
                    ? new CampaignArchive.Move(b.Name, ob.X, ob.Y, o.X, o.Y, o.Heading, jct, o.Id, true, o.Name)
                    : new CampaignArchive.Move(o.Name, ob.X, ob.Y, o.X, o.Y, Hd(o), newType.TryGetValue(o.Id, out int t) ? t : -1, o.Id,
                                               NewFeatures: swapped.TryGetValue(o.Id, out var sw) ? sw.Features : replaced.TryGetValue(o.Id, out var rp) ? rp.Features : -1));
            }
            changes.RemovedBases = removedBases.OrderBy(x => x).ToList();
            string chPath = TerrainTool.Core.Campaign.ObjectChangeSet.PathFor(c.Project);
            // obiettivi cambiati da un giro precedente e non piu' da questo (es. KEEP in coastal_moves.csv, ponte non piu' spostato):
            // si rimettono come nell'add-on, altrimenti nei salvataggi della campagna resterebbe la posizione vecchia
            try
            {
                var prevSet = TerrainTool.Core.Campaign.ObjectChangeSet.Load(chPath);
                if (prevSet != null)
                {
                    var nowIds = changes.Objectives.Select(x => x.Id).ToHashSet();
                    var cur = objs.ToDictionary(o => o.Id);
                    int rev = 0;
                    foreach (var pc in prevSet.Objectives)
                    {
                        if (nowIds.Contains(pc.Id) || !before.TryGetValue(pc.Id, out var b) || !cur.ContainsKey(pc.Id)) continue;
                        changes.Objectives.Add(new TerrainTool.Core.Campaign.ObjectiveChange(b.Id, b.Ocd, null, b.Heading, b.X, b.Y, false));
                        // aveva un'altra disposizione (base coreana o disposizione scelta a mano, poi tolta): si rimettono tipo e pezzi
                        int backCt = -1, backFeat = -1;
                        if (pc.Ocd != b.Ocd && OcdCt(dst, b.Ocd) is int bct)
                        {
                            backCt = bct;
                            var bf = Directory.Exists(Path.Combine(outOrd, $"OCD_{b.Ocd:00000}")) ? Directory.EnumerateFiles(Path.Combine(outOrd, $"OCD_{b.Ocd:00000}"), "FED_*.XML").FirstOrDefault() : null;
                            backFeat = bf == null ? 0 : Regex.Matches(File.ReadAllText(bf), @"<FED Num=""\d+"">").Count;
                        }
                        changes.Moves.Add(new CampaignArchive.Move(pc.NewName ?? b.Name, pc.X, pc.Y, b.X, b.Y, b.Heading, backCt, b.Id, NewFeatures: backFeat));
                        nowIds.Add(pc.Id);
                        rev++;
                    }
                    if (rev > 0) notes.Add($"Objectives changed by an earlier Run Objects and not by this one (e.g. KEEP in coastal_moves.csv): {rev} put back as in the add-on");
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            changes.Save(chPath);
            notes.Add($"Objective changes for the campaign: {changes.Objectives.Count} objectives ({changes.RemovedBases.Count} removed bases) → {chPath}");
            notes.Add("   Apply them in the Campaign Editor (project, 'Apply object changes'): CampObjData.XML, *.cam, Te_New_Nt.tac, stations+ils.dat.");
            File.WriteAllText(Path.Combine(dst, "Airbases_relocation.csv"), relRep, new UTF8Encoding(false));
            notes.Add($@"Airbases moved to their real runways: {moved} (Campaign\CampObjData.XML, report Terrdata\{FolderName}\Airbases_relocation.csv)");
        }
        else notes.Add("No airbase_targets.csv in the project folder: airbases stay where the old add-on put them");
        int rwyNums = SetRunwayNumbers(outOrd, objs);
        if (rwyNums > 0) notes.Add($"Runway numbers (QFU) set on the generic runways and Korean bases: {rwyNums} landing directions (magnetic, variation {MagneticVariation:0.#}°E)");
        if (_swappedDirs.Count > 0) FixSwappedSigns(outOrd, objs, notes);
        if (_bake.Count > 0) BakeRotations(c, objs, outOrd, notes);
        else if (Directory.Exists(BakedLayouts.Folder(c.Project.ProjectFolder))) Directory.Delete(BakedLayouts.Folder(c.Project.ProjectFolder), true);
        if (_bridgeShift.Count > 0) ShiftBridgeLayouts(c, objs, outOrd, notes);
        var (lev, report) = MakeTerrainData(c, objs, outOrd, Path.Combine(dst, "TerrainRelatedData"), ct, p);
        notes.Add($"TerrainRelatedData: leveling (TLD) and airbase land cover (LCD) for {lev} airbases");
        WriteBridgeTld(Path.Combine(dst, "TerrainRelatedData"), notes);
        File.WriteAllText(Path.Combine(dst, "Airbases_leveling.csv"), report, new UTF8Encoding(false));
        notes.Add($@"Report: Install\{addOn}\Terrdata\{FolderName}\Airbases_leveling.csv");
        notes.Add("Run 'Build theater' again: the .tdf will use objectdir " + $@"{addOn}\Terrdata\{FolderName}");
        p.Report(1);
        return (null, notes);
    }

    /// <summary>
    /// Diramazioni delle vie di rullaggio (BranchIdx/RootIdx, indici relativi alla lista di punti di ogni PHD).
    /// La 4.38 vuole che la diramazione vada sempre avanti nella lista (RootIdx minore dell'indice del punto) e che
    /// le due meta' si corrispondano; i dati 4.36 a volte tornano indietro ("ROOT IDX is incorrect" → crash ATC).
    /// Le coppie non valide vengono tolte: si perde una scorciatoia di rullaggio, non la via.
    /// </summary>
    private static string FixBranches(string pdx, string phd, string ocdName, List<string> fixes)
    {
        var blocks = Regex.Matches(pdx, @"<PD Num=""(\d+)"">.*?</PD>", RegexOptions.Singleline).Cast<Match>().ToList();
        int? Tag(int g, string t)
        {
            if (g < 0 || g >= blocks.Count) return null;
            var m = Regex.Match(blocks[g].Value, $"<{t}>(-?\\d+)</{t}>");
            return m.Success ? int.Parse(m.Groups[1].Value, Ci) : null;
        }
        var remove = new Dictionary<int, HashSet<string>>();   // indice PD globale → tag da togliere
        void Drop(int g, string t) { if (!remove.TryGetValue(g, out var s)) remove[g] = s = new(); s.Add(t); }
        bool Dropped(int g, string t) => remove.TryGetValue(g, out var s) && s.Contains(t);
        int rwy = -1;
        foreach (Match h in Regex.Matches(phd, @"<PHD Num=""\d+"">(.*?)</PHD>", RegexOptions.Singleline))
        {
            string b = h.Groups[1].Value;
            int G(string t) { var x = Regex.Match(b, $"<{t}>(-?\\d+)</{t}>"); return x.Success ? int.Parse(x.Groups[1].Value, Ci) : -1; }
            int type = G("Type"), first = G("FirstPtIdx"), n = G("PointCount");
            if (type == 1) rwy++;
            if (first < 0 || n <= 0) continue;
            for (int i = 0; i < n; i++)
            {
                int? br = Tag(first + i, "BranchIdx");
                if (br is int bi && !Dropped(first + i, "BranchIdx") && !(bi > i && bi < n && Tag(first + bi, "RootIdx") == i))
                {
                    Drop(first + i, "BranchIdx");
                    if (bi >= 0 && bi < n && Tag(first + bi, "RootIdx") == i) Drop(first + bi, "RootIdx");
                    fixes.Add($"{ocdName} list {(type == 1 ? $"runway {rwy}" : $"type {type}")}: branch {i} → {bi}");
                }
                int? ro = Tag(first + i, "RootIdx");
                if (ro is int ri && !Dropped(first + i, "RootIdx") && !(ri < i && ri >= 0 && Tag(first + ri, "BranchIdx") == i))
                {
                    Drop(first + i, "RootIdx");
                    if (ri >= 0 && ri < n && Tag(first + ri, "BranchIdx") == i) Drop(first + ri, "BranchIdx");
                    fixes.Add($"{ocdName} list {(type == 1 ? $"runway {rwy}" : $"type {type}")}: point {i} root {ri}");
                }
            }
        }
        if (remove.Count == 0) return pdx;
        var sb = new StringBuilder(); int last = 0;
        for (int g = 0; g < blocks.Count; g++)
        {
            if (!remove.TryGetValue(g, out var tags)) continue;
            var m = blocks[g];
            sb.Append(pdx, last, m.Index - last);
            string v = m.Value;
            foreach (var t in tags) v = Regex.Replace(v, $@"\s*<{t}>-?\d+</{t}>", "");
            sb.Append(v);
            last = m.Index + m.Length;
        }
        sb.Append(pdx, last, pdx.Length - last);
        return sb.ToString();
    }

    private static readonly Regex RunwayPiece = new(@"\b(rwy|runway|stopway|thr|tdz|qfu)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NotPavement = new(@"light|papi|vasi|sign|\bils\b|qfu|marker|approach", RegexOptions.IgnoreCase);
    private static readonly Regex NotRunwayPiece = new(@"light|papi|vasi|sign|\bils\b|marker|approach", RegexOptions.IgnoreCase);
    private static readonly Regex ThrLights = new(@"lights? thr|thr lights?|threshold light", RegexOptions.IgnoreCase);
    private static readonly Regex AirportPrefix = new(@"^([A-Z]{4}) ");
    /// <summary>Minuterie di una base che si possono togliere se servono posti (max 255 elementi).</summary>
    private static readonly Regex Clutter = new(@"fire ext|boarding ladder|forklift|tow tractor|towing veh|weapon trailer|weapon loader|gse|floodlight|fence|tree|sport|bench|billboard", RegexOptions.IgnoreCase);

    private const int GenThr = 4241, GenTdz = 4244, GenQfu = 1410;   // DPRK Gen Rwy THR / TDZ, Runway QFU XX
    private const double GenPiece = 2052;

    /// <summary>
    /// Pista generica della 4.38 (OCD 805-808): pezzi da 2052 ft, testata, zone di contatto, testata, con i numeri (QFU)
    /// vicino al bordo esterno delle testate. Qui la si adatta alla lunghezza della lista di pista della disposizione (altrimenti l'aereo nasce prima
    /// dell'asfalto, o le luci di soglia finiscono in mezzo alla pista).
    /// </summary>
    private static List<(int Ct, double S, bool Flip, int? Value)> GenericRunway(double lo, double hi)
    {
        // lo..hi = dove deve stare l'asfalto lungo l'asse (dal centro della lista di pista). I pezzi vanno uno dopo l'altro
        // senza sovrapporsi (pezzi sovrapposti sfarfallano): l'asfalto e' centrato su lo..hi e puo' superarlo fino a 1026 ft
        // per parte.
        int n = Math.Max(4, (int)Math.Ceiling((hi - lo) / GenPiece - 0.01));
        double half = n * GenPiece / 2, mid = (hi + lo) / 2;
        var list = new List<(int, double, bool, int?)> { (GenQfu, mid - (half - 216), true, null) };
        for (int i = 0; i < n; i++)
        {
            double sAlong = -half + GenPiece * (i + 0.5);
            bool end = i == 0 || i == n - 1;
            list.Add((end ? GenThr : GenTdz, mid + sAlong, sAlong > 0, end ? 90 : 100));
        }
        list.Add((GenQfu, mid + half - 216, false, null));
        return list;
    }

    /// <summary>
    /// Nel vecchio terreno molte piste del Mideast erano dipinte sulle tessere del suolo: i pezzi 3D sono pochi o sono pezzi
    /// di altri aeroporti coreani (es. "ZKPY ...") messi di traverso. Per ogni pista della disposizione (liste PHD di tipo 8)
    /// che i pezzi di asfalto coprono per meno di meta' si mette la pista generica della 4.38, e si tolgono i vecchi pezzi
    /// di pista che non stanno su una pista rimasta (con le altre parti dello stesso aeroporto coreano: raccordi, luci).
    /// Gli elementi tolti vengono riusati per i pezzi nuovi (il numero di elementi non scende mai).
    /// </summary>
    /// <param name="force">base coreana con i numeri di pista scritti nei modelli e girata: tutte le piste diventano generiche
    /// (numeri dalla direzione), si tolgono solo i pezzi di pista sugli assi (vie di rullaggio e il resto restano)</param>
    private bool AddMissingRunways(string ocdDir, List<string> log, bool force = false)
    {
        var fed = Directory.EnumerateFiles(ocdDir, "FED_*.XML").FirstOrDefault();
        if (fed == null) return false;
        var runways = LayoutRunways(ocdDir).Where(r => r.Len >= 3000).ToList();
        if (runways.Count == 0) return false;
        string xml = File.ReadAllText(fed);
        var blocks = Regex.Matches(xml, @"<FED Num=""(\d+)"">(.*?)</FED>", RegexOptions.Singleline).Cast<Match>().ToList();
        var feats = new List<(int Num, string Name, double N, double E)>();
        foreach (var m in blocks)
        {
            string b = m.Groups[2].Value;
            var c = Regex.Match(b, @"<FeatureCtIdx>(\d+)</FeatureCtIdx>"); var x = Regex.Match(b, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(b, @"<OffsetY>(.*?)</OffsetY>");
            if (c.Success && x.Success && y.Success)
                feats.Add((int.Parse(m.Groups[1].Value, Ci), _featureName(int.Parse(c.Groups[1].Value, Ci)), double.Parse(y.Groups[1].Value, Ci), double.Parse(x.Groups[1].Value, Ci)));
        }

        bool OnAxis((double X, double Y, double Hdg, double Len, int Num) r, double n, double e)
        {
            double h = r.Hdg * Math.PI / 180, dN = n - r.X, dE = e - r.Y;
            double along = dN * Math.Cos(h) + dE * Math.Sin(h), across = -dN * Math.Sin(h) + dE * Math.Cos(h);
            return Math.Abs(along) < r.Len / 2 + 600 && Math.Abs(across) < 300;
        }

        var replace = new List<((double X, double Y, double Hdg, double Len, int Num) R, double Covered)>();
        var keep = new List<(double X, double Y, double Hdg, double Len, int Num)>();
        foreach (var r in runways)
        {
            // Contano solo i pezzi di asfalto (non luci, PAPI, cartelli, ILS o numeri QFU); ogni pezzo copre ~2052 ft.
            double h = r.Hdg * Math.PI / 180, uN = Math.Cos(h), uE = Math.Sin(h);
            var spans = feats.Where(f => RunwayPiece.IsMatch(f.Name) && !NotPavement.IsMatch(f.Name) && OnAxis(r, f.N, f.E))
                .Select(f => (f.N - r.X) * uN + (f.E - r.Y) * uE)
                .Select(a => (Lo: Math.Max(-r.Len / 2, a - 1026), Hi: Math.Min(r.Len / 2, a + 1026)))
                .Where(a => a.Hi > a.Lo).OrderBy(a => a.Lo).ToList();
            double covered = 0, curLo = double.NaN, curHi = double.NaN;
            foreach (var (lo, hi) in spans)
            {
                if (double.IsNaN(curLo) || lo > curHi) { if (!double.IsNaN(curLo)) covered += curHi - curLo; curLo = lo; curHi = hi; }
                else curHi = Math.Max(curHi, hi);
            }
            if (!double.IsNaN(curLo)) covered += curHi - curLo;
            if (covered >= r.Len * 0.5 && !force) keep.Add(r); else replace.Add((r, covered));
        }
        if (replace.Count == 0) return false;

        // vecchi pezzi da togliere
        bool NearKept(double n, double e) => keep.Any(k => OnAxis(k, n, e));
        var drop = feats.Where(f => RunwayPiece.IsMatch(f.Name) && !NotRunwayPiece.IsMatch(f.Name)
                                    && (replace.Any(r => OnAxis(r.R, f.N, f.E)) || !force && !NearKept(f.N, f.E))).ToList();
        if (!force)
        {
            var prefixes = drop.Select(f => AirportPrefix.Match(f.Name)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();
            drop.AddRange(feats.Where(f => !drop.Contains(f) && !NearKept(f.N, f.E)
                                            && AirportPrefix.Match(f.Name) is { Success: true } m && prefixes.Contains(m.Groups[1].Value)));
        }
        var dropNums = drop.Select(f => f.Num).OrderBy(n => n).ToList();

        // pezzi nuovi
        var pieces = new List<string>();
        var pieceRunway = new List<(int Num, int Ct)>();   // per ogni pezzo nuovo: pista e tipo (nell'ordine lungo l'asse)
        foreach (var (r, covered) in replace)
        {
            double h = r.Hdg * Math.PI / 180, uN = Math.Cos(h), uE = Math.Sin(h);
            // L'asfalto arriva alle luci di soglia della disposizione, se ci sono sull'asse poco oltre la fine della lista
            // (nel vecchio terreno la pista dipinta arrivava li'); altrimenti 100 ft oltre la lista (lo spawn e' in fondo).
            double lo = -r.Len / 2 - 100, hi = r.Len / 2 + 100;
            foreach (var f in feats.Where(f => ThrLights.IsMatch(f.Name)))
            {
                double dN = f.N - r.X, dE = f.E - r.Y, al = dN * uN + dE * uE, ac = -dN * uE + dE * uN;
                if (Math.Abs(ac) > 200) continue;
                if (al < -r.Len / 2 && al > -r.Len / 2 - 800) lo = Math.Min(lo, al);
                if (al > r.Len / 2 && al < r.Len / 2 + 800) hi = Math.Max(hi, al);
            }
            foreach (var (ct, sAlong, flip, value) in GenericRunway(lo, hi))
            {
                double n = r.X + sAlong * uN, e = r.Y + sAlong * uE, hd = (r.Hdg + (flip ? 180 : 0)) % 360;
                var sb = new StringBuilder();
                sb.Append($"\r\n        <FeatureCtIdx>{ct}</FeatureCtIdx>\r\n");
                if (value != null) sb.Append($"        <Value>{value}</Value>\r\n");
                sb.Append($"        <OffsetX>{e.ToString("0.000", Ci)}</OffsetX>\r\n        <OffsetY>{n.ToString("0.000", Ci)}</OffsetY>\r\n");
                sb.Append($"        <OffsetZ>0.000</OffsetZ>\r\n        <Heading>{hd.ToString("0.000", Ci)}</Heading>\r\n    ");
                pieces.Add(sb.ToString());
                pieceRunway.Add((r.Num, ct));
            }
            log.Add($"{Path.GetFileName(ocdDir)}: runway {r.Hdg:0}/{(r.Hdg + 180) % 360:0}, {r.Len:0} ft (old pieces covered {covered:0} ft)");
        }
        // BMS: al massimo 255 elementi per disposizione. Per le basi coreane piene si liberano posti togliendo minuterie
        // (estintori, scalette, carrelli, fari, recinzioni...): i loro elementi diventano i pezzi di pista nuovi
        int total = blocks.Count + Math.Max(0, pieces.Count - drop.Count);
        if (total > 255 && force)
        {
            var clutter = feats.Where(f => !drop.Contains(f) && Clutter.IsMatch(f.Name)).Take(total - 255).ToList();
            if (clutter.Count == total - 255)
            {
                drop.AddRange(clutter);
                dropNums = drop.Select(f => f.Num).OrderBy(n => n).ToList();
                log.Add($"{Path.GetFileName(ocdDir)}:   to stay within 255 pieces removed: {string.Join(", ", clutter.Select(c => c.Name))}");
                total = 255;
            }
        }
        if (total > 255)
        {
            log.Add($"{Path.GetFileName(ocdDir)}: not done, it would have {total} pieces (BMS max 255)");
            return false;
        }
        if (drop.Count > 0)
            log.Add($"{Path.GetFileName(ocdDir)}:   old pieces replaced: {string.Join(", ", drop.GroupBy(f => f.Name).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key))}");

        // gli elementi tolti prendono i pezzi nuovi; se avanzano, ripetono l'ultimo pezzo sepolto 2000 ft sotto terra
        // (stessa posizione = sfarfallio; il numero di elementi non deve scendere)
        var bodyFor = new Dictionary<int, string>();
        for (int i = 0; i < dropNums.Count; i++)
            bodyFor[dropNums[i]] = i < pieces.Count ? pieces[i]
                : Regex.Replace(pieces[^1], "<OffsetZ>.*?</OffsetZ>", "<OffsetZ>2000.000</OffsetZ>");
        var sbx = new StringBuilder();
        int last = 0;
        foreach (var m in blocks)
        {
            int num = int.Parse(m.Groups[1].Value, Ci);
            if (!bodyFor.TryGetValue(num, out var body)) continue;
            sbx.Append(xml, last, m.Groups[2].Index - last).Append(body);
            last = m.Groups[2].Index + m.Groups[2].Length;
        }
        sbx.Append(xml, last, xml.Length - last);
        xml = sbx.ToString();

        int next = blocks.Count;
        var add = new StringBuilder();
        foreach (var body in pieces.Skip(dropNums.Count))
            add.Append($"    <FED Num=\"{next++}\">").Append(body).Append("</FED>\r\n");
        if (add.Length > 0)
        {
            int end = xml.LastIndexOf("</FEDRecords>", StringComparison.Ordinal);
            if (end < 0) return false;
            xml = xml[..end] + add + xml[end..];
        }
        File.WriteAllText(fed, xml, new UTF8Encoding(false));

        // numeri FED dei pezzi nuovi, nell'ordine in cui sono stati generati (prima gli elementi riusati, poi gli aggiunti)
        var pieceNum = new List<int>();
        for (int i = 0; i < pieces.Count; i++) pieceNum.Add(i < dropNums.Count ? dropNums[i] : blocks.Count + (i - dropNums.Count));
        var byRunway = replace.ToDictionary(t => t.R.Num, t => (Hdg: t.R.Hdg, Feds: pieceRunway.Select((pr, i) => (pr, i)).Where(x => x.pr.Num == t.R.Num).Select(x => (Ct: x.pr.Ct, Num: pieceNum[x.i])).ToList()));
        PatchRunwayHeaders(ocdDir, byRunway);
        _genericRunways[Path.GetFileName(ocdDir)] = byRunway.Keys.ToHashSet();
        return true;
    }

    private readonly Dictionary<string, HashSet<int>> _genericRunways = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Intestazioni delle piste (PHD di tipo 1, una per senso di atterraggio, RunwayNumber = pista): FeatureDependencyIdx
    /// sono gli elementi della pista (danni) e il primo e' il numero di pista (QFU) della testata dove si atterra, come nelle
    /// basi generiche della 4.38 (OCD 805-808). Per le piste generiche aggiunte si rimettono i pezzi nuovi.
    /// </summary>
    private static void PatchRunwayHeaders(string ocdDir, Dictionary<int, (double Hdg, List<(int Ct, int Num)> Feds)> runways)
    {
        var phd = Directory.EnumerateFiles(ocdDir, "PHD_*.XML").FirstOrDefault();
        if (phd == null) return;
        string xml = File.ReadAllText(phd);
        xml = Regex.Replace(xml, @"(<PHD Num=""\d+"">)(.*?)(</PHD>)", m =>
        {
            string b = m.Groups[2].Value;
            string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
            if (G("Type") != "1" || !int.TryParse(G("RunwayNumber"), NumberStyles.Integer, Ci, out int rn) || !runways.TryGetValue(rn, out var rw)) return m.Value;
            double data = double.TryParse(G("Data"), NumberStyles.Float, Ci, out var d) ? d : rw.Hdg;
            double diff = ((data - rw.Hdg) % 360 + 540) % 360 - 180;
            var feds = Math.Abs(diff) <= 90 ? rw.Feds : Enumerable.Reverse(rw.Feds).ToList();   // dalla testata d'atterraggio
            // al massimo 6 dipendenze (come nelle basi della 4.38): QFU e testata vicine, testata e QFU lontane, due centrali
            var dep = new List<int>();
            if (feds.Count <= 6) dep.AddRange(feds.Select(f => f.Num));
            else dep.AddRange(new[] { feds[0].Num, feds[1].Num, feds[feds.Count / 2 - 1].Num, feds[feds.Count / 2].Num, feds[^2].Num, feds[^1].Num });
            b = Regex.Replace(b, @"\s*<FeatureDependencyIdx_\d+>-?\d+</FeatureDependencyIdx_\d+>", "");
            var ins = new StringBuilder();
            for (int i = 0; i < dep.Count; i++) ins.Append($"\r\n        <FeatureDependencyIdx_{i}>{dep[i]}</FeatureDependencyIdx_{i}>");
            b = Regex.Replace(b, @"(<PointCount>-?\d+</PointCount>)", "$1" + ins.ToString().Replace("$", "$$"));
            return m.Groups[1].Value + b + m.Groups[3].Value;
        }, RegexOptions.Singleline);
        File.WriteAllText(phd, xml, new UTF8Encoding(false));
    }

    /// <summary>Declinazione magnetica media del teatro (gradi, est positivo): i numeri di pista sono magnetici.</summary>
    public double MagneticVariation { get; set; } = 3;

    /// <summary>
    /// Numero dipinto sul pezzo QFU = RunwayTexture dell'intestazione = prua magnetica / 10 (basi generiche 4.38: 44.8 vero + 8 W = 05). Serve la prua nel mondo: direzione nella disposizione + rotazione
    /// dell'obiettivo (se piu' basi usano la stessa disposizione con rotazioni diverse, la mediana).
    /// </summary>
    private int SetRunwayNumbers(string outOrd, List<CampObj> objs)
    {
        int n = 0;
        foreach (var (dirName, nums) in _genericRunways)
        {
            string od = Path.Combine(outOrd, dirName);
            if (!int.TryParse(dirName[4..], NumberStyles.Integer, Ci, out int ocd)) continue;
            var rots = objs.Where(o => o.Ocd == ocd).Select(o => ((o.Heading % 360) + 540) % 360 - 180).OrderBy(x => x).ToList();
            double rot = rots.Count > 0 ? rots[rots.Count / 2] : 0;
            var rws = LayoutRunways(od).ToDictionary(r => r.Num, r => r.Hdg);
            var phd = Directory.EnumerateFiles(od, "PHD_*.XML").FirstOrDefault();
            if (phd == null) continue;
            string xml = File.ReadAllText(phd);
            xml = Regex.Replace(xml, @"(<PHD Num=""\d+"">)(.*?)(</PHD>)", m =>
            {
                string b = m.Groups[2].Value;
                string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
                if (G("Type") != "1" || !int.TryParse(G("RunwayNumber"), NumberStyles.Integer, Ci, out int rn) || !nums.Contains(rn) || !rws.TryGetValue(rn, out var axis)) return m.Value;
                double data = double.TryParse(G("Data"), NumberStyles.Float, Ci, out var d) ? d : axis;
                double land = Math.Abs(((data - axis) % 360 + 540) % 360 - 180) <= 90 ? axis : axis + 180;
                double mag = ((land + rot - MagneticVariation) % 360 + 360) % 360;
                int num = (int)Math.Round(mag / 10); if (num <= 0) num += 36; if (num > 36) num -= 36;
                n++;
                // RunwayTexture = numero (come nelle basi generiche della 4.38). Il "34/16" visto a Kuwait il 20/10 veniva dalla
                // rotazione mediana delle 3 basi che usano la OCD 189 (Kuwait 10.8, Dhahran 19.9, Prince Sultan 31.3), non da uno sfasamento.
                return m.Groups[1].Value + Regex.Replace(b, "<RunwayTexture>-?\\d+</RunwayTexture>", $"<RunwayTexture>{num}</RunwayTexture>") + m.Groups[3].Value;
            }, RegexOptions.Singleline);
            File.WriteAllText(phd, xml, new UTF8Encoding(false));
        }
        return n;
    }

    private Dictionary<string, int> _ctByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cartelle OCD delle basi coreane copiate → OCD coreana di origine.</summary>
    private readonly Dictionary<string, int> _swappedDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _swapVar = new(StringComparer.OrdinalIgnoreCase);
    private double SwapVar(string dn) => _swapVar.TryGetValue(dn, out double v) ? v : KoreaVariation;

    /// <summary>Ponti con il punto spostato nel km del fiume: CampId → spostamento (nord, est) in piedi.</summary>
    private readonly Dictionary<int, (double DN, double DE)> _bridgeShift = new();

    /// <summary>
    /// BMS 4.38 segna come ponte, nella mappa della campagna, solo il km in cui cade il punto dell'obiettivo ponte
    /// (celle di 3279.98 ft); una cella di fiume si attraversa solo li'. Se il punto cade nel km accanto
    /// al fiume, i collegamenti che passano il ponte restano bloccati. Qui, con la mappa della campagna che BMS ha gia' scritto
    /// (Campaign\*.terrain.binpb dell'add-on) e i collegamenti del modello del TE, si sposta il punto dei ponti (entro 2 km) dove
    /// sblocca collegamenti senza bloccarne altri. I pezzi restano dove sono: la disposizione si sposta al contrario (ShiftBridgeLayouts).
    /// </summary>
    private Dictionary<int, int> PlanBridgeCells(TextureContext c, List<CampObj> objs, Dictionary<int, int> removed, Dictionary<int, int> ocdType,
        string dst, string outOrd, List<string> notes)
    {
        var res = new Dictionary<int, int>();
        var p = c.Project;
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string? data = TerrainTool.Core.Campaign.CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain);
        string? bmsCamp = data != null ? Path.Combine(data, addOn, "Campaign") : null;
        string? cache = bmsCamp != null && Directory.Exists(bmsCamp) ? Directory.EnumerateFiles(bmsCamp, "*.terrain.binpb").FirstOrDefault() : null;
        // l'installazione degli oggetti cancella la cache di BMS (la rifa' BMS al primo avvio): se manca, la copia che
        // l'installazione salva nel progetto. Senza nessuna delle due le copie dei ponti (OCD/CT nuovi) non si fanno e la
        // campagna, che le usa, manderebbe BMS in crash (l'installazione lo blocca).
        if (cache == null && Directory.Exists(KeptTerrainCacheFolder(p)))
        {
            cache = Directory.EnumerateFiles(KeptTerrainCacheFolder(p), "*.terrain.binpb").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (cache != null) notes.Add($"Bridge cells: BMS campaign terrain missing, using the copy kept by the last install ({cache}, {File.GetLastWriteTime(cache):yyyy-MM-dd HH:mm})");
        }
        if (cache == null)
        {
            notes.Add("Bridge cells: no campaign terrain of BMS (*.terrain.binpb in the add-on's Campaign folder) and no copy in " + KeptTerrainCacheFolder(p) +
                      ": bridge layout copies NOT made. If the campaign files already use them, 'Install objects' will refuse to install. Start a campaign of the theater once in BMS, then Run Objects again");
            return res;
        }
        string? Find(string? dir) => dir != null && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("Te_New_Nt.tac", StringComparison.OrdinalIgnoreCase))
              ?? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("te_new.tac", StringComparison.OrdinalIgnoreCase))
            : null;
        string? tpl = Find(CampaignEditsFolder(p)) ?? Find(Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign")) ?? Find(bmsCamp) ?? Find(Path.Combine(p.OldTheaterFolder, "Campaign"));
        if (tpl == null) { notes.Add("Bridge cells: no Te_New_Nt.tac (links of the theater) found"); return res; }
        TerrainTool.Core.Campaign.CampaignTerrainCache map;
        List<(int A, int B)> links;
        try
        {
            map = TerrainTool.Core.Campaign.CampaignTerrainCache.Read(cache);
            links = TerrainTool.Core.Campaign.CampaignTerrainCheck.GroundLinks(TerrainTool.Core.Campaign.CampaignDoc.Load(tpl, _ => null, new[] { Path.GetDirectoryName(tpl)! }));
        }
        catch (Exception ex) { notes.Add("Bridge cells: " + ex.Message); return res; }
        static (int, int) CellOf(CampObj o) => (TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(o.Y), TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(o.X));
        var cell = objs.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => CellOf(g.First()));
        var bridges = objs.Where(o => !removed.ContainsKey(o.Id) && ocdType.GetValueOrDefault(o.Ocd, -1) == 6).Select(o => o.Id).ToHashSet();
        var before = links.Count(l => cell.ContainsKey(l.A) && cell.ContainsKey(l.B) && !TerrainTool.Core.Campaign.CampaignTerrainCheck.Reachable(map,
            cell[l.A].Item1, cell[l.A].Item2, cell[l.B].Item1, cell[l.B].Item2, (x, y) => bridges.Any(b => cell[b] == (x, y))));
        var plan = TerrainTool.Core.Campaign.BridgeCellPlanner.Plan(map, links, cell, bridges);
        notes.Add($"Bridge cells (campaign terrain {Path.GetFileName(cache)} of {File.GetLastWriteTime(cache):yyyy-MM-dd HH:mm}, links of {Path.GetFileName(tpl)}): " +
                  $"{before} ground links blocked by water, {plan.Count} bridges moved into the km of the crossing (pieces stay where they are), " +
                  $"{plan.Sum(s => s.Fixed)} links unblocked");
        if (plan.Count == 0) return res;
        const double G = TerrainTool.Core.Campaign.CampaignDoc.BmsFtPerGrid, M = 100;
        var ids = plan.Select(s => s.CampId).ToHashSet();
        foreach (var kv in CloneSharedLayouts(objs, ids, dst, outOrd, notes, "bridge cells")) res[kv.Key] = kv.Value;
        foreach (var s in plan)
        {
            int i = objs.FindIndex(o => o.Id == s.CampId);
            if (i < 0) continue;
            var o = objs[i];
            double n = Math.Clamp(o.X, s.ToY * G + M, (s.ToY + 1) * G - M), e = Math.Clamp(o.Y, s.ToX * G + M, (s.ToX + 1) * G - M);
            _bridgeShift[o.Id] = (n - o.X, e - o.Y);
            objs[i] = o with { X = n, Y = e };
            notes.Add($"   {o.Name} (CampId {o.Id}): point moved {Math.Sqrt((n - o.X) * (n - o.X) + (e - o.Y) * (e - o.Y)) * 0.3048:0} m " +
                      $"(cell {s.FromX},{s.FromY} → {s.ToX},{s.ToY}), {s.Fixed} link(s) unblocked");
        }
        return res;
    }

    /// <summary>
    /// Disposizioni dei ponti spostati da PlanBridgeCells: pezzi (FED) e punti (PD) spostati al contrario, nel riferimento della disposizione
    /// (dopo BakeRotations, quindi con la direzione finale), cosi' in BMS restano dove erano. L'editor usa la copia di prima (Output\EditorLayouts)
    /// e bridge_cells.csv per mostrare il ponte nel punto di prima.
    /// </summary>
    private void ShiftBridgeLayouts(TextureContext c, List<CampObj> objs, string outOrd, List<string> notes)
    {
        string keep = BakedLayouts.Folder(c.Project.ProjectFolder);
        Directory.CreateDirectory(keep);
        var csv = new StringBuilder("CampId;Ocd;DNorthFt;DEastFt\n");
        int n = 0;
        foreach (var o in objs)
        {
            if (!_bridgeShift.TryGetValue(o.Id, out var d)) continue;
            string dir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            if (!Directory.Exists(dir)) continue;
            string save = Path.Combine(keep, $"OCD_{o.Ocd:00000}");
            if (!Directory.Exists(save))
            {
                Directory.CreateDirectory(save);
                foreach (var f in Directory.EnumerateFiles(dir)) File.Copy(f, Path.Combine(save, Path.GetFileName(f)), true);
            }
            double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            double dn = d.DN * ch + d.DE * sh, de = -d.DN * sh + d.DE * ch;   // spostamento nel riferimento della disposizione
            foreach (var f in Directory.EnumerateFiles(dir, "*.XML"))
            {
                string fn = Path.GetFileName(f).ToUpperInvariant(), xml = File.ReadAllText(f), r = xml;
                if (fn.StartsWith("FED_")) r = BakedLayouts.TranslateRecords(xml, "FED", dn, de);
                else if (fn.StartsWith("PDX_")) r = BakedLayouts.TranslateRecords(xml, "PD", dn, de);
                if (r != xml) File.WriteAllText(f, r, new UTF8Encoding(false));
            }
            csv.Append(o.Id).Append(';').Append(o.Ocd).Append(';').Append(d.DN.ToString("0.000", Ci)).Append(';').Append(d.DE.ToString("0.000", Ci)).Append('\n');
            n++;
        }
        File.WriteAllText(Path.Combine(keep, BakedLayouts.ShiftCsvName), csv.ToString(), new UTF8Encoding(false));
        notes.Add($"Bridge cells: layouts of {n} bridges moved back so the pieces stay in place");
    }

    /// <summary>
    /// Pezzi toccati a mano (piece_edits.csv) nella disposizione propria dell'obiettivo: posizione (OffsetX = est, OffsetY = nord),
    /// direzione; i pezzi tolti si seppelliscono (OffsetZ 2000, come le piste vecchie), cosi' il numero dei pezzi non cambia.
    /// </summary>
    private static void ApplyPieceEdits(List<CampObj> objs, HashSet<int> ids, Dictionary<int, Dictionary<int, TerrainTool.Core.Editor.PieceEdit>> edits,
        string outOrd, List<string> notes)
    {
        var lines = new List<string>();
        int moved = 0, gone = 0;
        foreach (var o in objs.Where(o => ids.Contains(o.Id)))
        {
            string dir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            string? fed = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault() : null;
            if (fed == null) { lines.Add($"{o.Name}: layout OCD {o.Ocd} not found, pieces not changed"); continue; }
            var list = edits[o.Id];
            string xml = File.ReadAllText(fed);
            int m0 = 0, g0 = 0, miss = 0;
            var done = new HashSet<int>();
            string res = Regex.Replace(xml, @"(<FED Num=""(\d+)"">)(.*?)(</FED>)", m =>
            {
                int num = int.Parse(m.Groups[2].Value, Ci);
                if (!list.TryGetValue(num, out var e) || e.IsNoOp) return m.Value;
                done.Add(num);
                string b = m.Groups[3].Value;
                if (e.Removed) { g0++; b = Regex.Replace(b, "<OffsetZ>.*?</OffsetZ>", "<OffsetZ>2000.000</OffsetZ>"); }
                else
                {
                    m0++;
                    b = Regex.Replace(b, "<OffsetX>.*?</OffsetX>", $"<OffsetX>{e.E.ToString("0.000", Ci)}</OffsetX>");
                    b = Regex.Replace(b, "<OffsetY>.*?</OffsetY>", $"<OffsetY>{e.N.ToString("0.000", Ci)}</OffsetY>");
                    b = Regex.Replace(b, "<Heading>.*?</Heading>", $"<Heading>{e.Heading.ToString("0.0##", Ci)}</Heading>");
                }
                return m.Groups[1].Value + b + m.Groups[4].Value;
            }, RegexOptions.Singleline);
            miss = list.Values.Count(e => !e.IsNoOp && !done.Contains(e.Num));
            if (!ReferenceEquals(res, xml) && res != xml) File.WriteAllText(fed, res, new UTF8Encoding(false));
            moved += m0; gone += g0;
            lines.Add($"{o.Name}: {m0} pieces moved/turned, {g0} removed" + (miss > 0 ? $", {miss} not found in the layout (it changed?)" : ""));
        }
        notes.Add($"Pieces edited by hand (piece_edits.csv): {moved} moved/turned, {gone} removed, in {lines.Count} objectives:");
        foreach (var l in lines) notes.Add("   " + l);
    }

    /// <summary>Punti della disposizione (PDX) spostati a mano (point_edits.csv): posizione (OffsetX = est, OffsetY = nord) e direzione.</summary>
    private static void ApplyPointEdits(List<CampObj> objs, HashSet<int> ids, Dictionary<int, Dictionary<int, TerrainTool.Core.Editor.PointEdit>> edits,
        string outOrd, List<string> notes)
    {
        if (ids.Count == 0) return;
        var lines = new List<string>();
        int total = 0;
        foreach (var o in objs.Where(o => ids.Contains(o.Id)))
        {
            string dir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            string? pdx = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PDX_*.XML").FirstOrDefault() : null;
            if (pdx == null) { lines.Add($"{o.Name}: layout OCD {o.Ocd} has no points, not changed"); continue; }
            var list = edits[o.Id];
            string xml = File.ReadAllText(pdx);
            int n0 = 0;
            var done = new HashSet<int>();
            string res = Regex.Replace(xml, @"(<PD Num=""(\d+)"">)(.*?)(</PD>)", m =>
            {
                int num = int.Parse(m.Groups[2].Value, Ci);
                if (!list.TryGetValue(num, out var e) || e.IsNoOp) return m.Value;
                done.Add(num); n0++;
                string b = m.Groups[3].Value;
                b = Regex.Replace(b, "<OffsetX>.*?</OffsetX>", $"<OffsetX>{e.E.ToString("0.000", Ci)}</OffsetX>");
                b = Regex.Replace(b, "<OffsetY>.*?</OffsetY>", $"<OffsetY>{e.N.ToString("0.000", Ci)}</OffsetY>");
                if (b.Contains("<Heading>")) b = Regex.Replace(b, "<Heading>.*?</Heading>", $"<Heading>{e.Heading.ToString("0.000", Ci)}</Heading>");
                return m.Groups[1].Value + b + m.Groups[4].Value;
            }, RegexOptions.Singleline);
            int miss = list.Values.Count(e => !e.IsNoOp && !done.Contains(e.Num));
            if (res != xml) File.WriteAllText(pdx, res, new UTF8Encoding(false));
            total += n0;
            lines.Add($"{o.Name}: {n0} points moved" + (miss > 0 ? $", {miss} not found in the layout (it changed?)" : ""));
        }
        notes.Add($"Points (vehicle positions) moved by hand (point_edits.csv): {total} in {lines.Count} objectives:");
        foreach (var l in lines) notes.Add("   " + l);
    }

    /// <summary>Obiettivi la cui rotazione va dentro la disposizione: CampId → (OCD, gradi).</summary>
    private readonly Dictionary<int, (int Ocd, double Deg)> _bake = new();

    /// <summary>Cambiati da questo lavoro, girati (direzione diversa da 0) e con una disposizione usata solo da loro (le basi spostate ne hanno una copia propria).</summary>
    private void PlanBake(HashSet<int> changed, List<CampObj> objs, Dictionary<int, int> removed, string outOrd)
    {
        _bake.Clear();
        var use = objs.GroupBy(o => o.Ocd).ToDictionary(g => g.Key, g => g.Count());
        foreach (var o in objs)
        {
            double h = ((o.Heading % 360) + 540) % 360 - 180;
            if (!changed.Contains(o.Id) || removed.ContainsKey(o.Id) || Math.Abs(h) < 0.01 || use.GetValueOrDefault(o.Ocd) != 1) continue;
            if (!Directory.Exists(Path.Combine(outOrd, $"OCD_{o.Ocd:00000}"))) continue;
            _bake[o.Id] = (o.Ocd, o.Heading);
        }
    }

    /// <summary>
    /// Mette la rotazione dell'obiettivo dentro la sua disposizione: posizioni e direzioni dei pezzi (FED), punti e loro direzioni (PDX),
    /// direzione delle piste (PHD tipo 1 e 8; tipo 14 se indicata); la direzione dell'obiettivo diventa 0. In BMS la base resta
    /// identica, ma anche il Recon la mostra giusta. Le disposizioni non girate restano in Output\EditorLayouts per l'editor degli obiettivi
    /// (che lavora sempre con la direzione vera: baked.csv dice quale).
    /// </summary>
    private void BakeRotations(TextureContext c, List<CampObj> objs, string outOrd, List<string> notes)
    {
        string keep = BakedLayouts.Folder(c.Project.ProjectFolder);
        if (Directory.Exists(keep)) Directory.Delete(keep, true);
        Directory.CreateDirectory(keep);
        var csv = new StringBuilder("CampId;Ocd;Heading\n");
        int n = 0;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (!_bake.TryGetValue(o.Id, out var b) || b.Ocd != o.Ocd) continue;
            string dir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            if (!Directory.Exists(dir)) continue;
            string save = Path.Combine(keep, $"OCD_{o.Ocd:00000}");
            Directory.CreateDirectory(save);
            foreach (var f in Directory.EnumerateFiles(dir)) File.Copy(f, Path.Combine(save, Path.GetFileName(f)), true);
            foreach (var f in Directory.EnumerateFiles(dir, "*.XML"))
            {
                string fn = Path.GetFileName(f).ToUpperInvariant(), xml = File.ReadAllText(f), res = xml;
                if (fn.StartsWith("FED_")) res = BakedLayouts.RotateRecords(xml, "FED", b.Deg, addHeading: true);
                else if (fn.StartsWith("PDX_")) res = BakedLayouts.RotateRecords(xml, "PD", b.Deg, addHeading: false);
                else if (fn.StartsWith("PHD_")) res = BakedLayouts.RotateRunwayHeadings(xml, b.Deg);
                if (!ReferenceEquals(res, xml) && res != xml) File.WriteAllText(f, res, new UTF8Encoding(false));
            }
            csv.Append(o.Id).Append(';').Append(o.Ocd).Append(';').Append(b.Deg.ToString("0.000", Ci)).Append('\n');
            objs[i] = o with { Heading = 0 };
            n++;
        }
        File.WriteAllText(Path.Combine(keep, BakedLayouts.CsvName), csv.ToString(), new UTF8Encoding(false));
        notes.Add($"Turned objectives: rotation put inside their own layout for {n} objectives (heading 0 in the campaign files; " +
                  "the Recon preview of BMS does not turn the pieces of a turned objective correctly)");
    }
    /// <summary>Declinazione magnetica della Korea (gradi, est positivo): i numeri delle basi coreane sono calcolati con questa.</summary>
    private const double KoreaVariation = -8;

    /// <summary>
    /// Basi sostituite con una base coreana completa (base_swaps.csv): per ognuna una cartella OCD nuova con la disposizione
    /// della 4.38 (FED/PDX/PHD/OCD dalla cartella di BMS, sola lettura; niente alberi OTD) e una voce CT nuova copiata da
    /// quella della base coreana. Aggiorna objs (Ocd) e restituisce CampId → (nuovo indice CT, numero di pezzi) per i file
    /// della campagna. Gli altri obiettivi coreani della stessa base (edifici in piu') non vengono copiati.
    /// </summary>
    private Dictionary<int, (int Ct, int Features)> SwapLayouts(Dictionary<int, TerrainTool.Core.Editor.BaseSwap> swaps, List<CampObj> objs,
        Dictionary<int, int> removed, string newOrd, string dst, string outOrd, List<string> notes, string? theaterOrd = null)
    {
        var res = new Dictionary<int, (int, int)>();
        string ctPath = Path.Combine(dst, "Falcon4_CT.xml");
        string ctXml = File.ReadAllText(ctPath);
        var ctBlocks = Regex.Matches(ctXml, @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline)
            .Cast<Match>().ToDictionary(m => int.Parse(m.Groups[1].Value, Ci), m => m.Groups[2].Value);
        int nextCt = ctBlocks.Keys.Max() + 1;
        int nextOcd = Directory.EnumerateDirectories(outOrd, "OCD_*").Select(d => int.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, Ci, out int n) ? n : -1).Max() + 1;
        var add = new StringBuilder();
        var list = new List<string>();
        foreach (var sw in swaps.Values.OrderBy(s => s.CampId))
        {
            int idx = objs.FindIndex(o => o.Id == sw.CampId);
            if (idx < 0) { list.Add($"#{sw.CampId} {sw.Name}: objective not found, skipped"); continue; }
            var o = objs[idx];
            if (removed.ContainsKey(o.Id)) { list.Add($"{o.Name}: turned into a junction, Korean base not used"); continue; }
            if (sw.FromTheater && theaterOrd == null) { list.Add($"{o.Name}: base of the theater (OCD {sw.RealOcd}) only for installed theaters, skipped"); continue; }
            string src = Path.Combine(sw.FromTheater ? theaterOrd! : newOrd, $"OCD_{sw.RealOcd:00000}");
            string what = sw.FromTheater ? "theater" : "BMS";
            var ocdXml = Directory.Exists(src) ? Directory.EnumerateFiles(src, "OCD_*.XML").FirstOrDefault() : null;
            if (ocdXml == null) { list.Add($"{o.Name}: {what} OCD {sw.RealOcd} not found, skipped"); continue; }
            int srcCt = CtIdx(ocdXml);
            if (!ctBlocks.TryGetValue(srcCt, out var ctBody)) { list.Add($"{o.Name}: CT {srcCt} of {what} OCD {sw.RealOcd} not found, skipped"); continue; }
            int ocd = nextOcd++, ctNum = nextCt++;
            string dir = Path.Combine(outOrd, $"OCD_{ocd:00000}");
            Directory.CreateDirectory(dir);
            int features = 0;
            foreach (var f in Directory.EnumerateFiles(src))
            {
                string fn = Path.GetFileName(f);
                // alberi (OTD): solo per le basi del teatro (ct_idx = CT dell'obiettivo); le coreane restano senza
                bool trees = sw.FromTheater && Regex.IsMatch(fn, @"^OTD_\d{5}_L\d+\.TPB$", RegexOptions.IgnoreCase);
                if (!trees && !Regex.IsMatch(fn, @"^(FED|PDX|PHD|OCD)_\d{5}\.XML$", RegexOptions.IgnoreCase)) continue;
                string t = File.ReadAllText(f);
                if (trees) t = Regex.Replace(t, @"ct_idx:\s*\d+", $"ct_idx: {ctNum}");
                t = Regex.Replace(t, @"<ObjIdx>\d+</ObjIdx>", $"<ObjIdx>{ocd}</ObjIdx>");
                if (fn.StartsWith("OCD_", StringComparison.OrdinalIgnoreCase))
                {
                    t = Regex.Replace(t, @"<OCD Num=""\d+"">", $"<OCD Num=\"{ocd}\">");
                    t = Regex.Replace(t, @"<CtIdx>\d+</CtIdx>", $"<CtIdx>{ctNum}</CtIdx>");
                }
                if (fn.StartsWith("FED_", StringComparison.OrdinalIgnoreCase)) features = Regex.Matches(t, @"<FED Num=""\d+"">").Count;
                File.WriteAllText(Path.Combine(dir, Regex.Replace(fn, @"_\d{5}(?=[._])", $"_{ocd:00000}")), t, new UTF8Encoding(false));
            }
            string body = Regex.Replace(ctBody, @"<EntityIdx>\d+</EntityIdx>", $"<EntityIdx>{ocd}</EntityIdx>");
            add.Append($"    <CT Num=\"{ctNum}\">").Append(body).Append("</CT>\r\n");
            objs[idx] = o with { Ocd = ocd };
            string dn = Path.GetFileName(dir);
            _genericRunways[dn] = LayoutRunways(dir).Select(r => r.Num).ToHashSet();   // numeri di pista dalla direzione nel teatro
            _swappedDirs[dn] = sw.KoreaOcd;
            _swapVar[dn] = sw.FromTheater ? MagneticVariation : KoreaVariation;   // con che declinazione sono numerate le sue piste
            res[o.Id] = (ctNum, features);
            var fed = Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault();
            var fixedNums = fed == null ? new List<string>() : Regex.Matches(File.ReadAllText(fed), @"<FeatureCtIdx>(\d+)</FeatureCtIdx>")
                .Select(m => _featureName(int.Parse(m.Groups[1].Value, Ci)))
                .Where(n => Regex.IsMatch(n, @"\b(thr|threshold)\s+\d{2}[LRC]?\b|\brwy\s+\d{2}", RegexOptions.IgnoreCase) && !n.Contains("XX", StringComparison.OrdinalIgnoreCase) && !n.Contains("Sign", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            list.Add($"{o.Name}: {sw.KoreaName} ({what} OCD {sw.RealOcd}) -> OCD {ocd}, CT {ctNum}, {features} pieces"
                     + (fixedNums.Count > 0 ? $"; runway numbers fixed in the models: {string.Join(", ", fixedNums)}" : ""));
        }
        int end = ctXml.LastIndexOf("</CTRecords>", StringComparison.Ordinal);
        bool bom = File.ReadAllBytes(ctPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        if (end >= 0 && add.Length > 0) File.WriteAllText(ctPath, ctXml[..end] + add + ctXml[end..], new UTF8Encoding(bom));
        notes.Add($"Replaced bases (base_swaps.csv): {res.Count} airbases replaced by a complete base (Korea 4.38 or of this theater):");
        foreach (var l in list) notes.Add("   " + l);
        return res;
    }

    /// <summary>Indice CT dell'obiettivo con questa disposizione (EntityType 3, EntityIdx = OCD) nel Falcon4_CT.xml di dst, o null.</summary>
    private static int? OcdCt(string dst, int ocd)
    {
        string f = Path.Combine(dst, "Falcon4_CT.xml");
        if (!File.Exists(f)) return null;
        foreach (Match m in Regex.Matches(File.ReadAllText(f), @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline))
            if (Regex.IsMatch(m.Groups[2].Value, @"<EntityType>3</EntityType>") && Regex.IsMatch(m.Groups[2].Value, $@"<EntityIdx>{ocd}</EntityIdx>"))
                return int.Parse(m.Groups[1].Value, Ci);
        return null;
    }

    /// <summary>
    /// Disposizioni scelte a mano nell'editor (layout_swaps.csv): per ogni obiettivo una cartella OCD nuova copiata dalla
    /// disposizione scelta (Run Objects per "mideast", cartella della 4.38 per "bms") e una voce CT nuova copiata da quella della
    /// disposizione scelta: il tipo dell'obiettivo diventa quello della disposizione (porto → citta'). Aggiorna objs (Ocd) e
    /// ocdType; restituisce CampId → (nuovo indice CT, numero di pezzi) per i file della campagna (record rifatto, pezzi intatti).
    /// </summary>
    private Dictionary<int, (int Ct, int Features)> ReplaceLayouts(TextureContext c, Dictionary<int, TerrainTool.Core.Editor.LayoutSwap> swaps,
        List<CampObj> objs, Dictionary<int, int> removed, Dictionary<int, (int Ct, int Features)> koreaSwapped, string newOrd, string dst, string outOrd,
        Dictionary<int, int> ocdType, List<string> notes)
    {
        var res = new Dictionary<int, (int, int)>();
        var applied = new List<(int, string, int, int)>();
        if (swaps.Count == 0) { TerrainTool.Core.Editor.LayoutSwaps.SaveApplied(c.Project.ProjectFolder, applied); return res; }
        string ctPath = Path.Combine(dst, "Falcon4_CT.xml");
        string ctXml = File.ReadAllText(ctPath);
        var ctBlocks = Regex.Matches(ctXml, @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline)
            .Cast<Match>().ToDictionary(m => int.Parse(m.Groups[1].Value, Ci), m => m.Groups[2].Value);
        int nextCt = ctBlocks.Keys.Max() + 1;
        int nextOcd = Directory.EnumerateDirectories(outOrd, "OCD_*").Select(d => int.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, Ci, out int n) ? n : -1).Max() + 1;
        var add = new StringBuilder();
        var list = new List<string>();
        foreach (var sw in swaps.Values.OrderBy(s => s.CampId))
        {
            int idx = objs.FindIndex(o => o.Id == sw.CampId);
            if (idx < 0) { list.Add($"#{sw.CampId} {sw.Name}: objective not found, skipped"); continue; }
            var o = objs[idx];
            if (removed.ContainsKey(o.Id)) { list.Add($"{o.Name}: turned into a junction, layout not replaced"); continue; }
            if (koreaSwapped.ContainsKey(o.Id)) { list.Add($"{o.Name}: has a Korean base, layout not replaced"); continue; }
            string src = Path.Combine(sw.Source == TerrainTool.Core.Editor.LayoutSwaps.Bms ? newOrd : outOrd, $"OCD_{sw.Ocd:00000}");
            var ocdXml = Directory.Exists(src) ? Directory.EnumerateFiles(src, "OCD_*.XML").FirstOrDefault() : null;
            if (ocdXml == null) { list.Add($"{o.Name}: layout {sw.Source} OCD {sw.Ocd} not found, skipped"); continue; }
            int srcCt = CtIdx(ocdXml);
            if (!ctBlocks.TryGetValue(srcCt, out var ctBody)) { list.Add($"{o.Name}: CT {srcCt} of OCD {sw.Ocd} not found, skipped"); continue; }
            int ocd = nextOcd++, ctNum = nextCt++;
            string dir = Path.Combine(outOrd, $"OCD_{ocd:00000}");
            Directory.CreateDirectory(dir);
            int features = 0;
            foreach (var f in Directory.EnumerateFiles(src))
            {
                string fn = Path.GetFileName(f);
                if (!Regex.IsMatch(fn, @"^(FED|PDX|PHD|OCD)_\d{5}\.XML$", RegexOptions.IgnoreCase)) continue;
                string t = File.ReadAllText(f);
                t = Regex.Replace(t, @"<ObjIdx>\d+</ObjIdx>", $"<ObjIdx>{ocd}</ObjIdx>");
                if (fn.StartsWith("OCD_", StringComparison.OrdinalIgnoreCase))
                {
                    t = Regex.Replace(t, @"<OCD Num=""\d+"">", $"<OCD Num=\"{ocd}\">");
                    t = Regex.Replace(t, @"<CtIdx>\d+</CtIdx>", $"<CtIdx>{ctNum}</CtIdx>");
                }
                if (fn.StartsWith("FED_", StringComparison.OrdinalIgnoreCase)) features = Regex.Matches(t, @"<FED Num=""\d+"">").Count;
                File.WriteAllText(Path.Combine(dir, Regex.Replace(fn, @"_\d{5}\.", $"_{ocd:00000}.")), t, new UTF8Encoding(false));
            }
            if (features > 255) { Directory.Delete(dir, true); nextOcd--; nextCt--; list.Add($"{o.Name}: {sw.LayoutName} has {features} pieces (BMS max 255), skipped"); continue; }
            string body = Regex.Replace(ctBody, @"<EntityIdx>\d+</EntityIdx>", $"<EntityIdx>{ocd}</EntityIdx>");
            add.Append($"    <CT Num=\"{ctNum}\">").Append(body).Append("</CT>\r\n");
            var tm = Regex.Match(ctBody, @"<Type>(\d+)</Type>");
            int newT = tm.Success ? int.Parse(tm.Groups[1].Value, Ci) : ocdType.GetValueOrDefault(o.Ocd);
            int oldT = ocdType.GetValueOrDefault(o.Ocd);
            ocdType[ocd] = newT;
            objs[idx] = o with { Ocd = ocd };
            res[o.Id] = (ctNum, features);
            applied.Add((o.Id, sw.Source, sw.Ocd, ocd));
            list.Add($"{o.Name}: {sw.LayoutName} ({(sw.Source == TerrainTool.Core.Editor.LayoutSwaps.Bms ? "BMS 4.38" : "Mideast")} OCD {sw.Ocd}) -> OCD {ocd}, CT {ctNum}, {features} pieces"
                     + (newT != oldT ? $"; type {TerrainTool.Core.Editor.EditorData.TypeName(oldT)} -> {TerrainTool.Core.Editor.EditorData.TypeName(newT)}" : ""));
        }
        int end = ctXml.LastIndexOf("</CTRecords>", StringComparison.Ordinal);
        bool bom = File.ReadAllBytes(ctPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        if (end >= 0 && add.Length > 0) File.WriteAllText(ctPath, ctXml[..end] + add + ctXml[end..], new UTF8Encoding(bom));
        TerrainTool.Core.Editor.LayoutSwaps.SaveApplied(c.Project.ProjectFolder, applied);
        notes.Add($"Layouts replaced by hand (layout_swaps.csv): {res.Count} objectives:");
        foreach (var l in list) notes.Add("   " + l);
        return res;
    }

    /// <summary>
    /// Basi coreane girate che hanno il numero di pista scritto nelle soglie: se con la direzione nel teatro il numero non e'
    /// piu' quello giusto, le piste diventano generiche 4.38 (asfalto a pezzi da 2052 ft con i numeri QFU dalla direzione);
    /// vie di rullaggio, luci, cartelli e il resto della base restano. Aggiorna il numero di pezzi per i file della campagna.
    /// </summary>
    private void GenericRunwaysForTurnedBases(List<CampObj> objs, string outOrd, Dictionary<int, (int Ct, int Features)> swapped, List<string> notes)
    {
        var log = new List<string>();
        foreach (var id in swapped.Keys.ToList())
        {
            var o = objs.FirstOrDefault(x => x.Id == id);
            if (o == null) continue;
            string dir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            var fed = Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault();
            if (fed == null) continue;
            var names = Regex.Matches(File.ReadAllText(fed), @"<FeatureCtIdx>(\d+)</FeatureCtIdx>").Select(m => _featureName(int.Parse(m.Groups[1].Value, Ci))).ToList();
            bool fixedNums = names.Any(n => Regex.IsMatch(n, @"\b(thr|threshold)\s*\d{2}[LRC]?\b|\brwy\s+\d{2}", RegexOptions.IgnoreCase)
                                            && !n.Contains("XX", StringComparison.OrdinalIgnoreCase) && !n.Contains("Sign", StringComparison.OrdinalIgnoreCase));
            if (!fixedNums) continue;
            static int Num(double mag) { int n = (int)Math.Round(((mag % 360) + 360) % 360 / 10); return n <= 0 ? n + 36 : n > 36 ? n - 36 : n; }
            var rws = LayoutRunways(dir);
            if (rws.All(r => Num(r.Hdg - SwapVar(Path.GetFileName(dir))) == Num(r.Hdg + o.Heading - MagneticVariation))) { log.Add($"{o.Name}: painted runway numbers still right, original runway kept"); continue; }
            var l = new List<string>();
            if (AddMissingRunways(dir, l, force: true))
            {
                int n = Regex.Matches(File.ReadAllText(fed), @"<FED Num=""\d+"">").Count;
                swapped[id] = (swapped[id].Ct, n);
                log.Add($"{o.Name}: runway numbers painted in the Korean models would be wrong here: generic runway pieces with the right numbers ({n} pieces)");
            }
            else log.Add($"{o.Name}: runway numbers painted in the models stay WRONG ({string.Join("; ", l.Where(x => x.Contains("not done")))})");
        }
        if (log.Count > 0) { notes.Add("Korean bases turned in the theater:"); foreach (var x in log) notes.Add("   " + x); }
    }

    private static readonly Regex SignName = new(@"^Rwy Sign\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Cartelli lungo le vie di rullaggio delle basi coreane ("Rwy Sign 28-10", "Rwy Sign Rwy - 36"): il numero e' scritto nel
    /// modello. Con la base ruotata si mette il cartello con il numero nuovo (stessa lettera L/R/C) se c'e' nel database,
    /// altrimenti il cartello si seppellisce (OffsetZ 2000: il numero di elementi non cambia).
    /// </summary>
    private void FixSwappedSigns(string outOrd, List<CampObj> objs, List<string> notes)
    {
        int changed = 0, buried = 0;
        foreach (var (dn, _) in _swappedDirs)
        {
            if (!int.TryParse(dn[4..], NumberStyles.Integer, Ci, out int ocd)) continue;
            var o = objs.FirstOrDefault(x => x.Ocd == ocd);
            if (o == null) continue;
            string dir = Path.Combine(outOrd, dn);
            var phd = Directory.EnumerateFiles(dir, "PHD_*.XML").FirstOrDefault();
            var fed = Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault();
            if (phd == null || fed == null) continue;
            static string Num(double mag) { int n = (int)Math.Round(((mag % 360) + 360) % 360 / 10); if (n <= 0) n += 36; if (n > 36) n -= 36; return n.ToString("00", Ci); }
            var map = new Dictionary<string, string>();
            foreach (Match h in Regex.Matches(File.ReadAllText(phd), @"<PHD Num=""\d+"">(.*?)</PHD>", RegexOptions.Singleline))
            {
                string b = h.Groups[1].Value;
                if (!Regex.IsMatch(b, "<Type>1</Type>")) continue;
                var dm = Regex.Match(b, "<Data>(.*?)</Data>");
                if (!dm.Success || !double.TryParse(dm.Groups[1].Value, NumberStyles.Float, Ci, out double data)) continue;
                map.TryAdd(Num(data - SwapVar(dn)), Num(data + o.Heading - MagneticVariation));
            }
            if (map.Count == 0 || map.All(kv => kv.Key == kv.Value)) continue;
            string xml = File.ReadAllText(fed);
            xml = Regex.Replace(xml, @"(<FED Num=""\d+"">)(.*?)(</FED>)", m =>
            {
                string b = m.Groups[2].Value;
                var ci = Regex.Match(b, @"<FeatureCtIdx>(\d+)</FeatureCtIdx>");
                if (!ci.Success) return m.Value;
                string name = _featureName(int.Parse(ci.Groups[1].Value, Ci));
                if (!SignName.IsMatch(name) || !Regex.IsMatch(name, @"\d{2}")) return m.Value;
                bool ok = true;
                string nn = Regex.Replace(name, @"\b(\d{2})(?=[LRC]?\b)", d =>
                {
                    if (map.TryGetValue(d.Value, out var v)) return v;
                    ok = false; return d.Value;
                });
                if (nn == name) return m.Value;
                if (ok && _ctByName.TryGetValue(nn, out int nct))
                {
                    changed++;
                    return m.Groups[1].Value + Regex.Replace(b, @"<FeatureCtIdx>\d+</FeatureCtIdx>", $"<FeatureCtIdx>{nct}</FeatureCtIdx>") + m.Groups[3].Value;
                }
                buried++;
                return m.Groups[1].Value + Regex.Replace(b, "<OffsetZ>.*?</OffsetZ>", "<OffsetZ>2000.000</OffsetZ>") + m.Groups[3].Value;
            }, RegexOptions.Singleline);
            File.WriteAllText(fed, xml, new UTF8Encoding(false));
        }
        notes.Add($"Korean bases: taxiway signs with runway numbers: {changed} replaced with the new number, {buried} hidden (no sign with that number)");
    }

    private static int CtIdx(string ocdXml)
    {
        var m = Regex.Match(File.ReadAllText(ocdXml), @"<CtIdx>(\d+)</CtIdx>");
        return m.Success ? int.Parse(m.Groups[1].Value, Ci) : -1;
    }

    private static string RemapFed(string xml, Dictionary<int, int> fmap, Dictionary<int, int> used) =>
        Regex.Replace(xml, @"<FeatureCtIdx>(\d+)</FeatureCtIdx>", m =>
        {
            int i = int.Parse(m.Groups[1].Value, Ci);
            used[i] = used.GetValueOrDefault(i) + 1;
            return fmap.TryGetValue(i, out var j) && j != i ? $"<FeatureCtIdx>{j}</FeatureCtIdx>" : m.Value;
        });

    // ───────────────────────── TLD / LCD ─────────────────────────

    private sealed record CampObj(int Id, string Name, int Ocd, double Heading, double X, double Y);

    /// <summary>Tipi di obiettivo (Falcon4_CT Type) che si spostano verso terra: niente basi (1, 2), spiagge (4),
    /// confini (5), ponti (6), guadi (12), incroci (15), radiofari (16), passi (18).</summary>
    private static readonly HashSet<int> SnapTypes = new() { 3, 7, 8, 9, 10, 11, 13, 14, 17, 19, 20, 21, 22, 25, 28, 29, 30, 31 };
    public double SnapRadiusM { get; set; } = 4000;
    private static readonly Regex NoSnapName = new(@"\bdam\b|\bbarrage\b|\bplatform\b|offshore", RegexOptions.IgnoreCase);

    /// <summary>Pezzi che in un porto (o altrove) devono stare in acqua o sul bordo: moli, banchine, navi, boe.</summary>
    private static readonly Regex MarinePiece = new(@"\b(piers?|docks?|quay|quays|wharf|jett(y|ies)|ships?|boats?|vessels?|barges?|tankers?|buoys?|breakwaters?|moles?|harbou?r|ferry|frigate|destroyer|corvette|cargo|container\s*ship|lng|patrol\s*boat|landing)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Sul terreno nuovo (coste vere da OSM) alcuni obiettivi del Mideast hanno gli edifici in mare (es. il porto del
    /// Bahrein in mezzo al canale). Per ogni obiettivo dei tipi SnapTypes con piu' di un quarto degli edifici "di terra" in acqua
    /// (acqua del NewTerrain come la disegna BMS: WaterHeightMap sopra HeightMap) si cerca, entro SnapRadiusM, lo spostamento
    /// PIU' CORTO che lascia in acqua al massimo il 10% degli edifici di terra; per i porti moli, banchine e navi (MarinePiece)
    /// non contano come edifici e si preferisce che almeno meta' tocchi ancora l'acqua (il porto resta sulla costa). Se nessuna
    /// posizione arriva al 10%, quella con meno edifici in acqua. Senza terra entro il raggio l'obiettivo resta dov'e'.
    /// Ogni caso va in coastal_moves.csv del progetto (Review coastal moves dell'Object Editor): KEEP = resta dov'era nell'add-on.
    /// </summary>
    /// <summary>
    /// Incroci (tipo 15) con piu' di un quarto dei pezzi (o il punto, se non hanno pezzi) in acqua. Gli incroci reggono la rete delle strade
    /// e SnapToLand non li tocca; ma se la citta' a cui sono collegati e' stata spostata (sul luogo vero o verso terra), l'incrocio in mare
    /// blocca il collegamento (Emirati: Dubai, Umm Al Quwain, Ras Al Khaimah…). Qui l'incrocio si sposta della stessa quantita' dell'obiettivo
    /// collegato spostato piu' vicino (resta dov'era rispetto alla citta'), poi, se e' ancora in acqua, verso la terra piu' vicina (max 4 km).
    /// Senza obiettivi collegati spostati: solo verso la terra piu' vicina. Le righe vanno in coastal_moves.csv (tipo Junction, KEEP valido).
    /// </summary>
    private void MoveJunctions(TextureContext c, List<CampObj> objs, Dictionary<int, (double X, double Y)> preSnap, Dictionary<int, int> removed,
        Dictionary<int, int> ocdType, string outOrd, List<string> notes, Dictionary<int, TerrainTool.Core.Editor.CoastalRow> coastBefore)
    {
        var p = c.Project;
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string? data = TerrainTool.Core.Campaign.CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain);
        string? bmsCamp = data != null ? Path.Combine(data, addOn, "Campaign") : null;
        string? Find(string? dir) => dir != null && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("Te_New_Nt.tac", StringComparison.OrdinalIgnoreCase))
              ?? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("te_new.tac", StringComparison.OrdinalIgnoreCase))
            : null;
        string? tpl = Find(CampaignEditsFolder(p)) ?? Find(Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign")) ?? Find(bmsCamp) ?? Find(Path.Combine(p.OldTheaterFolder, "Campaign"));
        List<(int A, int B)> links;
        try { links = tpl == null ? new() : TerrainTool.Core.Campaign.CampaignTerrainCheck.GroundLinks(TerrainTool.Core.Campaign.CampaignDoc.Load(tpl, _ => null, new[] { Path.GetDirectoryName(tpl)! })); }
        catch (Exception ex) { notes.Add("Junctions in water: links not read (" + ex.Message + ")"); links = new(); }
        TerrainTool.Core.Editor.WaterProbe probe;
        try { probe = new TerrainTool.Core.Editor.WaterProbe(p); }
        catch (Exception ex) { notes.Add("Junctions in water: not checked (" + ex.Message + ")"); return; }
        using var _probe = probe;
        if (!probe.HasWater) return;
        const double ft = 0.3048;
        double sizeM = p.Theater.SizeMeters, px = probe.PixelFt * ft;
        var neigh = new Dictionary<int, List<int>>();
        foreach (var (a, b) in links)
        {
            if (!neigh.TryGetValue(a, out var la)) neigh[a] = la = new(); la.Add(b);
            if (!neigh.TryGetValue(b, out var lb)) neigh[b] = lb = new(); lb.Add(a);
        }
        var byId = objs.Select((o, i) => (o, i)).GroupBy(x => x.o.Id).ToDictionary(g => g.Key, g => g.First().i);
        var old = coastBefore;
        var rows = TerrainTool.Core.Editor.CoastalPlan.Load(p.ProjectFolder).Values.Where(r => r.Type != "Junction").ToList();   // righe di SnapToLand + incroci rifatti
        var log = new List<string>();
        int wet = 0, moved = 0, kept = 0, left = 0;
        var offsets = new List<(double Dx, double Dy, double D)>();
        int steps = (int)(SnapRadiusM / px);
        for (int sy = -steps; sy <= steps; sy++)
            for (int sx = -steps; sx <= steps; sx++)
            {
                double dx = sx * px, dy = sy * px, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= SnapRadiusM) offsets.Add((dx, dy, d));
            }
        offsets.Sort((a, b) => a.D.CompareTo(b.D));
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (ocdType.GetValueOrDefault(o.Ocd, -1) != 15 || removed.ContainsKey(o.Id)) continue;   // anche le basi/ponti diventati incroci restano dove sono
            var pts = new List<(double N, double E)>();
            var fed = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}", $"FED_{o.Ocd:00000}.XML");
            if (File.Exists(fed))
                foreach (Match m in Regex.Matches(File.ReadAllText(fed), @"<FED Num=""\d+"">(.*?)</FED>", RegexOptions.Singleline))
                {
                    string b = m.Groups[1].Value;
                    double G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success && double.TryParse(x.Groups[1].Value, NumberStyles.Float, Ci, out var v) ? v : 0; }
                    if (G("OffsetZ") > 100) continue;
                    pts.Add((G("OffsetY"), G("OffsetX")));
                }
            double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            var rel = pts.Count > 0 ? pts.Select(f => ((f.N * ch - f.E * sh) * ft, (f.N * sh + f.E * ch) * ft)).ToList() : new List<(double, double)> { (0.0, 0.0) };
            double oe = o.Y * ft, on = o.X * ft;
            int Water(double dx, double dy)
            {
                int n = 0;
                foreach (var (pn, pe) in rel)
                {
                    double east = oe + dx + pe, north = on + dy + pn;
                    if (east < 0 || north < 0 || east >= sizeM || north >= sizeM || probe.IsWater(north / ft, east / ft)) n++;
                }
                return n;
            }
            int now = Water(0, 0);
            if (now * 4 <= rel.Count) continue;
            wet++;
            // obiettivo collegato spostato (da SnapToLand) piu' vicino
            (double Dx, double Dy, string Name)? shift = null;
            double best = double.MaxValue;
            foreach (int nb in neigh.GetValueOrDefault(o.Id) ?? new())
            {
                if (!byId.TryGetValue(nb, out int ni) || !preSnap.TryGetValue(nb, out var pre)) continue;
                var no = objs[ni];
                double mdx = (no.Y - pre.Y) * ft, mdy = (no.X - pre.X) * ft;
                if (Math.Abs(mdx) < 1 && Math.Abs(mdy) < 1) continue;
                double dist = Math.Sqrt((pre.X - o.X) * (pre.X - o.X) + (pre.Y - o.Y) * (pre.Y - o.Y)) * ft;
                if (dist < best && dist <= 15000) { best = dist; shift = (mdx, mdy, no.Name); }
            }
            double bx = shift?.Dx ?? 0, by = shift?.Dy ?? 0;
            (double Dx, double Dy, double D, int W)? pick = null;
            foreach (var (ddx, ddy, _) in offsets)
            {
                double dx = bx + ddx, dy = by + ddy;
                int wv = Water(dx, dy);
                if (wv * 10 <= rel.Count) { pick = (dx, dy, Math.Sqrt(dx * dx + dy * dy), wv); break; }
            }
            if (pick == null && shift != null)   // dal posto della citta' non c'e' terra vicina: dal punto dell'add-on
                foreach (var (dx, dy, d) in offsets)
                {
                    int wv = Water(dx, dy);
                    if (wv * 10 <= rel.Count) { pick = (dx, dy, d, wv); break; }
                }
            string note = shift != null ? $"junction: same shift as {shift.Value.Name}" : "junction: to the nearest land";
            var dec = old.TryGetValue(o.Id, out var prev) ? prev.Decision : TerrainTool.Core.Editor.CoastalDecision.None;
            if (pick == null)
            {
                left++;
                rows.Add(new TerrainTool.Core.Editor.CoastalRow(o.Id, o.Name, "Junction", rel.Count, now, now, 0, 0, 0, o.X, o.Y, o.X, o.Y, $"junction: no land within {SnapRadiusM / 1000:0} km: left", dec));
                continue;
            }
            var (pdx, pdy, pd, pw) = pick.Value;
            double nx = o.X + pdy / ft, ny = o.Y + pdx / ft;
            rows.Add(new TerrainTool.Core.Editor.CoastalRow(o.Id, o.Name, "Junction", rel.Count, now, pw, 0, 0, pd, o.X, o.Y, nx, ny, note, dec));
            if (dec == TerrainTool.Core.Editor.CoastalDecision.Keep) { kept++; continue; }
            objs[i] = o with { X = nx, Y = ny };
            moved++;
            log.Add($"{o.Name} (CampId {o.Id}): {now}/{rel.Count} pieces in water -> {pw} after moving {pd:0} m ({note})");
        }
        TerrainTool.Core.Editor.CoastalPlan.Save(p.ProjectFolder, rows);
        notes.Add($"Junctions in water: {wet} with more than 1/4 of the pieces in water, {moved} moved (with the city they link to, or to the nearest land), " +
                  $"{kept} left in place (KEEP), {left} with no land near; also in coastal_moves.csv:");
        foreach (var l in log.Take(80)) notes.Add("   " + l);
    }

    private HashSet<int> SnapToLand(TextureContext c, List<CampObj> objs, string outOrd, Dictionary<int, int> ocdType, List<string> notes)
    {
        var res = new HashSet<int>();
        TerrainTool.Core.Editor.WaterProbe probe;
        try { probe = new TerrainTool.Core.Editor.WaterProbe(c.Project); }
        catch (Exception ex) { notes.Add("Objectives in water: not checked (" + ex.Message + ")"); return res; }
        using var _probe = probe;
        if (!probe.HasWater) { notes.Add("Objectives in water: no water data (NewTerrain WaterHeightMap or TB_Input\\Watermap.raw), not checked"); return res; }
        const double ft = 0.3048;
        double sizeM = c.Project.Theater.SizeMeters, px = probe.PixelFt * ft;
        var old = TerrainTool.Core.Editor.CoastalPlan.Load(c.Project.ProjectFolder);
        var real = TerrainTool.Core.Editor.RealPlaces.Load(c.Project.ProjectFolder);
        int realUsed = 0;
        var rows = new List<TerrainTool.Core.Editor.CoastalRow>();
        var feds = new Dictionary<int, List<(double N, double E, bool Marine)>>();
        var log = new List<string>();
        int checkedN = 0, wet = 0, kept = 0;
        // spostamenti candidati, dal piu' corto
        int steps = (int)(SnapRadiusM / px);
        var offsets = new List<(double Dx, double Dy, double D)>();
        for (int sy = -steps; sy <= steps; sy++)
            for (int sx = -steps; sx <= steps; sx++)
            {
                double dx = sx * px, dy = sy * px, d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= SnapRadiusM) offsets.Add((dx, dy, d));
            }
        offsets.Sort((a, b) => a.D.CompareTo(b.D));
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (!ocdType.TryGetValue(o.Ocd, out int type) || !SnapTypes.Contains(type)) continue;
            if (NoSnapName.IsMatch(o.Name)) continue;   // dighe: stanno sull'acqua per natura
            if (!feds.TryGetValue(o.Ocd, out var fp))
            {
                fp = new List<(double, double, bool)>();
                var fed = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}", $"FED_{o.Ocd:00000}.XML");
                if (File.Exists(fed))
                    foreach (Match m in Regex.Matches(File.ReadAllText(fed), @"<FED Num=""\d+"">(.*?)</FED>", RegexOptions.Singleline))
                    {
                        string b = m.Groups[1].Value;
                        double G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success && double.TryParse(x.Groups[1].Value, NumberStyles.Float, Ci, out var v) ? v : 0; }
                        if (G("OffsetZ") > 100) continue;   // gli elementi sepolti non contano
                        bool marine = MarinePiece.IsMatch(_featureName((int)G("FeatureCtIdx")));
                        fp.Add((G("OffsetY"), G("OffsetX"), marine));
                    }
                feds[o.Ocd] = fp;
            }
            var landP = fp.Where(f => !f.Marine).ToList();
            var marP = fp.Where(f => f.Marine).ToList();
            if (landP.Count < 3) continue;
            checkedN++;
            // edifici in metri del teatro (x est, y nord) con la rotazione dell'obiettivo
            double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            List<(double, double)> Rel(List<(double N, double E, bool Marine)> l) => l.Select(f => ((f.N * ch - f.E * sh) * ft, (f.N * sh + f.E * ch) * ft)).ToList();
            var pts = Rel(landP); var mpts = Rel(marP);
            double ox = o.Y * ft, oy = o.X * ft;   // est, nord
            int Water(List<(double, double)> l, double dx, double dy)
            {
                int n = 0;
                foreach (var (pn, pe) in l)
                {
                    double east = ox + dx + pe, north = oy + dy + pn;   // metri
                    if (east < 0 || north < 0 || east >= sizeM || north >= sizeM || probe.IsWater(north / ft, east / ft)) n++;
                }
                return n;
            }
            int now = Water(pts, 0, 0);
            if (now * 4 <= pts.Count) continue;
            wet++;
            string tname = TerrainTool.Core.Editor.EditorData.TypeName(type);
            // il primo (piu' corto) con <= 10% degli edifici di terra in acqua; porti: anche almeno meta' dei pezzi marini in acqua.
            // Con un luogo vero da OSM (real_places.csv) la ricerca parte da li': l'add-on sbaglia la posizione anche di km.
            ((double Dx, double Dy, double D, int W)? Pick, (double Dx, double Dy, double D, int W)? Least) Search(double bx, double by)
            {
                (double Dx, double Dy, double D, int W)? pk = null, pkNoSea = null, ls = null;
                foreach (var (ddx, ddy, _) in offsets)
                {
                    double dx = bx + ddx, dy = by + ddy, d = Math.Sqrt(dx * dx + dy * dy);
                    int wv = Water(pts, dx, dy);
                    if (ls == null || wv < ls.Value.W) ls = (dx, dy, d, wv);
                    if (wv * 10 > pts.Count) continue;
                    if (mpts.Count == 0) { pk = (dx, dy, d, wv); break; }
                    pkNoSea ??= (dx, dy, d, wv);
                    if (Water(mpts, dx, dy) * 2 >= mpts.Count) { pk = (dx, dy, d, wv); break; }
                }
                return (pk ?? pkNoSea, ls);
            }
            (double Dx, double Dy, double D, int W)? pick = null, least = null;
            string realNote = "";
            // un porto prende il nome del paese: il centro del paese non e' il porto (Ras Al Khafji Port finiva sopra Ras Al Khafji City,
            // Salwa Port con i moli a terra). Per i porti vale solo un'isola (Farsi); altrimenti lo spostamento normale verso la costa.
            if (real.TryGetValue(o.Id, out var rp) && rp.Use && RealPlaces.UsableFor(tname, rp.OsmKind))
            {
                var (rpk, rls) = Search((rp.Y - o.Y) * ft, (rp.X - o.X) * ft);
                if (rpk != null) { pick = rpk; realNote = $"real place (OSM): {rp.OsmName} ({rp.OsmKind}), {rp.DistKm:0.0} km from the add-on point"; realUsed++; }
                else
                {
                    // il luogo vero non ha abbastanza terra: prima lo spostamento normale verso terra; solo se anche quello non trova
                    // terra, il punto del luogo vero con meno pezzi in acqua (isola piu' piccola dell'obiettivo: Farsi, 0,5 km, porto di 27 pezzi)
                    var (npk, nls) = Search(0, 0);
                    if (npk != null) { pick = npk; realNote = $"real place (OSM) {rp.OsmName} is under water too: moved to the nearest land instead"; }
                    else if (rls != null && rls.Value.W < now && (nls == null || rls.Value.W <= nls.Value.W))
                    {
                        pick = rls; realUsed++;
                        realNote = $"real place (OSM): {rp.OsmName} ({rp.OsmKind}), {rp.DistKm:0.0} km from the add-on point; the land there is smaller than the objective: the place with the fewest pieces in the water";
                    }
                    else { least = nls; realNote = $"real place (OSM) {rp.OsmName} is under water too: moved to the nearest land instead"; }
                }
            }
            if (pick == null) (pick, least) = Search(0, 0);
            string note;
            if (pick == null && least != null && least.Value.W < now) { pick = least; note = "no position with 10% or less of the buildings in water: the one with the fewest"; }
            else note = pick == null ? $"no land within {SnapRadiusM / 1000:0} km: left" : mpts.Count > 0 ? "port: piers, quays and ships may stay in the water" : "";
            if (realNote.Length > 0) note = note.Length > 0 ? realNote + "; " + note : realNote;
            var dec = old.TryGetValue(o.Id, out var prev) ? prev.Decision : TerrainTool.Core.Editor.CoastalDecision.None;
            if (pick == null)
            {
                rows.Add(new TerrainTool.Core.Editor.CoastalRow(o.Id, o.Name, tname, fp.Count, now, now, mpts.Count, Water(mpts, 0, 0), 0, o.X, o.Y, o.X, o.Y, note, dec));
                log.Add($"{o.Name}: {now}/{pts.Count} buildings in water, {note}");
                continue;
            }
            var (bdx, bdy, bd, best) = pick.Value;
            double nx = o.X + bdy / ft, ny = o.Y + bdx / ft;
            rows.Add(new TerrainTool.Core.Editor.CoastalRow(o.Id, o.Name, tname, fp.Count, now, best, mpts.Count, Water(mpts, bdx, bdy), bd, o.X, o.Y, nx, ny, note, dec));
            if (dec == TerrainTool.Core.Editor.CoastalDecision.Keep) { kept++; log.Add($"{o.Name}: {now}/{pts.Count} buildings in water, KEEP in coastal_moves.csv: left where it was"); continue; }
            objs[i] = o with { X = nx, Y = ny };
            res.Add(o.Id);
            log.Add($"{o.Name}: {now}/{pts.Count} buildings in water -> {best} after moving {bd:0} m" + (mpts.Count > 0 ? $" ({mpts.Count} piers/ships, {Water(mpts, bdx, bdy)} in the water)" : ""));
        }
        TerrainTool.Core.Editor.CoastalPlan.Save(c.Project.ProjectFolder, rows);
        notes.Add($"Objectives in water ({probe.Source}): {checkedN} checked, {wet} with more than 1/4 of the buildings in water, {res.Count} moved to land " +
                  $"(shortest move leaving 10% or less in the water, within {SnapRadiusM / 1000:0} km; {realUsed} of them onto their real place from OSM, real_places.csv), {kept} left in place (KEEP); review: Object Editor → Review coastal moves (coastal_moves.csv):");
        foreach (var l in log.Take(150)) notes.Add("   " + l);
        if (log.Count > 150) notes.Add($"   ... {log.Count - 150} more");
        return res;
    }

    /// <summary>
    /// Copie per base: per ogni obiettivo in <paramref name="ids"/> la cui disposizione (OCD) e' usata anche da altri
    /// obiettivi, nuova cartella OCD (numero dopo l'ultimo, FED/PDX/PHD/OCD copiati con ObjIdx e CtIdx nuovi) e nuova voce
    /// in Falcon4_CT.xml (copia di quella dell'OCD originale con EntityIdx nuovo). Aggiorna objs (Ocd) e restituisce
    /// CampId -> nuovo indice CT (il "tipo" dell'obiettivo nei file della campagna).
    /// </summary>
    private Dictionary<int, int> CloneSharedLayouts(List<CampObj> objs, HashSet<int> ids, string dst, string outOrd, List<string> notes, string what = "moved airbases")
    {
        var res = new Dictionary<int, int>();
        var useCount = objs.GroupBy(o => o.Ocd).ToDictionary(g => g.Key, g => g.Count());
        var todo = objs.Where(o => ids.Contains(o.Id) && useCount.GetValueOrDefault(o.Ocd) > 1).OrderBy(o => o.Id).ToList();
        if (todo.Count == 0) return res;
        string ctPath = Path.Combine(dst, "Falcon4_CT.xml");
        string ctXml = File.ReadAllText(ctPath);
        var ctBlocks = Regex.Matches(ctXml, @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline)
            .Cast<Match>().ToDictionary(m => int.Parse(m.Groups[1].Value, Ci), m => m.Groups[2].Value);
        int nextCt = ctBlocks.Keys.Max() + 1;
        int nextOcd = Directory.EnumerateDirectories(outOrd, "OCD_*").Select(d => int.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, Ci, out int n) ? n : -1).Max() + 1;
        var add = new StringBuilder();
        var list = new List<string>();
        foreach (var o in todo)
        {
            string srcDir = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}");
            var ocdXml = Directory.EnumerateFiles(srcDir, "OCD_*.XML").FirstOrDefault();
            if (ocdXml == null) continue;
            int srcCt = CtIdx(ocdXml);
            if (!ctBlocks.TryGetValue(srcCt, out var ctBody)) continue;
            int ocd = nextOcd++, ctNum = nextCt++;
            string dir = Path.Combine(outOrd, $"OCD_{ocd:00000}");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.EnumerateFiles(srcDir))
            {
                string fn = Path.GetFileName(f);
                string nn = Regex.Replace(fn, @"_\d{5}\.", $"_{ocd:00000}.");
                string t = File.ReadAllText(f);
                t = Regex.Replace(t, @"<ObjIdx>\d+</ObjIdx>", $"<ObjIdx>{ocd}</ObjIdx>");
                if (fn.StartsWith("OCD_", StringComparison.OrdinalIgnoreCase))
                {
                    t = Regex.Replace(t, @"<OCD Num=""\d+"">", $"<OCD Num=\"{ocd}\">");
                    t = Regex.Replace(t, @"<CtIdx>\d+</CtIdx>", $"<CtIdx>{ctNum}</CtIdx>");
                }
                File.WriteAllText(Path.Combine(dir, nn), t, new UTF8Encoding(false));
            }
            string body = Regex.Replace(ctBody, @"<EntityIdx>\d+</EntityIdx>", $"<EntityIdx>{ocd}</EntityIdx>");
            add.Append($"    <CT Num=\"{ctNum}\">").Append(body).Append("</CT>\r\n");
            if (_genericRunways.TryGetValue(Path.GetFileName(srcDir), out var gr)) _genericRunways[Path.GetFileName(dir)] = new HashSet<int>(gr);
            int idx = objs.FindIndex(x => x.Id == o.Id);
            objs[idx] = objs[idx] with { Ocd = ocd };
            res[o.Id] = ctNum;
            list.Add($"{o.Name}: OCD {o.Ocd} -> {ocd} (CT {ctNum})");
        }
        int end = ctXml.LastIndexOf("</CTRecords>", StringComparison.Ordinal);
        bool bom = File.ReadAllBytes(ctPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        if (end >= 0 && add.Length > 0) File.WriteAllText(ctPath, ctXml[..end] + add + ctXml[end..], new UTF8Encoding(bom));
        notes.Add($"Own layout copy for {res.Count} {what} that shared their layout (new OCD/CT at the end of the database):");
        foreach (var l in list.Take(120)) notes.Add("   " + l);
        return res;
    }

    /// <summary>
    /// Ponti a piu' pezzi rimasti ponti. BMS appoggia ogni pezzo al terreno sotto il suo centro: su un fiume campata e rampe
    /// finiscono a quote diverse e i pezzi (rigidi, orizzontali) non si incontrano. Come fa la Korea per 15 ponti (TLD dei ponti):
    /// il terreno sotto i pezzi a terra si spiana a una quota unica (meta' tra le quote delle due testate), con due fasce di
    /// raccordo al 66% e al 33%; i pezzi sull'acqua (il fiume non si spiana: diventerebbe una diga) vanno alla stessa quota con
    /// OffsetZ nel FED. Dighe sul mare (80% dei pezzi sull'acqua) e ponti con Level? = NO restano come sono. Un ponte che
    /// condivide la disposizione riceve una sua copia. Restituisce CampId → nuovo indice CT dei ponti copiati; gli spianamenti
    /// si scrivono dopo quelli delle basi (WriteBridgeTld).
    /// </summary>
    private Dictionary<int, int> LevelBridges(TextureContext c, List<CampObj> objs, Dictionary<int, int> removed, Dictionary<int, int> ocdType,
        string dst, string outOrd, List<string> notes, HashSet<int> noLevel, Dictionary<int, Ct> ct, string modelsDir)
    {
        var res = new Dictionary<int, int>();
        _bridgeTld.Clear();
        if (noLevel.Count > 0) notes.Add($"Bridge leveling: {noLevel.Count} bridges left as they are (Level? = NO in bridges.csv)");
        TerrainTool.Core.Editor.WaterProbe probe;
        try { probe = new TerrainTool.Core.Editor.WaterProbe(c.Project); }
        catch (Exception ex) { notes.Add("Bridge leveling skipped: " + ex.Message); return res; }
        using var _ = probe;
        var dims = new Dictionary<int, (double MinX, double MaxX, double MinY, double MaxY)>();
        (double MinX, double MaxX, double MinY, double MaxY) Dims(int ctIdx)
        {
            int g = ct.TryGetValue(ctIdx, out var cc) ? cc.Graphics : -1;
            if (dims.TryGetValue(g, out var d)) return d;
            d = (-40, 40, -40, 40);
            string pd = Path.Combine(modelsDir, g.ToString(Ci), "Parent.dat");
            if (g > 0 && File.Exists(pd))
                foreach (var l in File.ReadLines(pd))
                {
                    var m = Regex.Match(l, @"^\s*Dimensions\s*=\s*(.*)$");
                    if (!m.Success) continue;
                    var v = m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => double.TryParse(x, NumberStyles.Float, Ci, out var y) ? y : double.NaN).ToArray();
                    if (v.Length >= 5 && v.Take(5).All(double.IsFinite)) d = (v[1], v[2], v[3], v[4]);
                    break;
                }
            return dims[g] = d;
        }
        static List<(int Num, double N, double E, double Z, double H, int Ct)> Feds(string fed) =>
            Regex.Matches(File.ReadAllText(fed), @"<FED Num=""(\d+)"">(.*?)</FED>", RegexOptions.Singleline).Cast<Match>().Select(m =>
            {
                string b = m.Groups[2].Value;
                double G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success && double.TryParse(x.Groups[1].Value, NumberStyles.Float, Ci, out var v) ? v : 0; }
                return (int.Parse(m.Groups[1].Value, Ci), G("OffsetY"), G("OffsetX"), G("OffsetZ"), G("Heading"), (int)G("FeatureCtIdx"));   // OffsetX = est, OffsetY = nord
            }).Where(f => f.Item4 < 100).ToList();

        var plan = new Dictionary<int, (double[] Dz, int Level, List<(double Pct, List<(double X, double Y)> Poly)> Polys, string Log)>();
        int causeways = 0;
        var causewayNames = new List<string>();
        foreach (var o in objs)
        {
            if (removed.ContainsKey(o.Id) || noLevel.Contains(o.Id) || ocdType.GetValueOrDefault(o.Ocd, -1) != 6) continue;
            string fed = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}", $"FED_{o.Ocd:00000}.XML");
            if (!File.Exists(fed)) continue;
            var fs = Feds(fed);
            if (fs.Count < 2) continue;
            double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            var centers = fs.Select(f => (N: o.X + f.N * ch - f.E * sh, E: o.Y + f.N * sh + f.E * ch)).ToList();
            var wet = centers.Select(cc => probe.IsWater(cc.N, cc.E)).ToArray();
            // testate: i due centri piu' lontani
            int a = 0, b = 1; double best = -1;
            for (int i = 0; i < fs.Count; i++)
                for (int j = i + 1; j < fs.Count; j++)
                {
                    double dd = Math.Pow(centers[i].N - centers[j].N, 2) + Math.Pow(centers[i].E - centers[j].E, 2);
                    if (dd > best) { best = dd; a = i; b = j; }
                }
            // diga sul mare: quasi tutto sull'acqua e almeno una testata in acqua (non arriva a terra da entrambi i lati).
            // Un ponte lungo su un fiume largo (testate sulle due sponde) si livella anche se quasi tutti i pezzi sono sull'acqua.
            if (wet.Count(w => w) >= 0.8 * fs.Count && (wet[a] || wet[b]))
            {
                causeways++;
                causewayNames.Add($"{o.Name} (CampId {o.Id}): {wet.Count(w => w)} of {fs.Count} pieces over water, " +
                                  (wet[a] && wet[b] ? "both ends in the water" : "one end in the water") + $" (bridge {Math.Sqrt(best) * 0.3048:0} m between the end pieces)");
                continue;
            }
            var g = centers.Select(cc => probe.Ground(cc.N, cc.E)).ToArray();
            int level = (int)Math.Round((g[a] + g[b]) / 2);
            var dz = new double[fs.Count];
            for (int i = 0; i < fs.Count; i++) dz[i] = wet[i] ? Math.Round(g[i] - level, 1) : 0;   // + = giu'
            double worst = Enumerable.Range(0, fs.Count).Max(i => Math.Abs(g[i] - level));
            if (worst < TerrainTool.Core.Editor.BridgeLeveling.MinFt) continue;
            // spianamento sotto i pezzi a terra: sagoma del pezzo + 20 ft al 100%, +80 ft al 66%, +140 ft al 33%
            var polys = new List<(double, List<(double, double)>)>();
            foreach (var (pct, m) in new[] { (100.0, 20.0), (66.6666718, 80.0), (33.3333359, 140.0) })
                for (int i = 0; i < fs.Count; i++)
                {
                    if (wet[i]) continue;
                    var dm = Dims(fs[i].Ct);
                    double H = (o.Heading + fs[i].H) * Math.PI / 180, fn = Math.Cos(H), fe = Math.Sin(H), rn = -Math.Sin(H), re = Math.Cos(H);
                    (double, double) P(double x, double y) => (centers[i].N + x * fn + y * rn, centers[i].E + x * fe + y * re);
                    polys.Add((pct, new List<(double, double)> { P(dm.MinX - m, dm.MinY - m), P(dm.MaxX + m, dm.MinY - m), P(dm.MaxX + m, dm.MaxY + m), P(dm.MinX - m, dm.MaxY + m) }));
                }
            string log = $"{o.Name} (CampId {o.Id}): level {level} ft, banks {g[a]:0}/{g[b]:0} ft, " +
                         $"terrain under {wet.Count(w => !w)} pieces on land leveled (cut/fill up to {Enumerable.Range(0, fs.Count).Where(i => !wet[i]).Select(i => Math.Abs(g[i] - level)).DefaultIfEmpty(0).Max():0} ft)" +
                         (wet.Any(w => w) ? ", pieces over water " + string.Join(" ", Enumerable.Range(0, fs.Count).Where(i => wet[i]).Select(i => (-dz[i]).ToString("+0.0;-0.0;0", Ci))) + " ft" : "");
            plan[o.Id] = (dz, level, polys, log);
        }
        if (causeways > 0)
        {
            notes.Add($"Bridge leveling: {causeways} causeways (80% of the pieces over water and an end in the water) left as they are; a river bridge here is too short or not centred on the river:");
            foreach (var l in causewayNames) notes.Add("   " + l);
        }
        if (plan.Count == 0) { notes.Add("Bridge leveling: no multi-piece bridge needs it"); return res; }
        // copia della disposizione solo se ci sono pezzi da spostare (OffsetZ); lo spianamento del terreno e' per obiettivo
        var needCopy = plan.Where(kv => kv.Value.Dz.Any(x => Math.Abs(x) >= 0.05)).Select(kv => kv.Key).ToHashSet();
        if (needCopy.Count > 0) foreach (var kv in CloneSharedLayouts(objs, needCopy, dst, outOrd, notes, "leveled bridges")) res[kv.Key] = kv.Value;
        var logs = new List<string>();
        foreach (var o in objs)
        {
            if (!plan.TryGetValue(o.Id, out var pl)) continue;
            if (needCopy.Contains(o.Id))
            {
                string fed = Path.Combine(outOrd, $"OCD_{o.Ocd:00000}", $"FED_{o.Ocd:00000}.XML");
                var nums = Feds(fed).Select(f => f.Num).ToList();
                if (nums.Count != pl.Dz.Length) continue;
                var add = nums.Zip(pl.Dz).ToDictionary(x => x.First, x => x.Second);
                string xml = Regex.Replace(File.ReadAllText(fed), @"(<FED Num=""(\d+)"">)(.*?)(</FED>)", m =>
                {
                    if (!add.TryGetValue(int.Parse(m.Groups[2].Value, Ci), out double d) || Math.Abs(d) < 0.05) return m.Value;
                    string bb = m.Groups[3].Value;
                    var z = Regex.Match(bb, @"<OffsetZ>(.*?)</OffsetZ>");
                    double z0 = z.Success && double.TryParse(z.Groups[1].Value, NumberStyles.Float, Ci, out var v) ? v : 0;
                    string nz = $"<OffsetZ>{(z0 + d).ToString("0.000", Ci)}</OffsetZ>";
                    bb = z.Success ? bb.Replace(z.Value, nz) : bb.TrimEnd() + "\r\n        " + nz + "\r\n    ";
                    return m.Groups[1].Value + bb + m.Groups[4].Value;
                }, RegexOptions.Singleline);
                File.WriteAllText(fed, xml, new UTF8Encoding(false));
            }
            if (pl.Polys.Count > 0) _bridgeTld[o.Id] = (pl.Level, pl.Polys);
            logs.Add(pl.Log);
        }
        notes.Add($"Bridge leveling: {logs.Count} multi-piece bridges at one height (halfway between the two ends): ground under the pieces on land leveled " +
                  "(TerrainRelatedData TLD, as the Korean bridges, 66%/33% blend), pieces over water moved with OffsetZ (+ = up):");
        foreach (var l in logs.Take(150)) notes.Add("   " + l);
        return res;
    }

    /// <summary>Spianamenti dei ponti preparati da LevelBridges: CampId → (quota, poligoni con percentuale).</summary>
    private readonly Dictionary<int, (int Level, List<(double Pct, List<(double X, double Y)> Poly)> Polys)> _bridgeTld = new();

    /// <summary>Scrive i TLD dei ponti (dopo quelli delle basi; un obiettivo che ha gia' il suo spianamento non si tocca).</summary>
    private void WriteBridgeTld(string outDir, List<string> notes)
    {
        if (_bridgeTld.Count == 0) return;
        Directory.CreateDirectory(outDir);
        int n = 0, skip = 0;
        foreach (var (id, (level, polys)) in _bridgeTld)
        {
            string path = Path.Combine(outDir, $"TLD_{id:00000}.TPB");
            if (File.Exists(path)) { skip++; continue; }
            var sb = new StringBuilder();
            sb.Append("terrain_level_data {\r\n  terrain_level: ").Append(level.ToString(Ci)).Append("\r\n");
            foreach (var (pct, poly) in polys.OrderByDescending(q => q.Pct))
            {
                sb.Append("  terrain_level_polygon {\r\n    terrain_leveling_percentage: ").Append(pct.ToString("0.#######", Ci)).Append("\r\n");
                foreach (var (x, y) in poly)
                    sb.Append("    terrain_level_point {\r\n      sim_grid_pos_x: ").Append(F(x)).Append("\r\n      sim_grid_pos_y: ").Append(F(y)).Append("\r\n    }\r\n");
                sb.Append("  }\r\n");
            }
            sb.Append("}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            n++;
        }
        notes.Add($"TerrainRelatedData: terrain leveling (TLD) for {n} bridges" + (skip > 0 ? $", {skip} skipped (the objective already has its own leveling)" : ""));
    }

    private static List<CampObj> LoadCampObjs(string path)
    {
        var s = File.ReadAllText(path);
        var list = new List<CampObj>();
        foreach (Match m in Regex.Matches(s, @"<CampObj CampId=""(\d+)"">(.*?)</CampObj>", RegexOptions.Singleline))
        {
            string b = m.Groups[2].Value;
            string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>", RegexOptions.Singleline); return x.Success ? x.Groups[1].Value.Trim() : ""; }
            if (!int.TryParse(G("OcdIndex"), NumberStyles.Integer, Ci, out int ocd)) continue;
            double D(string t) => double.TryParse(G(t), NumberStyles.Float, Ci, out var v) ? v : 0;
            list.Add(new CampObj(int.Parse(m.Groups[1].Value, Ci), System.Net.WebUtility.HtmlDecode(G("CampName")), ocd, D("Heading"), D("PositionX"), D("PositionY")));
        }
        return list;
    }

    /// <summary>Punti di piste (1, 2), vie di rullaggio (3), parcheggi (11, 12) e altri punti a terra (16) della disposizione, in piedi (x nord, y est).</summary>
    /// <summary>Strutture pavimentate (piste, testate, zone di arresto, vie di rullaggio, piazzali): anche loro dentro lo spianamento.</summary>
    private static readonly Regex Pavement = new(@"\b(rwy|runway|stopway|overrun|thr|threshold|tdz|taxi|taxiway|taxiways|apron|ramp|parking|pad|hardstand)\b|^[A-Z]{4} S\d", RegexOptions.IgnoreCase);
    private Func<int, string> _featureName = _ => "";
    /// <summary>Margine attorno alle strutture pavimentate (la posizione e' il centro del modello).</summary>
    public double PavementMarginFt { get; set; } = 1500;

    private List<(double X, double Y, double M)>? GroundPoints(string ocdDir)
    {
        var phd = Directory.EnumerateFiles(ocdDir, "PHD_*.XML").FirstOrDefault();
        var pdx = Directory.EnumerateFiles(ocdDir, "PDX_*.XML").FirstOrDefault();
        if (phd == null || pdx == null) return null;
        // solo obiettivi con almeno una pista (PHD di tipo 1)
        if (!Regex.IsMatch(File.ReadAllText(phd), @"<Type>1</Type>")) return null;
        var pts = new List<(double, double, double)>();
        foreach (Match m in Regex.Matches(File.ReadAllText(pdx), @"<PD Num=""\d+"">(.*?)</PD>", RegexOptions.Singleline))
        {
            string b = m.Groups[1].Value;
            var t = Regex.Match(b, @"<Type>(\d+)</Type>");
            if (!t.Success || int.Parse(t.Groups[1].Value, Ci) is not (1 or 2 or 3 or 11 or 12 or 16)) continue;
            var x = Regex.Match(b, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(b, @"<OffsetY>(.*?)</OffsetY>");
            // nelle disposizioni OffsetX = est, OffsetY = nord (verificato con la direzione delle piste nei PHD)
            if (x.Success && y.Success) pts.Add((double.Parse(y.Groups[1].Value, Ci), double.Parse(x.Groups[1].Value, Ci), MarginFt));
        }
        if (pts.Count < 3) return null;
        var fed = Directory.EnumerateFiles(ocdDir, "FED_*.XML").FirstOrDefault();
        if (fed != null)
            foreach (Match m in Regex.Matches(File.ReadAllText(fed), @"<FED Num=""\d+"">(.*?)</FED>", RegexOptions.Singleline))
            {
                string b = m.Groups[1].Value;
                var ci = Regex.Match(b, @"<FeatureCtIdx>(\d+)</FeatureCtIdx>");
                if (!ci.Success || !Pavement.IsMatch(_featureName(int.Parse(ci.Groups[1].Value, Ci)))) continue;
                var x = Regex.Match(b, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(b, @"<OffsetY>(.*?)</OffsetY>");
                if (x.Success && y.Success) pts.Add((double.Parse(y.Groups[1].Value, Ci), double.Parse(x.Groups[1].Value, Ci), PavementMarginFt));
            }
        return pts;
    }

    private static List<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double, double)>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    private static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }

    private (int, string) MakeTerrainData(TextureContext c, List<CampObj> objs, string outOrd, string outDir, CancellationToken ct, IProgress<double> p)
    {
        Directory.CreateDirectory(outDir);
        string hm = HeightMap(c)!;
        long len = new FileInfo(hm).Length;
        int side = (int)Math.Round(Math.Sqrt(len / 2.0));
        if ((long)side * side * 2 != len) throw new InvalidDataException("HeightMap.raw is not a square int16 map");
        double ftPerPx = c.Project.Theater.SizeMeters / side * FtPerM;
        double sizeFt = c.Project.Theater.SizeMeters * FtPerM;
        var layouts = new Dictionary<int, List<(double X, double Y, double M)>?>();
        var rep = new StringBuilder("CampId;Name;OCD;LevelFt;MinFt;MaxFt;Samples;Points\n");
        int n = 0, k = 0;
        using var fs = new FileStream(hm, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var row = new byte[2];
        foreach (var o in objs)
        {
            ct.ThrowIfCancellationRequested();
            p.Report(0.7 + 0.3 * ++k / objs.Count);
            if (!layouts.TryGetValue(o.Ocd, out var lp))
                layouts[o.Ocd] = lp = GroundPoints(Path.Combine(outOrd, $"OCD_{o.Ocd:00000}"));
            if (lp == null) continue;
            // posizione assoluta (piedi, x nord / y est) con la rotazione dell'obiettivo
            double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            var abs = new List<(double X, double Y)>();
            foreach (var (dx, dy, mg) in lp)
            {
                double rx = dx * ch - dy * sh, ry = dx * sh + dy * ch;
                // margine: un ottagono attorno a ogni punto
                for (int a = 0; a < 8; a++)
                    abs.Add((o.X + rx + mg * Math.Cos(a * Math.PI / 4), o.Y + ry + mg * Math.Sin(a * Math.PI / 4)));
            }
            var poly = Hull(abs);
            if (poly.Count < 3) continue;
            // quota: mediana della heightmap dentro il poligono (solo terra, se c'e')
            double minX = poly.Min(q => q.X), maxX = poly.Max(q => q.X), minY = poly.Min(q => q.Y), maxY = poly.Max(q => q.Y);
            var hs = new List<short>();
            int c0 = Math.Max(0, (int)(minY / ftPerPx)), c1 = Math.Min(side - 1, (int)(maxY / ftPerPx));
            int r0 = Math.Max(0, (int)((sizeFt - maxX) / ftPerPx)), r1 = Math.Min(side - 1, (int)((sizeFt - minX) / ftPerPx));
            var buf = new byte[Math.Max(0, c1 - c0 + 1) * 2];
            for (int r = r0; r <= r1 && buf.Length > 0; r++)
            {
                fs.Position = ((long)r * side + c0) * 2;
                fs.ReadExactly(buf);
                double north = sizeFt - (r + 0.5) * ftPerPx;
                for (int cc = c0; cc <= c1; cc++)
                    if (Inside(poly, north, (cc + 0.5) * ftPerPx)) hs.Add(BitConverter.ToInt16(buf, (cc - c0) * 2));
            }
            if (hs.Count == 0) continue;
            var land = hs.Where(v => v > 0).ToList();
            var use = land.Count >= hs.Count / 4 ? land : hs;
            use.Sort();
            int level = Math.Max(0, (int)use[use.Count / 2]);
            WriteTld(Path.Combine(outDir, $"TLD_{o.Id:00000}.TPB"), level, poly);
            WriteLcd(Path.Combine(outDir, $"LCD_{o.Id:00000}.LPB"), poly);
            rep.Append(o.Id).Append(';').Append(o.Name.Replace(';', ',')).Append(';').Append(o.Ocd).Append(';').Append(level).Append(';')
               .Append(hs.Min()).Append(';').Append(hs.Max()).Append(';').Append(hs.Count).Append(';').Append(poly.Count).Append('\n');
            n++;
        }
        return (n, rep.ToString());
    }

    /// <summary>Piste della disposizione: liste PHD di tipo 8 (estremita' pista), centro in piedi (x nord, y est) e direzione (PHD Data).</summary>
    internal static List<(double X, double Y, double Hdg, double Len, int Num)> LayoutRunways(string ocdDir)
    {
        // Pista = lista PHD di tipo 8: i punti di tipo 8 sono gli angoli dell'asfalto (due per testata).
        // La direzione si ricava dalla geometria (i due punti piu' lontani): in 12 disposizioni del Mideast il campo Data
        // e' sbagliato (anche di 90 gradi). Data serve solo a scegliere il verso (atterraggio) dell'asse.
        var res = new List<(double, double, double, double, int)>();
        var phd = Directory.EnumerateFiles(ocdDir, "PHD_*.XML").FirstOrDefault();
        var pdx = Directory.EnumerateFiles(ocdDir, "PDX_*.XML").FirstOrDefault();
        if (phd == null || pdx == null) return res;
        var pts = new List<(double X, double Y, string T)>();
        int k8 = 0;
        foreach (Match m in Regex.Matches(File.ReadAllText(pdx), @"<PD Num=""\d+"">(.*?)</PD>", RegexOptions.Singleline))
        {
            var x = Regex.Match(m.Groups[1].Value, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(m.Groups[1].Value, @"<OffsetY>(.*?)</OffsetY>");
            var t = Regex.Match(m.Groups[1].Value, @"<Type>(\d+)</Type>");
            pts.Add((double.Parse(y.Groups[1].Value, Ci), double.Parse(x.Groups[1].Value, Ci), t.Success ? t.Groups[1].Value : ""));   // (nord, est, tipo)
        }
        foreach (Match h in Regex.Matches(File.ReadAllText(phd), @"<PHD Num=""\d+"">(.*?)</PHD>", RegexOptions.Singleline))
        {
            string b = h.Groups[1].Value;
            string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
            if (G("Type") != "8") continue;
            int num = int.TryParse(G("RunwayNumber"), NumberStyles.Integer, Ci, out var rn) ? rn : k8;
            k8++;
            int f = int.Parse(G("FirstPtIdx"), Ci), n = int.Parse(G("PointCount"), Ci);
            if (n < 2 || f + n > pts.Count) continue;
            var P = pts.GetRange(f, n).Where(q => q.T == "8").Select(q => (q.X, q.Y)).ToList();
            if (P.Count < 2) P = pts.GetRange(f, n).Select(q => (q.X, q.Y)).ToList();
            var (pa, pb) = P.SelectMany(a => P.Select(q => (a, q))).MaxBy(t => (t.a.X - t.q.X) * (t.a.X - t.q.X) + (t.a.Y - t.q.Y) * (t.a.Y - t.q.Y));
            // I due punti piu' lontani sono una diagonale (errore di ~1 grado = ~90 ft di lato in testata):
            // l'asse vero va dal centro degli angoli di una testata al centro di quelli dell'altra.
            double dN0 = pb.X - pa.X, dE0 = pb.Y - pa.Y, mN0 = (pa.X + pb.X) / 2, mE0 = (pa.Y + pb.Y) / 2;
            var endA = P.Where(q => (q.X - mN0) * dN0 + (q.Y - mE0) * dE0 < 0).ToList();
            var endB = P.Where(q => (q.X - mN0) * dN0 + (q.Y - mE0) * dE0 >= 0).ToList();
            double axis = endA.Count > 0 && endB.Count > 0
                ? Math.Atan2(endB.Average(q => q.Y) - endA.Average(q => q.Y), endB.Average(q => q.X) - endA.Average(q => q.X)) * 180 / Math.PI
                : Math.Atan2(dE0, dN0) * 180 / Math.PI;
            double data = double.TryParse(G("Data"), NumberStyles.Float, Ci, out var hd) ? hd : axis;
            double diff = ((data - axis) % 360 + 540) % 360 - 180;                 // -180..180
            double hdg = ((Math.Abs(diff) > 90 ? axis + 180 : axis) % 360 + 360) % 360;
            double r = hdg * Math.PI / 180, uN = Math.Cos(r), uE = Math.Sin(r);
            var along = P.Select(q => q.X * uN + q.Y * uE).ToList();
            double across = P.Average(q => -q.X * uE + q.Y * uN), mid = (along.Max() + along.Min()) / 2;
            res.Add((mid * uN - across * uE, mid * uE + across * uN, hdg, along.Max() - along.Min(), num));
        }
        return res;
    }

    /// <summary>
    /// Sposta (e ruota) le basi elencate in airbase_targets.csv: il centro della pista principale della disposizione va sul
    /// centro della pista reale piu' lunga (OurAirports) e la sua direzione su quella reale (Heading dell'obiettivo,
    /// la stessa rotazione che la Korea usa per le sue basi). Modifica la lista objs e restituisce la nuova CampObjData.XML.
    /// </summary>
    private static (int, string, string) Relocate(TextureContext c, List<CampObj> objs, string targetsCsv, string outOrd)
    {
        var grid = new TerrainTool.Core.Theater.TheaterGrid(c.Project.Theater);
        var lines = File.ReadAllLines(targetsCsv);
        var head = lines[0].Split(';').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => head.IndexOf(n.ToLowerInvariant());
        int cId = Col("CampId"), cLeLa = Col("LeLat"), cLeLo = Col("LeLon"), cHeLa = Col("HeLat"), cHeLo = Col("HeLon"), cIcao = Col("Icao");
        var rep = new StringBuilder("CampId;Name;Icao;ShiftKm;RotationDeg;OldX;OldY;NewX;NewY;Heading\n");
        var byId = objs.Select((o, i) => (o, i)).ToDictionary(t => t.o.Id, t => t.i);
        var newPos = new Dictionary<int, CampObj>();
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(';');
            if (f.Length <= Math.Max(cHeLo, cId) || !int.TryParse(f[cId], NumberStyles.Integer, Ci, out int id) || !byId.TryGetValue(id, out int idx)) continue;
            double D(int i) => double.Parse(f[i], NumberStyles.Float, Ci);
            var o = objs[idx];
            var rws = LayoutRunways(Path.Combine(outOrd, $"OCD_{o.Ocd:00000}"));
            if (rws.Count == 0) continue;
            var main = rws.OrderByDescending(r => r.Len).First();
            // pista reale in piedi del teatro (x nord, y est)
            var (e1, n1) = grid.ToTheater(D(cLeLa), D(cLeLo)); var (e2, n2) = grid.ToTheater(D(cHeLa), D(cHeLo));
            double rN = (n1 + n2) / 2 * FtPerM, rE = (e1 + e2) / 2 * FtPerM;
            double realHdg = Math.Atan2(e2 - e1, n2 - n1) * 180 / Math.PI;
            // rotazione minima che porta la direzione della pista della disposizione su quella reale (una pista vale nei due sensi)
            double delta = ((realHdg - main.Hdg) % 180 + 180) % 180; if (delta > 90) delta -= 180;
            double hdg = ((o.Heading + delta) % 360 + 360) % 360;
            double h = hdg * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            double cx = main.X * ch - main.Y * sh, cy = main.X * sh + main.Y * ch;
            var moved = o with { X = rN - cx, Y = rE - cy, Heading = hdg };
            objs[idx] = moved; newPos[id] = moved;
            double shift = Math.Sqrt((moved.X - o.X) * (moved.X - o.X) + (moved.Y - o.Y) * (moved.Y - o.Y)) / FtPerM / 1000;
            rep.Append(id).Append(';').Append(o.Name.Replace(';', ',')).Append(';').Append(cIcao >= 0 ? f[cIcao] : "").Append(';')
               .Append(shift.ToString("0.00", Ci)).Append(';').Append(delta.ToString("0.0", Ci)).Append(';')
               .Append(F(o.X)).Append(';').Append(F(o.Y)).Append(';').Append(F(moved.X)).Append(';').Append(F(moved.Y)).Append(';').Append(hdg.ToString("0.000", Ci)).Append('\n');
        }
        // CampObjData.XML con posizioni e direzioni nuove (il resto invariato)
        string xml = File.ReadAllText(OldCampObjData(c)!);
        xml = Regex.Replace(xml, @"<CampObj CampId=""(\d+)"">.*?</CampObj>", m =>
        {
            if (!newPos.TryGetValue(int.Parse(m.Groups[1].Value, Ci), out var o)) return m.Value;
            string v = m.Value;
            v = Regex.Replace(v, "<Heading>.*?</Heading>", $"<Heading>{o.Heading.ToString("0.000", Ci)}</Heading>");
            v = Regex.Replace(v, "<PositionX>.*?</PositionX>", $"<PositionX>{o.X.ToString("0.000", Ci)}</PositionX>");
            v = Regex.Replace(v, "<PositionY>.*?</PositionY>", $"<PositionY>{o.Y.ToString("0.000", Ci)}</PositionY>");
            return v;
        }, RegexOptions.Singleline);
        return (newPos.Count, rep.ToString(), xml);
    }

    private static string F(double v) => v.ToString("0.##", Ci);

    /// <summary>
    /// Basi tolte: in basi_tieni_via.csv (cartella del progetto) la colonna DECISIONE, o la Proposta se e' vuota, vale VIA.
    /// Solo gli obiettivi che sono davvero basi (tipo 1 airbase, 2 airstrip) diventano incroci: disposizione dell'incrocio piu'
    /// usato nel teatro, nome "... Junction". Restituisce CampId → indice CT dell'incrocio.
    /// </summary>
    private static Dictionary<int, int> RemoveObjectives(TextureContext c, string csvName, HashSet<int> types, string what,
        List<CampObj> objs, Dictionary<int, Ct> ct, Dictionary<int, int> ocdType, List<string> notes)
    {
        var res = new Dictionary<int, int>();
        var via = ReadVia(c, csvName, notes);
        return via.Count == 0 ? res : ConvertToJunctions(via, t => types.Contains(t), $"{what} removed ({csvName})", objs, ct, ocdType, notes);
    }

    /// <summary>Posizione e direzione scritte a mano nell'editor (objective_edits.csv). Restituisce quante.</summary>
    private static int ApplyEditPositions(List<CampObj> objs, Dictionary<int, TerrainTool.Core.Editor.ObjectiveEdit> edits)
    {
        int n = 0;
        for (int i = 0; i < objs.Count; i++)
            if (edits.TryGetValue(objs[i].Id, out var e))
            {
                var o = objs[i];
                if (Math.Abs(o.X - e.X) > 0.01 || Math.Abs(o.Y - e.Y) > 0.01 || Math.Abs(o.Heading - e.Heading) > 0.001) n++;
                objs[i] = o with { X = e.X, Y = e.Y, Heading = e.Heading };
            }
        return n;
    }

    private static HashSet<int> ReadVia(TextureContext c, string csvName, List<string> notes)
    {
        var via = new HashSet<int>();
        string csv = Path.Combine(c.Project.ProjectFolder, csvName);
        if (!File.Exists(csv)) return via;
        var lines = File.ReadAllLines(csv);
        var head = lines[0].TrimStart('\uFEFF').Split(';').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int cId = head.IndexOf("campid"), cDec = head.IndexOf("decisione"), cProp = head.IndexOf("proposta");
        if (cId < 0 || cDec < 0) { notes.Add($"{csvName}: CampId/DECISIONE columns not found, nothing removed"); return via; }
        foreach (var l in lines.Skip(1))
        {
            var f = l.Split(';');
            if (f.Length <= Math.Max(cId, cDec) || !int.TryParse(f[cId], NumberStyles.Integer, Ci, out int id)) continue;
            string d = f[cDec].Trim();
            if (d.Length == 0 && cProp >= 0 && cProp < f.Length) d = f[cProp].Trim();
            if (d.Equals("VIA", StringComparison.OrdinalIgnoreCase)) via.Add(id);
        }
        return via;
    }

    /// <summary>Obiettivi → incrocio (disposizione di incrocio piu' usata nel teatro, nome "... Junction"). CampId → indice CT.</summary>
    private static Dictionary<int, int> ConvertToJunctions(HashSet<int> via, Func<int, bool> typeOk, string what,
        List<CampObj> objs, Dictionary<int, Ct> ct, Dictionary<int, int> ocdType, List<string> notes)
    {
        var res = new Dictionary<int, int>();
        // incrocio di riferimento: la disposizione di tipo 15 piu' usata dagli obiettivi del teatro
        int jOcd = objs.Where(o => ocdType.GetValueOrDefault(o.Ocd, -1) == 15).GroupBy(o => o.Ocd).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(-1);
        int jCt = ct.Where(kv => kv.Value.EntityType == 3 && kv.Value.EntityIdx == jOcd).Select(kv => kv.Key).DefaultIfEmpty(-1).Min();
        if (jOcd < 0 || jCt < 0) { notes.Add("No junction layout found: nothing removed"); return res; }
        var skipped = new List<string>();
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (!via.Contains(o.Id)) continue;
            int t = ocdType.GetValueOrDefault(o.Ocd, -1);
            if (t == 15 || !typeOk(t)) { skipped.Add($"{o.Id} {o.Name} (type {t})"); continue; }
            string name = Regex.Replace(o.Name, @"\s*\b(Air ?Strip|Airport|Air ?base|Airfield|Air Field|Bridge|Beach)\b", "", RegexOptions.IgnoreCase).Trim();
            objs[i] = o with { Ocd = jOcd, Name = (name.Length > 0 ? name : o.Name) + " Junction" };
            res[o.Id] = jCt;
        }
        notes.Add($"{what}: {res.Count} turned into junctions (OCD {jOcd}, CT {jCt}), links kept");
        if (skipped.Count > 0) notes.Add($"   left as they are (already junctions or of another type): " + string.Join(", ", skipped));
        return res;
    }

    /// <summary>stations+ils.dat del vecchio add-on con le righe delle basi tolte commentate (niente TACAN/ILS su un incrocio).</summary>
    private static void WriteTld(string path, int level, List<(double X, double Y)> poly)
    {
        var sb = new StringBuilder();
        sb.Append("terrain_level_data {\r\n  terrain_level: ").Append(level).Append("\r\n  terrain_level_polygon {\r\n    terrain_leveling_percentage: 100\r\n");
        foreach (var (x, y) in poly)
            sb.Append("    terrain_level_point {\r\n      sim_grid_pos_x: ").Append(F(x)).Append("\r\n      sim_grid_pos_y: ").Append(F(y)).Append("\r\n    }\r\n");
        sb.Append("  }\r\n}\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteLcd(string path, List<(double X, double Y)> poly)
    {
        var sb = new StringBuilder("landCoverPolygonProtoVec {\r\n  groundType: GTP_AIRBASE\r\n");
        foreach (var (x, y) in poly)
            sb.Append("  point {\r\n    x: ").Append(F(x)).Append("\r\n    y: ").Append(F(y)).Append("\r\n  }\r\n");
        sb.Append("}\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
