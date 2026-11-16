using System.IO;

namespace ArtEditor;

/// <summary>Una schermata di BMS: le finestre (per gruppo) che BMS mostra insieme quando si sceglie una voce del menu.</summary>
/// <param name="Legacy">schermata del vecchio formato 1024×768 (ACMI): le finestre si mettono al centro dello schermo Full HD</param>
public sealed record UiScreen(string Name, int[] Groups, bool WithMainMenu, string? MenuImage, bool Checked, bool Legacy = false)
{
    public override string ToString() => Name + (Checked ? "" : "  (layout not verified yet)");
}

/// <summary>Un elemento disegnato: dove sta, cosa mostra e da quale file (e riga) viene.</summary>
public sealed class UiElement
{
    public ScfWindow Win { get; init; } = null!;
    public ScfItem Item { get; init; } = null!;
    public string Kind => Item.Kind;
    public Rect32 Rect { get; set; }
    public string? Image { get; set; }
    public string? TextKey { get; set; }
    public string? Text { get; set; }
    public string? Font { get; set; }
    public (byte R, byte G, byte B) Color { get; set; } = (230, 230, 230);
    /// <summary>Contenuto disegnato da BMS mentre gira (mappe, elenchi, caselle di testo): qui solo un riquadro.</summary>
    public bool Dynamic { get; set; }
    public string Align { get; set; } = "left";
    public bool VCenter { get; set; }
    public bool Wrap { get; set; }
    /// <summary>Elemento nascosto (C_BIT_INVISIBLE): non si disegna; in modalita' Edit si vede il suo riquadro per poterlo rimostrare.</summary>
    public bool Hidden { get; set; }
    /// <summary>Nascosto dall'editor (per es. per un Art PvP), non da BMS.</summary>
    public bool HiddenByEditor => Hidden && Item.Get("EDITORHIDDEN") != null;
    public string Label => $"{Kind} {Item.Id}".Trim();
}

/// <summary>
/// Le schermate principali (collegate come nel menu di BMS) e la costruzione della scena: finestre in ordine di profondita',
/// elementi con posizione assoluta sullo schermo 1920×1080, poi il disegno in un'immagine BGRA (con i font .bft di BMS).
/// Il collegamento tra le schermate non e' nei file (lo decide BMS): qui e' scritto a mano, per gruppo di finestre.
/// </summary>
public static class UiScreens
{
    public const string MainWin = @"_FHD\Main\Main_Win.scf";

    public static readonly UiScreen[] All =
    {
        new("Main menu", Array.Empty<int>(), true, null, true),
        new("Instant Action", new[] { 1000 }, true, "MAIN_IA", true),
        new("Dogfight", new[] { 2000 }, true, "MAIN_DGFT", true),
        new("Dogfight (game)", new[] { 2020 }, false, null, false),
        new("Tactical Engagement", new[] { 3000 }, true, "MAIN_TE", true),
        new("TE (mission selected)", new[] { 3000, 3050 }, true, null, false),
        new("TE (running)", new[] { 3025, 3300 }, false, null, false),
        new("TE Builder / Edit", new[] { 3900, 3200 }, false, null, false),   // TE BUILDER ed EDIT aprono la stessa schermata (barra 3900 + mappa 3200)
        new("Campaign", new[] { 4000, 4050 }, true, "MAIN_CAMP", true),
        new("Campaign (running)", new[] { 200, 6100 }, false, null, false),
        new("Campaign (intel)", new[] { 200, 6200 }, false, null, false),
        new("Multiplayer", new[] { 6001 }, true, "MAIN_COMMS", true),   // la finestra COMMS (rubrica dei server); la chat (6000) si apre dopo
        new("Tactical Reference", new[] { 10000 }, true, "MAIN_TR", true),
        new("Logbook", new[] { 5000 }, true, "MAIN_LOGBOOK", false),
        // ACMI: in BMS 4.38 il pulsante mostra solo un messaggio (serve Tacview); le vecchie finestre 1024×768 non si usano piu'
        new("Theater", new[] { 3203 }, true, "MAIN_THEATER", true),
        new("Setup", new[] { 8000 }, false, "MAIN_SETUP", true),
    };

