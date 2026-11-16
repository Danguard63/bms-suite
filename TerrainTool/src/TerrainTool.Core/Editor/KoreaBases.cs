using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Dds;

namespace TerrainTool.Core.Editor;

/// <summary>Pista di una disposizione: centro (piedi, X nord / Y est), direzione (gradi), lunghezza (piedi), numero.</summary>
public readonly record struct Runway(double X, double Y, double Hdg, double Len, int Num);

/// <summary>
/// Base completa della Korea 4.38 che si puo' mettere al posto di una base del teatro: la sua disposizione principale
/// (quella con le piste e il modello delle vie di rullaggio), letta dalla cartella di BMS in sola lettura.
/// </summary>
public sealed class KoreaBase
{
    public int Ocd { get; init; }
    public string Name { get; init; } = "";
    public List<Runway> Runways { get; init; } = new();
    public int Features { get; init; }
    public int Parking { get; init; }
    /// <summary>Apertura alare massima ammessa dal parcheggio piu' grande (piedi, MaxWidth dei punti), 0 se non indicata.</summary>
    public double MaxWingspanFt { get; init; }
    /// <summary>Ha il modello delle vie di rullaggio (asfalto 3D): base completa.</summary>
    public bool HasTaxiways { get; init; }
    /// <summary>Base militare (AB, Airbase) o aeroporto civile (Intl, Airport).</summary>
    public bool Military { get; init; }
    /// <summary>Pezzi con il numero di pista scritto nel modello: ruotando la base restano sbagliati.</summary>
    public List<string> FixedNumbers { get; init; } = new();
    /// <summary>Altri obiettivi coreani della stessa base (edifici in piu'): non vengono importati.</summary>
    public int ExtraParts { get; init; }
    /// <summary>Disposizione del teatro stesso (anche una importata con Database Interchange), non della Korea 4.38.</summary>
    public bool FromTheater => BaseSwaps.FromTheater(Ocd);
    /// <summary>Numero OCD vero (Ocd e' codificato con <see cref="BaseSwaps.TheaterOffset"/> per le basi del teatro).</summary>
    public int RealOcd => BaseSwaps.RealOcd(Ocd);
    /// <summary>Declinazione con cui sono numerate le piste della disposizione.</summary>
    public double Variation => FromTheater ? KoreaBases.TheaterVariation : KoreaBases.KoreaVariation;

    public string Describe() =>
        $"{Name}  [{(FromTheater ? "theater" : "Korea")} OCD {RealOcd}]  {Runways.Count} rwy {string.Join("/", Runways.Select(r => (r.Len / 3.2808).ToString("0", CultureInfo.InvariantCulture) + " m"))}" +
        $"  {(Military ? "military" : "civil")}  {Features} pieces, {Parking} parking" + (MaxWingspanFt >= 9000 ? " (any wingspan)" : MaxWingspanFt > 0 ? $" (wingspan up to {MaxWingspanFt * 0.3048:0} m)" : "") +
(ExtraParts > 0 ? $"  (+{ExtraParts} extra parts not imported)" : "");

    public override string ToString() => Describe();
}

/// <summary>Dove mettere una base coreana: direzione e posizione dell'obiettivo, scarto delle piste (piedi).</summary>
/// <param name="NumbersOk">i numeri di pista scritti nei modelli (se ce ne sono) restano giusti con questa rotazione</param>
public readonly record struct Placement(double Cost, double Heading, double X, double Y, bool NumbersOk = true);

