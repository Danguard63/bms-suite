using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.NewTerrain;

/// <summary>Perche' i pezzi di un obiettivo stanno sott'acqua nel terreno finito.</summary>
public enum CoastCause { None, WatermapWater, DemAtWaterLevel, ShoreSmoothing, WaterLevel }

/// <summary>Una riga di coast_check.csv: un obiettivo con pezzi sott'acqua e la causa piu' frequente tra i suoi pezzi.</summary>
public sealed record CoastCheckRow(int CampId, string Name, string Type, int Pieces, int InWater, int WatermapWater, int DemLow, int Shore, int Level,
    double MinDemFt, double MaxDemFt, double MinGroundFt, double MaxWaterFt, double X, double Y, bool OriginalPlace, CoastCause Cause)
{
    public static string CauseText(CoastCause c) => c switch
    {
        CoastCause.WatermapWater => "Watermap: water (the land is missing in the water map, or the objective is offshore)",
        CoastCause.DemAtWaterLevel => "DEM at water level (the land is too low)",
        CoastCause.ShoreSmoothing => "shore smoothing (land lowered next to the water)",
        CoastCause.WaterLevel => "water level above the land (250 m water map)",
        _ => "",
    };
}

/// <summary>
/// Controllo delle coste e delle isole del terreno finito: per ogni obiettivo (tranne ponti e dighe, che stanno sull'acqua per natura)
/// conta i pezzi che BMS disegna sott'acqua (pelo dell'acqua della WaterHeightMap sopra la HeightMap del NewTerrain) e per ognuno
/// cerca la causa nelle mappe di partenza: la Watermap (terra/acqua di OSM), il DEM originale, il terreno finale (dopo il raccordo
/// con la riva) e il pelo dell'acqua. Gli obiettivi spostati da Run Objects si controllano nella posizione dell'add-on.
/// Legge soltanto; scrive coast_check.csv nella cartella del progetto.
/// </summary>
public static class CoastCheck
{
    public const string FileName = "coast_check.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly Regex NoCheckName = new(@"\bdam\b|\bdiga\b", RegexOptions.IgnoreCase);

