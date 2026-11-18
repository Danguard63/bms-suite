using System.Globalization;
using System.Text.RegularExpressions;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Editor;

/// <summary>Un obiettivo nell'editor. X nord, Y est (piedi del teatro), Heading in gradi (come CampObjData.XML).</summary>
public sealed class EdObjective
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public int Ocd { get; set; }
    public int Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Heading { get; set; }
    public bool Junction { get; set; }

    // valori caricati (uscita dell'ultimo lavoro Objects), per "Reset"
    public string Name0 { get; init; } = "";
    public int Ocd0 { get; init; }
    public int Type0 { get; init; }
    public double X0 { get; init; }
    public double Y0 { get; init; }
    public double H0 { get; init; }
    /// <summary>OCD e tipo del vecchio add-on (prima di ogni conversione in incrocio).</summary>
    public int OrigOcd { get; init; }
    public int OrigType { get; init; }
    /// <summary>C'era gia' una modifica salvata in objective_edits.csv.</summary>
    public bool HadEdit { get; set; }
    /// <summary>Incrocio secondo objective_edits.csv al caricamento.</summary>
    public bool JunctionLoaded { get; set; }

    /// <summary>Base coreana messa al posto di questa (OCD della 4.38, -1 = nessuna) e quella salvata in base_swaps.csv.</summary>
    public int SwapOcd { get; set; } = -1;
    public string SwapName { get; set; } = "";
    public int SwapOcd0 { get; set; } = -1;
    public string SwapName0 { get; set; } = "";
    /// <summary>Al salvataggio si toglie la modifica salvata (base coreana tolta: torna dove la mette il tool).</summary>
    public bool DropEdit { get; set; }

    /// <summary>Disposizione scelta a mano al posto della sua (layout_swaps.csv): sorgente, OCD, nome, tipo; -1 = nessuna.</summary>
    public string RepSource { get; set; } = "";
    public int RepOcd { get; set; } = -1;
    public string RepName { get; set; } = "";
    public int RepType { get; set; } = -1;
    public string RepSource0 { get; set; } = "";
    public int RepOcd0 { get; set; } = -1;
    public bool RepChanged => RepOcd != RepOcd0 || (RepOcd >= 0 && RepSource != RepSource0);
    public bool Changed => Junction != JunctionLoaded || SwapOcd != SwapOcd0 || DropEdit || PiecesDirty || RepChanged || Math.Abs(X - X0) > 0.01 || Math.Abs(Y - Y0) > 0.01 || Math.Abs(Heading - H0) > 0.001;
    /// <summary>Pezzi della disposizione toccati a mano (piece_edits.csv): numero del pezzo → posizione, direzione, tolto.</summary>
    public Dictionary<int, PieceEdit> Pieces { get; } = new();
    /// <summary>Punti della disposizione spostati a mano (point_edits.csv): numero del punto (PD Num) → posizione, direzione.</summary>
    public Dictionary<int, PointEdit> PointMoves { get; } = new();
    /// <summary>Pezzi cambiati dopo l'ultimo salvataggio.</summary>
    public bool PiecesDirty { get; set; }
    /// <summary>Cresce a ogni modifica dei pezzi: la disposizione propria dell'obiettivo si ricalcola.</summary>
    public int PieceVersion { get; set; }
    public string TypeName => EditorData.TypeName(Type);
    public string State => Junction ? "→ junction" : Type0 == 15 && OrigType != 15 ? "removed (csv)" : SwapOcd >= 0 ? (SwapOcd != SwapOcd0 ? "→ Korea" : "Korea base")
        : RepChanged ? (RepOcd >= 0 ? "→ layout" : "layout back") : RepOcd >= 0 && !PiecesDirty && !(Math.Abs(X - X0) > 0.01 || Math.Abs(Y - Y0) > 0.01 || Math.Abs(Heading - H0) > 0.001) ? "new layout"
        : PiecesDirty ? "pieces" : Changed ? "moved" : HadEdit ? (Pieces.Count + PointMoves.Count > 0 ? "edited, pieces" : "edited") : "";
    public double ShiftM => Math.Sqrt((X - X0) * (X - X0) + (Y - Y0) * (Y - Y0)) * 0.3048;

    // pista reale (airbase_targets.csv: OurAirports o OpenStreetMap) e pista principale come e' messa ora nell'editor
    /// <summary>Pista reale: centro (piedi del teatro), direzione sulla mappa (griglia del teatro) e geografica (vera), fonte.</summary>
    public (double X, double Y, double MapHdg, double TrueHdg, double LenFt, string Source)? Real { get; set; }
    /// <summary>Direzione della pista principale ora (mappa, 0-180), aggiornata dall'editor.</summary>
    public double? RwyHdg { get; set; }
    public string RealText => Real is { } r ? $"{r.TrueHdg % 180:000.0}/{r.TrueHdg % 180 + 180:000.0}" : "";
    /// <summary>Quanto manca alla pista reale (gradi, -90..90): positivo = da girare in senso orario.</summary>
    public string DeltaText => Real is { } r && RwyHdg is double h ? $"{((r.MapHdg - h) % 180 + 270) % 180 - 90:+0.0;-0.0;0.0}" : "";
}

/// <summary>Un pezzo della disposizione: posizione relativa (piedi, nord/est), direzione e sagoma del modello (Parent.dat).</summary>
/// Z = OffsetZ del FED (piedi, positivo = in giu' rispetto al terreno sotto il pezzo).
public sealed record EdFeature(double N, double E, double Heading, int CtIdx, string Name, double MinX, double MaxX, double MinY, double MaxY, int Num = -1, double Z = 0);

/// <summary>
/// Lista di punti della disposizione (PHD/PDX). Lista tipo 8 = angoli dell'asfalto della pista; tipo 1 = percorso di
/// rullaggio di una pista (punti 1/2 alle testate, 3/11/12/15 lungo le vie); le altre liste sono punti sparsi
/// (parcheggi 4/5/6, 7, 9, 16...), non percorsi. T = tipo del punto.
/// </summary>
public sealed record EdPath(int Type, List<(double N, double E, int T)> Points)
{
    /// <summary>Numero (PD Num) di ogni punto della lista, nello stesso ordine di Points.</summary>
    public List<int> Idx { get; init; } = new();
    /// <summary>Numero del PHD e tutti i suoi campi (RunwayNumber, LandingPattern, Data, FeatureDependencyIdx_n...), come nel file.</summary>
    public int HeaderNum { get; init; } = -1;
    public IReadOnlyList<(string Field, string Value)> Header { get; init; } = Array.Empty<(string, string)>();
    public static readonly HashSet<int> TaxiTypes = new() { 1, 2, 3, 11, 12, 15 };
}

/// <summary>Un punto della disposizione (PDX): numero, posizione relativa (piedi, nord/est), tipo, direzione.</summary>
public sealed record EdPoint(int Num, double N, double E, int T, double Heading);

public sealed class EdLayout
{
    public List<EdFeature> Features { get; } = new();
    /// <summary>Tutti i punti del PDX, in ordine (Num = posizione nella lista).</summary>
    public List<EdPoint> Points { get; } = new();
    public List<EdPath> Paths { get; } = new();
    /// <summary>Intestazioni delle piste (PHD tipo 1, una per senso d'atterraggio): elemento del numero (FeatureDependencyIdx_0 = FED Num) e direzione nella disposizione.</summary>
    public List<(int Dep0, double Data)> RunwayHeads { get; } = new();
    /// <summary>Flags di ogni punto del PDX (1 = primo della lista, 2 = ultimo), stesso ordine di Points.</summary>
    public List<int> PointFlags { get; } = new();
    /// <summary>Distanza massima dal centro dell'obiettivo (piedi), per sapere se e' sullo schermo.</summary>
    public double Radius { get; set; } = 200;
}

