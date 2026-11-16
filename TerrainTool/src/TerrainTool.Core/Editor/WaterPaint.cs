using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Una pennellata d'acqua dell'editor: Add = acqua interna (25 nella Watermap) dove c'e' terra, Remove = terra (0) dove c'e'
/// acqua (interna o mare). Diametro in metri; punti in piedi del teatro (x nord, y est, come gli obiettivi).
/// </summary>
public sealed record WaterStroke(bool Add, double DiameterM, List<(double X, double Y)> Points);

/// <summary>
/// Projects\&lt;progetto&gt;\water_paint.csv: l'acqua aggiunta o tolta a mano (fiumi che OSM non ha come superficie, pezzi
/// mancanti...). Non tocca i dati scaricati: si applica alla Watermap (Output\TB_Input\Watermap.raw) prima del Water DEM e
/// alla fine del passo Watermap. La Watermap senza pennellate si tiene in Work\Watermap_unpainted.raw, cosi' le pennellate
/// tolte o cambiate non restano nella Watermap.
/// </summary>
public static class WaterPaint
{
    public const string FileName = "water_paint.csv";
    public const byte Inland = 25, Sea = 45, Land = 0;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);
    public static string UnpaintedPath(string projectFolder) => Path.Combine(projectFolder, "Work", "Watermap_unpainted.raw");

    public static List<WaterStroke> Load(string projectFolder)
    {
        var res = new List<WaterStroke>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 3 || !double.TryParse(p[1], NumberStyles.Float, Ci, out double d)) continue;
            var pts = new List<(double, double)>();
            foreach (var xy in p[2].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var q = xy.Split(',');
                if (q.Length == 2 && double.TryParse(q[0], NumberStyles.Float, Ci, out double x) && double.TryParse(q[1], NumberStyles.Float, Ci, out double y)) pts.Add((x, y));
            }
            if (pts.Count > 0) res.Add(new WaterStroke(p[0].Trim().Equals("add", StringComparison.OrdinalIgnoreCase), d, pts));
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<WaterStroke> strokes)
    {
        var sb = new StringBuilder("Mode;Diameter (m);Points (north,east in theater feet)\r\n");
        foreach (var s in strokes)
            sb.Append(s.Add ? "add" : "remove").Append(';').Append(s.DiameterM.ToString("0.#", Ci)).Append(';')
              .Append(string.Join("|", s.Points.Select(q => q.X.ToString("0", Ci) + "," + q.Y.ToString("0", Ci)))).Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Watermap con le pennellate: riparte da Work\Watermap_unpainted.raw (creata la prima volta dalla Watermap attuale, o
    /// aggiornata quando il passo Watermap ne ha scritta una nuova) e ci applica le pennellate, nell'ordine.
    /// Restituisce una riga per il registro, o null se non c'e' niente da fare.
    /// </summary>
    public static string? ApplyToWatermap(string projectFolder, string watermapPath, double sizeMeters, bool watermapIsFresh = false)
    {
        var strokes = Load(projectFolder);
        string un = UnpaintedPath(projectFolder);
        if (!File.Exists(watermapPath)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(un)!);
        // Watermap appena rifatta dal passo Watermap (o nessuna copia ancora): quella e' la base senza pennellate
        if (watermapIsFresh || !File.Exists(un) || new FileInfo(un).Length != new FileInfo(watermapPath).Length) File.Copy(watermapPath, un, true);
        if (strokes.Count == 0)
        {
            // pennellate tolte tutte: si torna alla Watermap senza pennellate
            if (!watermapIsFresh && FilesDiffer(un, watermapPath)) { File.Copy(un, watermapPath, true); return "Water paint: no strokes, Watermap put back as made by the Watermap step"; }
            return null;
        }
        long len = new FileInfo(un).Length;
        int size = (int)Math.Round(Math.Sqrt(len));
        if ((long)size * size != len) throw new InvalidDataException("Watermap is not a square 8-bit RAW");
        byte[] w = File.ReadAllBytes(un);
        long added = 0, removed = 0;
        double pxM = sizeMeters / size, sizeFt = sizeMeters / 0.3048;
        foreach (var s in strokes)
        {
            double r = Math.Max(0.5, s.DiameterM / 2 / pxM);   // raggio in pixel
            // pixel (colonna = est, riga = da nord)
            var pts = s.Points.Select(q => (C: q.Y * 0.3048 / pxM, R: (sizeFt - q.X) * 0.3048 / pxM)).ToList();
            if (pts.Count == 1) pts.Add(pts[0]);
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                var (c0, r0) = pts[k]; var (c1, r1) = pts[k + 1];
                int xa = (int)Math.Floor(Math.Min(c0, c1) - r), xb = (int)Math.Ceiling(Math.Max(c0, c1) + r);
                int ya = (int)Math.Floor(Math.Min(r0, r1) - r), yb = (int)Math.Ceiling(Math.Max(r0, r1) + r);
                xa = Math.Max(0, xa); ya = Math.Max(0, ya); xb = Math.Min(size - 1, xb); yb = Math.Min(size - 1, yb);
                double dx = c1 - c0, dy = r1 - r0, l2 = dx * dx + dy * dy;
                for (int y = ya; y <= yb; y++)
                    for (int x = xa; x <= xb; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double t = l2 < 1e-9 ? 0 : Math.Clamp(((px - c0) * dx + (py - r0) * dy) / l2, 0, 1);
                        double ex = px - (c0 + t * dx), ey = py - (r0 + t * dy);
                        if (ex * ex + ey * ey > r * r) continue;
                        long i = (long)y * size + x;
                        if (s.Add) { if (w[i] == Land) { w[i] = Inland; added++; } }
                        else if (w[i] != Land) { w[i] = Land; removed++; }
                    }
            }
        }
        File.WriteAllBytes(watermapPath, w);
        double km2 = pxM * pxM / 1e6;
        return $"Water paint ({strokes.Count} strokes from {FileName}): {added:N0} pixels of inland water added ({added * km2:0.#} km²), {removed:N0} pixels of water removed ({removed * km2:0.#} km²), {pxM:0.#} m per pixel";
    }

    private static bool FilesDiffer(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return true;
        using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
        var ba = new byte[1 << 20]; var bb = new byte[1 << 20];
        while (true)
        {
            int na = sa.Read(ba, 0, ba.Length), nb = sb.Read(bb, 0, bb.Length);
            if (na != nb) return true;
            if (na == 0) return false;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return true;
        }
    }
}
