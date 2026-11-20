using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TerrainTool.Core.Database;

/// <summary>
/// An old add-on's own 3D models brought into BMS's shared models folder (Terrdata\objects): only ADDED, never replacing
/// anything. A model keeps its number when BMS has no model with it, otherwise it gets a free number (from 20000 up); the
/// number is kept in the project (imports_models.csv) so it stays the same at every Run Objects. The model files are not
/// changed: a version 1 model names its textures by KoreaObj number, so a texture is copied only when BMS has no texture
/// with that number, reused when BMS has the same one, and the model cannot be brought when BMS has a different texture with
/// that number (renumbering it would mean changing the model file).
/// </summary>
public sealed class AddOnModels
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public const string AssignFile = "imports_models.csv";
    public const string FilesFile = "imports_files.csv";
    public const string ManifestFile = "imported_files.txt";
    public const int FirstNew = 20000, LastNew = 32000;

    public sealed record Tex(int Id, bool Copy, string? Problem);

    public sealed record ModelPlan(int Source, int Target, string SourceDir, int Version, List<Tex> Textures, string? Problem, bool AlreadyThere)
    {
        public bool Ok => Problem == null;
        public string Text => Problem ?? (Source == Target ? $"own model {Source}" : $"own model {Source} → {Target} (BMS has another {Source})")
            + (Problem == null ? $", BML v{Version}" + (Textures.Count == 0 ? (Version >= 2 ? ", textures in its folder" : "") :
               $", textures {string.Join(" ", Textures.Select(t => t.Id))}" + (Textures.Any(t => t.Copy) ? $" ({Textures.Count(t => t.Copy)} copied)" : " (BMS's)"))
               + (AlreadyThere ? ", already in BMS" : "") : "");
    }

    private readonly Campaign.BmsTheater _src;
    private readonly string _bms;
    private readonly Dictionary<int, int> _assigned;     // source model → number in BMS (from the project)
    private readonly HashSet<int> _taken = new();
    private readonly Dictionary<int, ModelPlan> _plans = new();
    private readonly HashSet<string> _installed;

    public AddOnModels(Campaign.BmsTheater src, string bmsObjects, Dictionary<int, int> assigned, HashSet<string> installedByUs)
    {
        _src = src; _bms = bmsObjects; _assigned = assigned; _installed = installedByUs;
        foreach (var v in assigned.Values) _taken.Add(v);
    }

    public IReadOnlyDictionary<int, ModelPlan> Plans => _plans;

    private string SrcModel(int g) => Path.Combine(_src.ModelsDir, "Models", g.ToString(Ci));
    private string BmsModel(int g) => Path.Combine(_bms, "Models", g.ToString(Ci));
    private static bool HasModel(string dir) => Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).Equals("Parent.dat", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the source model is the same as BMS's with that number (nothing to bring).</summary>
    public bool SameAsBms(int g) => HasModel(SrcModel(g)) && HasModel(BmsModel(g)) && Interchange.SameFolder(SrcModel(g), BmsModel(g));

    public ModelPlan Plan(int g)
    {
        if (_plans.TryGetValue(g, out var done)) return done;
        string sdir = SrcModel(g);
        if (!HasModel(sdir)) return _plans[g] = new ModelPlan(g, g, sdir, 0, new(), $"model {g} is not in the add-on", false);
        // its number in BMS: the one already given, the same when free, else a free one
        int target;
        bool there = false;
        if (_assigned.TryGetValue(g, out int a) && (!HasModel(BmsModel(a)) || Interchange.SameFolder(sdir, BmsModel(a))))
        { target = a; there = HasModel(BmsModel(a)); }
        else if (!HasModel(BmsModel(g)) && !_taken.Contains(g)) target = g;
        else if (HasModel(BmsModel(g)) && Interchange.SameFolder(sdir, BmsModel(g))) { target = g; there = true; }
        else
        {
            target = -1;
            for (int n = FirstNew; n <= LastNew; n++) if (!_taken.Contains(n) && !Directory.Exists(BmsModel(n))) { target = n; break; }
            if (target < 0) return _plans[g] = new ModelPlan(g, g, sdir, 0, new(), "no free model number", false);
        }
        _taken.Add(target);
        _assigned[g] = target;
        // textures named by number (version 1)
        int ver = 0;
        var ids = new List<int>();
        try
        {
            foreach (var bml in Directory.EnumerateFiles(sdir, "*.bml"))
            {
                var (v, t) = Editor.BmlModel.TextureIds(bml);
                ver = Math.Max(ver, v);
                foreach (var id in t) if (!ids.Contains(id)) ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        { return _plans[g] = new ModelPlan(g, target, sdir, 0, new(), $"model {g}: {ex.Message}", false); }
        var tex = new List<Tex>();
        foreach (int id in ids.OrderBy(x => x))
        {
            string s = Path.Combine(_src.ModelsDir, "KoreaObj", id.ToString(Ci) + ".dds");
            string b = Path.Combine(_bms, "KoreaObj", id.ToString(Ci) + ".dds");
            if (File.Exists(b))
            {
                if (!File.Exists(s) || SameFile(s, b)) tex.Add(new Tex(id, false, null));
                else tex.Add(new Tex(id, false, $"texture {id}.dds is different in BMS (renumbering it would mean changing the model file)"));
            }
            else if (File.Exists(s)) tex.Add(new Tex(id, true, null));
            else tex.Add(new Tex(id, false, $"texture {id}.dds is neither in the add-on nor in BMS"));
        }
        string? problem = tex.FirstOrDefault(t => t.Problem != null)?.Problem;
        return _plans[g] = new ModelPlan(g, target, sdir, ver, tex, problem, there);
    }

    /// <summary>Files to add to BMS's objects folder for a model: (path relative to Terrdata\objects, source file).</summary>
    public IEnumerable<(string Rel, string From)> Files(ModelPlan m)
    {
        if (!m.Ok) yield break;
        foreach (var f in Directory.EnumerateFiles(m.SourceDir))
            yield return (Path.Combine("Models", m.Target.ToString(Ci), Path.GetFileName(f)), f);
        foreach (var t in m.Textures.Where(t => t.Copy))
            foreach (var sub in new[] { "KoreaObj", "KoreaObj_HiRes" })
            {
                string s = Path.Combine(_src.ModelsDir, sub, t.Id.ToString(Ci) + ".dds");
                if (File.Exists(s)) yield return (Path.Combine(sub, t.Id.ToString(Ci) + ".dds"), s);
            }
    }

    private static string Hash(string f) { using var s = File.OpenRead(f); return Convert.ToHexString(SHA1.HashData(s)); }
    public static bool SameFile(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && Hash(a) == Hash(b);

    // ───────────────────────── project files

    public static Dictionary<int, int> LoadAssigned(string projectFolder)
    {
        var res = new Dictionary<int, int>();
        string f = Path.Combine(projectFolder, AssignFile);
        if (!File.Exists(f)) return res;
        foreach (var l in File.ReadAllLines(f).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length >= 2 && int.TryParse(c[0], NumberStyles.Integer, Ci, out int a) && int.TryParse(c[1], NumberStyles.Integer, Ci, out int b)) res[a] = b;
        }
        return res;
    }

    public static void SaveAssigned(string projectFolder, Dictionary<int, int> m)
    {
        var sb = new StringBuilder("SourceModel;BmsModel\r\n");
        foreach (var kv in m.OrderBy(k => k.Key)) sb.Append(kv.Key.ToString(Ci)).Append(';').Append(kv.Value.ToString(Ci)).Append("\r\n");
        File.WriteAllText(Path.Combine(projectFolder, AssignFile), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The files the install added to BMS's objects folder (paths relative to it), from the project's manifest.</summary>
    public static HashSet<string> LoadInstalled(string projectFolder)
    {
        string f = Path.Combine(projectFolder, "Output", "BmsBackup", ManifestFile);
        return File.Exists(f) ? File.ReadAllLines(f).Where(l => l.Trim().Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    public static void AddInstalled(string projectFolder, IEnumerable<string> rels)
    {
        var all = LoadInstalled(projectFolder);
        foreach (var r in rels) all.Add(r);
        string f = Path.Combine(projectFolder, "Output", "BmsBackup", ManifestFile);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllLines(f, all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies the files listed by Run Objects (Output\Install\imports_files.csv) into BMS's objects folder: only files that are
    /// not there, or that this project put there before; a different file BMS has is never replaced. Returns the log lines.
    /// </summary>
    public static List<string> Install(string projectFolder, string bmsObjects)
    {
        var log = new List<string>();
        string list = Path.Combine(projectFolder, "Output", "Install", FilesFile);
        if (!File.Exists(list)) return log;
        var ours = LoadInstalled(projectFolder);
        var added = new List<string>();
        int same = 0;
        foreach (var l in File.ReadAllLines(list).Skip(1))
        {
            var c = l.Split(';');
            if (c.Length < 2) continue;
            string rel = c[0], from = c[1];
            if (!File.Exists(from)) { log.Add($"  Imported models: {from} missing, {rel} not copied"); continue; }
            string to = Path.Combine(bmsObjects, rel);
            if (File.Exists(to))
            {
                if (SameFile(from, to)) { same++; continue; }
                if (!ours.Contains(rel)) { log.Add($"  Imported models: BMS has a different {rel}: NOT replaced (run Objects again: the model gets another number)"); continue; }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
            added.Add(rel);
        }
        if (added.Count > 0) AddInstalled(projectFolder, added);
        log.Add($"  Imported models: {added.Count} files added to {bmsObjects}" + (same > 0 ? $", {same} already there" : "") +
                $" (listed in Output\\BmsBackup\\{ManifestFile}; 'Remove imported models' takes them away)");
        return log;
    }

    /// <summary>Removes from BMS's objects folder the files this project added (manifest), and empty model folders.</summary>
    public static List<string> Remove(string projectFolder, string bmsObjects)
    {
        var log = new List<string>();
        var ours = LoadInstalled(projectFolder);
        int n = 0;
        foreach (var rel in ours)
        {
            string f = Path.Combine(bmsObjects, rel);
            if (!File.Exists(f)) continue;
            File.Delete(f); n++;
            string? d = Path.GetDirectoryName(f);
            if (d != null && Path.GetFileName(Path.GetDirectoryName(d) ?? "").Equals("Models", StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(d).Any())
                Directory.Delete(d);
        }
        string m = Path.Combine(projectFolder, "Output", "BmsBackup", ManifestFile);
        if (File.Exists(m)) File.Delete(m);
        log.Add($"Imported models: {n} files removed from {bmsObjects}");
        return log;
    }
}
