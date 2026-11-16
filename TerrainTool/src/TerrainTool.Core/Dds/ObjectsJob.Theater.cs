using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Dds;

/// <summary>
/// "Build objects" of Objective Studio for a theater installed in BMS (any 4.38 theater). Unlike Run Objects of a theater
/// conversion it starts from the theater's own objects database and campaign and applies ONLY the edits saved in the work folder:
/// objectives moved, turned or made junctions, Korean bases, layouts replaced, pieces and points moved, and the bridges plan of
/// Optimize bridges (bridges.csv: junctions, moves onto the river, deck heights). No other automatic fix (real runways, land/water
/// moves) and the database of the theater is never replaced.
/// Output in &lt;work folder&gt;\Output: Objects (copy of the theater's objects data with the edits) and Campaign (CampObjData.XML and
/// the campaign/TE files with the new objective positions); "Install into BMS" copies only what differs.
/// </summary>
public sealed partial class ObjectsJob
{
    public static string InstalledObjectsOut(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "Objects");
    public static string InstalledCampaignOut(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "Campaign");

    public List<string> BuildInstalled(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var proj = c.Project;
        if (!proj.IsInstalledTheater) throw new InvalidOperationException("Not a theater installed in BMS.");
        var notes = new List<string>();
        string src = proj.SourceObjectsDir;
        string campFile = TheaterWorkspace.FindCampObjData(proj.SourceCampaignDir)
            ?? throw new FileNotFoundException("CampObjData.XML not found in " + proj.SourceCampaignDir);
        string dst = InstalledObjectsOut(proj);
        if (proj.IsProtectedPath(dst)) throw new InvalidOperationException("Output folder is protected: " + dst);
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        Directory.CreateDirectory(dst);
        _genericRunways.Clear(); _swappedDirs.Clear(); _swapVar.Clear(); _bake.Clear(); _bridgeTld.Clear(); _bridgeShift.Clear();

        // 1) copy of the theater's objects data: database (XML, .dat, .ini), layouts, terrain leveling. Not the models, not FALCON4.* (BMS rebuilds them)
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(src))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".xml" or ".dat" or ".ini") { CopyKeepTime(f, Path.Combine(dst, Path.GetFileName(f))); n++; }
        }
        foreach (var sub in new[] { "ObjectiveRelatedData", "TerrainRelatedData" })
        {
            string from = Path.Combine(src, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                CopyKeepTime(f, Path.Combine(dst, sub, Path.GetRelativePath(from, f)));
                n++;
            }
        }
        notes.Add($"Copy of the theater's objects: {n} files ({src})");
        p.Report(0.3);

        string outOrd = Path.Combine(dst, "ObjectiveRelatedData");
        var thCt = LoadCt(Path.Combine(dst, "Falcon4_CT.xml"));
        var thFcd = File.Exists(Path.Combine(dst, "Falcon4_FCD.xml")) ? LoadFcdNames(Path.Combine(dst, "Falcon4_FCD.xml")) : new Dictionary<int, string>();
        _featureName = i => thCt.TryGetValue(i, out var r) && r.EntityType == Feature ? thFcd.GetValueOrDefault(r.EntityIdx, "") : "";
        _ctByName = thCt.Where(kv => kv.Value.EntityType == Feature).OrderBy(kv => kv.Key)
            .GroupBy(kv => thFcd.GetValueOrDefault(kv.Value.EntityIdx, "").Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        var ocdType = thCt.Values.Where(v => v.EntityType == 3).GroupBy(v => v.EntityIdx).ToDictionary(g => g.Key, g => g.First().Type);

        // layouts of BMS (Korean bases, BMS layouts): only if the theater's database has the same pieces under the same numbers
        string? bmsObj = Directory.Exists(proj.BmsDataDir) ? Path.Combine(proj.BmsDataDir, "Terrdata", "objects") : null;
        string bmsOrd = bmsObj == null ? "" : Path.Combine(bmsObj, "ObjectiveRelatedData");
        Dictionary<int, Ct>? bmsCt = bmsObj != null && File.Exists(Path.Combine(bmsObj, "Falcon4_CT.xml")) ? LoadCt(Path.Combine(bmsObj, "Falcon4_CT.xml")) : null;
        string? Incompatible(int bmsOcd)
        {
            if (bmsCt == null) return "BMS objects not found";
            string dir = Path.Combine(bmsOrd, $"OCD_{bmsOcd:00000}");
            var fed = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault() : null;
            var ocdXml = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "OCD_*.XML").FirstOrDefault() : null;
            if (ocdXml == null) return $"BMS layout {bmsOcd} not found";
            int oc = CtIdx(ocdXml);
            if (!thCt.TryGetValue(oc, out var tc) || !bmsCt.TryGetValue(oc, out var bc) || tc.EntityType != bc.EntityType || tc.Type != bc.Type)
                return $"the theater's database has another objective under number {oc}";
            if (fed == null) return null;
            foreach (Match m in Regex.Matches(File.ReadAllText(fed), @"<FeatureCtIdx>(\d+)</FeatureCtIdx>"))
            {
                int i = int.Parse(m.Groups[1].Value, Ci);
                if (!bmsCt.TryGetValue(i, out var b) || !thCt.TryGetValue(i, out var t) || b.Graphics != t.Graphics)
                    return $"piece {i} is another model in the theater's database";
            }
            return null;
        }

        var objs = LoadCampObjs(campFile);
        var before = objs.ToDictionary(o => o.Id);
        string work = proj.ProjectFolder;
        var removed = new Dictionary<int, int>();

        // 2) objectives made junctions
        var edits = Editor.ObjectiveEdits.Load(work);
        var junctions = edits.Values.Where(e => e.Junction).Select(e => e.CampId).ToHashSet();
        if (junctions.Count > 0)
            foreach (var kv in ConvertToJunctions(junctions, _ => true, "Objective editor", objs, thCt, ocdType, notes)) removed[kv.Key] = kv.Value;
        // bridges.csv (Optimize bridges): bridges with no water under them become junctions (same place, same links)
        var bridgePlan = Editor.BridgePlan.Load(work);
        if (bridgePlan != null)
        {
            var bj = bridgePlan.Values.Where(r => r.Final == Editor.BridgeAction.Junction && !removed.ContainsKey(r.CampId)).Select(r => r.CampId).ToHashSet();
            if (bj.Count > 0)
                foreach (var kv in ConvertToJunctions(bj, t => t == 6, "Bridges without water (bridges.csv)", objs, thCt, ocdType, notes)) removed[kv.Key] = kv.Value;
        }

        // 3) Korean bases
        var swaps = Editor.BaseSwaps.Load(work);
        foreach (var sw in swaps.Values.ToList())
            if (!sw.FromTheater && Incompatible(sw.KoreaOcd) is string why) { swaps.Remove(sw.CampId); notes.Add($"Korean base for {sw.Name} not used: {why}"); }
        var swapped = swaps.Count > 0 ? SwapLayouts(swaps, objs, removed, bmsOrd, dst, outOrd, notes, theaterOrd: outOrd) : new Dictionary<int, (int Ct, int Features)>();

        // 4) positions and headings
        int manual = ApplyEditPositions(objs, edits);
        if (edits.Count > 0) notes.Add($"Objective editor (objective_edits.csv): {edits.Count} edits, {manual} positions/headings, {junctions.Count} junctions");
        // a moved airbase that shares its layout with other objectives gets its own copy (runway numbers, leveling)
        var newType = CloneSharedLayouts(objs, objs.Where(o => before[o.Id] != o && !removed.ContainsKey(o.Id) && ocdType.GetValueOrDefault(o.Ocd) is 1 or 2)
            .Select(o => o.Id).ToHashSet(), dst, outOrd, notes);
        foreach (var kv in swapped) newType[kv.Key] = kv.Value.Ct;

        // 5) layouts replaced by hand
        var layoutSwaps = Editor.LayoutSwaps.Load(work);
        foreach (var sw in layoutSwaps.Values.Where(s => s.Source == Editor.LayoutSwaps.Bms).ToList())
            if (Incompatible(sw.Ocd) is string why) { layoutSwaps.Remove(sw.CampId); notes.Add($"Layout {sw.LayoutName} for {sw.Name} not used: {why}"); }
        var replaced = ReplaceLayouts(c, layoutSwaps, objs, removed, swapped, bmsOrd, dst, outOrd, ocdType, notes);
        foreach (var kv in replaced) newType[kv.Key] = kv.Value.Ct;

        // 6) pieces and points moved by hand
        var pieceEdits = Editor.PieceEdits.Load(work);
        var pointEdits = Editor.PointEdits.Load(work);
        var pids = new HashSet<int>();
        if (pieceEdits.Count > 0 || pointEdits.Count > 0)
        {
            pids = pieceEdits.Where(kv => kv.Value.Values.Any(e => !e.IsNoOp)).Select(kv => kv.Key)
                .Concat(pointEdits.Where(kv => kv.Value.Values.Any(e => !e.IsNoOp)).Select(kv => kv.Key))
                .Where(id => !removed.ContainsKey(id) && objs.Any(o => o.Id == id)).ToHashSet();
            foreach (var kv in CloneSharedLayouts(objs, pids, dst, outOrd, notes, "objectives with pieces edited by hand")) newType[kv.Key] = kv.Value;
            ApplyPieceEdits(objs, pids.Where(pieceEdits.ContainsKey).ToHashSet(), pieceEdits, outOrd, notes);
            ApplyPointEdits(objs, pids.Where(pointEdits.ContainsKey).ToHashSet(), pointEdits, outOrd, notes);
        }
        // bridges kept with several pieces: piece heights (OffsetZ) so that the deck meets, own copy of a shared layout, ground leveled under the ends
        if (bridgePlan != null && bridgePlan.Values.Any(r => r.Final != Editor.BridgeAction.Junction && r.LevelFt > 0))
        {
            string models = Path.Combine(src, "Models");
            if (!Directory.Exists(models) && bmsObj != null) models = Path.Combine(bmsObj, "Models");
            // only the bridges chosen in bridges.csv (Level? YES in Objective Studio): every other objective is left alone
            var chosen = bridgePlan.Values.Where(r => r.LevelDecision != false && r.LevelFt > 0 && r.Final != Editor.BridgeAction.Junction).Select(r => r.CampId).ToHashSet();
            var noLevel = objs.Select(o => o.Id).Where(id => !chosen.Contains(id)).ToHashSet();
            // bridges the theater already levels itself (its own TLD, like the Korean ones): left as the theater makers did them
            var own = chosen.Where(id => File.Exists(Path.Combine(src, "TerrainRelatedData", $"TLD_{id:00000}.TPB"))).ToList();
            if (own.Count > 0) notes.Add($"Bridge leveling: {own.Count} bridges already leveled by the theater (own TLD) left as they are");
            noLevel.UnionWith(own);
            foreach (var kv in LevelBridges(c, objs, removed, ocdType, dst, outOrd, notes, noLevel, thCt, models))
                newType[kv.Key] = kv.Value;
        }
        if (swapped.Count > 0) GenericRunwaysForTurnedBases(objs, outOrd, swapped, notes);
        int rwyNums = SetRunwayNumbers(outOrd, objs);
        if (rwyNums > 0) notes.Add($"Runway numbers set on the Korean bases' generic runways: {rwyNums} landing directions");
        if (_swappedDirs.Count > 0) FixSwappedSigns(outOrd, objs, notes);
        p.Report(0.6);

        // 7) terrain leveling (TLD/LCD) of the objectives that changed; the ones made junctions lose theirs
        var changed = objs.Where(o => (before[o.Id] != o || pids.Contains(o.Id)) && !removed.ContainsKey(o.Id)).ToList();
        string trd = Path.Combine(dst, "TerrainRelatedData");
        foreach (var id in removed.Keys)
            foreach (var f in new[] { $"TLD_{id:00000}.TPB", $"LCD_{id:00000}.LPB" })
                if (File.Exists(Path.Combine(trd, f))) File.Delete(Path.Combine(trd, f));
        if (changed.Count > 0 && HeightMap(c) != null)
        {
            var (lev, _) = MakeTerrainData(c, changed, outOrd, trd, ct, new Progress<double>(_ => { }));
            notes.Add($"Terrain leveling made again for {lev} changed objectives");
        }
        else if (changed.Count > 0) notes.Add("HeightMap.raw of the theater not found: terrain leveling of the changed objectives not made again");
        if (_bridgeTld.Count > 0) WriteBridgeTld(trd, notes);

        // 8) campaign: the changes for CampObjData.XML and every campaign/TE file of the theater
        var changes = new ObjectChangeSet();
        foreach (var o in objs)
        {
            var b = before[o.Id];
            if (o == b && !newType.ContainsKey(o.Id)) continue;
            changes.Objectives.Add(new ObjectiveChange(o.Id, o.Ocd, o.Name != b.Name ? o.Name : null, o.Heading, o.X, o.Y, edits.ContainsKey(o.Id)));
            changes.Moves.Add(removed.TryGetValue(o.Id, out int jct)
                ? new CampaignArchive.Move(b.Name, b.X, b.Y, o.X, o.Y, o.Heading, jct, o.Id, true, o.Name)
                : new CampaignArchive.Move(o.Name, b.X, b.Y, o.X, o.Y, o.Heading, newType.TryGetValue(o.Id, out int t) ? t : -1, o.Id,
                                           NewFeatures: swapped.TryGetValue(o.Id, out var sw2) ? sw2.Features : replaced.TryGetValue(o.Id, out var rp) ? rp.Features : -1));
        }
        changes.Save(ObjectChangeSet.PathFor(proj));
        string campOut = InstalledCampaignOut(proj);
        if (Directory.Exists(campOut)) Directory.Delete(campOut, true);
        if (changes.Objectives.Count > 0)
        {
            notes.Add($"Campaign: {changes.Objectives.Count} objectives changed");
            foreach (var l in ObjectChangeApplier.Apply(changes, proj, campOut)) notes.Add("   " + l);
        }
        else notes.Add("Campaign: no objective changed, campaign files not touched");
        p.Report(1);
        notes.Add($"Built in {Path.Combine(proj.ProjectFolder, "Output")}. BMS not touched yet: use 'Install into BMS'.");
        return notes;
    }

    private static void CopyKeepTime(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, true);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }
}
