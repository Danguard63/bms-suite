using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>Abbinamento di un tipo di unita' del vecchio database (indice CT) a uno del database BMS 4.38.</summary>
public sealed class TypeMatch
{
    public int OldIdx;
    public UnitKind Kind;
    public int Count;                         // unita' di questo tipo nei file scelti
    public string OldName = "";
    public List<string> OldVehicles = new();
    public int NewIdx = -1;
    public int AutoIdx = -1;
    public bool Manual;
    public string Quality = "";
    public int Score;
    public List<(int Idx, string Text, int Score)> Candidates = new();
    /// <summary>Produttore del mezzo principale del vecchio tipo (occidentale / sovietico / cinese).</summary>
    public Bloc OldBloc;

    public string OldText => OldName + (OldVehicles.Count > 0 ? " (" + string.Join(", ", OldVehicles.Take(3)) + (OldVehicles.Count > 3 ? "…" : "") + ")" : "");
}

/// <summary>
/// Conversione dei tipi di unita' delle campagne/missioni del vecchio add-on (indici della sua Falcon4_CT) agli indici
/// del database BMS 4.38 usato dal nuovo terreno (ObjectsNT). Abbinamento per tipo di unita' (UCD) e mezzi (VCD);
/// le armi nei magazzini degli squadroni e nei carichi dei voli si rimappano per nome (WCD).
/// Si cambiano solo questi indici: posizioni, gerarchie, ordini, obiettivi restano identici.
/// </summary>
public static class UnitTypeConverter
{
    public const string MapFile = "unit_type_map.csv";
    public const string DoneFile = "units_converted.txt";

    public static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Somiglianza 0..1 tra due nomi (uguali normalizzati = 1, altrimenti trigrammi di Dice).</summary>
    public static double Sim(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        static HashSet<string> Tri(string s) { var h = new HashSet<string>(); s = "  " + s + " "; for (int i = 0; i + 3 <= s.Length; i++) h.Add(s.Substring(i, 3)); return h; }
        var x = Tri(a); var y = Tri(b);
        int inter = x.Count(y.Contains);
        return 2.0 * inter / (x.Count + y.Count) * 0.95;
    }

    /// <param name="vehicleSub">mezzo del vecchio add-on → mezzo 4.38 scelto (per i mezzi che nella 4.38 non ci sono)</param>
    public static TypeMatch Match(int oldIdx, CampaignDb oldDb, CampaignDb newDb, UnitKind kind, IReadOnlyDictionary<string, string>? vehicleSub = null)
    {
        var m = new TypeMatch { OldIdx = oldIdx, Kind = kind, OldName = oldDb.UcdName(oldIdx), OldVehicles = oldDb.UnitVehicles(oldIdx) };
        if (!oldDb.Ct.TryGetValue(oldIdx, out var o)) { m.Quality = "Unknown in old database"; return m; }
        // i mezzi che la 4.38 non ha si confrontano con il sostituto scelto
        var tr = m.OldVehicles.Select(v => vehicleSub != null && vehicleSub.TryGetValue(Norm(v), out var s) ? s : v).ToList();
        string main = tr.FirstOrDefault() ?? "";
        var oldSet = tr.Select(Norm).ToHashSet();
        var cands = new List<(int Idx, int Score)>();
        var mainBloc = Producer.OfName(main);
        var oldVcts = oldDb.UnitVehicleCts(oldIdx);
        var mainRole = oldVcts.Count > 0 ? oldDb.RoleOf(oldVcts[0]) : VehicleRole.Other;
        m.OldBloc = Producer.OfName(m.OldVehicles.FirstOrDefault());
        foreach (var (idx, c) in newDb.Ct)
        {
            if (c.Domain != o.Domain || c.Class != o.Class || c.Type != o.Type || c.EntityType != o.EntityType) continue;
            if (!newDb.Ucd.TryGetValue(c.EntityIdx, out var u) || u.Name.Length == 0) continue;
            var veh = newDb.UnitVehicles(idx);
            double nameSim = Sim(m.OldName, u.Name);
            double vehSim = Sim(main, veh.FirstOrDefault() ?? "");
            var newSet = veh.Select(Norm).ToHashSet();
            double jac = oldSet.Count + newSet.Count == 0 ? 0 : (double)oldSet.Intersect(newSet).Count() / oldSet.Union(newSet).Count();
            double score = 45 * nameSim + 40 * vehSim + 10 * jac + (c.SubType == o.SubType ? 3 : 0) + (idx == oldIdx ? 2 : 0)
                         + 0.6 * Producer.Affinity(mainBloc, Producer.OfName(veh.FirstOrDefault()))
                         - (VehicleRoles.Compatible(mainRole, newDb.UnitVehicleCts(idx).Select(newDb.RoleOf).FirstOrDefault()) ? 0 : 35);   // SAM per SAM, carri per carri
            cands.Add((idx, (int)Math.Round(score)));
        }
        foreach (var (idx, sc) in cands.OrderByDescending(c => c.Score).Take(12))
            m.Candidates.Add((idx, Describe(newDb, idx), sc));
        if (m.Candidates.Count == 0) { m.Quality = "No equivalent"; return m; }
        var best = m.Candidates[0];
        m.AutoIdx = m.NewIdx = best.Idx; m.Score = best.Score;
        var bv = newDb.UnitVehicles(best.Idx);
        bool sameName = Norm(newDb.UcdName(best.Idx)) == Norm(m.OldName);
        bool sameMain = Norm(bv.FirstOrDefault() ?? "") == Norm(main);
        bool subst = vehicleSub != null && m.OldVehicles.Any(v => vehicleSub.ContainsKey(Norm(v)));
        bool sameAll = bv.Select(Norm).ToHashSet().SetEquals(oldSet);
        m.Quality = best.Idx == oldIdx && sameName && sameAll ? "Unchanged"
                  : sameName && sameAll ? "Same type, moved"
                  : sameMain && subst && Norm(m.OldVehicles.FirstOrDefault() ?? "") != Norm(main) ? "Your vehicle"
                  : sameMain ? "Same vehicle"
                  : best.Score >= 60 ? "Close" : "Substitute";
        return m;
    }

