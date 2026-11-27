using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Database;

/// <summary>
/// Numbering check of the Falcon4_*.xml tables of a theater, as BMS 4.38 loads them (LoadAcdXml, LoadVcdXml... and the CT
/// loader in InitClassTableAndDataXml): the table gets (Num of the LAST record) + 1 slots and every record goes to the slot of its
/// own Num. So:
/// - a missing number is a slot nothing fills: an empty record in VCD, WCD, FCD, RCD, ICD, RWD, VSD, a zeroed one in UCD, and
///   uninitialized memory (random values) in CT, ACD, SWD, RKT, DDP, FED, PHD, PDX (WLD and SSD appear to be the same);
/// - if the last record is not the highest number, the records above it are written past the end of the table;
/// - a number used twice keeps only the record that comes later in the file.
/// BMS's own stock tables are numbered 0..last with no gaps and in order.
/// </summary>
public static class XmlNumbering
{
    public sealed record Issue(string File, string Table, string Text, bool Serious);

    /// <summary>Tables whose missing slots are left as uninitialized memory by BMS (WLD and SSD: same as far as seen).</summary>
    public static readonly HashSet<string> Uninitialized = new(StringComparer.OrdinalIgnoreCase)
        { "CT", "ACD", "SWD", "RKT", "DDP", "FED", "PHD", "PDX", "WLD", "SSD" };

    private static readonly EnumerationOptions NoCase = new() { MatchCasing = MatchCasing.CaseInsensitive };

    /// <summary>Checks every Falcon4_*.xml of the folder; empty list = all numbered 0..last, in order, no duplicates.</summary>
    public static List<Issue> Check(string objectsDir)
    {
        var res = new List<Issue>();
        if (!Directory.Exists(objectsDir)) return res;
        foreach (var f in Directory.EnumerateFiles(objectsDir, "Falcon4_*.xml", NoCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string code = Path.GetFileNameWithoutExtension(f)["Falcon4_".Length..].ToUpperInvariant();
            string text;
            try { text = File.ReadAllText(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            res.AddRange(CheckText(Path.GetFileName(f), code, text));
        }
        return res;
    }

    /// <summary>Check of one table's text (records &lt;CODE Num="n"&gt; in file order).</summary>
    public static List<Issue> CheckText(string file, string code, string xml)
    {
        var res = new List<Issue>();
        var nums = Regex.Matches(xml, $@"<{Regex.Escape(code)}\s+Num=""(-?\d+)""", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) return res;
        int last = nums[^1], max = nums.Max();
        bool bad = Uninitialized.Contains(code);

        if (nums.Any(n => n < 0))
            res.Add(new(file, code, $"negative record number {nums.First(n => n < 0)}: BMS cannot place it", true));
        if (last != max)
            res.Add(new(file, code, $"the last record is {last} but {max} is higher: BMS sizes the table from the last record " +
                                    $"({last + 1} slots), so the records above {last} are written past its end", true));
        var dup = nums.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n).ToList();
        if (dup.Count > 0)
            res.Add(new(file, code, $"number{(dup.Count == 1 ? "" : "s")} used twice: {Ranges(dup)} (BMS keeps the later record)", false));
        var have = nums.ToHashSet();
        var gaps = Enumerable.Range(0, Math.Max(0, max + 1)).Where(n => !have.Contains(n)).ToList();
        if (gaps.Count > 0)
            res.Add(new(file, code, $"{gaps.Count} missing number{(gaps.Count == 1 ? "" : "s")}: {Ranges(gaps)} — " +
                                    (bad ? "BMS leaves these slots as uninitialized memory (random values): fill them or renumber"
                                         : "BMS leaves these slots as empty records"), bad));
        return res;
    }

    /// <summary>"3, 7-12, 40" from a sorted list (at most 12 pieces, then "...").</summary>
    public static string Ranges(IReadOnlyList<int> sorted)
    {
        var sb = new StringBuilder();
        int pieces = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            int a = sorted[i], b = a;
            while (i + 1 < sorted.Count && sorted[i + 1] == b + 1) b = sorted[++i];
            if (pieces == 12) { sb.Append(", ..."); break; }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(a == b ? a.ToString(CultureInfo.InvariantCulture) : $"{a}-{b}");
            pieces++;
        }
        return sb.ToString();
    }
}