/// <summary>
/// Dati dell'editor degli obiettivi, letti dall'uscita del lavoro Objects (Output\Install\&lt;add-on&gt;):
/// CampObjData.XML, Falcon4_CT/FCD e disposizioni di ObjectsNT; sagome dei modelli dai Parent.dat di BMS (sola lettura).
/// </summary>
public sealed class EditorData
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public TheaterProject Project { get; }
    public List<EdObjective> Objectives { get; } = new();
    public string CampObjSource { get; private set; } = "";
    public string ObjectsDir { get; }
    public string? ModelsDir { get; }
    /// <summary>Folders with the 3D models, searched in order (theater, then BMS).</summary>
    public List<string> ModelDirs { get; } = new();

    /// <summary>Models\&lt;n&gt; of a model number: the first folder that has it.</summary>
    private string? ModelFolder(int graphics)
    {
        foreach (var md in ModelDirs)
        {
            string d = Path.Combine(md, graphics.ToString(Ci));
            if (Directory.Exists(d)) return d;
        }
        return ModelDirs.Count > 0 ? Path.Combine(ModelDirs[0], graphics.ToString(Ci)) : null;
    }
    /// <summary>Cartella Data di BMS (sola lettura) e sua ObjectiveRelatedData: disposizioni delle basi coreane.</summary>
    public string? BmsData { get; }
    public string? KoreaOrd { get; }
    public int JunctionOcd { get; private set; } = -1;
    public List<string> Warnings { get; } = new();
    public PhotoTiles? Photo { get; }

    private readonly Dictionary<int, (int Type, int Idx)> _ctObjective = new();        // indice CT → (tipo, OCD)
    private readonly Dictionary<int, int> _ocdType = new();
    private readonly Dictionary<int, (int Graphics, int Fcd)> _ctFeature = new();      // indice CT → (modello, FCD)
    private readonly Dictionary<int, string> _fcdName = new();
    private readonly Dictionary<int, List<(string Field, string Value)>> _fcdFields = new();
    private readonly Dictionary<int, (double, double, double, double)?> _dims = new();
    private readonly Dictionary<int, EdLayout> _layouts = new();

    public static string TypeName(int t) => t switch
    {
        1 => "Airbase", 2 => "Airstrip", 3 => "Army base", 4 => "Beach", 5 => "Border", 6 => "Bridge", 7 => "Chemical",
        8 => "City", 9 => "Com", 10 => "Depot", 11 => "Factory", 12 => "Ford", 13 => "Fortification", 14 => "Scenery",
        15 => "Junction", 16 => "Nav beacon", 17 => "Nuclear", 18 => "Pass", 19 => "Port", 20 => "Power plant", 21 => "Radar",
        22 => "Radio tower", 23 => "Rail terminal", 24 => "Railroad", 25 => "Refinery", 26 => "Road", 27 => "Sea", 28 => "Town",
        29 => "Village", 30 => "HARTS", 31 => "SAM site", _ => $"Type {t}",
    };

    public EditorData(TheaterProject p)
    {
        Project = p;
        bool installed = p.IsInstalledTheater;
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string install = Path.Combine(p.ProjectFolder, "Output", "Install", addOn);
        // theater installed in BMS: its own objects database, read only (the edits stay in the work folder)
        ObjectsDir = installed ? p.SourceObjectsDir : Path.Combine(install, "Terrdata", ObjectsJob.FolderName);
        if (!File.Exists(Path.Combine(ObjectsDir, "Falcon4_CT.xml")))
            throw new InvalidOperationException(installed ? $"{ObjectsDir}\\Falcon4_CT.xml not found: the theater has no objects database"
                                                          : $"{ObjectsDir} not found: run the 'Objects' job first");
        var data = installed && Directory.Exists(p.BmsDataDir) ? p.BmsDataDir : AssembleJob.FindDataDir(p.KoreaNewTerrain);
        BmsData = data;
        KoreaOrd = data == null ? null : KoreaBases.OrdDir(data);
        // theater models first (an add-on can have its own), then the BMS ones for everything else
        ModelDirs = new[] { installed ? Path.Combine(ObjectsDir, "Models") : null, data == null ? null : Path.Combine(data, "Terrdata", "Objects", "Models") }
            .Where(d => d != null && Directory.Exists(d)).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ModelsDir = ModelDirs.FirstOrDefault();
        if (ModelsDir == null) Warnings.Add("BMS Terrdata\\Objects\\Models not found: buildings are drawn as small squares");

        LoadCt(Path.Combine(ObjectsDir, "Falcon4_CT.xml"));
        LoadFcd(Path.Combine(ObjectsDir, "Falcon4_FCD.xml"));

        // obiettivi: l'uscita dell'ultimo Objects, altrimenti quelli del vecchio add-on
        string camp = installed ? Campaign.TheaterWorkspace.FindCampObjData(p.SourceCampaignDir) ?? Path.Combine(p.SourceCampaignDir, "CampObjData.XML")
                                : Path.Combine(install, "Campaign", "CampObjData.XML");
        // l'uscita di Objects e' ObjectChanges.json (la CampObjData di Install la scrive il Campaign Editor con "Apply object changes"):
        // se e' piu' recente, vecchio add-on + ObjectChanges.json, cosi' i numeri delle disposizioni copiate (OCD) sono quelli di ObjectsNT
        string chPath = TerrainTool.Core.Campaign.ObjectChangeSet.PathFor(p);
        string oldCampFile = installed ? camp : Path.Combine(p.OldTheaterFolder, "Campaign", "CampObjData.XML");
        Dictionary<int, TerrainTool.Core.Campaign.ObjectiveChange>? overlay = null;
        if (!installed && File.Exists(chPath) && File.Exists(oldCampFile) && (!File.Exists(camp) || File.GetLastWriteTimeUtc(chPath) > File.GetLastWriteTimeUtc(camp)))
        {
            try
            {
                overlay = TerrainTool.Core.Campaign.ObjectChangeSet.Load(chPath)?.Objectives.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
                if (overlay != null) camp = oldCampFile;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { overlay = null; }
        }
        if (!File.Exists(camp)) camp = oldCampFile;
        CampObjSource = overlay != null ? camp + " + Output\\ObjectChanges.json (latest Run Objects)" : camp;
        var edits = ObjectiveEdits.Load(p.ProjectFolder);
        var srcGrid = p.Theater.CreateGrid();
        // basi con la rotazione messa dentro la disposizione: qui direzione vera e disposizione non girata (Output\EditorLayouts)
        _baked = BakedLayouts.Load(p.ProjectFolder);
        _bakedDir = BakedLayouts.Folder(p.ProjectFolder);
        // ponti col punto spostato nel km del fiume (Objects): qui nel punto di prima, con la disposizione di prima
        var shifts = BakedLayouts.LoadShifts(p.ProjectFolder);
        var orig = new Dictionary<int, int>();
        string oldCamp = installed ? camp : Path.Combine(p.OldTheaterFolder, "Campaign", "CampObjData.XML");
        if (File.Exists(oldCamp))
            foreach (Match m in Regex.Matches(File.ReadAllText(oldCamp), @"<CampObj CampId=""(\d+)"">.*?<OcdIndex>(\d+)</OcdIndex>", RegexOptions.Singleline))
                orig[int.Parse(m.Groups[1].Value, Ci)] = int.Parse(m.Groups[2].Value, Ci);
        foreach (Match m in Regex.Matches(File.ReadAllText(camp), @"<CampObj CampId=""(\d+)"">(.*?)</CampObj>", RegexOptions.Singleline))
        {
            string b = m.Groups[2].Value;
            string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
            int id = int.Parse(m.Groups[1].Value, Ci);
            int ocd = int.TryParse(G("OcdIndex"), NumberStyles.Integer, Ci, out var oc) ? oc : -1;
            double x = D(G("PositionX")), y = D(G("PositionY")), h = D(G("Heading"));
            // old add-on in another grid (project on the standard projection): its positions carried over
            if (srcGrid.Reprojects && string.Equals(camp, oldCampFile, StringComparison.OrdinalIgnoreCase)) (x, y) = srcGrid.FromSourceFt(x, y);
            string? newName = null;
            bool stale = false;
            if (overlay != null && overlay.TryGetValue(id, out var chg))
            {
                ocd = chg.Ocd; newName = chg.NewName;
                // posizione che veniva da una modifica che in objective_edits.csv non c'e' piu': si torna a quella originale
                stale = chg.Edited && !edits.ContainsKey(id);
                if (!stale) { x = chg.X; y = chg.Y; h = chg.Heading; }
                if (!stale && shifts.TryGetValue(id, out var sh) && sh.Ocd == ocd) { x -= sh.DN; y -= sh.DE; }
            }
            else if (overlay == null && !string.Equals(camp, oldCampFile, StringComparison.OrdinalIgnoreCase)
                     && shifts.TryGetValue(id, out var sh2) && sh2.Ocd == ocd) { x -= sh2.DN; y -= sh2.DE; }
            if (!stale && _baked.TryGetValue(id, out var bk) && bk.Ocd == ocd) h = ((h + bk.Heading) % 360 + 360) % 360;
            string name = newName ?? System.Net.WebUtility.HtmlDecode(G("CampName")).Trim();
            int type = _ocdType.GetValueOrDefault(ocd, -1);
            int oocd = orig.GetValueOrDefault(id, ocd);
            var o = new EdObjective { Id = id, Name = name, Name0 = name, Ocd = ocd, Ocd0 = ocd, Type = type, Type0 = type, X = x, X0 = x, Y = y, Y0 = y, Heading = h, H0 = h,
                                      OrigOcd = oocd, OrigType = _ocdType.GetValueOrDefault(oocd, type) };
            if (edits.TryGetValue(id, out var e))
            {
                o.HadEdit = true;
                o.X = e.X; o.Y = e.Y; o.Heading = e.Heading;
                if (e.Junction) { o.Junction = o.JunctionLoaded = true; o.Type = 15; }
            }
            Objectives.Add(o);
        }
        foreach (var (id, list) in PieceEdits.Load(p.ProjectFolder))
            if (Objectives.FirstOrDefault(o => o.Id == id) is EdObjective po)
                foreach (var pe in list.Values) po.Pieces[pe.Num] = pe;
        foreach (var (id, list) in PointEdits.Load(p.ProjectFolder))
            if (Objectives.FirstOrDefault(o => o.Id == id) is EdObjective po)
                foreach (var pe in list.Values) po.PointMoves[pe.Num] = pe;
        _applied = LayoutSwaps.LoadApplied(p.ProjectFolder);
        foreach (var ls in LayoutSwaps.Load(p.ProjectFolder).Values)
            if (Objectives.FirstOrDefault(o => o.Id == ls.CampId) is EdObjective lo)
            {
                lo.RepSource = lo.RepSource0 = ls.Source; lo.RepOcd = lo.RepOcd0 = ls.Ocd; lo.RepName = ls.LayoutName; lo.RepType = ls.Type;
                if (ls.Type > 0) lo.Type = ls.Type;
            }
        foreach (var sw in BaseSwaps.Load(p.ProjectFolder).Values)
            if (Objectives.FirstOrDefault(o => o.Id == sw.CampId) is EdObjective so)
            { so.SwapOcd = so.SwapOcd0 = sw.KoreaOcd; so.SwapName = so.SwapName0 = sw.KoreaName; }
        JunctionOcd = Objectives.Where(o => o.Type == 15).GroupBy(o => o.Ocd).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(-1);
        // le modifiche "incrocio" lette prima di conoscere l'OCD dell'incrocio
        foreach (var o in Objectives.Where(o => o.Junction && o.Type0 != 15)) o.Ocd = JunctionOcd;

        LoadRealRunways();
        foreach (var o in Objectives) UpdateRunway(o);

        string photo = Path.Combine(p.OutputNewTerrain, "Photoreal", "4K");
        if (Directory.Exists(photo)) Photo = new PhotoTiles(photo, p.Theater.SizeMeters);
        else Warnings.Add($"{photo} not found: no satellite photo under the objects");
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, Ci, out var v) ? v : 0;

    private void LoadCt(string path)
    {
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"<CT Num=""(\d+)"">(.*?)</CT>", RegexOptions.Singleline))
        {
            string b = m.Groups[2].Value;
            int G(string t) { var x = Regex.Match(b, $"<{t}>(-?\\d+)</{t}>"); return x.Success ? int.Parse(x.Groups[1].Value, Ci) : -1; }
            int n = int.Parse(m.Groups[1].Value, Ci), et = G("EntityType"), ei = G("EntityIdx");
            if (et == 3) { _ctObjective[n] = (G("Type"), ei); _ocdType.TryAdd(ei, G("Type")); }
            else if (et == 1) _ctFeature[n] = (G("GraphicsNormal"), ei);
        }
    }

    private void LoadFcd(string path)
    {
        if (!File.Exists(path)) return;
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"<FCD Num=""(\d+)"">(.*?)</FCD>", RegexOptions.Singleline))
        {
            var n = Regex.Match(m.Groups[2].Value, "<Name>(.*?)</Name>");
            int num = int.Parse(m.Groups[1].Value, Ci);
            _fcdName[num] = n.Success ? System.Net.WebUtility.HtmlDecode(n.Groups[1].Value).Trim() : "";
            _fcdFields[num] = Regex.Matches(m.Groups[2].Value, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline)
                .Select(x => (x.Groups[1].Value, System.Net.WebUtility.HtmlDecode(x.Groups[2].Value).Trim())).ToList();
        }
    }

    public IEnumerable<int> TypesPresent => Objectives.Select(o => o.Type).Distinct().OrderBy(t => t);

    public void SetJunction(EdObjective o, bool on)
    {
        if (on)
        {
            if (o.OrigType == 15) return;   // era gia' un incrocio nel vecchio add-on
            o.Junction = true; o.Ocd = JunctionOcd >= 0 ? JunctionOcd : o.Ocd; o.Type = 15;
        }
        else
        {
            o.Junction = false;
            // tolto da un foglio (basi/ponti VIA): si mostra com'era; il foglio resta valido al prossimo Objects
            if (o.Type0 == 15) { o.Ocd = o.OrigOcd; o.Type = o.OrigType; } else { o.Ocd = o.Ocd0; o.Type = o.Type0; }
        }
    }

    public void Reset(EdObjective o)
    {
        o.X = o.X0; o.Y = o.Y0; o.Heading = o.H0;
        o.SwapOcd = o.SwapOcd0; o.SwapName = o.SwapName0; o.DropEdit = false;
        SetJunction(o, o.JunctionLoaded);
    }

    /// <summary>Scrive objective_edits.csv: le modifiche gia' salvate restano, quelle nuove si aggiungono.</summary>
    public int SaveEdits()
    {
        var old = ObjectiveEdits.Load(Project.ProjectFolder);
        foreach (var o in Objectives)
        {
            if (o.DropEdit) { old.Remove(o.Id); continue; }
            if (o.Changed || o.HadEdit)
                old[o.Id] = new ObjectiveEdit(o.Id, o.Name0, o.X, o.Y, o.Heading, o.Junction);
        }
        ObjectiveEdits.Save(Project.ProjectFolder, old.Values);
        // pezzi toccati a mano: piece_edits.csv (le modifiche che non cambiano niente si tolgono)
        if (Objectives.Any(o => o.PiecesDirty) || File.Exists(PieceEdits.PathFor(Project.ProjectFolder)))
            PieceEdits.Save(Project.ProjectFolder, Objectives.Where(o => !o.DropEdit).SelectMany(o => o.Pieces.Values));
        if (Objectives.Any(o => o.PiecesDirty) || File.Exists(PointEdits.PathFor(Project.ProjectFolder)))
            PointEdits.Save(Project.ProjectFolder, Objectives.Where(o => !o.DropEdit).SelectMany(o => o.PointMoves.Values));
        foreach (var o in Objectives)
        {
            o.PiecesDirty = false;
            foreach (var k in o.Pieces.Where(kv => kv.Value.IsNoOp).Select(kv => kv.Key).ToList()) o.Pieces.Remove(k);
            foreach (var k in o.PointMoves.Where(kv => kv.Value.IsNoOp).Select(kv => kv.Key).ToList()) o.PointMoves.Remove(k);
        }
        // disposizioni scelte a mano: layout_swaps.csv
        if (Objectives.Any(o => o.RepChanged) || File.Exists(LayoutSwaps.PathFor(Project.ProjectFolder)))
            LayoutSwaps.Save(Project.ProjectFolder, Objectives.Where(o => o.RepOcd >= 0 && !o.DropEdit).Select(o => new LayoutSwap(o.Id, o.Name0, o.RepSource, o.RepOcd, o.RepName, o.RepType)));
        foreach (var o in Objectives) { o.RepSource0 = o.RepSource; o.RepOcd0 = o.RepOcd; }
        // basi coreane: base_swaps.csv
        var sw = BaseSwaps.Load(Project.ProjectFolder);
        bool swChanged = false;
        foreach (var o in Objectives.Where(o => o.SwapOcd != o.SwapOcd0 || (o.SwapOcd >= 0 && !sw.ContainsKey(o.Id))))
        {
            swChanged = true;
            if (o.SwapOcd >= 0) sw[o.Id] = new BaseSwap(o.Id, o.Name0, o.SwapOcd, o.SwapName); else sw.Remove(o.Id);
        }
        if (swChanged || File.Exists(BaseSwaps.PathFor(Project.ProjectFolder))) BaseSwaps.Save(Project.ProjectFolder, sw.Values);
        foreach (var o in Objectives)
        {
            if (o.DropEdit) { o.HadEdit = false; o.DropEdit = false; }
            else if (o.Changed) { o.HadEdit = true; o.JunctionLoaded = o.Junction; }
            o.SwapOcd0 = o.SwapOcd; o.SwapName0 = o.SwapName;
        }
        return old.Count;
    }

    /// <summary>Toglie la modifica salvata di un obiettivo (al prossimo Objects torna dove lo mette il tool).</summary>
    public void ForgetEdit(EdObjective o)
    {
        var old = ObjectiveEdits.Load(Project.ProjectFolder);
        if (old.Remove(o.Id)) ObjectiveEdits.Save(Project.ProjectFolder, old.Values);
        o.HadEdit = false;
    }

    // ───────────────────────── anteprima 3D ─────────────────────────

    private readonly Dictionary<int, BmlModel?> _models = new();

    /// <summary>Modello 3D (BML) di un pezzo, o null se non c'e' o non si legge.</summary>
    public BmlModel? Model(int ctIdx)
    {
        if (ModelsDir == null || !_ctFeature.TryGetValue(ctIdx, out var f)) return null;
        if (_models.TryGetValue(f.Graphics, out var m)) return m;
        m = null;
        string dir = ModelFolder(f.Graphics)!;
        // textures: KoreaObj next to the Models folder the model comes from
        string tex = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!, "KoreaObj");
        var why = new List<string>();
        foreach (var lod in Directory.Exists(dir) ? BmlModel.LodOrder(dir) : Enumerable.Empty<string>())
        {
            try { m = BmlModel.Load(lod, tex, BmsData == null ? null : Path.Combine(BmsData, "Terrdata", "Objects", "KoreaObj")); if (m.TriangleCount > 0) break; why.Add($"{Path.GetFileName(lod)}: no triangles"); m = null; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            { why.Add($"{Path.GetFileName(lod)}: {ex.Message}"); m = null; }
        }
        if (m == null) ModelErrors[f.Graphics] = (FeatureName(ctIdx), why.Count > 0 ? string.Join("; ", why) : Directory.Exists(dir) ? "no .bml files" : "folder Models\\" + f.Graphics + " missing");
        _models[f.Graphics] = m;
        return m;
    }

    /// <summary>Il modello si legge ma non ha superfici (luci, punti): non c'e' niente da disegnare.</summary>
    public bool IsEmptyModel(int ctIdx) =>
        _ctFeature.TryGetValue(ctIdx, out var f) && ModelErrors.TryGetValue(f.Graphics, out var e) && e.Why.Split("; ").All(w => w.EndsWith("no triangles"));

    /// <summary>Modelli che l'anteprima non ha letto: numero del modello → (nome del pezzo, motivo).</summary>
    public Dictionary<int, (string Name, string Why)> ModelErrors { get; } = new();

    private readonly Dictionary<string, (byte[] Bgra, int W, int H)?> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Texture di colore di un modello (file .dds, al massimo 512 pixel), o null.</summary>
    public (byte[] Bgra, int W, int H)? Texture(string? path)
    {
        if (path == null) return null;
        int sep = path.IndexOf(BmlModel.EmissionSep, StringComparison.Ordinal);
        if (sep >= 0) path = path[..sep];   // colore; l'emissione si chiede con EmissionTexture
        if (_textures.TryGetValue(path, out var t)) return t;
        t = File.Exists(path) ? DdsImage.Load(path, 512) : null;
        _textures[path] = t;
        return t;
    }

    /// <summary>Emission texture (glow at night) of a part Tex "albedo|E|emission", or null.</summary>
    public (byte[] Bgra, int W, int H)? EmissionTexture(string? tex)
    {
        if (tex == null) return null;
        int sep = tex.IndexOf(BmlModel.EmissionSep, StringComparison.Ordinal);
        return sep < 0 ? null : Texture(tex[(sep + BmlModel.EmissionSep.Length)..]);
    }

    /// <summary>
    /// Spianamento di BMS per un obiettivo (TerrainRelatedData\TLD_&lt;id&gt;.TPB scritto dal lavoro Objects): quota (piedi)
    /// e poligono (x nord, y est), riportato sulla posizione e la direzione attuali dell'editor. Null se non c'e'.
    /// </summary>
    /// <summary>
    /// Spianamento completo: quota e poligoni con la loro percentuale (100 = alla quota, 66/33 = raccordi), nelle coordinate attuali
    /// dell'obiettivo. Le basi hanno un solo poligono al 100%, i ponti uno per pezzo a terra piu' i raccordi.
    /// </summary>
    public (double Level, List<(double Pct, List<(double X, double Y)> Poly)> Polys)? LevelingAll(EdObjective o)
    {
        if (o.Junction) return null;
        if (o.SwapOcd != o.SwapOcd0) return ApproxLeveling(o) is { } ap ? (ap.Level, new List<(double, List<(double, double)>)> { (100, ap.Poly) }) : null;
        string f = Path.Combine(ObjectsDir, "TerrainRelatedData", $"TLD_{o.Id:00000}.TPB");
        if (!File.Exists(f)) return null;
        string t = File.ReadAllText(f);
        var lv = Regex.Match(t, @"terrain_level:\s*(-?[\d.]+)");
        if (!lv.Success) return null;
        double dh = (o.Heading - o.H0) * Math.PI / 180, ch = Math.Cos(dh), sh = Math.Sin(dh);
        var res = new List<(double, List<(double, double)>)>();
        foreach (Match pm in Regex.Matches(t, @"terrain_level_polygon\s*\{(.*?)\n  \}", RegexOptions.Singleline))
        {
            var pc = Regex.Match(pm.Groups[1].Value, @"terrain_leveling_percentage:\s*([\d.]+)");
            var poly = new List<(double, double)>();
            foreach (Match m in Regex.Matches(pm.Groups[1].Value, @"sim_grid_pos_x:\s*(-?[\d.]+(?:[eE][+-]?\d+)?)\s*sim_grid_pos_y:\s*(-?[\d.]+(?:[eE][+-]?\d+)?)"))
            {
                double x = D(m.Groups[1].Value) - o.X0, y = D(m.Groups[2].Value) - o.Y0;
                poly.Add((o.X + x * ch - y * sh, o.Y + x * sh + y * ch));
            }
            if (poly.Count >= 3) res.Add((pc.Success ? D(pc.Groups[1].Value) : 100, poly));
        }
        return res.Count > 0 ? (D(lv.Groups[1].Value), res) : null;
    }

    /// <summary>Area of an objective in BMS: leveling (TLD), land cover (LCD) or special polygon (SPD); points X north / Y east, absolute.</summary>
    public sealed record AreaPoly(string Kind, string Label, List<(double X, double Y)> Poly);

    private const string Num = @"-?[\d.]+(?:[eE][+-]?\d+)?";

    /// <summary>A point saved for the objective's original place, brought to where the editor has it now (moved/turned).</summary>
    private static (double X, double Y) Moved(EdObjective o, double x, double y)
    {
        double dh = (o.Heading - o.H0) * Math.PI / 180, ch = Math.Cos(dh), sh = Math.Sin(dh);
        double rx = x - o.X0, ry = y - o.Y0;
        return (o.X + rx * ch - ry * sh, o.Y + rx * sh + ry * ch);
    }

    /// <summary>
    /// Everything BMS keeps around an objective besides pieces and points: leveling polygons (TLD, with %), land cover (LCD, e.g.
    /// GTP_AIRBASE), special polygons (SPD) from TerrainRelatedData, and the trees of its layout (OTD_*.TPB in the OCD folder:
    /// tree model + positions). Read only. Trees are in absolute X north / Y east.
    /// </summary>
    public (List<AreaPoly> Areas, List<(double X, double Y)> Trees, List<string> TreeModels) AreasOf(EdObjective o)
    {
        var areas = new List<AreaPoly>();
        if (LevelingAll(o) is { } lv)
            foreach (var (pct, poly) in lv.Polys)
                areas.Add(new AreaPoly("Leveling", $"leveled to {lv.Level:0} ft, {pct:0}%", poly));
        string trd = Path.Combine(ObjectsDir, "TerrainRelatedData");
        List<(double, double)> Pts(string block) => Regex.Matches(block, $@"x:\s*({Num})\s*y:\s*({Num})")
            .Select(m => Moved(o, D(m.Groups[1].Value), D(m.Groups[2].Value))).ToList();
        try
        {
            string lcd = Path.Combine(trd, $"LCD_{o.Id:00000}.LPB");
            if (File.Exists(lcd))
                foreach (Match m in Regex.Matches(File.ReadAllText(lcd), @"landCoverPolygonProtoVec\s*\{(.*?)\n\}", RegexOptions.Singleline))
                {
                    var gt = Regex.Match(m.Groups[1].Value, @"groundType:\s*(\w+)");
                    var poly = Pts(m.Groups[1].Value);
                    if (poly.Count >= 3) areas.Add(new AreaPoly("Land cover", gt.Success ? gt.Groups[1].Value : "land cover", poly));
                }
            string spd = Path.Combine(trd, $"SPD_{o.Id:00000}.SPB");
            if (File.Exists(spd))
                foreach (Match m in Regex.Matches(File.ReadAllText(spd), @"specialPolygonData\s*\{(.*?)\n\}", RegexOptions.Singleline))
                {
                    var ty = Regex.Match(m.Groups[1].Value, @"^\s*type:\s*(\w+)", RegexOptions.Multiline);
                    var poly = Pts(m.Groups[1].Value);
                    if (poly.Count >= 3) areas.Add(new AreaPoly("Special", $"special polygon type {(ty.Success ? ty.Groups[1].Value : "?")}", poly));
                }
        }
        catch (IOException) { }
        var trees = new List<(double, double)>();
        var models = new List<string>();
        string dir = LayoutDir(o.Ocd);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "OTD_*.TPB"))
            {
                string t;
                try { t = File.ReadAllText(f); } catch (IOException) { continue; }
                var ct = Regex.Match(t, @"ct_idx:\s*(\d+)");
                int n0 = trees.Count;
                // nelle disposizioni x = est, y = nord (come OffsetX/OffsetY dei pezzi)
                foreach (Match m in Regex.Matches(t, $@"pos_x:\s*({Num})\s*pos_y:\s*({Num})"))
                    trees.Add(ToWorld(o, D(m.Groups[2].Value), D(m.Groups[1].Value)));
                if (trees.Count > n0)
                    models.Add($"{Path.GetFileName(f)}: {trees.Count - n0} trees");
            }
        return (areas, trees, models);
    }

    public (double Level, List<(double X, double Y)> Poly)? Leveling(EdObjective o)
    {
        if (o.Junction) return null;
        if (o.SwapOcd != o.SwapOcd0) return ApproxLeveling(o);   // base coreana non ancora passata dal lavoro Objects
        string f = Path.Combine(ObjectsDir, "TerrainRelatedData", $"TLD_{o.Id:00000}.TPB");
        if (!File.Exists(f)) return null;
        string t = File.ReadAllText(f);
        var lv = Regex.Match(t, @"terrain_level:\s*(-?[\d.]+)");
        if (!lv.Success) return null;
        var poly = new List<(double, double)>();
        double dh = (o.Heading - o.H0) * Math.PI / 180, ch = Math.Cos(dh), sh = Math.Sin(dh);
        foreach (Match m in Regex.Matches(t, @"sim_grid_pos_x:\s*(-?[\d.]+(?:[eE][+-]?\d+)?)\s*sim_grid_pos_y:\s*(-?[\d.]+(?:[eE][+-]?\d+)?)"))
        {
            double x = D(m.Groups[1].Value) - o.X0, y = D(m.Groups[2].Value) - o.Y0;
            poly.Add((o.X + x * ch - y * sh, o.Y + x * sh + y * ch));
        }
        return poly.Count >= 3 ? (D(lv.Groups[1].Value), poly) : null;
    }

    /// <summary>Spianamento stimato (come lo fara' il lavoro Objects): inviluppo dei punti a terra e dei pezzi d'asfalto con margine, quota al centro della pista.</summary>
    private (double Level, List<(double X, double Y)> Poly)? ApproxLeveling(EdObjective o)
    {
        var lay = LayoutOf(o);
        var pts = new List<(double X, double Y)>();
        void Add(double n, double e, double m)
        {
            var (x, y) = ToWorld(o, n, e);
            for (int a = 0; a < 8; a++) pts.Add((x + m * Math.Cos(a * Math.PI / 4), y + m * Math.Sin(a * Math.PI / 4)));
        }
        foreach (var p in lay.Paths) foreach (var q in p.Points) if (q.T is 1 or 2 or 3 or 11 or 12 or 16) Add(q.N, q.E, 600);
        foreach (var f in lay.Features) if (Regex.IsMatch(f.Name, @"\b(rwy|runway|taxi|taxiway|taxiways|apron|parking|thr|tdz)\b", RegexOptions.IgnoreCase)) Add(f.N, f.E, 1500);
        if (pts.Count < 3) return null;
        var rws = WorldRunways(o);
        var (cx, cy) = rws.Count > 0 ? (rws.MaxBy(r => r.Len).X, rws.MaxBy(r => r.Len).Y) : (o.X, o.Y);
        var hw = HeightWindow(cx, cy, 300, 3);
        double level = hw == null ? 0 : Math.Max(0, hw[1, 1]);
        return (level, Hull(pts));
    }

    private static List<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    public string HeightMapPath => Path.Combine(Project.OutputNewTerrain, "HeightMaps", "HeightMap.raw");

    /// <summary>
    /// Quote (piedi) su una griglia n×n che copre il quadrato di lato 2·half piedi centrato in (x nord, y est);
    /// riga 0 = nord. Dalla HeightMap.raw del NewTerrain (int16, riga 0 a nord), interpolazione bilineare.
    /// </summary>
    public float[,]? HeightWindow(double x, double y, double half, int n)
    {
        string f = HeightMapPath;
        if (!File.Exists(f)) return null;
        long len = new FileInfo(f).Length;
        int side = (int)Math.Round(Math.Sqrt(len / 2.0));
        double ftPx = Project.Theater.SizeMeters * 3.280839895 / side, sizeFt = Project.Theater.SizeMeters * 3.280839895;
        // pixel (colonna = est, riga = da nord) del bordo nord-ovest e di quello sud-est, con un pixel di margine
        int c0 = Math.Max(0, (int)Math.Floor((y - half) / ftPx) - 1), c1 = Math.Min(side - 1, (int)Math.Ceiling((y + half) / ftPx) + 1);
        int r0 = Math.Max(0, (int)Math.Floor((sizeFt - (x + half)) / ftPx) - 1), r1 = Math.Min(side - 1, (int)Math.Ceiling((sizeFt - (x - half)) / ftPx) + 1);
        int w = c1 - c0 + 1, h = r1 - r0 + 1;
        if (w < 2 || h < 2) return null;
        var raw = new short[w * h];
        var buf = new byte[w * 2];
        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
            for (int r = 0; r < h; r++)
            {
                fs.Position = ((long)(r0 + r) * side + c0) * 2;
                fs.ReadExactly(buf);
                Buffer.BlockCopy(buf, 0, raw, r * w * 2, w * 2);
            }
        var res = new float[n, n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double north = x + half - 2 * half * j / (n - 1), east = y - half + 2 * half * i / (n - 1);
                double fc = east / ftPx - 0.5 - c0, fr = (sizeFt - north) / ftPx - 0.5 - r0;
                int ic = Math.Clamp((int)Math.Floor(fc), 0, w - 2), ir = Math.Clamp((int)Math.Floor(fr), 0, h - 2);
                double tc = Math.Clamp(fc - ic, 0, 1), tr = Math.Clamp(fr - ir, 0, 1);
                double a = raw[ir * w + ic], b = raw[ir * w + ic + 1], c = raw[(ir + 1) * w + ic], d = raw[(ir + 1) * w + ic + 1];
                res[j, i] = (float)((a * (1 - tc) + b * tc) * (1 - tr) + (c * (1 - tc) + d * tc) * tr);
            }
        return res;
    }

    public string WaterMapPath => Path.Combine(Project.OutputNewTerrain, "HeightMaps", "WaterHeightMap.raw");

    /// <summary>Lato (pixel) di un RAW quadrato a 16 bit, o -1.</summary>
    private static int RawSide(string f)
    {
        if (!File.Exists(f)) return -1;
        long px = new FileInfo(f).Length / 2;
        int s = (int)Math.Round(Math.Sqrt(px));
        return (long)s * s == px ? s : -1;
    }

    /// <summary>
    /// Acqua come la vede BMS 4.38 (pelo dell'acqua della WaterHeightMap sopra il terreno della HeightMap, la stessa regola
    /// di WaterProbe usata per ponti, porti e mezzi) sul quadrato di lato 2·half piedi centrato in (x nord, y est):
    /// BGRA px×px (riga 0 = nord, centri dei pixel come PhotoArea) da mettere sopra la foto. Acqua = azzurro trasparente,
    /// riva (pixel d'acqua che toccano la terra) = ciano pieno. null se mancano le mappe.
    /// </summary>
    public byte[]? WaterOverlay(double x, double y, double half, int px, CancellationToken ct = default)
    {
        int hs = RawSide(HeightMapPath), ws = RawSide(WaterMapPath);
        if (hs <= 0 || ws <= 0 || px < 2) return null;
        double sizeFt = Project.Theater.SizeMeters * 3.280839895, pixFt = 2 * half / px;
        using var fh = new FileStream(HeightMapPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var fw = new FileStream(WaterMapPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        // valori di una mappa lungo una riga dell'uscita: bilineare da vicino, il pixel piu' vicino da lontano
        float[] Row(FileStream fs, int side, double north, Dictionary<int, short[]> cache, int c0, int c1)
        {
            double ftPx = sizeFt / side;
            bool near = pixFt > 2 * ftPx;
            short[] Get(int r)
            {
                r = Math.Clamp(r, 0, side - 1);
                if (cache.TryGetValue(r, out var a)) return a;
                if (cache.Count > 8) cache.Clear();
                var buf = new byte[(c1 - c0 + 1) * 2];
                fs.Position = ((long)r * side + c0) * 2;
                fs.ReadExactly(buf);
                a = new short[c1 - c0 + 1];
                Buffer.BlockCopy(buf, 0, a, 0, buf.Length);
                return cache[r] = a;
            }
            var res = new float[px];
            double fr = (sizeFt - north) / ftPx - 0.5;
            int ir = (int)Math.Floor(fr); double tr = fr - ir;
            var ra = near ? Get((int)Math.Round(fr)) : Get(ir);
            var rb = near ? ra : Get(ir + 1);
            int n = c1 - c0 + 1;
            for (int i = 0; i < px; i++)
            {
                double east = y - half + (i + 0.5) * pixFt;
                double fc = east / ftPx - 0.5 - c0;
                if (near) { int c = Math.Clamp((int)Math.Round(fc), 0, n - 1); res[i] = ra[c]; continue; }
                int ic = Math.Clamp((int)Math.Floor(fc), 0, n - 2); double tc = Math.Clamp(fc - ic, 0, 1);
                res[i] = (float)((ra[ic] * (1 - tc) + ra[ic + 1] * tc) * (1 - tr) + (rb[ic] * (1 - tc) + rb[ic + 1] * tc) * tr);
            }
            return res;
        }
        (int, int) Cols(int side)
        {
            double ftPx = sizeFt / side;
            int c0 = Math.Clamp((int)Math.Floor((y - half) / ftPx) - 1, 0, side - 2), c1 = Math.Clamp((int)Math.Ceiling((y + half) / ftPx) + 1, c0 + 1, side - 1);
            return (c0, c1);
        }
        var (hc0, hc1) = Cols(hs); var (wc0, wc1) = Cols(ws);
        var hCache = new Dictionary<int, short[]>(); var wCache = new Dictionary<int, short[]>();
        var water = new bool[px * px];
        var inside = new bool[px * px];
        for (int j = 0; j < px; j++)
        {
            ct.ThrowIfCancellationRequested();
            double north = x + half - (j + 0.5) * pixFt;
            if (north < 0 || north >= sizeFt) continue;
            var h = Row(fh, hs, north, hCache, hc0, hc1);
            var w = Row(fw, ws, north, wCache, wc0, wc1);
            for (int i = 0; i < px; i++)
            {
                double east = y - half + (i + 0.5) * pixFt;
                if (east < 0 || east >= sizeFt) continue;
                inside[j * px + i] = true;
                water[j * px + i] = w[i] > h[i] + 0.5;
            }
        }
        var outp = new byte[px * px * 4];
        for (int j = 0; j < px; j++)
            for (int i = 0; i < px; i++)
            {
                int k = j * px + i;
                if (!water[k]) continue;
                bool shore = (i > 0 && inside[k - 1] && !water[k - 1]) || (i < px - 1 && inside[k + 1] && !water[k + 1])
                          || (j > 0 && inside[k - px] && !water[k - px]) || (j < px - 1 && inside[k + px] && !water[k + px]);
                int o = k * 4;
                if (shore) { outp[o] = 255; outp[o + 1] = 255; outp[o + 2] = 0; outp[o + 3] = 255; }
                else { outp[o] = 255; outp[o + 1] = 110; outp[o + 2] = 20; outp[o + 3] = 110; }
            }
        return outp;
    }

    /// <summary>Foto (BGRA, px×px) del quadrato di lato 2·half piedi centrato in (x nord, y est), riga 0 = nord.</summary>
    public byte[]? PhotoArea(double x, double y, double half, int px)
    {
        if (Photo == null) return null;
        var ph = Photo;
        double ftM = 3.280839895, pixFt = 2 * half / px;
        double baseFt = ph.TileM * ftM / PhotoTiles.TilePx;
        int mip = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, pixFt / baseFt))), 0, 8);
        int w = PhotoTiles.TilePx >> mip, side = Math.Min(PhotoTiles.Chunk, w), per = w / side;
        double chunkFt = ph.TileM * ftM / per, sizeFt = ph.SizeM * ftM;
        var outp = new byte[px * px * 4];
        var local = new Dictionary<(int, int), (byte[]? Px, int S)>();
        for (int j = 0; j < px; j++)
            for (int i = 0; i < px; i++)
            {
                double north = x + half - (j + 0.5) * pixFt, east = y - half + (i + 0.5) * pixFt;
                double fromTop = sizeFt - north;
                if (east < 0 || fromTop < 0 || east >= sizeFt || fromTop >= sizeFt) continue;
                int cc = (int)(east / chunkFt), rr = (int)(fromTop / chunkFt);
                if (!local.TryGetValue((cc, rr), out var hit))
                {
                    var got = ph.GetChunk(cc / per, rr / per, mip, cc % per, rr % per, out int gs);
                    local[(cc, rr)] = hit = (got, gs);
                }
                var (ch, s) = hit;
                if (ch == null) continue;
                int u = Math.Clamp((int)((east - cc * chunkFt) / chunkFt * s), 0, s - 1), v = Math.Clamp((int)((fromTop - rr * chunkFt) / chunkFt * s), 0, s - 1);
                Buffer.BlockCopy(ch, (v * s + u) * 4, outp, (j * px + i) * 4, 4);
            }
        return outp;
    }

    /// <summary>Model number (CT GraphicsNormal) and FCD record of a feature (building/piece), or -1.</summary>
    public (int Graphics, int Fcd) FeatureIds(int ctIdx) => _ctFeature.TryGetValue(ctIdx, out var f) ? f : (-1, -1);

    /// <summary>Models\&lt;n&gt; folder of a feature (theater first, then BMS), or null.</summary>
    public string? FeatureModelDir(int ctIdx) =>
        _ctFeature.TryGetValue(ctIdx, out var f) && f.Graphics > 0 && ModelFolder(f.Graphics) is { } d && Directory.Exists(d) ? d : null;

    /// <summary>Texture folders for a model folder: KoreaObj(_HiRes) next to it, then the BMS ones (theaters often use the BMS textures).</summary>
    public List<string> TextureDirsFor(string modelDir)
    {
        var res = new List<string>();
        var roots = new List<string> { Path.GetDirectoryName(Path.GetDirectoryName(modelDir)!)! };
        if (BmsData != null) roots.Add(Path.Combine(BmsData, "Terrdata", "Objects"));
        foreach (var root in roots)
            foreach (var n in new[] { "KoreaObj_HiRes", "KoreaObj" })
            {
                string d = Path.Combine(root, n);
                if (Directory.Exists(d) && !res.Contains(d, StringComparer.OrdinalIgnoreCase)) res.Add(d);
            }
        return res;
    }

    /// <summary>All the fields of the FCD record of a feature (as in Falcon4_FCD.xml), empty if none.</summary>
    public IReadOnlyList<(string Field, string Value)> FeatureData(int ctIdx) =>
        _ctFeature.TryGetValue(ctIdx, out var f) && _fcdFields.TryGetValue(f.Fcd, out var l) ? l : Array.Empty<(string, string)>();

    /// <summary>How many objectives (and how many different layouts) have this feature among their pieces.</summary>
    public (int Objectives, int Layouts, List<string> Examples) FeatureUsage(int ctIdx)
    {
        var hasIt = new Dictionary<EdLayout, bool>(ReferenceEqualityComparer.Instance);
        int objs = 0; var ex = new List<string>();
        foreach (var o in Objectives)
        {
            EdLayout l;
            try { l = BaseLayoutOf(o); } catch (Exception) { continue; }
            if (!hasIt.TryGetValue(l, out bool h)) hasIt[l] = h = l.Features.Any(f => f.CtIdx == ctIdx);
            if (!h) continue;
            objs++;
            if (ex.Count < 8) ex.Add(o.Name);
        }
        return (objs, hasIt.Count(kv => kv.Value), ex);
    }

    public string FeatureName(int ctIdx) => _ctFeature.TryGetValue(ctIdx, out var f) ? _fcdName.GetValueOrDefault(f.Fcd, $"CT {ctIdx}") : $"CT {ctIdx}";

    /// <summary>Sagoma del modello (min/max lungo l'asse del pezzo e di traverso, piedi) dal Parent.dat, se c'e'.</summary>
    private (double MinX, double MaxX, double MinY, double MaxY)? Dims(int ctIdx)
    {
        if (!_ctFeature.TryGetValue(ctIdx, out var f) || ModelsDir == null) return null;
        if (_dims.TryGetValue(f.Graphics, out var d)) return d;
        d = null;
        try
        {
            string pd = Path.Combine(ModelFolder(f.Graphics)!, "Parent.dat");
            if (File.Exists(pd))
                foreach (var l in File.ReadLines(pd))
                {
                    var m = Regex.Match(l, @"^\s*Dimensions\s*=\s*(.*)$");
                    if (!m.Success) continue;
                    var v = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(D).ToArray();
                    if (v.Length >= 5) d = (v[1], v[2], v[3], v[4]);
                    break;
                }
        }
        catch (IOException) { }
        _dims[f.Graphics] = d;
        return d;
    }

    /// <summary>Quote dei pezzi provate prima di Run Objects (Optimize bridges): CampId → (FED Num → OffsetZ da aggiungere, piedi, + = giu').</summary>
    public Dictionary<int, Dictionary<int, double>> ExtraZ { get; } = new();

    public EdLayout Layout(int ocd)
    {
        if (_layouts.TryGetValue(ocd, out var l)) return l;
        l = LoadLayout(LayoutDir(ocd));
        _layouts[ocd] = l;
        return l;
    }

    private Dictionary<int, (int Ocd, double Heading)> _baked = new();
    private string _bakedDir = "";
    /// <summary>Cartella della disposizione come la vede l'editor (non girata, se il lavoro Objects ci ha messo dentro la rotazione).</summary>
    public string LayoutPath(int ocd) => LayoutDir(ocd);
    private string LayoutDir(int ocd)
    {
        string u = Path.Combine(_bakedDir, $"OCD_{ocd:00000}");
        return _bakedDir.Length > 0 && Directory.Exists(u) ? u : Path.Combine(ObjectsDir, "ObjectiveRelatedData", $"OCD_{ocd:00000}");
    }
    private string? KoreaDir(int ocd) =>
        BaseSwaps.FromTheater(ocd) ? Path.Combine(ObjectsDir, "ObjectiveRelatedData", $"OCD_{BaseSwaps.RealOcd(ocd):00000}")
        : KoreaOrd == null ? null : Path.Combine(KoreaOrd, $"OCD_{ocd:00000}");

    private readonly Dictionary<int, EdLayout> _koreaLayouts = new();

    /// <summary>Disposizione di una base della 4.38 (cartella di BMS, sola lettura).</summary>
    public EdLayout KoreaLayout(int ocd)
    {
        if (_koreaLayouts.TryGetValue(ocd, out var l)) return l;
        l = KoreaDir(ocd) is string d ? LoadLayout(d) : new EdLayout();
        _koreaLayouts[ocd] = l;
        return l;
    }

    /// <summary>La disposizione da disegnare: quella della base coreana scelta, se c'e', altrimenti la sua.</summary>
    /// <remarks>Base coreana gia' passata dal lavoro Objects (salvata e con la sua copia OCD): si mostra quella copia, che ha
    /// anche le piste generiche messe al posto di quelle con i numeri scritti.</remarks>
    public EdLayout LayoutOf(EdObjective o)
    {
        var b = BaseLayoutOf(o);
        if (o.Pieces.Count == 0 && o.PointMoves.Count == 0) return b;
        if (_own.TryGetValue(o.Id, out var c) && c.Version == o.PieceVersion && ReferenceEquals(c.Base, b)) return c.Layout;
        var l = new EdLayout { Radius = b.Radius };
        l.RunwayHeads.AddRange(b.RunwayHeads);
        l.PointFlags.AddRange(b.PointFlags);
        if (o.PointMoves.Count == 0) { l.Paths.AddRange(b.Paths); l.Points.AddRange(b.Points); }
        else
        {
            foreach (var q in b.Points)
                l.Points.Add(o.PointMoves.TryGetValue(q.Num, out var pm) ? q with { N = pm.N, E = pm.E, Heading = pm.Heading } : q);
            foreach (var path in b.Paths)
            {
                if (path.Idx.Count != path.Points.Count || !path.Idx.Any(o.PointMoves.ContainsKey)) { l.Paths.Add(path); continue; }
                var pts = path.Points.Select((q, i) => o.PointMoves.TryGetValue(path.Idx[i], out var pm) ? (pm.N, pm.E, q.T) : q).ToList();
                l.Paths.Add(path with { Points = pts });
            }
            foreach (var q in l.Points) l.Radius = Math.Max(l.Radius, Math.Sqrt(q.N * q.N + q.E * q.E));
        }
        var seen = new HashSet<int>();
        foreach (var f in b.Features)
        {
            seen.Add(f.Num);
            if (!o.Pieces.TryGetValue(f.Num, out var pe)) { l.Features.Add(f); continue; }
            if (pe.Removed) continue;
            l.Features.Add(f with { N = pe.N, E = pe.E, Heading = pe.Heading });
        }
        // pezzi gia' sepolti da un Run Objects (non nella disposizione letta) e poi rimessi: dalla modifica
        foreach (var pe in o.Pieces.Values.Where(p => !p.Removed && !seen.Contains(p.Num)))
        {
            var dm = Dims(pe.CtIdx) ?? (-40, 40, -40, 40);
            l.Features.Add(new EdFeature(pe.N, pe.E, pe.Heading, pe.CtIdx, FeatureName(pe.CtIdx), dm.MinX, dm.MaxX, dm.MinY, dm.MaxY, pe.Num));
        }
        foreach (var f in l.Features)
        {
            double ext = Math.Max(Math.Max(Math.Abs(f.MinX), Math.Abs(f.MaxX)), Math.Max(Math.Abs(f.MinY), Math.Abs(f.MaxY)));
            l.Radius = Math.Max(l.Radius, Math.Sqrt(f.N * f.N + f.E * f.E) + ext);
        }
        _own[o.Id] = (o.PieceVersion, b, l);
        return l;
    }

    private readonly Dictionary<int, (int Version, EdLayout Base, EdLayout Layout)> _own = new();

    /// <summary>Si possono toccare i singoli pezzi? Non sui ponti (il lavoro Objects ne sposta e livella i pezzi) ne' sugli incroci.</summary>
    public static bool CanEditPieces(EdObjective o) => !o.Junction && o.Type is not (6 or 15) && o.SwapOcd == o.SwapOcd0;

    /// <summary>Sposta/gira/toglie un pezzo dell'obiettivo (solo suo: gli altri obiettivi con la stessa disposizione non cambiano).</summary>
    public void SetPiece(EdObjective o, EdFeature f, double n, double e, double heading, bool removed)
    {
        heading = ((heading % 360) + 360) % 360;
        var pe = o.Pieces.TryGetValue(f.Num, out var old)
            ? old with { N = n, E = e, Heading = heading, Removed = removed }
            : new PieceEdit(o.Id, f.Num, f.CtIdx, n, e, heading, removed, f.N, f.E, f.Heading);
        o.Pieces[f.Num] = pe;
        o.PieceVersion++;
        o.PiecesDirty = true;
    }

    /// <summary>Punti che si possono spostare: non quelli di piste e vie di rullaggio (testate, rullaggio, parcheggi lungo le vie, angoli della pista, attese).</summary>
    public static bool CanEditPoint(int type) => type is not (1 or 2 or 3 or 8 or 11 or 12 or 15);

    /// <summary>Sposta/gira un punto della disposizione dell'obiettivo (dove BMS mette un mezzo: lanciatore, cannone, radar, camion...).</summary>
    public void SetPoint(EdObjective o, EdPoint p, double n, double e, double heading)
    {
        heading = ((heading % 360) + 360) % 360;
        o.PointMoves[p.Num] = o.PointMoves.TryGetValue(p.Num, out var old)
            ? old with { N = n, E = e, Heading = heading }
            : new PointEdit(o.Id, p.Num, p.T, n, e, heading, p.N, p.E, p.Heading);
        o.PieceVersion++;
        o.PiecesDirty = true;
    }

    /// <summary>Rimette tutti i pezzi dell'obiettivo come li ha la disposizione dell'add-on (al prossimo salvataggio la modifica sparisce).</summary>
    public void ResetPieces(EdObjective o)
    {
        foreach (var k in o.PointMoves.Keys.ToList())
        {
            var pm = o.PointMoves[k];
            o.PointMoves[k] = pm with { N = pm.OrigN, E = pm.OrigE, Heading = pm.OrigHeading };
        }
        foreach (var k in o.Pieces.Keys.ToList())
        {
            var pe = o.Pieces[k];
            o.Pieces[k] = pe with { N = pe.OrigN, E = pe.OrigE, Heading = pe.OrigHeading, Removed = false };
        }
        o.PieceVersion++;
        o.PiecesDirty = true;
    }

    /// <summary>Pezzi tolti (per rimetterli uno a uno).</summary>
    public IEnumerable<PieceEdit> RemovedPieces(EdObjective o) => o.Pieces.Values.Where(p => p.Removed);

    private Dictionary<int, (string Source, int Ocd, int NewOcd)> _applied = new();

    private EdLayout BaseLayoutOf(EdObjective o)
    {
        if (o.SwapOcd < 0 && o.RepOcd >= 0)
        {
            // gia' fatta da Run Objects (copia propria dell'obiettivo, con i pezzi ritoccati dopo): quella; altrimenti la sorgente
            if (!o.RepChanged && _applied.TryGetValue(o.Id, out var ap) && ap.Source == o.RepSource && ap.Ocd == o.RepOcd && ap.NewOcd == o.Ocd && Directory.Exists(LayoutDir(o.Ocd)))
                return Layout(o.Ocd);
            return o.RepSource == LayoutSwaps.Bms ? KoreaLayout(o.RepOcd) : Layout(o.RepOcd);
        }
        // tolta dopo un Run Objects che l'aveva messa: la disposizione di prima
        if (o.SwapOcd < 0 && o.RepOcd < 0 && _applied.TryGetValue(o.Id, out var ap2) && ap2.NewOcd == o.Ocd) return Layout(o.OrigOcd);
        return KoreaOrBase(o);
    }

    private EdLayout KoreaOrBase(EdObjective o) =>
        o.SwapOcd >= 0 && KoreaOrd != null && !(o.SwapOcd == o.SwapOcd0 && o.Ocd != o.OrigOcd && o.Ocd0 == o.Ocd && Directory.Exists(LayoutDir(o.Ocd)) && o.Ocd > 1000)
            ? KoreaLayout(o.SwapOcd) : Layout(o.Ocd);

    private EdLayout LoadLayout(string dir)
    {
        var l = new EdLayout();
        double r = 100;
        string? fed = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "FED_*.XML").FirstOrDefault() : null;
        if (fed != null)
            foreach (Match m in Regex.Matches(File.ReadAllText(fed), @"<FED Num=""(\d+)"">(.*?)</FED>", RegexOptions.Singleline))
            {
                string b = m.Groups[2].Value;
                int num = int.Parse(m.Groups[1].Value, Ci);
                string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
                if (D(G("OffsetZ")) > 1000) continue;   // pezzi sepolti (piste vecchie tolte)
                int ci = int.TryParse(G("FeatureCtIdx"), NumberStyles.Integer, Ci, out var c) ? c : -1;
                // nelle disposizioni OffsetX = est, OffsetY = nord
                double e = D(G("OffsetX")), n = D(G("OffsetY")), h = D(G("Heading"));
                var dm = Dims(ci) ?? (-40, 40, -40, 40);
                l.Features.Add(new EdFeature(n, e, h, ci, FeatureName(ci), dm.MinX, dm.MaxX, dm.MinY, dm.MaxY, num, D(G("OffsetZ"))));
                double ext = Math.Max(Math.Max(Math.Abs(dm.MinX), Math.Abs(dm.MaxX)), Math.Max(Math.Abs(dm.MinY), Math.Abs(dm.MaxY)));
                r = Math.Max(r, Math.Sqrt(n * n + e * e) + ext);
            }
        string? phd = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PHD_*.XML").FirstOrDefault() : null;
        string? pdx = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PDX_*.XML").FirstOrDefault() : null;
        if (phd != null && pdx != null)
        {
            var pts = new List<(double N, double E, int T)>();
            foreach (Match m in Regex.Matches(File.ReadAllText(pdx), @"<PD Num=""\d+"">(.*?)</PD>", RegexOptions.Singleline))
            {
                var x = Regex.Match(m.Groups[1].Value, "<OffsetX>(.*?)</OffsetX>"); var y = Regex.Match(m.Groups[1].Value, "<OffsetY>(.*?)</OffsetY>");
                var t = Regex.Match(m.Groups[1].Value, "<Type>(\\d+)</Type>");
                pts.Add((D(y.Groups[1].Value), D(x.Groups[1].Value), t.Success ? int.Parse(t.Groups[1].Value, Ci) : 0));
                var hd = Regex.Match(m.Groups[1].Value, "<Heading>(.*?)</Heading>");
                l.Points.Add(new EdPoint(l.Points.Count, pts[^1].N, pts[^1].E, pts[^1].T, hd.Success ? D(hd.Groups[1].Value) : 0));
                var fl = Regex.Match(m.Groups[1].Value, "<Flags>(-?\\d+)</Flags>");
                l.PointFlags.Add(fl.Success ? int.Parse(fl.Groups[1].Value, Ci) : 0);
            }
            foreach (Match h in Regex.Matches(File.ReadAllText(phd), @"<PHD Num=""(\d+)"">(.*?)</PHD>", RegexOptions.Singleline))
            {
                string b = h.Groups[2].Value;
                string G(string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>"); return x.Success ? x.Groups[1].Value : ""; }
                if (!int.TryParse(G("FirstPtIdx"), NumberStyles.Integer, Ci, out int f) || !int.TryParse(G("PointCount"), NumberStyles.Integer, Ci, out int cnt)) continue;
                if (f < 0 || cnt < 1 || f + cnt > pts.Count) continue;
                int type = int.TryParse(G("Type"), NumberStyles.Integer, Ci, out var t) ? t : 0;
                if (type == 1 && int.TryParse(G("FeatureDependencyIdx_0"), NumberStyles.Integer, Ci, out int dep0))
                    l.RunwayHeads.Add((dep0, D(G("Data"))));
                var list = pts.GetRange(f, cnt);
                l.Paths.Add(new EdPath(type, list)
                {
                    Idx = Enumerable.Range(f, cnt).ToList(), HeaderNum = int.Parse(h.Groups[1].Value, Ci),
                    Header = Regex.Matches(b, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline).Select(x => (x.Groups[1].Value, x.Groups[2].Value.Trim())).ToList(),
                });
                foreach (var (n, e, _) in list) r = Math.Max(r, Math.Sqrt(n * n + e * e));
            }
        }
        l.Radius = r;
        return l;
    }

    // ───────────────────────── disposizioni da scegliere ─────────────────────────

    /// <summary>Una disposizione che si puo' mettere al posto di quella di un obiettivo.</summary>
    public sealed record LayoutChoice(string Source, int Ocd, string Name, int Type, int Pieces, double SizeM, double SpanM, double AxisDeg)
    {
        public string SourceText => Source == LayoutSwaps.Bms ? "BMS 4.38" : "Mideast";
        public string TypeText => EditorData.TypeName(Type);
    }

    private List<LayoutChoice>? _catalog;

    /// <summary>
    /// Tutte le disposizioni: quelle dell'add-on (come le ha preparate Run Objects) e quelle della 4.38 (cartella di BMS). Tipo dal
    /// database (Falcon4_CT); misure dalla disposizione. Lenta la prima volta (qualche secondo): si chiama fuori dal thread della finestra.
    /// </summary>
    public List<LayoutChoice> LayoutCatalog()
    {
        if (_catalog != null) return _catalog;
        var res = new List<LayoutChoice>();
        string oldOrd = Project.IsInstalledTheater ? Path.Combine(ObjectsDir, "ObjectiveRelatedData") : Path.Combine(Project.OldTheaterFolder, "Terrdata", "Objects", "ObjectiveRelatedData");
        var mideast = Directory.Exists(oldOrd) ? Directory.EnumerateDirectories(oldOrd, "OCD_*").Select(d => int.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, Ci, out int n) ? n : -1).Where(n => n >= 0).ToHashSet() : new HashSet<int>();
        string outOrd = Path.Combine(ObjectsDir, "ObjectiveRelatedData");
        void Add(string src, int n, string dir, EdLayout lay)
        {
            int t = _ocdType.GetValueOrDefault(n, -1);
            if (t <= 0 || lay.Features.Count == 0) return;
            string? ox = Directory.EnumerateFiles(dir, "OCD_*.XML").FirstOrDefault();
            var nm = ox == null ? null : Regex.Match(File.ReadAllText(ox), "<Name>(.*?)</Name>");
            string name = nm is { Success: true } ? System.Net.WebUtility.HtmlDecode(nm.Groups[1].Value).Trim() : $"OCD {n}";
            var (axis, span) = Axis(lay.Features);
            res.Add(new LayoutChoice(src, n, name, t, lay.Features.Count, lay.Radius * 2 * 0.3048, span * 0.3048, axis));
        }
        foreach (int n in mideast.OrderBy(x => x))
        {
            string dir = Path.Combine(outOrd, $"OCD_{n:00000}");
            if (Directory.Exists(dir)) Add(LayoutSwaps.Mideast, n, dir, LoadLayout(dir));
        }
        if (KoreaOrd != null)
            foreach (var dir in Directory.EnumerateDirectories(KoreaOrd, "OCD_*").OrderBy(d => d))
                if (int.TryParse(Path.GetFileName(dir)[4..], NumberStyles.Integer, Ci, out int n)) Add(LayoutSwaps.Bms, n, dir, KoreaLayout(n));
        _catalog = res;
        return res;
    }

    /// <summary>Asse lungo dei pezzi (gradi in senso orario dal nord della disposizione) e lunghezza lungo l'asse (piedi), dalle sagome.</summary>
    public static (double AxisDeg, double SpanFt) Axis(IEnumerable<EdFeature> features)
    {
        var pts = new List<(double N, double E)>();
        foreach (var f in features)
        {
            if (f.Z > 100) continue;
            double h = f.Heading * Math.PI / 180, fn = Math.Cos(h), fe = Math.Sin(h), rn = -Math.Sin(h), re = Math.Cos(h);
            foreach (var (x, y) in new[] { (f.MinX, f.MinY), (f.MaxX, f.MinY), (f.MaxX, f.MaxY), (f.MinX, f.MaxY) })
                pts.Add((f.N + x * fn + y * rn, f.E + x * fe + y * re));
        }
        if (pts.Count == 0) return (0, 0);
        double mn = pts.Average(p => p.N), me = pts.Average(p => p.E), snn = 0, see = 0, sne = 0;
        foreach (var (n, e) in pts) { snn += (n - mn) * (n - mn); see += (e - me) * (e - me); sne += (n - mn) * (e - me); }
        double ang = 0.5 * Math.Atan2(2 * sne, snn - see);   // dal nord verso est
        double cn = Math.Cos(ang), ce = Math.Sin(ang);
        var proj = pts.Select(p => p.N * cn + p.E * ce).ToList();
        return ((ang * 180 / Math.PI + 360) % 180, proj.Max() - proj.Min());
    }

    /// <summary>
    /// Larghezza dell'acqua (metri) attraversata dalla linea per il centro del ponte lungo il suo asse (gradi nel teatro): il tratto
    /// d'acqua che contiene il centro, o il piu' vicino entro 2 km. 0 = niente acqua.
    /// </summary>
    public static double RiverWidthM(WaterProbe w, double cx, double cy, double axisDeg)
    {
        double a = axisDeg * Math.PI / 180, dn = Math.Cos(a), de = Math.Sin(a);
        double step = Math.Max(20, w.PixelFt / 2), reach = 2000 * 3.280839895;
        int k = (int)(reach / step);
        var wet = new bool[2 * k + 1];
        for (int i = -k; i <= k; i++) wet[i + k] = w.IsWater(cx + dn * i * step, cy + de * i * step);
        int best = -1, bestDist = int.MaxValue, bestLen = 0;
        for (int i = 0; i < wet.Length;)
        {
            if (!wet[i]) { i++; continue; }
            int j = i; while (j < wet.Length && wet[j]) j++;
            int dist = k >= i && k < j ? 0 : Math.Min(Math.Abs(i - k), Math.Abs(j - 1 - k));
            if (dist < bestDist) { bestDist = dist; best = i; bestLen = j - i; }
            i = j;
        }
        return best < 0 ? 0 : bestLen * step * 0.3048;
    }

    /// <summary>Centro e asse (gradi nel teatro) del ponte com'e' messo ora, e la sua lunghezza (piedi).</summary>
    public (double X, double Y, double AxisDeg, double SpanFt) BridgeAxis(EdObjective o)
    {
        var feats = LayoutOf(o).Features.Where(f => f.Z <= 100).ToList();
        var (ax, span) = Axis(feats);
        double cn = feats.Count > 0 ? feats.Average(f => f.N) : 0, ce = feats.Count > 0 ? feats.Average(f => f.E) : 0;
        var (x, y) = ToWorld(o, cn, ce);
        return (x, y, (ax + o.Heading + 360) % 180, span);
    }

    /// <summary>
    /// Mette (o con null toglie) una disposizione scelta al posto di quella dell'obiettivo. I pezzi e i punti ritoccati a mano
    /// si tolgono (erano della disposizione vecchia). Ponti: la disposizione nuova si gira e si sposta sull'asse e sul centro di
    /// quella di prima, cosi' attraversa lo stesso fiume.
    /// </summary>
    public void SetReplacement(EdObjective o, LayoutChoice? c)
    {
        (double X, double Y, double AxisDeg, double SpanFt)? bridge = (o.Type == 6 || (c?.Type ?? 0) == 6) ? BridgeAxis(o) : null;
        o.Pieces.Clear(); o.PointMoves.Clear(); o.PiecesDirty = true; o.PieceVersion++;
        if (c == null) { o.RepOcd = -1; o.RepSource = ""; o.RepName = ""; o.RepType = -1; o.Type = o.OrigType; }
        else { o.RepOcd = c.Ocd; o.RepSource = c.Source; o.RepName = c.Name; o.RepType = c.Type; o.Type = c.Type; }
        _own.Remove(o.Id);
        if (bridge is { } b && c != null)
        {
            var lay = LayoutOf(o);
            var feats = lay.Features.Where(f => f.Z <= 100).ToList();
            if (feats.Count > 0)
            {
                // l'asse della disposizione nuova sull'asse del ponte di prima (tra i due sensi, quello che gira meno)
                double h = b.AxisDeg - c.AxisDeg;
                double turn = ((h - o.Heading) % 180 + 270) % 180 - 90;
                o.Heading = ((o.Heading + turn) % 360 + 360) % 360;
                double cn = feats.Average(f => f.N), ce = feats.Average(f => f.E);
                var (wx, wy) = ToWorld(o, cn, ce);
                o.X += b.X - wx; o.Y += b.Y - wy;
            }
        }
    }

    // ───────────────────────── basi coreane ─────────────────────────

    private List<KoreaBase>? _korea;

    /// <summary>Basi della Korea 4.38 con le piste nella disposizione (lette alla prima richiesta).</summary>
    /// <remarks>Teatro installato: anche le basi del teatro stesso (comprese quelle importate con Database Interchange).</remarks>
    public List<KoreaBase> Korea => _korea ??= (BmsData == null ? new List<KoreaBase>() : KoreaBases.Load(BmsData, FeatureName))
        .Concat(Project.IsInstalledTheater ? KoreaBases.LoadTheater(Path.Combine(ObjectsDir, "ObjectiveRelatedData"), FeatureName) : new List<KoreaBase>()).ToList();

    /// <summary>Piste dell'obiettivo come e' messo ora (coordinate del teatro), dalla disposizione che disegna l'editor.</summary>
    public List<Runway> WorldRunways(EdObjective o)
    {
        string? dir = o.SwapOcd >= 0 ? KoreaDir(o.SwapOcd) : LayoutDir(o.Ocd);
        if (dir == null) return new List<Runway>();
        if (!_localRunways.TryGetValue(dir, out var local))
            _localRunways[dir] = local = Directory.Exists(dir)
                ? ObjectsJob.LayoutRunways(dir).Where(r => r.Len >= 2500).Select(r => new Runway(r.X, r.Y, r.Hdg, r.Len, r.Num)).ToList()
                : new List<Runway>();
        double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
        return local.Select(r => new Runway(o.X + r.X * ch - r.Y * sh, o.Y + r.X * sh + r.Y * ch, ((r.Hdg + o.Heading) % 360 + 360) % 360, r.Len, r.Num)).ToList();
    }

    private readonly Dictionary<string, List<Runway>> _localRunways = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Piste reali di airbase_targets.csv (testate in gradi): centro e direzione nel teatro e direzione geografica.</summary>
    private void LoadRealRunways()
    {
        string csv = Path.Combine(Project.ProjectFolder, "airbase_targets.csv");
        if (!File.Exists(csv)) return;
        var lines = File.ReadAllLines(csv);
        if (lines.Length < 2) return;
        var head = lines[0].TrimStart('\uFEFF').Split(';').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int cId = head.IndexOf("campid"), c1 = head.IndexOf("lelat"), c2 = head.IndexOf("lelon"), c3 = head.IndexOf("helat"), c4 = head.IndexOf("helon"), cNote = head.IndexOf("note"), cIcao = head.IndexOf("icao");
        if (cId < 0 || c1 < 0 || c2 < 0 || c3 < 0 || c4 < 0) return;
        var grid = Project.Theater.CreateGrid();
        var byId = Objectives.ToDictionary(o => o.Id);
        foreach (var l in lines.Skip(1))
        {
            var f = l.Split(';');
            if (f.Length <= Math.Max(Math.Max(c1, c2), Math.Max(c3, Math.Max(c4, cId))) || !int.TryParse(f[cId], NumberStyles.Integer, Ci, out int id) || !byId.TryGetValue(id, out var o)) continue;
            if (!double.TryParse(f[c1], NumberStyles.Float, Ci, out double la1) || !double.TryParse(f[c2], NumberStyles.Float, Ci, out double lo1)
                || !double.TryParse(f[c3], NumberStyles.Float, Ci, out double la2) || !double.TryParse(f[c4], NumberStyles.Float, Ci, out double lo2)) continue;
            var (e1, n1) = grid.ToTheater(la1, lo1); var (e2, n2) = grid.ToTheater(la2, lo2);
            const double ft = 3.280839895;
            double mapHdg = ((Math.Atan2(e2 - e1, n2 - n1) * 180 / Math.PI) + 360) % 360;
            // direzione geografica (vera) sulla sfera, dalla prima testata alla seconda
            double p1 = la1 * Math.PI / 180, p2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            double trueHdg = (Math.Atan2(Math.Sin(dl) * Math.Cos(p2), Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl)) * 180 / Math.PI + 360) % 360;
            string src = cNote >= 0 && cNote < f.Length && f[cNote].StartsWith("OSM", StringComparison.OrdinalIgnoreCase) ? "OpenStreetMap" : "OurAirports" + (cIcao >= 0 && cIcao < f.Length && f[cIcao].Length > 0 ? " " + f[cIcao] : "");
            o.Real = ((n1 + n2) / 2 * ft, (e1 + e2) / 2 * ft, mapHdg, trueHdg, Math.Sqrt((e2 - e1) * (e2 - e1) + (n2 - n1) * (n2 - n1)) * ft, src);
        }
    }

    /// <summary>Aggiorna la direzione della pista principale mostrata in tabella (dopo ogni modifica).</summary>
    public void UpdateRunway(EdObjective o) => o.RwyHdg = o.Type is 1 or 2 || o.SwapOcd >= 0 ? MainRunway(o) is Runway r ? r.Hdg % 180 : null : null;

    /// <summary>Mette la pista principale sulla pista reale: stessa direzione e stesso centro. False se non c'e' la pista reale.</summary>
    public bool AlignToReal(EdObjective o)
    {
        if (o.Real is not { } real || !SetRunwayHeading(o, real.MapHdg) || MainRunway(o) is not Runway r) return false;
        o.X += real.X - r.X; o.Y += real.Y - r.Y;
        return true;
    }

    /// <summary>Pista principale (la piu' lunga) dell'obiettivo nel teatro, o null se non ne ha.</summary>
    public Runway? MainRunway(EdObjective o) { var r = WorldRunways(o); return r.Count > 0 ? r.MaxBy(x => x.Len) : null; }

    /// <summary>Punto attorno a cui si ruota l'obiettivo: centro della pista principale, o l'obiettivo stesso.</summary>
    public (double X, double Y) Pivot(EdObjective o) => MainRunway(o) is Runway r ? (r.X, r.Y) : (o.X, o.Y);

    /// <summary>Ruota l'obiettivo di <paramref name="delta"/> gradi (orari) attorno al punto (px, py) partendo dalla posa data.</summary>
    public static void RotateAbout(EdObjective o, double x0, double y0, double h0, double delta, double px, double py)
    {
        double d = delta * Math.PI / 180, c = Math.Cos(d), s = Math.Sin(d);
        double vx = x0 - px, vy = y0 - py;
        o.X = px + vx * c - vy * s; o.Y = py + vx * s + vy * c;
        o.Heading = ((h0 + delta) % 360 + 360) % 360;
    }

    /// <summary>Gira la base finche' la pista principale ha la direzione <paramref name="heading"/> (gradi, una pista vale nei due sensi).</summary>
    public bool SetRunwayHeading(EdObjective o, double heading)
    {
        if (MainRunway(o) is not Runway r) return false;
        double delta = ((heading - r.Hdg) % 180 + 180) % 180; if (delta > 90) delta -= 180;
        RotateAbout(o, o.X, o.Y, o.Heading, delta, r.X, r.Y);
        return true;
    }

    /// <summary>Testo della pista principale: direzione nel teatro, magnetica e numeri di pista.</summary>
    public string RunwayText(EdObjective o)
    {
        if (MainRunway(o) is not Runway r) return "";
        double a = r.Hdg % 180, b = a + 180;
        int N(double h) { int n = (int)Math.Round((((h - KoreaBases.TheaterVariation) % 360) + 360) % 360 / 10); return n <= 0 ? n + 36 : n > 36 ? n - 36 : n; }
        return $"{a:0.0}°/{b:0.0}° (mag {N(a):00}/{N(b):00}), {r.Len * 0.3048:0} m";
    }

    /// <summary>Basi coreane complete (con l'asfalto 3D) che si possono mettere al posto dell'obiettivo, dalla piu' simile.</summary>
    /// <param name="anyCount">anche le basi con un numero di piste diverso (allineate sulla pista piu' lunga)</param>
    public List<(KoreaBase Base, Placement Where)> Suggest(EdObjective o, bool onlyComplete = true, bool anyCount = false)
    {
        var target = WorldRunways(o);
        var res = new List<(KoreaBase, Placement)>();
        foreach (var k in Korea)
        {
            if (onlyComplete && !k.HasTaxiways && !k.FromTheater) continue;   // le basi del teatro si mostrano tutte (il dialogo filtra)
            if ((anyCount && k.Runways.Count != target.Count ? KoreaBases.PlaceMain(target, k) : KoreaBases.Place(target, k)) is Placement p) res.Add((k, p));
        }
        return res.OrderBy(t => t.Item2.Cost).ToList();
    }

    public void ApplySwap(EdObjective o, KoreaBase k, Placement where)
    {
        o.SwapOcd = k.Ocd; o.SwapName = k.Name; o.DropEdit = false;
        o.X = where.X; o.Y = where.Y; o.Heading = where.Heading;
    }

    /// <summary>Toglie la base coreana: se era gia' salvata, al salvataggio si toglie anche la posizione (torna dove la mette il tool).</summary>
    public void RemoveSwap(EdObjective o)
    {
        o.SwapOcd = -1; o.SwapName = "";
        if (o.SwapOcd0 < 0) { o.X = o.X0; o.Y = o.Y0; o.Heading = o.H0; }
        else o.DropEdit = true;
    }

    /// <summary>
    /// Numero di pista (1-36) che BMS mostra su ogni pezzo "QFU" della disposizione (FED Num → numero), calcolato come lo
    /// scrive il lavoro Objects: direzione d'atterraggio nel teatro, magnetica.
    /// </summary>
    public static Dictionary<int, int> RunwayNumbers(EdObjective o, EdLayout lay)
    {
        var res = new Dictionary<int, int>();
        foreach (var (dep, data) in lay.RunwayHeads)
        {
            double mag = ((data + o.Heading - KoreaBases.TheaterVariation) % 360 + 360) % 360;
            int n = (int)Math.Round(mag / 10); if (n <= 0) n += 36; if (n > 36) n -= 36;
            res.TryAdd(dep, n);
        }
        return res;
    }

    /// <summary>Base coreana scelta per l'obiettivo, se c'e'.</summary>
    public KoreaBase? SwapBase(EdObjective o) => o.SwapOcd < 0 ? null : Korea.FirstOrDefault(k => k.Ocd == o.SwapOcd);

    /// <summary>I numeri di pista scritti nei modelli della base coreana restano giusti con la rotazione attuale (null se non e' una base coreana).</summary>
    public bool? NumbersOk(EdObjective o) => SwapBase(o) is KoreaBase k ? k.FixedNumbers.Count == 0 || KoreaBases.SameNumbers(k, o.Heading) : null;

    /// <summary>Gira l'obiettivo di 180° attorno al centro della pista principale (la pista resta dov'e', la base passa dall'altra parte).</summary>
    public void Turn180(EdObjective o)
    {
        var rws = WorldRunways(o);
        var (px, py) = rws.Count > 0 ? (rws.MaxBy(r => r.Len).X, rws.MaxBy(r => r.Len).Y) : (o.X, o.Y);
        o.X = 2 * px - o.X; o.Y = 2 * py - o.Y;
        o.Heading = (o.Heading + 180) % 360;
    }

    /// <summary>Posizione assoluta (piedi) di un punto della disposizione, con la rotazione dell'obiettivo.</summary>
    public static (double X, double Y) ToWorld(EdObjective o, double n, double e)
    {
        double h = o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
        return (o.X + n * ch - e * sh, o.Y + n * sh + e * ch);
    }

    /// <summary>Angoli della sagoma di un pezzo in coordinate assolute (piedi, X nord / Y est).</summary>
    public static (double X, double Y)[] FeatureCorners(EdObjective o, EdFeature f)
    {
        var (cx, cy) = ToWorld(o, f.N, f.E);
        double H = (o.Heading + f.Heading) * Math.PI / 180;
        double fn = Math.Cos(H), fe = Math.Sin(H), rn = -Math.Sin(H), re = Math.Cos(H);   // avanti, destra
        (double, double) P(double x, double y) => (cx + x * fn + y * rn, cy + x * fe + y * re);
        return new[] { P(f.MinX, f.MinY), P(f.MaxX, f.MinY), P(f.MaxX, f.MaxY), P(f.MinX, f.MaxY) };
    }
}