    /// <summary>
    /// Pulsanti che in BMS aprono un'altra schermata (oltre al menu principale): schermata di partenza, ID del pulsante → schermata.
    /// Servono per navigare nell'editor visuale come in BMS (TE BUILDER, EDIT, COMMIT, INTEL...).
    /// </summary>
    public static readonly (string From, string Id, string To)[] Links =
    {
        ("Tactical Engagement", "TAC_NEW_MISSION", "TE Builder / Edit"),
        ("Tactical Engagement", "TAC_EDIT", "TE Builder / Edit"),
        ("Tactical Engagement", "SINGLE_COMMIT_CTRL", "TE (running)"),
        ("TE (mission selected)", "TAC_NEW_MISSION", "TE Builder / Edit"),
        ("TE (mission selected)", "TAC_EDIT", "TE Builder / Edit"),
        ("TE (mission selected)", "SINGLE_COMMIT_CTRL", "TE (running)"),
        ("Campaign", "SINGLE_COMMIT_CTRL", "Campaign (running)"),
        ("Campaign (running)", "INTEL_MAIN_CTRL", "Campaign (intel)"),
        ("Campaign (intel)", "MISSION_MAIN_CTRL", "Campaign (running)"),
        ("Dogfight", "DF_OFFLINE_CTRL", "Dogfight (game)"),
    };

    /// <summary>Aree (finestre) che aprono un'altra schermata cliccandoci: la lista delle missioni del TE → missione scelta.</summary>
    public static readonly (string From, string WindowId, string To)[] AreaLinks =
    {
        ("Tactical Engagement", "TAC_MISSION_WIN", "TE (mission selected)"),
    };

    /// <summary>Le finestre della schermata, in ordine di disegno.</summary>
    public static List<ScfWindow> Windows(UiArt ui, UiScreen s)
    {
        var res = new List<ScfWindow>();
        if (s.WithMainMenu) res.AddRange(ui.Windows(MainWin));
        foreach (var w in ui.AllWindows())
            if (s.Groups.Contains(w.Group) && !w.File.Equals(MainWin, StringComparison.OrdinalIgnoreCase) && Shown(w))
            {
                // finestre a 1024×768 (vecchio formato): al centro dello schermo 1920×1080
                if (s.Legacy && !w.Centered) { w.X = w.FileX + (ScreenW - 1024) / 2; w.Y = w.FileY + (ScreenH - 768) / 2; }
                res.Add(w);
            }
        // ordine di disegno: il menu sotto; poi le finestre-sfondo (piu' di meta' schermo), le piu' grandi prima; poi le altre per
        // profondita' ([DEPTH] piu' alto = piu' in fondo: l'orologio di Instant Action a 0 sta sopra le finestre a 1) e grandezza.
        // Verificato su BMS: Instant Action, Dogfight, TE, Campaign e le schermate della campagna e del TE in corso.
        static bool Main(ScfWindow w) => w.File.Equals(MainWin, StringComparison.OrdinalIgnoreCase);
        static bool Back(ScfWindow w) => (long)w.W * w.H >= (long)ScreenW * ScreenH / 2;
        return res.Select((w, i) => (w, i))
                  .OrderBy(x => Main(x.w) ? 0 : Back(x.w) ? 1 : 2)
                  .ThenBy(x => Back(x.w) ? 0 : -x.w.Depth)
                  .ThenByDescending(x => (long)x.w.W * x.w.H).ThenBy(x => x.i).Select(x => x.w).ToList();
    }

