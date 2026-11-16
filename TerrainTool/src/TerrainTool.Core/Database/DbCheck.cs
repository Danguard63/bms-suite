namespace TerrainTool.Core.Database;

/// <summary>
/// Checks that every link of the database points to something that exists and is of the right kind: CT → entity, entity → CT,
/// vehicle slots → weapon / hardpoint list, hardpoint lists → weapons, weapons → sim data / radar, units → vehicles, aircraft data
/// → sensors, squadron stores → weapons, rocket pods → weapons, names not longer than BMS keeps.
/// </summary>
public static class DbCheck
{
    public sealed record Problem(DbRecord Record, string Message, bool Error);

    public static List<Problem> Run(BmsDatabase d)
    {
        var res = new List<Problem>();
        void P(DbRecord r, string m, bool err = true) => res.Add(new Problem(r, m, err));
        bool Has(string t, int n) => d.Get(t, n) != null;

        foreach (var c in d.All("CT"))
        {
            string t = BmsDatabase.EntityTable(c.Int("EntityType"));
            int idx = c.Int("EntityIdx");
            if (t is "VCD" or "WCD" or "UCD" or "FCD" && idx >= 0 && !Has(t, idx)) P(c, $"points to {t} {idx}, which does not exist");
        }
        foreach (var t in new[] { "VCD", "WCD", "UCD", "FCD" })
            foreach (var r in d.All(t))
            {
                int ct = r.Int("CtIdx");
                if (ct <= 0 || r.Num == 0) continue;   // record 0 = "no vehicle / no weapon" placeholder
                if (r.Name.StartsWith("_Empty", StringComparison.OrdinalIgnoreCase)) continue;   // posti liberi riservati dai creatori del teatro: il loro CtIdx e' vecchio
                var c = d.Get("CT", ct);
                if (c == null) P(r, $"CtIdx {ct} does not exist");
                else if (BmsDatabase.EntityTable(c.Int("EntityType")) != t || c.Int("EntityIdx") != r.Num)
                    P(r, $"CtIdx {ct} points back to {BmsDatabase.EntityTable(c.Int("EntityType"))} {c.Int("EntityIdx")}, not to this record (CT linkage inconsistent)", false);
                if (t is "VCD" or "WCD" && r.Name.Length > BmsDatabase.MaxName(t)) P(r, $"name longer than {BmsDatabase.MaxName(t)} characters", false);
            }
        foreach (var v in d.All("VCD"))
        {
            foreach (var s in d.Slots(v))
            {
                if (s.Hardpoint && !Has("WLD", s.Idx)) P(v, $"slot {s.Slot}: hardpoint list {s.Idx} does not exist");
                if (!s.Hardpoint && s.Idx > 0 && !Has("WCD", s.Idx)) P(v, $"slot {s.Slot}: weapon {s.Idx} does not exist");
            }
            if (v.Int("RadarIdx", 0) > 0 && !Has("RCD", v.Int("RadarIdx"))) P(v, $"radar {v.Int("RadarIdx")} does not exist");
        }
        foreach (var l in d.All("WLD"))
        {
            var w = d.ListWeapons(l);
            foreach (var (wcd, n) in w) if (!Has("WCD", wcd)) P(l, $"weapon {wcd} does not exist");
            if (w.Count > 64) P(l, $"{w.Count} weapons: BMS keeps at most 64");
            if (l.Name.Length > BmsDatabase.MaxName("WLD")) P(l, $"name longer than {BmsDatabase.MaxName("WLD")} characters", false);
        }
        foreach (var w in d.All("WCD"))
        {
            int s = w.Int("SimWeaponDataIdx");
            if (s > 0 && !Has("SWD", s)) P(w, $"sim weapon data {s} does not exist");
            if (w.Int("RadarIdx", 0) > 0 && !Has("RCD", w.Int("RadarIdx"))) P(w, $"seeker radar {w.Int("RadarIdx")} does not exist");
        }
        foreach (var u in d.All("UCD"))
        {
            foreach (var (ct, n, _) in d.Elements(u))
                if (n > 0 && (d.Get("CT", ct) is not { } c || c.Int("EntityType") != BmsDatabase.Vehicle)) P(u, $"element with CT {ct}: not a vehicle");
            // SquadronStoresIdx: in the Korea database many units point past the end of the SSD table (meaning unclear): not checked
        }
        foreach (var a in d.All("ACD"))
            for (int i = 0; i < 5; i++)
            {
                int t = a.Int($"SensorType_{i}"), idx = a.Int($"SensorIdx_{i}");
                string tb = t switch { 0 => "VSD", 1 => "RCD", 2 => "ICD", 3 => "RWD", _ => "" };
                if (tb.Length > 0 && idx >= 0 && !Has(tb, idx)) P(a, $"sensor {i + 1}: {tb} {idx} does not exist", false);
            }
        foreach (var s in d.All("SSD"))
            foreach (var (k, v) in s.Fields.Where(f => f.Field.StartsWith("WpnStores_", StringComparison.Ordinal)))
                if (int.TryParse(k["WpnStores_".Length..], out int w) && !Has("WCD", w)) P(s, $"weapon {w} does not exist");
        foreach (var k in d.All("RKT"))
        {
            if (!Has("WCD", k.Int("RocketPodIdx"))) P(k, $"pod {k.Int("RocketPodIdx")} does not exist");
            if (!Has("WCD", k.Int("RocketWpnIdx"))) P(k, $"rocket {k.Int("RocketWpnIdx")} does not exist");
        }
        return res;
    }
}
