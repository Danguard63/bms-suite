using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Sistemi antiaerei del teatro (database + Sim) e controllo delle posizioni sugli obiettivi.
/// Come BMS 4.38 schiera un battaglione su un obiettivo (BattalionClass::GetDeaggregationPoint, GetDeagHdr, GetDeagPoint;
/// tipo di punto di ogni mezzo da LoadClassTable):
///  - il battaglione prende UNA lista di punti (PHD) libera dell'obiettivo: antiaereo = lista SAM (tipo 4), o AAA (6) se il primo mezzo
///    e' fanteria/cannone; preferisce una lista con almeno tanti punti quanti mezzi; se non c'e' prova l'altra (4↔6), poi la 19;
///    ogni lista ospita un solo battaglione;
///  - ogni mezzo prende un punto del suo tipo (4 SAM, 6 AAA, 7 radar, 9 supporto, 5 artiglieria, 20 fanteria) dentro la lista;
///    finiti quelli, uno qualsiasi tra 4/5/6/7/9 rimasti; finiti tutti, il mezzo resta fuori dai punti della base.
/// </summary>
public sealed class SamDb
{
    public sealed record PtList(int Index, int Type, int Count, IReadOnlyDictionary<int, int> PtTypes);
    public sealed record FireData(string RadarFile, double FireRateSec, double SalvoRateSec, int MaxInAir, int MaxPerTarget);

    private readonly Dictionary<string, Dictionary<int, Dictionary<string, string>>> _t = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _ctOfVcd = new();
    private readonly Dictionary<int, List<PtList>?> _lists = new();
    private readonly string? _ordDir;
    private readonly string? _simDir;
    public List<string> Notes { get; } = new();

    public static readonly int[] AnyPt = { 4, 5, 6, 7, 9 };
    public static string PtName(int t) => t switch { 4 => "SAM", 5 => "artillery", 6 => "AAA", 7 => "radar", 9 => "support", 20 => "infantry", 19 => "ground", _ => "type " + t };

    private SamDb(IReadOnlyList<string> dbDirs, string? simDir, string? dbEditsFile)
    {
        foreach (var t in new[] { "CT", "UCD", "VCD", "WCD", "RCD" })
        {
            string? f = dbDirs.Select(d => Path.Combine(d, $"Falcon4_{t}.xml")).FirstOrDefault(File.Exists);
            _t[t] = f == null ? new() : Records(File.ReadAllText(f), t);
            if (f == null) Notes.Add($"Falcon4_{t}.xml not found");
        }
        foreach (var (n, v) in _t["VCD"]) _ctOfVcd[n] = I(v, "CtIdx");
        _ordDir = dbDirs.Select(d => Path.Combine(d, "ObjectiveRelatedData")).FirstOrDefault(Directory.Exists);
        _simDir = simDir != null && Directory.Exists(simDir) ? simDir : null;
        if (dbEditsFile != null && File.Exists(dbEditsFile))
        {
            int n = 0;
            foreach (var e in Dds.TheaterSimData.ParseDbEdits(dbEditsFile))
                if (e.Field == Dds.TheaterSimData.CopyField)
                {
                    if (_t.TryGetValue(e.Table, out var tc) && tc.TryGetValue(int.Parse(e.Value, CultureInfo.InvariantCulture), out var src) && !tc.ContainsKey(e.Num))
                    { tc[e.Num] = new Dictionary<string, string>(src, StringComparer.OrdinalIgnoreCase); n++; }
                }
                else if (_t.TryGetValue(e.Table, out var tab) && tab.TryGetValue(e.Num, out var rec)) { rec[e.Field] = e.Value; n++; }
            if (n > 0) Notes.Add($"{n} DbEdits.txt changes applied (in memory)");
        }
    }

