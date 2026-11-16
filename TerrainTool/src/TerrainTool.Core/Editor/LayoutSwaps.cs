using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Disposizione (OCD) scelta a mano al posto di quella dell'obiettivo: un ponte piu' lungo per un fiume largo, un porto
/// che diventa citta' perche' e' nell'entroterra... Source = "mideast" (disposizioni dell'add-on, come le ha preparate
/// Run Objects) o "bms" (cartella della 4.38, sola lettura). Type = tipo della disposizione nuova (puo' cambiare quello
/// dell'obiettivo). Nome, posizione, collegamenti e numero dell'obiettivo restano.
/// </summary>
public sealed record LayoutSwap(int CampId, string Name, string Source, int Ocd, string LayoutName, int Type);

/// <summary>Projects\&lt;progetto&gt;\layout_swaps.csv (scelte dell'editor) e Output\layout_swaps_applied.csv (cosa ha fatto Run Objects).</summary>
public static class LayoutSwaps
{
    public const string FileName = "layout_swaps.csv";
    public const string AppliedName = "layout_swaps_applied.csv";
    public const string Mideast = "mideast", Bms = "bms";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);
    public static string AppliedPathFor(string projectFolder) => Path.Combine(projectFolder, "Output", AppliedName);

    public static Dictionary<int, LayoutSwap> Load(string projectFolder)
    {
        var res = new Dictionary<int, LayoutSwap>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 6 || !int.TryParse(p[0], NumberStyles.Integer, Ci, out int id) || !int.TryParse(p[3], NumberStyles.Integer, Ci, out int ocd)) continue;
            int.TryParse(p[5], NumberStyles.Integer, Ci, out int type);
            string src = p[2].Trim().ToLowerInvariant() == Bms ? Bms : Mideast;
            res[id] = new LayoutSwap(id, p[1], src, ocd, p[4], type);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<LayoutSwap> swaps)
    {
        var sb = new StringBuilder("CampId;Name;Source;Ocd;Layout;Type\r\n");
        foreach (var s in swaps.OrderBy(s => s.CampId))
            sb.Append(s.CampId.ToString(Ci)).Append(';').Append(s.Name.Replace(';', ',')).Append(';').Append(s.Source).Append(';')
              .Append(s.Ocd.ToString(Ci)).Append(';').Append(s.LayoutName.Replace(';', ',')).Append(';').Append(s.Type.ToString(Ci)).Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>CampId → (Source, Ocd sorgente, OCD nuovo scritto da Run Objects).</summary>
    public static Dictionary<int, (string Source, int Ocd, int NewOcd)> LoadApplied(string projectFolder)
    {
        var res = new Dictionary<int, (string, int, int)>();
        string f = AppliedPathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length >= 4 && int.TryParse(p[0], NumberStyles.Integer, Ci, out int id) && int.TryParse(p[2], NumberStyles.Integer, Ci, out int o)
                && int.TryParse(p[3], NumberStyles.Integer, Ci, out int n)) res[id] = (p[1], o, n);
        }
        return res;
    }

    public static void SaveApplied(string projectFolder, IEnumerable<(int CampId, string Source, int Ocd, int NewOcd)> rows)
    {
        var sb = new StringBuilder("CampId;Source;Ocd;NewOcd\r\n");
        foreach (var r in rows) sb.Append(r.CampId.ToString(Ci)).Append(';').Append(r.Source).Append(';').Append(r.Ocd.ToString(Ci)).Append(';').Append(r.NewOcd.ToString(Ci)).Append("\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(AppliedPathFor(projectFolder))!);
        File.WriteAllText(AppliedPathFor(projectFolder), sb.ToString(), new UTF8Encoding(false));
    }
}
