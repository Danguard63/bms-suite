using System.Globalization;
using System.Text;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Editor;

/// <summary>Cosa fare di un ponte: tenerlo (c'e' acqua sotto), controllarlo a mano (acqua vicina ma non sotto), farne un incrocio (niente acqua).</summary>
public enum BridgeAction { Keep, Check, Junction }

/// <summary>Una riga di bridges.csv: rilievo automatico di un ponte e decisione (vuota = vale la proposta).</summary>
public sealed record BridgeRow(int CampId, string Name, int Pieces, double WaterUnderM, double NearestWaterM, double LevelFt,
                               BridgeAction Proposal, BridgeAction? Decision, double X, double Y, string Note,
                               double MoveM = 0, double TurnDeg = 0, double NewX = 0, double NewY = 0, double NewHeading = 0, bool? MoveDecision = null, bool? LevelDecision = null)
{
    /// <summary>Spostamento sul corso d'acqua proposto e non rifiutato (colonna Move decision vuota o YES).</summary>
    public bool HasMove => MoveM > 0.5 || Math.Abs(TurnDeg) > 0.1;
    /// <summary>Spostamenti oltre BridgeOptimizer.AutoMoveM solo con Move? = YES (un fiume lontano puo' essere quello sbagliato).</summary>
    public bool MoveFinal => HasMove && (MoveDecision == true || (MoveDecision == null && MoveM <= BridgeOptimizer.AutoMoveM)) && Final != BridgeAction.Junction;

    /// <summary>Quello che fa Run Objects: la decisione, altrimenti la proposta; "Check" senza decisione resta un ponte.</summary>
    public BridgeAction Final => Decision ?? (MoveDecision == true ? BridgeAction.Keep : Proposal == BridgeAction.Junction ? BridgeAction.Junction : BridgeAction.Keep);
}

/// <summary>
/// bridges.csv nella cartella del progetto: lo scrive "Optimize bridges…" dell'Object Editor, lo legge Run Objects
/// (incroci al posto dei ponti senza acqua, quote dei pezzi dei ponti tenuti). Colonna Decision modificabile a mano.
/// </summary>
public static class BridgePlan
{
    public const string FileName = "bridges.csv";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const string Header = "CampId;Name;Pieces;Water under (m);Nearest water (m);Level (ft);Proposal;Decision;X (ft);Y (ft);Note;Move (m);Turn (deg);New X (ft);New Y (ft);New heading;Move decision;Level decision";

    public static string PathFor(string projectFolder) => Path.Combine(projectFolder, FileName);

    public static Dictionary<int, BridgeRow>? Load(string projectFolder)
    {
        string f = PathFor(projectFolder);
        if (!File.Exists(f)) return null;
        var res = new Dictionary<int, BridgeRow>();
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length < 10 || !int.TryParse(c[0], NumberStyles.Integer, Ci, out int id)) continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, Ci, out var v) ? v : -1;
            double E(int i) => c.Length > i && double.TryParse(c[i], NumberStyles.Float, Ci, out var v) ? v : 0;
            bool? md = c.Length > 16 ? c[16].Trim().ToUpperInvariant() switch { "YES" => true, "NO" => false, _ => null } : null;
            res[id] = new BridgeRow(id, c[1], (int)D(2), D(3), D(4), D(5), Parse(c[6]) ?? BridgeAction.Keep, Parse(c[7]), D(8), D(9), c.Length > 10 ? c[10] : "",
                                    E(11), E(12), E(13), E(14), E(15), md,
                                    c.Length > 17 && c[17].Trim().Equals("NO", StringComparison.OrdinalIgnoreCase) ? false : null);
        }
        return res;
    }

    public static void Save(string projectFolder, IEnumerable<BridgeRow> rows)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var r in rows.OrderBy(r => r.Proposal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(r.CampId).Append(';').Append(r.Name.Replace(';', ',')).Append(';').Append(r.Pieces).Append(';')
              .Append(r.WaterUnderM.ToString("0", Ci)).Append(';').Append(r.NearestWaterM < 0 ? "" : r.NearestWaterM.ToString("0", Ci)).Append(';')
              .Append(r.LevelFt.ToString("0.0", Ci)).Append(';').Append(Name(r.Proposal)).Append(';').Append(r.Decision is { } d ? Name(d) : "").Append(';')
              .Append(r.X.ToString("0", Ci)).Append(';').Append(r.Y.ToString("0", Ci)).Append(';').Append(r.Note.Replace(';', ',')).Append(';')
              .Append(r.HasMove ? r.MoveM.ToString("0", Ci) : "").Append(';').Append(r.HasMove ? r.TurnDeg.ToString("0", Ci) : "").Append(';')
              .Append(r.HasMove ? r.NewX.ToString("0.0", Ci) : "").Append(';').Append(r.HasMove ? r.NewY.ToString("0.0", Ci) : "").Append(';')
              .Append(r.HasMove ? r.NewHeading.ToString("0.0", Ci) : "").Append(';').Append(r.MoveDecision switch { true => "YES", false => "NO", _ => "" }).Append(';')
              .Append(r.LevelDecision == false ? "NO" : "").Append("\r\n");
        string f = PathFor(projectFolder);
        if (File.Exists(f)) File.Copy(f, f + ".bak", true);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
    }

    public static string Name(BridgeAction a) => a switch { BridgeAction.Keep => "KEEP", BridgeAction.Check => "CHECK", _ => "JUNCTION" };

    public static BridgeAction? Parse(string s) => s.Trim().ToUpperInvariant() switch
    {
        "KEEP" or "TIENI" => BridgeAction.Keep,
        "CHECK" or "DA DECIDERE" => BridgeAction.Check,
        "JUNCTION" or "VIA" => BridgeAction.Junction,
        _ => null,
    };
}