public static class KoreaBases
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    /// <summary>Declinazione magnetica (gradi, est positivo) della Korea, con cui sono numerate le sue piste, e quella del teatro.</summary>
    public const double KoreaVariation = -8, TheaterVariation = 3;

    private static int Num(double mag) { int n = (int)Math.Round(((mag % 360) + 360) % 360 / 10); if (n <= 0) n += 36; if (n > 36) n -= 36; return n; }

    /// <summary>Con la base ruotata di <paramref name="heading"/> gradi i numeri di pista calcolati restano quelli della Korea.</summary>
    public static bool SameNumbers(KoreaBase k, double heading) =>
        k.Runways.All(r => Num(r.Hdg - k.Variation) == Num(r.Hdg + heading - TheaterVariation));
    private static readonly Regex FixedNum = new(@"\b(thr|threshold)\s+\d{2}[LRC]?\b|\brwy\s+\d{2}[LRC]?[T]?\b", RegexOptions.IgnoreCase);
    private static readonly Regex Civil = new(@"\b(intl|international|airport)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MilitaryName = new(@"\b(ab|airbase|air base|afb|mcas)\b", RegexOptions.IgnoreCase);

    /// <summary>Cartella ObjectiveRelatedData della 4.38 (Data\Terrdata\objects), o null.</summary>
    public static string? OrdDir(string bmsData)
    {
        foreach (var o in new[] { "objects", "Objects" })
        {
            string d = Path.Combine(bmsData, "Terrdata", o, "ObjectiveRelatedData");
            if (Directory.Exists(d)) return d;
        }
        return null;
    }

    /// <summary>
    /// Basi della campagna coreana (Data\Campaign\CampObjData.XML) con le piste nella disposizione: una voce per disposizione.
    /// <paramref name="featureName"/> da indice CT al nome del pezzo (database 4.38).
    /// </summary>
    public static List<KoreaBase> Load(string bmsData, Func<int, string> featureName)
    {
        var res = new List<KoreaBase>();
        string? ord = OrdDir(bmsData);
        string camp = Path.Combine(bmsData, "Campaign", "CampObjData.XML");
        if (ord == null || !File.Exists(camp)) return res;
        var objs = Regex.Matches(File.ReadAllText(camp), @"<CampObj CampId=""\d+"">\s*<CampName>(.*?)</CampName>\s*<OcdIndex>(\d+)</OcdIndex>", RegexOptions.Singleline)
            .Select(m => (Name: System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim(), Ocd: int.Parse(m.Groups[2].Value, Ci))).ToList();
        var parts = objs.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.Select(o => o.Ocd).Distinct().Count());
        var seen = new HashSet<int>();
        foreach (var (name, ocd) in objs)
        {
            if (!seen.Add(ocd)) continue;
            if (Build(Path.Combine(ord, $"OCD_{ocd:00000}"), ocd, name, parts.GetValueOrDefault(name, 1) - 1, featureName) is { } k) res.Add(k);
        }
        return res.OrderBy(b => b.Name).ToList();
    }

    /// <summary>
    /// Basi del teatro stesso (cartella ObjectiveRelatedData del teatro installato, comprese quelle importate con Database
    /// Interchange): ogni disposizione con almeno una pista di 2500 piedi. Ocd codificato con <see cref="BaseSwaps.TheaterOffset"/>.
    /// </summary>
    public static List<KoreaBase> LoadTheater(string ord, Func<int, string> featureName)
    {
        var res = new List<KoreaBase>();
        if (!Directory.Exists(ord)) return res;
        foreach (var dir in Directory.EnumerateDirectories(ord, "OCD_*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)[4..], NumberStyles.Integer, Ci, out int ocd)) continue;
            var x = Directory.EnumerateFiles(dir, "OCD_*.XML").FirstOrDefault();
            var phd = Directory.EnumerateFiles(dir, "PHD_*.XML").FirstOrDefault();
            if (x == null || phd == null) continue;
            string name = $"OCD {ocd}";
            try { var m = Regex.Match(File.ReadAllText(x), @"<Name>(.*?)</Name>"); if (m.Success && m.Groups[1].Value.Trim().Length > 0) name = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim(); }
            catch (IOException) { }
            try { if (Build(dir, BaseSwaps.TheaterOffset + ocd, name, 0, featureName) is { } k) res.Add(k); }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) { }
        }
        return res.OrderBy(b => b.Name).ToList();
    }

    private static KoreaBase? Build(string dir, int ocd, string name, int extraParts, Func<int, string> featureName)
    {
        {
            if (!Directory.Exists(dir)) return null;
            var rws = ObjectsJob.LayoutRunways(dir).Where(r => r.Len >= 2500).Select(r => new Runway(r.X, r.Y, r.Hdg, r.Len, r.Num)).ToList();
            if (rws.Count == 0) return null;
            var fed = Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault();
            var names = fed == null ? new List<string>() :
                Regex.Matches(File.ReadAllText(fed), @"<FeatureCtIdx>(\d+)</FeatureCtIdx>").Select(m => featureName(int.Parse(m.Groups[1].Value, Ci))).ToList();
            var pdx = Directory.EnumerateFiles(dir, "PDX_*.XML").FirstOrDefault();
            // parcheggi = punti 10-13 sulle vie di rullaggio; le liste di ogni senso di pista li ripetono: si contano le posizioni diverse
            // parcheggi = punti 10-13 sulle vie di rullaggio (MaxWidth = apertura alare massima, piedi); le liste di ogni senso di
            // pista li ripetono: si contano le posizioni diverse
            var spots = new Dictionary<(double, double), double>();
            if (pdx != null)
                foreach (Match m in Regex.Matches(File.ReadAllText(pdx), @"<PD Num=""\d+"">(.*?)</PD>", RegexOptions.Singleline))
                {
                    string b = m.Groups[1].Value;
                    var t = Regex.Match(b, @"<Type>(\d+)</Type>");
                    if (!t.Success || int.Parse(t.Groups[1].Value, Ci) is < 10 or > 13) continue;
                    double X(string tag) { var v = Regex.Match(b, $"<{tag}>(.*?)</{tag}>"); return v.Success ? double.Parse(v.Groups[1].Value, Ci) : 0; }
                    spots[(Math.Round(X("OffsetX") / 10), Math.Round(X("OffsetY") / 10))] = X("MaxWidth");
                }
            int park = spots.Count;
            double maxSpan = spots.Count > 0 ? spots.Values.Max() : 0;
            return new KoreaBase
            {
                Ocd = ocd, Name = name, Runways = rws, Features = names.Count, Parking = park, MaxWingspanFt = maxSpan,
                HasTaxiways = names.Any(n => n.Contains("taxiway", StringComparison.OrdinalIgnoreCase)),
                Military = MilitaryName.IsMatch(name) || !Civil.IsMatch(name),
                FixedNumbers = names.Where(n => FixedNum.IsMatch(n) && !n.Contains("XX", StringComparison.OrdinalIgnoreCase) && !n.Contains("Sign", StringComparison.OrdinalIgnoreCase)).Distinct().ToList(),
                ExtraParts = extraParts,
            };
        }
    }

    private static double AxisDiff(double a, double b) { double d = ((a - b) % 180 + 180) % 180; return d > 90 ? 180 - d : d; }

    /// <summary>
    /// Posizione e direzione dell'obiettivo che portano le piste della base coreana (nella sua disposizione) sulle piste
    /// <paramref name="target"/> (coordinate del teatro), e lo scarto: distanza dei centri + angolo + differenza di lunghezza.
    /// Null se il numero di piste e' diverso. Con una pista sola vanno bene i due versi: si prende quello con lo scarto minore
    /// (il pulsante "Turn 180°" dell'editor da' l'altro).
    /// </summary>
    public static Placement? Place(IReadOnlyList<Runway> target, KoreaBase k)
    {
        if (target.Count == 0 || target.Count != k.Runways.Count) return null;
        Placement? best = null;
        foreach (var perm in Permutations(k.Runways.Count))
            foreach (double f in new[] { 0.0, 180.0 })
            {
                double h = ((target[0].Hdg - k.Runways[perm[0]].Hdg + f) % 360 + 360) % 360;
                double r = h * Math.PI / 180, ch = Math.Cos(r), sh = Math.Sin(r);
                var kc = perm.Select(j => (X: k.Runways[j].X * ch - k.Runways[j].Y * sh, Y: k.Runways[j].X * sh + k.Runways[j].Y * ch)).ToList();
                double tx = target.Select((t, i) => t.X - kc[i].X).Average(), ty = target.Select((t, i) => t.Y - kc[i].Y).Average();
                double cost = 0;
                for (int i = 0; i < target.Count; i++)
                {
                    var kr = k.Runways[perm[i]];
                    cost += Math.Sqrt(Math.Pow(target[i].X - kc[i].X - tx, 2) + Math.Pow(target[i].Y - kc[i].Y - ty, 2))
                          + AxisDiff(target[i].Hdg, kr.Hdg + h) * Math.PI / 180 * 20000 + 0.5 * Math.Abs(target[i].Len - kr.Len);
                }
                bool ok = k.FixedNumbers.Count == 0 || SameNumbers(k, h);
                // a parita' di scarto (una pista sola: i due versi) vince il verso che lascia giusti i numeri
                if (best == null || cost + (ok ? 0 : 1) < best.Value.Cost + (best.Value.NumbersOk ? 0 : 1)) best = new Placement(cost, h, tx, ty, ok);
            }
        return best;
    }

    /// <summary>
    /// Come <see cref="Place"/> ma solo con la pista piu' lunga dei due (anche con un numero di piste diverso): lo scarto
    /// conta la differenza di lunghezza e 3000 piedi per ogni pista in piu' o in meno.
    /// </summary>
    public static Placement? PlaceMain(IReadOnlyList<Runway> target, KoreaBase k)
    {
        if (target.Count == 0 || k.Runways.Count == 0) return null;
        var t = target.MaxBy(r => r.Len); var kr = k.Runways.MaxBy(r => r.Len);
        var p = Place(new[] { t }, new KoreaBase { Ocd = k.Ocd, Name = k.Name, Runways = new List<Runway> { kr }, FixedNumbers = k.FixedNumbers });
        return p is Placement q ? q with { Cost = q.Cost + 3000 * Math.Abs(target.Count - k.Runways.Count), NumbersOk = k.FixedNumbers.Count == 0 || SameNumbers(k, q.Heading) } : null;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        IEnumerable<int[]> Rec(int k)
        {
            if (k == n) { yield return (int[])a.Clone(); yield break; }
            for (int i = k; i < n; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Rec(k + 1)) yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
        return n <= 4 ? Rec(0) : new[] { a };
    }
}