    /// <summary>
    /// Finestre che BMS non mostra all'apertura della schermata: nascoste ([FLAGBITON] C_BIT_INVISIBLE sulla finestra), di misura zero
    /// (barre spente) o di prova (la finestra "cheats" della campagna, LOGFONT_WIN).
    /// </summary>
    private static bool Shown(ScfWindow w)
    {
        if (w.Head.Flag("C_BIT_INVISIBLE")) return false;
        if (w.W <= 0 || w.H <= 0) return false;
        if (w.Id.Equals("LOGFONT_WIN", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int I(string[]? a, int k, int def = 0) => a != null && a.Length > k && int.TryParse(a[k], out int v) ? v : def;
    private static (byte, byte, byte)? Col(string[]? a, int k = 0) =>
        a != null && a.Length >= k + 3 && byte.TryParse(a[k], out var r) && byte.TryParse(a[k + 1], out var g) && byte.TryParse(a[k + 2], out var b) ? (r, g, b) : null;

    private static readonly HashSet<string> Visual = new(StringComparer.OrdinalIgnoreCase) { "BITMAP", "TILE", "FILL", "LINE", "BOX", "TEXT", "BUTTON" };

    /// <summary>Gli elementi della schermata con la posizione sullo schermo. Gli elementi nascosti (C_BIT_INVISIBLE, gruppi alternativi) non ci sono.</summary>
    public static List<UiElement> Build(UiArt ui, IEnumerable<ScfWindow> wins, bool includeHidden = false)
    {
        var res = new List<UiElement>();
        foreach (var w in wins)
        {
            var els = new List<(UiElement E, int? Cluster)>();
            foreach (var it in w.Items)
            {
                bool hidden = it.Flag("C_BIT_INVISIBLE");
                // nascosti da BMS: non si mostrano; nascosti dall'editor: solo in modalita' Edit (per rimostrarli)
                if (hidden && (!includeHidden || it.Get("EDITORHIDDEN") == null)) continue;
                var e = Element(ui, w, it);
                if (e == null) continue;
                e.Hidden = hidden;
                els.Add((e, it.Get("CLUSTER") is { } cl ? I(cl, 0) : null));
            }
            var show = VisibleClusters(w, els);
            var seen = new List<Rect32>();
            foreach (var (e, c) in els)
            {
                if (c is int ci && !show.Contains(ci)) continue;
                // pulsanti alternativi nello stesso punto (es. COMMIT e COMMIT ONLINE): BMS ne mostra uno, si tiene il primo
                if (e.Kind == "BUTTON" && !e.Hidden)
                {
                    if (seen.Any(r => r.X == e.Rect.X && r.Y == e.Rect.Y && r.W == e.Rect.W && r.H == e.Rect.H)) continue;
                    seen.Add(e.Rect);
                }
                res.Add(e);
            }
        }
        return res;
    }

    /// <summary>
    /// Gruppi ([CLUSTER]) che BMS accende e spegne mentre gira. Gruppi che occupano lo stesso spazio sono alternative (es. la descrizione
    /// di ogni campagna): si vede il primo. Gruppi in punti diversi si vedono tutti (es. lista e OK nella scelta del teatro). Un gruppo
    /// solo nella finestra = parte che BMS mostra solo in certi casi (es. "Enable training script", solo per un TE di addestramento):
    /// resta nascosta, salvo un pulsante attivo ([STATE] 1) che lo accende.
    /// </summary>
    private static HashSet<int> VisibleClusters(ScfWindow w, List<(UiElement E, int? Cluster)> els)
    {
        // tutti i gruppi della finestra, anche quelli fatti solo di elementi che BMS riempie (liste) e che qui non si disegnano
        var ids = w.Items.Select(it => it.Get("CLUSTER")).Where(c => c != null).Select(c => I(c, 0)).Distinct().OrderBy(c => c).ToList();
        var show = new HashSet<int>();
        if (ids.Count == 1)
        {
            if (w.Items.Any(it => I(it.Get("STATE"), 0) == 1 && it.All("USERDATA").Any(u => u.Length > 1 && I(u, 1) == ids[0]))) show.Add(ids[0]);
            return show;
        }
        var box = ids.Where(c => els.Any(x => x.Cluster == c)).ToDictionary(c => c, c =>
        {
            var rs = els.Where(x => x.Cluster == c).Select(x => x.E.Rect).ToList();
            int x0 = rs.Min(r => r.X), y0 = rs.Min(r => r.Y), x1 = rs.Max(r => r.X + r.W), y1 = rs.Max(r => r.Y + r.H);
            return (x0, y0, x1, y1);
        });
        bool Overlap(int a, int b) => box.ContainsKey(a) && box.ContainsKey(b) && box[a].x0 < box[b].x1 && box[b].x0 < box[a].x1 && box[a].y0 < box[b].y1 && box[b].y0 < box[a].y1;
        var done = new HashSet<int>();
        foreach (var c in ids)
        {
            if (done.Contains(c)) continue;
            // c e' il piu' basso del suo insieme di gruppi sovrapposti: si vede; gli altri dell'insieme no
            show.Add(c);
            var stack = new Stack<int>(); stack.Push(c); done.Add(c);
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                foreach (var o in ids) if (!done.Contains(o) && Overlap(k, o)) { done.Add(o); stack.Push(o); }
            }
        }
        return show;
    }

    private static UiElement? Element(UiArt ui, ScfWindow w, ScfItem it)
    {
        var setup = it.Get("SETUP") ?? Array.Empty<string>();
        var xywh = it.Get("XYWH");
        var xy = it.Get("XY");
        var wh = it.Get("WH");
        var e = new UiElement { Win = w, Item = it, Font = it.Get("FONT")?.FirstOrDefault() ?? w.Font };
        int x, y, ww, hh;
        if (xywh != null) { x = I(xywh, 0); y = I(xywh, 1); ww = I(xywh, 2); hh = I(xywh, 3); }
        else if (xy != null) { x = I(xy, 0); y = I(xy, 1); ww = I(wh, 0); hh = I(wh, 1); }
        else if (setup.Length >= 4 && int.TryParse(setup[2], out x) && int.TryParse(setup[3], out y)) { ww = 0; hh = 0; }
        else { x = 0; y = 0; ww = I(wh, 0); hh = I(wh, 1); }

        switch (it.Kind)
        {
            case "BITMAP":
            case "TILE":
                e.Image = setup.Skip(2).LastOrDefault(s => ui.FindImage(s) != null) ?? (setup.Length >= 3 && ui.FindImage(setup[^1]) != null ? setup[^1] : null);
                if (e.Image == null) return null;
                if (ww == 0 || hh == 0) { var p = ui.Image(e.Image); ww = p?.W ?? 0; hh = p?.H ?? 0; }
                break;
            case "FILL":
            case "LINE":
            case "BOX":
                // senza [COLOR] BMS non disegna nulla (es. la barra della vecchia toolbar della campagna, Cpm_Tool.scf)
                if (Col(it.Get("COLOR")) is not { } col) return null;
                e.Color = col;
                break;
            case "TEXT":
                e.TextKey = it.Get("SETTEXT")?.FirstOrDefault();
                e.Text = e.TextKey != null ? ui.Text(e.TextKey)?.Text ?? e.TextKey : it.Get("TEXT")?.FirstOrDefault();
                // testo con un nome (non NID) e vuoto: lo scrive BMS mentre gira (nome della base, dello squadrone...)
                if (it.Id is { } tid && !tid.Equals("NID", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(e.Text)) e.Dynamic = true;
                e.Color = Col(it.Get("FGCOLOR")) ?? Col(it.Get("COLOR")) ?? (230, 230, 230);
                // i flag (C_BIT_LEFT / RIGHT / HCENTER) contano piu' del tipo di [SETUP]
                e.Align = it.Flag("C_BIT_LEFT") ? "left" : it.Flag("C_BIT_HCENTER") ? "center" : it.Flag("C_BIT_RIGHT") ? "right"
                        // nei testi di BMS il tipo dice da che parte cresce il testo: C_TYPE_RIGHT = parte da X verso destra
                        // (allineato a sinistra, es. le etichette del Logbook), C_TYPE_LEFT = finisce in X
                        // C_TYPE_CENTER centra il testo nella sua larghezza ([WH]/[XYWH]); senza larghezza parte da X (nota rossa della
                        // scelta del teatro, verificato su BMS)
                        : setup.Any(s => s.Equals("C_TYPE_CENTER", StringComparison.OrdinalIgnoreCase)) ? (ww > 0 ? "center" : "left")
                        : setup.Any(s => s.Equals("C_TYPE_LEFT", StringComparison.OrdinalIgnoreCase)) ? "right" : "left";
                e.VCenter = it.Flag("C_BIT_VCENTER");
                e.Wrap = it.Flag("C_BIT_WORDWRAP") && ww > 0;
                if (e.Wrap && !it.Flag("C_BIT_HCENTER")) e.Align = "left";    // i testi a capo di BMS partono da sinistra
                if (ww == 0 || hh == 0)
                {
                    var f = ui.Font(e.Font);
                    int tw = f?.Width(e.Text ?? "") ?? 0, th = f?.Height ?? 14;
                    ww = Math.Max(4, tw); hh = th;
                    if (e.Align == "center") x -= ww / 2; else if (e.Align == "right") x -= ww;
                    if (e.VCenter) y -= hh / 2;
                }
                break;
            case "BUTTON":
                e.Image = it.Get("IMAGES")?.FirstOrDefault(s => !s.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                          ?? it.All("BUTTONIMAGE").Select(a => a.Length > 1 ? a[1] : null).FirstOrDefault(s => s != null && !s.Equals("NULL", StringComparison.OrdinalIgnoreCase));
                if (e.Image != null && ui.Image(e.Image) is { } bp && (ww == 0 || hh == 0))
                {
                    ww = bp.W; hh = bp.H;
                    // pulsante-immagine con i flag di centratura: l'immagine e' centrata nel punto ([XY] o le coordinate di [SETUP]),
                    // es. la foto del pilota nel Logbook e le voci del menu principale (C_BIT_VCENTER: 32 pixel piu' in alto, verificato su BMS)
                    if (xywh == null)
                    {
                        if (it.Flag("C_BIT_HCENTER")) x -= ww / 2;
                        if (it.Flag("C_BIT_VCENTER")) y -= hh / 2;
                    }
                }
                e.TextKey = it.Get("LABELALL")?.FirstOrDefault() ?? it.All("BUTTONTEXT").Select(a => a.Length > 1 ? a[1] : null).FirstOrDefault();
                if (e.TextKey != null) e.Text = ui.Text(e.TextKey)?.Text ?? e.TextKey;
                // pulsante di una campagna spenta (riga [IMAGES] commentata): BMS non lo mostra
                if (e.Image == null && setup.Length > 1 && setup[1].Equals("C_TYPE_CUSTOM", StringComparison.OrdinalIgnoreCase) && it.Get("WH") == null) return null;
                e.Color = Col(it.All("TEXTCOLOR").FirstOrDefault(a => a.Length > 0 && (a[0] == "C_STATE_0" || a[0] == "C_STATE_UP")), 1) ?? Col(it.Get("UPCOLOR")) ?? Col(it.Get("FGCOLOR")) ?? (230, 230, 230);
                e.Align = it.All("LABELFLAGBITON").Any(a => a.Contains("C_BIT_RIGHT")) ? "right" : it.All("LABELFLAGBITON").Any(a => a.Contains("C_BIT_LEFT")) ? "left" : "center";
                if (ww == 0 && hh == 0 && e.Text != null && ui.Font(e.Font) is { } bf) { ww = bf.Width(e.Text); hh = bf.Height; }
                if (it.Flag("C_BIT_VCENTER") && e.Image == null) y -= hh / 2;
                break;
            default:
                if (ww <= 0 || hh <= 0) return null;
                e.Dynamic = true;
                break;
        }
        if (ww <= 0 || hh <= 0) return null;
        if (e.Text is { } tx && (tx.Equals("TXT_EMPTY", StringComparison.OrdinalIgnoreCase) || tx.Equals("TXT_NULL", StringComparison.OrdinalIgnoreCase))) e.Text = "";
        // area della finestra a cui l'elemento appartiene
        // solo gli elementi con [CLIENT] n sono relativi all'area n; gli altri alla finestra
        var client = it.Get("CLIENT") is { } cn && w.Clients.TryGetValue(I(cn, 0), out var ca) ? ca : (0, 0);
        e.Rect = new Rect32(w.X + client.Item1 + x, w.Y + client.Item2 + y, ww, hh);
        return e;
    }

    // ───────────────────────── disegno ─────────────────────────

    public const int ScreenW = 1920, ScreenH = 1080;

    /// <summary>Disegna gli elementi in un'immagine BGRA 1920×1080.</summary>
    public static byte[] Render(UiArt ui, List<UiElement> els)
    {
        var c = new byte[ScreenW * ScreenH * 4];
        for (int i = 3; i < c.Length; i += 4) c[i] = 255;
        foreach (var e in els)
        {
            if (e.Hidden) continue;
            var r = e.Rect;
            switch (e.Kind)
            {
                case "BITMAP":
                case "TILE":
                    if (e.Image != null && ui.Image(e.Image) is { } p) Blit(c, p, r, stretch: true);
                    break;
                case "FILL":
                case "LINE":
                    Fill(c, r, e.Color);
                    break;
                case "BOX":
                    Fill(c, new Rect32(r.X, r.Y, r.W, 1), e.Color); Fill(c, new Rect32(r.X, r.Y + r.H - 1, r.W, 1), e.Color);
                    Fill(c, new Rect32(r.X, r.Y, 1, r.H), e.Color); Fill(c, new Rect32(r.X + r.W - 1, r.Y, 1, r.H), e.Color);
                    break;
                case "TEXT":
                    if (e.Dynamic) Dashed(c, r, (110, 160, 200)); else DrawText(ui, c, e, r);
                    break;
                case "BUTTON":
                    if (e.Image != null && ui.Image(e.Image) is { } bp) Blit(c, bp, new Rect32(r.X, r.Y, bp.W, bp.H), stretch: false);
                    if (!string.IsNullOrEmpty(e.Text)) DrawLabel(ui, c, e, r);
                    break;
                default:
                    if (e.Dynamic) Dashed(c, r, (110, 160, 200));
                    break;
            }
        }
        return c;
    }

    private static void Blit(byte[] c, Pic p, Rect32 r, bool stretch)
    {
        int w = stretch ? r.W : p.W, h = stretch ? r.H : p.H;
        for (int y = 0; y < h; y++)
        {
            int py = r.Y + y; if (py < 0 || py >= ScreenH) continue;
            int sy = stretch ? Math.Min(p.H - 1, y * p.H / Math.Max(1, h)) : y;
            for (int x = 0; x < w; x++)
            {
                int px = r.X + x; if (px < 0 || px >= ScreenW) continue;
                int sx = stretch ? Math.Min(p.W - 1, x * p.W / Math.Max(1, w)) : x;
                int si = 4 * (sy * p.W + sx);
                if (p.Bgra[si + 3] == 0) continue;
                int di = 4 * (py * ScreenW + px);
                c[di] = p.Bgra[si]; c[di + 1] = p.Bgra[si + 1]; c[di + 2] = p.Bgra[si + 2]; c[di + 3] = 255;
            }
        }
    }

    private static void Fill(byte[] c, Rect32 r, (byte R, byte G, byte B) col)
    {
        for (int y = Math.Max(0, r.Y); y < Math.Min(ScreenH, r.Y + r.H); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(ScreenW, r.X + r.W); x++)
            {
                int i = 4 * (y * ScreenW + x);
                c[i] = col.B; c[i + 1] = col.G; c[i + 2] = col.R; c[i + 3] = 255;
            }
    }

    private static void Dashed(byte[] c, Rect32 r, (byte R, byte G, byte B) col)
    {
        for (int x = 0; x < r.W; x += 1)
            if ((x / 6) % 2 == 0) { Fill(c, new Rect32(r.X + x, r.Y, 1, 1), col); Fill(c, new Rect32(r.X + x, r.Y + r.H - 1, 1, 1), col); }
        for (int y = 0; y < r.H; y += 1)
            if ((y / 6) % 2 == 0) { Fill(c, new Rect32(r.X, r.Y + y, 1, 1), col); Fill(c, new Rect32(r.X + r.W - 1, r.Y + y, 1, 1), col); }
    }

    private static void DrawText(UiArt ui, byte[] c, UiElement e, Rect32 r)
    {
        var f = ui.Font(e.Font);
        if (f == null || string.IsNullOrEmpty(e.Text)) return;
        var lines = e.Wrap ? WrapText(f, e.Text, r.W) : new List<string> { e.Text };
        int y = r.Y;
        foreach (var l in lines)
        {
            int tw = f.Width(l);
            int x = e.Align == "center" ? r.X + (r.W - tw) / 2 : e.Align == "right" ? r.X + r.W - tw : r.X;
            f.Draw(c, ScreenW, ScreenH, l, x, y, e.Color);
            y += f.Height;
        }
    }

    private static void DrawLabel(UiArt ui, byte[] c, UiElement e, Rect32 r)
    {
        var f = ui.Font(e.Font);
        if (f == null) return;
        var off = e.Item.All("TEXTOFFSET").FirstOrDefault(a => a.Length >= 3 && (a[0] == "C_STATE_UP" || a[0] == "C_STATE_0"));
        int dx = I(off, 1), dy = I(off, 2);
        int tw = f.Width(e.Text!);
        int x = e.Align == "right" ? r.X + r.W - tw + dx : e.Align == "left" ? r.X + dx : r.X + (r.W - tw) / 2 + dx;
        int y = r.Y + (r.H - f.Height) / 2 + dy;
        f.Draw(c, ScreenW, ScreenH, e.Text!, x, y, e.Color);
    }

    private static List<string> WrapText(BftFont f, string s, int w)
    {
        var res = new List<string>();
        var cur = "";
        foreach (var word in s.Split(' '))
        {
            string t = cur.Length == 0 ? word : cur + " " + word;
            if (f.Width(t) > w && cur.Length > 0) { res.Add(cur); cur = word; } else cur = t;
        }
        if (cur.Length > 0) res.Add(cur);
        return res;
    }
}
