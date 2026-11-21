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
    /// <summary>In the theater's Sim\Acdata: the flight models the converter added (Index;Name), kept when the theater's Sim is
    /// installed again (TerrainTool).</summary>
    public const string FlightModelsFile = "UnitsConverter_flightmodels.csv";

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
    internal static string? OwnArtRoot(Destination d)
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
    /// <param name="FlightModel">Aircraft: the BMS 4.38 flight model (name in AcTypes.lst) it flies with; null = the one of the same name.</param>
    public sealed record Pick(int Ct, string Name, bool OwnModel, string? TeName = null, int TeTemplate = -1, string? FlightModel = null);

    // ───────────────────────── flight models (read only: never copied nor changed)

    /// <summary>A flight model of the list: its hardpoint groups (racks and pylons of each hardpoint, BmsRack.dat) and
    /// where its wheels touch the ground (x aft, z down from the reference point; nose wheel first), read from its .txtpb.</summary>
    public sealed record FlightModel(int Index, string Name, IReadOnlyList<string> Groups, IReadOnlyList<(double X, double Z)> Wheels, string? File = null)
    {
        public bool HasGroup(int hp) => hp < Groups.Count && Groups[hp].Length > 0 && Groups[hp] != "0";
        public int Hardpoints => Enumerable.Range(1, Math.Max(0, Groups.Count - 1)).Count(HasGroup);
        public IEnumerable<double> GearZ => Wheels.Select(w => w.Z);
        public string Gear => Wheels.Count == 0 ? "?" : string.Join(" / ", GearZ.Take(2).Select(z => z.ToString("0.##", Ci)));
        public override string ToString() => Name;
    }

    private static FlightModel? ReadFlightModel(string dir, int index, string name)
    {
        string f = Path.Combine(dir, name + ".txtpb");
        if (!File.Exists(f)) return null;
        string t;
        try { t = File.ReadAllText(f); } catch (IOException) { return null; }
        var gear = Regex.Matches(t, @"gear \{\s*position_ft \{\s*x: *([-\d.eE]+)\s*y: *([-\d.eE]+)\s*z: *([-\d.eE]+)")
            .Select(m => (double.Parse(m.Groups[1].Value, Ci), double.Parse(m.Groups[3].Value, Ci))).ToList();
        var hp = Regex.Match(t, @"hardpoints \{(.*?)\n  \}", RegexOptions.Singleline);
        var groups = hp.Success ? Regex.Matches(hp.Groups[1].Value, "hardpoint_grp: \"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList() : new List<string>();
        return new FlightModel(index, name, groups, gear, f);
    }

    private static List<FlightModel> ReadFlightModels(string? acTypes)
    {
        var res = new List<FlightModel>();
        if (acTypes == null || !File.Exists(acTypes)) return res;
        string dir = Path.GetDirectoryName(acTypes)!;
        var names = ReadAcTypes(acTypes);
        for (int i = 0; i < names.Count; i++)
            if (ReadFlightModel(dir, i, names[i]) is { } fm) res.Add(fm);
        return res;
    }

    private static readonly Dictionary<string, List<FlightModel>> _fmCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The flight models of the destination theater (its Sim, else BMS's).</summary>
    public static List<FlightModel> FlightModels(Destination d)
    {
        string? f = DestAcTypes(d);
        if (f == null) return new();
        if (!_fmCache.TryGetValue(f, out var l)) _fmCache[f] = l = ReadFlightModels(f);
        return l;
    }

    /// <summary>Hardpoints an aircraft of the source uses (1…15: a list with at least one weapon).</summary>
    private static List<int> UsedHardpoints(BmsDatabase S, int ct)
    {
        var res = new List<int>();
        if (S.EntityOf(ct) is not { Table: "VCD" } v) return res;
        for (int i = 1; i < 16; i++)
        {
            int w = v.Int($"WpnOrHpIdx_{i}", 0);
            if (w <= 0 || S.Get("WLD", w) is not { } wl) continue;
            if (wl.Fields.Any(x => x.Field.StartsWith("WpnIdx_", StringComparison.Ordinal) && int.TryParse(x.Value.Trim(), NumberStyles.Integer, Ci, out int n) && n > 0)) res.Add(i);
        }
        return res;
    }

    /// <summary>The source's own flight model of an aircraft (its theater's Sim, read only), for the comparison.</summary>
    private static FlightModel? SourceFlightModel(SourceDb s, BmsDatabase S, int ct)
    {
        int acd = S.Get("CT", ct)?.Int("MoverDefinitionData", -1) ?? -1;
        int af = acd >= 0 ? S.Get("ACD", acd)?.Int("AirframeDatIdx", -1) ?? -1 : -1;
        if (af < 0 || s.AcTypes == null) return null;
        var names = ReadAcTypes(s.AcTypes);
        return af < names.Count ? ReadFlightModel(Path.GetDirectoryName(s.AcTypes)!, af, names[af]) ?? new FlightModel(af, names[af], new List<string>(), new List<(double, double)>()) : null;
    }

    /// <summary>
    /// What a flight model gives an aircraft: hardpoints it uses without racks, mirrored pylons that differ (left ≠ right),
    /// where the old wheels end up when the aircraft rests on this flight model's wheels (feet, + above the ground,
    /// − into it; NaN = unknown), hardpoints whose pylon models are not the old ones.
    /// </summary>
    public sealed record FlightModelFit(FlightModel Model, List<int> NoRack, List<int> Asymmetric, double NoseGap, double MainGap, List<int> OtherPylons, bool SameName, bool Own = false)
    {
        /// <summary>What a pick keeps: the flight model's name in the theater's list, or <see cref="OwnFlightModel"/>.</summary>
        public string Key => Own ? OwnFlightModel : Model.Name;
        public string Label => Own ? $"its own flight model (the old database's {Model.Name})" : Model.Name;
        /// <summary>The biggest distance of the old wheels from the ground, feet (NaN = unknown).</summary>
        public double GearDiff => double.IsNaN(NoseGap) ? double.NaN : Math.Max(Math.Abs(NoseGap), Math.Abs(MainGap));
        public static string Cm(double ft) => Math.Abs(ft) < 0.07 ? "on the ground" : $"{Math.Abs(ft) * 30.48:0} cm {(ft > 0 ? "above" : "into")} the ground";
        public string Wheels => double.IsNaN(NoseGap) ? "wheels: no comparison" : $"nose wheel {Cm(NoseGap)}, main wheels {Cm(MainGap)}";
        public string Text => (Own ? $"its own flight model of the old database ({Model.Name}, copied into the theater's Sim)" : Model.Name) + ": " +
            (NoRack.Count > 0 ? $"hardpoint(s) {string.Join(", ", NoRack)} used by the aircraft have NO rack group" : "racks on all the hardpoints the aircraft uses") +
            (Asymmetric.Count > 0 ? $", left and right pylons differ on {string.Join(", ", Asymmetric)}" : ", left and right alike") +
            (OtherPylons.Count > 0 ? $", other pylon models than the old database's on {string.Join(", ", OtherPylons)}" : ", the same pylons as the old database") +
            ", " + (Own ? "wheels as in the old database" : Wheels);
    }

    /// <summary>Pick.FlightModel: the aircraft keeps the old database's flight model, copied into the theater's own Sim.</summary>
    public const string OwnFlightModel = "(its own)";

    /// <summary>Flight models of aircraft flyable in BMS are never copied (see <see cref="Sim.SimLibrary.IsFlyable"/>).</summary>
    public static bool IsFlyable(string flightModelName) => Sim.SimLibrary.IsFlyable(flightModelName);

    /// <summary>The theater's own Sim\Acdata (simdatadir of its .tdf), or null when it uses BMS's Data\Sim.</summary>
    private static string? OwnAcdata(Destination d)
    {
        string? f = DestAcTypes(d);
        if (f == null) return null;
        string dir = Path.GetDirectoryName(f)!;
        return Same(dir, Path.Combine(d.DataDir, "Sim", "Acdata")) ? null : dir;
    }

    /// <summary>Why an aircraft cannot keep its own flight model (null = it can).</summary>
    private static string? OwnProblem(FlightModel? old, Destination d)
    {
        if (old?.File == null) return "the old database has no flight model in the BMS 4.38 format (.txtpb) for it";
        if (IsFlyable(old.Name)) return $"{old.Name} is a flight model of an aircraft flyable in BMS: it is not copied";
        if (OwnAcdata(d) == null) return "the theater uses BMS's own Sim folder (simdatadir of its .tdf), which is not changed";
        return null;
    }

    /// <summary>The name of the aircraft's own flight model in the theater's AcTypes.lst (a file name: no \ / : * ? " &lt; &gt; |).</summary>
    private static string OwnName(Pick p) =>
        Regex.Replace(string.IsNullOrWhiteSpace(p.TeName) ? p.Name : p.TeName.Trim(), @"[\\/:*?""<>|;]", "-").Trim();

    /// <summary>The BMS 4.38 flight models for an aircraft of the source, best first (see <see cref="Fits"/>).</summary>
    public static List<FlightModelFit> FlightModelsFor(SourceDb s, Destination d, BmsDatabase S, int ct) => Fits(s, d, S, ct);

    /// <summary>
    /// The flight model proposed for an aircraft: the one of the same name when it is as good as the best (racks on every
    /// hardpoint used, left and right alike, wheels on the ground), else the best.
    /// </summary>
    public static FlightModelFit? Proposed(List<FlightModelFit> fits)
    {
        var best = fits.FirstOrDefault();
        if (best is { Own: true, NoRack.Count: 0 }) return best;   // the old database's own: as it was
        best = fits.FirstOrDefault(f => !f.Own);
        var same = fits.FirstOrDefault(f => f.SameName);
        if (same != null && best != null && same.NoRack.Count == 0 && same.Asymmetric.Count == 0 && WheelStep(same) <= WheelStep(best)) return same;
        return best ?? same;
    }

    private static double WheelStep(FlightModelFit f) => double.IsNaN(f.GearDiff) ? 0 : Math.Round(f.GearDiff / 0.25);

    /// <summary>
    /// Ranks the flight models: (1) every hardpoint the aircraft uses has a rack group; (2) the pylons of the left and
    /// the right wing alike (hardpoint 1 ↔ n, 2 ↔ n−1…); (3) the aircraft's own wheels on the ground (the old flight model's
    /// wheel points, with the aircraft resting on the new one's), in steps of 7–8 cm; (4) most hardpoints with the old
    /// pylon models; (5) the one of the same name. Flight models are only read, never changed.
    /// </summary>
    private static List<FlightModelFit> Fits(SourceDb s, Destination d, BmsDatabase S, int ct)
    {
        var used = UsedHardpoints(S, ct);
        var old = SourceFlightModel(s, S, ct);
        int n = 0;
        if (S.EntityOf(ct) is { Table: "VCD" } v)
            for (int i = 1; i < 16; i++) if (v.Int($"WpnOrHpIdx_{i}", 0) > 0) n = i;
        var dPylon = PylonGroups(Path.Combine(d.Theater.ObjectDir, "BmsRack.dat"));
        var sPylon = PylonGroups(Path.Combine(s.Theater.ObjectDir, "BmsRack.dat"));
        bool Has(FlightModel m, int h) => m.HasGroup(h) && (dPylon.Count == 0 || dPylon.ContainsKey(m.Groups[h]));
        static string Set(Dictionary<string, SortedSet<int>> p, FlightModel m, int h) =>
            !m.HasGroup(h) ? "" : p.TryGetValue(m.Groups[h], out var x) ? string.Join(",", x) : "?" + m.Groups[h];
        List<int> Asym(FlightModel m) => Enumerable.Range(1, n / 2)
            .Where(i => m.HasGroup(i) != m.HasGroup(n + 1 - i) || Set(dPylon, m, i) != Set(dPylon, m, n + 1 - i))
            .SelectMany(i => new[] { i, n + 1 - i }).ToList();
        List<int> Other(FlightModel m) => old == null ? new() : used.Where(h => m.HasGroup(h) && Set(dPylon, m, h) != Set(sPylon, old, h)).ToList();
        (double, double) Gaps(FlightModel m)
        {
            if (old == null || old.Wheels.Count < 2 || m.Wheels.Count < 2 || m.Wheels[1].X == m.Wheels[0].X) return (double.NaN, double.NaN);
            var (xn, zn) = m.Wheels[0]; var (xm, zm) = m.Wheels[1];
            double Ground(double x) => zn + (x - xn) * (zm - zn) / (xm - xn);   // the ground line, body axes (z down)
            return (Ground(old.Wheels[0].X) - old.Wheels[0].Z, Ground(old.Wheels[1].X) - old.Wheels[1].Z);
        }
        var list = FlightModels(d).Where(m => m.Wheels.Count > 0)
            .Select(m =>
            {
                var (g0, g1) = Gaps(m);
                return new FlightModelFit(m, used.Where(h => !Has(m, h)).ToList(), Asym(m), g0, g1, Other(m), old != null && m.Name.Equals(old.Name, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(f => f.NoRack.Count).ThenBy(f => f.Asymmetric.Count).ThenBy(WheelStep).ThenBy(f => f.OtherPylons.Count)
            .ThenBy(f => f.SameName ? 0 : 1).ThenBy(f => double.IsNaN(f.GearDiff) ? 0 : f.GearDiff)
            .ToList();
        // an AI-only aircraft may keep the old database's flight model (copied into the theater's own Sim), unless BMS's of
        // the same name is that very file
        if (OwnProblem(old, d) == null && !list.Any(f => f.SameName && f.Model.File != null && AddOnModels.SameFile(old!.File!, f.Model.File)))
            list.Insert(0, new FlightModelFit(old!, used.Where(h => !Has(old!, h)).ToList(), Asym(old!), 0, 0, Other(old!), false, Own: true));
        return list;
    }

    /// <summary>Hardpoint groups of a BmsRack.dat: name → the pylon models it draws (pylonct above 0; none = no pylon).</summary>
    private static Dictionary<string, SortedSet<int>> PylonGroups(string rackFile)
    {
        var res = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(rackFile)) return res;
        SortedSet<int>? cur = null;
        foreach (var raw in File.ReadLines(rackFile, Encoding.Latin1))
        {
            string l = raw.Split('#')[0].Trim();
            if (l.Length == 0) continue;
            var m = Regex.Match(l, @"^definegroup\s+(\S+)", RegexOptions.IgnoreCase);
            if (m.Success) { res[m.Groups[1].Value] = cur = new SortedSet<int>(); continue; }
            if (Regex.IsMatch(l, @"^define", RegexOptions.IgnoreCase)) { cur = null; continue; }
            var p = Regex.Match(l, @"^pylonct\s+(\d+)", RegexOptions.IgnoreCase);
            if (cur != null && p.Success && int.Parse(p.Groups[1].Value, Ci) is > 0 and var ctn) cur.Add(ctn);
        }
        return res;
    }

    /// <summary>The source flight model's name of an aircraft (the one of the same name is the default), or null.</summary>
    public static string? SourceFlightModelName(SourceDb s, int ct) => SourceFlightModel(s, BmsDatabase.Load(s.Theater.ObjectDir), ct)?.Name;

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
        // the flight model of each aircraft: the chosen one, else the one of the same name; hardpoints without racks are told
        var acdAirframe = new Dictionary<int, int>();
        // own flight models (AI-only aircraft): copied into the theater's Sim\Acdata, added at the end of its AcTypes.lst
        string? acTypes = DestAcTypes(d);
        List<string>? acNames = null;
        bool acChanged = false;
        var ownFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ct, p) in byCt)
        {
            if (S.EntityOf(ct) is not { Table: "VCD" } || S.Get("CT", ct)?.Int("Domain") != 2) continue;
            var fits = Fits(s, d, S, ct);
            bool own = p.FlightModel == OwnFlightModel;
            var fit = own ? fits.FirstOrDefault(f => f.Own)
                : p.FlightModel != null ? fits.FirstOrDefault(f => !f.Own && f.Model.Name.Equals(p.FlightModel, StringComparison.OrdinalIgnoreCase)) : fits.FirstOrDefault(f => f.SameName);
            if (own && fit == null) { c.Problems.Add($"{p.Name}: its own flight model cannot be used: {OwnProblem(SourceFlightModel(s, S, ct), d) ?? "BMS's of the same name is the same file (choose it)"}"); continue; }
            if (p.FlightModel != null && fit == null) { c.Problems.Add($"{p.Name}: flight model '{p.FlightModel}' is not in the theater's list (Sim\\Acdata\\AcTypes.lst)"); continue; }
            if (fit == null) continue;
            int acd = S.Get("CT", ct)!.Int("MoverDefinitionData", -1);
            int index = fit.Model.Index;
            if (own)
            {
                acNames ??= ReadAcTypes(acTypes!);
                string name = OwnName(p), dir = Path.GetDirectoryName(acTypes!)!;
                // the same name already there: the same file again (an earlier conversion), else a free name
                for (int k = 2; acNames.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) && !(File.Exists(Path.Combine(dir, name + ".txtpb")) && AddOnModels.SameFile(fit.Model.File!, Path.Combine(dir, name + ".txtpb"))); k++)
                    name = $"{OwnName(p)} {k}";
                index = acNames.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                string target = Path.Combine(dir, name + ".txtpb");
                if (index < 0) { acNames.Add(name); index = acNames.Count - 1; acChanged = true; }
                if (!File.Exists(target) || !AddOnModels.SameFile(fit.Model.File!, target)) ownFiles[target] = fit.Model.File!;
                c.Steps[3].Add($"{p.Name}: flies with its own flight model, the old database's {Path.GetFileName(fit.Model.File)} (an AI-only aircraft) copied as {target}, flight model {index} of {acTypes}");
            }
            if (p.FlightModel != null && acd >= 0) acdAirframe[acd] = index;
            if (!own) c.Steps[3].Add($"{p.Name}: flies with {fit.Text}{(p.FlightModel == null || fit.SameName ? " (the flight model of the same name)" : "")}");
            if (fit.NoRack.Count > 0)
            {
                var best = fits.FirstOrDefault(f => f.NoRack.Count == 0);
                c.Problems.Add($"{p.Name}: hardpoint(s) {string.Join(", ", fit.NoRack)} have no rack group in the flight model {fit.Model.Name}: weapons there hang without pylon or rack" +
                               (best != null ? $" ({best.Label} has racks on all of them: choose it in 'Flies like')" : ""));
            }
            if (fit.Asymmetric.Count > 0)
                c.Problems.Add($"{p.Name}: with the flight model {fit.Model.Name} the left and right pylons differ (hardpoints {string.Join(", ", fit.Asymmetric)})" +
                               (fits.FirstOrDefault(f => f.NoRack.Count == 0 && f.Asymmetric.Count == 0) is { } sym ? $": {sym.Label} has them alike" : ""));
        }

        var plan = Interchange.MakeEntities(src, dst, d.DataDir, cts, preferDestination: true, modelMap: modelMap, airframeMap: airframes, acdAirframe: acdAirframe);
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
            plan = Interchange.MakeEntities(src, dst, d.DataDir, cts, preferDestination: true, modelMap: modelMap, airframeMap: airframes, acdAirframe: acdAirframe);
        c.Plan = plan;
        c.Problems.AddRange(plan.Problems);
        foreach (var (target, from) in ownFiles) plan.Files[target] = (from, null);
        if (ownFiles.Count > 0 || acChanged)
        {
            // the list of the converter's flight models in the theater's Acdata (earlier ones and these)
            string dir = Path.GetDirectoryName(acTypes!)!;
            var kept = ReadFlightModelsFile(dir);
            foreach (var t in ownFiles.Keys)
            {
                string n = Path.GetFileNameWithoutExtension(t);
                int i = acNames!.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                kept.RemoveAll(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                kept.Add((i, n));
            }
            string lf = Path.Combine(workDir, FlightModelsFile);
            File.WriteAllText(lf, "Index;Name\r\n" + string.Concat(kept.OrderBy(k => k.Index).Select(k => $"{k.Index.ToString(Ci)};{k.Name}\r\n")), new UTF8Encoding(false));
            plan.Files[Path.Combine(dir, FlightModelsFile)] = (lf, null);
        }
        if (acChanged)
        {
            string f = Path.Combine(workDir, "AcTypes.lst");
            File.WriteAllText(f, acNames!.Count.ToString(Ci) + "\r\n" + string.Join("\r\n", acNames), Encoding.Latin1);
            plan.Files[acTypes!] = (f, null);
        }
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
        c.Steps[3].Add($"flight models: by name ({srcList} → {dstList}); BMS's flight models are never changed, those of flyable aircraft never copied");
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
    internal sealed class UiNames
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
        var ps = new StringBuilder("Source;Ct;Name;SourceModel;TeName;TeTemplate;FlightModel\r\n");
        foreach (var (src, p) in all)
            ps.Append(src).Append(';').Append(p.Ct.ToString(Ci)).Append(';').Append(p.Name.Replace(';', ',')).Append(';').Append(p.OwnModel ? "1" : "0")
              .Append(';').Append((p.TeName ?? "").Replace(';', ',')).Append(';').Append(p.TeTemplate.ToString(Ci)).Append(';').Append(p.FlightModel ?? "").Append("\r\n");
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

    /// <summary>The converter's flight models listed in a theater's Sim\Acdata (index in AcTypes.lst, name).</summary>
    public static List<(int Index, string Name)> ReadFlightModelsFile(string acdataDir)
    {
        var res = new List<(int, string)>();
        string f = Path.Combine(acdataDir, FlightModelsFile);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length >= 2 && int.TryParse(c[0], NumberStyles.Integer, Ci, out int i) && c[1].Trim().Length > 0) res.Add((i, c[1].Trim()));
        }
        return res;
    }

    /// <summary>
    /// After the theater's Sim was replaced (TerrainTool's install): the converter's flight models go back into its
    /// AcTypes.lst with the same numbers (the database points to them). Their .txtpb files must still be there. Returns the log.
    /// </summary>
    public static List<string> KeepFlightModels(string acdataDir, IReadOnlyList<(int Index, string Name)> kept)
    {
        var log = new List<string>();
        string f = Path.Combine(acdataDir, "AcTypes.lst");
        if (kept.Count == 0 || !File.Exists(f)) return log;
        var names = ReadAcTypes(f);
        bool changed = false;
        foreach (var (i, n) in kept.OrderBy(k => k.Index))
        {
            if (i < names.Count && names[i].Equals(n, StringComparison.OrdinalIgnoreCase)) continue;
            if (i == names.Count) { names.Add(n); changed = true; log.Add($"4.37 Units Converter: flight model '{n}' kept (number {i} in AcTypes.lst)"); continue; }
            log.Add($"4.37 Units Converter: flight model '{n}' cannot keep number {i} in the new AcTypes.lst ({(i < names.Count ? $"it is now '{names[i]}'" : $"the list has {names.Count}")}): " +
                    "open the 4.37 Units Converter, Restore its last conversion and convert again");
        }
        if (changed) File.WriteAllText(f, names.Count.ToString(Ci) + "\r\n" + string.Join("\r\n", names), Encoding.Latin1);
        return log;
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
            res.Add((c[0], new Pick(ct, c[2], c[3] == "1", c[4].Length > 0 ? c[4] : null, int.TryParse(c[5], NumberStyles.Integer, Ci, out int t) ? t : -1,
                                    c.Length > 6 && c[6].Trim().Length > 0 ? c[6].Trim() : null)));
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
        _fmCache.Clear();   // the theater's AcTypes.lst may change
        return Interchange.Install(c.Plan, Path.Combine(workDir, "out"), backupRoot, addNewModelsToShared: true);
    }

    /// <summary>Backups of a destination theater, newest first.</summary>
    public static List<(string Dir, DateTime Time, string Summary)> Backups(string backupRoot) =>
        Interchange.Backups(backupRoot).Where(b => !b.Dir.EndsWith("_restored", StringComparison.OrdinalIgnoreCase)).ToList();

    public static List<string> Restore(string backupDir)
    {
        if (DbInstaller.BmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        _fmCache.Clear();
        return Interchange.Restore(backupDir);
    }
}