/// <summary>
/// Basi del teatro sostituite con una base coreana completa (Projects\&lt;progetto&gt;\base_swaps.csv). Posizione e direzione
/// stanno in objective_edits.csv; il lavoro Objects copia la disposizione coreana con un numero OCD nuovo.
/// </summary>
public sealed record BaseSwap(int CampId, string Name, int KoreaOcd, string KoreaName)
{
    /// <summary>Base presa dal teatro stesso (KoreaOcd codificato con <see cref="BaseSwaps.TheaterOffset"/>).</summary>
    public bool FromTheater => BaseSwaps.FromTheater(KoreaOcd);
    public int RealOcd => BaseSwaps.RealOcd(KoreaOcd);
}

public static class BaseSwaps
{
    public const string FileName = "base_swaps.csv";
    /// <summary>Numeri da qui in su = disposizioni del teatro stesso (OCD + offset), sotto = Korea 4.38.</summary>
    public const int TheaterOffset = 100000;
    public static bool FromTheater(int ocd) => ocd >= TheaterOffset;
    public static int RealOcd(int ocd) => ocd >= TheaterOffset ? ocd - TheaterOffset : ocd;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    public static Dictionary<int, BaseSwap> Load(string projectFolder)
    {
        var res = new Dictionary<int, BaseSwap>();
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return res;
        foreach (var line in File.ReadAllLines(f).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length < 4 || !int.TryParse(p[0], NumberStyles.Integer, Ci, out int id) || !int.TryParse(p[2], NumberStyles.Integer, Ci, out int ocd)) continue;
            res[id] = new BaseSwap(id, p[1], ocd, p[3]);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<BaseSwap> swaps)
    {
        var sb = new StringBuilder("CampId;Name;KoreaOcd;KoreaName\r\n");
        foreach (var s in swaps.OrderBy(s => s.CampId))
            sb.Append(s.CampId.ToString(Ci)).Append(';').Append(s.Name.Replace(';', ',')).Append(';').Append(s.KoreaOcd.ToString(Ci)).Append(';')
              .Append(s.KoreaName.Replace(';', ',')).Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }
}
