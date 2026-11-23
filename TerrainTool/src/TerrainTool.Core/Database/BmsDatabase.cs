using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Database;

/// <summary>One record of a BMS database table (Falcon4_XXX.xml): its number and all its fields in file order.</summary>
public sealed class DbRecord
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public string Table { get; }
    public int Num { get; }
    public List<(string Field, string Value)> Fields { get; }
    private readonly Dictionary<string, string> _map;

    public DbRecord(string table, int num, List<(string, string)> fields)
    {
        Table = table; Num = num; Fields = fields;
        _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fields) _map.TryAdd(k, v);
    }

    public string? Get(string field) => _map.TryGetValue(field, out var v) ? v : null;
    public int Int(string field, int def = -1) => _map.TryGetValue(field, out var v) && int.TryParse(v, NumberStyles.Integer, Ci, out int i) ? i : def;
    public long Long(string field, long def = 0) => _map.TryGetValue(field, out var v) && long.TryParse(v, NumberStyles.Integer, Ci, out long i) ? i : def;
    public double Dbl(string field, double def = 0) => _map.TryGetValue(field, out var v) && double.TryParse(v, NumberStyles.Float, Ci, out double d) ? d : def;
    public string Name => Get("Name") ?? Get("WpnName") ?? "";
}

/// <summary>
/// The database of a BMS theater (the Falcon4_*.xml files of its objects folder), read only, with the links between the tables
/// as the BMS Editor shows them: Class Table (CT) → entity (vehicle VCD, weapon WCD, unit UCD, feature FCD, objective) and model;
/// vehicle slots → weapon (WCD) or hardpoint list (WLD); weapon → sim data (SWD), radar (RCD); unit → vehicles (CT); aircraft
/// → ACD (CT MoverDefinitionData) → sensors. BMS compiles these XML files into FALCON4.* by itself.
/// </summary>
public sealed class BmsDatabase
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public string Dir { get; }
    /// <summary>Table code (CT, VCD, WCD, UCD, FCD, WLD, SWD, ACD, RCD, ICD, RWD, VSD, SSD, RKT, DDP…) → records by number.</summary>
    public Dictionary<string, SortedDictionary<int, DbRecord>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = new();

    // CT EntityType: which table the CT record points to
    public const int Feature = 1, Objective = 3, Unit = 4, Vehicle = 5, Weapon = 6;
    public static string EntityTable(int entityType) => entityType switch { 1 => "FCD", 3 => "OCD", 4 => "UCD", 5 => "VCD", 6 => "WCD", _ => "" };
    public static string DomainName(int d) => d switch { 1 => "abstract", 2 => "air", 3 => "land", 4 => "sea", 5 => "space", 6 => "underground", 7 => "undersea", _ => $"domain {d}" };

    private BmsDatabase(string dir) { Dir = dir; }

    private static readonly EnumerationOptions NoCase = new() { MatchCasing = MatchCasing.CaseInsensitive };

    /// <summary>
    /// The database is only in the compiled FALCON4.* files, with no Falcon4_*.xml (e.g. a theater made with older tools): the suite
    /// reads and writes the XML tables, so it sees such a database as empty and must not plan anything for it.
    /// </summary>
    public static bool CompiledOnly(string objectsDir) =>
        Directory.Exists(objectsDir) && !Directory.EnumerateFiles(objectsDir, "Falcon4_*.xml", NoCase).Any() &&
        Directory.EnumerateFiles(objectsDir, "FALCON4.CT", NoCase).Any();

    /// <summary>The explanation shown when a theater's database is <see cref="CompiledOnly"/>.</summary>
    public static string CompiledOnlyText(string theater, string objectsDir) =>
        $"{theater} has its database only in compiled FALCON4.* files ({objectsDir}), with no Falcon4_*.xml tables: the BMS Suite reads and writes " +
        "the XML tables, so it cannot read this database nor add to it";

    public static BmsDatabase Load(string objectsDir)
    {
        var db = new BmsDatabase(objectsDir);
        foreach (var f in Directory.EnumerateFiles(objectsDir, "Falcon4_*.xml", NoCase))
        {
            string code = Path.GetFileNameWithoutExtension(f)["Falcon4_".Length..].ToUpperInvariant();
            try
            {
                var t = new SortedDictionary<int, DbRecord>();
                string text = File.ReadAllText(f);
                foreach (Match m in Regex.Matches(text, @"<(\w+) Num=""(-?\d+)"">(.*?)</\1>", RegexOptions.Singleline))
                {
                    var fields = Regex.Matches(m.Groups[3].Value, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline)
                        .Select(x => (x.Groups[1].Value, WebUtility.HtmlDecode(x.Groups[2].Value).Trim())).ToList();
                    int num = int.Parse(m.Groups[2].Value, Ci);
                    t[num] = new DbRecord(code, num, fields);
                }
                db.Tables[code] = t;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { db.Problems.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        // objective types: OCD_n.XML in ObjectiveRelatedData (there is no Falcon4_OCD.xml)
        string ord = Path.Combine(objectsDir, "ObjectiveRelatedData");
        if (!db.Tables.ContainsKey("OCD") && Directory.Exists(ord))
        {
            var t = new SortedDictionary<int, DbRecord>();
            foreach (var d in Directory.EnumerateDirectories(ord, "OCD_*", NoCase))
                foreach (var f in Directory.EnumerateFiles(d, "OCD_*.XML", NoCase))
                {
                    try
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(f), @"<OCD Num=""(\d+)"">(.*?)</OCD>", RegexOptions.Singleline))
                        {
                            var fields = Regex.Matches(m.Groups[2].Value, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline)
                                .Select(x => (x.Groups[1].Value, WebUtility.HtmlDecode(x.Groups[2].Value).Trim())).ToList();
                            int num = int.Parse(m.Groups[1].Value, Ci);
                            t[num] = new DbRecord("OCD", num, fields);
                        }
                    }
                    catch (IOException) { }
                }
            if (t.Count > 0) db.Tables["OCD"] = t;
        }
        return db;
    }

    /// <summary>Records as they were in the files before the changes of the work folder (table, number) → record, or null for an added one.</summary>
    public Dictionary<(string, int), DbRecord?> Originals { get; } = new();

    /// <summary>Puts a changed or new record in the database in memory (the file is not touched).</summary>
    public void SetRecord(string table, int num, List<(string Field, string Value)> fields)
    {
        table = table.ToUpperInvariant();
        if (!Tables.TryGetValue(table, out var t)) Tables[table] = t = new SortedDictionary<int, DbRecord>();
        if (!Originals.ContainsKey((table, num))) Originals[(table, num)] = t.GetValueOrDefault(num);
        t[num] = new DbRecord(table, num, fields);
        _ctIndex = null;
    }

    /// <summary>Applies the changes of the work folder on top of the files.</summary>
    public void Apply(DbEdits edits)
    {
        foreach (var r in edits.Data.Records) SetRecord(r.Table, r.Num, r.Fields.Select(f => (f[0], f[1])).ToList());
    }

    /// <summary>First number after the last one of a table (new records go at the end, so that nothing existing moves).</summary>
    public int NextFree(string table) => Tables.TryGetValue(table, out var t) && t.Count > 0 ? t.Keys.Max() + 1 : 0;

    /// <summary>Longest name BMS keeps for a table (the files never have longer ones: the compiled records have fixed-size names).</summary>
    public static int MaxName(string table) => table switch { "WLD" => 15, "WCD" => 17, "VCD" => 19, _ => 15 };

    public DbRecord? Get(string table, int num) => Tables.TryGetValue(table, out var t) && t.TryGetValue(num, out var r) ? r : null;
    public IEnumerable<DbRecord> All(string table) => Tables.TryGetValue(table, out var t) ? t.Values : Enumerable.Empty<DbRecord>();

    /// <summary>The record a CT entry points to (vehicle, weapon, unit, feature, objective), or null.</summary>
    public DbRecord? EntityOf(int ct) => Get("CT", ct) is { } c ? Get(EntityTable(c.Int("EntityType")), c.Int("EntityIdx")) : null;

    /// <summary>Name of the thing behind a CT index.</summary>
    public string CtName(int ct) => EntityOf(ct)?.Name ?? (Get("CT", ct) is { } c ? $"CT {ct} ({EntityTable(c.Int("EntityType"))} {c.Int("EntityIdx")})" : $"CT {ct}");

    /// <summary>CT index of a record (its CtIdx field, or the CT entry that points to it).</summary>
    public int CtOf(DbRecord r)
    {
        if (r.Table == "CT") return r.Num;
        int ct = r.Int("CtIdx");
        if (ct >= 0 && Get("CT", ct) != null) return ct;
        return CtIndex().TryGetValue((r.Table, r.Num), out int c) ? c : -1;
    }

    /// <summary>3D model number (CT GraphicsNormal) of a record, or -1.</summary>
    public int ModelOf(DbRecord r) => Get("CT", CtOf(r)) is { } c ? c.Int("GraphicsNormal") : -1;

    private Dictionary<(string, int), int>? _ctIndex;
    private Dictionary<(string, int), int> CtIndex()
    {
        if (_ctIndex != null) return _ctIndex;
        _ctIndex = new();
        foreach (var c in All("CT"))
        {
            string t = EntityTable(c.Int("EntityType"));
            if (t.Length > 0) _ctIndex.TryAdd((t, c.Int("EntityIdx")), c.Num);
        }
        return _ctIndex;
    }

    /// <summary>Domain of a vehicle (air, land, sea, undersea) from its CT entry.</summary>
    public int DomainOf(DbRecord r) => Get("CT", CtOf(r))?.Int("Domain") ?? 0;

    /// <summary>The 12 slots of a vehicle: weapon (WCD index, rounds) or hardpoint list (WLD index, count 255), visible, rack.</summary>
    public List<(int Slot, bool Hardpoint, int Idx, int Count, bool Visible, bool Rack)> Slots(DbRecord vcd)
    {
        var res = new List<(int, bool, int, int, bool, bool)>();
        long vis = vcd.Long("VisibleFlags"), rack = vcd.Long("RackFlags");
        for (int i = 0; i < 16; i++)
        {
            int idx = vcd.Int($"WpnOrHpIdx_{i}", -1);
            if (vcd.Get($"WpnOrHpIdx_{i}") == null) break;
            int cnt = vcd.Int($"WpnCount_{i}", 0);
            if (idx <= 0 && cnt <= 0) continue;
            res.Add((i, cnt == 255, idx, cnt, (vis >> i & 1) != 0, (rack >> i & 1) != 0));
        }
        return res;
    }

    /// <summary>Weapons of a hardpoint list (WLD): WCD index and count.</summary>
    public List<(int Wcd, int Count)> ListWeapons(DbRecord wld)
    {
        var res = new List<(int, int)>();
        for (int i = 0; i < 64; i++)
        {
            if (wld.Get($"WpnIdx_{i}") == null) break;
            int w = wld.Int($"WpnIdx_{i}"), c = wld.Int($"WpnCount_{i}", 0);
            if (w > 0) res.Add((w, c));
        }
        return res;
    }

    /// <summary>Elements of a unit (UCD): vehicle CT index, count, name of the element.</summary>
    public List<(int Ct, int Count, string Name)> Elements(DbRecord ucd)
    {
        var res = new List<(int, int, string)>();
        for (int i = 0; i < 32; i++)
        {
            if (ucd.Get($"VehicleCtIdx_{i}") == null && ucd.Get($"ElementCount_{i}") == null) { if (i > 16) break; continue; }
            int ct = ucd.Int($"VehicleCtIdx_{i}"), n = ucd.Int($"ElementCount_{i}", 0);
            if (ct > 0 || n > 0) res.Add((ct, n, ucd.Get($"ElementName_{i}") ?? ""));
        }
        return res;
    }

    /// <summary>Who uses a record: vehicles carrying a weapon (directly or through a hardpoint list), lists with a weapon, vehicles
    /// with a list, units with a vehicle, records with this radar/sensor/sim data, CT entries with a model.</summary>
    public List<(DbRecord Who, string How)> UsedBy(DbRecord r)
    {
        var res = new List<(DbRecord, string)>();
        switch (r.Table)
        {
            case "WCD":
                foreach (var l in All("WLD")) if (ListWeapons(l).Any(x => x.Wcd == r.Num)) res.Add((l, "hardpoint list"));
                var lists = res.Select(x => x.Item1.Num).ToHashSet();
                foreach (var v in All("VCD"))
                    foreach (var s in Slots(v))
                        if (!s.Hardpoint && s.Idx == r.Num) res.Add((v, $"slot {s.Slot}, {s.Count} rounds"));
                        else if (s.Hardpoint && lists.Contains(s.Idx)) res.Add((v, $"slot {s.Slot} through list {s.Idx}"));
                foreach (var k in All("RKT")) if (k.Int("RocketWpnIdx") == r.Num || k.Int("RocketPodIdx") == r.Num) res.Add((k, "rocket pod"));
                foreach (var st in All("SSD")) if (st.Int($"WpnStores_{r.Num}", 0) > 0) res.Add((st, $"squadron stores: {st.Int($"WpnStores_{r.Num}", 0)}"));
                break;
            case "WLD":
                foreach (var v in All("VCD")) foreach (var s in Slots(v)) if (s.Hardpoint && s.Idx == r.Num) res.Add((v, $"slot {s.Slot}"));
                break;
            case "VCD":
                int ct = CtOf(r);
                foreach (var u in All("UCD")) foreach (var e in Elements(u)) if (e.Ct == ct) res.Add((u, $"{e.Count} × {e.Name}".Trim(' ', '×')));
                foreach (var u in All("UCD")) if (u.Int("RadarVehicle") == ct) res.Add((u, "radar vehicle"));
                break;
            case "SWD":
                foreach (var w in All("WCD")) if (w.Int("SimWeaponDataIdx") == r.Num) res.Add((w, "sim data"));
                break;
            case "RCD":
                foreach (var t in new[] { "VCD", "WCD", "FCD" }) foreach (var x in All(t)) if (x.Int("RadarIdx") == r.Num && r.Num > 0) res.Add((x, "radar"));
                break;
            case "SSD":
                foreach (var u in All("UCD")) if (u.Int("SquadronStoresIdx") == r.Num) res.Add((u, "squadron stores"));
                break;
            case "ACD":
                foreach (var c in All("CT")) if (c.Int("EntityType") == Vehicle && c.Int("MoverDefinitionData") == r.Num && c.Int("Domain") == 2) res.Add((c, "aircraft data"));
                break;
            case "CT":
                break;
        }
        if (r.Table is "FCD" or "UCD" or "VCD" or "WCD" && CtOf(r) is int c0 && c0 >= 0 && Get("CT", c0) is { } ctr)
        {
            int model = ctr.Int("GraphicsNormal");
            if (model > 0)
                foreach (var c in All("CT")) if (c.Num != c0 && c.Int("GraphicsNormal") == model) res.Add((c, $"same 3D model {model}"));
        }
        return res;
    }

    /// <summary>Records of a table whose name or number matches the text.</summary>
    public IEnumerable<DbRecord> Search(string table, string q) =>
        All(table).Where(r => q.Length == 0 || r.Num.ToString(Ci) == q || DisplayName(r).Contains(q, StringComparison.OrdinalIgnoreCase));

    /// <summary>Name to show for any record (CT: the name of its entity).</summary>
    public string DisplayName(DbRecord r) => r.Table switch
    {
        "CT" => CtName(r.Num),
        "ACD" => Get("CT", All("CT").FirstOrDefault(c => c.Int("EntityType") == Vehicle && c.Int("Domain") == 2 && c.Int("MoverDefinitionData") == r.Num)?.Num ?? -1) is { } c2 ? CtName(c2.Num) : $"ACD {r.Num}",
        "RKT" => $"{Get("WCD", r.Int("RocketPodIdx"))?.Name ?? "?"} → {Get("WCD", r.Int("RocketWpnIdx"))?.Name ?? "?"}",
        "SSD" => $"Squadron stores {r.Num}",
        "UCD" => Elements(r).FirstOrDefault(e => e.Count > 0 && e.Ct > 0) is { Ct: > 0 } e0 ? $"{r.Name} – {CtName(e0.Ct)}" : r.Name,
        "DDP" => $"Priority list {r.Num}",
        _ => r.Name.Length > 0 ? r.Name : $"{r.Table} {r.Num}",
    };

    // ───────────── flag names, as the BMS Editor shows them (bits checked against the KTO database)

    public static readonly string?[] VehicleFlags =
    {
        "Air Force", "Navy", "Marines", "Army", "All Weather", "Stealth", "Night", "VTOL", "Flat Container", "Elevated Container",
        "Can Explode", "Can Burn", "Can Smoke", "Can Collapse", "Has Crew", "Is Towed", "Has Jammer", "Is Air Defense", "Has NCTR",
        "Has Exact RWR", "Uses Unit Radar", null, null, null, null, null, null, null, null, "Has HMS", "Has Chaff/Flares", "Gunfighter",
    };
    public static readonly string?[] WeaponFlags =
    {
        "Recon", "Fuel", "ECM", "Area Effect", "Cluster", "Tracer", null, null, "Bomb Warhead",
    };
    public static readonly string?[] FeatureFlags =
    {
        "Enemy Can Repair", "Is Virtual", "Has Lights", "Has Smoke Stack", "Is Flat Container", "Is Elevated Container",
        "Container Top", "Next Is Top", "Do Not Evaluate", "Point Trackable", "Is Tacan",
    };

    public static string FlagText(long flags, string?[] names)
    {
        var l = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((flags >> i & 1) != 0) l.Add(i < names.Length && names[i] != null ? names[i]! : $"bit {i}");
        return l.Count == 0 ? "none" : string.Join(", ", l);
    }

    /// <summary>WCD Guidance is a set of bits: 0 = unguided (visual).</summary>
    public static string GuidanceName(int g)
    {
        if (g == 0) return "Unguided / visual";
        var l = new List<string>();
        string[] n = { "Anti-radiation", "IR", "Radar", "Laser", "TV", "Rear aspect" };
        for (int i = 0; i < 16; i++) if ((g >> i & 1) != 0) l.Add(i < n.Length ? n[i] : $"bit {1 << i}");
        return string.Join(" + ", l);
    }

    public static readonly string[] TargetKinds = { "NoMove", "Foot", "Wheeled", "Tracked", "LowAir", "Air", "Naval", "Rail" };

    public static string DamageTypeName(int t) => t switch
    {
        0 => "None", 1 => "Penetration", 2 => "High explosive", 3 => "Heave", 4 => "Incendiary", 5 => "Proximity", 6 => "Kinetic",
        7 => "Hydrostatic", 8 => "Chemical", 9 => "Nuclear", 10 => "Other", _ => $"type {t}",
    };
}
