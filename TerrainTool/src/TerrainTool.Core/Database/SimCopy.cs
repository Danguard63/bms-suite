using System.Globalization;
using System.Text;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Sim;

namespace TerrainTool.Core.Database;

/// <summary>
/// The Sim files the copied records need (Database Interchange, "copy the Sim files"): weapon data (SWD WpnDatIdx → a line of
/// Misdata\MisTypes.lst or Bombdata\BombTypes.lst), radar data (RCD RadarDatIdx → Radar\RadTypes.lst) and flight models
/// (ACD AirframeDatIdx → Acdata\AcTypes.lst). Each number is the position of a name in that list (0 = the first name after
/// the count line); the file is &lt;folder&gt;\&lt;name&gt;.dat (.txtpb for flight models). The names are matched between the two
/// theaters' lists: the same file is reused, a missing one is copied and added at the end of the destination's list.
/// Flight models of aircraft flyable in BMS are never copied. When the destination uses BMS's own Data\Sim, it gets a Sim
/// folder of its own first (a copy of BMS's, simdatadir in its .tdf). Everything goes through the plan (backup, Restore).
/// </summary>
public static class SimCopy
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private sealed class Kind
    {
        public string Folder = "", List = "", Ext = ".dat", What = "";
        public List<string> Src = new(), Dst = new();
        public bool Changed;
    }

    private static string? FindCI(string dir, string name)
    {
        if (!Directory.Exists(dir)) return null;
        string f = Path.Combine(dir, name);
        if (File.Exists(f)) return f;
        return Directory.EnumerateFiles(dir).FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadList(string? f) =>
        f == null || !File.Exists(f) ? new() : File.ReadAllLines(f, Encoding.Latin1).Skip(1).Select(l => l.Trim()).ToList();

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Weapon class (SWD WpnClass) → its data list: bombs and guided bombs in Bombdata, missiles, rockets, SAMs in Misdata.</summary>
    private static string? WeaponList(int cls) => cls switch { 2 or 9 => "Bombdata", 0 or 1 or 6 or 7 or 8 => "Misdata", _ => null };

    public static void Apply(Interchange.Plan p, BmsDatabase S, BmsDatabase D, string dataDir)
    {
        string srcSim = TheaterSim.SimDir(p.Src), dstSim = TheaterSim.SimDir(p.Dst), bmsSim = Path.Combine(p.Dst.DataDir, "Sim");
        if (string.Equals(Path.GetFullPath(srcSim).TrimEnd('\\', '/'), Path.GetFullPath(dstSim).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        { p.Report.Add($"Sim files: both theaters use the same Sim folder ({srcSim}): nothing to copy"); return; }
        bool own = TheaterSim.HasOwnSim(p.Dst);
        string dstBase = own ? dstSim : bmsSim;          // where the destination's files are read now
        string dstOut = own ? dstSim : TheaterSim.NewSimDir(p.Dst);   // where they are written
        var kinds = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
        {
            ["Misdata"] = new() { Folder = "Misdata", List = "MisTypes.lst", What = "missile data" },
            ["Bombdata"] = new() { Folder = "Bombdata", List = "BombTypes.lst", What = "bomb data" },
            ["Radar"] = new() { Folder = "Radar", List = "RadTypes.lst", What = "radar data" },
            ["Acdata"] = new() { Folder = "Acdata", List = "AcTypes.lst", Ext = ".txtpb", What = "flight model" },
        };
        foreach (var k in kinds.Values)
        {
            k.Src = ReadList(FindCI(Path.Combine(srcSim, k.Folder), k.List));
            k.Dst = ReadList(FindCI(Path.Combine(dstBase, k.Folder), k.List));
        }
        var written = new Dictionary<string, (string Src, string What)>(StringComparer.OrdinalIgnoreCase);
        string shortSrc = new string(p.Src.Name.Where(char.IsLetterOrDigit).ToArray());
        var lines = new List<string>();

        int Map(Kind k, int idx, bool replaced, string label)
        {
            if (idx < 0 || idx >= k.Src.Count) { if (idx >= 0) p.Problems.Add($"{label}: {k.What} {idx} is not in the source's {k.List}"); return idx; }
            string name = k.Src[idx];
            string? from = FindCI(Path.Combine(srcSim, k.Folder), name + k.Ext);
            int j = k.Dst.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (k.Folder == "Acdata" && IsFlyable(name))
            {
                if (j < 0) p.Problems.Add($"{label}: flight model '{name}' is not in the destination and is not copied (flyable aircraft: BMS's own, as the developers ask)");
                else if (j != idx) lines.Add($"  {label}: flight model '{name}' → {j} (the destination's own)");
                return j >= 0 ? j : idx;
            }
            if (from == null) { p.Problems.Add($"{label}: {k.What} file {name}{k.Ext} not found in {Path.Combine(srcSim, k.Folder)}"); return j >= 0 ? j : idx; }
            string? there = j >= 0 ? FindCI(Path.Combine(dstBase, k.Folder), name + k.Ext) : null;
            if (j >= 0 && there != null && SameFile(from, there)) { if (j != idx) lines.Add($"  {label}: {k.What} '{name}' → {j} (the same file)"); return j; }
            if (j >= 0 && (replaced || there == null))
            {
                // the record replaces the destination's one: its data file too (backup by the install)
                written[Path.Combine(dstOut, k.Folder, name + k.Ext)] = (from, k.What);
                lines.Add($"  {label}: {k.What} '{name}' of the destination replaced with the source's");
                return j;
            }
            // a new record: its own file, under its own name when the destination has another one with that name
            string nn = name;
            if (j >= 0) { nn = $"{name}_{shortSrc}"; for (int n = 2; k.Dst.Contains(nn, StringComparer.OrdinalIgnoreCase); n++) nn = $"{name}_{shortSrc}{n}"; }
            k.Dst.Add(nn); k.Changed = true;
            written[Path.Combine(dstOut, k.Folder, nn + k.Ext)] = (from, k.What);
            lines.Add($"  {label}: {k.What} '{name}' copied as '{nn}', number {k.Dst.Count - 1} of {k.List}");
            return k.Dst.Count - 1;
        }

        for (int i = 0; i < p.Records.Count; i++)
        {
            var (t, n, f) = p.Records[i];
            string? field = null; Kind? k = null;
            int Int(string fld) => f.FirstOrDefault(x => x.Field == fld).Value is { } v && int.TryParse(v.Trim(), NumberStyles.Integer, Ci, out int r) ? r : -1;
            string Name() => f.FirstOrDefault(x => x.Field is "Name" or "WpnName").Value?.Trim() ?? "";
            switch (t)
            {
                case "SWD": if (WeaponList(Int("WpnClass")) is { } wl) { k = kinds[wl]; field = "WpnDatIdx"; } break;
                case "RCD": k = kinds["Radar"]; field = "RadarDatIdx"; break;
                case "ACD": k = kinds["Acdata"]; field = "AirframeDatIdx"; break;
            }
            if (k == null || field == null) continue;
            int idx = Int(field);
            int ni = Map(k, idx, D.Get(t, n) != null, $"{t} {n}{(Name().Length > 0 ? $" '{Name()}'" : "")}");
            if (ni != idx) p.Records[i] = (t, n, DbOps.With(f, (field, ni.ToString(Ci))));
        }

        bool any = written.Count > 0 || kinds.Values.Any(k => k.Changed);
        if (!any) { p.Report.Add("Sim files: the destination already has the same ones"); p.Report.AddRange(lines); return; }
        if (!own)
        {
            if (TheaterSim.Problem(p.Dst) is { } why) { p.Problems.Add("Sim files not copied: " + why); return; }
            // the destination gets its own Sim: a copy of BMS's, then simdatadir in its .tdf
            int c = 0;
            foreach (var f in Directory.EnumerateFiles(bmsSim, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dstOut, Path.GetRelativePath(bmsSim, f));
                if (!written.ContainsKey(to) && !File.Exists(to)) { p.Files[to] = (f, null); c++; }
            }
            string rel = Path.GetRelativePath(p.Dst.DataDir, dstOut).Replace('/', '\\');
            if (Dds.TheaterSimData.SetSimDataDir(File.ReadAllText(p.Dst.Tdf), rel) is { } tdf) p.Files[p.Dst.Tdf] = (null, tdf);
            p.Report.Add($"Sim files: {p.Dst.Name} uses BMS's own Data\\Sim: it gets its own Sim folder {dstOut} ({c} files copied from BMS) and 'simdatadir {rel}' in its .tdf");
        }
        foreach (var (to, (from, _)) in written) p.Files[to] = (from, null);
        foreach (var k in kinds.Values.Where(k => k.Changed))
            p.Files[Path.Combine(dstOut, k.Folder, k.List)] = (null, k.Dst.Count.ToString(Ci) + "\r\n" + string.Join("\r\n", k.Dst) + "\r\n");
        p.Report.Add($"Sim files: {written.Count} file(s) into {dstOut}" + (kinds.Values.Any(k => k.Changed) ? "; lists: " + string.Join(", ", kinds.Values.Where(k => k.Changed).Select(k => k.List)) : ""));
        p.Report.AddRange(lines);
    }

    private static bool IsFlyable(string name) => SimLibrary.IsFlyable(name);
}
