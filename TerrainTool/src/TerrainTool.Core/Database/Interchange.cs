using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Database;

/// <summary>
/// Database Interchange: copies objective layouts (bases, ground objectives) from one BMS theater to another, with everything
/// they need, renumbered for the destination: the layout folder (OCD, pieces FED, point lists PHD/PDX, trees OTD), the CT entry
/// of the objective type, the pieces (CT + FCD, and their radar RCD), the 3D models of the pieces (theater or BMS Models folders)
/// with their numbered textures. A piece or model that the destination already has identical is reused; everything else gets a
/// free number at the end of its table. Nothing is written here: Plan() makes the plan, Write() puts the files into a work
/// folder, Install() copies them with a backup and a manifest (Restore removes what was added and puts back what was replaced).
/// </summary>
public static partial class Interchange
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public sealed class Plan
    {
        public BmsTheater Src = null!, Dst = null!;
        public string BmsObjects = "";
        /// <summary>New / changed records of the destination database (table, number, fields).</summary>
        public List<(string Table, int Num, List<(string Field, string Value)> Fields)> Records { get; } = new();
        /// <summary>Files to write into the destination: target path (absolute) → source path, or generated text.</summary>
        public Dictionary<string, (string? Src, string? Text)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Report { get; } = new();
        public List<string> Problems { get; } = new();
        public Dictionary<int, int> OcdMap { get; } = new();
        public Dictionary<int, int> FeatureCtMap { get; } = new();
        public Dictionary<int, int> ModelMap { get; } = new();
        /// <summary>Entities plan (MakeEntities): the chosen CT entries → their number in the destination.</summary>
        public Dictionary<int, int> CtMap { get; } = new();
        /// <summary>Records the destination has with the same name but other values, with the choice applied (entities plan).</summary>
        public List<Conflict> Conflicts { get; } = new();
        public int ReusedFeatures, AddedFeatures, ReusedModels, CopiedModels, KeptModels;
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    /// <summary>Folder of model n for a theater: its 3ddatadir Models, then BMS's (null if none has it).</summary>
    public static string? ModelDir(BmsTheater t, string bmsObjects, int n)
    {
        foreach (var root in new[] { t.ModelsDir, bmsObjects }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string d = Path.Combine(root, "Models", n.ToString(Ci));
            if (File.Exists(Path.Combine(d, "Parent.dat"))) return d;
        }
        return null;
    }

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA1.HashData(s));
    }

    /// <summary>Two model folders with the same files and the same bytes.</summary>
    public static bool SameFolder(string a, string b)
    {
        var fa = Directory.EnumerateFiles(a).Select(Path.GetFileName).Select(x => x!.ToLowerInvariant()).OrderBy(x => x).ToList();
        var fb = Directory.EnumerateFiles(b).Select(Path.GetFileName).Select(x => x!.ToLowerInvariant()).OrderBy(x => x).ToList();
        if (!fa.SequenceEqual(fb)) return false;
        foreach (var f in fa)
        {
            var x = new FileInfo(Path.Combine(a, f)); var y = new FileInfo(Path.Combine(b, f));
            if (x.Length != y.Length || Hash(x.FullName) != Hash(y.FullName)) return false;
        }
        return true;
    }

    private static bool SameFile(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && Hash(a) == Hash(b);

    /// <summary>Objective layouts of a theater: OCD number, name, pieces, CT type.</summary>
    public static List<(int Ocd, string Name, int Pieces, int Type, string Dir)> Layouts(BmsTheater t, BmsDatabase db)
    {
        var res = new List<(int, string, int, int, string)>();
        string ord = Path.Combine(t.ObjectDir, "ObjectiveRelatedData");
        if (!Directory.Exists(ord)) return res;
        foreach (var d in Directory.EnumerateDirectories(ord, "OCD_*"))
        {
            if (!int.TryParse(Path.GetFileName(d)[4..], NumberStyles.Integer, Ci, out int n)) continue;
            var o = db.Get("OCD", n);
            string fed = Path.Combine(d, $"FED_{n:00000}.XML");
            int pieces = File.Exists(fed) ? Regex.Matches(File.ReadAllText(fed), @"<FED Num=").Count : 0;
            int type = o != null && db.Get("CT", o.Int("CtIdx")) is { } c ? c.Int("Type") : -1;
            res.Add((n, o?.Name ?? $"OCD {n}", pieces, type, d));
        }
        return res.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>The plan to copy these layouts of src into dst (nothing written).</summary>
    public static Plan Make(BmsTheater src, BmsTheater dst, string dataDir, IList<int> ocds)
    {
        var p = new Plan { Src = src, Dst = dst, BmsObjects = Path.Combine(dataDir, "Terrdata", "objects") };
        if (Norm(src.ObjectDir) == Norm(dst.ObjectDir)) { p.Problems.Add("Source and destination use the same database: nothing to copy."); return p; }
        if (DbInstaller.IsShared(dataDir, dst.ObjectDir)) { p.Problems.Add("The destination uses BMS's shared Terrdata\\objects (Korea): it is never written."); return p; }
        var s = BmsDatabase.Load(src.ObjectDir);
        var d = BmsDatabase.Load(dst.ObjectDir);
        var dLayouts = Directory.Exists(Path.Combine(dst.ObjectDir, "ObjectiveRelatedData"))
            ? Directory.EnumerateDirectories(Path.Combine(dst.ObjectDir, "ObjectiveRelatedData"), "OCD_*").Select(x => int.TryParse(Path.GetFileName(x)[4..], NumberStyles.Integer, Ci, out int n) ? n : -1).ToList()
            : new List<int>();
        int nextOcd = Math.Max(dLayouts.DefaultIfEmpty(-1).Max(), d.All("OCD").Select(r => r.Num).DefaultIfEmpty(-1).Max()) + 1;

        // ── modelli: numero di BMS/teatro sorgente → numero nella destinazione (stesso se c'e' uguale o se e' libero)
        var dstModelRoots = new[] { dst.ModelsDir, p.BmsObjects }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var usedModelNums = new HashSet<int>(dstModelRoots.Select(r => Path.Combine(r, "Models")).Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories).Select(x => int.TryParse(Path.GetFileName(x), NumberStyles.Integer, Ci, out int n) ? n : -1));
        // la destinazione prende i modelli dal Terrdata\objects condiviso di BMS: li' non si scrive mai (nemmeno file nuovi)
        bool sharedModels = Norm(dst.ModelsDir) == Norm(p.BmsObjects);
        int MapModel(int g, string forWhat)
        {
            if (g <= 0) return g;
            if (p.ModelMap.TryGetValue(g, out int m)) return m;
            string? sdir = ModelDir(src, p.BmsObjects, g);
            if (sdir == null) { p.Problems.Add($"model {g} ({forWhat}) not found in the source: kept as {g}"); return p.ModelMap[g] = g; }
            string? ddir = ModelDir(dst, p.BmsObjects, g);
            if (ddir != null && (Norm(sdir) == Norm(ddir) || SameFolder(sdir, ddir))) { p.ReusedModels++; return p.ModelMap[g] = g; }
            if (sharedModels)
            {
                if (ddir != null) p.Report.Add($"model {g} ({forWhat}): {dst.Name} has its own model {g} (BMS's shared models, never changed): the piece keeps it and may look different");
                else p.Problems.Add($"model {g} ({forWhat}): not in BMS's shared models used by {dst.Name}, which are never changed: the piece will not be visible");
                p.KeptModels++;
                return p.ModelMap[g] = g;
            }
            int n = g;
            if (ddir != null || usedModelNums.Contains(g))
            {
                n = Math.Max(usedModelNums.DefaultIfEmpty(0).Max() + 1, 1);
                p.Report.Add($"model {g} ({forWhat}): the destination has a different model {g} → copied as model {n}");
            }
            usedModelNums.Add(n);
            string target = Path.Combine(dst.ModelsDir, "Models", n.ToString(Ci));
            foreach (var f in Directory.EnumerateFiles(sdir)) p.Files[Path.Combine(target, Path.GetFileName(f))] = (f, null);
            p.CopiedModels++;
            CheckTextures(p, src, dst, sdir, forWhat);
            return p.ModelMap[g] = n;
        }

        // ── pezzi: CT (EntityType 1) sorgente → CT nella destinazione
        int nextCt = d.NextFree("CT"), nextFcd = d.NextFree("FCD"), nextRcd = d.NextFree("RCD");
        var dFeatByKey = new Dictionary<string, int>();
        foreach (var c in d.All("CT").Where(c => c.Int("EntityType") == BmsDatabase.Feature))
            if (d.Get("FCD", c.Int("EntityIdx")) is { } f) dFeatByKey.TryAdd(FeatKey(f.Name, c), c.Num);
        var rcdMap = new Dictionary<int, int>();
        int MapRadar(int r)
        {
            if (r <= 0) return r;
            if (rcdMap.TryGetValue(r, out int m)) return m;
            var sr = s.Get("RCD", r);
            if (sr == null) return rcdMap[r] = r;
            var same = d.All("RCD").FirstOrDefault(x => x.Name == sr.Name);
            if (same != null) return rcdMap[r] = same.Num;
            int n = nextRcd++;
            p.Records.Add(("RCD", n, sr.Fields.ToList()));
            p.Report.Add($"radar {sr.Name}: added as RCD {n}");
            return rcdMap[r] = n;
        }
        int MapFeature(int sct)
        {
            if (p.FeatureCtMap.TryGetValue(sct, out int m)) return m;
            var sc = s.Get("CT", sct);
            var sf = sc != null && sc.Int("EntityType") == BmsDatabase.Feature ? s.Get("FCD", sc.Int("EntityIdx")) : null;
            if (sc == null || sf == null) { p.Problems.Add($"piece CT {sct}: not a feature in the source, kept as {sct}"); return p.FeatureCtMap[sct] = sct; }
            // la destinazione ha gia' lo stesso pezzo (stesso nome e stessi modelli uguali)?
            if (dFeatByKey.TryGetValue(FeatKey(sf.Name, sc), out int dct) && ModelsEqual(sc, d.Get("CT", dct)!)) { p.ReusedFeatures++; return p.FeatureCtMap[sct] = dct; }
            int ctn = nextCt++, fcn = nextFcd++;
            var ctF = DbOps.With(sc.Fields, ("EntityIdx", fcn.ToString(Ci)));
            foreach (var g in new[] { "GraphicsNormal", "GraphicsRepaired", "GraphicsDamaged", "GraphicsDestroyed", "GraphicsLeftDestroyed", "GraphicsRightDestroyed", "GraphicsBothDestroyed" })
                if (sc.Int(g, 0) > 0) ctF = DbOps.With(ctF, (g, MapModel(sc.Int(g), sf.Name).ToString(Ci)));
            var fcF = DbOps.With(sf.Fields, ("CtIdx", ctn.ToString(Ci)));
            if (sf.Int("RadarIdx", 0) > 0) fcF = DbOps.With(fcF, ("RadarIdx", MapRadar(sf.Int("RadarIdx")).ToString(Ci)));
            p.Records.Add(("CT", ctn, ctF));
            p.Records.Add(("FCD", fcn, fcF));
            p.AddedFeatures++;
            return p.FeatureCtMap[sct] = ctn;
        }

        bool ModelsEqual(DbRecord a, DbRecord b)
        {
            int ga = a.Int("GraphicsNormal", 0), gb = b.Int("GraphicsNormal", 0);
            if (ga != gb) return false;
            if (ga <= 0) return true;
            var sa = ModelDir(src, p.BmsObjects, ga); var sb = ModelDir(dst, p.BmsObjects, gb);
            if (sa == null && sb == null) return true;   // nessuno dei due lo ha: stesso numero, niente da confrontare
            if (sharedModels && sb != null)
            {
                // modelli condivisi: non si copia nulla, quindi lo stesso pezzo con lo stesso numero si riusa (aspetto della destinazione)
                if (sa != null && Norm(sa) != Norm(sb) && !SameFolder(sa, sb) && p.ModelMap.TryAdd(ga, ga))
                {
                    p.KeptModels++;
                    p.Report.Add($"model {ga}: {dst.Name} has its own model {ga} (BMS's shared models, never changed): the piece keeps it and may look different");
                }
                return true;
            }
            return sa != null && sb != null && (Norm(sa) == Norm(sb) || SameFolder(sa, sb));
        }

        foreach (int ocd in ocds)
        {
            string sdir = Path.Combine(src.ObjectDir, "ObjectiveRelatedData", $"OCD_{ocd:00000}");
            string? ocdXml = Directory.Exists(sdir) ? Directory.EnumerateFiles(sdir, "OCD_*.XML").FirstOrDefault() : null;
            if (ocdXml == null) { p.Problems.Add($"OCD {ocd}: folder or OCD file not found in the source"); continue; }
            var so = s.Get("OCD", ocd);
            int sct = so?.Int("CtIdx") ?? -1;
            var sctr = s.Get("CT", sct);
            if (sctr == null) { p.Problems.Add($"OCD {ocd}: its CT entry {sct} not found in the source"); continue; }
            int nOcd = nextOcd++, nCt = nextCt++;
            p.OcdMap[ocd] = nOcd;
            string ddir = Path.Combine(dst.ObjectDir, "ObjectiveRelatedData", $"OCD_{nOcd:00000}");
            int f0 = p.AddedFeatures, r0 = p.ReusedFeatures, pieces = 0;
            foreach (var f in Directory.EnumerateFiles(sdir))
            {
                string fn = Path.GetFileName(f);
                string t = File.ReadAllText(f);
                if (Regex.IsMatch(fn, @"^OCD_\d{5}\.XML$", RegexOptions.IgnoreCase))
                {
                    t = Regex.Replace(t, @"<OCD Num=""\d+"">", $"<OCD Num=\"{nOcd}\">");
                    t = Regex.Replace(t, @"<CtIdx>\d+</CtIdx>", $"<CtIdx>{nCt}</CtIdx>");
                }
                else if (Regex.IsMatch(fn, @"^FED_\d{5}\.XML$", RegexOptions.IgnoreCase))
                {
                    t = Regex.Replace(t, @"<FeatureCtIdx>(\d+)</FeatureCtIdx>", m => $"<FeatureCtIdx>{MapFeature(int.Parse(m.Groups[1].Value, Ci))}</FeatureCtIdx>");
                    pieces = Regex.Matches(t, @"<FED Num=").Count;
                }
                else if (Regex.IsMatch(fn, @"^(PHD|PDX)_\d{5}\.XML$", RegexOptions.IgnoreCase))
                    t = Regex.Replace(t, @"<ObjIdx>\d+</ObjIdx>", $"<ObjIdx>{nOcd}</ObjIdx>");
                else if (Regex.IsMatch(fn, @"^OTD_\d{5}_L\d+\.TPB$", RegexOptions.IgnoreCase))
                    t = Regex.Replace(t, @"ct_idx:\s*\d+", $"ct_idx: {nCt}");   // gli alberi dicono di quale obiettivo sono (CT del tipo di obiettivo)
                else { p.Report.Add($"OCD {ocd}: {fn} not known, not copied"); continue; }
                p.Files[Path.Combine(ddir, Regex.Replace(fn, @"_\d{5}", $"_{nOcd:00000}", RegexOptions.None, TimeSpan.FromSeconds(1)))] = (null, t);
            }
            if (pieces > 255) p.Problems.Add($"OCD {ocd} has {pieces} pieces: BMS keeps at most 255");
            p.Records.Add(("CT", nCt, DbOps.With(sctr.Fields, ("EntityIdx", nOcd.ToString(Ci)))));
            p.Report.Add($"{so?.Name ?? $"OCD {ocd}"} (OCD {ocd}, CT {sct}) → OCD {nOcd}, CT {nCt}: {pieces} pieces, " +
                         $"{p.ReusedFeatures - r0} kinds of piece already in the destination, {p.AddedFeatures - f0} added");
        }
        p.Report.Insert(0, $"{src.Name} → {dst.Name}: {p.OcdMap.Count} layout(s), {p.ReusedFeatures} kinds of piece reused, {p.AddedFeatures} added " +
                           $"({p.Records.Count(r => r.Table == "CT")} CT, {p.Records.Count(r => r.Table == "FCD")} FCD, {p.Records.Count(r => r.Table == "RCD")} RCD), " +
                           $"{p.ReusedModels} models already there, {p.CopiedModels} copied into {Path.Combine(dst.ModelsDir, "Models")}");
        if (sharedModels)
            p.Report.Add($"Note: {dst.Name} takes its 3D models from BMS's shared Terrdata\\objects, which is never written: no model or texture is copied, " +
                         $"pieces use the models {dst.Name} already has ({p.KeptModels} different from {src.Name}).");
        return p;
    }

    private static string FeatKey(string name, DbRecord ct) => name.Trim().ToLowerInvariant() + "|" + ct.Int("GraphicsNormal", 0);

    /// <summary>Numbered textures (KoreaObj) of a classic model: copied if the destination does not have them; a different one with the same number is a problem.</summary>
    internal static void CheckTextures(Plan p, BmsTheater src, BmsTheater dst, string modelDir, string forWhat)
    {
        var sTex = new[] { Path.Combine(src.ModelsDir, "KoreaObj"), Path.Combine(p.BmsObjects, "KoreaObj") }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bml in Directory.EnumerateFiles(modelDir, "*.bml"))
        {
            try
            {
                var m = Editor.BmlModel.Load(bml, sTex[0], sTex.Count > 1 ? sTex[1] : null);
                foreach (var (tex, _, _) in m.Parts)
                    if (tex != null)
                    {
                        string t = tex.Split(Editor.BmlModel.EmissionSep)[0];
                        string dir = Path.GetFileName(Path.GetDirectoryName(t) ?? "");
                        if (dir.StartsWith("KoreaObj", StringComparison.OrdinalIgnoreCase)) used.Add(t);
                    }
            }
            catch (Exception) { /* modelo nuovo (BML v2) o non leggibile: texture nella sua cartella */ }
        }
        foreach (var t in used)
        {
            string name = Path.GetFileName(t), sub = Path.GetFileName(Path.GetDirectoryName(t)!);
            string dRoot = Path.Combine(dst.ModelsDir, sub);
            string dFile = Path.Combine(dRoot, name);
            string bFile = Path.Combine(p.BmsObjects, sub, name);
            string? have = File.Exists(dFile) ? dFile : File.Exists(bFile) ? bFile : null;
            if (have != null)
            {
                if (!SameFile(t, have)) p.Problems.Add($"{forWhat}: texture {sub}\\{name} is different in the destination (the model may look different)");
                continue;
            }
            p.Files[dFile] = (t, null);
            p.Report.Add($"texture {sub}\\{name} ({forWhat}) copied");
        }
    }

    // ─────────────────── writing and installing

    /// <summary>The destination's tables with the new records (Falcon4_XXX.xml) and every file of the plan, into outDir (mirror of the destination paths).</summary>
    public static List<string> Write(Plan p, string workDir, string outDir)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var edits = DbEdits.Open(Path.Combine(workDir, "plan"), p.Dst.Name, p.Dst.ObjectDir);
        edits.Data.Records.Clear();
        foreach (var (t, n, f) in p.Records) edits.Set(t, n, f);
        var written = new List<string>();
        string tables = Path.Combine(outDir, "tables");
        foreach (var f in DbInstaller.Write(edits, p.Dst.ObjectDir, tables)) written.Add(f);
        foreach (var (target, (src, text)) in p.Files)
        {
            string o = Path.Combine(outDir, "files", Mirror(target));
            Directory.CreateDirectory(Path.GetDirectoryName(o)!);
            if (src != null) File.Copy(src, o, true); else File.WriteAllText(o, text!, new UTF8Encoding(false));
            written.Add(o);
        }
        File.WriteAllLines(Path.Combine(outDir, "targets.txt"), p.Files.Keys);
        return written;
    }

    /// <summary>"C:\Falcon BMS 4.38\Data\x" → "C\Falcon BMS 4.38\Data\x" (a path that can live under another folder).</summary>
    private static string Mirror(string abs) => abs.Replace(":", "").TrimStart('\\', '/');

    /// <summary>
    /// Copies the written plan into the destination: tables into its objects folder, every file to its place. Files replaced
    /// are copied first to backupDir; manifest.txt lists what was added (Restore deletes it) and what was replaced. FALCON4.*
    /// of the destination are removed (BMS builds them again).
    /// </summary>
    /// <param name="addNewModelsToShared">An old version's own 3D models and textures (4.37 Units Converter): new files may be
    /// ADDED to BMS's shared Models and KoreaObj folders (never one that is there: the plan gives them free numbers).</param>
    public static List<string> Install(Plan p, string outDir, string backupRoot, bool addNewModelsToShared = false)
    {
        // sicurezza: niente si scrive nel Terrdata\objects condiviso di BMS (le cartelle dei teatri Add-On ne sono fuori)
        string shared = Norm(p.BmsObjects) + Path.DirectorySeparatorChar;
        bool NewModelFile(string t)
        {
            if (!addNewModelsToShared || File.Exists(t)) return false;
            string rel = Path.GetRelativePath(Norm(p.BmsObjects), Norm(t));
            string first = rel.Split('\\', '/')[0];
            return first.Equals("Models", StringComparison.OrdinalIgnoreCase) || first.StartsWith("KoreaObj", StringComparison.OrdinalIgnoreCase);
        }
        var bad = File.ReadAllLines(Path.Combine(outDir, "targets.txt")).Where(t => !NewModelFile(t)).Append(p.Dst.ObjectDir + Path.DirectorySeparatorChar)
            .Where(t => (Norm(t) + Path.DirectorySeparatorChar).StartsWith(shared, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0) throw new InvalidOperationException($"{bad.Count} file(s) would go into BMS's shared Terrdata\\objects (e.g. {bad[0]}): nothing installed.");
        var log = new List<string>();
        string bak = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(bak);
        var manifest = new List<string> { "objects;" + p.Dst.ObjectDir };
        string tables = Path.Combine(outDir, "tables");
        if (Directory.Exists(tables))
            foreach (var f in Directory.EnumerateFiles(tables, "Falcon4_*.xml"))
            {
                string target = Path.Combine(p.Dst.ObjectDir, Path.GetFileName(f));
                if (File.Exists(target)) { File.Copy(target, Path.Combine(bak, Path.GetFileName(f)), true); manifest.Add("replaced;" + target + ";" + Path.GetFileName(f)); }
                File.Copy(f, target, true);
                log.Add($"{Path.GetFileName(f)}: installed");
            }
        int added = 0, k = 0;
        foreach (var target in File.ReadAllLines(Path.Combine(outDir, "targets.txt")))
        {
            string o = Path.Combine(outDir, "files", Mirror(target));
            if (File.Exists(target))
            {
                string b = $"file_{k++:00000}_{Path.GetFileName(target)}";
                File.Copy(target, Path.Combine(bak, b), true);
                manifest.Add("replaced;" + target + ";" + b);
            }
            else manifest.Add("added;" + target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(o, target, true);
            added++;
        }
        log.Add($"{added} file(s) written (layouts, models, textures, racks, Sim files)");
        foreach (var f in Directory.EnumerateFiles(p.Dst.ObjectDir, "FALCON4.*").Where(f => !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Add($"{Path.GetFileName(f)}: NOT removed – {ex.Message}"); }
        }
        log.Add("FALCON4.* of the destination removed: BMS builds them again at the next start");
        File.WriteAllLines(Path.Combine(bak, "manifest.txt"), manifest);
        File.WriteAllLines(Path.Combine(bak, "report.txt"), p.Report.Concat(p.Problems.Select(x => "PROBLEM: " + x)));
        log.Add($"Backup and manifest: {bak}");
        return log;
    }

    public static List<(string Dir, DateTime Time, string Summary)> Backups(string backupRoot) =>
        !Directory.Exists(backupRoot) ? new()
        : Directory.EnumerateDirectories(backupRoot).Where(d => File.Exists(Path.Combine(d, "manifest.txt"))).OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => (d, Directory.GetCreationTime(d), File.Exists(Path.Combine(d, "report.txt")) ? File.ReadLines(Path.Combine(d, "report.txt")).FirstOrDefault() ?? "" : "")).ToList();

    /// <summary>Undoes an installation: deletes the files it added, puts back the ones it replaced, removes FALCON4.*.</summary>
    public static List<string> Restore(string backupDir)
    {
        var log = new List<string>();
        var lines = File.ReadAllLines(Path.Combine(backupDir, "manifest.txt"));
        string objects = lines.FirstOrDefault(l => l.StartsWith("objects;"))?[8..] ?? "";
        int del = 0, rep = 0;
        foreach (var l in lines)
        {
            var x = l.Split(';');
            if (x[0] == "added" && File.Exists(x[1])) { File.Delete(x[1]); del++; var dir = Path.GetDirectoryName(x[1])!; if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            else if (x[0] == "replaced" && x.Length >= 3 && File.Exists(Path.Combine(backupDir, x[2]))) { File.Copy(Path.Combine(backupDir, x[2]), x[1], true); rep++; }
        }
        if (Directory.Exists(objects))
            foreach (var f in Directory.EnumerateFiles(objects, "FALCON4.*").Where(f => !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Add($"{Path.GetFileName(f)}: NOT removed – {ex.Message}"); }
        log.Add($"{del} added files removed, {rep} files put back; FALCON4.* removed (BMS builds them again)");
        return log;
    }
}
