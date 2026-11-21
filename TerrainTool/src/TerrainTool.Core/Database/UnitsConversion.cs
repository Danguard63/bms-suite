using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Database;

/// <summary>
/// 4.37 Units Converter: aircraft, helicopters, vehicles, ships and weapons of an old BMS database (a 4.37 installation, an
/// add-on or just its objects folder) converted into a theater of a BMS 4.38 installation, from start to end:
/// <list type="number">
/// <item>3D models: the source's own models and textures, with a free number when BMS has another one with theirs;</item>
/// <item>hardpoint lists and racks: compared by their weapons (an old add-on often changed BMS's lists keeping the names);</item>
/// <item>weapons: BMS's ones with the same name, the others added with their models;</item>
/// <item>aircraft and vehicles: free class numbers (Specific), first year of service, the flight model of the same name in
/// BMS 4.38's AcTypes.lst (the flight models themselves are BMS's and are never copied nor changed);</item>
/// <item>squadron and flight types of an aircraft;</item>
/// <item>the Tactical Engagement: a line in the theater's TePlanes.lst and the name in the theater's interface files.</item>
/// </list>
/// Everything is installed with a backup first (Restore puts the theater back as it was). What was converted is listed in
/// the theater's objects folder (UnitsConverter.csv).
/// </summary>
public static class UnitsConversion
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    /// <summary>In the destination's objects folder: what was converted (one line per item).</summary>
    public const string StateFile = "UnitsConverter.csv";
    /// <summary>In the destination's objects folder: source model number → number given in BMS's models.</summary>
    public const string ModelsFile = "UnitsConverter_models.csv";
    /// <summary>In the destination's objects folder: the choices, to convert them again when the theater's database is rebuilt
    /// (TerrainTool's Install objects, or a BMS update of the theater).</summary>
    public const string PicksFile = "UnitsConverter_picks.csv";

    public sealed record SourceDb(BmsTheater Theater, string Label, string? AcTypes, string Version)
    {
        public bool Is438 => Version.StartsWith("BMS 4.38", StringComparison.Ordinal);
        public override string ToString() => Label;
    }

    public sealed record Destination(BmsTheater Theater, string DataDir, string? Problem)
    {
        public override string ToString() => Theater.Name + (Problem != null ? "  (cannot be written)" : "");
    }

    /// <summary>The Data folder of an installation (the folder itself or its Data subfolder), or null.</summary>
    public static string? DataDir(string root)
    {
        foreach (var d in new[] { root, Path.Combine(root, "Data") })
            if (File.Exists(Path.Combine(d, "TerrData", "TheaterDefinition", "theater.lst"))) return d;
        return null;
    }

    // ───────────────────────── source

    /// <summary>
    /// The databases in a folder: an objects folder (Falcon4_CT.xml), an add-on (Terrdata\Objects) or a whole installation
    /// (one per theater with its own objects folder).
    /// </summary>
    public static List<SourceDb> FindSources(string path)
    {
        var res = new List<SourceDb>();
        if (!Directory.Exists(path)) return res;
        if (File.Exists(Path.Combine(path, "Falcon4_CT.xml")))
        {
            var t = new BmsTheater { Name = new DirectoryInfo(path).Name, DataDir = path, ObjectDir = path, ModelsDir = path };
            res.Add(MakeSource(t, $"objects folder {path}", FindAcTypes(path)));
            return res;
        }
        if (AddOnImport.Source(path) is { } a)
        {
            res.Add(MakeSource(a, $"add-on {a.Name}", FindAcTypes(a.ObjectDir)));
            return res;
        }
        if (DataDir(path) is { } data)
            foreach (var t in BmsTheater.List(data).GroupBy(t => Path.GetFullPath(t.ObjectDir), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                if (File.Exists(Path.Combine(t.ObjectDir, "Falcon4_CT.xml")))
                    res.Add(MakeSource(t, $"{t.Name}  ({Path.GetRelativePath(data, t.ObjectDir)})", FindAcTypes(t.ObjectDir) ?? SimAcTypes(data, t)));
        return res;
    }

    private static SourceDb MakeSource(BmsTheater t, string label, string? acTypes)
    {
        string version;
        try { version = AddOnImport.VersionOf(BmsDatabase.Load(t.ObjectDir)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException) { version = "unreadable: " + ex.Message; }
        return new SourceDb(t, label, acTypes, version);
    }

    /// <summary>Sim\Acdata\AcTypes.lst next to an objects folder (an add-on's own Sim, or the installation's), or null.</summary>
    private static string? FindAcTypes(string objectDir)
    {
        var d = new DirectoryInfo(objectDir);
        for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            foreach (var cand in new[] { Path.Combine(d.FullName, "Sim", "Acdata", "AcTypes.lst"), Path.Combine(d.FullName, "Data", "Sim", "Acdata", "AcTypes.lst") })
                if (File.Exists(cand)) return cand;
        return null;
    }

    /// <summary>The theater's AcTypes.lst: its simdatadir (.tdf) or the installation's Sim.</summary>
    private static string? SimAcTypes(string data, BmsTheater t)
    {
        string sim = Path.Combine(data, "Sim");
        try
        {
            foreach (var raw in File.ReadAllLines(t.Tdf))
            {
                var l = raw.Trim();
                if (!l.StartsWith("simdatadir", StringComparison.OrdinalIgnoreCase)) continue;
                string v = l["simdatadir".Length..].Trim();
                if (v.Length > 0) sim = Path.GetFullPath(Path.Combine(data, v.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { }
        string f = Path.Combine(sim, "Acdata", "AcTypes.lst");
        if (File.Exists(f)) return f;
        f = Path.Combine(data, "Sim", "Acdata", "AcTypes.lst");
        return File.Exists(f) ? f : null;
    }

    // ───────────────────────── destination

    /// <summary>The theaters of a BMS 4.38 installation; Problem says why one cannot be written.</summary>
    public static List<Destination> FindDestinations(string root)
    {
        var res = new List<Destination>();
        var data = DataDir(root);
        if (data == null) return res;
        foreach (var t in BmsTheater.List(data))
        {
            string? why = DbInstaller.IsShared(data, t.ObjectDir) ? "it uses BMS's own database (Terrdata\\objects): convert into an add-on theater"
                : !File.Exists(Path.Combine(t.ObjectDir, "Falcon4_CT.xml")) ? $"no database in {t.ObjectDir}"
                : Same(t.CampaignDir, Path.Combine(data, "Campaign")) ? "it uses BMS's own Campaign folder"
                : null;
            res.Add(new Destination(t, data, why));
        }
        return res;
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>BMS's own folders: the destination's 3D models and interface files may be there (they are only added to, or not written).</summary>
    private static string BmsObjects(Destination d) => Path.Combine(d.DataDir, "Terrdata", "objects");

    /// <summary>The interface art the theater has of its own (artdir of its .tdf), or null when it uses BMS's (Data\Art).</summary>
    private static string? OwnArtRoot(Destination d)
    {
        string a = d.Theater.ArtDir;
        if (a.Length == 0 || Same(a, d.DataDir) || Same(a, Path.Combine(d.DataDir, "Art"))) return null;
        return a;
    }

    public static Dictionary<int, int> LoadModels(Destination d) => ReadPairs(Path.Combine(d.Theater.ObjectDir, ModelsFile));

    private static Dictionary<int, int> ReadPairs(string f)
    {
        var res = new Dictionary<int, int>();
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length >= 2 && int.TryParse(c[0], NumberStyles.Integer, Ci, out int a) && int.TryParse(c[1], NumberStyles.Integer, Ci, out int b)) res[a] = b;
        }
        return res;
    }

    /// <summary>What the source has that the destination theater has not (by name).</summary>
    public static AddOnImport.Analysis Analyze(SourceDb s, Destination d) =>
        AddOnImport.Analyze(s.Theater, d.Theater.ObjectDir, d.Theater.ModelsDir, LoadModels(d));

    /// <summary>The theater's TE aircraft list (its own TePlanes.lst, else BMS's), for the picture/icon of a new aircraft.</summary>
    public static TePlanesFile TePlanes(Destination d)
    {
        string own = Path.Combine(d.Theater.CampaignDir, "TePlanes.lst"), bms = Path.Combine(d.DataDir, "Campaign", "TePlanes.lst");
        return File.Exists(own) ? TePlanesFile.Load(own) : File.Exists(bms) ? TePlanesFile.Load(bms) : new TePlanesFile();
    }

    // ───────────────────────── the conversion

    /// <param name="TeName">Name in the Tactical Engagement (aircraft and helicopters); null = the database name.</param>
    /// <param name="TeTemplate">ID of the TePlanes entry whose picture and icon it takes; -1 = the aircraft with the same flight model.</param>
    public sealed record Pick(int Ct, string Name, bool OwnModel, string? TeName = null, int TeTemplate = -1);

    public static readonly string[] StepNames =
    {
        "1 · 3D models", "2 · Hardpoint lists and racks", "3 · Weapons and stores", "4 · Aircraft and vehicles", "5 · Squadron and flight types", "6 · Tactical Engagement",
    };

    public sealed class Conversion
    {
        public Interchange.Plan Plan = null!;
        public readonly List<string>[] Steps = Enumerable.Range(0, 6).Select(_ => new List<string>()).ToArray();
        public readonly List<string> General = new();
        public readonly List<string> Problems = new();
        public bool HasWork => Plan.Records.Count > 0 || Plan.Files.Count > 0;

        public string Text()
        {
            var sb = new StringBuilder();
            foreach (var g in General) sb.Append(g).Append("\r\n");
            for (int i = 0; i < 6; i++)
            {
                sb.Append("\r\n").Append(StepNames[i]).Append("\r\n");
                if (Steps[i].Count == 0) sb.Append("   nothing to do\r\n");
                foreach (var l in Steps[i]) sb.Append("   ").Append(l.Trim()).Append("\r\n");
            }
            if (Problems.Count > 0)
            {
                sb.Append("\r\nPROBLEMS\r\n");
                foreach (var pr in Problems) sb.Append("   ").Append(pr).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    private static readonly string[] GraphicsFields = { "GraphicsNormal", "GraphicsRepaired", "GraphicsDamaged", "GraphicsDestroyed", "GraphicsLeftDestroyed", "GraphicsRightDestroyed", "GraphicsBothDestroyed" };

    /// <summary>Everything the conversion would write (nothing is written here); workDir holds the files it prepares.</summary>
    public static Conversion MakePlan(SourceDb s, Destination d, IList<Pick> picks, string workDir)
    {
        var c = new Conversion();
        Directory.CreateDirectory(workDir);
        var src = s.Theater;
        var dst = d.Theater;
        var S = BmsDatabase.Load(src.ObjectDir);
        var D = BmsDatabase.Load(dst.ObjectDir);
        string modelsRoot = dst.ModelsDir;
        c.General.Add($"{src.Name} ({s.Version}) → {dst.Name} ({dst.ObjectDir})");

        // what is converted: the chosen entries, and the squadron and flight types of an aircraft (TE and campaigns need them)
        var cts = new List<int>();
        var byCt = new Dictionary<int, Pick>();
        foreach (var p in picks)
        {
            int ct = S.Get("CT", p.Ct) != null && S.CtName(p.Ct) == p.Name ? p.Ct : S.All("CT").FirstOrDefault(x => S.CtName(x.Num) == p.Name)?.Num ?? -1;
            if (ct < 0) { c.General.Add($"'{p.Name}' is not in {src.Name}: skipped"); continue; }
            cts.Add(ct); byCt[ct] = p;
            if (S.EntityOf(ct) is { Table: "VCD" } && S.Get("CT", ct)?.Int("Domain") == 2)
                foreach (var u in S.All("CT").Where(u => u.Int("Domain") == 2 && BmsDatabase.EntityTable(u.Int("EntityType")) == "UCD"))
                {
                    if (cts.Contains(u.Num) || S.EntityOf(u.Num) is not { } ue) continue;
                    var el = S.Elements(ue).Where(x => x.Ct > 0 && x.Count > 0).ToList();
                    if (el.Count == 0 || el.Any(x => x.Ct != ct)) continue;
                    cts.Add(u.Num);
                    c.Steps[4].Add($"{p.Name}: its {(u.Int("Type") == 3 ? "squadron" : "flight")} type '{ue.Name}' (CT {u.Num}) comes along");
                }
        }
        if (cts.Count == 0) { c.Plan = new Interchange.Plan { Src = src, Dst = dst, BmsObjects = BmsObjects(d) }; c.Problems.Add("Nothing chosen."); return c; }

        // 1 · 3D models: the source's own ones (not in BMS) always, and instead of BMS's different one when chosen
        var assigned = LoadModels(d);
        var models = new AddOnModels(src, modelsRoot, assigned, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var modelMap = new Dictionary<int, int>();
        var modelFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddModel(int m, string forWhat)
        {
            var mp = models.Plan(m);
            if (!mp.Ok) { c.Problems.Add($"{forWhat}: {mp.Problem}"); return; }
            modelMap[m] = mp.Target;
            foreach (var (rel, from) in models.Files(mp)) modelFiles[Path.Combine(modelsRoot, rel)] = from;
            c.Steps[0].Add($"{forWhat}: {mp.Text}");
        }
        foreach (int ct in cts)
        {
            var p = byCt.GetValueOrDefault(ct);
            var withVehicles = new List<int> { ct };
            if (S.EntityOf(ct) is { Table: "UCD" } ent) withVehicles.AddRange(S.Elements(ent).Where(x => x.Ct > 0 && x.Count > 0).Select(x => x.Ct).Distinct());
            foreach (int vct in withVehicles)
                foreach (var gf in GraphicsFields)
                {
                    int m = S.Get("CT", vct)?.Int(gf, 0) ?? 0;
                    if (m <= 0 || modelMap.ContainsKey(m) || models.SameAsBms(m)) continue;
                    bool bmsHas = Directory.Exists(Path.Combine(modelsRoot, "Models", m.ToString(Ci)));
                    bool own = p?.OwnModel ?? byCt.Values.Any(x => x.OwnModel);
                    if (bmsHas && !own) { c.Steps[0].Add($"{S.CtName(vct)}: BMS's model {m} (different from the source's: tick 'Source model' to bring the old one)"); continue; }
                    AddModel(m, $"{S.CtName(vct)} ({gf.Replace("Graphics", "").ToLowerInvariant()})");
                }
        }

        // 4 · the flight model of the same name in the two AcTypes.lst
        var airframes = AirframeMap(s.AcTypes, DestAcTypes(d), c);

        var plan = Interchange.MakeEntities(src, dst, d.DataDir, cts, preferDestination: true, modelMap: modelMap, airframeMap: airframes);
        // what comes along (weapons, pods, tanks the source has and BMS has not): their 3D models too, then the plan again
        int before = modelMap.Count;
        foreach (var (t, _, f) in plan.Records.Where(r => r.Table == "CT"))
            foreach (var (field, value) in f.Where(x => x.Field.StartsWith("Graphics", StringComparison.Ordinal)))
            {
                if (!int.TryParse(value, NumberStyles.Integer, Ci, out int m) || m <= 0 || modelMap.ContainsKey(m) || modelMap.ContainsValue(m) || models.SameAsBms(m)) continue;
                if (!Directory.Exists(Path.Combine(src.ModelsDir, "Models", m.ToString(Ci)))) continue;
                AddModel(m, $"model {m} (a weapon or store that comes along)");
            }
        if (modelMap.Count > before)
            plan = Interchange.MakeEntities(src, dst, d.DataDir, cts, preferDestination: true, modelMap: modelMap, airframeMap: airframes);
        c.Plan = plan;
        c.Problems.AddRange(plan.Problems);
        foreach (var (target, from) in modelFiles)
        {
            if (File.Exists(target) && AddOnModels.SameFile(from, target)) continue;   // already there (an earlier conversion)
            plan.Files[target] = (from, null);
        }
        Sort(c, plan);

        // 6 · Tactical Engagement
        var D2 = new Lookup(D, plan);
        TacticalEngagement(c, d, S, D2, cts, byCt, workDir);

        // what was converted, in the theater (for the next conversion and for Restore)
        State(c, d, s, S, D2, cts, assigned, workDir, picks);
        // first line of the backup's report: what this conversion is
        plan.Report.Insert(0, $"{string.Join(", ", picks.Select(p => p.Name))} from {src.Name} into {dst.Name}");
        return c;
    }

    /// <summary>Report lines of the entities plan, step by step.</summary>
    private static void Sort(Conversion c, Interchange.Plan plan)
    {
        if (plan.Report.Count > 0) c.General.Add(plan.Report[0]);
        foreach (var raw in plan.Report.Skip(1))
        {
            string l = raw.Trim();
            int step = l.Contains("WLD", StringComparison.Ordinal) || l.Contains("BmsRack", StringComparison.Ordinal) ? 1
                : l.Contains("WCD", StringComparison.Ordinal) || l.Contains("SSD", StringComparison.Ordinal) || l.Contains("RKT", StringComparison.Ordinal) || l.Contains("SWD", StringComparison.Ordinal) ? 2
                : l.Contains("UCD", StringComparison.Ordinal) || l.Contains("as its aircraft", StringComparison.Ordinal) ? 4
                : l.StartsWith("model", StringComparison.Ordinal) ? 0
                : l.StartsWith("Note:", StringComparison.Ordinal) ? -1
                : 3;
            if (step < 0) continue;
            c.Steps[step].Add(l);
        }
        int wld = plan.Records.Count(r => r.Table == "WLD");
        c.Steps[1].Insert(0, wld == 0 ? "the hardpoint lists of BMS with the same weapons are used" : $"{wld} hardpoint list(s) added with the source's weapons (BMS's lists of the same name have other weapons)");
    }

    /// <summary>The destination's records as they will be: the plan's new ones first, then the database.</summary>
    private sealed class Lookup
    {
        public readonly BmsDatabase D;
        private readonly Dictionary<(string, int), Dictionary<string, string>> _new = new();
        public Lookup(BmsDatabase d, Interchange.Plan p)
        {
            D = d;
            foreach (var (t, n, f) in p.Records) _new[(t, n)] = f.ToDictionary(x => x.Field, x => x.Value.Trim());
        }
        public string? Get(string table, int num, string field) =>
            _new.TryGetValue((table, num), out var f) ? f.GetValueOrDefault(field) : D.Get(table, num)?.Get(field);
        public int Int(string table, int num, string field, int def = -1) =>
            int.TryParse(Get(table, num, field), NumberStyles.Integer, Ci, out int v) ? v : def;
        public bool IsNew(string table, int num) => _new.ContainsKey((table, num));
    }

    private static string? DestAcTypes(Destination d) => SimAcTypes(d.DataDir, d.Theater);

    private static List<string> ReadAcTypes(string f) =>
        File.ReadAllLines(f).Skip(1).Select(l => l.Trim()).ToList();   // first line: how many

    private static Dictionary<int, int>? AirframeMap(string? srcList, string? dstList, Conversion c)
    {
        if (srcList == null || dstList == null)
        {
            c.Steps[3].Add($"flight models: {(srcList == null ? "the source's" : "BMS 4.38's")} AcTypes.lst not found: the source's flight model numbers are kept (check the aircraft in BMS)");
            return null;
        }
        var a = ReadAcTypes(srcList); var b = ReadAcTypes(dstList);
        var map = new Dictionary<int, int>();
        for (int i = 0; i < a.Count; i++)
        {
            int j = b.FindIndex(x => x.Equals(a[i], StringComparison.OrdinalIgnoreCase));
            if (j >= 0) map[i] = j;
        }
        c.Steps[3].Add($"flight models: by name ({srcList} → {dstList}); the flight models are BMS 4.38's, never copied nor changed");
        return map;
    }

    // ───────────────────────── Tactical Engagement

    private static void TacticalEngagement(Conversion c, Destination d, BmsDatabase S, Lookup D, List<int> cts, Dictionary<int, Pick> byCt, string workDir)
    {
        var plan = c.Plan;
        var te = TePlanes(d);
        bool changed = false;
        string? art = OwnArtRoot(d);
        var names = art != null ? new UiNames(d, art) : null;
        foreach (int ct in cts)
        {
            if (!byCt.TryGetValue(ct, out var pick)) continue;
            if (S.EntityOf(ct) is not { Table: "VCD" } || S.Get("CT", ct)?.Int("Domain") != 2) continue;
            if (!plan.CtMap.TryGetValue(ct, out int vct)) continue;
            // its squadron type (the TE offers squadrons: UnitSubType = the squadron's SubType)
            int sq = cts.Where(u => S.EntityOf(u) is { Table: "UCD" } && S.Get("CT", u)?.Int("Type") == 3 && S.Elements(S.EntityOf(u)!).Any(x => x.Ct == ct))
                        .Select(u => plan.CtMap.GetValueOrDefault(u, -1)).FirstOrDefault(-1);
            if (sq < 0) { c.Steps[5].Add($"{pick.Name}: no squadron type in the source: not offered in the TE"); continue; }
            int type = D.Int("CT", vct, "Type"), sub = D.Int("CT", vct, "SubType"), spec = D.Int("CT", vct, "Specific"), usub = D.Int("CT", sq, "SubType");
            if (te.Entries.Any(e => e.Type == type && e.SubType == sub && e.Specific == spec && e.UnitSubType == usub))
            { c.Steps[5].Add($"{pick.Name}: already in the TE list"); continue; }
            var tpl = pick.TeTemplate >= 0 ? te.Entries.FirstOrDefault(e => e.Id == pick.TeTemplate) : null;
            tpl ??= SameFlightModel(D, vct, te) ?? te.Entries.FirstOrDefault(e => e.Type != 0 && e.UnitSubType == usub) ?? te.Entries.FirstOrDefault(e => e.Type != 0);
            if (tpl == null) { c.Problems.Add($"{pick.Name}: the TE list has no aircraft to take the picture and icon from"); continue; }
            string teName = string.IsNullOrWhiteSpace(pick.TeName) ? pick.Name : pick.TeName.Trim();
            int id = te.Entries.Select(e => e.Id).DefaultIfEmpty(21100).Max() + 1;
            int text = tpl.Text;
            string nameNote;
            if (names != null)
            {
                text = names.Add(teName, te.Entries.Select(e => e.Text).DefaultIfEmpty(0).Max());
                nameNote = $"name '{teName}' (text {text}, in the theater's interface files)";
            }
            else nameNote = $"the name shown is '{tpl.Name}' (text {tpl.Text}): the theater uses BMS's own interface files, which are not changed";
            te.Entries.Add(new TePlanesFile.Entry { Name = teName, Type = type, SubType = sub, Specific = spec, UnitSubType = usub, Id = id, Text = text, Icon = tpl.Icon });
            changed = true;
            c.Steps[5].Add($"{pick.Name}: TE line {type} {sub} {spec} {usub}, ID {id}, icon {tpl.Icon} (as {tpl.Name}); {nameNote}");
        }
        if (changed)
        {
            string f = Path.Combine(workDir, "TePlanes.lst");
            te.Save(f);
            plan.Files[Path.Combine(d.Theater.CampaignDir, "TePlanes.lst")] = (f, null);
        }
        if (names != null && names.Changed) names.Write(plan, workDir);
    }

    /// <summary>The TE entry of a BMS aircraft with the same flight model (AirframeDatIdx), for picture and icon.</summary>
    private static TePlanesFile.Entry? SameFlightModel(Lookup D, int vct, TePlanesFile te)
    {
        int acd = D.Int("CT", vct, "MoverDefinitionData");
        int af = acd >= 0 ? D.Int("ACD", acd, "AirframeDatIdx") : -1;
        if (af < 0) return null;
        foreach (var e in te.Entries.Where(e => e.Type != 0))
        {
            var ct = D.D.All("CT").FirstOrDefault(x => x.Int("Domain") == 2 && x.Int("Class") == 7 && x.Int("Type") == e.Type && x.Int("SubType") == e.SubType && x.Int("Specific") == e.Specific);
            if (ct == null) continue;
            int a = ct.Int("MoverDefinitionData", -1);
            if (a >= 0 && D.D.Get("ACD", a)?.Int("AirframeDatIdx", -1) == af) return e;
        }
        return null;
    }

    /// <summary>
    /// Names of the TE list in the theater's own interface files (artdir\Art\Main\textids.id and units.irc): BMS's files
    /// with the new names added. Only for a theater with its own artdir; BMS's Data\Art is never changed.
    /// </summary>
    private sealed class UiNames
    {
        private readonly string _ids, _irc;
        private string _idsText, _ircText;
        public bool Changed;

        public UiNames(Destination d, string artRoot)
        {
            string main = Path.Combine(artRoot, "Art", "Main"), bms = Path.Combine(d.DataDir, "Art", "Main");
            _ids = Path.Combine(main, "textids.id"); _irc = Path.Combine(main, "units.irc");
            _idsText = Read(_ids, Path.Combine(bms, "textids.id"));
            _ircText = Read(_irc, Path.Combine(bms, "units.irc"));
        }

        private static string Read(string own, string bms) => File.Exists(own) ? File.ReadAllText(own, Encoding.Latin1) : File.Exists(bms) ? File.ReadAllText(bms, Encoding.Latin1) : "";

        /// <summary>The text number of the name (a new one after the highest of the TE list, not used in textids.id).</summary>
        public int Add(string name, int highestTeText)
        {
            string label = "TXT_UC_" + Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", "");
            var m = Regex.Match(_idsText, $@"^{Regex.Escape(label)}[ \t]+(\d+)[ \t]*\r?$", RegexOptions.Multiline);
            if (m.Success) return int.Parse(m.Groups[1].Value, Ci);
            var used = new HashSet<int>(Regex.Matches(_idsText, @"^\w+[ \t]*=?[ \t]*(\d+)[ \t]*\r?$", RegexOptions.Multiline).Select(x => int.Parse(x.Groups[1].Value, Ci)));
            int n = highestTeText + 1;
            while (used.Contains(n)) n++;
            // after the line of the highest TE name (the block of the aircraft names), else at the end
            var last = Regex.Matches(_idsText, $@"^\w+[ \t]+{highestTeText}[ \t]*\r?\n", RegexOptions.Multiline).LastOrDefault();
            string line = $"{label}\t{n}\r\n";
            _idsText = last != null ? _idsText.Insert(last.Index + last.Length, line) : _idsText.TrimEnd() + "\r\n" + line;
            if (!_ircText.EndsWith('\n')) _ircText += "\r\n";
            _ircText += $"[ADDTEXT] {label}\t\"{name.Replace("\"", "'")}\"\r\n";
            Changed = true;
            return n;
        }

        public void Write(Interchange.Plan p, string workDir)
        {
            string a = Path.Combine(workDir, "textids.id"), b = Path.Combine(workDir, "units.irc");
            File.WriteAllText(a, _idsText, Encoding.Latin1);
            File.WriteAllText(b, _ircText, Encoding.Latin1);
            p.Files[_ids] = (a, null);
            p.Files[_irc] = (b, null);
        }
    }

    // ───────────────────────── state

    private static void State(Conversion c, Destination d, SourceDb s, BmsDatabase S, Lookup D, List<int> cts, Dictionary<int, int> assigned, string workDir, IList<Pick> picks)
    {
        var plan = c.Plan;
        // the choices (all of them: earlier conversions from any source, and these)
        var all = LoadPicks(d).Where(x => !(Same(x.Source, s.Theater.ObjectDir) && picks.Any(p => p.Name == x.Pick.Name))).ToList();
        all.AddRange(picks.Select(p => (s.Theater.ObjectDir, p)));
        var ps = new StringBuilder("Source;Ct;Name;SourceModel;TeName;TeTemplate\r\n");
        foreach (var (src, p) in all)
            ps.Append(src).Append(';').Append(p.Ct.ToString(Ci)).Append(';').Append(p.Name.Replace(';', ',')).Append(';').Append(p.OwnModel ? "1" : "0")
              .Append(';').Append((p.TeName ?? "").Replace(';', ',')).Append(';').Append(p.TeTemplate.ToString(Ci)).Append("\r\n");
        string pf = Path.Combine(workDir, PicksFile);
        File.WriteAllText(pf, ps.ToString(), new UTF8Encoding(false));
        plan.Files[Path.Combine(d.Theater.ObjectDir, PicksFile)] = (pf, null);
        string state = Path.Combine(d.Theater.ObjectDir, StateFile);
        var sb = new StringBuilder(File.Exists(state) ? File.ReadAllText(state) : "Time;Source;SourceCt;Name;Kind;Ct;EntityIdx\r\n");
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ci);
        foreach (int ct in cts)
        {
            if (!plan.CtMap.TryGetValue(ct, out int n) || !D.IsNew("CT", n)) continue;
            sb.Append(time).Append(';').Append(s.Theater.ObjectDir).Append(';').Append(ct.ToString(Ci)).Append(';').Append(S.CtName(ct).Replace(';', ','))
              .Append(';').Append(BmsDatabase.EntityTable(S.Get("CT", ct)?.Int("EntityType") ?? 0)).Append(';').Append(n.ToString(Ci))
              .Append(';').Append(D.Int("CT", n, "EntityIdx").ToString(Ci)).Append("\r\n");
        }
        string f = Path.Combine(workDir, StateFile);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
        plan.Files[state] = (f, null);
        if (assigned.Count > 0)
        {
            var m = new StringBuilder("SourceModel;Model\r\n");
            foreach (var kv in assigned.OrderBy(k => k.Key)) m.Append(kv.Key.ToString(Ci)).Append(';').Append(kv.Value.ToString(Ci)).Append("\r\n");
            string mf = Path.Combine(workDir, ModelsFile);
            File.WriteAllText(mf, m.ToString(), new UTF8Encoding(false));
            plan.Files[Path.Combine(d.Theater.ObjectDir, ModelsFile)] = (mf, null);
        }
    }

    /// <summary>The choices kept in the destination theater (source objects folder, pick).</summary>
    public static List<(string Source, Pick Pick)> LoadPicks(Destination d)
    {
        var res = new List<(string, Pick)>();
        string f = Path.Combine(d.Theater.ObjectDir, PicksFile);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length < 6 || !int.TryParse(c[1], NumberStyles.Integer, Ci, out int ct)) continue;
            res.Add((c[0], new Pick(ct, c[2], c[3] == "1", c[4].Length > 0 ? c[4] : null, int.TryParse(c[5], NumberStyles.Integer, Ci, out int t) ? t : -1)));
        }
        return res;
    }

    /// <summary>
    /// Converts again what the theater's picks file lists (after its database was rebuilt, e.g. by TerrainTool's Install
    /// objects): only what is missing is added; backup first as always. Returns the log (empty when there is nothing to do).
    /// </summary>
    public static List<string> Reapply(string bmsRoot, string theaterObjectDir, string workDir, string backupRoot)
    {
        var log = new List<string>();
        var d = FindDestinations(bmsRoot).FirstOrDefault(x => Same(x.Theater.ObjectDir, theaterObjectDir) && x.Problem == null);
        if (d == null) return log;
        foreach (var g in LoadPicks(d).GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
        {
            var s = FindSources(g.Key).FirstOrDefault();
            if (s == null) { log.Add($"4.37 Units Converter: {g.Key} not found: {g.Count()} converted item(s) not added again"); continue; }
            var have = new HashSet<string>(Analyze(s, d).Candidates.Select(x => x.Name));
            var todo = g.Select(x => x.Pick).Where(p => have.Contains(p.Name)).ToList();
            if (todo.Count == 0) continue;
            var c = MakePlan(s, d, todo, Path.Combine(workDir, "reapply"));
            if (!c.HasWork) continue;
            log.Add($"4.37 Units Converter: {todo.Count} item(s) of {s.Theater.Name} converted again ({string.Join(", ", todo.Select(p => p.Name))})");
            log.AddRange(Install(c, Path.Combine(workDir, "reapply"), backupRoot).Select(x => "  " + x));
            log.AddRange(c.Problems.Select(x => "  problem: " + x));
        }
        return log;
    }

    /// <summary>
    /// A TePlanes.lst about to replace the theater's (e.g. a campaign editor's install): the lines this converter added to the
    /// installed one (names of the theater's picks) are kept. Writes outFile and returns how many lines were kept.
    /// </summary>
    public static int KeepConvertedTeLines(string newTePlanes, string installedTePlanes, string theaterObjectDir, string outFile)
    {
        string picks = Path.Combine(theaterObjectDir, PicksFile);
        if (!File.Exists(picks) || !File.Exists(installedTePlanes)) return 0;
        var d = new Destination(new BmsTheater { ObjectDir = theaterObjectDir }, "", null);
        var names = new HashSet<string>(LoadPicks(d).SelectMany(x => new[] { x.Pick.Name, x.Pick.TeName ?? "" }).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        var nw = TePlanesFile.Load(newTePlanes); var old = TePlanesFile.Load(installedTePlanes);
        int kept = 0;
        foreach (var e in old.Entries.Where(e => e.Type != 0 && names.Contains(e.Name) && !nw.Entries.Any(x => x.Key == e.Key)))
        {
            if (nw.Entries.Any(x => x.Id == e.Id)) e.Id = nw.Entries.Max(x => x.Id) + 1;
            nw.Entries.Add(e); kept++;
        }
        if (kept > 0) nw.Save(outFile);
        return kept;
    }

    // ───────────────────────── install and restore

    /// <summary>Backup first, then everything into BMS (BMS closed). Returns the log.</summary>
    public static List<string> Install(Conversion c, string workDir, string backupRoot)
    {
        if (DbInstaller.BmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        Interchange.Write(c.Plan, workDir, Path.Combine(workDir, "out"));
        return Interchange.Install(c.Plan, Path.Combine(workDir, "out"), backupRoot, addNewModelsToShared: true);
    }

    /// <summary>Backups of a destination theater, newest first.</summary>
    public static List<(string Dir, DateTime Time, string Summary)> Backups(string backupRoot) =>
        Interchange.Backups(backupRoot).Where(b => !b.Dir.EndsWith("_restored", StringComparison.OrdinalIgnoreCase)).ToList();

    public static List<string> Restore(string backupDir)
    {
        if (DbInstaller.BmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        return Interchange.Restore(backupDir);
    }
}
