namespace TerrainTool.Core.Editor;

/// <summary>
/// Scena dell'anteprima 3D intorno a un obiettivo, gia' pronta per il disegno (coordinate relative al centro, piedi):
/// X = est, Y = su (piedi sopra il terreno al centro), Z = sud (come vuole WPF: Y in alto, terna destra).
/// Terreno a griglia con la foto sopra, spianato sotto le basi come fa BMS (file TLD), e i modelli BML con le loro
/// texture per tutti i pezzi degli obiettivi nell'area, nella posizione e con la rotazione dell'editor.
/// </summary>
public sealed class PreviewScene
{
    public double CenterX { get; init; }   // nord (piedi del teatro)
    public double CenterY { get; init; }   // est
    public double Half { get; init; }
    public int GridN { get; init; }
    /// <summary>Quote del terreno (piedi), [riga da nord, colonna da ovest], gia' spianate sotto le basi.</summary>
    public float[,]? Heights { get; init; }
    public byte[]? Photo { get; init; }
    public int PhotoPx { get; init; }

    /// <summary>Gruppo di triangoli con la stessa texture (null = senza) e lo stesso stato (obiettivo scelto o no).</summary>
    public sealed class Group
    {
        public string? Tex; public bool Selected;
        public List<float> Pos { get; } = new();   // 9 float per triangolo
        public List<float> Uv { get; } = new();    // 6 float per triangolo (solo se c'e' Tex)
    }
    public Dictionary<(string Tex, bool Sel), Group> Groups { get; } = new();
    public int Triangles { get; private set; }
    public int MissingModels { get; set; }
    public int Objectives { get; set; }
    public int Leveled { get; set; }
    public double GroundAtCenter { get; set; }
    /// <summary>Light points of the models in scene coordinates (x east, y up, z south, feet from the centre), colour, size (ft).</summary>
    public List<(float X, float Y, float Z, byte R, byte G, byte B, float Size)> Lights { get; } = new();
    /// <summary>Pools of light on the ground (scene coordinates), colour + alpha, radius (ft).</summary>
    public List<(byte R, byte G, byte B, byte A, float[] Pos, float[] Fade)> Pools { get; } = new();

    public const int MaxTriangles = 900_000;

    /// <param name="separateSelected">i pezzi dell'obiettivo scelto non entrano nei gruppi: li disegna uno per uno chi chiama
    /// (PieceParts + PieceBase), per poterli spostare senza rifare la scena</param>
    public static PreviewScene Build(EditorData d, EdObjective sel, double half, int gridN = 129, int photoPx = 2048, double exaggeration = 1, bool separateSelected = false)
        => BuildAt(d, sel.X, sel.Y, sel, half, gridN, photoPx, exaggeration, separateSelected);

