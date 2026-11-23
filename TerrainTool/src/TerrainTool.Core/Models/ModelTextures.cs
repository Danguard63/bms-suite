using System.Globalization;
using System.Text.Json;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Models;

/// <summary>Una texture di un modello: file DDS usato da BMS, ruolo, materiali, livree (set) e LOD che la usano.</summary>
public sealed class ModelTexture
{
    public string File { get; init; } = "";
    /// <summary>Altra copia con lo stesso numero (KoreaObj accanto a KoreaObj_HiRes), o null.</summary>
    public string? OtherCopy { get; set; }
    public string Role { get; set; } = "";
    public SortedSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<int> Sets { get; } = new();
    public SortedSet<int> Lods { get; } = new();
    public string Name => Path.GetFileName(File);
    public string SetsText => Sets.Count == 0 ? "" : string.Join(",", Sets.Select(s => (s + 1).ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
/// Texture dei modelli 3D di BMS 4.3x (Terrdata\objects\Models\&lt;n&gt;). Modelli PBR (BML v2): i file indicati per slot in
/// Materials.mtl, livree dai file Model_N.mti. Modelli vecchi (BML v1): i numeri di KoreaObj del BML, divisi per set
/// (TextureSets di Parent.dat); il file usato e' quello di KoreaObj_HiRes se c'e', altrimenti KoreaObj. Solo lettura.
/// </summary>
public static class ModelTextures
{
    public static string RoleOf(int slot, string file)
    {
        string f = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (f.Contains("albedo") || f.EndsWith("_alb") || f.Contains("_alb")) return "Albedo (colour)";
        if (f.Contains("normal") || f.EndsWith("_nrm") || f.Contains("_nrm")) return "Normal map";
        if (f.Contains("armw")) return "ARMW (AO, roughness, metal)";
        if (f.Contains("emm") || f.Contains("emis")) return "Emissive (lights)";
        return slot switch { 0 => "Slot 0 (colour)", 1 => "Slot 1", 2 => "Slot 2", 3 => "Slot 3", _ => $"Slot {slot}" };
    }

    /// <summary>Le texture del modello (tutti i LOD e tutte le livree). textureDirs = KoreaObj_HiRes, KoreaObj.</summary>
    public static List<ModelTexture> List(string modelDir, IReadOnlyList<string> textureDirs, List<string>? notes = null)
    {
        var res = new Dictionary<string, ModelTexture>(StringComparer.OrdinalIgnoreCase);
        var parent = BmlModel.ReadParent(modelDir);
        if (parent == null) { notes?.Add("Parent.dat not found"); return new(); }
        string modelsRoot = Path.GetDirectoryName(modelDir)!;
        ModelTexture Get(string file)
        {
            if (!res.TryGetValue(file, out var t)) res[file] = t = new ModelTexture { File = file };
            return t;
        }

        // materiali PBR (Materials.mtl): nome → (slot, file)
        Dictionary<string, List<(int Slot, string File)>>? mtl = null;
        string mtlPath = Path.Combine(modelDir, "Materials.mtl");
        if (System.IO.File.Exists(mtlPath))
        {
            mtl = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(mtlPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("Materials", out var arr))
                    foreach (var m in arr.EnumerateArray())
                    {
                        string name = m.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                        var list = new List<(int, string)>();
                        if (m.TryGetProperty("Textures", out var tx))
                            foreach (var t in tx.EnumerateArray())
                                if (t.TryGetProperty("File", out var fl) && fl.GetString() is string fs)
                                    list.Add((t.TryGetProperty("Slot", out var sl) ? sl.GetInt32() : 0, fs));
                        mtl[name] = list;
                    }
            }
            catch (Exception e) { notes?.Add("Materials.mtl: " + e.Message); }
        }
        string? ResolvePbr(string rel)
        {
            // accanto al modello, nella cartella Models ("1909/x.dds") o dalla cartella di BMS ("Data/TerrData/...")
            rel = rel.Replace('/', Path.DirectorySeparatorChar);
            var f = BmlScene.TextureCandidates(modelDir, modelsRoot, rel).FirstOrDefault(System.IO.File.Exists);
            return f == null ? null : Path.GetFullPath(f);
        }

        for (int lod = 0; lod < parent.Lods.Count; lod++)
        {
            string file = Path.Combine(modelDir, parent.Lods[lod].File);
            if (!System.IO.File.Exists(file)) continue;
            int ver; List<int> ids; List<string> names;
            try { (ver, ids, names) = Materials(file); }
            catch (Exception e) { notes?.Add($"{parent.Lods[lod].File}: {e.Message}"); continue; }
            if (ver == 1)
            {
                int sets = parent.TextureSets;
                int per = sets > 1 && ids.Count % sets == 0 ? ids.Count / sets : ids.Count;
                for (int i = 0; i < ids.Count; i++)
                {
                    // 9990-9999: numeri speciali dei modelli vecchi (nessuna texture / colore), non sono file
                    if (ids[i] < 0 || ids[i] is >= 9990 and <= 9999) continue;
                    string? found = null, other = null;
                    foreach (var d in textureDirs)
                    {
                        var f = Path.Combine(d, ids[i].ToString(CultureInfo.InvariantCulture) + ".dds");
                        if (!System.IO.File.Exists(f)) continue;
                        if (found == null) found = f; else other ??= f;
                    }
                    if (found == null) { notes?.Add($"texture {ids[i]}.dds not found"); continue; }
                    var t = Get(Path.GetFullPath(found));
                    t.OtherCopy ??= other;
                    t.Role = "Colour (KoreaObj " + ids[i] + ")";
                    t.Materials.Add(ids[i].ToString(CultureInfo.InvariantCulture));
                    t.Sets.Add(per == ids.Count ? 0 : i / per);
                    t.Lods.Add(lod);
                }
            }
            else if (mtl != null)
            {
                // livree: righe del .mti (nomi dei materiali per set), altrimenti i nomi del BML
                var setNames = new List<List<string>> { names };
                string mti = Path.Combine(modelDir, Path.ChangeExtension(parent.Lods[lod].File, ".mti"));
                if (System.IO.File.Exists(mti) && names.Count > 0)
                {
                    var lines = System.IO.File.ReadAllLines(mti).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    if (lines.Count >= names.Count && lines.Count % names.Count == 0)
                        setNames = Enumerable.Range(0, lines.Count / names.Count).Select(k => lines.Skip(k * names.Count).Take(names.Count).ToList()).ToList();
                }
                for (int s = 0; s < setNames.Count; s++)
                    foreach (var mn in setNames[s])
                        if (mtl.TryGetValue(mn, out var texs))
                            foreach (var (slot, rel) in texs)
                            {
                                var f = ResolvePbr(rel);
                                if (f == null) { notes?.Add($"{rel} (material {mn}) not found"); continue; }
                                var t = Get(f);
                                if (t.Role.Length == 0) t.Role = RoleOf(slot, f);
                                t.Materials.Add(mn); t.Sets.Add(s); t.Lods.Add(lod);
                            }
            }
        }
        return res.Values.OrderBy(t => t.Role.StartsWith("Albedo") || t.Role.StartsWith("Colour") ? 0 : 1).ThenBy(t => t.Sets.FirstOrDefault()).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Tutti i file di texture che il modello puo' usare, senza leggere la geometria: modelli PBR da Materials.mtl (nessun BML
    /// da decomprimere), modelli vecchi dai numeri dei BML (piccoli). Per l'indice "chi usa questa texture".
    /// </summary>
    public static List<string> FilesQuick(string modelDir, IReadOnlyList<string> textureDirs)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string mtl = Path.Combine(modelDir, "Materials.mtl"), root = Path.GetDirectoryName(modelDir)!;
        if (System.IO.File.Exists(mtl))
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(mtl), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("Materials", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("Textures", out var tx))
                        foreach (var t in tx.EnumerateArray())
                            if (t.TryGetProperty("File", out var fl) && fl.GetString() is string rel)
                            {
                                rel = rel.Replace('/', Path.DirectorySeparatorChar);
                                var a = Path.Combine(modelDir, rel); var b = Path.Combine(root, rel);
                                if (System.IO.File.Exists(a)) res.Add(Path.GetFullPath(a)); else if (System.IO.File.Exists(b)) res.Add(Path.GetFullPath(b));
                            }
            return res.ToList();
        }
        var parent = BmlModel.ReadParent(modelDir);
        if (parent == null) return res.ToList();
        foreach (var (lodFile, _) in parent.Lods)
        {
            string f = Path.Combine(modelDir, lodFile);
            if (!System.IO.File.Exists(f)) continue;
            try
            {
                var (ver, ids, _) = Materials(f);
                if (ver != 1) continue;
                foreach (int id in ids.Where(i => i >= 0))
                    foreach (var d in textureDirs)
                    {
                        var t = Path.Combine(d, id.ToString(CultureInfo.InvariantCulture) + ".dds");
                        if (System.IO.File.Exists(t)) { res.Add(Path.GetFullPath(t)); break; }
                    }
            }
            catch { }
        }
        return res.ToList();
    }

    /// <summary>Versione del BML e materiali: numeri di texture (v1) o nomi dei materiali (v2).</summary>
    public static (int Version, List<int> Ids, List<string> Names) Materials(string bml)
    {
        var b = BmlModel.ReadPayload(bml, out int ver);
        int o = 0;
        int U() { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        U();
        int mc = U();
        var ids = new List<int>(); var names = new List<string>();
        for (int i = 0; i < mc; i++)
        {
            if (ver == 1) ids.Add(U());
            else { int n = U(); names.Add(System.Text.Encoding.ASCII.GetString(b, o, n)); o += n; }
        }
        return (ver, ids, names);
    }
}

/// <summary>
/// Quali modelli usano ciascun file di texture (per avvisare prima di sostituire una texture condivisa). Si costruisce
/// leggendo tutti i modelli (qualche minuto la prima volta) e si salva in un file di cache.
/// </summary>
public sealed class TextureUsageIndex
{
    public Dictionary<string, List<int>> ByFile { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ModelsRoot { get; init; } = "";
    public DateTime Built { get; set; }

    public IReadOnlyList<int> ModelsUsing(string file) => ByFile.TryGetValue(Path.GetFullPath(file), out var l) ? l : Array.Empty<int>();

    public static TextureUsageIndex Build(string modelsRoot, IReadOnlyList<string> textureDirs, IProgress<double>? p, CancellationToken ct)
    {
        var idx = new TextureUsageIndex { ModelsRoot = modelsRoot, Built = DateTime.Now };
        var dirs = Directory.EnumerateDirectories(modelsRoot).Where(d => int.TryParse(Path.GetFileName(d), out _)).ToList();
        var lk = new object();
        int done = 0;
        Parallel.ForEach(dirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, d =>
        {
            int id = int.Parse(Path.GetFileName(d), CultureInfo.InvariantCulture);
            List<string> files;
            try { files = ModelTextures.FilesQuick(d, textureDirs); } catch { files = new(); }
            lock (lk)
                foreach (var f in files)
                {
                    if (!idx.ByFile.TryGetValue(f, out var l)) idx.ByFile[f] = l = new List<int>();
                    if (!l.Contains(id)) l.Add(id);
                }
            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0) p?.Report((double)n / dirs.Count);
        });
        foreach (var l in idx.ByFile.Values) l.Sort();
        return idx;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        w.WriteLine(ModelsRoot);
        w.WriteLine(Built.ToString("o", CultureInfo.InvariantCulture));
        foreach (var (f, l) in ByFile) w.WriteLine(f + "\t" + string.Join(",", l));
    }

    public static TextureUsageIndex? Load(string path, string modelsRoot)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var lines = System.IO.File.ReadAllLines(path);
            if (lines.Length < 2 || !string.Equals(lines[0], modelsRoot, StringComparison.OrdinalIgnoreCase)) return null;
            var idx = new TextureUsageIndex { ModelsRoot = modelsRoot, Built = DateTime.Parse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
            foreach (var l in lines.Skip(2))
            {
                int t = l.IndexOf('\t'); if (t < 0) continue;
                idx.ByFile[l[..t]] = l[(t + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
            }
            return idx;
        }
        catch { return null; }
    }
}

/// <summary>Mezzi del database (aerei, elicotteri, mezzi di terra, navi) con il numero del loro modello 3D.</summary>
public sealed record ModelEntry(int Ct, string Name, string Category, int ModelId, string? ModelDir);

public static class ModelCatalog
{
    /// <summary>Tutti i mezzi con un modello, da un database di BMS (Falcon4_CT/VCD). Categorie dal dominio del CT.</summary>
    public static List<ModelEntry> Vehicles(CampaignDb db)
    {
        var res = new List<ModelEntry>();
        foreach (var (ct, c) in db.Ct)
        {
            if (c.Graphics <= 0 || c.EntityType != 5) continue;     // solo i mezzi (EntityType 5 = VCD); 6 = armi, 1 = edifici
            string name = db.VehicleName(ct);
            if (string.IsNullOrWhiteSpace(name)) continue;          // non e' un mezzo (VCD)
            string cat = c.Domain switch { 2 => c.Type == 5 ? "Helicopters" : "Aircraft", 3 => "Ground vehicles", 4 => "Ships", _ => "Other" };
            res.Add(new ModelEntry(ct, name.Trim(), cat, c.Graphics, db.ModelDir(ct)));
        }
        return res.GroupBy(e => (e.Name, e.ModelId)).Select(g => g.First()).OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Armi con un modello (EntityType 6 = WCD), categoria "Weapons".</summary>
    public static List<ModelEntry> Weapons(CampaignDb db)
    {
        var res = new List<ModelEntry>();
        foreach (var (ct, c) in db.Ct)
        {
            if (c.Graphics <= 0 || c.EntityType != 6) continue;
            if (!db.Wcd.TryGetValue(c.EntityIdx, out var name) || string.IsNullOrWhiteSpace(name)) continue;
            res.Add(new ModelEntry(ct, name.Trim(), "Weapons", c.Graphics, db.ModelDir(ct)));
        }
        return res.GroupBy(e => (e.Name, e.ModelId)).Select(g => g.First()).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
