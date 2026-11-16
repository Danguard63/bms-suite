using System.Globalization;

namespace TerrainTool.Core.Database;

/// <summary>The changes the Model Editor can make, as new field lists (field order of the file kept; nothing is written here).</summary>
public static class DbOps
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static List<(string Field, string Value)> With(IEnumerable<(string Field, string Value)> fields, params (string Field, string Value)[] set)
    {
        var l = fields.ToList();
        foreach (var (k, v) in set)
        {
            int i = l.FindIndex(f => f.Field == k);
            if (i >= 0) l[i] = (k, v); else l.Add((k, v));
        }
        return l;
    }

    /// <summary>Number of slot fields of a vehicle (WpnOrHpIdx_0..n-1).</summary>
    public static int SlotCount(DbRecord vcd) => vcd.Fields.Count(f => f.Field.StartsWith("WpnOrHpIdx_", StringComparison.Ordinal));

    /// <summary>A vehicle slot: hardpoint list (count 255), weapon with rounds, or empty (0, 0); visible and rack bits.</summary>
    public static List<(string, string)> SetSlot(DbRecord vcd, int slot, bool hardpoint, int idx, int rounds, bool visible, bool rack)
    {
        long vis = vcd.Long("VisibleFlags"), rk = vcd.Long("RackFlags");
        vis = visible ? vis | (1L << slot) : vis & ~(1L << slot);
        rk = rack ? rk | (1L << slot) : rk & ~(1L << slot);
        int count = idx <= 0 ? 0 : hardpoint ? 255 : Math.Clamp(rounds, 1, 254);
        return With(vcd.Fields, ($"WpnOrHpIdx_{slot}", Math.Max(0, idx).ToString(Ci)), ($"WpnCount_{slot}", count.ToString(Ci)),
                    ("VisibleFlags", vis.ToString(Ci)), ("RackFlags", rk.ToString(Ci)));
    }

    /// <summary>All the slots (with visible and rack bits) of another vehicle.</summary>
    public static List<(string, string)> CopySlots(DbRecord target, DbRecord source)
    {
        int n = Math.Min(SlotCount(target), SlotCount(source));
        var set = new List<(string, string)>();
        for (int i = 0; i < n; i++)
        {
            set.Add(($"WpnOrHpIdx_{i}", source.Get($"WpnOrHpIdx_{i}") ?? "0"));
            set.Add(($"WpnCount_{i}", source.Get($"WpnCount_{i}") ?? "0"));
        }
        set.Add(("VisibleFlags", source.Get("VisibleFlags") ?? "0"));
        set.Add(("RackFlags", source.Get("RackFlags") ?? "0"));
        return With(target.Fields, set.ToArray());
    }

    /// <summary>Hardpoint list with these weapons (Name, then WpnIdx_0.., then WpnCount_0.., as in the file).</summary>
    public static List<(string, string)> ListWith(DbRecord wld, string name, IList<(int Wcd, int Count)> weapons)
    {
        var l = new List<(string, string)> { ("Name", name) };
        l.AddRange(wld.Fields.Where(f => f.Field != "Name" && !f.Field.StartsWith("WpnIdx_", StringComparison.Ordinal) && !f.Field.StartsWith("WpnCount_", StringComparison.Ordinal)));
        for (int i = 0; i < weapons.Count; i++) l.Add(($"WpnIdx_{i}", weapons[i].Wcd.ToString(Ci)));
        for (int i = 0; i < weapons.Count; i++) l.Add(($"WpnCount_{i}", Math.Clamp(weapons[i].Count, 1, 255).ToString(Ci)));
        return l;
    }

    /// <summary>Name for a copy of a list, within the length BMS keeps: "F-16 CCIP 3/7" → "F-16 CCIP 3/7*".</summary>
    public static string CopyName(string name, int max)
    {
        name = name.TrimEnd('*').TrimEnd();
        return name.Length + 1 <= max ? name + "*" : name[..(max - 1)] + "*";
    }

    /// <summary>Who would be touched by a change of a vehicle: units (squadrons, battalions…) that have it.</summary>
    public static List<DbRecord> UnitsWith(BmsDatabase d, DbRecord vcd)
    {
        int ct = d.CtOf(vcd);
        return d.All("UCD").Where(u => d.Elements(u).Any(e => e.Ct == ct && e.Count > 0)).ToList();
    }

    /// <summary>Vehicles whose slots use a hardpoint list.</summary>
    public static List<DbRecord> VehiclesWithList(BmsDatabase d, int wld) =>
        d.All("VCD").Where(v => d.Slots(v).Any(s => s.Hardpoint && s.Idx == wld)).ToList();
}
