using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>Scelta per un obiettivo con gli edifici in acqua: spostarlo verso terra (MOVE) o lasciarlo dov'era nell'add-on (KEEP).</summary>
public enum CoastalDecision { None, Move, Keep }

/// <summary>
/// Un obiettivo che Run Objects ha trovato con gli edifici in acqua (SnapToLand). Posizioni in piedi (X nord, Y est).
/// WaterBefore/After = edifici "di terra" in acqua prima e dopo lo spostamento; per i porti moli, banchine e navi non contano
/// (MarineInWater = quanti di quelli toccano l'acqua nella posizione nuova).
/// </summary>
public sealed record CoastalRow(int CampId, string Name, string Type, int Pieces, int WaterBefore, int WaterAfter, int Marine, int MarineInWater,
    double MoveM, double OrigX, double OrigY, double NewX, double NewY, string Note, CoastalDecision Decision = CoastalDecision.None)
{
    public bool Moved => MoveM > 0;
    /// <summary>Si sposta: c'e' uno spostamento e non e' stato rifiutato (KEEP).</summary>
    public bool Applies => Moved && Decision != CoastalDecision.Keep;
}

/// <summary>coastal_moves.csv del progetto: scritto da Run Objects (le decisioni restano), modificato da "Review coastal moves".</summary>
public static class CoastalPlan
{
    public const string FileName = "coastal_moves.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const string Header = "CampId;Name;Type;Pieces;Land pieces in water before;after;Marine pieces;Marine in water after;Move (m);X (ft);Y (ft);New X (ft);New Y (ft);Note;Decision";

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    public static Dictionary<int, CoastalRow> Load(string projectFolder)
    {
        var res = new Dictionary<int, CoastalRow>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length < 14 || !int.TryParse(c[0], NumberStyles.Integer, Ci, out int id)) continue;
            int I(int i) => int.TryParse(c[i], NumberStyles.Integer, Ci, out var v) ? v : 0;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, Ci, out var v) ? v : 0;
            res[id] = new CoastalRow(id, c[1], c[2], I(3), I(4), I(5), I(6), I(7), D(8), D(9), D(10), D(11), D(12), c[13],
                c.Length > 14 ? Parse(c[14]) : CoastalDecision.None);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<CoastalRow> rows)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var r in rows.OrderByDescending(r => r.MoveM).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.CampId).Append(';').Append(r.Name.Replace(';', ',')).Append(';').Append(r.Type).Append(';').Append(r.Pieces).Append(';')
              .Append(r.WaterBefore).Append(';').Append(r.WaterAfter).Append(';').Append(r.Marine).Append(';').Append(r.MarineInWater).Append(';')
              .Append(r.MoveM.ToString("0", Ci)).Append(';').Append(r.OrigX.ToString("0.0", Ci)).Append(';').Append(r.OrigY.ToString("0.0", Ci)).Append(';')
              .Append(r.NewX.ToString("0.0", Ci)).Append(';').Append(r.NewY.ToString("0.0", Ci)).Append(';').Append(r.Note.Replace(';', ',')).Append(';')
              .Append(Name(r.Decision)).Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
    }

    public static string Name(CoastalDecision d) => d switch { CoastalDecision.Move => "MOVE", CoastalDecision.Keep => "KEEP", _ => "" };

    public static CoastalDecision Parse(string s) => s.Trim().ToUpperInvariant() switch
    {
        "MOVE" or "SPOSTA" => CoastalDecision.Move,
        "KEEP" or "TIENI" or "UNDO" => CoastalDecision.Keep,
        _ => CoastalDecision.None,
    };
}