    // ───── mezzi ─────

    public sealed class VehicleMatch
    {
        public string OldName = "";
        public int OldCt;
        public string Category = "";
        public int Units;                       // unita' (nei file scelti) che hanno questo mezzo
        public List<string> UsedIn = new();     // tipi di unita' che lo usano
        public bool Exists;                     // c'e' anche nella 4.38 (stesso nome)
        public string? NewName;                 // sostituto scelto (null = nessuno)
        public string? AutoName;
        public bool Manual;
        public Bloc Bloc;                       // produttore ricavato dal nome
        public VehicleRole Role;                // ruolo (SAM, carro, IFV...): si sostituisce solo con lo stesso
        public List<(string Name, int Score)> Candidates = new();
    }

    public static string VehicleCategory(CampaignDb.CtEntry c) =>
        (c.Domain switch { 2 => "Air", 3 => "Land", 4 => "Sea", _ => "Domain " + c.Domain }) + $" type {c.Type}/{c.SubType}";

    /// <summary>
    /// Mezzi usati dalle unita' (tipi oldTypes con quante unita'): quelli che nella 4.38 non esistono ricevono una lista di
    /// mezzi 4.38 dello stesso genere (stesso dominio e tipo CT; prima quelli dello stesso sottotipo), ordinata per somiglianza.
    /// </summary>
    public static List<VehicleMatch> MatchVehicles(IReadOnlyDictionary<int, int> oldTypes, CampaignDb oldDb, CampaignDb newDb)
    {
        var newByName = newDb.VehicleByName();
        var res = new Dictionary<string, VehicleMatch>();
        foreach (var (t, n) in oldTypes)
            foreach (int v in oldDb.UnitVehicleCts(t))
            {
                string name = oldDb.VehicleName(v);
                if (name.Length == 0) continue;
                string key = Norm(name);
                if (!res.TryGetValue(key, out var vm))
                {
                    var c = oldDb.Ct[v];
                    vm = new VehicleMatch { OldName = name, OldCt = v, Category = VehicleCategory(c), Exists = newByName.ContainsKey(key), Bloc = Producer.OfName(name), Role = oldDb.RoleOf(v) };
                    if (!vm.Exists)
                    {
                        var ob = vm.Bloc;
                        bool land = c.Domain == 3;
                        // mezzi di terra: stesso ruolo (SAM con SAM, carro con carro, IFV con IFV), cingolato o ruotato che sia;
                        // aerei e navi: stesso tipo CT come prima
                        var pool = newDb.Ct.Where(kv => kv.Value.EntityType == 5 && kv.Value.Domain == c.Domain &&
                                                        (land ? VehicleRoles.Compatible(vm.Role, newDb.RoleOf(kv.Key)) : kv.Value.Type == c.Type)).ToList();
                        var cands = pool.Select(kv => (Name: newDb.VehicleName(kv.Key), C: kv.Value)).Where(x => x.Name.Length > 0)
                            .GroupBy(x => Norm(x.Name)).Select(g => g.First())
                            .Select(x => (x.Name, Score: (int)Math.Round(60 * Sim(name, x.Name)
                                + (land ? (x.C.Type == c.Type ? 20 : 0) + (x.C.SubType == c.SubType ? 10 : 0) : x.C.SubType == c.SubType ? 30 : 0)
                                + (x.C.Specific == c.Specific ? 5 : 0) + Producer.Affinity(ob, Producer.OfName(x.Name)))))
                            .OrderByDescending(x => x.Score).ThenBy(x => x.Name).Take(40).ToList();
                        vm.Candidates = cands;
                        vm.AutoName = vm.NewName = cands.FirstOrDefault().Name;
                    }
                    res[key] = vm;
                }
                vm.Units += n;
                string un = oldDb.UcdName(t);
                if (!vm.UsedIn.Contains(un)) vm.UsedIn.Add(un);
            }
        return res.Values.OrderBy(v => v.Exists).ThenByDescending(v => v.Units).ToList();
    }

