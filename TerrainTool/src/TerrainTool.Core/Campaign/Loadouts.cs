namespace TerrainTool.Core.Campaign;

/// <summary>
/// Regole del carico di un aereo come le applica BMS nella finestra dei carichi:
///   - pilone n (1-15) esiste se il VCD dell'aereo ha WpnOrHpIdx_n; il pilone 0 e' il cannone;
///   - su ogni pilone una sola arma, tra quelle della sua lista WLD, al massimo quante ne dice la lista (rastrelliera);
///   - due piloni con la stessa lista WLD sono la coppia simmetrica (es. F-16: 1/9, 2/8, 3/7, 4/6);
///   - peso del carico entro decollo massimo - peso a vuoto - carburante interno (VCD);
///   - le armi devono esserci nel magazzino dello squadrone (per tutti gli aerei del volo).
/// </summary>
public sealed class LoadoutRules
{
    private readonly CampaignDb _db;
    public int Vcd { get; }
    public string Aircraft { get; }
    public LoadoutRules(CampaignDb db, int unitCt)
    {
        _db = db;
        Vcd = db.AircraftVcdOf(unitCt);
        Aircraft = db.UnitVehicles(unitCt).FirstOrDefault() ?? "";
    }

    public sealed record Pylon(int Index, int Wld, string Name, List<(int Wcd, int Max)> Weapons);

    /// <summary>I piloni dell'aereo (senza il cannone), nell'ordine dei numeri.</summary>
    public List<Pylon> Pylons()
    {
        var res = new List<Pylon>();
        if (!_db.VcdHardpoints.TryGetValue(Vcd, out var hp)) return res;
        for (int i = 1; i < 16; i++)
            if (hp[i] > 0 && _db.Wld.TryGetValue(hp[i], out var w)) res.Add(new Pylon(i, hp[i], w.Name, w.Weapons));
        return res;
    }

    /// <summary>Il pilone simmetrico (stessa lista WLD), o -1.</summary>
    public int Mirror(int pylon)
    {
        if (!_db.VcdHardpoints.TryGetValue(Vcd, out var hp) || pylon <= 0 || pylon > 15 || hp[pylon] <= 0) return -1;
        for (int i = 1; i < 16; i++) if (i != pylon && hp[i] == hp[pylon]) return i;
        return -1;
    }

    public int MaxOn(int pylon, int wcd) => Pylons().FirstOrDefault(p => p.Index == pylon)?.Weapons.Where(w => w.Wcd == wcd).Select(w => w.Max).DefaultIfEmpty(0).Max() ?? 0;

    public (int Max, int Empty, int Fuel) Weights => _db.VcdWeights.GetValueOrDefault(Vcd);
    /// <summary>Carico utile massimo (libbre): decollo massimo - a vuoto - carburante interno; 0 se il database non lo dice.</summary>
    public int MaxPayload => Weights.Max > 0 ? Math.Max(0, Weights.Max - Weights.Empty - Weights.Fuel) : 0;

    public int Weight(short[] ids, byte[] counts)
    {
        int w = 0;
        for (int i = 1; i < 16; i++) if (ids[i] > 0) w += _db.WcdWeight.GetValueOrDefault(ids[i]) * counts[i];
        return w;
    }

    public string WeaponName(int wcd) => _db.Wcd.GetValueOrDefault(wcd, $"weapon {wcd}");
    public int WeaponWeight(int wcd) => _db.WcdWeight.GetValueOrDefault(wcd);

    /// <summary>Problemi del carico: errori (BMS non lo accetterebbe) e avvisi (magazzino). stores = magazzino dello squadrone per WCD (o null).</summary>
    public List<(bool Error, string Text)> Check(short[] ids, byte[] counts, int aircraft, byte[]? stores)
    {
        var res = new List<(bool, string)>();
        var pyl = Pylons().ToDictionary(p => p.Index);
        for (int i = 1; i < 16; i++)
        {
            if (ids[i] <= 0 || counts[i] == 0) continue;
            if (!pyl.TryGetValue(i, out var p)) { res.Add((true, $"Pylon {i}: this aircraft has no pylon {i}")); continue; }
            int max = p.Weapons.Where(w => w.Wcd == ids[i]).Select(w => w.Max).DefaultIfEmpty(0).Max();
            if (max == 0) res.Add((true, $"Pylon {i}: {WeaponName(ids[i])} is not allowed on {p.Name}"));
            else if (counts[i] > max) res.Add((true, $"Pylon {i}: {counts[i]} × {WeaponName(ids[i])}, at most {max}"));
        }
        int wt = Weight(ids, counts);
        if (MaxPayload > 0 && wt > MaxPayload) res.Add((true, $"Weight {wt:N0} lb, the aircraft can carry {MaxPayload:N0} lb"));
        if (stores != null)
            foreach (var g in Enumerable.Range(1, 15).Where(i => ids[i] > 0 && counts[i] > 0).GroupBy(i => ids[i]))
            {
                int need = g.Sum(i => counts[i]) * Math.Max(1, aircraft);
                int have = g.Key < stores.Length ? stores[g.Key] : 0;
                if (have < need) res.Add((false, $"{WeaponName(g.Key)}: {need} needed for {aircraft} aircraft, {have} in the squadron's stores"));
            }
        return res;
    }
}