/// <summary>
/// Tessere Photoreal 4K del teatro (DXT1 4096², una ogni 64 km, numerate da 1 riga per riga da nord-ovest) lette a
/// pezzi di 512 pixel di un livello mip, decodificati in BGRA. Solo lettura.
/// </summary>
public sealed class PhotoTiles
{
    public const int TilePx = 4096, Chunk = 512;
    private readonly string _dir;
    public double SizeM { get; }
    public int N { get; }
    public double TileM => SizeM / N;
    private readonly Dictionary<(int, int, int, int, int), byte[]?> _cache = new();
    private readonly LinkedList<(int, int, int, int, int)> _lru = new();
    private const int MaxChunks = 160;

    public PhotoTiles(string dir, double sizeM)
    {
        _dir = dir; SizeM = sizeM;
        N = (int)Math.Round(sizeM / 1000 / Photoreal4KJob.TileKm);
    }

    /// <summary>Dimentica i pezzi gia' letti di una tessera (dopo che e' stata riscritta).</summary>
    public void Forget(int tx, int ty)
    {
        foreach (var k in _cache.Keys.Where(k => k.Item1 == tx && k.Item2 == ty).ToList()) { _cache.Remove(k); _lru.Remove(k); }
    }

    public string TilePath(int tx, int ty) => Path.Combine(_dir, (ty * N + tx + 1).ToString(CultureInfo.InvariantCulture) + ".dds");

