using System.Globalization;
using System.Text.Json;

namespace TerrainTool.Core.Models;

/// <summary>
/// Parent.dat come testo: righe "Chiave = valori" (Dimensions, TextureSets, Switches, Dofs, AddDynamicVertex, AddSlot, AddLOD),
/// commenti con //. Si legge e si scrive mantenendo l'ordine e i commenti.
/// </summary>
public sealed class ParentDat
{
    public sealed record Line(int Number, string Key, string[] Values, string Raw);

    public List<Line> Lines { get; } = new();
    public string Folder { get; private set; } = "";

    public static ParentDat Parse(string text, string folder = "")
    {
        var p = new ParentDat { Folder = folder };
        var rows = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string raw = rows[i], s = raw;
            int c = s.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) s = s[..c];
            s = s.Trim();
            if (s.Length == 0) continue;
            int eq = s.IndexOf('=');
            string key; string[] vals;
            if (eq >= 0) { key = s[..eq].Trim(); vals = s[(eq + 1)..].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); }
            else
            {
                // anche "Chiave valori" senza '=' (es. BoundingBoxMin_0 -1 -2 -3)
                var t = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                key = t[0]; vals = t.Skip(1).ToArray();
            }
            p.Lines.Add(new Line(i + 1, key, vals, raw));
        }
        return p;
    }

    public IEnumerable<Line> All(string key) => Lines.Where(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public Line? First(string key) => All(key).FirstOrDefault();

    public static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}

/// <summary>Un problema trovato in un modello.</summary>
public sealed record ModelIssue(string Severity, string Model, string Text)
{
    public const string Error = "ERROR", Warning = "Warning", Info = "Info";
}

/// <summary>
/// Controlli su un modello di BMS (come il "Check Data" di LODEditor, rifatto per i modelli BML di 4.38): Parent.dat,
/// file dei LOD, numeri di DOF/switch/slot, livree, texture. Solo lettura.
/// </summary>
public static class ModelChecks
{
    public static readonly string[] KnownKeys = { "Dimensions", "TextureSets", "Switches", "Dofs", "AddDynamicVertex", "AddSlot", "AddLOD", "NumBoundingBoxes" };