/// <summary>
/// Acqua e terreno del teatro finito, per i ponti. Acqua: Output\TB_Input\Watermap.raw (8 bit, diverso da 0 = acqua, la
/// maschera da cui Terrain Builder fa l'acqua delle tessere) se c'e', altrimenti WaterHeightMap sopra HeightMap del NewTerrain.
/// Terreno: HeightMap del NewTerrain. Coordinate in piedi del teatro (north = X di BMS, east = Y).
/// </summary>
public sealed class WaterProbe : IDisposable
{
    private readonly TerrainSampler _s;
    private readonly FileStream? _wm;
    private readonly int _wmSide;
    private readonly double _sizeFt;
    private readonly bool _nt;
    private readonly Dictionary<(int, int), bool> _cache = new();
    private readonly Dictionary<(int, int), bool[,]> _tiles = new();
    private readonly Dictionary<(int, int), double> _ground = new();
    private const int Tile = 128;
    public string Source { get; }

    public WaterProbe(TheaterProject p)
    {
        _s = new TerrainSampler(p);
        _sizeFt = _s.SizeFt;
        // prima l'acqua che BMS disegna davvero: pelo dell'acqua (WaterHeightMap) sopra il terreno (HeightMap) del NewTerrain
        _nt = _s.WaterSide > 0;
        string wm = Path.Combine(p.ProjectFolder, "Output", "TB_Input", "Watermap.raw");
        if (!_nt && File.Exists(wm))
        {
            long len = new FileInfo(wm).Length;
            int side = (int)Math.Round(Math.Sqrt(len));
            if ((long)side * side == len)
            {
                _wm = new FileStream(wm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                _wmSide = side;
            }
        }
        Source = _nt ? $"NewTerrain WaterHeightMap above HeightMap ({_s.HeightPxFt * 0.3048:0.#} m per pixel, as BMS draws it)"
               : _wm != null ? $"Watermap.raw ({_sizeFt * 0.3048 / _wmSide:0.#} m per pixel)"
               : "no water data";
    }

    public bool HasWater => _nt || _wm != null;

    /// <summary>Lato di un pixel della mappa dell'acqua usata (piedi).</summary>
    public double PixelFt => _nt ? _s.HeightPxFt : _wm != null ? _sizeFt / _wmSide : 200;

    public bool IsWater(double north, double east)
    {
        if (north < 0 || east < 0 || north >= _sizeFt || east >= _sizeFt) return false;
        if (_nt)
        {
            // a blocchi di Tile×Tile pixel della HeightMap: una lettura per blocco
            double px = _s.HeightPxFt, tf = Tile * px;
            int tj = (int)((_sizeFt - north) / tf), ti = (int)(east / tf);
            if (!_tiles.TryGetValue((tj, ti), out var t))
            {
                double cn = _sizeFt - (tj + 0.5) * tf, ce = (ti + 0.5) * tf, half = tf / 2;
                var h = _s.Heights(cn, ce, half, Tile + 1);
                var w = _s.WaterSurface(cn, ce, half, Tile + 1);
                t = new bool[Tile + 1, Tile + 1];
                if (h != null && w != null)
                    for (int j = 0; j <= Tile; j++)
                        for (int i = 0; i <= Tile; i++) t[j, i] = w[j, i] > h[j, i] + 0.5;
                _tiles[(tj, ti)] = t;
            }
            double cn0 = _sizeFt - tj * tf, ce0 = ti * tf;
            int r = Math.Clamp((int)Math.Round((cn0 - north) / px), 0, Tile), c = Math.Clamp((int)Math.Round((east - ce0) / px), 0, Tile);
            return t[r, c];
        }
        if (_wm != null)
        {
            int col = (int)(east / _sizeFt * _wmSide), row = (int)((_sizeFt - north) / _sizeFt * _wmSide);
            if (_cache.TryGetValue((row, col), out bool b)) return b;
            _wm.Position = (long)row * _wmSide + col;
            b = _wm.ReadByte() is int v && v > 0;
            return _cache[(row, col)] = b;
        }
        return false;
    }

    /// <summary>Quota del terreno (piedi) come la usa BMS per appoggiare i pezzi (HeightMap, bilineare).</summary>
    public double Ground(double north, double east)
    {
        var k = ((int)Math.Round(north), (int)Math.Round(east));
        if (_ground.TryGetValue(k, out double g)) return g;
        return _ground[k] = _s.GroundAt(north, east);
    }

    public void Dispose() => _wm?.Dispose();
}

/// <summary>
/// Quote dei pezzi di un ponte. BMS appoggia ogni pezzo al terreno sotto il proprio centro: su un fiume la campata sta
/// sul letto e le rampe sugli argini, e alle giunzioni i pezzi non si incontrano. I pezzi sono rigidi e orizzontali e combaciano
/// solo se stanno tutti alla stessa quota: qui vanno tutti a meta' tra le quote delle due estremita', con OffsetZ (piedi, + = giu').
/// </summary>
public static class BridgeLeveling
{
    /// <summary>Sotto questo scarto (piedi) non si tocca niente.</summary>
    public const double MinFt = 2;
    /// <summary>Spostamento massimo (piedi) dei pezzi di testa.</summary>
    public const double MaxEndFt = 15;