    /// <summary>
    /// Vista d'insieme di tutto il teatro: ogni tessera al livello mip scelto (64 pixel con mip 6), in un'unica immagine BGRA
    /// lato = N * (4096 &gt;&gt; mip), righe da nord. Le tessere che mancano (solo mare, saltate da Photoreal) sono color mare.
    /// </summary>
    public (byte[] Bgra, int Side) Overview(int mip = 6)
    {
        int t = TilePx >> mip, side = N * t;
        var img = new byte[side * side * 4];
        for (int i = 0; i < img.Length; i += 4) { img[i] = 92; img[i + 1] = 72; img[i + 2] = 22; img[i + 3] = 255; }   // mare (BGR)
        for (int ty = 0; ty < N; ty++)
            for (int tx = 0; tx < N; tx++)
            {
                byte[]? px = null;
                try { px = Decode(tx, ty, mip, 0, 0, t); } catch (IOException) { } catch (InvalidDataException) { }
                if (px == null) continue;
                for (int r = 0; r < t; r++) Buffer.BlockCopy(px, r * t * 4, img, ((ty * t + r) * side + tx * t) * 4, t * 4);
            }
        return (img, side);
    }

    /// <summary>Pezzo (cx, cy) del livello mip della tessera: BGRA, lato = min(512, 4096 &gt;&gt; mip). Null se la tessera manca.</summary>
    public byte[]? GetChunk(int tx, int ty, int mip, int cx, int cy, out int side)
    {
        int w = TilePx >> mip;
        side = Math.Min(Chunk, w);
        var key = (tx, ty, mip, cx, cy);
        if (_cache.TryGetValue(key, out var hit)) { _lru.Remove(key); _lru.AddFirst(key); return hit; }
        byte[]? res = null;
        try { res = Decode(tx, ty, mip, cx, cy, side); } catch (IOException) { } catch (InvalidDataException) { }
        _cache[key] = res; _lru.AddFirst(key);
        while (_lru.Count > MaxChunks) { _cache.Remove(_lru.Last!.Value); _lru.RemoveLast(); }
        return res;
    }