    public static SamDb Load(IEnumerable<string> dbDirs, string? simDir, string? dbEditsFile) =>
        new(dbDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).ToList(), simDir, dbEditsFile);

    private static Dictionary<int, Dictionary<string, string>> Records(string xml, string tag)
    {
        var res = new Dictionary<int, Dictionary<string, string>>();
        foreach (Match m in Regex.Matches(xml, $@"<{tag} Num=""(\d+)"">(.*?)</{tag}>", RegexOptions.Singleline))
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match f in Regex.Matches(m.Groups[2].Value, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline)) d[f.Groups[1].Value] = global::System.Net.WebUtility.HtmlDecode(f.Groups[2].Value).Trim();
            res[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = d;
        }
        return res;
    }

    private static int I(Dictionary<string, string>? r, string f) =>
        r != null && r.TryGetValue(f, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? (int)v : 0;
    private static string S(Dictionary<string, string>? r, string f) => r != null && r.TryGetValue(f, out var s) ? s : "";

    public Dictionary<string, string>? Rec(string table, int num) => _t.TryGetValue(table, out var t) && t.TryGetValue(num, out var r) ? r : null;
    public IEnumerable<int> Nums(string table) => _t.TryGetValue(table, out var t) ? t.Keys : Enumerable.Empty<int>();

    private Dictionary<string, string>? Ct(int ct) => Rec("CT", ct);
    /// <summary>VCD del mezzo con indice CT ct (EntityIdx del CT).</summary>
    public int VcdOfCt(int ct) => Ct(ct) is { } c && I(c, "EntityType") >= 0 ? I(c, "EntityIdx") : -1;
    public int UcdOfCt(int ct) => VcdOfCt(ct);
    public int OcdOfCt(int ct) => VcdOfCt(ct);
    public string VehicleName(int ct) => S(Rec("VCD", VcdOfCt(ct)), "Name");

    /// <summary>Elementi del battaglione: (indice CT del mezzo, quanti).</summary>
    public List<(int Ct, int Count)> Elements(int ucd)
    {
        var u = Rec("UCD", ucd); var res = new List<(int, int)>();
        for (int i = 0; i < 16; i++) { int c = I(u, "VehicleCtIdx_" + i), k = I(u, "ElementCount_" + i); if (c > 0 && k > 0) res.Add((c, k)); }
        return res;
    }

    private bool IsAaWeapon(int wcd)
    {
        var c = Ct(I(Rec("WCD", wcd), "CtIdx"));
        if (c == null) return false;
        int dom = I(c, "Domain"), cls = I(c, "Class"), typ = I(c, "Type"), sub = I(c, "SubType");
        return (dom == 2 && cls == 7 && typ == 6 && sub == 5) || (dom == 1 && cls == 8 && typ == 3);
    }

    /// <summary>Arma antiaerea del mezzo (indice WCD, quante), o null.</summary>
    public (int Wcd, int Count)? AaWeapon(int vcd)
    {
        var v = Rec("VCD", vcd);
        for (int i = 0; i < 16; i++) { int w = I(v, "WpnOrHpIdx_" + i), n = I(v, "WpnCount_" + i); if (w > 0 && n > 0 && IsAaWeapon(w)) return (w, n); }
        return null;
    }

    /// <summary>Tipo di punto del mezzo, come lo calcola BMS al caricamento (LoadClassTable) quando il VCD non ha PtType.</summary>
    public int PtType(int ct)
    {
        var c = Ct(ct); var v = Rec("VCD", VcdOfCt(ct));
        if (v != null && v.TryGetValue("PtType", out var pt) && int.TryParse(pt, out int ptv) && ptv > 0) return ptv;
        if (c == null || v == null || I(c, "Domain") != 3 || I(c, "Class") != 7) return 9;
        int t = I(c, "Type"), s = I(c, "SubType");
        int p = 9;
        bool canHitGround = I(v, "Hit_NoMove") + I(v, "Hit_Foot") + I(v, "Hit_Wheeled") + I(v, "Hit_Tracked") > 0;
        if (((t == 2 && s == 2) || (t == 3 && s == 4) || (t == 4 && s == 3)) && canHitGround) p = 5;
        bool aa = AaWeapon(VcdOfCt(ct)) != null; int radar = I(v, "RadarIdx");
        bool canHitAir = I(v, "Hit_LowAir") > 0 || I(v, "Hit_Air") > 0;
        if ((I(v, "Flags") & 0x20000) != 0)
        {
            if (canHitAir)
            {
                if (t == 1) p = 20;
                else if ((t is 2 or 3 && s == 1) || (t == 4 && s == 1)) p = 6;
                else if (aa || radar == 0) p = 4;
                else p = 7;
            }
            else if (!aa && radar != 0) p = 7;
        }
        return p;
    }

    /// <summary>Lista principale del battaglione antiaereo: 6 (AAA) se il primo mezzo e' fanteria o cannone, altrimenti 4 (SAM).</summary>
    public int MainList(int ucd)
    {
        var e = Elements(ucd);
        if (e.Count == 0) return 4;
        var c = Ct(e[0].Ct);
        return I(c, "Type") == 1 || I(c, "SubType") == 1 ? 6 : 4;
    }

    public Dictionary<int, int> Needs(int ucd)
    {
        var res = new Dictionary<int, int>();
        foreach (var (ct, k) in Elements(ucd)) { int t = PtType(ct); res[t] = res.GetValueOrDefault(t) + k; }
        return res;
    }

    public List<PtList>? Lists(int ocd)
    {
        if (_lists.TryGetValue(ocd, out var r)) return r;
        r = null;
        string dir = _ordDir == null ? "" : Path.Combine(_ordDir, $"OCD_{ocd:00000}");
        string? phd = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PHD_*.XML").FirstOrDefault() : null;
        string? pdx = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PDX_*.XML").FirstOrDefault() : null;
        if (phd != null)
        {
            var pts = pdx == null ? new List<int>() : Regex.Matches(File.ReadAllText(pdx), @"<PD Num=""\d+"">(.*?)</PD>", RegexOptions.Singleline)
                .Select(m => Regex.Match(m.Groups[1].Value, @"<Type>(\d+)</Type>") is { Success: true } t ? int.Parse(t.Groups[1].Value) : 0).ToList();
            r = new List<PtList>();
            foreach (var (n, h) in Records(File.ReadAllText(phd), "PHD").OrderBy(x => x.Key))
            {
                int first = I(h, "FirstPtIdx"), cnt = I(h, "PointCount");
                var by = new Dictionary<int, int>();
                for (int i = first; i < first + cnt && i < pts.Count; i++) by[pts[i]] = by.GetValueOrDefault(pts[i]) + 1;
                r.Add(new PtList(n, I(h, "Type"), cnt, by));
            }
        }
        return _lists[ocd] = r;
    }

    /// <summary>Mezzi messi su un punto di un altro tipo e mezzi senza punto.</summary>
    public static (int Fallback, int Missing) Fit(IReadOnlyDictionary<int, int> needs, IReadOnlyDictionary<int, int> pts)
    {
        var left = new Dictionary<int, int>(pts);
        int over = 0;
        foreach (var (t, k) in needs) { int take = Math.Min(k, left.GetValueOrDefault(t)); left[t] = left.GetValueOrDefault(t) - take; over += k - take; }
        int fb = 0, miss = 0;
        for (int i = 0; i < over; i++)
        {
            int p = AnyPt.FirstOrDefault(q => left.GetValueOrDefault(q) > 0);
            if (p == 0) miss++; else { left[p]--; fb++; }
        }
        return (fb, miss);
    }

    // ───────── Sim: dati di tiro del radar (Sim\Radar\<nome>.dat, indice da RadTypes.lst) ─────────

    private List<string>? _radTypes;

    public FireData? Fire(int rcd)
    {
        if (_simDir == null) return null;
        int idx = I(Rec("RCD", rcd), "RadarDatIdx");
        _radTypes ??= File.Exists(Path.Combine(_simDir, "Radar", "RadTypes.lst")) ? File.ReadAllLines(Path.Combine(_simDir, "Radar", "RadTypes.lst")).Skip(1).Select(l => l.Trim()).ToList() : new();
        if (idx < 0 || idx >= _radTypes.Count) return null;
        string f = Path.Combine(_simDir, "Radar", _radTypes[idx] + ".dat");
        if (!File.Exists(f)) return null;
        var kv = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in File.ReadAllLines(f))
        {
            var p = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) kv[p[0]] = v;
        }
        return new FireData(Path.GetFileName(f), kv.GetValueOrDefault("AirFireRate") / 1000, kv.GetValueOrDefault("AirSalvoRate") / 1000,
                            (int)kv.GetValueOrDefault("Maxmissilesintheair"), (int)kv.GetValueOrDefault("Maxmissilespertarget"));
    }

    /// <summary>Riassunto di un tipo di battaglione antiaereo.</summary>
    public sealed class SamSystem
    {
        public int Ucd { get; init; }
        public string Name { get; init; } = "";
        public string Launcher { get; init; } = "";
        public int Launchers { get; init; }
        public int MissilesEach { get; init; }
        public string Missile { get; init; } = "";
        public int Missiles => Launchers * MissilesEach;
        public string Radars { get; init; } = "";
        public string Fire { get; init; } = "";
        public int Vehicles { get; init; }
        public string Needs { get; init; } = "";
        public int MainList { get; init; }
        public int Battalions { get; set; }
        public string Worst { get; set; } = "";
    }

    /// <summary>Tipi di battaglione con lanciatori di missili antiaerei (mezzi da punto SAM con un missile antiaereo).</summary>
    public SamSystem? SystemOf(int ucd)
    {
        var u = Rec("UCD", ucd);
        if (u == null) return null;
        var el = Elements(ucd);
        var launchers = el.Where(e => PtType(e.Ct) == 4 && AaWeapon(VcdOfCt(e.Ct)) is { } w && IsMissile(w.Wcd)).ToList();
        if (launchers.Count == 0) return null;
        var first = AaWeapon(VcdOfCt(launchers[0].Ct))!.Value;
        var radars = el.Where(e => PtType(e.Ct) == 7).ToList();
        var fire = radars.Select(r => I(Rec("VCD", VcdOfCt(r.Ct)), "RadarIdx")).Where(x => x > 0).Select(Fire).FirstOrDefault(f => f != null && f.MaxInAir > 0);
        string desc = S(u, "Description");
        return new SamSystem
        {
            Ucd = ucd,
            Name = S(u, "Name") + (desc.Length > 0 ? " - " + desc : ""),
            Launcher = string.Join(", ", launchers.Select(l => VehicleName(l.Ct)).Distinct()),
            Launchers = launchers.Sum(l => l.Count),
            MissilesEach = first.Count,
            Missile = S(Rec("WCD", first.Wcd), "Name"),
            Radars = string.Join(", ", radars.Select(r => $"{r.Count}× {VehicleName(r.Ct)}")),
            Fire = fire == null ? "" : $"{fire.FireRateSec:0.#} s between missiles, salvo every {fire.SalvoRateSec:0.#} s, max {fire.MaxInAir} in the air / {fire.MaxPerTarget} per target ({fire.RadarFile})",
            Vehicles = el.Sum(e => e.Count),
            Needs = string.Join(", ", Needs(ucd).OrderBy(k => k.Key).Select(k => $"{k.Value} {PtName(k.Key)}")),
            MainList = MainList(ucd),
        };
    }

    private bool IsMissile(int wcd) => Ct(I(Rec("WCD", wcd), "CtIdx")) is { } c && I(c, "Domain") == 2;
}