    private static bool Known(string key) => KnownKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ||
        key.StartsWith("BoundingBoxMin_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("BoundingBoxMax_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Controlla un modello. parentText = testo di Parent.dat da controllare (anche non ancora salvato); null = quello su disco.</summary>
    public static List<ModelIssue> Check(string modelDir, string? parentText = null, IReadOnlyList<string>? textureDirs = null, bool deep = true, string? modelsRoot = null)
    {
        // modelsRoot: cartella Models di BMS per le texture condivise ("1909/PilotUS_ALB.dds"); null = quella che contiene il modello
        modelsRoot ??= Path.GetDirectoryName(modelDir) ?? modelDir;
        var res = new List<ModelIssue>();
        string id = Path.GetFileName(modelDir.TrimEnd('\\', '/'));
        void Add(string sev, string t) => res.Add(new ModelIssue(sev, id, t));
        string pf = Path.Combine(modelDir, "Parent.dat");
        if (parentText == null)
        {
            if (!File.Exists(pf)) { Add(ModelIssue.Error, "Parent.dat missing"); return res; }
            try { parentText = File.ReadAllText(pf); } catch (Exception e) { Add(ModelIssue.Error, "Parent.dat cannot be read: " + e.Message); return res; }
        }
        var p = ParentDat.Parse(parentText, modelDir);

        foreach (var l in p.Lines.Where(l => !Known(l.Key)))
            Add(ModelIssue.Warning, $"line {l.Number}: unknown key '{l.Key}'");
        foreach (var k in new[] { "Dimensions", "TextureSets", "Switches", "Dofs" })
        {
            int n = p.All(k).Count();
            if (n == 0) Add(k == "Dimensions" || k == "TextureSets" ? ModelIssue.Error : ModelIssue.Warning, $"{k} missing");
            else if (n > 1) Add(ModelIssue.Warning, $"{k} appears {n} times (only one is used)");
        }
        int Int(string key, int def) { var l = p.First(key); return l != null && l.Values.Length > 0 && int.TryParse(l.Values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def; }
        foreach (var l in p.Lines)
        {
            int need = l.Key.ToLowerInvariant() switch { "dimensions" => 7, "textureSets" or "texturesets" or "switches" or "dofs" => 1, "adddynamicvertex" or "addslot" => 3, "addlod" => 2, _ => 0 };
            if (need == 0) continue;
            if (l.Values.Length < need) { Add(ModelIssue.Error, $"line {l.Number}: {l.Key} needs {need} value(s), found {l.Values.Length}"); continue; }
            var nums = l.Key.Equals("AddLOD", StringComparison.OrdinalIgnoreCase) ? l.Values.Skip(1) : l.Values;
            foreach (var v in nums.Take(need))
                if (!ParentDat.Num(v, out double d) || !double.IsFinite(d)) Add(ModelIssue.Error, $"line {l.Number}: '{v}' is not a number");
        }
        int sets = Int("TextureSets", 1), dofsMax = Int("Dofs", 0), swMax = Int("Switches", 0);
        if (sets < 1) Add(ModelIssue.Error, $"TextureSets = {sets}: must be at least 1");

        // LOD
        var lods = p.All("AddLOD").Where(l => l.Values.Length >= 2).Select(l => (l, File: l.Values[0], Dist: ParentDat.Num(l.Values[1], out double d) ? d : double.NaN)).ToList();
        if (lods.Count == 0) Add(ModelIssue.Error, "no AddLOD: the model has nothing to draw");
        for (int i = 1; i < lods.Count; i++)
            if (lods[i].Dist <= lods[i - 1].Dist) Add(ModelIssue.Warning, $"line {lods[i].l.Number}: LOD distances must grow ({lods[i - 1].Dist} then {lods[i].Dist})");
        foreach (var g in lods.GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Add(ModelIssue.Warning, $"{g.Key} is listed {g.Count()} times");
        var referenced = new HashSet<string>(lods.Select(x => x.File), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.Exists(modelDir) ? Directory.EnumerateFiles(modelDir, "*.bml") : Enumerable.Empty<string>())
            if (!referenced.Contains(Path.GetFileName(f))) Add(ModelIssue.Info, $"{Path.GetFileName(f)} is in the folder but not listed in AddLOD (not used)");

        int slotLines = p.All("AddSlot").Count();
        BmlScene? first = null;
        var badDofs = new List<string>();
        var parentInfo = new BmlModel.ParentInfo(modelDir, Math.Max(1, sets), lods.Select(x => (x.File, x.Dist)).ToList(), 0);
        for (int i = 0; i < lods.Count; i++)
        {
            string f = Path.Combine(modelDir, lods[i].File);
            if (!File.Exists(f)) { Add(ModelIssue.Error, $"LOD {i}: {lods[i].File} not found"); continue; }
            if (!deep) continue;
            BmlScene sc;
            try { sc = BmlScene.Load(parentInfo, i, 0, modelsRoot, textureDirs ?? Array.Empty<string>()); }
            catch (Exception e) { Add(ModelIssue.Error, $"LOD {i}: {lods[i].File} cannot be read – {e.Message}"); continue; }
            first ??= sc;
            int maxDof = sc.Dofs.Count == 0 ? -1 : sc.Dofs.Max(d => d.Number), maxSw = sc.Switches.Count == 0 ? -1 : sc.Switches.Max(s => s.Number);
            if (maxDof >= dofsMax) Add(ModelIssue.Error, $"LOD {i} uses DOF {maxDof} but Dofs = {dofsMax} (must be at least {maxDof + 1})");
            if (maxSw >= swMax) Add(maxSw == 0 ? ModelIssue.Warning : ModelIssue.Error, $"LOD {i} uses switch {maxSw} but Switches = {swMax} (must be at least {maxSw + 1})");
            int maxSlot = sc.Slots.Count == 0 ? -1 : sc.Slots.Max(s => s.Number);
            if (maxSlot >= slotLines) Add(ModelIssue.Info, $"LOD {i} has slot {maxSlot} but Parent.dat lists {slotLines} AddSlot (weapons on that slot are not placed)");
            var bad = sc.Dofs.Where(d => d.Kind == BmlScene.DofKind.Rotate && !d.ValidMatrix).Select(d => d.Number).ToList();
            if (bad.Count > 0) badDofs.Add($"LOD {i}: {string.Join(", ", bad)}");
            if (sc.PrimitiveCount == 0)
                Add(sc.Slots.Count > 0 ? ModelIssue.Info : ModelIssue.Warning, $"LOD {i}: no geometry" + (sc.Slots.Count > 0 ? $" (only {sc.Slots.Count} slot(s): a rack or an adapter)" : ""));
            if (i == 0 && sc.Version == 1 && textureDirs != null && textureDirs.Count > 0)
            {
                var miss = sc.MissingTextures().OrderBy(t => t).ToList();
                if (miss.Count > 0) Add(ModelIssue.Error, $"{miss.Count} texture(s) not found in {string.Join(" / ", textureDirs.Select(Path.GetFileName))}: {string.Join(", ", miss.Take(20))}{(miss.Count > 20 ? "…" : "")}");
            }
            if (i == 0 && sc.Version == 1 && sets > 1 && sc.MaterialCount % sets != 0)
                Add(ModelIssue.Warning, $"TextureSets = {sets} but LOD 0 has {sc.MaterialCount} textures (not a multiple)");
            if (sc.PrimitiveCount > 0) foreach (var n in sc.Notes.Distinct()) Add(ModelIssue.Warning, $"LOD {i}: {n}");
        }
        if (badDofs.Count > 0) Add(ModelIssue.Info, "rotation DOFs with an invalid matrix (old converted model; shown unrotated): " + string.Join("; ", badDofs));
        if (first != null && first.Version != 1 && first.PrimitiveCount > 0)
        {
            CheckPbr(modelDir, modelsRoot, sets, lods.Select(x => x.File).ToList(), Add);
        }
        if (first != null && slotLines > 0 && first.Slots.Count == 0)
            Add(ModelIssue.Info, $"Parent.dat lists {slotLines} AddSlot but LOD 0 has no slot nodes");

        // dimensioni: il raggio dichiarato contro quello del LOD 0
        var dim = p.First("Dimensions");
        if (deep && first != null && dim != null && dim.Values.Length >= 1 && ParentDat.Num(dim.Values[0], out double rad))
        {
            var pos = first.Pose().Meshes.SelectMany(m => m.Positions).ToArray();
            if (pos.Length >= 3)
            {
                double r = 0;
                for (int i = 0; i + 2 < pos.Length; i += 3) r = Math.Max(r, pos[i] * pos[i] + pos[i + 1] * pos[i + 1] + pos[i + 2] * pos[i + 2]);
                r = Math.Sqrt(r);
                if (rad <= 0) Add(ModelIssue.Error, $"Dimensions radius = {rad}");
                else if (rad < r * 0.8)
                    Add(ModelIssue.Info, $"Dimensions radius {rad:0.#} ft, but LOD 0 reaches {r:0.#} ft from the centre: parts may disappear at the edge of the screen (BMS uses it for culling)");
            }
        }
        return res;
    }

    private static void CheckPbr(string dir, string root, int sets, List<string> lods, Action<string, string> add)
    {
        string mtl = Path.Combine(dir, "Materials.mtl");
        if (!File.Exists(mtl)) { add(ModelIssue.Error, "Materials.mtl missing (PBR model)"); return; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mtl), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("Materials", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("Textures", out var tx))
                        foreach (var t in tx.EnumerateArray())
                            if (t.TryGetProperty("File", out var f) && f.GetString() is { Length: > 0 } rel)
                            {
                                string r = rel.Replace('/', Path.DirectorySeparatorChar);
                                if (!BmlScene.TextureCandidates(dir, root, r).Any(File.Exists)) missing.Add(rel);
                            }
            if (missing.Count > 0) add(ModelIssue.Error, $"Materials.mtl: {missing.Count} texture(s) not found: {string.Join(", ", missing.Take(20))}{(missing.Count > 20 ? "…" : "")}");
        }
        catch (Exception e) { add(ModelIssue.Error, "Materials.mtl cannot be read: " + e.Message); }
        if (sets > 1)
            foreach (var l in lods)
            {
                string mti = Path.Combine(dir, Path.ChangeExtension(l, ".mti"));
                if (!File.Exists(mti)) add(ModelIssue.Info, $"TextureSets = {sets} but {Path.GetFileName(mti)} is missing (that LOD shows only the first livery)");
            }
    }

    /// <summary>Controlla tutti i modelli di Terrdata\objects\Models.</summary>
    public static List<ModelIssue> CheckAll(string modelsRoot, bool deep, IProgress<(int Done, int Total)>? progress, CancellationToken ct, bool gear = false,
                                            bool lights = false, BmsNames? names = null)
    {
        var dirs = Directory.EnumerateDirectories(modelsRoot).Where(d => int.TryParse(Path.GetFileName(d), out _))
            .OrderBy(d => int.Parse(Path.GetFileName(d), CultureInfo.InvariantCulture)).ToList();
        var bag = new System.Collections.Concurrent.ConcurrentBag<ModelIssue>();
        int done = 0;
        var tex = new[] { "KoreaObj_HiRes", "KoreaObj" }.Select(n => Path.Combine(Path.GetDirectoryName(modelsRoot) ?? modelsRoot, n)).Where(Directory.Exists).ToList();
        Parallel.ForEach(dirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, d =>
        {
            try
            {
                foreach (var i in Check(d, null, tex, deep)) bag.Add(i);
                if (gear) foreach (var i in GearCheck(d)) bag.Add(i);
                if (lights) foreach (var i in LightCheck(d, names)) bag.Add(i);
            }
            catch (Exception e) { bag.Add(new ModelIssue(ModelIssue.Error, Path.GetFileName(d), e.Message)); }
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == dirs.Count) progress?.Report((n, dirs.Count));
        });
        return bag.OrderBy(i => int.TryParse(i.Model, out int n) ? n : int.MaxValue).ThenBy(i => i.Severity == ModelIssue.Error ? 0 : i.Severity == ModelIssue.Warning ? 1 : 2).ToList();
    }

    /// <summary>
    /// Prova del carrello sul LOD 0 (come "Gear down" della vista 3D): gli switch che contengono i DOF 19-21 accesi e tutti
    /// i DOF del carrello (19-24) al limite piu' alto. Segnala: niente che esce, ruote che non scendono sotto la fusoliera,
    /// lato sinistro e destro diversi, pezzi che sporgono davanti, dietro o di lato oltre l'aereo.
    /// </summary>
    public static List<ModelIssue> GearCheck(string modelDir)
    {
        var res = new List<ModelIssue>();
        string id = Path.GetFileName(modelDir.TrimEnd('\\', '/'));
        void Add(string sev, string t) => res.Add(new ModelIssue(sev, id, "Gear: " + t));
        var parent = BmlModel.ReadParent(modelDir);
        if (parent == null || parent.Lods.Count == 0 || !File.Exists(Path.Combine(modelDir, parent.Lods[0].File))) return res;
        BmlScene sc;
        try { sc = BmlScene.Load(parent, 0, 0, Path.GetDirectoryName(modelDir) ?? modelDir, Array.Empty<string>()); }
        catch { return res; }
        if (!sc.DofNodes.Any(d => d.Number is 19 or 20 or 21)) return res;   // niente carrello (navi, mezzi, armi, edifici…)
        var sw = sc.GearSwitches();
        var vals = sc.GearDownValues();
        var rest = sc.Pose();
        var down = sc.Pose(n => vals.TryGetValue(n, out var v) ? v : 0, (a, b, on) => sw.Contains((a, b)) || on);
        var r = Bounds(rest);
        if (r == null) return res;
        var (rMin, rMax) = r.Value;
        double length = rMax[2] - rMin[2], span = rMax[0] - rMin[0];

        // fondo della fusoliera: il 2% piu' basso dei vertici a riposo (un pezzo storto a riposo non conta)
        var ys = rest.Meshes.SelectMany(m => Enumerable.Range(0, m.Positions.Length / 3).Select(i => (double)m.Positions[i * 3 + 1])).OrderBy(y => y).ToList();
        double bottom = ys[(int)(ys.Count * 0.02)];
        // vertici del carrello: quelli che con il carrello giu' non sono dove erano a riposo (o che appaiono)
        var restSet = new HashSet<(int, int, int)>();
        foreach (var m in rest.Meshes) for (int i = 0; i + 2 < m.Positions.Length; i += 3) restSet.Add(Key(m.Positions, i));
        var moved = new List<(double X, double Y, double Z)>();
        foreach (var m in down.Meshes)
            for (int i = 0; i + 2 < m.Positions.Length; i += 3)
                if (!restSet.Contains(Key(m.Positions, i))) moved.Add((m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]));
        if (moved.Count == 0) { Add(ModelIssue.Info, "nothing moves with the gear down (fixed gear, skids, or gear switches/DOFs not found)"); return res; }
        double low = moved.Min(v => v.Y);
        if (low > bottom - 0.3 && sw.Count == 0)
        { Add(ModelIssue.Info, $"the gear does not go down (no gear switches: fixed gear or skids?) – lowest gear point {low:0.0} ft"); return res; }
        if (low > bottom - 0.3)
            Add(ModelIssue.Warning, $"wheels do not go below the fuselage (lowest gear point {low:0.0} ft, fuselage bottom {bottom:0.0} ft)");
        if (low < bottom - Math.Max(12, 0.4 * length))
            Add(ModelIssue.Warning, $"gear goes very far down ({low:0.0} ft, fuselage bottom {bottom:0.0} ft)");
        double tol = Math.Max(1.5, 0.04 * Math.Max(length, span));
        double zMin = moved.Min(v => v.Z), zMax = moved.Max(v => v.Z), xMin = moved.Min(v => v.X), xMax = moved.Max(v => v.X);
        if (zMin < rMin[2] - tol || zMax > rMax[2] + tol)
            Add(ModelIssue.Warning, $"with the gear down something sticks out in front or behind ({zMin:0.0}..{zMax:0.0} ft, aircraft {rMin[2]:0.0}..{rMax[2]:0.0})");
        if (xMin < rMin[0] - tol || xMax > rMax[0] + tol)
            Add(ModelIssue.Warning, $"with the gear down something sticks out to the side ({xMin:0.0}..{xMax:0.0} ft, span {rMin[0]:0.0}..{rMax[0]:0.0})");
        // lati: punto piu' basso del carrello a sinistra e a destra
        var left = moved.Where(v => v.X < -0.7).Select(v => v.Y).DefaultIfEmpty(double.NaN).Min();
        var right = moved.Where(v => v.X > 0.7).Select(v => v.Y).DefaultIfEmpty(double.NaN).Min();
        bool lowL = !double.IsNaN(left) && left < bottom - 0.3, lowR = !double.IsNaN(right) && right < bottom - 0.3;
        if (lowL && lowR && Math.Abs(left - right) > 0.6)
            Add(ModelIssue.Warning, $"left and right gear end at different heights ({left:0.0} / {right:0.0} ft)");
        else if (lowL != lowR)
            Add(ModelIssue.Warning, $"only the {(lowL ? "left" : "right")} gear goes below the fuselage");
        return res;
    }