    /// <param name="centers">centri dei pezzi in coordinate del teatro (north, east)</param>
    /// <returns>OffsetZ da aggiungere a ciascun pezzo (piedi, positivo = in giu'), o null se non serve</returns>
    public static double[]? Offsets(IReadOnlyList<(double N, double E)> centers, Func<double, double, double> ground, Func<double, double, bool>? isWater = null)
    {
        int n = centers.Count;
        if (n < 2) return null;
        // dighe e ponti sul mare (almeno l'80% dei pezzi sull'acqua, niente rampe a terra): si lasciano come li ha fatti l'autore
        if (isWater != null && centers.Count(c => isWater(c.N, c.E)) >= 0.8 * n) return null;
        // asse del ponte: i due centri piu' lontani tra loro
        int a = 0, b = 1; double best = -1;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double d = Sq(centers[i].N - centers[j].N) + Sq(centers[i].E - centers[j].E);
                if (d > best) { best = d; a = i; b = j; }
            }
        if (best < 1) return null;
        double len = Math.Sqrt(best), un = (centers[b].N - centers[a].N) / len, ue = (centers[b].E - centers[a].E) / len;
        var g = centers.Select(c => ground(c.N, c.E)).ToArray();
        var res = new double[n];
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            // tutti i pezzi alla stessa quota (i pezzi di un ponte combaciano solo in piano: sono rigidi e orizzontali),
            // a meta' tra le quote delle due estremita': una testata entra un po' nella riva, l'altra resta un po' sollevata
            double want = (g[a] + g[b]) / 2;
            // le testate si spostano al massimo di MaxEndFt: con rive molto diverse una testata sospesa o sepolta di decine
            // di piedi e' peggio di un gradino alla giunzione
            if (i == a || i == b) want = g[i] + Math.Clamp(want - g[i], -MaxEndFt, MaxEndFt);
            res[i] = Math.Round(g[i] - want, 1);   // terreno piu' basso di dove deve stare il pezzo → negativo = in su
            max = Math.Max(max, Math.Abs(res[i]));
        }
        return max < MinFt ? null : res;
    }

    private static double Sq(double x) => x * x;
}

