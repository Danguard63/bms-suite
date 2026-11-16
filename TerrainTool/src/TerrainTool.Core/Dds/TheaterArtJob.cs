using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Art del teatro per la 4.38: si parte SEMPRE dai file della 4.38 (Data\Art, solo lettura: quelli del vecchio add-on sono
/// della 4.36 e non hanno le voci nuove dell'interfaccia) e ci si mettono dentro solo le immagini del teatro prese dall'Art
/// del vecchio add-on: bandiere (gli slot della Korea che il teatro ha ridisegnato), mappe piccole, sfondo del menu,
/// immagini delle campagne, mappe intel, patch degli squadroni e coccarde; i testi di Theater.irc (paesi, squadre,
/// campagne). Units.irc resta quello della 4.38 (i nomi degli aerei sono del database 4.38).
/// Scrive in Output\Art\Art (Assemble lo copia nella cartella artdir dell'add-on). La mappa piccola MAP_SMALL del lavoro
/// 2D map resta.
/// </summary>
public sealed class TheaterArtJob : TextureJob
{
    public override string Id => "theaterart";
    public override string Name => "Theater art (flags, backgrounds, patches)";
    public override string Description =>
        "UI art of the theater for BMS 4.38: starts from the 4.38 Art files (all the new UI entries) and puts in only the theater's own images " +
        "from the old add-on's Art: flags, small maps, main menu background, campaign pictures, intel maps, squadron patches and roundels, " +
        "plus country/team/campaign names from Theater.irc. Output\\Art\\Art; Build theater copies it into the add-on.";
    public override string Output => @"..\Art\_FHD\Main.rsc";

    /// <summary>Gli slot delle bandiere che un teatro può ridisegnare (numero di bandiera della squadra → nome).</summary>
    /// <summary>
    /// Bandiere disegnate qui, per le squadre del teatro che non hanno una bandiera in BMS (che ha solo i posti 0-7):
    /// (posto, numero della bandiera da mettere nella squadra, disegno della bandiera orizzontale: x,y in 0..1 → colore).
    /// Ogni squadra deve avere un posto suo: nel TE BMS sposta una squadra con una bandiera gia' usata sul primo posto libero
    /// (SetupTeamListValues), e il posto 0 e' del team neutro XX. UAE: posto 7 (NATO, che il Mideast non usa), striscia rossa
    /// verticale e poi verde, bianco, nero.
    /// </summary>
    public static readonly (string Slot, byte Flag, Func<double, double, (byte R, byte G, byte B)> Draw)[] DrawnFlags =
    {
        ("NATO", 7, (x, y) => x < 0.25 ? ((byte)200, (byte)16, (byte)46) : y < 1.0 / 3 ? ((byte)0, (byte)115, (byte)47) : y < 2.0 / 3 ? ((byte)248, (byte)248, (byte)248) : ((byte)0, (byte)0, (byte)0)),
    };

    /// <summary>Immagine RGB w×h della bandiera: orizzontale, o verticale (ruotata di 90° in senso orario, l'asta in alto, come
    /// le OOB_FLAGS di BMS); la versione _DRK è più scura.</summary>
    public static byte[] FlagImage(Func<double, double, (byte R, byte G, byte B)> draw, int w, int h, bool vertical, bool dark)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // verticale: il lato dell'asta (sinistra) va in alto, il lato superiore va a destra
                double fx = vertical ? (y + 0.5) / h : (x + 0.5) / w;
                double fy = vertical ? (w - x - 0.5) / w : (y + 0.5) / h;
                var (r, g, bl) = draw(fx, fy);
                if (dark) { r = (byte)(r * 0.55); g = (byte)(g * 0.55); bl = (byte)(bl * 0.55); }
                int i = 3 * (y * w + x); rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = bl;
            }
        return rgb;
    }

    /// <summary>Finestre del TE con il riquadro "Team Territory" (mappa IA_MAP_TGA + livello TAC_OVERLAY).</summary>
    public static readonly string[] OverlayScf = { @"_FHD\TacEng\Tac_Load\Tac_Sua.scf", @"_FHD\TacEng\Tac_Schd\Tac_Psua.scf" };

    /// <summary>Il testo del .scf senza il blocco [BITMAP] di TAC_OVERLAY, con un commento al suo posto (null se non c'e').</summary>
    public static string? RemoveOverlay(string scf)
    {
        var lines = scf.Replace("\r\n", "\n").Split('\n').ToList();
        int i = lines.FindIndex(l => l.TrimStart().StartsWith("[SETUP] TAC_OVERLAY", StringComparison.OrdinalIgnoreCase));
        if (i < 1 || !lines[i - 1].Trim().Equals("[BITMAP]", StringComparison.OrdinalIgnoreCase)) return null;
        int end = i + 1;
        while (end < lines.Count && lines[end].Trim().Length > 0 && !lines[end].TrimStart().StartsWith("[BITMAP]") && !lines[end].TrimStart().StartsWith("[BOX]")) end++;
        lines.RemoveRange(i - 1, end - (i - 1));
        lines.Insert(i - 1, "# TAC_OVERLAY removed by TerrainTool: the occupation map does not fit this theater size");
        return string.Join("\r\n", lines);
    }

    /// <summary>Controlli del pennello del territorio nella finestra Teams del TE Builder (Tac_Team.scf).</summary>
    public static readonly string[] TerritoryTools = { "TAC_MAP_TE", "DRAW_TERRITORY", "ERASE_TERRITORY", "CLEAR_TERRITORY", "UNDO_TERRITORY" };

    /// <summary>
    /// Tac_Team.scf senza la mappa colorata e senza gli strumenti di disegno (resta la mappa del teatro). BMS li cerca
    /// per numero e salta quelli che mancano. Null se uno dei blocchi attesi manca (file della 4.38 cambiato).
    /// </summary>
    public static string? RemoveTerritoryTools(string scf)
    {
        var lines = scf.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var id in TerritoryTools)
        {
            int i = lines.FindIndex(l => Regex.IsMatch(l.Trim(), @"^\[SETUP\]\s+" + id + @"\b"));
            if (i < 1 || !lines[i - 1].Trim().Equals("[BUTTON]", StringComparison.OrdinalIgnoreCase)) return null;
            int end = i + 1;
            while (end < lines.Count && lines[end].Trim().Length > 0) end++;
            lines.RemoveRange(i - 1, end - (i - 1));
            lines.Insert(i - 1, $"# {id} removed by TerrainTool (BMS draws the territory map sheared in this theater)");
        }
        return string.Join("\r\n", lines);
    }

    public static readonly string[] FlagSlots = { "USA", "SKOREA", "JAPAN", "CIS", "CHINA", "NKOREA", "NATO", "FRANCE", "SWISS", "BELGIUM", "BAHRAIN", "BRAZIL", "GERMANY" };

    /// <summary>Immagini del menu e delle schermate che sono del teatro (non dell'interfaccia).</summary>
    // KoreaKTO / KoreaTvT: la scritta in basso nelle schermate ("KOREA THEATER OPERATION"), BMS la mostra per ogni teatro
    // (le mappe piccole - CAMP_SMAP, IA_MAP_TGA, REVI_MAP_TGA, KOREA_256, JSTARS_MAP, PAK_* - non si prendono dal vecchio
    //  add-on: sono del terreno 4.36 e non combaciano con il teatro nuovo; si rifanno, vedi SmallMaps)
    private static readonly string[] MainTheaterImages = { "KoreaKTO", "KoreaTvT" };
    // (PAK e ADDPACK no: nella 4.38 sono gli sfondi delle finestre Priorities e Add package, non del teatro; quelli del
    //  vecchio add-on sono della 4.36, con le scritte disegnate sopra e un altro formato, e rovinano le finestre)
    private static readonly string[] IntelImages = Array.Empty<string>();

    public override IReadOnlyList<JobInput> Inputs(TextureContext c)
    {
        string? data = AssembleJob.FindDataDir(c.Project.KoreaNewTerrain);
        string? baseMain = data == null ? null : Path.Combine(data, "Art", "_FHD", "Main.idx");
        string oldArt = Path.Combine(c.Project.OldTheaterFolder, "Art");
        return new[]
        {
            new JobInput("BMS 4.38 Art (Data\\Art)", baseMain != null && File.Exists(baseMain) ? Path.GetDirectoryName(Path.GetDirectoryName(baseMain)) : null, 0, true),
            new JobInput("Old add-on Art", Directory.Exists(oldArt) ? oldArt : null, 0, true),
        };
    }

    public override (byte[]?, List<string>) Run(TextureContext c, IProgress<double> p, CancellationToken ct)
    {
        var inp = Inputs(c);
        string baseArt = inp[0].Path!, oldArt = inp[1].Path!;
        string outArt = Path.Combine(CampMapJob.ArtFolder(c), "Art");
        var notes = new List<string>();
        string? Old(params string[] rel)
        {
            foreach (var r in rel) { var f = FindCI(oldArt, r); if (f != null) return f; }
            return null;
        }

        var flagNames = new List<string>();
        foreach (var s in FlagSlots) { flagNames.Add("OOB_FLAGS_" + s); flagNames.Add("OOB_FLAGS_" + s + "_DRK"); flagNames.Add("OOB_FLAGS_" + s + "_HRZ"); flagNames.Add("SMFLGS_" + s); }

        // (archivio della 4.38, archivio del vecchio add-on, voci da prendere; solo quelle che il teatro ha cambiato)
        var jobs = new (string Base, string[] Old, IEnumerable<string> Names, bool AddMissing)[]
        {
            (@"_FHD\Main.idx", new[] { @"_FHD\Main.idx", @"resource\main.idx" }, flagNames.Concat(MainTheaterImages), false),
            (@"Resource\Main.idx", new[] { @"resource\main.idx", @"_FHD\Main.idx" }, flagNames.Concat(MainTheaterImages), false),
            (@"_FHD\MainBg.idx", new[] { @"_FHD\MainBg.idx", @"resource\mainbg.idx" }, new[] { "MAIN_SCRN" }, false),
            (@"Resource\MainBg.idx", new[] { @"resource\mainbg.idx", @"_FHD\MainBg.idx" }, new[] { "MAIN_SCRN" }, false),
            (@"_FHD\Select.idx", new[] { @"resource\select.idx" }, null!, false),
            (@"Resource\Select.idx", new[] { @"resource\select.idx" }, null!, false),
            (@"_FHD\Intel.idx", new[] { @"_FHD\Intel.idx", @"resource\intel.idx" }, IntelImages, false),
            (@"Resource\Intel.idx", new[] { @"resource\intel.idx", @"_FHD\Intel.idx" }, IntelImages, false),
            (@"Resource\Patches.idx", new[] { @"resource\patches.idx" }, null!, true),
        };
        // mappe piccole rifatte dal teatro nuovo (rilievo, acqua, confini, regioni PAK)
        Dictionary<string, (byte[] Rgb, bool Key)> small;
        try { small = SmallMaps(c, baseArt, notes, ct); }
        catch (Exception ex) { small = new(); notes.Add("Small maps not made: " + ex.Message); }
        int k = 0;
        foreach (var (baseRel, oldRels, names, addMissing) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            p.Report((double)k++ / (jobs.Length + 1));
            string baseIdx = FindCI(baseArt, baseRel) ?? "";
            string? oldIdx = Old(oldRels);
            if (!File.Exists(baseIdx)) { notes.Add($"{baseRel}: not in BMS Data\\Art, skipped"); continue; }
            var b = ResourceBundle.Load(baseIdx);
            var o = oldIdx != null ? ResourceBundle.Load(oldIdx) : new ResourceBundle();
            // senza elenco: tutte le immagini che nel vecchio add-on sono diverse da quelle della 4.38 (Select, Patches)
            var list = (names ?? o.Entries.Where(e => e.IsImage).Select(e => e.Name)).ToList();
            int changed = 0, added = 0; var changedNames = new List<string>();
            foreach (var n in list)
            {
                var oe = o.Find(n); if (oe == null || !oe.IsImage) continue;
                var be = b.Find(n);
                if (be == null && !addMissing) continue;
                if (be != null && be.Width == oe.Width && be.Height == oe.Height && be.Flags == oe.Flags && Same(b, be, o, oe)) continue;
                b.CopyImageFrom(o, n);
                if (be == null) added++; else { changed++; changedNames.Add(n); }
            }
            // mappe piccole del teatro (stessa dimensione di quelle della 4.38)
            var smallDone = new List<string>();
            foreach (var (n, (rgb, key)) in small)
                if (b.Find(n) is { IsImage: true } se && rgb.Length == se.Width * se.Height * 3)
                { b.SetImageRgb(n, rgb, se.Width, se.Height, key); smallDone.Add(n); }
            if (smallDone.Count > 0) notes.Add($"   {baseRel}: made from the new theater: {string.Join(", ", smallDone)}");
            // bandiere disegnate per squadre che BMS non ha (es. UAE): nel posto indicato da DrawnFlags
            if (names != null && names.Contains("OOB_FLAGS_" + FlagSlots[0]))
                foreach (var (slot, _, draw) in DrawnFlags)
                {
                    var made = new List<string>();
                    foreach (var (n, vertical, dark) in new[] { ("OOB_FLAGS_" + slot, true, false), ("OOB_FLAGS_" + slot + "_DRK", true, true), ("OOB_FLAGS_" + slot + "_HRZ", false, false), ("SMFLGS_" + slot, false, false) })
                        if (b.Find(n) is { IsImage: true } fe) { b.SetImageRgb(n, FlagImage(draw, fe.Width, fe.Height, vertical, dark), fe.Width, fe.Height); made.Add(n); }
                    if (made.Count > 0) { changed++; notes.Add($"   {baseRel}: flag slot {slot} drawn ({string.Join(", ", made)})"); }
                }
            // la mappa piccola dell'Instant Action fatta dal lavoro 2D map
            string outIdx = Path.Combine(outArt, baseRel.Replace("Resource", "resource").Replace('\\', Path.DirectorySeparatorChar));
            if (baseRel.Equals(@"_FHD\Main.idx", StringComparison.OrdinalIgnoreCase) && File.Exists(outIdx))
            {
                try { var prev = ResourceBundle.Load(outIdx); if (prev.Find("MAP_SMALL") is { } ms && !Same(b, b.Find("MAP_SMALL")!, prev, ms)) { b.CopyImageFrom(prev, "MAP_SMALL"); notes.Add("   MAP_SMALL kept from the 2D map job"); } }
                catch (Exception ex) { notes.Add("   MAP_SMALL of the 2D map job not kept: " + ex.Message); }
            }
            if (changed + added + smallDone.Count == 0) { notes.Add($"{baseRel}: nothing different in the old add-on, BMS default kept"); ResourceBundle.BreakLink(outIdx); ResourceBundle.BreakLink(Path.ChangeExtension(outIdx, ".rsc")); continue; }
            b.Save(outIdx);
            notes.Add($"{baseRel}: {changed} images from the old add-on" + (added > 0 ? $", {added} added" : "") +
                      (changedNames.Count is > 0 and <= 12 ? " (" + string.Join(", ", changedNames) + ")" : ""));
        }

        // Teatri piu' grandi di 1024 km: BMS crea la mappa di occupazione di 1 cella ogni 6 km (341 px per 2048 km) e la
        // disegna 1:1 sopra la mappa piccola da 170 px del riquadro "Team Territory" (TE: caricamento e pianificazione), cosi'
        // si vede un quarto del teatro ingrandito sopra la mappa intera. Nelle copie del teatro di quelle finestre si toglie
        // il livello TAC_OVERLAY (BMS lo salta se manca); la finestra delle squadre del TE Builder resta com'e'.
        foreach (var scf in OverlayScf)
        {
            string outScf = Path.Combine(outArt, scf.Replace('\\', Path.DirectorySeparatorChar));
            string? baseScf = FindCI(baseArt, scf);
            if (c.Project.Theater.SizeKm <= 1024 || baseScf == null) { ResourceBundle.BreakLink(outScf); continue; }
            var text = File.ReadAllText(baseScf, Encoding.Latin1);
            var cut = RemoveOverlay(text);
            if (cut == null) { notes.Add($"{scf}: TAC_OVERLAY not found, not changed"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(outScf)!);
            ResourceBundle.BreakLink(outScf);
            File.WriteAllText(outScf, cut, Encoding.Latin1);
            notes.Add($"{scf}: team territory overlay removed (it does not fit a {c.Project.Theater.SizeKm} km theater)");
        }

        // Finestra delle squadre del TE Builder: con righe di celle dispari (341 per 2048 km) BMS 4.38 ridisegna la mappa del
        // territorio inclinata di una cella per riga (MakeOccupationMap legge w/2 byte per riga), e il pennello non corrisponde
        // a quello che si vede. Nella copia del teatro si tolgono la mappa colorata e gli strumenti di disegno.
        {
            const string teamScf = @"_FHD\TacEng\Tac_Team\Tac_Team.scf";
            string outScf = Path.Combine(outArt, teamScf.Replace('\\', Path.DirectorySeparatorChar));
            string? baseScf = FindCI(baseArt, teamScf);
            if (c.Project.Theater.SizeKm / 6 % 2 == 0 || baseScf == null) ResourceBundle.BreakLink(outScf);
            else if (RemoveTerritoryTools(File.ReadAllText(baseScf, Encoding.Latin1)) is { } cut)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outScf)!);
                ResourceBundle.BreakLink(outScf);
                File.WriteAllText(outScf, cut, Encoding.Latin1);
                notes.Add($"{teamScf}: territory drawing tools removed (BMS shows the territory map sheared in a {c.Project.Theater.SizeKm} km theater)");
            }
            else notes.Add($"{teamScf}: layout not as expected, not changed");
        }

        // id delle patch (quelle aggiunte dal teatro): il file del vecchio add-on se contiene tutte le righe della 4.38
        string? oldIds = Old(@"Patches\imageids.id");
        string? baseIds = FindCI(baseArt, @"Patches\ImageIds.id");
        if (oldIds != null && baseIds != null)
        {
            var bl = File.ReadAllLines(baseIds, Encoding.Latin1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var ol = File.ReadAllLines(oldIds, Encoding.Latin1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var extra = ol.Where(l => !bl.Contains(l)).ToList();
            if (extra.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(outArt, "Patches"));
                ResourceBundle.BreakLink(Path.Combine(outArt, "Patches", "ImageIds.id"));
                File.WriteAllLines(Path.Combine(outArt, "Patches", "ImageIds.id"), bl.Concat(extra), Encoding.Latin1);
                notes.Add($@"Patches\ImageIds.id: 4.38 list + {extra.Count} ids of the theater ({string.Join(", ", extra.Take(3).Select(e => e.Split('\t', ' ')[0]))}…)");
            }
        }

        // Theater.irc: quello della 4.38 con i testi del teatro (paesi, squadre, campagne)
        foreach (var rel in new[] { @"Main\Theater.irc", @"_FHD\Main\Theater.irc" })
        {
            string? bt = FindCI(baseArt, rel), ot = Old(rel.Replace("Main", "main"), @"main\theater.irc");
            if (bt == null || ot == null) continue;
            var (text, n) = MergeIrc(File.ReadAllLines(bt, Encoding.Latin1), File.ReadAllLines(ot, Encoding.Latin1));
            string dst = Path.Combine(outArt, rel.Replace(@"Main\", @"main\").Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            ResourceBundle.BreakLink(dst);
            File.WriteAllLines(dst, text, Encoding.Latin1);
            notes.Add($"{rel}: {n} texts of the theater (country and team names, campaign names and descriptions)");
        }
        notes.Add(@"Units.irc: BMS 4.38 kept (aircraft names of the 4.38 database; the old add-on renamed 4.36 types)");
        notes.Add(@"Output\Art\Art — run Build theater and install to put it into the add-on");
        p.Report(1);
        return (null, notes);
    }

    private static bool Same(ResourceBundle a, ResourceBundle.Entry ae, ResourceBundle b, ResourceBundle.Entry be)
    {
        if (ae.Width != be.Width || ae.Height != be.Height || ae.Flags != be.Flags) return false;
        var (pa, la) = a.ImageBytes(ae); var (pb, lb) = b.ImageBytes(be);
        return pa.AsSpan().SequenceEqual(pb) && la.AsSpan().SequenceEqual(lb);
    }

    private static readonly Regex AddText = new(@"^\s*\[ADDTEXT\]\s+(\S+)", RegexOptions.Compiled);

    /// <summary>Righe del file della 4.38 con il valore del teatro per le chiavi che il teatro ha; le chiavi solo del teatro in fondo.</summary>
    public static (List<string> Lines, int Replaced) MergeIrc(string[] baseLines, string[] theaterLines)
    {
        var th = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (key, block) in Blocks(theaterLines))
            if (key != null && th.TryAdd(key, block)) order.Add(key);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<string>();
        foreach (var (key, block) in Blocks(baseLines))
        {
            if (key != null && th.TryGetValue(key, out var t)) { res.AddRange(t); used.Add(key); }
            // riga di una campagna che il teatro ha ma senza questa voce: vuota, non il testo della Korea
            else if (key != null && Regex.Match(key, @"^TXT_SC_(\d+)_", RegexOptions.IgnoreCase) is { Success: true } sc && th.ContainsKey("TXT_SCENARIO_" + sc.Groups[1].Value))
                res.Add($"[ADDTEXT] {key} \"\"");
            else res.AddRange(block);
        }
        var extra = order.Where(k => !used.Contains(k)).ToList();
        if (extra.Count > 0) { res.Add(""); res.Add("// ----- theater texts not in the BMS 4.38 file -----"); foreach (var k in extra) res.AddRange(th[k]); }
        return (res, used.Count + extra.Count);
    }

    /// <summary>Righe raggruppate per voce: una [ADDTEXT] con le virgolette aperte continua nelle righe dopo.</summary>
    private static IEnumerable<(string? Key, List<string> Lines)> Blocks(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var m = AddText.Match(lines[i]);
            var block = new List<string> { lines[i].TrimEnd() };
            if (m.Success)
            {
                int quotes = lines[i].Count(ch => ch == '"');
                while (quotes % 2 == 1 && i + 1 < lines.Length && !AddText.IsMatch(lines[i + 1]))
                {
                    i++; block.Add(lines[i].TrimEnd()); quotes += lines[i].Count(ch => ch == '"');
                }
            }
            yield return (m.Success ? m.Groups[1].Value : null, block);
        }
    }

    // ───────────────────────── mappe piccole ─────────────────────────

    /// <summary>
    /// Le mappe piccole come quelle della Korea (studiate sui file della 4.38): rilievo ombreggiato in grigio sulla terra,
    /// mare grigio chiaro uniforme (216), confini di stato neri, reticolo leggero. CAMP_SMAP (riquadro Team Territory e mappa
    /// della campagna: BMS ci colora sopra le zone delle squadre), IA_MAP_TGA, KOREA_256, JSTARS_MAP in grigio; REVI_MAP_TGA a
    /// colori (dalla mappa 2D); PAK_REGION_OVERLAY / PAK_OUTLINE_OVERLAY dalle regioni del file &lt;teatro&gt;.pak della campagna.
    /// Le dimensioni sono quelle della 4.38. Restituisce nome → (RGB, magenta trasparente).
    /// </summary>
    private static Dictionary<string, (byte[] Rgb, bool Key)> SmallMaps(TextureContext c, string baseArt, List<string> notes, CancellationToken ct)
    {
        var res = new Dictionary<string, (byte[], bool)>(StringComparer.OrdinalIgnoreCase);
        var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in new[] { @"_FHD\Main.idx", @"_FHD\Intel.idx" })
            if (FindCI(baseArt, rel) is { } f)
                foreach (var e in ResourceBundle.Load(f).Entries.Where(e => e.IsImage && e.Width == e.Height))
                    sizes.TryAdd(e.Name, e.Width);
        int Size(string n, int def) => sizes.TryGetValue(n, out int v) ? v : def;

        // confini di stato (Natural Earth, Data\mappa_base.txt accanto al programma)
        string? baseMap = FindUp(AppContext.BaseDirectory, Path.Combine("Data", "mappa_base.txt"));
        var borders = baseMap != null ? Theater.BaseMap.Load(baseMap).Where(l => l.Kind == 'B').ToList() : new();
        if (baseMap == null) notes.Add("   Small maps: Data\\mappa_base.txt not found, no borders drawn");
        var grid = c.Project.Theater.CreateGrid();
        double S = c.Project.Theater.SizeMeters;

        // rilievo e acqua dalle mappe del New Terrain (lette a righe, come l'anteprima)
        NewTerrain.TerrainSampler? ts = null;
        try { ts = new NewTerrain.TerrainSampler(c.Project); } catch (Exception ex) { notes.Add("   Small maps: " + ex.Message); }
        if (ts == null) return res;
        const int N = 1024;
        var (h, water) = ReadHeights(ts, N, ct);

        byte[] Gray(int px)
        {
            var img = new byte[px * px * 3];
            double cellFt = ts.SizeFt / px;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    // media dei campioni 1024² che cadono nel pixel
                    int x0 = x * N / px, x1 = Math.Max(x0 + 1, (x + 1) * N / px), y0 = y * N / px, y1 = Math.Max(y0 + 1, (y + 1) * N / px);
                    double hs = 0, wcount = 0, n = 0, he = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            float dzx = h[yy, Math.Min(N - 1, xx + 1)] - h[yy, Math.Max(0, xx - 1)], dzy = h[Math.Min(N - 1, yy + 1), xx] - h[Math.Max(0, yy - 1), xx];
                            hs += (-dzx + dzy) / (2 * ts.SizeFt / N); he += h[yy, xx]; if (water[yy, xx]) wcount++; n++;
                        }
                    double v;
                    if (wcount / n > 0.5) v = 216; // mare: grigio chiaro uniforme come la Korea
                    else
                    {
                        double shade = Math.Clamp(hs / n * 5, -1, 1), e = Math.Clamp(he / n / 10000.0, 0, 1);
                        v = Math.Clamp(196 + shade * 60 - e * 30, 110, 236);
                    }
                    int o = (y * px + x) * 3; img[o] = img[o + 1] = img[o + 2] = (byte)v;
                }
            Graticule(img, px, 200);
            DrawBorders(img, px, 30, 30, 30);
            return img;
        }

        void Graticule(byte[] img, int px, byte v)
        {
            var corners = grid.Corners();
            double minLat = Math.Floor(corners.Min(q => q.Lat)), maxLat = Math.Ceiling(corners.Max(q => q.Lat));
            double minLon = Math.Floor(corners.Min(q => q.Lon)), maxLon = Math.Ceiling(corners.Max(q => q.Lon));
            for (double lat = Math.Ceiling(minLat / 2) * 2; lat <= maxLat; lat += 2)
                Line(img, px, Enumerable.Range(0, 81).Select(i => (lat, minLon + (maxLon - minLon) * i / 80.0)), v, v, v, onlyLight: true);
            for (double lon = Math.Ceiling(minLon / 2) * 2; lon <= maxLon; lon += 2)
                Line(img, px, Enumerable.Range(0, 81).Select(i => (minLat + (maxLat - minLat) * i / 80.0, lon)), v, v, v, onlyLight: true);
        }

        void DrawBorders(byte[] img, int px, byte r, byte g, byte b)
        {
            foreach (var l in borders) Line(img, px, l.Points, r, g, b, onlyLight: false);
        }

        void Line(byte[] img, int px, IEnumerable<(double Lat, double Lon)> pts, byte r, byte g, byte b, bool onlyLight)
        {
            (double X, double Y)? prev = null;
            foreach (var (lat, lon) in pts)
            {
                var (tx, ty) = grid.ToTheater(lat, lon);
                var cur = (X: tx / S * px, Y: (1 - ty / S) * px);
                if (prev is { } pv)
                {
                    double dx = cur.X - pv.X, dy = cur.Y - pv.Y; int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
                    for (int i = 0; i <= steps; i++)
                    {
                        int xi = (int)Math.Floor(pv.X + dx * i / Math.Max(1, steps)), yi = (int)Math.Floor(pv.Y + dy * i / Math.Max(1, steps));
                        if (xi < 0 || yi < 0 || xi >= px || yi >= px) continue;
                        int o = (yi * px + xi) * 3;
                        if (onlyLight && img[o] < 205) continue; // il reticolo solo sul mare / zone chiare, come le aerovie della Korea
                        img[o] = r; img[o + 1] = g; img[o + 2] = b;
                    }
                }
                prev = cur;
            }
        }

        foreach (var (n, def) in new[] { ("CAMP_SMAP", 344), ("IA_MAP_TGA", 170), ("KOREA_256", 258), ("JSTARS_MAP", 370) })
        {
            ct.ThrowIfCancellationRequested();
            res[n] = (Gray(Size(n, def)), false);
        }

        // REVI_MAP_TGA: a colori, dalla mappa 2D del teatro (campmap) se c'è
        string camp = Path.Combine(CampMapJob.ArtFolder(c), "Art", "resource", "campmap.idx");
        if (File.Exists(camp) && ResourceBundle.ReadImageRgb(camp, "BIG_MAP_ID_SCENARIO_0") is { } cm)
        {
            int px = Size("REVI_MAP_TGA", 344);
            var img = Downsample(cm.Rgb, cm.W, px);
            DrawBorders(img, px, 30, 30, 30);
            res["REVI_MAP_TGA"] = (img, false);
        }
        else notes.Add("   REVI_MAP_TGA: no 2D map (campmap) yet, BMS default kept");

        // regioni PAK: <campagna>\<teatro>.pak (un byte per cella, 0 = mare), come Korea.pak → PAK_REGION/OUTLINE_OVERLAY
        string? pak = PakFile(c);
        if (pak != null)
        {
            var pb = File.ReadAllBytes(pak);
            int ps = (int)Math.Round(Math.Sqrt(pb.Length));
            if (ps * ps == pb.Length)
            {
                int px = Size("PAK_REGION_OVERLAY", 256);
                // gli 8 rosa della Korea; regioni vicine con colori diversi (colorazione greedy del grafo delle regioni)
                var pinks = new (byte R, byte G, byte B)[] { (240, 0, 72), (240, 32, 104), (240, 64, 136), (240, 80, 152), (240, 96, 168), (240, 112, 192), (240, 136, 208), (240, 168, 240) };
                var adj = new HashSet<(byte, byte)>();
                for (int y = 0; y < ps; y++)
                    for (int x = 0; x < ps; x++)
                    {
                        byte a = pb[y * ps + x];
                        if (x + 1 < ps && pb[y * ps + x + 1] is var r && r != a) { adj.Add((a, r)); adj.Add((r, a)); }
                        if (y + 1 < ps && pb[(y + 1) * ps + x] is var d && d != a) { adj.Add((a, d)); adj.Add((d, a)); }
                    }
                var colorOf = new int[256];
                for (int id = 1; id < 256; id++)
                {
                    var used = Enumerable.Range(1, id - 1).Where(o => adj.Contains(((byte)id, (byte)o))).Select(o => colorOf[o]).ToHashSet();
                    colorOf[id] = Enumerable.Range(0, pinks.Length).FirstOrDefault(ci => !used.Contains(ci), id % pinks.Length);
                }
                var reg = new byte[px * px * 3]; var outl = new byte[px * px * 3];
                byte Id(int x, int y) => pb[Math.Clamp(y * ps / px, 0, ps - 1) * ps + Math.Clamp(x * ps / px, 0, ps - 1)];
                for (int y = 0; y < px; y++)
                    for (int x = 0; x < px; x++)
                    {
                        int o = (y * px + x) * 3; byte id = Id(x, y);
                        var col = id == 0 ? ((byte)248, (byte)0, (byte)248) : pinks[colorOf[id]];
                        reg[o] = col.Item1; reg[o + 1] = col.Item2; reg[o + 2] = col.Item3;
                        bool edge = id != 0 && ((x + 1 < px && Id(x + 1, y) != id) || (y + 1 < px && Id(x, y + 1) != id));
                        if (edge) { outl[o] = outl[o + 1] = outl[o + 2] = 0; } else { outl[o] = 248; outl[o + 1] = 0; outl[o + 2] = 248; }
                    }
                res["PAK_REGION_OVERLAY"] = (reg, true);
                res["PAK_OUTLINE_OVERLAY"] = (outl, true);
            }
            else notes.Add($"   {Path.GetFileName(pak)} is not square, PAK overlays not made");
        }
        else notes.Add("   No <theater>.pak in the campaign folder, PAK overlays left as BMS default");
        return res;
    }

    private static (float[,] H, bool[,] Water) ReadHeights(NewTerrain.TerrainSampler ts, int n, CancellationToken ct)
    {
        var h = new float[n, n]; var w = new bool[n, n];
        var row = new byte[ts.HeightSide * 2];
        using (var fs = new FileStream(ts.HeightPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            for (int y = 0; y < n; y++)
            {
                ct.ThrowIfCancellationRequested();
                fs.Position = (long)((y + 0.5) * ts.HeightSide / n) * ts.HeightSide * 2;
                fs.ReadExactly(row);
                for (int x = 0; x < n; x++) h[y, x] = BitConverter.ToInt16(row, (int)((x + 0.5) * ts.HeightSide / n) * 2);
            }
        if (ts.WaterSide > 0)
        {
            var wr = new byte[ts.WaterSide * 2];
            using var fw = new FileStream(ts.WaterPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            for (int y = 0; y < n; y++)
            {
                fw.Position = (long)((y + 0.5) * ts.WaterSide / n) * ts.WaterSide * 2;
                fw.ReadExactly(wr);
                for (int x = 0; x < n; x++) w[y, x] = BitConverter.ToInt16(wr, (int)((x + 0.5) * ts.WaterSide / n) * 2) > h[y, x] - 1;
            }
        }
        return (h, w);
    }

    private static byte[] Downsample(byte[] rgb, int size, int px)
    {
        var img = new byte[px * px * 3];
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                int x0 = x * size / px, x1 = Math.Max(x0 + 1, (x + 1) * size / px), y0 = y * size / px, y1 = Math.Max(y0 + 1, (y + 1) * size / px);
                long r = 0, g = 0, b = 0, k = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) { long o = ((long)yy * size + xx) * 3; r += rgb[o]; g += rgb[o + 1]; b += rgb[o + 2]; k++; }
                int d = (y * px + x) * 3; img[d] = (byte)(r / k); img[d + 1] = (byte)(g / k); img[d + 2] = (byte)(b / k);
            }
        return img;
    }

    /// <summary>Il file delle regioni PAK della campagna (&lt;teatro&gt;.pak): CampaignEdits, Output\Install, BMS, vecchio add-on.</summary>
    private static string? PakFile(TextureContext c)
    {
        string addOn = new DirectoryInfo(c.Project.OldTheaterFolder).Name;
        string? data = AssembleJob.FindDataDir(c.Project.KoreaNewTerrain);
        foreach (var dir in new[] { Path.Combine(c.Project.ProjectFolder, "CampaignEdits"), Path.Combine(c.Project.ProjectFolder, "Output", "Install", addOn, "Campaign"),
                                    data == null ? "" : Path.Combine(data, addOn, "Campaign"), Path.Combine(c.Project.OldTheaterFolder, "Campaign") })
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.pak").FirstOrDefault() is { } f) return f;
        return null;
    }

    private static string? FindUp(string start, string rel)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, rel);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Percorso relativo cercato senza distinzione di maiuscole (Windows sì, ma anche altrove).</summary>
    public static string? FindCI(string root, string rel)
    {
        string cur = root;
        foreach (var part in rel.Split('\\', '/'))
        {
            if (!Directory.Exists(cur)) return null;
            var hit = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            cur = hit;
        }
        return File.Exists(cur) ? cur : null;
    }
}