    private byte[]? Decode(int tx, int ty, int mip, int cx, int cy, int side)
    {
        string f = TilePath(tx, ty);
        if (!File.Exists(f)) return null;
        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[128];
        fs.ReadExactly(head);
        if (head[0] != 'D' || head[1] != 'D' || head[2] != 'S' || head[84] != 'D' || head[85] != 'X' || head[86] != 'T' || head[87] != '1')
            throw new InvalidDataException("not a DXT1 dds");
        int w0 = BitConverter.ToInt32(head, 16);
        long off = 128;
        for (int l = 0; l < mip; l++) { int wl = Math.Max(1, w0 >> l); off += (long)Math.Max(1, (wl + 3) / 4) * Math.Max(1, (wl + 3) / 4) * 8; }
        int w = Math.Max(1, w0 >> mip), bw = Math.Max(1, (w + 3) / 4);
        int bx0 = cx * side / 4, by0 = cy * side / 4, nb = Math.Max(1, side / 4);
        var outp = new byte[side * side * 4];
        var row = new byte[nb * 8];
        for (int by = 0; by < nb; by++)
        {
            fs.Position = off + ((long)(by0 + by) * bw + bx0) * 8;
            fs.ReadExactly(row);
            for (int bx = 0; bx < nb; bx++) DecodeBlock(row, bx * 8, outp, side, bx * 4, by * 4);
        }
        return outp;
    }

    private static void DecodeBlock(byte[] s, int o, byte[] dst, int stride, int x0, int y0)
    {
        int c0 = s[o] | s[o + 1] << 8, c1 = s[o + 2] | s[o + 3] << 8;
        Span<int> r = stackalloc int[4], g = stackalloc int[4], b = stackalloc int[4];
        r[0] = (c0 >> 11 & 31) * 255 / 31; g[0] = (c0 >> 5 & 63) * 255 / 63; b[0] = (c0 & 31) * 255 / 31;
        r[1] = (c1 >> 11 & 31) * 255 / 31; g[1] = (c1 >> 5 & 63) * 255 / 63; b[1] = (c1 & 31) * 255 / 31;
        if (c0 > c1)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
            r[3] = g[3] = b[3] = 0;
        }
        uint bits = BitConverter.ToUInt32(s, o + 4);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int idx = (int)(bits >> 2 * (j * 4 + i) & 3);
                int p = ((y0 + j) * stride + x0 + i) * 4;
                if (p + 3 >= dst.Length) continue;
                dst[p] = (byte)b[idx]; dst[p + 1] = (byte)g[idx]; dst[p + 2] = (byte)r[idx]; dst[p + 3] = 255;
            }
    }
}
