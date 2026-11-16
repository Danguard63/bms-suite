using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Obiettivi girati la cui rotazione il lavoro Objects ha messo dentro la disposizione (in BMS la direzione dell'obiettivo diventa 0).
/// In Output\EditorLayouts restano le disposizioni come prima (non girate) e baked.csv (CampId;Ocd;Heading): l'editor degli obiettivi
/// le usa con la direzione vera, cosi' le modifiche a mano restano nello stesso riferimento del lavoro Objects.
/// Nelle disposizioni OffsetX = est, OffsetY = nord; direzioni in gradi, in senso orario.
/// </summary>
public static class BakedLayouts
{
    public const string CsvName = "baked.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string Folder(string projectFolder) => Path.Combine(projectFolder, "Output", "EditorLayouts");

    /// <summary>CampId → (OCD, direzione vera) dell'ultimo lavoro Objects; vuoto se non c'e'.</summary>
    public static Dictionary<int, (int Ocd, double Heading)> Load(string projectFolder)
    {
        var res = new Dictionary<int, (int, double)>();
        string f = Path.Combine(Folder(projectFolder), CsvName);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var p = l.Split(';');
            if (p.Length >= 3 && int.TryParse(p[0], NumberStyles.Integer, Ci, out int id) && int.TryParse(p[1], NumberStyles.Integer, Ci, out int ocd)
                && double.TryParse(p[2], NumberStyles.Float, Ci, out double h)) res[id] = (ocd, h);
        }
        return res;
    }

    public const string ShiftCsvName = "bridge_cells.csv";

    /// <summary>
    /// Ponti il cui punto (centro dell'obiettivo) il lavoro Objects ha spostato nel km del fiume da attraversare, lasciando fermi i pezzi
    /// (la disposizione e' spostata al contrario): CampId → (OCD, spostamento nord, est in piedi). L'editor li mostra nel punto di prima.
    /// </summary>
    public static Dictionary<int, (int Ocd, double DN, double DE)> LoadShifts(string projectFolder)
    {
        var res = new Dictionary<int, (int, double, double)>();
        string f = Path.Combine(Folder(projectFolder), ShiftCsvName);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var p = l.Split(';');
            if (p.Length >= 4 && int.TryParse(p[0], NumberStyles.Integer, Ci, out int id) && int.TryParse(p[1], NumberStyles.Integer, Ci, out int ocd)
                && double.TryParse(p[2], NumberStyles.Float, Ci, out double dn) && double.TryParse(p[3], NumberStyles.Float, Ci, out double de)) res[id] = (ocd, dn, de);
        }
        return res;
    }

    /// <summary>Record FED o PD: posizione meno (dn, de) piedi nel riferimento della disposizione (OffsetY = nord, OffsetX = est).</summary>
    public static string TranslateRecords(string xml, string tag, double dn, double de) =>
        Regex.Replace(xml, $@"(<{tag} Num=""\d+"">)(.*?)(</{tag}>)", m =>
        {
            string b = m.Groups[2].Value;
            var x = Regex.Match(b, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(b, @"<OffsetY>(.*?)</OffsetY>");
            if (!x.Success || !y.Success) return m.Value;
            double e = double.Parse(x.Groups[1].Value, Ci) - de, n = double.Parse(y.Groups[1].Value, Ci) - dn;
            b = b.Replace(x.Value, $"<OffsetX>{F(e)}</OffsetX>").Replace(y.Value, $"<OffsetY>{F(n)}</OffsetY>");
            return m.Groups[1].Value + b + m.Groups[3].Value;
        }, RegexOptions.Singleline);

    private static double Norm(double h) => ((h % 360) + 360) % 360;
    private static string F(double v) => v.ToString("0.000", Ci);

    /// <summary>Record FED o PD: posizione girata attorno al centro dell'obiettivo, direzione + deg (addHeading: la aggiunge se manca).</summary>
    public static string RotateRecords(string xml, string tag, double deg, bool addHeading)
    {
        double r = deg * Math.PI / 180, ch = Math.Cos(r), sh = Math.Sin(r);
        return Regex.Replace(xml, $@"(<{tag} Num=""\d+"">)(.*?)(</{tag}>)", m =>
        {
            string b = m.Groups[2].Value;
            var x = Regex.Match(b, @"<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(b, @"<OffsetY>(.*?)</OffsetY>");
            if (!x.Success || !y.Success) return m.Value;
            double e = double.Parse(x.Groups[1].Value, Ci), n = double.Parse(y.Groups[1].Value, Ci);
            double n2 = n * ch - e * sh, e2 = n * sh + e * ch;
            b = b.Replace(x.Value, $"<OffsetX>{F(e2)}</OffsetX>").Replace(y.Value, $"<OffsetY>{F(n2)}</OffsetY>");
            var h = Regex.Match(b, @"<Heading>(.*?)</Heading>");
            if (h.Success) b = b.Replace(h.Value, $"<Heading>{F(Norm(double.Parse(h.Groups[1].Value, Ci) + deg))}</Heading>");
            else if (addHeading)
            {
                var z = Regex.Match(b, @"<OffsetZ>.*?</OffsetZ>");
                string ins = $"<Heading>{F(Norm(deg))}</Heading>";
                b = z.Success ? b.Insert(z.Index + z.Length, "\r\n        " + ins) : b.TrimEnd() + "\r\n        " + ins + "\r\n    ";
            }
            return m.Groups[1].Value + b + m.Groups[3].Value;
        }, RegexOptions.Singleline);
    }

    /// <summary>PHD: direzione delle piste (tipo 1 e 8) e dei moli grandi (tipo 14, se indicata) + deg.</summary>
    public static string RotateRunwayHeadings(string xml, double deg) =>
        Regex.Replace(xml, @"(<PHD Num=""\d+"">)(.*?)(</PHD>)", m =>
        {
            string b = m.Groups[2].Value;
            string t = Regex.Match(b, @"<Type>(\d+)</Type>").Groups[1].Value;
            var d = Regex.Match(b, @"<Data>(.*?)</Data>");
            if (!d.Success || !double.TryParse(d.Groups[1].Value, NumberStyles.Float, Ci, out double v)) return m.Value;
            if (!(t is "1" or "8" || t == "14" && Math.Abs(v) > 0.001)) return m.Value;
            double nv = Norm(v + deg);
            if (t is "1" or "8" && nv < 0.0005) nv = 360;   // come la Korea: le piste verso nord hanno 360
            return m.Groups[1].Value + b.Replace(d.Value, $"<Data>{F(nv)}</Data>") + m.Groups[3].Value;
        }, RegexOptions.Singleline);
}