    /// <param name="demPath">DEM a 16 bit in piedi (quello dato al Water DEM)</param>
    /// <param name="watermapPath">Watermap a 8 bit (0 terra, 25 acque interne, 45 mare)</param>
    public static (List<CoastCheckRow> Rows, List<string> Summary) Run(TheaterProject p, string demPath, string watermapPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var summary = new List<string>();
        var d = new EditorData(p);
        using var probe = new WaterProbe(p);
        if (!probe.HasWater) throw new InvalidOperationException("No NewTerrain WaterHeightMap: build the terrain first");
        var s = new TerrainSampler(p);
        double sizeFt = s.SizeFt;
        using var dem = OpenSquare(demPath, 2, out int demSide);
        using var wm = OpenSquare(watermapPath, 1, out int wmSide);
        if (dem == null) summary.Add($"DEM not found or not a square 16-bit RAW ({demPath}): DEM heights not checked");
        if (wm == null) summary.Add($"Watermap not found or not a square 8-bit RAW ({watermapPath}): land/water of the map not checked");
        var moves = CoastalPlan.Load(p.ProjectFolder);

        short Dem(double n, double e)
        {
            if (dem == null) return short.MinValue;
            var (r, c) = Px(n, e, demSide);
            var b = new byte[2];
            dem.Position = ((long)r * demSide + c) * 2;
            dem.ReadExactly(b);
            return BitConverter.ToInt16(b, 0);
        }
        int Wm(double n, double e)
        {
            if (wm == null) return -1;
            var (r, c) = Px(n, e, wmSide);
            wm.Position = (long)r * wmSide + c;
            return wm.ReadByte();
        }
        (int R, int C) Px(double n, double e, int side) =>
            (Math.Clamp((int)((sizeFt - n) / sizeFt * side), 0, side - 1), Math.Clamp((int)(e / sizeFt * side), 0, side - 1));

        var rows = new List<CoastCheckRow>();
        int total = d.Objectives.Count, done = 0, checkedN = 0;
        foreach (var o in d.Objectives)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 50 == 0) progress?.Report((double)done / total);
            if (o.Junction || o.Type == 6 || NoCheckName.IsMatch(o.Name)) continue;
            var lay = d.LayoutOf(o);
            if (lay.Features.Count == 0) continue;
            checkedN++;
            // nella posizione dell'add-on se Run Objects l'ha spostato verso terra
            bool orig = moves.TryGetValue(o.Id, out var mv) && mv.Moved;
            var at = orig ? new EdObjective { Id = o.Id, Name = o.Name, X = mv!.OrigX, Y = mv.OrigY, Heading = o.Heading } : o;
            int wet = 0, wW = 0, wDem = 0, wShore = 0, wLevel = 0;
            double minDem = double.MaxValue, maxDem = double.MinValue, minG = double.MaxValue, maxW = double.MinValue;
            foreach (var f in lay.Features)
            {
                if (f.Z > 100) continue;   // pezzi sepolti
                var (n, e) = EditorData.ToWorld(at, f.N, f.E);
                if (n < 0 || e < 0 || n >= sizeFt || e >= sizeFt || !probe.IsWater(n, e)) continue;
                wet++;
                double g = probe.Ground(n, e), w = s.WaterAt(n, e) ?? g;
                short dv = Dem(n, e);
                int wv = Wm(n, e);
                minG = Math.Min(minG, g); maxW = Math.Max(maxW, w);
                if (dv != short.MinValue) { minDem = Math.Min(minDem, dv); maxDem = Math.Max(maxDem, dv); }
                if (wv > 0) wW++;
                else if (dv != short.MinValue && dv <= w + 0.5) wDem++;
                else if (dv != short.MinValue && g < dv - 0.5) wShore++;
                else wLevel++;
            }
            if (wet == 0) continue;
            var counts = new[] { (CoastCause.WatermapWater, wW), (CoastCause.DemAtWaterLevel, wDem), (CoastCause.ShoreSmoothing, wShore), (CoastCause.WaterLevel, wLevel) };
            var cause = counts.OrderByDescending(x => x.Item2).First().Item1;
            rows.Add(new CoastCheckRow(o.Id, o.Name, EditorData.TypeName(o.Type), lay.Features.Count, wet, wW, wDem, wShore, wLevel,
                minDem == double.MaxValue ? double.NaN : minDem, maxDem == double.MinValue ? double.NaN : maxDem, minG, maxW,
                at.X, at.Y, orig, cause));
        }
        progress?.Report(1);

        rows.Sort((a, b) => (b.InWater * 1.0 / b.Pieces).CompareTo(a.InWater * 1.0 / a.Pieces));
        int half = rows.Count(r => r.InWater * 4 > r.Pieces);
        summary.Add($"{checkedN} objectives checked (no bridges, dams or junctions), {rows.Count} with at least one piece under water, {half} with more than a quarter");
        foreach (CoastCause c in new[] { CoastCause.WatermapWater, CoastCause.DemAtWaterLevel, CoastCause.ShoreSmoothing, CoastCause.WaterLevel })
        {
            var g = rows.Where(r => r.Cause == c && r.InWater * 4 > r.Pieces).ToList();
            if (g.Count > 0) summary.Add($"  {g.Count,4} × {CoastCheckRow.CauseText(c)}");
        }
        Save(Path.Combine(p.ProjectFolder, FileName), rows);
        summary.Add("List: " + Path.Combine(p.ProjectFolder, FileName));
        return (rows, summary);
    }

    private static FileStream? OpenSquare(string path, int bytes, out int side)
    {
        side = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        long px = new FileInfo(path).Length / bytes;
        int sd = (int)Math.Round(Math.Sqrt(px));
        if ((long)sd * sd != px) return null;
        side = sd;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
    }

    private static void Save(string path, List<CoastCheckRow> rows)
    {
        var sb = new StringBuilder("CampId;Name;Type;Pieces;Pieces under water;Watermap water;DEM at water level;Shore smoothing;Water level;" +
                                   "Min DEM (ft);Max DEM (ft);Min ground (ft);Max water level (ft);X (ft);Y (ft);Place;Main cause\r\n");
        string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.0", Ci);
        foreach (var r in rows)
            sb.Append(r.CampId).Append(';').Append(r.Name.Replace(';', ',')).Append(';').Append(r.Type).Append(';').Append(r.Pieces).Append(';')
              .Append(r.InWater).Append(';').Append(r.WatermapWater).Append(';').Append(r.DemLow).Append(';').Append(r.Shore).Append(';').Append(r.Level).Append(';')
              .Append(F(r.MinDemFt)).Append(';').Append(F(r.MaxDemFt)).Append(';').Append(F(r.MinGroundFt)).Append(';').Append(F(r.MaxWaterFt)).Append(';')
              .Append(r.X.ToString("0.0", Ci)).Append(';').Append(r.Y.ToString("0.0", Ci)).Append(';').Append(r.OriginalPlace ? "add-on place (moved by Run Objects)" : "current")
              .Append(';').Append(CoastCheckRow.CauseText(r.Cause)).Append("\r\n");
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