/// <summary>Rilievo di tutti i ponti del teatro: acqua sotto i pezzi, acqua vicina, quote dei pezzi. Solo lettura.</summary>
public static class BridgeOptimizer
{
    /// <summary>Acqua entro questa distanza ma non sotto il ponte: da controllare a mano (ponte forse spostato rispetto al fiume).</summary>
    public const double CheckRadiusM = 2000;
    private const double Ft = 0.3048;

    public static bool IsBridge(EdObjective o) => o.Type == 6 || (o.Type == 15 && o.OrigType == 6);

    /// <summary>Pezzi del ponte (anche di un ponte che un Run Objects precedente ha gia' fatto diventare incrocio).</summary>
    public static IReadOnlyList<EdFeature> Pieces(EditorData d, EdObjective o) =>
        (o.Type == 6 ? d.LayoutOf(o) : d.Layout(o.OrigOcd)).Features.Where(f => f.Z < 100).ToList();

    public static (double N, double E) Center(EdObjective o, EdFeature f) => EditorData.ToWorld(o, f.N, f.E);

    public static List<BridgeRow> Survey(EditorData d, WaterProbe w, Dictionary<int, BridgeRow>? old, IProgress<double>? progress, CancellationToken ct)
    {
        var res = new List<BridgeRow>();
        var list = d.Objectives.Where(IsBridge).ToList();
        int k = 0;
        foreach (var o in list)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((double)k++ / Math.Max(1, list.Count));
            var pieces = Pieces(d, o);
            var dec = old != null && old.TryGetValue(o.Id, out var prev) ? prev.Decision : null;
            if (pieces.Count == 0)
            {
                res.Add(new BridgeRow(o.Id, o.Name, 0, 0, -1, 0, BridgeAction.Keep, dec, o.X, o.Y, "no pieces in the layout"));
                continue;
            }
            // acqua sotto: campioni lungo l'asse di ogni pezzo (80% centrale della sagoma), ogni 30 piedi
            double wetFt = 0;
            var ends = new List<(double N, double E)>();
            foreach (var f in pieces)
            {
                double H = (o.Heading + f.Heading) * Math.PI / 180, fn = Math.Cos(H), fe = Math.Sin(H);
                var (cn, ce) = Center(o, f);
                double x0 = f.MinX * 0.9, x1 = f.MaxX * 0.9;
                for (double t = x0; t <= x1; t += 30)
                    if (w.IsWater(cn + t * fn, ce + t * fe)) wetFt += 30;
                ends.Add((cn + f.MinX * fn, ce + f.MinX * fe));
                ends.Add((cn + f.MaxX * fn, ce + f.MaxX * fe));
                ends.Add((cn, ce));
            }
            // acqua vicina (se sotto non ce n'e'): anelli fino a CheckRadiusM attorno a centri ed estremita' dei pezzi
            double near = wetFt > 0 ? 0 : -1;
            if (wetFt <= 0)
            {
                for (double r = 25; r <= CheckRadiusM && near < 0; r += r < 300 ? 25 : 50)
                    foreach (var (pn, pe) in ends)
                    {
                        for (int a = 0; a < 24 && near < 0; a++)
                        {
                            double an = a * Math.PI / 12;
                            if (w.IsWater(pn + r / Ft * Math.Cos(an), pe + r / Ft * Math.Sin(an))) near = r;
                        }
                        if (near >= 0) break;
                    }
            }
            // posizione sul corso d'acqua: per i ponti con acqua sotto o vicina, la posizione/direzione migliore entro FitRadiusM
            var dmove = old != null && old.TryGetValue(o.Id, out var pv) ? pv.MoveDecision : null;
            var dlevel = old != null && old.TryGetValue(o.Id, out var pl) ? pl.LevelDecision : null;
            var fit = wetFt > 0 || near >= 0 ? Fit(o, pieces, w) : null;
            var pose = fit is { Move: true } ? new EdObjective { X = fit.X, Y = fit.Y, Heading = fit.Heading } : o;
            // terreno gia' spianato da un Run Objects precedente (TLD del ponte): li' la quota e' quella dello spianamento
            var tld = fit is { Move: true } ? null : d.LevelingAll(o);
            double G(double n, double e)
            {
                if (tld is { } t)
                    foreach (var (pct, poly) in t.Polys)
                        if (pct >= 99.9 && InsidePoly(poly, n, e)) return t.Level;
                return w.Ground(n, e);
            }
            var offs = BridgeLeveling.Offsets(pieces.Select(f => Center(pose, f)).ToList(), G, w.IsWater);
            // per le anteprime (Fly over) prima di Run Objects: quanto manca rispetto all'OffsetZ che il ponte ha gia'
            // spostamento lungo (oltre AutoMoveM) per un ponte senza acqua sotto: si propone l'incrocio, lo spostamento solo se scelto
            bool far = fit is { Move: true } && fit.MoveM > AutoMoveM && wetFt <= 0 && dmove != true;
            bool keep = wetFt > 0 || (fit is { Move: true } && !far);
            lock (d.ExtraZ)
            {
                if (offs != null && keep && dlevel != false) d.ExtraZ[o.Id] = pieces.Select((f, i) => (f.Num, offs[i] - f.Z)).GroupBy(x => x.Num).ToDictionary(g => g.Key, g => g.First().Item2);
                else d.ExtraZ.Remove(o.Id);
            }
            double lv = offs?.Max(Math.Abs) ?? 0;
            BridgeAction prop = keep ? BridgeAction.Keep : far ? BridgeAction.Junction : near >= 0 ? BridgeAction.Check : BridgeAction.Junction;
            var notes = new List<string>();
            if (far) notes.Add($"the nearest good crossing is {fit!.MoveM:0} m away: JUNCTION proposed; Move? = YES moves the bridge there instead");
            else if (fit is { Move: true } && fit.MoveM > AutoMoveM && dmove != true) notes.Add($"move {fit.MoveM:0} m, turn {fit.TurnDeg:+0;-0;0}° onto the river ({fit.Why}): more than {AutoMoveM:0} m, applied only with Move? = YES");
            else if (fit is { Move: true }) notes.Add($"move {fit.MoveM:0} m, turn {fit.TurnDeg:+0;-0;0}° onto the river ({fit.Why})");
            else if (fit != null && fit.NowOk && fit.Why == "ok") notes.Add("crosses the river correctly");
            else if (fit != null) notes.Add(fit.Why);
            if (prop == BridgeAction.Check) notes.Add($"water {near:0} m away but not under the bridge, no good crossing found within {FitRadiusM:0} m");
            if (prop == BridgeAction.Junction && !far) notes.Add($"no water within {CheckRadiusM:0} m");
            if (offs != null && keep && dlevel == false) notes.Add("not leveled (Level? = NO)");
            else if (offs != null && keep) notes.Add(lv > 30 ? $"pieces leveled up to {lv:0} ft: steep banks, check it (Level? = NO keeps the original heights)" : $"pieces leveled up to {lv:0} ft");
            if (o.Type == 15) notes.Add("already a junction from a previous Run Objects");
            res.Add(new BridgeRow(o.Id, o.Name, pieces.Count, wetFt * Ft, near, lv, prop, dec, o.X, o.Y, string.Join("; ", notes),
                fit is { Move: true } ? fit.MoveM : 0, fit?.TurnDeg ?? 0, fit?.X ?? 0, fit?.Y ?? 0, fit?.Heading ?? 0, dmove, dlevel));
        }
        progress?.Report(1);
        return res;
    }

    // ───────────────────────── posizione sul fiume ─────────────────────────

    /// <summary>Distanza massima a cui si cerca un attraversamento migliore.</summary>
    public const double FitRadiusM = 2000;

    /// <summary>Spostamenti fino a questa distanza si applicano da soli; oltre, solo con Move? = YES.</summary>
    public const double AutoMoveM = 1000;

    /// <summary>Risultato della ricerca: posa migliore (piedi del teatro, gradi), spostamento, e se la posa attuale va gia' bene.</summary>
    public sealed record FitResult(double X, double Y, double Heading, double MoveM, double TurnDeg, bool NowOk, bool Move, string Why);

    /// <summary>
    /// Attraversamento del fiume: lungo l'asse del ponte, le due estremita' devono stare sulla terra, l'acqua solo tra i centri dei
    /// pezzi di testa (sotto le campate, non sotto le rampe) e possibilmente in mezzo; a parita', il tratto d'acqua piu' corto
    /// (ponte perpendicolare alla corrente) e lo spostamento piu' piccolo. Si provano posizioni entro FitRadiusM (75 m, poi 25 m attorno ai migliori) e
    /// direzioni ogni 5° fino a ±90° (un ponte e' simmetrico). Solo sulla mappa dell'acqua del teatro.
    /// </summary>
    public static FitResult? Fit(EdObjective o, IReadOnlyList<EdFeature> pieces, WaterProbe w) => Fit(o, pieces, w.IsWater, w.PixelFt);

    public static FitResult? Fit(EdObjective o, IReadOnlyList<EdFeature> pieces, Func<double, double, bool> isWater, double pixelFt)
    {
        if (pieces.Count == 0) return null;
        // asse nella disposizione (direzione 0): i due centri piu' lontani, o la direzione del pezzo se e' uno solo
        double a0;
        int ia = 0, ib = 0; double best = -1;
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
            {
                double dd = Sq(pieces[i].N - pieces[j].N) + Sq(pieces[i].E - pieces[j].E);
                if (dd > best) { best = dd; ia = i; ib = j; }
            }
        if (best > 100) a0 = Math.Atan2(pieces[ib].E - pieces[ia].E, pieces[ib].N - pieces[ia].N);
        else a0 = pieces[0].Heading * Math.PI / 180;
        double un = Math.Cos(a0), ue = Math.Sin(a0);
        double S(double n, double e) => n * un + e * ue;
        double lat = pieces.Average(f => -f.N * ue + f.E * un);
        double s0 = double.MaxValue, s1 = double.MinValue;
        foreach (var f in pieces)
        {
            double H = f.Heading * Math.PI / 180, fn = Math.Cos(H), fe = Math.Sin(H);
            foreach (double x in new[] { f.MinX, f.MaxX }) { double sv = S(f.N + x * fn, f.E + x * fe); s0 = Math.Min(s0, sv); s1 = Math.Max(s1, sv); }
        }
        double len = s1 - s0;
        if (len < 50) return null;
        double zA, zB;
        if (best > 100) { zA = Math.Min(S(pieces[ia].N, pieces[ia].E), S(pieces[ib].N, pieces[ib].E)); zB = Math.Max(S(pieces[ia].N, pieces[ia].E), S(pieces[ib].N, pieces[ib].E)); }
        else { zA = s0 + 0.15 * len; zB = s1 - 0.15 * len; }
        double endLen = Math.Max(len * 0.08, 30);

        // mappa locale dell'acqua (bool per pixel) attorno al ponte
        double px = pixelFt, rad = FitRadiusM / Ft + len / 2 + 2 * px;
        int nCell = (int)Math.Ceiling(2 * rad / px) + 1;
        double gN0 = o.X + rad, gE0 = o.Y - rad;   // angolo nord-ovest
        var grid = new bool[nCell, nCell];
        for (int r = 0; r < nCell; r++)
            for (int c = 0; c < nCell; c++)
                grid[r, c] = isWater(gN0 - (r + 0.5) * px, gE0 + (c + 0.5) * px);
        bool Wet(double n, double e)
        {
            int r = (int)((gN0 - n) / px), c = (int)((e - gE0) / px);
            return r >= 0 && c >= 0 && r < nCell && c < nCell && grid[r, c];
        }
        // direzione del fiume (radianti, 0 = nord) dai pixel d'acqua entro rr piedi; null se non c'e' una direzione netta
        double rr = Math.Max(3 * px, len * 0.6);
        var dirCache = new Dictionary<(int, int), double?>();
        double? RiverDir(double n, double e)
        {
            int r0 = (int)((gN0 - n) / px), c0 = (int)((e - gE0) / px);
            if (dirCache.TryGetValue((r0, c0), out var cached)) return cached;
            int k = (int)Math.Ceiling(rr / px);
            double sn = 0, se = 0, snn = 0, see = 0, sne = 0; int cnt = 0, tot = 0;
            for (int r = r0 - k; r <= r0 + k; r++)
                for (int c = c0 - k; c <= c0 + k; c++)
                {
                    if (Sq(r - r0) + Sq(c - c0) > k * k) continue;
                    tot++;
                    if (r < 0 || c < 0 || r >= nCell || c >= nCell || !grid[r, c]) continue;
                    double dn = -(r - r0) * px, de = (c - c0) * px;
                    sn += dn; se += de; snn += dn * dn; see += de * de; sne += dn * de; cnt++;
                }
            double? res = null;
            if (cnt >= 3 && cnt < tot * 0.85)
            {
                double mn = sn / cnt, me = se / cnt, cnn = snn / cnt - mn * mn, cee = see / cnt - me * me, cne = sne / cnt - mn * me;
                double tr = cnn + cee, det = cnn * cee - cne * cne, l1 = tr / 2 + Math.Sqrt(Math.Max(0, tr * tr / 4 - det)), l2 = tr - l1;
                if (l1 > 2.5 * Math.Max(l2, 1e-6)) res = 0.5 * Math.Atan2(2 * cne, cee - cnn) is double t ? Math.PI / 2 - t : null;   // asse principale (0 = nord)
            }
            return dirCache[(r0, c0)] = res;
        }
        double step = Math.Min(px / 2, len / 24);
        int ns = (int)(len / step) + 1;

        (double Score, bool Ok, string Why) Eval(double x, double y, double hdg, double moveFt, double turn)
        {
            double h = (hdg) * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            int wz = 0, wo = 0, nz = 0; bool endWet = false; double sumS = 0;
            for (int k = 0; k < ns; k++)
            {
                double sv = s0 + k * step;
                double ln = sv * un - lat * ue, le = sv * ue + lat * un;   // punto dell'asse nella disposizione
                double n = x + ln * ch - le * sh, e = y + ln * sh + le * ch;
                bool wet = Wet(n, e);
                bool inZone = sv >= zA && sv <= zB;
                if (inZone) nz++;
                if (!wet) continue;
                if (sv - s0 < endLen || s1 - sv < endLen) endWet = true;
                if (inZone) { wz++; sumS += sv; } else wo++;
            }
            double off = wz > 0 ? Math.Abs(sumS / wz - (zA + zB) / 2) / Math.Max(1, zB - zA) : 1;
            // angolo con la corrente: direzione del fiume dai pixel d'acqua attorno al punto d'attraversamento (PCA);
            // un lago o il mare (nessuna direzione netta) non conta. Un ponte parallelo al fiume che ne sfiora la riva non va.
            double dev = 0;
            if (wz > 0)
            {
                double sm = sumS / wz, ln = sm * un - lat * ue, le = sm * ue + lat * un;
                double cn = x + ln * ch - le * sh, ce = y + ln * sh + le * ch;
                if (RiverDir(cn, ce) is double rd)
                {
                    double ax = hdg * Math.PI / 180 + a0;
                    double ang = Math.Abs(Math.Atan2(Math.Sin(ax - rd), Math.Cos(ax - rd))) * 180 / Math.PI;   // 0..180
                    dev = Math.Abs(90 - ang);                                                                // 0 = perpendicolare
                }
            }
            bool ok = !endWet && wz > 0 && wo == 0 && dev <= 50;
            double score = (endWet ? 10 : 0) + (wz > 0 ? 0 : 6) + 4.0 * wo / ns + 1.5 * off + 0.5 * wz / Math.Max(1, nz) + 1.5 * dev / 90
                           + moveFt * Ft / 600 + Math.Abs(turn) / 240;
            string why = endWet ? "an end of the bridge is in the water" : wz == 0 ? "no water under the spans" : wo > 0 ? "water under the ramps/ends"
                       : dev > 50 ? $"crosses the river at a slant ({90 - dev:0}°)" : off > 0.35 ? "river off-centre" : "ok";
            return (score, ok, why);
        }

        // vicino a mare o laghi (piu' del 35% d'acqua entro 1 km): niente spostamenti automatici, la riva non e' un fiume
        {
            int k = (int)Math.Ceiling(1000 / Ft / px), r0 = (int)((gN0 - o.X) / px), c0 = (int)((o.Y - gE0) / px), wetC = 0, allC = 0;
            for (int r = r0 - k; r <= r0 + k; r++)
                for (int c = c0 - k; c <= c0 + k; c++)
                {
                    if (Sq(r - r0) + Sq(c - c0) > k * k || r < 0 || c < 0 || r >= nCell || c >= nCell) continue;
                    allC++; if (grid[r, c]) wetC++;
                }
            if (allC > 0 && wetC > 0.35 * allC)
                return new FitResult(o.X, o.Y, o.Heading, 0, 0, true, false, $"large water within 1 km ({100.0 * wetC / allC:0}%: sea, lake or reservoir): not moved automatically");
        }
        // mare, laghi, dighe lunghe (causeway): acqua sotto quasi tutto il ponte e nessuna direzione di corrente: si lascia com'e'
        {
            int wet = 0;
            double h0 = o.Heading * Math.PI / 180, c0 = Math.Cos(h0), s0h = Math.Sin(h0), mid = (s0 + s1) / 2;
            for (int k = 0; k < ns; k++)
            {
                double sv = s0 + k * step, ln = sv * un - lat * ue, le = sv * ue + lat * un;
                if (Wet(o.X + ln * c0 - le * s0h, o.Y + ln * s0h + le * c0)) wet++;
            }
            double mn = mid * un - lat * ue, me = mid * ue + lat * un;
            if (wet > ns / 2 && RiverDir(o.X + mn * c0 - me * s0h, o.Y + mn * s0h + me * c0) == null)
                return new FitResult(o.X, o.Y, o.Heading, 0, 0, true, false, "open water (sea, lake or causeway): left as it is");
        }
        var now = Eval(o.X, o.Y, o.Heading, 0, 0);
        double bx = o.X, by = o.Y, bh = o.Heading, bScore = now.Score; bool bOk = now.Ok; string bWhy = now.Why;
        // 1) passo largo (75 m, 10°) su tutto il raggio; 2) intorno ai migliori, passo fine (25 m, 5°)
        double R = FitRadiusM / Ft;
        var coarse = new List<(double Score, double Dx, double Dy, double Turn)>();
        double cs = 75 / Ft;
        for (double dy = -R; dy <= R; dy += cs)
            for (double dx = -R; dx <= R; dx += cs)
            {
                double m2 = dx * dx + dy * dy;
                if (m2 > R * R) continue;
                for (int t = -9; t <= 9; t++)
                {
                    var ev = Eval(o.X + dx, o.Y + dy, o.Heading + t * 10, Math.Sqrt(m2), t * 10);
                    if (ev.Ok) coarse.Add((ev.Score, dx, dy, t * 10));
                }
            }
        double fs = 25 / Ft;
        foreach (var c in coarse.OrderBy(c => c.Score).Take(12))
            for (double ddy = -cs; ddy <= cs + 1; ddy += fs)
                for (double ddx = -cs; ddx <= cs + 1; ddx += fs)
                    for (int tt = -2; tt <= 2; tt++)
                    {
                        double dx = c.Dx + ddx, dy = c.Dy + ddy, turn = c.Turn + tt * 5, m2 = dx * dx + dy * dy;
                        if (m2 > R * R || Math.Abs(turn) > 90) continue;
                        var ev = Eval(o.X + dx, o.Y + dy, o.Heading + turn, Math.Sqrt(m2), turn);
                        if (ev.Ok && ev.Score < bScore) { bScore = ev.Score; bx = o.X + dx; by = o.Y + dy; bh = ((o.Heading + turn) % 360 + 360) % 360; bOk = true; bWhy = ev.Why; }
                    }
        double moveM = Math.Sqrt(Sq(bx - o.X) + Sq(by - o.Y)) * Ft, turnD = ((bh - o.Heading) % 360 + 540) % 360 - 180;
        // si sposta solo se adesso non va (o il fiume e' molto fuori centro) e si e' trovato un attraversamento buono
        bool move = bOk && (moveM > 1 || Math.Abs(turnD) > 0.1) && (!now.Ok || now.Why == "river off-centre") && bScore < now.Score - 0.2;
        return new FitResult(move ? bx : o.X, move ? by : o.Y, move ? bh : o.Heading, move ? moveM : 0, move ? turnD : 0, now.Ok && now.Why == "ok",
                             move, move ? $"now: {now.Why}" : now.Ok ? now.Why : $"{now.Why}, no better crossing within {FitRadiusM:0} m");
    }

    private static double Sq(double x) => x * x;

    private static bool InsidePoly(List<(double X, double Y)> poly, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }
}
