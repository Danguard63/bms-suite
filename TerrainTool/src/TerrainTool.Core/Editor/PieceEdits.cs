using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Un pezzo della disposizione di un obiettivo spostato, girato o tolto a mano nell'editor degli obiettivi.
/// N/E/Heading sono nella disposizione (piedi, nord/est dell'obiettivo prima della sua rotazione; come OffsetY/OffsetX del FED),
/// assoluti, non spostamenti: rifare Run Objects non li somma due volte. Orig* = com'era il pezzo la prima volta che e' stato toccato.
/// Tolto = sepolto (OffsetZ 2000, come le piste vecchie): il numero dei pezzi non cambia, quindi i file della campagna restano validi.
/// </summary>
public sealed record PieceEdit(int CampId, int Num, int CtIdx, double N, double E, double Heading, bool Removed,
    double OrigN, double OrigE, double OrigHeading)
{
    public bool IsNoOp => !Removed && Math.Abs(N - OrigN) < 0.01 && Math.Abs(E - OrigE) < 0.01 && Math.Abs(Heading - OrigHeading) < 0.001;
}

/// <summary>Projects\&lt;progetto&gt;\piece_edits.csv: i pezzi toccati a mano, per obiettivo (CampId) e numero del pezzo (FED Num).</summary>
public static class PieceEdits
{
    public const string FileName = "piece_edits.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    /// <summary>CampId → (numero del pezzo → modifica).</summary>
    public static Dictionary<int, Dictionary<int, PieceEdit>> Load(string projectFolder)
    {
        var res = new Dictionary<int, Dictionary<int, PieceEdit>>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 10) continue;
            bool I(string s, out int v) => int.TryParse(s, NumberStyles.Integer, Ci, out v);
            bool D(string s, out double v) => double.TryParse(s, NumberStyles.Float, Ci, out v);
            if (!I(p[0], out int id) || !I(p[1], out int num) || !I(p[2], out int ct)) continue;
            if (!D(p[3], out double n) || !D(p[4], out double e) || !D(p[5], out double h)) continue;
            if (!D(p[7], out double on) || !D(p[8], out double oe) || !D(p[9], out double oh)) continue;
            if (!res.TryGetValue(id, out var d)) res[id] = d = new Dictionary<int, PieceEdit>();
            d[num] = new PieceEdit(id, num, ct, n, e, h, p[6].Trim() == "1", on, oe, oh);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<PieceEdit> edits)
    {
        var sb = new StringBuilder("CampId;Piece;CtIdx;North (ft);East (ft);Heading;Removed;Original north;Original east;Original heading\r\n");
        foreach (var e in edits.Where(e => !e.IsNoOp).OrderBy(e => e.CampId).ThenBy(e => e.Num))
            sb.Append(e.CampId.ToString(Ci)).Append(';').Append(e.Num.ToString(Ci)).Append(';').Append(e.CtIdx.ToString(Ci)).Append(';')
              .Append(e.N.ToString("0.000", Ci)).Append(';').Append(e.E.ToString("0.000", Ci)).Append(';').Append(e.Heading.ToString("0.000", Ci)).Append(';')
              .Append(e.Removed ? "1" : "0").Append(';')
              .Append(e.OrigN.ToString("0.000", Ci)).Append(';').Append(e.OrigE.ToString("0.000", Ci)).Append(';').Append(e.OrigHeading.ToString("0.000", Ci))
              .Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>
/// Un punto della disposizione (PDX: dove BMS mette un mezzo dell'obiettivo) spostato a mano. N/E/Heading nella disposizione,
/// assoluti; Orig* = com'era la prima volta. Il numero dei punti non cambia mai (le liste PHD li contano).
/// </summary>
public sealed record PointEdit(int CampId, int Num, int Type, double N, double E, double Heading, double OrigN, double OrigE, double OrigHeading)
{
    public bool IsNoOp => Math.Abs(N - OrigN) < 0.01 && Math.Abs(E - OrigE) < 0.01 && Math.Abs(Heading - OrigHeading) < 0.001;
}

/// <summary>Projects\&lt;progetto&gt;\point_edits.csv: i punti spostati a mano, per obiettivo (CampId) e numero del punto (PD Num).</summary>
public static class PointEdits
{
    public const string FileName = "point_edits.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    public static Dictionary<int, Dictionary<int, PointEdit>> Load(string projectFolder)
    {
        var res = new Dictionary<int, Dictionary<int, PointEdit>>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 9) continue;
            bool I(string s, out int v) => int.TryParse(s, NumberStyles.Integer, Ci, out v);
            bool D(string s, out double v) => double.TryParse(s, NumberStyles.Float, Ci, out v);
            if (!I(p[0], out int id) || !I(p[1], out int num) || !I(p[2], out int t)) continue;
            if (!D(p[3], out double n) || !D(p[4], out double e) || !D(p[5], out double h)) continue;
            if (!D(p[6], out double on) || !D(p[7], out double oe) || !D(p[8], out double oh)) continue;
            if (!res.TryGetValue(id, out var d)) res[id] = d = new Dictionary<int, PointEdit>();
            d[num] = new PointEdit(id, num, t, n, e, h, on, oe, oh);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<PointEdit> edits)
    {
        var sb = new StringBuilder("CampId;Point;Type;North (ft);East (ft);Heading;Original north;Original east;Original heading\r\n");
        foreach (var e in edits.Where(e => !e.IsNoOp).OrderBy(e => e.CampId).ThenBy(e => e.Num))
            sb.Append(e.CampId.ToString(Ci)).Append(';').Append(e.Num.ToString(Ci)).Append(';').Append(e.Type.ToString(Ci)).Append(';')
              .Append(e.N.ToString("0.000", Ci)).Append(';').Append(e.E.ToString("0.000", Ci)).Append(';').Append(e.Heading.ToString("0.000", Ci)).Append(';')
              .Append(e.OrigN.ToString("0.000", Ci)).Append(';').Append(e.OrigE.ToString("0.000", Ci)).Append(';').Append(e.OrigHeading.ToString("0.000", Ci))
              .Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }
}
