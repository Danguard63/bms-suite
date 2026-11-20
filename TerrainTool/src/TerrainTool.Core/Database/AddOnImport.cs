using System.Globalization;
using System.Text;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Database;

/// <summary>
/// Aircraft, vehicles, ships, weapons and unit types of an old add-on (e.g. a 4.37 theater) brought into a TerrainTool project's
/// database. The choice is kept in the project (imports.csv) and made again at every Run Objects, on top of the fresh copy of
/// BMS's database: the numbers follow the BMS version in use, and what BMS has with the same name stays BMS's (its values).
/// For now only what uses BMS's own 3D models: the theater takes its models from BMS's shared folder, which is never written.
/// </summary>
public static class AddOnImport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public const string FileName = "imports.csv";

    public enum Fit
    {
        /// <summary>Uses a 3D model that BMS has the same: can be brought in.</summary>
        BmsModel,
        /// <summary>BMS has a different model with its number (an updated model of the same aircraft, or another one): it looks like BMS's.</summary>
        OtherModel,
        /// <summary>Its own 3D model, not in BMS.</summary>
        OwnModel,
        /// <summary>A unit type: depends on its vehicles.</summary>
        Unit,
    }

    /// <param name="OwnModelPossible">The add-on's model can be added to BMS's models (own model, or instead of BMS's different one).</param>
    /// <param name="OwnModelDefault">Use the add-on's model unless the user says otherwise (always for an own model).</param>
    public sealed record Candidate(Interchange.Entity Entity, Fit Fit, bool CanImport, string What, string Note, bool OwnModelPossible = false, bool OwnModelDefault = false)
    {
        public int Ct => Entity.Ct;
        public string Name => Entity.Name;
        public string Kind => Interchange.KindName(Entity.Kind);
    }

    public sealed record Analysis(string Version, List<Candidate> Candidates, List<string> Problems);

    /// <summary>The old add-on as a source theater (its Terrdata\Objects holds the database and the models).</summary>
    public static BmsTheater? Source(string addOnFolder)
    {
        string? obj = Sub(addOnFolder, "Terrdata", "Objects");
        if (obj == null || !File.Exists(Path.Combine(obj, "Falcon4_CT.xml"))) return null;
        return new BmsTheater { Name = new DirectoryInfo(addOnFolder).Name, DataDir = addOnFolder, ObjectDir = obj, ModelsDir = obj };
    }

    private static string? Sub(string root, params string[] parts)
    {
        string cur = root;
        foreach (var p in parts)
        {
            if (!Directory.Exists(cur)) return null;
            var d = Directory.EnumerateDirectories(cur).FirstOrDefault(x => Path.GetFileName(x).Equals(p, StringComparison.OrdinalIgnoreCase));
            if (d == null) return null;
            cur = d;
        }
        return cur;
    }

    /// <summary>Which BMS version a database looks like, from its fields (4.38 added the in-service years of vehicles and weapons).</summary>
    public static string VersionOf(BmsDatabase db)
    {
        bool years = db.All("VCD").Concat(db.All("WCD")).Any(r => r.Get("InServiceStart") != null || r.Get("InServiceEnd") != null);
        return years ? "BMS 4.38 database" : "BMS 4.37 or older database (no in-service years on vehicles and weapons)";
    }

    /// <summary>What the add-on has that BMS has not (by name), and whether it can be brought in now.</summary>
    public static Analysis Analyze(string addOnFolder, string bmsData, Dictionary<int, int>? assigned = null, HashSet<string>? installed = null)
    {
        var problems = new List<string>();
        var src = Source(addOnFolder);
        if (src == null) return new Analysis("", new(), new() { $"{addOnFolder}: no Terrdata\\Objects with Falcon4_CT.xml" });
        string bmsObjects = Path.Combine(bmsData, "Terrdata", "objects");
        var S = BmsDatabase.Load(src.ObjectDir);
        var D = BmsDatabase.Load(bmsObjects);
        var dNames = new HashSet<(string, string)>(D.All("VCD").Select(r => ("VCD", r.Name)).Concat(D.All("WCD").Select(r => ("WCD", r.Name)))
                                                   .Concat(D.All("UCD").Select(r => ("UCD", r.Name))));
        var modelUsers = D.All("CT").Where(c => c.Int("GraphicsNormal", 0) > 0).GroupBy(c => c.Int("GraphicsNormal"))
                          .ToDictionary(g => g.Key, g => g.Select(c => D.CtName(c.Num)).First());
        var cache = new Dictionary<int, (Fit, string)>();
        var models = new AddOnModels(src, bmsObjects, new Dictionary<int, int>(assigned ?? new()), installed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        (Fit, string) ModelFit(int g)
        {
            if (g <= 0) return (Fit.BmsModel, "no 3D model");
            if (cache.TryGetValue(g, out var r)) return r;
            string sdir = Path.Combine(src.ModelsDir, "Models", g.ToString(Ci));
            string ddir = Path.Combine(bmsObjects, "Models", g.ToString(Ci));
            bool sHas = File.Exists(Path.Combine(sdir, "Parent.dat")), dHas = File.Exists(Path.Combine(ddir, "Parent.dat"));
            r = !sHas ? (dHas ? (Fit.BmsModel, $"BMS model {g}") : (Fit.OwnModel, $"model {g} is missing in the add-on and in BMS"))
                : !dHas ? (Fit.OwnModel, $"own model {g} (not in BMS)")
                : Interchange.SameFolder(sdir, ddir) ? (Fit.BmsModel, $"BMS model {g} ({modelUsers.GetValueOrDefault(g, "")})")
                : (Fit.OtherModel, $"BMS has a different model {g} (used by {modelUsers.GetValueOrDefault(g, "?")})");
            return cache[g] = r;
        }
        var res = new List<Candidate>();
        // one row per vehicle / weapon / unit: its own CT entry (the extra CT entries of the same record are liveries)
        var ents = Interchange.Entities(S).GroupBy(e => (e.Table, e.Idx))
            .Select(g => (E: g.FirstOrDefault(e => S.Get(e.Table, e.Idx)?.Int("CtIdx") == e.Ct) ?? g.First(), Extra: g.Count() - 1)).ToList();
        foreach (var (e, extra) in ents)
        {
            if (dNames.Contains((e.Table, e.Name))) continue;
            if (e.Table == "UCD")
            {
                var ucd = S.Get("UCD", e.Idx);
                var veh = ucd == null ? new List<(int Ct, int Count, string Name)>() : S.Elements(ucd).Where(x => x.Ct > 0 && x.Count > 0).ToList();
                var bad = new List<string>();
                foreach (var v in veh.Select(v => v.Ct).Distinct())
                {
                    string vn = S.CtName(v);
                    if (dNames.Contains(("VCD", vn))) continue;
                    var (f, w) = ModelFit(S.Get("CT", v)?.Int("GraphicsNormal", 0) ?? 0);
                    int vg = S.Get("CT", v)?.Int("GraphicsNormal", 0) ?? 0;
                    if (f == Fit.OwnModel && !models.Plan(vg).Ok) bad.Add($"{vn}: {models.Plan(vg).Text}");
                }
                res.Add(new Candidate(e, Fit.Unit, bad.Count == 0, e.Detail,
                    bad.Count == 0 ? "" : "not possible: " + string.Join("; ", bad)));
                continue;
            }
            var (fit, what) = ModelFit(e.Model);
            AddOnModels.ModelPlan? plan = fit is Fit.OwnModel or Fit.OtherModel && e.Model > 0 ? models.Plan(e.Model) : null;
            string note = fit switch
            {
                Fit.OtherModel => "BMS's model is used unless you tick 'Add-on model': right if BMS only updated the same aircraft (e.g. a newer F-15C), wrong if it is another one" +
                                  (plan is { Ok: true } ? $"; with 'Add-on model': {plan.Text}" : plan != null ? $"; the add-on's model cannot be added: {plan.Problem}" : ""),
                Fit.OwnModel => plan is { Ok: true } ? "its model is added to BMS's models (nothing of BMS replaced)" : $"not possible: {plan?.Problem}",
                _ => "",
            };
            if (fit == Fit.OwnModel && plan is { Ok: true }) what = plan.Text;
            if (extra > 0) what += $"; {extra} more CT entr{(extra == 1 ? "y" : "ies")} of the same record (liveries) not brought";
            bool can = fit is Fit.BmsModel or Fit.OtherModel || plan is { Ok: true };
            res.Add(new Candidate(e, fit, can, what, note, OwnModelPossible: plan is { Ok: true }, OwnModelDefault: fit == Fit.OwnModel));
        }
        return new Analysis(VersionOf(S), res, problems);
    }

    // ───────────────────────── the project's choice (imports.csv)

    /// <param name="OwnModel">Use the add-on's 3D model (added to BMS's models) instead of BMS's one with the same number.</param>
    public sealed record Choice(string Source, int Ct, string Name, bool OwnModel = false);

    public static string FilePath(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, FileName);

    public static List<Choice> Load(Project.TheaterProject p)
    {
        var res = new List<Choice>();
        string f = FilePath(p);
        if (!File.Exists(f)) return res;
        foreach (var raw in File.ReadAllLines(f).Skip(1))
        {
            var c = raw.Split(';');
            if (c.Length < 3 || !int.TryParse(c[1].Trim(), NumberStyles.Integer, Ci, out int ct)) continue;
            res.Add(new Choice(c[0].Trim(), ct, c[2].Trim(), c.Length > 3 && c[3].Trim() == "1"));
        }
        return res;
    }

    public static void Save(Project.TheaterProject p, IEnumerable<Choice> choices)
    {
        var sb = new StringBuilder("Source;Ct;Name;OwnModel\r\n");
        foreach (var c in choices) sb.Append(c.Source).Append(';').Append(c.Ct.ToString(Ci)).Append(';').Append(c.Name.Replace(';', ',')).Append(';').Append(c.OwnModel ? "1" : "0").Append("\r\n");
        File.WriteAllText(FilePath(p), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Brings the chosen entries into the database being built (dbDir, already a copy of BMS's with DbEdits.txt) and writes
    /// Output\Install\imports_map.csv (name → numbers in this build). Called by the Objects job.
    /// </summary>
    public static List<string> Apply(Project.TheaterProject p, string dbDir, string bmsData, string mapFile)
    {
        var notes = new List<string>();
        var choices = Load(p);
        string filesList = Path.Combine(p.ProjectFolder, "Output", "Install", AddOnModels.FilesFile);
        if (File.Exists(filesList)) File.Delete(filesList);
        if (choices.Count == 0) return notes;
        var map = new StringBuilder("Name;Kind;SourceCt;Ct;EntityIdx\r\n");
        var files = new StringBuilder("Rel;From\r\n");
        string bmsObjects = Path.Combine(bmsData, "Terrdata", "objects");
        var assigned = AddOnModels.LoadAssigned(p.ProjectFolder);
        var installed = AddOnModels.LoadInstalled(p.ProjectFolder);
        foreach (var g in choices.GroupBy(c => c.Source, StringComparer.OrdinalIgnoreCase))
        {
            var src = Source(g.Key);
            if (src == null) { notes.Add($"Imports: {g.Key} not found (no Terrdata\\Objects): {g.Count()} entries skipped"); continue; }
            var S = BmsDatabase.Load(src.ObjectDir);
            var cts = new List<int>();
            var models = new AddOnModels(src, bmsObjects, assigned, installed);
            var modelMap = new Dictionary<int, int>();
            foreach (var c in g)
            {
                // the number in the source, checked by name (the add-on could have been updated)
                int ct = S.Get("CT", c.Ct) != null && S.CtName(c.Ct) == c.Name ? c.Ct
                    : S.All("CT").FirstOrDefault(x => S.CtName(x.Num) == c.Name)?.Num ?? -1;
                if (ct < 0) { notes.Add($"Imports: '{c.Name}' is no longer in {src.Name}: skipped"); continue; }
                cts.Add(ct);
                // an aircraft brings its squadron and flight types (TE and campaigns need them): the add-on's air units made only of it
                if (S.EntityOf(ct) is { Table: "VCD" } && S.Get("CT", ct)?.Int("Domain") == 2)
                    foreach (var u in S.All("CT").Where(u => u.Int("Domain") == 2 && BmsDatabase.EntityTable(u.Int("EntityType")) == "UCD"))
                    {
                        if (cts.Contains(u.Num) || S.EntityOf(u.Num) is not { } ue) continue;
                        var el = S.Elements(ue).Where(x => x.Ct > 0 && x.Count > 0).ToList();
                        if (el.Count == 0 || el.Any(x => x.Ct != ct)) continue;
                        cts.Add(u.Num);
                        notes.Add($"Imports: {c.Name}: its {(u.Int("Type") == 3 ? "squadron" : "flight")} type '{ue.Name}' (CT {u.Num}) comes along");
                    }
                // 3D models: the add-on's own ones (not in BMS) always, and instead of BMS's different one when chosen
                var withVehicles = new List<int> { ct };
                if (S.EntityOf(ct) is { } ent && ent.Table == "UCD") withVehicles.AddRange(S.Elements(ent).Where(x => x.Ct > 0 && x.Count > 0).Select(x => x.Ct).Distinct());
                foreach (int vct in withVehicles)
                    foreach (var gf in new[] { "GraphicsNormal", "GraphicsRepaired", "GraphicsDamaged", "GraphicsDestroyed", "GraphicsLeftDestroyed", "GraphicsRightDestroyed", "GraphicsBothDestroyed" })
                    {
                        int m = S.Get("CT", vct)?.Int(gf, 0) ?? 0;
                        if (m <= 0 || modelMap.ContainsKey(m) || models.SameAsBms(m)) continue;
                        bool bmsHas = Directory.Exists(Path.Combine(bmsObjects, "Models", m.ToString(Ci)));
                        if (bmsHas && !c.OwnModel) continue;     // BMS's (different) model, as chosen
                        var mp = models.Plan(m);
                        if (!mp.Ok) { notes.Add($"Imports: {S.CtName(vct)}: {mp.Problem}"); continue; }
                        modelMap[m] = mp.Target;
                        foreach (var (rel, from) in models.Files(mp)) files.Append(rel).Append(';').Append(from).Append("\r\n");
                        notes.Add($"Imports: {S.CtName(vct)}: {mp.Text}");
                    }
            }
            if (cts.Count == 0) continue;
            var dst = new BmsTheater { Name = p.Theater.Name, DataDir = bmsData, ObjectDir = dbDir, ModelsDir = bmsObjects };
            var plan = Interchange.MakeEntities(src, dst, bmsData, cts, preferDestination: true, modelMap: modelMap);
            // what comes along (weapons, pods, tanks the add-on has and BMS has not): their 3D models too, when BMS has no
            // model with that number or a different one; then the plan again with those numbers
            int before = modelMap.Count;
            foreach (var (t, _, f) in plan.Records.Where(r => r.Table == "CT"))
                foreach (var (field, value) in f.Where(x => x.Field.StartsWith("Graphics", StringComparison.Ordinal)))
                {
                    if (!int.TryParse(value, NumberStyles.Integer, Ci, out int m) || m <= 0 || modelMap.ContainsKey(m) || modelMap.ContainsValue(m) || models.SameAsBms(m)) continue;
                    if (!Directory.Exists(Path.Combine(src.ModelsDir, "Models", m.ToString(Ci)))) continue;
                    var mp = models.Plan(m);
                    if (!mp.Ok) { notes.Add($"Imports: model {m}: {mp.Problem}"); continue; }
                    modelMap[m] = mp.Target;
                    foreach (var (rel, from) in models.Files(mp)) files.Append(rel).Append(';').Append(from).Append("\r\n");
                    notes.Add($"Imports: model {m} (a weapon or store that comes along): {mp.Text}");
                }
            if (modelMap.Count > before)
                plan = Interchange.MakeEntities(src, dst, bmsData, cts, preferDestination: true, modelMap: modelMap);
            foreach (var pr in plan.Problems) notes.Add("Imports: problem: " + pr);
            // the tables with the new records, written over the database being built
            if (plan.Records.Count > 0)
            {
                string work = Path.Combine(p.ProjectFolder, "Output", "Imports");
                var edits = DbEdits.Open(Path.Combine(work, "plan"), dst.Name, dbDir);
                edits.Data.Records.Clear();
                foreach (var (t, n, f) in plan.Records) edits.Set(t, n, f);
                string tables = Path.Combine(work, "tables");
                foreach (var f in DbInstaller.Write(edits, dbDir, tables)) File.Copy(f, Path.Combine(dbDir, Path.GetFileName(f)), true);
            }
            foreach (var (target, (from, text)) in plan.Files)
            {
                if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(dbDir), StringComparison.OrdinalIgnoreCase))
                { notes.Add($"Imports: {target} is outside the theater's database: not written"); continue; }
                if (from != null) File.Copy(from, target, true); else File.WriteAllText(target, text!, new UTF8Encoding(false));
            }
            notes.Add($"Imports from {src.Name} ({VersionOf(S)}): {plan.Report.FirstOrDefault() ?? ""}");
            notes.AddRange(plan.Report.Skip(1).Select(r => "Imports: " + r.Trim()));
            var D = BmsDatabase.Load(dbDir);
            foreach (int ct in cts)
            {
                string name = S.CtName(ct);
                var sc = S.Get("CT", ct);
                var dc = D.All("CT").Where(x => D.CtName(x.Num) == name && x.Int("EntityType") == sc?.Int("EntityType") && x.Int("Type") == sc?.Int("Type"))
                    .OrderByDescending(x => x.Num).FirstOrDefault();
                if (dc != null) map.Append(name.Replace(';', ',')).Append(';').Append(BmsDatabase.EntityTable(dc.Int("EntityType"))).Append(';')
                                   .Append(ct.ToString(Ci)).Append(';').Append(dc.Num.ToString(Ci)).Append(';').Append(dc.Int("EntityIdx").ToString(Ci)).Append("\r\n");
            }
        }
        AddOnModels.SaveAssigned(p.ProjectFolder, assigned);
        if (files.Length > "Rel;From\r\n".Length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filesList)!);
            File.WriteAllText(filesList, files.ToString(), new UTF8Encoding(false));
            notes.Add("Imports: the add-on's own 3D models are added to BMS's models by 'Install objects' (only added, nothing of BMS replaced)");
        }
        // numbers of the previous build: if they changed (a BMS update), campaigns that use them must be updated
        string old = File.Exists(mapFile) ? File.ReadAllText(mapFile) : "";
        if (old.Length > 0 && old != map.ToString())
            notes.Add("Imports: the numbers of the imported entries changed since the last Run Objects (BMS update or different choice): " +
                      "campaigns using them need their unit types checked (Campaign Editor, Convert unit types).");
        Directory.CreateDirectory(Path.GetDirectoryName(mapFile)!);
        File.WriteAllText(mapFile, map.ToString(), new UTF8Encoding(false));
        return notes;
    }
}