    // ───── armi ─────

    public sealed class WeaponMatch
    {
        public int OldIdx;
        public string OldName = "";
        public string Category = "";
        public int Uses;                        // quante volte compare in magazzini e carichi
        public int NewIdx = -1;                 // -1 = togliere
        public Bloc Bloc;
        public int AutoIdx = -1;
        public bool Manual;
        public List<(int Idx, string Name, int Score)> Candidates = new();
    }

    /// <summary>Armi dell'add-on che la 4.38 non ha (stesso nome): alternative dello stesso genere (CT dell'arma), per somiglianza.</summary>
    public static List<WeaponMatch> MatchWeapons(IReadOnlyDictionary<int, int> uses, CampaignDb oldDb, CampaignDb newDb)
    {
        var res = new List<WeaponMatch>();
        foreach (var (i, n) in uses)
        {
            string name = oldDb.Wcd.GetValueOrDefault(i, "");
            if (newDb.Wcd.TryGetValue(i, out var same) && Norm(same) == Norm(name)) continue;
            if (newDb.Wcd.Any(kv => Norm(kv.Value) == Norm(name))) continue;   // c'e' con un altro numero: si sposta da solo
            var oc = oldDb.WeaponCt(i);
            var wm = new WeaponMatch { OldIdx = i, OldName = name, Uses = n, Category = oc == null ? "" : $"type {oc.Type}/{oc.SubType}", Bloc = Producer.OfName(name) };
            var wb = wm.Bloc;
            wm.Candidates = newDb.Wcd.Select(kv => (kv.Key, kv.Value, C: newDb.WeaponCt(kv.Key)))
                .Where(x => oc == null || x.C != null && x.C.Type == oc.Type)
                .Select(x => (Idx: x.Key, Name: x.Value, Score: (int)Math.Round(60 * Sim(name, x.Value) + (oc != null && x.C?.SubType == oc.SubType ? 30 : 0) + Producer.Affinity(wb, Producer.OfName(x.Value)))))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Name).Take(40).ToList();
            var best = wm.Candidates.FirstOrDefault();
            // scelta automatica solo se nome e genere bastano da soli (il produttore ordina, ma non basta per scegliere)
            int aff = Producer.Affinity(wb, Producer.OfName(best.Name)), raw = best.Score - aff;
            wm.AutoIdx = wm.NewIdx = raw >= 50 || aff == 25 && raw >= 40 ? best.Idx : -1;
            res.Add(wm);
        }
        return res.OrderByDescending(w => w.Uses).ToList();
    }

    /// <summary>Usi di ogni arma (indice WCD) nei magazzini degli squadroni e nei carichi dei voli.</summary>
    public static void CountWeapons(CampaignDoc d, Dictionary<int, int> uses)
    {
        if (d.Units == null) return;
        var b = d.Units.Data;
        foreach (var u in d.Units.Units)
        {
            if (u.Kind == UnitKind.Squadron)
                for (int i = 0; i < u.StoresLen; i++) if (b[u.Start + u.StoresAt + i] != 0) uses[i] = uses.GetValueOrDefault(i) + 1;
            if (u.Kind == UnitKind.Flight && u.LoadoutIdSize == 2)
                for (int l = 0; l < u.LoadoutCount; l++)
                    for (int k = 0; k < 16; k++)
                    {
                        int w = BitConverter.ToUInt16(b, u.Start + u.LoadoutAt + l * 48 + 2 * k);
                        if (w != 0) uses[w] = uses.GetValueOrDefault(w) + 1;
                    }
        }
    }

    public static Dictionary<string, string> LoadPairs(string path)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(path)) return d;
        foreach (var l in File.ReadAllLines(path).Skip(1))
        {
            var p = l.Split(';');
            if (p.Length >= 2 && p[0].Trim().Length > 0) d[p[0].Trim()] = p[1].Trim();
        }
        return d;
    }

    public static void SavePairs(string path, string header, IEnumerable<(string A, string B, string Note)> rows)
    {
        var sb = new StringBuilder(header + "\r\n");
        foreach (var (a, b, n) in rows) sb.Append(a.Replace(';', ',')).Append(';').Append(b.Replace(';', ',')).Append(';').Append(n.Replace(';', ',')).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static string Describe(CampaignDb db, int idx)
    {
        var v = db.UnitVehicles(idx);
        return $"{db.UcdName(idx)}" + (v.Count > 0 ? " (" + string.Join(", ", v.Take(3)) + (v.Count > 3 ? "…" : "") + ")" : "") + $"  [#{idx}]";
    }

    /// <summary>Armi: vecchio indice WCD → nuovo, per nome (normalizzato); -1 se nel 4.38 non c'e'.</summary>
    public static Dictionary<int, int> WeaponMap(CampaignDb oldDb, CampaignDb newDb, out List<string> lost)
    {
        var byName = newDb.Wcd.GroupBy(kv => Norm(kv.Value)).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var map = new Dictionary<int, int>();
        lost = new List<string>();
        foreach (var (i, n) in oldDb.Wcd)
        {
            if (newDb.Wcd.TryGetValue(i, out var same) && Norm(same) == Norm(n)) { map[i] = i; continue; }
            if (byName.TryGetValue(Norm(n), out var l)) { map[i] = l.OrderBy(x => Math.Abs(x - i)).First(); continue; }
            // nome simile (es. "Tank F-15 Grey" → "Tank F-15 ..."): il piu' somigliante sopra 0.6
            var best = newDb.Wcd.Select(kv => (kv.Key, S: Sim(n, kv.Value))).OrderByDescending(x => x.S).FirstOrDefault();
            if (best.S >= 0.5) { map[i] = best.Key; lost.Add($"{i} {n} → {newDb.Wcd[best.Key]}"); continue; }
            map[i] = -1; lost.Add($"{i} {n} → (none: dropped)");
        }
        return map;
    }

    // ---- file della mappa (modificabile a mano: la colonna NewIdx vince sull'automatico se Manual = 1) ----

    public static void SaveMap(string path, IEnumerable<TypeMatch> ms, CampaignDb newDb)
    {
        var sb = new StringBuilder("OldIdx;NewIdx;Manual;Quality;Units;Kind;OldType;NewType\r\n");
        foreach (var m in ms.OrderBy(m => m.Kind).ThenBy(m => m.OldName))
            sb.Append(CultureInfo.InvariantCulture, $"{m.OldIdx};{m.NewIdx};{(m.Manual ? 1 : 0)};{m.Quality};{m.Count};{m.Kind};{m.OldText.Replace(';', ',')};{(m.NewIdx >= 0 ? Describe(newDb, m.NewIdx).Replace(';', ',') : "")}\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static Dictionary<int, int> LoadManual(string path)
    {
        var res = new Dictionary<int, int>();
        if (!File.Exists(path)) return res;
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length >= 3 && p[2].Trim() == "1" && int.TryParse(p[0], out int o) && int.TryParse(p[1], out int n)) res[o] = n;
        }
        return res;
    }

    public static HashSet<string> LoadDone(string dir)
    {
        string f = Path.Combine(dir, DoneFile);
        return File.Exists(f) ? File.ReadAllLines(f).Select(l => l.Split(';')[0].Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    public static void MarkDone(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, DoneFile), $"{name};{DateTime.Now:yyyy-MM-dd HH:mm}\r\n");
    }

    /// <summary>
    /// Converte le unita' di un documento letto con il VECCHIO database: tipo (inizio record e entityType), magazzini degli
    /// squadroni e carichi dei voli. Restituisce le righe di resoconto.
    /// </summary>
    public static List<string> Convert(CampaignDoc doc, IReadOnlyDictionary<int, int> typeMap, IReadOnlyDictionary<int, int> weaponMap)
    {
        var log = new List<string>();
        var uf = doc.Units ?? throw new InvalidOperationException("no units");
        if (uf.Error != null) throw new InvalidDataException(uf.Error);
        var b = uf.Data;
        int changed = 0, lostStores = 0, lostLoads = 0, missing = 0;
        foreach (var u in uf.Units)
        {
            int old = u.Type - 100;
            if (!typeMap.TryGetValue(old, out int nw) || nw < 0) { missing++; continue; }
            if (nw != old)
            {
                short t = (short)(nw + 100);
                BitConverter.TryWriteBytes(b.AsSpan(u.Start, 2), t);
                BitConverter.TryWriteBytes(b.AsSpan(u.Start + 10, 2), (ushort)t);
                u.Type = t;
                changed++;
            }
            if (u.Kind == UnitKind.Squadron && u.StoresLen > 0)
            {
                int s0 = u.Start + u.StoresAt;
                var old0 = b.AsSpan(s0, u.StoresLen).ToArray();
                var nw0 = new byte[u.StoresLen];
                for (int i = 0; i < old0.Length; i++)
                {
                    if (old0[i] == 0) continue;
                    int j = weaponMap.TryGetValue(i, out int x) ? x : i;
                    if (j < 0 || j >= nw0.Length) { lostStores++; continue; }
                    nw0[j] = (byte)Math.Min(255, nw0[j] + old0[i]);
                }
                nw0.CopyTo(b, s0);
            }
            if (u.Kind == UnitKind.Flight && u.LoadoutCount > 0 && u.LoadoutIdSize == 2)
            {
                int p = u.Start + u.LoadoutAt;
                for (int l = 0; l < u.LoadoutCount; l++, p += 48)
                    for (int k = 0; k < 16; k++)
                    {
                        int w = BitConverter.ToUInt16(b, p + 2 * k);
                        if (w == 0) continue;
                        int j = weaponMap.TryGetValue(w, out int x) ? x : w;
                        if (j < 0) { j = 0; b[p + 32 + k] = 0; lostLoads++; }
                        BitConverter.TryWriteBytes(b.AsSpan(p + 2 * k, 2), (ushort)j);
                    }
            }
        }
        doc.Dirty = true;
        log.Add($"{changed} of {uf.Units.Count} units changed type");
        if (missing > 0) log.Add($"{missing} units without a mapping (left as they are)");
        if (lostStores > 0) log.Add($"{lostStores} squadron store entries dropped (weapons not in BMS 4.38)");
        if (lostLoads > 0) log.Add($"{lostLoads} flight loadout weapons removed (not in BMS 4.38)");
        return log;
    }

    /// <summary>
    /// Controllo dopo la conversione: ogni unita' deve puntare a un tipo di unita' dello stesso genere (battaglione → battaglione,
    /// squadrone → squadrone...) e ogni arma di magazzini e carichi a un'arma che esiste nel database. Mai un mezzo al posto di un'arma.
    /// </summary>
    public static (int BadUnits, int BadStores, int BadLoads) Verify(CampaignDoc d, CampaignDb db)
    {
        int bu = 0, bs = 0, bl = 0;
        if (d.Units == null) return (0, 0, 0);
        var b = d.Units.Data;
        foreach (var u in d.Units.Units)
        {
            var c = db.Ct.GetValueOrDefault(u.Type - 100);
            bool ok = c != null && db.Ucd.ContainsKey(c.EntityIdx) && u.Kind switch
            {
                UnitKind.Battalion => c.Domain == 3 && c.Type == 1, UnitKind.Brigade => c.Domain == 3 && c.Type == 2,
                UnitKind.Flight => c.Domain == 2 && c.Type == 1, UnitKind.Package => c.Domain == 2 && c.Type == 2,
                UnitKind.Squadron => c.Domain == 2 && c.Type == 3, UnitKind.TaskForce => c.Domain == 4 && c.Type == 1, _ => false,
            };
            if (!ok) bu++;
            if (u.Kind == UnitKind.Squadron)
                for (int i = 0; i < u.StoresLen; i++) if (b[u.Start + u.StoresAt + i] != 0 && !db.Wcd.ContainsKey(i)) bs++;
            if (u.Kind == UnitKind.Flight && u.LoadoutIdSize == 2)
                for (int l = 0; l < u.LoadoutCount; l++)
                    for (int k = 0; k < 16; k++)
                    {
                        int w = BitConverter.ToUInt16(b, u.Start + u.LoadoutAt + l * 48 + 2 * k);
                        if (w != 0 && !db.Wcd.ContainsKey(w)) bl++;
                    }
        }
        return (bu, bs, bl);
    }
}
