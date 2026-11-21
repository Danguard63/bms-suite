using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Database;

/// <summary>
/// Database Interchange for aircraft, helicopters, ground vehicles, ships, weapons and units (squadrons, battalions, task forces):
/// the record and everything it points to, renumbered for the destination.
/// <list type="bullet">
/// <item>CT entry → entity (VCD vehicle, WCD weapon, UCD unit) and 3D models (normal, repaired, damaged, destroyed);</item>
/// <item>aircraft (CT domain air) → aircraft data ACD → sensors (IR ICD, radar RCD, RWR RWD, visual VSD);</item>
/// <item>vehicle → radar RCD, slots → weapons WCD or hardpoint lists WLD → weapons;</item>
/// <item>weapon → radar RCD, sim data SWD, rocket pods (RKT) with their rockets;</item>
/// <item>unit → vehicles (CT), squadron stores SSD (weapon numbers renumbered too).</item>
/// </list>
/// A record the destination already has identical (same number or same name, same fields once renumbered, same 3D model) is
/// reused; everything else is added at the end of its table. Weapons that get a new number are added to the destination's
/// BmsRack.dat in the racks that carry them in the source. The Sim files (flight models, weapon and radar data) are not copied:
/// both theaters are expected to use BMS's Sim data. Nothing is written here: Write/Install/Restore are the same as for layouts.
/// </summary>
public static partial class Interchange
{
    public enum EntityKind { Aircraft, Helicopter, Ground, Ship, Weapon, Squadron, GroundUnit, NavalUnit }

    /// <summary>Something that can be copied: its CT entry, kind, name and a short description.</summary>
    public sealed record Entity(int Ct, EntityKind Kind, string Name, string Table, int Idx, int Model, string Detail);

    public static string KindName(EntityKind k) => k switch
    {
        EntityKind.Aircraft => "Aircraft", EntityKind.Helicopter => "Helicopter", EntityKind.Ground => "Ground vehicle", EntityKind.Ship => "Ship",
        EntityKind.Weapon => "Weapon", EntityKind.Squadron => "Squadron type", EntityKind.GroundUnit => "Battalion / brigade type", EntityKind.NavalUnit => "Task force type",
        _ => k.ToString(),
    };

