using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Modifiche fatte a mano nell'editor degli obiettivi, salvate in Projects\&lt;progetto&gt;\objective_edits.csv:
/// posizione assoluta (piedi, X nord / Y est come CampObjData.XML), direzione in gradi e "diventa incrocio".
/// Il lavoro Objects le applica per ultime, sopra gli spostamenti automatici.
/// </summary>
public sealed record ObjectiveEdit(int CampId, string Name, double X, double Y, double Heading, bool Junction);

public static class ObjectiveEdits
{
    public const string FileName = "objective_edits.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    public static Dictionary<int, ObjectiveEdit> Load(string projectFolder)
    {
        var res = new Dictionary<int, ObjectiveEdit>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 6 || !int.TryParse(p[0], NumberStyles.Integer, Ci, out int id)) continue;
            if (!double.TryParse(p[2], NumberStyles.Float, Ci, out double x) || !double.TryParse(p[3], NumberStyles.Float, Ci, out double y)
                || !double.TryParse(p[4], NumberStyles.Float, Ci, out double h)) continue;
            res[id] = new ObjectiveEdit(id, p[1], x, y, h, p[5].Trim() == "1");
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<ObjectiveEdit> edits)
    {
        var sb = new StringBuilder("CampId;Name;PositionX;PositionY;Heading;Junction\r\n");
        foreach (var e in edits.OrderBy(e => e.CampId))
            sb.Append(e.CampId.ToString(Ci)).Append(';').Append(e.Name.Replace(';', ',')).Append(';')
              .Append(e.X.ToString("0.000", Ci)).Append(';').Append(e.Y.ToString("0.000", Ci)).Append(';')
              .Append(e.Heading.ToString("0.000", Ci)).Append(';').Append(e.Junction ? "1" : "0").Append("\r\n");
        string f = PathFor(projectFolder);
        // copia della versione precedente, per sicurezza
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }
}