    /// <summary>
    /// Prova delle luci sul LOD 0, con il carrello giu' e tutti gli switch delle luci accesi: switch delle luci vuoti,
    /// luci staccate dall'aereo, luci di navigazione senza la gemella dall'altra parte, rosso/verde scambiati (modelli vecchi).
    /// names: nomi degli switch (per i messaggi), puo' essere null.
    /// </summary>
    public static List<ModelIssue> LightCheck(string modelDir, BmsNames? names = null)
    {
        var res = new List<ModelIssue>();
        string id = Path.GetFileName(modelDir.TrimEnd('\\', '/'));
        void Add(string sev, string t) => res.Add(new ModelIssue(sev, id, "Lights: " + t));
        string Sw(int n, int b) => names != null ? $"{n}/{b} {names.Switch(n, b)}" : $"{n}/{b}";
        var parent = BmlModel.ReadParent(modelDir);
        if (parent == null || parent.Lods.Count == 0 || !File.Exists(Path.Combine(modelDir, parent.Lods[0].File))) return res;
        BmlScene sc;
        try { sc = BmlScene.Load(parent, 0, 0, Path.GetDirectoryName(modelDir) ?? modelDir, Array.Empty<string>()); }
        catch { return res; }
        var lightSw = sc.Switches.Where(x => BmlScene.LightSwitches.Contains(x.Number)).ToList();
        if (lightSw.Count == 0) return res;
        var gearSw = sc.GearSwitches();
        var gearVals = sc.GearDownValues();
        var pts = new List<BmlScene.LightPoint>();
        var parts = new List<(int Node, string Material, int FirstIndex, int IndexCount)>();
        var model = sc.Pose(n => gearVals.TryGetValue(n, out var v) ? v : 0,
                            (a, b, on) => on || gearSw.Contains((a, b)) || BmlScene.LightSwitches.Contains(a), null, parts, true, pts);
        var texColor = LightColors(model, parts);
        (byte R, byte G, byte B) Col(BmlScene.LightPoint p) => texColor.TryGetValue(p.Node, out var c) ? c : p.Rgb;
        // geometria dell'aereo (non luci) in una griglia di 1 ft
        var grid = new HashSet<(int, int, int)>();
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var m in model.Meshes.Where(m => !m.Light))
        {
            var P = m.Positions;
            for (int i = 0; i + 2 < P.Length; i += 3)
            {
                minX = Math.Min(minX, P[i]); maxX = Math.Max(maxX, P[i]); minZ = Math.Min(minZ, P[i + 2]); maxZ = Math.Max(maxZ, P[i + 2]);
            }
            // superficie dei triangoli (non solo i vertici: una deriva puo' avere vertici lontani tra loro)
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                int a = m.Indices[t] * 3, b = m.Indices[t + 1] * 3, c = m.Indices[t + 2] * 3;
                double l = Math.Max(Dist(P, a, b), Math.Max(Dist(P, b, c), Dist(P, a, c)));
                int n = Math.Clamp((int)Math.Ceiling(l / 0.7), 1, 60);
                for (int u = 0; u <= n; u++)
                    for (int v = 0; u + v <= n; v++)
                    {
                        double wu = (double)u / n, wv = (double)v / n, ww = 1 - wu - wv;
                        grid.Add(((int)Math.Floor(P[a] * ww + P[b] * wu + P[c] * wv), (int)Math.Floor(P[a + 1] * ww + P[b + 1] * wu + P[c + 1] * wv), (int)Math.Floor(P[a + 2] * ww + P[b + 2] * wu + P[c + 2] * wv)));
                    }
            }
        }
        if (grid.Count == 0) return res;
        double size = Math.Max(maxX - minX, maxZ - minZ);
        int reach = (int)Math.Ceiling(Math.Max(1.5, 0.03 * size));
        bool Near(double x, double y, double z)
        {
            int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y), cz = (int)Math.Floor(z);
            for (int dx = -reach; dx <= reach; dx++) for (int dy = -reach; dy <= reach; dy++) for (int dz = -reach; dz <= reach; dz++)
                if (grid.Contains((cx + dx, cy + dy, cz + dz))) return true;
            return false;
        }
        var empty = lightSw.Where(s => !pts.Any(p => p.Switch == s.Number && p.Branch == s.Branch)).Select(s => Sw(s.Number, s.Branch)).ToList();
        if (empty.Count > 0) Add(ModelIssue.Info, $"light switch(es) with nothing inside: {string.Join(", ", empty.Take(8))}{(empty.Count > 8 ? "…" : "")}");
        foreach (var p in pts.Where(p => p.Switch >= 0 && !Near(p.X, p.Y, p.Z)).GroupBy(p => (p.Switch, p.Branch)))
        {
            var f = p.First();
            Add(ModelIssue.Warning, $"{p.Count()} light(s) {(f.Switch >= 0 ? "of switch " + Sw(f.Switch, f.Branch) : "(no switch)")} float more than {reach} ft away from the aircraft (first at {f.X:0.0}, {f.Y:0.0}, {f.Z:0.0})");
        }
        // luci di navigazione (8) alle estremita' delle ali, solo per gli aerei (hanno un carrello): navi e mezzi le hanno dove vogliono
        if (!sc.DofNodes.Any(d => d.Number is 19 or 20 or 21)) return res;
        double half = Math.Max(-minX, maxX);
        var nav = pts.Where(p => p.Switch is 8 && Math.Abs(p.X) > 0.6 * half).ToList();
        int lonely = nav.Count(p => !nav.Any(q => Math.Abs(q.X + p.X) < 1.0 && Math.Abs(q.Z - p.Z) < 1.5 && Math.Abs(q.Y - p.Y) < 1.5));
        if (lonely > 0) Add(ModelIssue.Warning, $"{lonely} nav light(s) without a matching light on the other side");
        // colori (solo dove il file li ha): a sinistra (x < 0) rosso, a destra verde
        static bool Red((byte R, byte G, byte B) c) => c.R > 150 && c.R > c.G * 1.5;
        static bool Green((byte R, byte G, byte B) c) => c.G > 150 && c.G > c.R * 1.5;
        var leftWrong = nav.Count(p => p.X < -1 && Green(Col(p))); var rightWrong = nav.Count(p => p.X > 1 && Red(Col(p)));
        if (leftWrong + rightWrong > 0) Add(ModelIssue.Warning, $"nav light colours look swapped ({leftWrong} green on the left, {rightWrong} red on the right)");
        return res;
    }

    /// <summary>
    /// Colore delle luci PBR (nodo → RGB): media della loro texture emissiva nei punti UV della luce. parts viene da Pose.
    /// </summary>
    public static Dictionary<int, (byte R, byte G, byte B)> LightColors(BmlModel model, List<(int Node, string Material, int FirstIndex, int IndexCount)> parts)
    {
        var texColor = new Dictionary<int, (byte R, byte G, byte B)>();
        var images = new Dictionary<string, DdsImage.Image?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, me, first, cnt) in model.Parts)
        {
            if (!me.Light || me.Texture == null || cnt == 0 || first + cnt > me.Indices.Length) continue;
            if (!images.TryGetValue(me.Texture!, out var im)) { try { im = DdsImage.Load(me.Texture!, 512); } catch { im = null; } images[me.Texture!] = im; }
            if (im == null) continue;
            double r = 0, g = 0, b = 0;
            for (int i = first; i < first + cnt; i++)
            {
                int q = me.Indices[i];
                if (q * 2 + 1 >= me.Uvs.Length) continue;
                double u = ((me.Uvs[q * 2] % 1) + 1) % 1, v = ((me.Uvs[q * 2 + 1] % 1) + 1) % 1;
                if (double.IsNaN(u) || double.IsNaN(v)) continue;
                int o = ((int)(v * im.Height) * im.Width + (int)(u * im.Width)) * 4;
                if (o < 0 || o + 2 >= im.Bgra.Length) continue;
                b += im.Bgra[o]; g += im.Bgra[o + 1]; r += im.Bgra[o + 2];
            }
            texColor[node] = ((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt));
        }
        return texColor;
    }

    private static double Dist(float[] p, int a, int b)
    {
        double dx = p[a] - p[b], dy = p[a + 1] - p[b + 1], dz = p[a + 2] - p[b + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static (int, int, int) Key(float[] p, int i) => ((int)Math.Round(p[i] * 50), (int)Math.Round(p[i + 1] * 50), (int)Math.Round(p[i + 2] * 50));

    private static (double[] Min, double[] Max)? Bounds(BmlModel m)
    {
        var mn = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; var mx = new[] { double.MinValue, double.MinValue, double.MinValue };
        bool any = false;
        foreach (var me in m.Meshes)
            for (int i = 0; i + 2 < me.Positions.Length; i += 3)
            {
                any = true;
                for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], me.Positions[i + c]); mx[c] = Math.Max(mx[c], me.Positions[i + c]); }
            }
        return any ? (mn, mx) : null;
    }
}