    /// <summary>Aircraft, vehicles, ships, weapons and unit types of a theater (CT entries with a vehicle, weapon or unit).</summary>
    public static List<Entity> Entities(BmsDatabase db)
    {
        var res = new List<Entity>();
        foreach (var c in db.All("CT"))
        {
            int et = c.Int("EntityType"), dom = c.Int("Domain");
            string table = BmsDatabase.EntityTable(et);
            if (table is not ("VCD" or "WCD" or "UCD")) continue;
            var e = db.Get(table, c.Int("EntityIdx"));
            if (e == null || e.Name.Length == 0) continue;
            int model = c.Int("GraphicsNormal", 0);
            EntityKind kind;
            string detail;
            switch (table)
            {
                case "VCD":
                    // helicopters: CT Type 5 in the air domain (1 is airplane)
                    kind = dom == 2 ? (c.Int("Type") == 5 ? EntityKind.Helicopter : EntityKind.Aircraft) : dom is 4 or 7 ? EntityKind.Ship : EntityKind.Ground;
                    int slots = db.Slots(e).Count;
                    detail = $"model {model}, {slots} slot(s), {e.Int("HitPoints", 0)} hit points";
                    break;
                case "WCD":
                    kind = EntityKind.Weapon;
                    detail = $"{BmsDatabase.GuidanceName(e.Int("Guidance", 0))}, range {e.Int("Range", 0)}, model {model}";
                    break;
                default:
                    kind = dom == 2 ? EntityKind.Squadron : dom is 4 or 7 ? EntityKind.NavalUnit : EntityKind.GroundUnit;
                    var el = db.Elements(e).Where(x => x.Ct > 0 && x.Count > 0).ToList();
                    detail = el.Count == 0 ? "no vehicles" : string.Join(", ", el.GroupBy(x => x.Ct).Select(g => $"{g.Sum(x => x.Count)} × {db.CtName(g.Key)}"));
                    break;
            }
            res.Add(new Entity(c.Num, kind, e.Name, table, e.Num, model, detail));
        }
        return res.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Ct).ToList();
    }

    /// <summary>Which sensor table an ACD SensorType points to (0 IR, 1 radar, 2 RWR, 3 visual).</summary>
    public static string SensorTable(int type) => type switch { 0 => "ICD", 1 => "RCD", 2 => "RWD", 3 => "VSD", _ => "" };

    /// <summary>The plan to copy these CT entries (aircraft, vehicles, ships, weapons, unit types) of src into dst (nothing written).</summary>
    /// <param name="preferDestination">For a source of another BMS version (an old add-on): what the destination already has
    /// with the same name (weapons, sensors, unit vehicles; the aircraft data with the same flight model) is used as it is in
    /// the destination instead of being added again with the source's values, and fields the destination's tables do not
    /// know are left out. Only the chosen entries themselves are added.</param>
    /// <param name="modelMap">Source model numbers already decided (an old add-on's own models, copied into BMS's models with
    /// these numbers by the install): used as they are.</param>
    public static Plan MakeEntities(BmsTheater src, BmsTheater dst, string dataDir, IList<int> cts, bool preferDestination = false,
                                    IReadOnlyDictionary<int, int>? modelMap = null, IReadOnlyDictionary<int, int>? airframeMap = null,
                                    IReadOnlyDictionary<int, int>? acdAirframe = null)
    {
        var p = new Plan { Src = src, Dst = dst, BmsObjects = Path.Combine(dataDir, "Terrdata", "objects") };
        if (modelMap != null) foreach (var kv in modelMap) p.ModelMap[kv.Key] = kv.Value;
        if (Norm(src.ObjectDir) == Norm(dst.ObjectDir)) { p.Problems.Add("Source and destination use the same database: nothing to copy."); return p; }
        if (DbInstaller.IsShared(dataDir, dst.ObjectDir)) { p.Problems.Add("The destination uses BMS's shared Terrdata\\objects (Korea): it is never written."); return p; }
        var x = new EntityCopy(p, BmsDatabase.Load(src.ObjectDir), BmsDatabase.Load(dst.ObjectDir)) { PreferDestination = preferDestination, AirframeMap = airframeMap, AcdAirframe = acdAirframe };
        foreach (int ct in cts) x.Selected.Add(ct);
        foreach (int ct in cts)
        {
            int before = p.Records.Count;
            int n = x.MapCt(ct);
            p.CtMap[ct] = n;
            var sc = x.S.Get("CT", ct);
            string name = sc != null ? x.S.CtName(ct) : $"CT {ct}";
            p.Report.Add(p.Records.Count == before && x.D.Get("CT", n) != null
                ? $"{name} (CT {ct}): already in {dst.Name} as CT {n}, the same – nothing to add"
                : $"{name} (CT {ct}) → CT {n} in {dst.Name}");
        }
        x.Racks();
        if (preferDestination) { x.ServiceYears(); x.DropUnknownFields(); }
        var counts = p.Records.GroupBy(r => r.Table).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
        p.Report.Insert(0, $"{src.Name} → {dst.Name}: {cts.Count} item(s); new records: {(p.Records.Count == 0 ? "none" : string.Join(", ", counts))}; " +
                           $"{x.Reused} records reused; {p.ReusedModels} models already there, {p.CopiedModels} copied into {Path.Combine(dst.ModelsDir, "Models")}");
        if (x.SharedModels && modelMap == null)
            p.Report.Add($"Note: {dst.Name} takes its 3D models from BMS's shared Terrdata\\objects, which is never written: models that only {src.Name} has cannot be copied.");
        if (x.AnyAircraft)
            p.Report.Add("Note: aircraft keep their flight model, weapon and radar data numbers (Sim): both theaters use BMS's Sim data.");
        return p;
    }

    /// <summary>The copy of one plan: maps from source numbers to destination numbers, table by table.</summary>
    private sealed class EntityCopy
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        public readonly Plan P;
        public readonly BmsDatabase S, D;
        public readonly bool SharedModels;
        public int Reused;
        public bool AnyAircraft;
        /// <summary>See MakeEntities(preferDestination).</summary>
        public bool PreferDestination;
        /// <summary>Source AirframeDatIdx → destination one (the flight model of the same name in the two AcTypes.lst), or null.</summary>
        public IReadOnlyDictionary<int, int>? AirframeMap;
        /// <summary>Source ACD → destination flight model chosen for it (an aircraft given another BMS flight model).</summary>
        public IReadOnlyDictionary<int, int>? AcdAirframe;
        /// <summary>The CT entries chosen by the user (always added when the destination has no identical one).</summary>
        public readonly HashSet<int> Selected = new();
        private readonly Dictionary<(string, int), int> _map = new();
        private readonly Dictionary<string, int> _next = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>EntityIdx of the CT entries added by this plan (they are not in D yet).</summary>
        private readonly Dictionary<int, int> _newCtEntity = new();
        private readonly HashSet<int> _usedModelNums;
        /// <summary>Weapons (WCD) and sim data (SWD) added with a new number: source → destination.</summary>
        public readonly Dictionary<int, int> NewWeapons = new(), NewSwd = new();

        public EntityCopy(Plan p, BmsDatabase s, BmsDatabase d)
        {
            P = p; S = s; D = d;
            SharedModels = Norm(p.Dst.ModelsDir) == Norm(p.BmsObjects);
            _usedModelNums = new HashSet<int>(new[] { p.Dst.ModelsDir, p.BmsObjects }.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(r => Path.Combine(r, "Models")).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)
                .Select(x => int.TryParse(Path.GetFileName(x), NumberStyles.Integer, Ci, out int n) ? n : -1));
        }

        private int Next(string table)
        {
            if (!_next.TryGetValue(table, out int n)) n = D.NextFree(table);
            _next[table] = n + 1;
            return n;
        }

        private static string Key(IEnumerable<(string Field, string Value)> f) =>
            string.Join("\n", f.Select(x => x.Field + "=" + x.Value.Trim()).OrderBy(x => x, StringComparer.Ordinal));

        private static bool Same(IEnumerable<(string, string)> a, IEnumerable<(string, string)> b) => Key(a) == Key(b);

        /// <summary>The weapons of a hardpoint list (number × count), in a comparable form.</summary>
        private static string Weapons(IEnumerable<(string Field, string Value)> f)
        {
            var d = f.ToDictionary(x => x.Field, x => x.Value.Trim());
            return string.Join(",", d.Where(kv => kv.Key.StartsWith("WpnIdx_", StringComparison.Ordinal) && kv.Value != "0" && kv.Value != "-1")
                .Select(kv => kv.Value + "x" + d.GetValueOrDefault("WpnCount_" + kv.Key[7..], "")).OrderBy(x => x, StringComparer.Ordinal));
        }

        private string Label(string table, DbRecord r) => r.Name.Length > 0 ? $"{table} {r.Num} '{r.Name}'" : $"{table} {r.Num}";

        /// <summary>
        /// A record whose references are already renumbered: reused if the destination has the same one (same number first,
        /// then same name), otherwise added at the end of the table.
        /// </summary>
        private int Place(string table, DbRecord sr, List<(string Field, string Value)> fields, Func<DbRecord, bool>? alsoSame = null)
        {
            if (_map.TryGetValue((table, sr.Num), out int done)) return done;
            var dSame = D.Get(table, sr.Num);
            if (dSame != null && Same(dSame.Fields, fields) && (alsoSame?.Invoke(dSame) ?? true)) { Reused++; return _map[(table, sr.Num)] = sr.Num; }
            if (sr.Name.Length > 0)
                foreach (var r in D.All(table))
                    if (r.Name == sr.Name && Same(r.Fields, fields) && (alsoSame?.Invoke(r) ?? true)) { Reused++; return _map[(table, sr.Num)] = r.Num; }
            if (PreferDestination)
            {
                // the destination's own version: same name, or (aircraft data, no name) the same number with the same flight model
                string? airframe = fields.FirstOrDefault(x => x.Field == "AirframeDatIdx").Value?.Trim();
                var keep = table == "WLD"
                    // a hardpoint list: the destination's one only with the same weapons (an old add-on often changed BMS's
                    // lists keeping their names: the Super Etendard's "A-4SU 1/5" had French weapons, BMS's has Sidewinders)
                    ? D.All(table).FirstOrDefault(r => r.Name == sr.Name && Weapons(r.Fields) == Weapons(fields))
                    : sr.Name.Length > 0
                    ? (dSame != null && dSame.Name == sr.Name ? dSame : D.All(table).FirstOrDefault(r => r.Name == sr.Name))
                    : table == "ACD" && dSame != null && dSame.Get("AirframeDatIdx")?.Trim() == airframe ? dSame : null;
                if (keep != null)
                {
                    Reused++;
                    P.Report.Add($"  {Label(table, sr)}: the destination's own {Label(table, keep)} is used (its values, not the source's)");
                    return _map[(table, sr.Num)] = keep.Num;
                }
            }
            int n = Next(table);
            P.Records.Add((table, n, fields));
            string why = dSame == null ? "" : dSame.Name == sr.Name && sr.Name.Length > 0 ? $" ({table} {sr.Num} of the destination is different)" : $" ({table} {sr.Num} is another record in the destination)";
            P.Report.Add($"  {Label(table, sr)} → added as {table} {n}{why}");
            return _map[(table, sr.Num)] = n;
        }

        /// <summary>Records with no references to other tables: sensors, sim data.</summary>
        private int MapLeaf(string table, int idx)
        {
            if (idx < 0 || table.Length == 0) return idx;
            if (_map.TryGetValue((table, idx), out int m)) return m;
            var sr = S.Get(table, idx);
            if (sr == null) { if (D.Get(table, idx) == null) P.Problems.Add($"{table} {idx}: not in the source nor in the destination"); return idx; }
            int n = Place(table, sr, sr.Fields.ToList());
            if (table == "SWD" && n != idx && !(D.Get("SWD", n) is { } same && Same(same.Fields, sr.Fields))) NewSwd[idx] = n;
            return n;
        }

        /// <summary>Weapon by WCD number (as vehicle slots, lists and squadron stores use it), through its CT entry.</summary>
        private int MapWcd(int widx)
        {
            if (widx <= 0) return widx;
            if (_map.TryGetValue(("WCD", widx), out int m)) return m;
            var sw = S.Get("WCD", widx);
            if (sw == null) { P.Problems.Add($"weapon WCD {widx}: not in the source"); return widx; }
            int sct = S.CtOf(sw);
            if (sct < 0) { P.Problems.Add($"weapon {sw.Name} (WCD {widx}): no CT entry in the source"); return widx; }
            int nct = MapCt(sct);
            return _map.TryGetValue(("WCD", widx), out m) ? m : EntityIdxOf(nct);
        }

        private int EntityIdxOf(int dct) => _newCtEntity.TryGetValue(dct, out int e) ? e : D.Get("CT", dct)?.Int("EntityIdx") ?? -1;

        /// <summary>Hardpoint list: its weapons renumbered.</summary>
        private int MapWld(int idx)
        {
            if (idx < 0) return idx;
            if (_map.TryGetValue(("WLD", idx), out int m)) return m;
            var sr = S.Get("WLD", idx);
            if (sr == null) { P.Problems.Add($"hardpoint list WLD {idx}: not in the source"); return idx; }
            var f = sr.Fields.Select(x => Regex.IsMatch(x.Field, @"^WpnIdx_\d+$") && int.TryParse(x.Value, NumberStyles.Integer, Ci, out int w) && w > 0
                ? (x.Field, MapWcd(w).ToString(Ci)) : x).ToList();
            return Place("WLD", sr, f);
        }

        /// <summary>Squadron stores: WpnStores_&lt;weapon number&gt; renumbered.</summary>
        private int MapSsd(int idx)
        {
            if (idx < 0) return idx;
            if (_map.TryGetValue(("SSD", idx), out int m)) return m;
            var sr = S.Get("SSD", idx);
            if (sr == null) { if (D.Get("SSD", idx) == null) P.Report.Add($"  squadron stores SSD {idx}: not in the source nor in the destination (kept as {idx}, as in the source)"); return _map[("SSD", idx)] = idx; }
            var f = new List<(string, string)>();
            var left = new List<string>();
            foreach (var (k, v) in sr.Fields)
            {
                var mm = Regex.Match(k, @"^WpnStores_(\d+)$");
                if (!mm.Success) { f.Add((k, v)); continue; }
                int w = int.Parse(mm.Groups[1].Value, Ci);
                // another version's stores: weapons the destination does not have and the chosen entries do not bring stay out
                // (a squadron's stock would otherwise add every old weapon it lists)
                if (PreferDestination && !_map.ContainsKey(("WCD", w)) && S.Get("WCD", w) is { } sw && sw.Name.Length > 0 &&
                    !D.All("WCD").Any(x => x.Name == sw.Name))
                { left.Add(sw.Name); continue; }
                f.Add(($"WpnStores_{MapWcd(w).ToString(Ci)}", v));
            }
            if (left.Count > 0) P.Report.Add($"  squadron stores SSD {idx}: {left.Count} weapons the destination does not have left out ({string.Join(", ", left.Take(8))}{(left.Count > 8 ? "…" : "")})");
            return Place("SSD", sr, f);
        }

        /// <summary>Aircraft data: sensors renumbered.</summary>
        private int MapAcd(int idx)
        {
            if (idx < 0) return idx;
            if (_map.TryGetValue(("ACD", idx), out int m)) return m;
            var sr = S.Get("ACD", idx);
            if (sr == null) { P.Problems.Add($"aircraft data ACD {idx}: not in the source"); return idx; }
            var f = sr.Fields.ToList();
            int af = sr.Int("AirframeDatIdx", -1);
            if (AirframeMap != null && af >= 0)
            {
                if (AirframeMap.TryGetValue(af, out int naf))
                {
                    if (naf != af) P.Report.Add($"  aircraft data ACD {idx}: flight model {af} of the source is {naf} in the destination (same name in AcTypes.lst)");
                    f = DbOps.With(f, ("AirframeDatIdx", naf.ToString(Ci)));
                }
                else if (AcdAirframe == null || !AcdAirframe.ContainsKey(idx))
                    P.Problems.Add($"aircraft data ACD {idx}: flight model {af} of the source has no flight model of the same name in the destination");
            }
            if (AcdAirframe != null && AcdAirframe.TryGetValue(idx, out int chosen))
                f = DbOps.With(f, ("AirframeDatIdx", chosen.ToString(Ci)));
            for (int i = 0; i < 8; i++)
            {
                int type = sr.Int($"SensorType_{i}", -1), si = sr.Int($"SensorIdx_{i}", -1);
                if (sr.Get($"SensorIdx_{i}") == null || type < 0 || si < 0) continue;
                f = DbOps.With(f, ($"SensorIdx_{i}", MapLeaf(SensorTable(type), si).ToString(Ci)));
            }
            return Place("ACD", sr, f);
        }

        /// <summary>3D model: the same if the destination has it identical, otherwise copied (with a new number when taken).</summary>
        private int MapModel(int g, string forWhat)
        {
            if (g <= 0) return g;
            if (P.ModelMap.TryGetValue(g, out int m)) return m;
            string? sdir = ModelDir(P.Src, P.BmsObjects, g);
            if (sdir == null) { P.Problems.Add($"model {g} ({forWhat}) not found in the source: kept as {g}"); return P.ModelMap[g] = g; }
            string? ddir = ModelDir(P.Dst, P.BmsObjects, g);
            if (ddir != null && (Norm(sdir) == Norm(ddir) || SameFolder(sdir, ddir))) { P.ReusedModels++; return P.ModelMap[g] = g; }
            if (SharedModels)
            {
                if (ddir != null) P.Report.Add($"  model {g} ({forWhat}): {P.Dst.Name} has its own model {g} (BMS's shared models, never changed): it keeps that one and may look different");
                else P.Problems.Add($"model {g} ({forWhat}): not in BMS's shared models used by {P.Dst.Name}, which are never changed: it will not be visible");
                P.KeptModels++;
                return P.ModelMap[g] = g;
            }
            int n = g;
            if (ddir != null || _usedModelNums.Contains(g))
            {
                n = Math.Max(_usedModelNums.DefaultIfEmpty(0).Max() + 1, 1);
                P.Report.Add($"  model {g} ({forWhat}): the destination has a different model {g} → copied as model {n}");
            }
            _usedModelNums.Add(n);
            string target = Path.Combine(P.Dst.ModelsDir, "Models", n.ToString(Ci));
            foreach (var f in Directory.EnumerateFiles(sdir)) P.Files[Path.Combine(target, Path.GetFileName(f))] = (f, null);
            P.CopiedModels++;
            CheckTextures(P, P.Src, P.Dst, sdir, forWhat);
            return P.ModelMap[g] = n;
        }

        private static readonly string[] Graphics = { "GraphicsNormal", "GraphicsRepaired", "GraphicsDamaged", "GraphicsDestroyed", "GraphicsLeftDestroyed", "GraphicsRightDestroyed", "GraphicsBothDestroyed" };

        /// <summary>A CT entry with its entity (vehicle, weapon, unit) and everything they point to.</summary>
        public int MapCt(int sct)
        {
            if (_map.TryGetValue(("CT", sct), out int m)) return m;
            var sc = S.Get("CT", sct);
            if (sc == null) { P.Problems.Add($"CT {sct}: not in the source"); return _map[("CT", sct)] = sct; }
            int et = sc.Int("EntityType"), dom = sc.Int("Domain");
            string table = BmsDatabase.EntityTable(et);
            var se = table.Length > 0 ? S.Get(table, sc.Int("EntityIdx")) : null;
            if (table is not ("VCD" or "WCD" or "UCD") || se == null)
            {
                // pieces, objectives and others: only if the destination has the same number (they are not copied from here)
                if (D.Get("CT", sct) == null) P.Problems.Add($"CT {sct} ({S.CtName(sct)}): not a vehicle, weapon or unit, and not in the destination – kept as {sct}");
                return _map[("CT", sct)] = sct;
            }
            string name = se.Name;
            // another version's weapon or vehicle that the destination has with the same name: the destination's one, as it is
            // (decided before anything it points to is looked at, so nothing of the source's version is added for it)
            if (PreferDestination && !Selected.Contains(sct))
            {
                var own = D.Get("CT", sct) is { } dn && dn.Int("EntityType") == et && D.Get(table, dn.Int("EntityIdx"))?.Name == name ? dn
                    : D.All("CT").FirstOrDefault(c => c.Int("EntityType") == et && D.Get(table, c.Int("EntityIdx"))?.Name == name);
                if (own != null)
                {
                    Reused += 2;
                    _map[(table, se.Num)] = own.Int("EntityIdx");
                    P.Report.Add($"  {table} '{name}' (CT {sct}): the destination's own CT {own.Num} is used");
                    return _map[("CT", sct)] = own.Num;
                }
            }
            // a placeholder against loops (a unit's vehicle is never the unit itself, but stay safe)
            _map[("CT", sct)] = sct;

            // entity fields, references renumbered (its own CtIdx is set below)
            var ef = se.Fields.ToList();
            switch (table)
            {
                case "VCD":
                    if (dom == 2) AnyAircraft = true;
                    if (se.Int("RadarIdx", -1) >= 0) ef = DbOps.With(ef, ("RadarIdx", MapLeaf("RCD", se.Int("RadarIdx")).ToString(Ci)));
                    for (int i = 0; i < 32; i++)
                    {
                        if (se.Get($"WpnOrHpIdx_{i}") == null) break;
                        int idx = se.Int($"WpnOrHpIdx_{i}", 0), cnt = se.Int($"WpnCount_{i}", 0);
                        if (idx <= 0 && cnt <= 0) continue;
                        int nidx = cnt == 255 ? MapWld(idx) : idx > 0 ? MapWcd(idx) : idx;
                        ef = DbOps.With(ef, ($"WpnOrHpIdx_{i}", nidx.ToString(Ci)));
                    }
                    break;
                case "WCD":
                    if (se.Int("RadarIdx", -1) >= 0) ef = DbOps.With(ef, ("RadarIdx", MapLeaf("RCD", se.Int("RadarIdx")).ToString(Ci)));
                    if (se.Int("SimWeaponDataIdx", -1) >= 0) ef = DbOps.With(ef, ("SimWeaponDataIdx", MapLeaf("SWD", se.Int("SimWeaponDataIdx")).ToString(Ci)));
                    break;
                case "UCD":
                    for (int i = 0; i < 32; i++)
                    {
                        if (se.Get($"VehicleCtIdx_{i}") == null) continue;
                        int v = se.Int($"VehicleCtIdx_{i}", 0);
                        if (v > 0) ef = DbOps.With(ef, ($"VehicleCtIdx_{i}", MapCt(v).ToString(Ci)));
                    }
                    if (se.Int("SquadronStoresIdx", -1) >= 0) ef = DbOps.With(ef, ("SquadronStoresIdx", MapSsd(se.Int("SquadronStoresIdx")).ToString(Ci)));
                    break;
            }
            // CT fields: models, aircraft data (air vehicles only; the other mover numbers are Sim data, kept)
            var cf = sc.Fields.ToList();
            foreach (var g in Graphics)
                if (sc.Int(g, 0) > 0) cf = DbOps.With(cf, (g, MapModel(sc.Int(g), name).ToString(Ci)));
            if (et == BmsDatabase.Vehicle && dom == 2 && sc.Int("MoverDefinitionData", -1) >= 0)
                cf = DbOps.With(cf, ("MoverDefinitionData", MapAcd(sc.Int("MoverDefinitionData")).ToString(Ci)));

            // same CT entry already in the destination (same number or same name), with the same entity and models?
            bool Matches(DbRecord dc)
            {
                if (dc.Int("EntityType") != et) return false;
                var de = D.Get(table, dc.Int("EntityIdx"));
                if (de == null || de.Name != name) return false;
                var cfx = DbOps.With(cf, ("EntityIdx", dc.Get("EntityIdx") ?? ""));
                var efx = DbOps.With(ef, ("CtIdx", dc.Num.ToString(Ci)));
                return Same(dc.Fields, cfx) && Same(de.Fields, efx);
            }
            var cand = new List<DbRecord>();
            if (D.Get("CT", sct) is { } same) cand.Add(same);
            cand.AddRange(D.All("CT").Where(c => c.Num != sct && c.Int("EntityType") == et && D.Get(table, c.Int("EntityIdx"))?.Name == name));
            if (cand.FirstOrDefault(Matches) is { } hit)
            {
                Reused += 2;
                _map[(table, se.Num)] = hit.Int("EntityIdx");
                return _map[("CT", sct)] = hit.Num;
            }
            cf = UniqueClass(sct, table, dom, cf, se, name);
            int nct = Next("CT"), ne = Next(table);
            _map[("CT", sct)] = nct;
            _map[(table, se.Num)] = ne;
            _newCtEntity[nct] = ne;
            if (table == "WCD") NewWeapons[se.Num] = ne;
            P.Records.Add(("CT", nct, DbOps.With(cf, ("EntityIdx", ne.ToString(Ci)))));
            P.Records.Add((table, ne, DbOps.With(ef, ("CtIdx", nct.ToString(Ci)))));
            string what = D.Get("CT", sct) is { } dsame && D.EntityOf(sct)?.Name == name ? $" (the destination's CT {sct} '{name}' is different)" : "";
            P.Report.Add($"  {name}: CT {sct} → CT {nct}, {table} {se.Num} → {table} {ne}{what}");
            if (table == "WCD") MapRockets(se.Num);
            return nct;
        }

        // ───── class numbers (Domain, Class, Type, SubType, Specific): BMS finds aircraft and unit types by them (the TE's
        // TePlanes/TeUnits lists, a squadron's flights, a flight's aircraft), so a new aircraft or unit must not take numbers
        // another entry has in the destination (an old add-on's Super Etendard had the Specific of BMS 4.38's A-4SU)

        private HashSet<(int, int, int, int, int)>? _classKeys;
        /// <summary>Source CT → its new Specific, when it had to change.</summary>
        private readonly Dictionary<int, int> _respec = new();

        private static int FieldInt(List<(string Field, string Value)> f, string name, int def = -1) =>
            f.FirstOrDefault(x => x.Field == name).Value is { } v && int.TryParse(v.Trim(), NumberStyles.Integer, Ci, out int n) ? n : def;

        private static (int, int, int, int, int) ClassKey(List<(string Field, string Value)> f) =>
            (FieldInt(f, "Domain"), FieldInt(f, "Class"), FieldInt(f, "Type"), FieldInt(f, "SubType"), FieldInt(f, "Specific"));

        private HashSet<(int, int, int, int, int)> ClassKeys()
        {
            if (_classKeys != null) return _classKeys;
            _classKeys = new();
            foreach (var c in D.All("CT"))
                if (BmsDatabase.EntityTable(c.Int("EntityType")) is "VCD" or "UCD") _classKeys.Add(ClassKey(c.Fields.ToList()));
            return _classKeys;
        }

        private List<(string Field, string Value)> UniqueClass(int sct, string table, int dom, List<(string Field, string Value)> cf, DbRecord se, string name)
        {
            bool air = table == "VCD" && dom == 2, unit = table == "UCD";
            if (!air && !unit) return cf;
            int orig = FieldInt(cf, "Specific");
            int spec = orig;
            // a squadron or flight follows its aircraft (they share the Specific in BMS)
            if (unit)
            {
                int v = Enumerable.Range(0, 32).Select(i => se.Int($"VehicleCtIdx_{i}", 0)).FirstOrDefault(x => x > 0);
                if (v > 0 && _respec.TryGetValue(v, out int vs) && S.Get("CT", v)?.Int("Specific", -1) == orig) spec = vs;
            }
            var keys = ClassKeys();
            var k = ClassKey(cf);
            var key = (k.Item1, k.Item2, k.Item3, k.Item4, spec);
            if (keys.Contains(key))
            {
                // the lowest free Specific (1-254): for an aircraft, one no air entry uses (its squadrons and flights take it too)
                bool Free(int x) => air
                    ? !keys.Any(c => c.Item1 == 2 && (c.Item2 == k.Item2 || c.Item2 == 6) && c.Item5 == x)
                    : !keys.Contains((k.Item1, k.Item2, k.Item3, k.Item4, x));
                int x = Enumerable.Range(1, 254).FirstOrDefault(Free, -1);
                if (x < 0) { P.Problems.Add($"{name}: no free Specific for Domain {k.Item1} Class {k.Item2} Type {k.Item3} SubType {k.Item4}"); return cf; }
                var other = D.All("CT").FirstOrDefault(c => ClassKey(c.Fields.ToList()) == key);
                P.Report.Add($"  {name}: Specific {spec} → {x} (Type {k.Item3} SubType {k.Item4} Specific {spec} is " +
                             (other != null ? $"'{D.CtName(other.Num)}' (CT {other.Num})" : "another imported entry") + " in the destination; BMS finds aircraft and unit types by these numbers)");
                spec = x;
            }
            else if (spec != orig)
                P.Report.Add($"  {name}: Specific {orig} → {spec}, as its aircraft");
            keys.Add((k.Item1, k.Item2, k.Item3, k.Item4, spec));
            if (spec != orig) _respec[sct] = spec;
            return spec == orig ? cf : DbOps.With(cf, ("Specific", spec.ToString(Ci)));
        }

        /// <summary>A rocket pod: its rockets and its RKT record.</summary>
        private void MapRockets(int podWcd)
        {
            foreach (var r in S.All("RKT").Where(r => r.Int("RocketPodIdx") == podWcd).ToList())
            {
                var f = DbOps.With(r.Fields, ("RocketPodIdx", MapWcd(podWcd).ToString(Ci)), ("RocketWpnIdx", MapWcd(r.Int("RocketWpnIdx")).ToString(Ci)));
                if (D.All("RKT").Any(x => Same(x.Fields, f)) || P.Records.Any(x => x.Table == "RKT" && Same(x.Fields, f))) continue;
                int n = Next("RKT");
                P.Records.Add(("RKT", n, f));
                P.Report.Add($"  rocket pod {S.Get("WCD", podWcd)?.Name}: added as RKT {n}");
            }
        }

        /// <summary>Default first year of service for another version's vehicles and weapons that have none.</summary>
        public const int DefaultServiceStart = 1950;

        /// <summary>
        /// Vehicles and weapons of an older version (4.37 had no service years): BMS 4.38 offers in the Tactical Engagement only
        /// what has an InServiceStart (every aircraft of its TePlanes.lst has one), so the new records get one when the
        /// destination's table uses it. No InServiceEnd: still in service.
        /// </summary>
        public void ServiceYears()
        {
            var named = new List<string>();
            foreach (var (t, _, fields) in P.Records)
            {
                if (t is not ("VCD" or "WCD") || fields.Any(f => f.Field == "InServiceStart")) continue;
                if (!D.All(t).Any(r => r.Get("InServiceStart") != null)) continue;
                fields.Add(("InServiceStart", DefaultServiceStart.ToString(Ci)));
                named.Add(fields.FirstOrDefault(f => f.Field == "Name").Value ?? t);
            }
            if (named.Count > 0)
                P.Report.Add($"In service from {DefaultServiceStart} (the source has no service years; BMS 4.38's Tactical Engagement needs one): {string.Join(", ", named)}");
        }

        /// <summary>
        /// Leaves out of the new records the fields that no record of the same table in the destination has (a field of
        /// another BMS version). Numbered fields (WpnStores_12, ElementFlags_3…) count by their family name.
        /// </summary>
        public void DropUnknownFields()
        {
            static string Family(string f) => Regex.Replace(f, @"_\d+$", "_#");
            var known = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var dropped = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (t, _, fields) in P.Records)
            {
                if (!known.TryGetValue(t, out var k))
                    known[t] = k = D.All(t).SelectMany(r => r.Fields).Select(f => Family(f.Field)).ToHashSet(StringComparer.Ordinal);
                if (k.Count == 0) continue;
                fields.RemoveAll(f => { bool drop = !k.Contains(Family(f.Field)); if (drop) dropped.Add($"{t}.{f.Field}"); return drop; });
            }
            if (dropped.Count > 0) P.Report.Add($"Fields the destination's database does not have, left out: {string.Join(", ", dropped)}");
        }

        /// <summary>
        /// Weapons and sim data that got a new number: added to the destination's BmsRack.dat in the racks (same name)
        /// that carry them in the source, so that they can be loaded the same way.
        /// </summary>
        public void Racks()
        {
            if (NewWeapons.Count == 0 && NewSwd.Count == 0) return;
            string sFile = Path.Combine(P.Src.ObjectDir, "BmsRack.dat"), dFile = Path.Combine(P.Dst.ObjectDir, "BmsRack.dat");
            if (!File.Exists(sFile) || !File.Exists(dFile))
            {
                P.Report.Add($"Note: BmsRack.dat missing in the {(File.Exists(sFile) ? "destination" : "source")} objects folder: the new weapon numbers are not added to any rack (check the loadouts in BMS).");
                return;
            }
            var sRacks = ReadRacks(File.ReadAllLines(sFile));
            var lines = File.ReadAllLines(dFile).ToList();
            var dRacks = ReadRacks(lines.ToArray());
            int added = 0;
            foreach (var (rack, (swid, sswd)) in sRacks)
            {
                if (!dRacks.TryGetValue(rack, out var dr)) continue;
                var wids = swid.Where(NewWeapons.ContainsKey).Select(w => NewWeapons[w]).Where(n => !dr.Wid.Contains(n)).ToList();
                var swds = sswd.Where(NewSwd.ContainsKey).Select(w => NewSwd[w]).Where(n => !dr.Swd.Contains(n)).ToList();
                if (wids.Count == 0 && swds.Count == 0) continue;
                (int start, int end) = Block(lines, rack);
                if (start < 0) continue;
                void AddTo(string key, List<int> nums)
                {
                    if (nums.Count == 0) return;
                    string add = string.Join(" ", nums.Select(n => n.ToString(Ci)));
                    int at = Enumerable.Range(start, end - start).FirstOrDefault(i => Regex.IsMatch(lines[i], $@"^\s*{key}\b", RegexOptions.IgnoreCase), -1);
                    if (at >= 0) lines[at] = lines[at].TrimEnd() + " " + add;
                    else { lines.Insert(end, "\t" + key + " " + add); end++; }
                    added += nums.Count;
                }
                AddTo("addwid", wids);
                AddTo("addswd", swds);
            }
            if (added == 0) { P.Report.Add("BmsRack.dat: no rack of the destination needed the new weapon numbers."); return; }
            P.Files[dFile] = (null, string.Join("\r\n", lines) + "\r\n");
            P.Report.Add($"BmsRack.dat of {P.Dst.Name}: {added} new weapon number(s) added to the racks that carry them in {P.Src.Name}.");
        }

        private static Dictionary<string, (HashSet<int> Wid, HashSet<int> Swd)> ReadRacks(string[] lines)
        {
            var res = new Dictionary<string, (HashSet<int>, HashSet<int>)>(StringComparer.OrdinalIgnoreCase);
            (HashSet<int> W, HashSet<int> S)? cur = null;
            foreach (var raw in lines)
            {
                string l = raw.Split('#')[0].Trim();
                if (l.Length == 0) continue;
                var m = Regex.Match(l, @"^definerack\s+(\S+)", RegexOptions.IgnoreCase);
                if (m.Success) { cur = (new HashSet<int>(), new HashSet<int>()); res[m.Groups[1].Value] = cur.Value; continue; }
                if (Regex.IsMatch(l, @"^define", RegexOptions.IgnoreCase)) { cur = null; continue; }
                if (cur == null) continue;
                var k = Regex.Match(l, @"^(addwid|addswd)\s+(.*)$", RegexOptions.IgnoreCase);
                if (!k.Success) continue;
                var set = k.Groups[1].Value.Equals("addwid", StringComparison.OrdinalIgnoreCase) ? cur.Value.W : cur.Value.S;
                foreach (var t in Regex.Split(k.Groups[2].Value, @"[\s,]+"))
                    if (int.TryParse(t, NumberStyles.Integer, Ci, out int n)) set.Add(n);
            }
            return res;
        }

        /// <summary>Lines of a definerack block: from the line after "definerack name" to the next define (or the end).</summary>
        private static (int, int) Block(List<string> lines, string rack)
        {
            int s = lines.FindIndex(l => Regex.IsMatch(l.Split('#')[0], $@"^\s*definerack\s+{Regex.Escape(rack)}\s*$", RegexOptions.IgnoreCase));
            if (s < 0) return (-1, -1);
            int e = s + 1;
            while (e < lines.Count && !Regex.IsMatch(lines[e].Split('#')[0], @"^\s*define", RegexOptions.IgnoreCase)) e++;
            // keep blank lines and comments after the block where they are
            while (e - 1 > s && lines[e - 1].Split('#')[0].Trim().Length == 0) e--;
            return (s + 1, e);
        }
    }
}