    /// <summary>Scena centrata su un punto qualunque del teatro (piedi: x nord, y est); sel = obiettivo da mettere per primo (o null).</summary>
    public static PreviewScene BuildAt(EditorData d, double x, double y, EdObjective? sel, double half, int gridN = 129, int photoPx = 2048, double exaggeration = 1,
        bool separateSelected = false)
    {
        var s = new PreviewScene
        {
            CenterX = x, CenterY = y, Half = half, GridN = gridN, PhotoPx = photoPx,
            Heights = d.HeightWindow(x, y, half, gridN),
            Photo = d.PhotoArea(x, y, half, photoPx),
        };
        var near = d.Objectives.Where(o => o != sel && Math.Abs(o.X - x) < half + 3000 && Math.Abs(o.Y - y) < half + 3000)
            .OrderBy(o => (o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y));
        var list = (sel != null ? near.Prepend(sel) : near)
            .Where(o => { var r = d.LayoutOf(o).Radius; return Math.Abs(o.X - x) <= half + r && Math.Abs(o.Y - y) <= half + r; })
            .ToList();
        // 1) spianamento delle basi, come BMS (prima dei modelli, che poggiano sul terreno spianato)
        if (s.Heights != null)
            foreach (var o in list)
                if (d.LevelingAll(o) is { } lv)
                {
                    // raccordi prima (33%, 66%) poi il pieno: ogni fascia parte dal terreno originale
                    var orig = (float[,])s.Heights.Clone();
                    foreach (var (pct, poly) in lv.Polys.OrderBy(q => q.Pct)) s.Level(lv.Level, poly, pct, orig);
                    s.Leveled++;
                }
        s.GroundAtCenter = s.Ground(x, y);
        // 2) modelli: prima l'obiettivo scelto, poi gli altri, i piu' vicini prima, finche' ci stanno i triangoli
        foreach (var o in list)
        {
            var lay = d.LayoutOf(o);
            var rwyNums = EditorData.RunwayNumbers(o, lay);
            s.Objectives++;
            bool isSel = o == sel;
            // luci dei modelli (sempre, anche per l'obiettivo scelto quando i suoi pezzi si disegnano uno per uno)
            foreach (var f in lay.Features)
            {
                if (d.Model(f.CtIdx) is not { } lm || lm.Lights.Count + lm.Pools.Count == 0) continue;
                var (lx, ly) = EditorData.ToWorld(o, f.N, f.E);
                if (Math.Abs(lx - x) > half || Math.Abs(ly - y) > half) continue;
                double LH = (o.Heading + f.Heading) * Math.PI / 180;
                double lfn = Math.Cos(LH), lfe = Math.Sin(LH), lrn = -Math.Sin(LH), lre = Math.Cos(LH);
                double lb = s.PieceBase(d, o, f, lx, ly, exaggeration);
                foreach (var (mx, my, mz, cr, cg, cb, sz) in lm.Lights)
                {
                    double n = lx + mz * lfn + mx * lrn, e = ly + mz * lfe + mx * lre;
                    s.Lights.Add(((float)(e - y), (float)(lb + my), (float)(-(n - x)), cr, cg, cb, sz));
                }
                foreach (var (cr, cg, cb, ca, mp, fade) in lm.Pools)
                {
                    var wp = new float[mp.Length];
                    for (int k = 0; k + 2 < mp.Length; k += 3)
                    {
                        double n = lx + mp[k + 2] * lfn + mp[k] * lrn, e = ly + mp[k + 2] * lfe + mp[k] * lre;
                        wp[k] = (float)(e - y); wp[k + 1] = (float)(lb + mp[k + 1] + 0.4); wp[k + 2] = (float)(-(n - x));
                    }
                    s.Pools.Add((cr, cg, cb, ca, wp, fade));
                }
            }
            if (isSel && separateSelected) { s.SelectedRunwayNumbers = rwyNums; continue; }   // pezzi uno per uno (PieceParts)
            foreach (var f in lay.Features)
            {
                if (s.Triangles > MaxTriangles) return s;
                var (cx, cy) = EditorData.ToWorld(o, f.N, f.E);
                if (Math.Abs(cx - x) > half || Math.Abs(cy - y) > half) continue;
                double H = (o.Heading + f.Heading) * Math.PI / 180;
                double fn = Math.Cos(H), fe = Math.Sin(H), rn = -Math.Sin(H), re = Math.Cos(H);   // avanti, destra (nord, est)
                double baseUp = s.PieceBase(d, o, f, cx, cy, exaggeration);
                foreach (var (t, pos, uv) in PieceParts(d, o, f, rwyNums, s))
                {
                    var g = s.GroupFor(t, isSel);
                    for (int i = 0; i < pos.Length; i += 3)
                    {
                        // pos: x destra, y su, z indietro (PieceParts)
                        double right = pos[i], up = pos[i + 1], fwd = -pos[i + 2];
                        double n = cx + fwd * fn + right * rn, e = cy + fwd * fe + right * re;
                        g.Pos.Add((float)(e - y)); g.Pos.Add((float)(baseUp + up)); g.Pos.Add((float)(-(n - x)));
                    }
                    if (t != null) g.Uv.AddRange(uv);
                    s.Triangles += pos.Length / 9;
                }
            }
        }
        return s;
    }

    /// <summary>Numeri di pista dell'obiettivo scelto (per PieceParts quando i suoi pezzi si disegnano uno per uno).</summary>
    public Dictionary<int, int> SelectedRunwayNumbers { get; private set; } = new();

    /// <summary>Quota (piedi, relativa al terreno al centro della scena) della base di un pezzo messo in (cx, cy) del teatro.</summary>
    public double PieceBase(EditorData d, EdObjective o, EdFeature f, double cx, double cy, double exaggeration = 1)
    {
        // pezzi piatti uno sopra l'altro (terreno, piazzali e vie di rullaggio, pista, numeri) leggermente sollevati in
        // quest'ordine: nell'anteprima, senza l'ordine di disegno di BMS, superfici alla stessa quota sfarfallano
        double dz = f.Z + (d.ExtraZ.TryGetValue(o.Id, out var xz) && xz.TryGetValue(f.Num, out double ez) ? ez : 0);   // OffsetZ: + = in giu'
        return (Ground(cx, cy) - GroundAtCenter) * exaggeration + Lift(f.Name) - dz;
    }