/// <summary>Un battaglione antiaereo di una campagna e la lista di punti che BMS gli darebbe.</summary>
public sealed class SamPlacement
{
    public string File { get; init; } = "";
    public int Team { get; init; }
    public int Ucd { get; init; }
    public string Unit { get; init; } = "";
    public string Objective { get; init; } = "";
    public int Ocd { get; init; }
    public string List { get; set; } = "";
    public int Vehicles { get; init; }
    public int Fallback { get; set; }
    public int Missing { get; set; }
    public string Result { get; set; } = "";
    public bool Problem => Missing > 0;
}

public static class SamCheck
{
    /// <summary>Battaglioni antiaerei della campagna (solo i tipi in onlyUcd se dato) con la lista di punti assegnata.</summary>
    public static List<SamPlacement> Check(string file, CampaignDb db, SamDb s, ISet<int>? onlyUcd = null, int maxCells = 2)
    {
        var d = CampaignDoc.Load(file, db.ClassOf);
        var ad = new AirDefense(d, db);
        var res = new List<SamPlacement>();
        if (d.Units == null) return res;
        var byId = new Dictionary<VuId, CampObjective>(); foreach (var o in d.Objectives) byId[o.Id] = o;
        var used = new Dictionary<VuId, HashSet<int>>();
        string fn = Path.GetFileName(file);
        foreach (var u in d.Units.Units.Where(ad.IsAdUnit))
        {
            int ucd = s.UcdOfCt(u.Type - 100);
            if (onlyUcd != null && !onlyUcd.Contains(ucd)) continue;
            var needs = s.Needs(ucd);
            int veh = needs.Values.Sum();
            // l'obiettivo dove si schiera: quello assegnato se e' li', altrimenti il piu' vicino con punti
            CampObjective? obj = byId.TryGetValue(u.AssignedObj, out var ao) && Math.Max(Math.Abs(ao.X - u.X), Math.Abs(ao.Y - u.Y)) <= maxCells ? ao : null;
            obj ??= d.Objectives.Where(o => Math.Max(Math.Abs(o.X - u.X), Math.Abs(o.Y - u.Y)) <= maxCells && s.Lists(s.OcdOfCt(o.Type - 100)) is { Count: > 0 })
                .OrderBy(o => Math.Abs(o.X - u.X) + Math.Abs(o.Y - u.Y)).FirstOrDefault();
            var p = new SamPlacement
            {
                File = fn, Team = u.Owner, Ucd = ucd, Unit = $"{db.UcdName(u.Type - 100)} #{u.Id.Num}", Vehicles = veh,
                Objective = obj == null ? $"none within {maxCells} km of {u.X},{u.Y}" : $"{(obj.Name.Length > 0 ? obj.Name : "objective")} #{obj.Id.Num} ({obj.X},{obj.Y})",
                Ocd = obj == null ? -1 : s.OcdOfCt(obj.Type - 100),
            };
            res.Add(p);
            if (obj == null) { p.Result = "field position (no objective nearby): BMS places it in formation"; continue; }
            var lists = s.Lists(p.Ocd);
            if (lists == null || lists.Count == 0) { p.Result = "no point lists on this objective: placed in formation"; continue; }
            if (!used.TryGetValue(obj.Id, out var taken)) used[obj.Id] = taken = new HashSet<int>();
            int main = s.MainList(ucd);
            SamDb.PtList? pick = null;
            foreach (int type in new[] { main, main == 4 ? 6 : 4, 19 })
            {
                var free = lists.Where(l => l.Type == type && !taken.Contains(l.Index)).ToList();
                pick = free.FirstOrDefault(l => l.Count >= veh) ?? free.FirstOrDefault();
                if (pick != null) break;
            }
            if (pick == null) { p.Result = "no free point list (all taken by other battalions on this objective)"; p.Missing = veh; continue; }
            taken.Add(pick.Index);
            var (fb, miss) = SamDb.Fit(needs, pick.PtTypes);
            p.List = $"#{pick.Index} {SamDb.PtName(pick.Type)} ({pick.Count} pts: {string.Join(" ", pick.PtTypes.OrderBy(k => k.Key).Select(k => $"{k.Value} {SamDb.PtName(k.Key)}"))})";
            p.Fallback = fb; p.Missing = miss;
            p.Result = miss > 0 ? $"{miss} vehicles without a point" : fb > 0 ? $"OK ({fb} on points of another type)" : "OK";
        }
        return res;
    }
}