    /// <summary>
    /// Triangoli di un pezzo nel suo sistema: x = destra, y = su, z = indietro (come WPF con il pezzo rivolto verso -z).
    /// Modello BML con le texture; se manca, scatola con la sagoma del Parent.dat (s.MissingModels++ se s non e' null).
    /// </summary>
    public static List<(string? Tex, float[] Pos, float[] Uv)> PieceParts(EditorData d, EdObjective o, EdFeature f, Dictionary<int, int> rwyNums, PreviewScene? s = null)
    {
        var res = new List<(string?, float[], float[])>();
        var m = d.Model(f.CtIdx);
        if (m != null && m.Parts.Count > 0)
        {
            // numero di pista: il pezzo QFU ha 36 serie di texture (KoreaObj 5000 = "36", 5001 = "01" ...); BMS sceglie quella del numero
            int qfu = f.Num >= 0 && rwyNums.TryGetValue(f.Num, out int rwyNo) && f.Name.Contains("QFU", StringComparison.OrdinalIgnoreCase) ? rwyNo % 36 : -1;
            foreach (var (tex0, pos, uv) in m.Parts)
            {
                string? tex = tex0;
                if (qfu > 0 && tex != null && int.TryParse(Path.GetFileNameWithoutExtension(tex), out int tid))
                    tex = Path.Combine(Path.GetDirectoryName(tex)!, (tid + qfu).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".dds");
                string? t = tex != null && uv.Length > 0 && d.Texture(tex) != null ? tex : null;
                // vertici del modello: x destra, y su, z avanti
                var p = new float[pos.Length];
                for (int i = 0; i < pos.Length; i += 3) { p[i] = pos[i]; p[i + 1] = pos[i + 1]; p[i + 2] = -pos[i + 2]; }
                res.Add((t, p, t != null ? uv : Array.Empty<float>()));
            }
            return res;
        }
        // senza superfici (luci di pista: la loro sagoma copre tutto il campo) o pezzo grande illeggibile: niente;
        // pezzo piccolo illeggibile: scatola alta 30 piedi con la sagoma del Parent.dat
        double ext = Math.Max(f.MaxX - f.MinX, f.MaxY - f.MinY);
        if (d.IsEmptyModel(f.CtIdx) || ext > 300) return res;
        if (s != null) s.MissingModels++;
        var b = new List<float>();
        Box((fwd, right, up) => { b.Add((float)right); b.Add((float)up); b.Add((float)-fwd); }, f.MinX, f.MaxX, f.MinY, f.MaxY, 30);
        res.Add((null, b.ToArray(), Array.Empty<float>()));
        return res;
    }

    private static double Lift(string name) =>
        name.Contains("QFU", StringComparison.OrdinalIgnoreCase) ? 1.6
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(rwy|runway|thr|tdz|stopway)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 1.0
        : System.Text.RegularExpressions.Regex.IsMatch(name, @"taxiway|taxi way|parking|apron|ramp|pad", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 0.5
        : 0.3;

    private Group GroupFor(string? tex, bool sel)
    {
        if (!Groups.TryGetValue((tex ?? "", sel), out var g)) Groups[(tex ?? "", sel)] = g = new Group { Tex = tex, Selected = sel };
        return g;
    }

    /// <summary>Porta alla quota dello spianamento i punti della griglia dentro il poligono (piedi del teatro).</summary>
    private void Level(double level, List<(double X, double Y)> poly, double pct = 100, float[,]? orig = null)
    {
        if (Heights == null) return;
        int n = GridN;
        double minX = poly.Min(p => p.X), maxX = poly.Max(p => p.X), minY = poly.Min(p => p.Y), maxY = poly.Max(p => p.Y);
        for (int j = 0; j < n; j++)
        {
            double north = CenterX + Half - 2 * Half * j / (n - 1);
            if (north < minX || north > maxX) continue;
            for (int i = 0; i < n; i++)
            {
                double east = CenterY - Half + 2 * Half * i / (n - 1);
                if (east < minY || east > maxY || !Inside(poly, north, east)) continue;
                Heights[j, i] = pct >= 99.9 || orig == null ? (float)level : (float)(orig[j, i] + (level - orig[j, i]) * pct / 100);
            }
        }
    }

    private static bool Inside(List<(double X, double Y)> poly, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }

    /// <summary>Scatola (12 triangoli) dalla sagoma del Parent.dat, alta h piedi.</summary>
    private static void Box(Action<double, double, double> P, double x0, double x1, double y0, double y1, double h)
    {
        (double, double, double)[] c = { (x0, y0, 0), (x1, y0, 0), (x1, y1, 0), (x0, y1, 0), (x0, y0, h), (x1, y0, h), (x1, y1, h), (x0, y1, h) };
        int[] idx = { 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
        foreach (int i in idx) P(c[i].Item1, c[i].Item2, c[i].Item3);
    }

    /// <summary>Quota del terreno (piedi) in un punto del teatro, dalla griglia (0 se manca la heightmap).</summary>
    public double Ground(double x, double y)
    {
        if (Heights == null) return 0;
        int n = GridN;
        double fi = (y - (CenterY - Half)) / (2 * Half) * (n - 1), fj = (CenterX + Half - x) / (2 * Half) * (n - 1);
        int i = Math.Clamp((int)Math.Floor(fi), 0, n - 2), j = Math.Clamp((int)Math.Floor(fj), 0, n - 2);
        double ti = Math.Clamp(fi - i, 0, 1), tj = Math.Clamp(fj - j, 0, 1);
        double a = Heights[j, i], b = Heights[j, i + 1], c = Heights[j + 1, i], dd = Heights[j + 1, i + 1];
        return (a * (1 - ti) + b * ti) * (1 - tj) + (c * (1 - ti) + dd * ti) * tj;
    }
}
