using System.Globalization;
using System.Text.Json;

namespace TerrainTool.Core.Models;

/// <summary>
/// Modello BML con la sua struttura (DOF, switch, slot) che si puo' mettere in posa: valori dei DOF e switch accesi/spenti
/// come in BMS. Regole dall'esportatore ufficiale di Benchmark Sims (bms-blender-plugin, licenza MIT):
///  - DOF rotazione (tipo 0): il pezzo ruota attorno all'asse Z locale del DOF di (valore × moltiplicatore) radianti,
///    poi la matrice e la posizione del DOF lo portano nel sistema del padre;
///  - DOF traslazione (tipo 1): il campo "translation" e' il vettore di spostamento: a riposo nessuno spostamento,
///    con valore v il pezzo si sposta di v × vettore;
///  - DOF scala (tipo 2): scala 1 + v × (scala − 1) attorno alla posizione del DOF;
///  - flag: 1 = limiti min/max, 2 = verso inverso, 4 = normalizza (valore portato a 0..1 tra min e max);
///  - switch: numero, ramo, acceso all'inizio; slot: numero, rotazione, origine (dove si montano le armi).
/// Solo lettura.
/// </summary>
public sealed partial class BmlScene
{
    public enum DofKind { Rotate = 0, Translate = 1, Scale = 2 }

    public sealed record DofInfo(int Number, DofKind Kind, double Min, double Max, double Multiplier, int Flags, float[] Scale, float[] T, float[] R, bool ValidMatrix)
    {
        public bool Limits => (Flags & 1) != 0;
        public bool Reverse => (Flags & 2) != 0;
        public bool Normalise => (Flags & 4) != 0;
    }
    public sealed record SwitchInfo(int Number, int Branch, bool StartsOn);

    /// <summary>
    /// Switch che BMS accende da solo in volo anche se nel file partono spenti. 61 = numero dell'aereo sulla deriva: ogni ramo
    /// e' una copia della deriva (e del timone) con un numero diverso e BMS ne accende uno; se nel file sono tutti spenti,
    /// per vedere il modello come in BMS si accende il primo ramo.
    /// </summary>
    public static readonly int[] SimSelected = { 61 };

    /// <summary>Rami da accendere in piu' "come in BMS": il primo ramo degli switch di SimSelected che nel file sono tutti spenti.</summary>
    public static HashSet<(int, int)> SimDefaults(IEnumerable<SwitchInfo> switches)
    {
        var l = switches.ToList();
        var res = new HashSet<(int, int)>();
        foreach (var g in l.Where(s => SimSelected.Contains(s.Number)).GroupBy(s => s.Number))
            if (!g.Any(s => s.StartsOn)) res.Add((g.Key, g.Min(s => s.Branch)));
        return res;
    }

    /// <summary>Rami accesi in piu' a riposo "come in BMS" (vedi SimDefaults); gli altri seguono il file, nodo per nodo.</summary>
    public HashSet<(int, int)> DefaultOn { get; private set; } = new();
    public sealed record SlotInfo(int Number, float[] R, float[] Origin);

    private sealed class Node
    {
        public int Type;
        public DofInfo? Dof;
        public SwitchInfo? Switch;
        public SlotInfo? Slot;
        // primitiva
        public int IndexCount, StartIndex, VertexStartIndex, VertexOffset, VertexCount, VertexSize, Material;
    }

    public int Version { get; private set; }
    public List<string> Notes { get; } = new();
    public List<DofInfo> Dofs { get; } = new();
    public List<SwitchInfo> Switches { get; } = new();
    public List<SlotInfo> Slots { get; } = new();
    public int PrimitiveCount { get; private set; }
    /// <summary>Materiali del LOD (modelli vecchi: numeri delle texture di tutte le livree; PBR: nomi dei materiali).</summary>
    public int MaterialCount { get; private set; }
    private List<int> _texIds = new();
    private List<string> _texDirs = new();

    /// <summary>Texture numerate (modelli vecchi) che non esistono in nessuna cartella delle texture.</summary>
    /// <summary>
    /// Dove cercare una texture di Materials.mtl: accanto al modello, nella cartella Models ("1909/x.dds") o, per i percorsi
    /// che iniziano con "Data/", dalla cartella di BMS (Models → objects → Terrdata → Data → BMS).
    /// </summary>
    public static IEnumerable<string> TextureCandidates(string modelDir, string modelsRoot, string rel)
    {
        yield return Path.Combine(modelDir, rel);
        yield return Path.Combine(modelsRoot, rel);
        if (rel.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            var bms = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(modelsRoot)))));
            if (bms != null) yield return Path.Combine(bms, rel);
        }
    }

    /// <summary>Numeri di texture speciali dei modelli vecchi (9998, 9999: nessuna texture / colore), non sono file.</summary>
    public static bool SpecialTextureId(int id) => id >= 9990;

    public IEnumerable<int> MissingTextures() => _texIds.Where(id => id >= 0 && !SpecialTextureId(id)).Distinct()
        .Where(id => !_texDirs.Any(d => File.Exists(Path.Combine(d, id.ToString(CultureInfo.InvariantCulture) + ".dds"))));

    /// <summary>Materiali (nome o numero di texture) e texture trovata, per la livrea caricata.</summary>
    public IEnumerable<(string Name, string? Texture)> MaterialList => _mats.Select(m => (m.Name, m.Tex));

    private byte[] _b = Array.Empty<byte>();
    private readonly List<Node> _nodes = new();
    private int _ibAt, _ibSize, _vbAt, _vbSize, _ibFormat;
    private readonly List<(string? Tex, bool Glass, bool Skip, string Name)> _mats = new();

    /// <summary>Legge un LOD con la livrea indicata. textureDirs = KoreaObj_HiRes, KoreaObj (modelli vecchi).</summary>
    public static BmlScene Load(BmlModel.ParentInfo parent, int lod, int textureSet, string modelsRoot, IReadOnlyList<string> textureDirs)
    {
        var s = new BmlScene();
        if (parent.Lods.Count == 0) throw new InvalidDataException("Parent.dat lists no LOD");
        lod = Math.Clamp(lod, 0, parent.Lods.Count - 1);
        var b = BmlModel.ReadPayload(Path.Combine(parent.Folder, parent.Lods[lod].File), out int ver);
        s._b = b; s.Version = ver;
        int o = 0;
        int U() { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        float F() { float v = BitConverter.ToSingle(b, o); o += 4; return v; }
        float[] Fs(int n) { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = F(); return r; }

        U();
        int mc = U();
        var ids = new List<int>(); var names = new List<string>();
        for (int i = 0; i < mc; i++)
        {
            if (ver == 1) ids.Add(U());
            else { int n = U(); names.Add(System.Text.Encoding.ASCII.GetString(b, o, n)); o += n; }
        }
        s.MaterialCount = mc; s._texIds = ids; s._texDirs = textureDirs.ToList();
        s._ibFormat = U(); U(); U();
        int nodeCount = U();
        var seenDof = new HashSet<int>(); var seenSw = new HashSet<(int, int)>();
        for (int k = 0; k < nodeCount; k++)
        {
            int type = U(); U(); U();
            var n = new Node { Type = type };
            switch (type)
            {
                case 1:
                    U(); F();
                    n.IndexCount = U(); n.StartIndex = U(); n.VertexStartIndex = U(); n.VertexOffset = U(); n.VertexCount = U(); n.VertexSize = U();
                    o += 12;
                    if (ver == 1) { o += 2; U(); U(); U(); n.Material = U(); U(); }
                    else { o += 2; n.Material = BitConverter.ToUInt16(b, o); o += 2; }
                    s.PrimitiveCount++;
                    break;
                case 2:
                {
                    int num = U(), dt = U(); float mn = F(), mx = F(), mul = F(); int fl = U();
                    var sc = Fs(3); var t = Fs(3); var r = Fs(9);
                    var kind = dt is 0 or 1 or 2 ? (DofKind)dt : DofKind.Rotate;
                    n.Dof = new DofInfo(num, kind, Finite(mn) ? mn : 0, Finite(mx) ? mx : 0, Finite(mul) && mul != 0 ? mul : 1, fl, sc, t, r, IsRotation(r));
                    if (seenDof.Add(num)) s.Dofs.Add(n.Dof);
                    break;
                }
                case 4:
                {
                    int num = U(), br = U(); bool on = b[o] != 0; o += 1;
                    n.Switch = new SwitchInfo(num, br, on);
                    if (seenSw.Add((num, br))) s.Switches.Add(n.Switch);
                    break;
                }
                case 6:
                {
                    int num = U(); var r = Fs(9); var org = Fs(3);
                    n.Slot = new SlotInfo(num, r, org);
                    s.Slots.Add(n.Slot);
                    break;
                }
                case 3: case 5: case 7: break;
                case 8: o += 36; break;
                default: throw new InvalidDataException($"unknown BML node type {type}");
            }
            s._nodes.Add(n);
        }
        s._ibSize = U(); s._ibAt = o; o += s._ibSize;
        // esportatore Blender: tutte le primitive con start index 0 e indici uno dopo l'altro nel buffer
        var prims = s._nodes.Where(n => n.Type == 1).ToList();
        if (prims.Count > 1 && prims.All(p => p.StartIndex == 0))
        {
            int cur = 0;
            foreach (var p in prims) { p.StartIndex = cur; cur += p.IndexCount; }
        }
        s._vbSize = U(); s._vbAt = o;
        if (s._vbAt + s._vbSize > b.Length) throw new InvalidDataException("BML vertex buffer out of range");
        s.Dofs.Sort((a, c) => a.Number.CompareTo(c.Number));
        s.DefaultOn = SimDefaults(s.Switches);
        s.Switches.Sort((a, c) => a.Number != c.Number ? a.Number.CompareTo(c.Number) : a.Branch.CompareTo(c.Branch));

        // materiali → texture
        if (ver == 1)
        {
            int per = parent.TextureSets > 1 && ids.Count % parent.TextureSets == 0 ? ids.Count / parent.TextureSets : ids.Count;
            int set = per == ids.Count ? 0 : Math.Clamp(textureSet, 0, parent.TextureSets - 1);
            for (int i = 0; i < per; i++)
            {
                int id = ids[Math.Min(ids.Count - 1, set * per + i)];
                string? f = textureDirs.Select(d => Path.Combine(d, id.ToString(CultureInfo.InvariantCulture) + ".dds")).FirstOrDefault(File.Exists);
                s._mats.Add((f, false, false, id.ToString(CultureInfo.InvariantCulture)));
            }
        }
        else
        {
            var mtl = ReadMtl(parent.Folder, s.Notes);
            var setNames = names;
            string mti = Path.Combine(parent.Folder, Path.ChangeExtension(parent.Lods[lod].File, ".mti"));
            if (textureSet > 0 && File.Exists(mti))
            {
                var lines = File.ReadAllLines(mti).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (names.Count > 0 && lines.Count >= (textureSet + 1) * names.Count) setNames = lines.Skip(textureSet * names.Count).Take(names.Count).ToList();
            }
            // mappe PBR complete (vista DirectX): colore, ARMW, normal, emissiva per i materiali della livrea caricata
            foreach (var (mn, maps) in ReadPbrMaps(parent.Folder, modelsRoot))
                if (setNames.Contains(mn, StringComparer.OrdinalIgnoreCase)) s.Pbr[mn] = maps;
            foreach (var mn in setNames)
            {
                if (!mtl.TryGetValue(mn, out var mt)) { s._mats.Add((null, false, false, mn)); continue; }
                bool skip = mt.Template.Contains("Emissive", StringComparison.OrdinalIgnoreCase) || mt.Template.Contains("AfterBurner", StringComparison.OrdinalIgnoreCase);
                bool glass = mt.Template.Contains("Glass", StringComparison.OrdinalIgnoreCase);
                string? f = null;
                if (mt.Albedo != null)
                {
                    string rel = mt.Albedo.Replace('/', Path.DirectorySeparatorChar);
                    f = TextureCandidates(parent.Folder, modelsRoot, rel).FirstOrDefault(File.Exists);
                }
                s._mats.Add((f, glass, skip, mn));
            }
        }
        return s;
    }

    /// <summary>Texture di un materiale PBR (percorsi completi, null se manca) e come BMS lo disegna.</summary>
    /// <param name="Albedo">slot 0: colore (sRGB)</param>
    /// <param name="Armw">slot 1: R ombre di contatto, G rugosita', B metallo, A bagnato (come l'ORM del glTF)</param>
    /// <param name="Normal">slot 2: normal map (BC5: solo X e Y)</param>
    /// <param name="Emissive">slot 3: parti che brillano</param>
    public sealed record PbrMaps(string Template, string? Albedo, string? Armw, string? Normal, string? Emissive, double EmissionIntensity, bool TwoSided)
    {
        public bool Glass => Template.Contains("Glass", StringComparison.OrdinalIgnoreCase);
        public bool EmissiveOnly => Template.Contains("Emissive", StringComparison.OrdinalIgnoreCase) || Template.Contains("AfterBurner", StringComparison.OrdinalIgnoreCase);
        public bool Alpha => Template.Contains("Alpha", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Materiali PBR della livrea caricata (nome del materiale → texture), vuoto per i modelli vecchi.</summary>
    public Dictionary<string, PbrMaps> Pbr { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, PbrMaps Maps)> ReadPbrMaps(string folder, string modelsRoot)
    {
        string f = Path.Combine(folder, "Materials.mtl");
        if (!File.Exists(f)) yield break;
        var list = new List<(string, PbrMaps)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("Materials", out var arr))
                foreach (var m in arr.EnumerateArray())
                {
                    string name = m.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "" : "";
                    string tpl = m.TryGetProperty("Template", out var tp) && tp.TryGetProperty("Material", out var tm) ? tm.GetString() ?? "" : "";
                    var slots = new string?[4];
                    if (m.TryGetProperty("Textures", out var tx))
                        foreach (var t in tx.EnumerateArray())
                            if (t.TryGetProperty("Slot", out var sl) && t.TryGetProperty("File", out var fl) && sl.GetInt32() is >= 0 and < 4 && fl.GetString() is string file)
                                slots[sl.GetInt32()] = TextureCandidates(folder, modelsRoot, file.Replace('/', Path.DirectorySeparatorChar)).FirstOrDefault(File.Exists);
                    double em = 1;
                    if (m.TryGetProperty("ShaderParams", out var sp))
                        foreach (var p in sp.EnumerateArray())
                            if (p.TryGetProperty("Constants", out var c) && c.TryGetProperty("emissionIntensity", out var ei) && ei.TryGetDouble(out double v)) em = v;
                    bool twoSided = m.TryGetProperty("Flags", out var fg) && fg.TryGetProperty("Cull", out var cu) && string.Equals(cu.GetString(), "NONE", StringComparison.OrdinalIgnoreCase);
                    list.Add((name, new PbrMaps(tpl, slots[0], slots[1], slots[2], slots[3], em, twoSided)));
                }
        }
        catch (Exception) { list.Clear(); }
        foreach (var x in list) yield return x;
    }

    private static Dictionary<string, (string Template, string? Albedo)> ReadMtl(string folder, List<string> notes)
    {
        var res = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
        string f = Path.Combine(folder, "Materials.mtl");
        if (!File.Exists(f)) { notes.Add("Materials.mtl not found"); return res; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("Materials", out var arr))
                foreach (var m in arr.EnumerateArray())
                {
                    string name = m.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "" : "";
                    string tpl = m.TryGetProperty("Template", out var tp) && tp.TryGetProperty("Material", out var tm) ? tm.GetString() ?? "" : "";
                    string? alb = null, emm = null;
                    if (m.TryGetProperty("Textures", out var tx))
                        foreach (var t in tx.EnumerateArray())
                            if (t.TryGetProperty("Slot", out var sl) && t.TryGetProperty("File", out var fl))
                            {
                                if (sl.GetInt32() == 0) alb = fl.GetString();
                                else if (sl.GetInt32() == 3) emm = fl.GetString();      // emissiva (luci)
                            }
                    alb ??= emm;
                    res.TryAdd(name, (tpl, alb));
                }
        }
        catch (Exception e) { notes.Add("Materials.mtl: " + e.Message); }
        return res;
    }

    /// <summary>
    /// Il modello in posa. dofValue(numero) = valore che BMS da' a quel DOF (0 = riposo); ogni nodo lo passa da Effective con i suoi moltiplicatori e limiti; switchOn(numero, ramo,
    /// accesoAllInizio) = se il ramo si disegna. slots riceve la matrice di ogni slot nel sistema del modello.
    /// </summary>
    public BmlModel Pose(Func<int, double>? dofValue = null, Func<int, int, bool, bool>? switchOn = null, List<(int Slot, double[] M)>? slots = null,
                         List<(int Node, string Material, int FirstIndex, int IndexCount)>? parts = null, bool lights = false,
                         List<LightPoint>? lightPoints = null)
    {
        int nodeNo = -1;
        var m = new BmlModel();
        var b = _b;
        var stack = new Stack<(double[] M, int Hidden, int InLight, (int, int)? Sw)>();
        var M = Identity(); int hidden = 0, inLight = 0; (int, int)? lightSw = null;
        var byMat = new Dictionary<long, (BmlModel.Mesh Mesh, List<float> P, List<float> N, List<float> T, List<int> I)>();
        foreach (var n in _nodes)
        {
            nodeNo++;
            switch (n.Type)
            {
                case 2:
                    stack.Push((M, hidden, inLight, lightSw));
                    M = Mul(M, DofLocal(n.Dof!, Effective(n.Dof!, dofValue?.Invoke(n.Dof!.Number) ?? 0)));
                    break;
                case 4:
                    stack.Push((M, hidden, inLight, lightSw));
                    bool def = n.Switch!.StartsOn || DefaultOn.Contains((n.Switch.Number, n.Switch.Branch));
                    if (!(switchOn?.Invoke(n.Switch.Number, n.Switch.Branch, def) ?? def)) hidden++;
                    if (LightSwitches.Contains(n.Switch.Number)) { inLight++; lightSw = (n.Switch.Number, n.Switch.Branch); }
                    break;
                case 6:
                    stack.Push((M, hidden, inLight, lightSw));
                    if (hidden == 0 && slots != null) slots.Add((n.Slot!.Number, Mul(M, Local(IsRotation(n.Slot.R) ? n.Slot.R : IdentityR, n.Slot.Origin))));
                    break;
                case 3: case 5: case 7:
                    if (stack.Count > 0) (M, hidden, inLight, lightSw) = stack.Pop();
                    break;
                case 1:
                {
                    if (hidden > 0 || n.VertexCount <= 0) break;
                    var mat = n.Material >= 0 && n.Material < _mats.Count ? _mats[n.Material] : (null, false, false, "");
                    // luci: dentro uno switch di luci, materiali emissivi/postbruciatore (PBR), geometria colorata senza texture (vecchi)
                    bool glow = (Version == 1 && n.Material < 0 && n.VertexSize != 36) || mat.Skip || n.VertexSize == 44;
                    bool isLight = glow || inLight > 0;
                    if (glow && !lights) break;
                    uint color = 0xFFFFFFFF;
                    if (isLight && (n.VertexSize == 40 || n.VertexSize == 44)) color = BitConverter.ToUInt32(b, _vbAt + n.VertexOffset + 24);
                    int uvAt = Version == 1 ? 28 : n.VertexSize >= 48 ? 36 : 28;
                    bool hasNormal = n.VertexSize >= 24;
                    long key = isLight && lights ? (1L << 62) | ((long)(n.Material + 1) << 32) | color : n.Material;
                    if (!byMat.TryGetValue(key, out var acc))
                        byMat[key] = acc = (new BmlModel.Mesh { Texture = glow && n.VertexSize != 48 ? null : mat.Tex, Glass = mat.Glass && !isLight, Material = mat.Name, Light = isLight && lights, Color = color },
                                            new List<float>(), new List<float>(), new List<float>(), new List<int>());
                    int baseV = acc.P.Count / 3;
                    for (int v = 0; v < n.VertexCount; v++)
                    {
                        int p = _vbAt + n.VertexOffset + v * n.VertexSize;
                        if (p + n.VertexSize > _vbAt + _vbSize) break;
                        double x = BitConverter.ToSingle(b, p), y = BitConverter.ToSingle(b, p + 4), z = BitConverter.ToSingle(b, p + 8);
                        acc.P.Add((float)(M[0] * x + M[1] * y + M[2] * z + M[3]));
                        acc.P.Add((float)(M[4] * x + M[5] * y + M[6] * z + M[7]));
                        acc.P.Add((float)(M[8] * x + M[9] * y + M[10] * z + M[11]));
                        if (hasNormal)
                        {
                            double nx = BitConverter.ToSingle(b, p + 12), ny = BitConverter.ToSingle(b, p + 16), nz = BitConverter.ToSingle(b, p + 20);
                            acc.N.Add((float)(M[0] * nx + M[1] * ny + M[2] * nz)); acc.N.Add((float)(M[4] * nx + M[5] * ny + M[6] * nz)); acc.N.Add((float)(M[8] * nx + M[9] * ny + M[10] * nz));
                        }
                        else { acc.N.Add(0); acc.N.Add(1); acc.N.Add(0); }
                        if (uvAt + 8 <= n.VertexSize) { acc.T.Add(BitConverter.ToSingle(b, p + uvAt)); acc.T.Add(BitConverter.ToSingle(b, p + uvAt + 4)); }
                        else { acc.T.Add(0); acc.T.Add(0); }
                    }
                    int added = acc.P.Count / 3 - baseV;
                    int firstIdx = acc.I.Count;
                    if (isLight && lightPoints != null && added > 0)
                    {
                        double cx = 0, cy = 0, cz = 0;
                        for (int q = baseV; q < baseV + added; q++) { cx += acc.P[q * 3]; cy += acc.P[q * 3 + 1]; cz += acc.P[q * 3 + 2]; }
                        lightPoints.Add(new LightPoint(nodeNo, lightSw?.Item1 ?? -1, lightSw?.Item2 ?? -1, cx / added, cy / added, cz / added, color, glow, mat.Name));
                    }
                    if (n.IndexCount > 0 && _ibSize > 0)
                    {
                        int isz = _ibFormat == 2 ? 4 : 2;
                        int vbase = n.VertexSize > 0 ? n.VertexOffset / n.VertexSize : 0;
                        int ib0 = BmlModel.IndexBase(k => Idx(isz, k), n.StartIndex, n.IndexCount, added, vbase, n.VertexStartIndex);
                        for (int i = 0; i + 2 < n.IndexCount; i += 3)
                        {
                            int a = Idx(isz, n.StartIndex + i) - ib0, c = Idx(isz, n.StartIndex + i + 1) - ib0, d = Idx(isz, n.StartIndex + i + 2) - ib0;
                            if ((uint)a >= added || (uint)c >= added || (uint)d >= added) continue;
                            acc.I.Add(baseV + a); acc.I.Add(baseV + c); acc.I.Add(baseV + d);
                        }
                    }
                    else
                        for (int i = 0; i + 2 < added; i += 3) { acc.I.Add(baseV + i); acc.I.Add(baseV + i + 1); acc.I.Add(baseV + i + 2); }
                    if (parts != null) { parts.Add((nodeNo, acc.Mesh.Material, firstIdx, acc.I.Count - firstIdx)); m.Parts.Add((nodeNo, acc.Mesh, firstIdx, acc.I.Count - firstIdx)); }
                    break;
                }
            }
        }
        foreach (var (mesh, p, nn, t, idx) in byMat.Values)
        {
            if (idx.Count == 0) continue;
            mesh.Positions = p.ToArray(); mesh.Normals = nn.ToArray(); mesh.Uvs = t.ToArray(); mesh.Indices = idx.ToArray();
            m.Meshes.Add(mesh);
        }
        return m;
    }

    /// <summary>Una luce del modello in posa: switch che la contiene (-1 se nessuno), centro, colore ABGR (modelli vecchi).</summary>
    public sealed record LightPoint(int Node, int Switch, int Branch, double X, double Y, double Z, uint Color, bool Glow, string Material)
    {
        public (byte R, byte G, byte B) Rgb => ((byte)(Color & 0xFF), (byte)((Color >> 8) & 0xFF), (byte)((Color >> 16) & 0xFF));
    }

    /// <summary>Debug: per ogni primitiva (nodo, dimensione del vertice, materiale grezzo, primi byte del primo vertice).</summary>
    public IEnumerable<(int Node, int VertexSize, int Material, int Count, byte[] First)> RawPrimitives()
    {
        for (int k = 0; k < _nodes.Count; k++)
        {
            var n = _nodes[k];
            if (n.Type != 1) continue;
            int p = _vbAt + n.VertexOffset;
            var first = p + n.VertexSize <= _b.Length ? _b[p..(p + n.VertexSize)] : Array.Empty<byte>();
            yield return (k, n.VertexSize, n.Material, n.VertexCount, first);
        }
    }

    /// <summary>Per ogni primitiva: materiale, DOF e switch che la contengono, vertici e ingombro a riposo (per i controlli).</summary>
    public List<(int Node, string Material, string Path, int Vertices, float[] Min, float[] Max)> PrimitiveReport()
    {
        var res = new List<(int, string, string, int, float[], float[])>();
        var stack = new Stack<(double[] M, string P)>();
        var M = Identity(); string path = "";
        for (int k = 0; k < _nodes.Count; k++)
        {
            var n = _nodes[k];
            switch (n.Type)
            {
                case 2: stack.Push((M, path)); M = Mul(M, DofLocal(n.Dof!, Effective(n.Dof!, 0))); path += $" D{n.Dof!.Number}"; break;
                case 4: stack.Push((M, path)); path += $" S{n.Switch!.Number}/{n.Switch.Branch}{(n.Switch.StartsOn ? "+" : "")}"; break;
                case 6: stack.Push((M, path)); path += $" L{n.Slot!.Number}"; break;
                case 3: case 5: case 7: if (stack.Count > 0) (M, path) = stack.Pop(); break;
                case 1:
                {
                    var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
                    for (int v = 0; v < n.VertexCount; v++)
                    {
                        int p = _vbAt + n.VertexOffset + v * n.VertexSize;
                        if (p + 12 > _vbAt + _vbSize) break;
                        double x = BitConverter.ToSingle(_b, p), y = BitConverter.ToSingle(_b, p + 4), z = BitConverter.ToSingle(_b, p + 8);
                        var q = new[] { M[0] * x + M[1] * y + M[2] * z + M[3], M[4] * x + M[5] * y + M[6] * z + M[7], M[8] * x + M[9] * y + M[10] * z + M[11] };
                        for (int c = 0; c < 3; c++) { mn[c] = Math.Min(mn[c], (float)q[c]); mx[c] = Math.Max(mx[c], (float)q[c]); }
                    }
                    string mat = n.Material >= 0 && n.Material < _mats.Count ? _mats[n.Material].Name : n.Material.ToString(CultureInfo.InvariantCulture);
                    res.Add((k, mat, path.Trim(), n.VertexCount, mn, mx));
                    break;
                }
            }
        }
        return res;
    }

    private int Idx(int size, int i)
    {
        int p = _ibAt + i * size;
        if (p + size > _b.Length) return -1;
        return size == 2 ? BitConverter.ToUInt16(_b, p) : BitConverter.ToInt32(_b, p);
    }

    /// <summary>
    /// Valore effettivo di un nodo DOF per il valore che BMS da' a quel numero di DOF (regola di Falcon, verificata sulle
    /// catene di aste dei carrelli): v = valore (cambiato di segno se "inverso"), portato tra min e max se "limiti";
    /// se "normalizza" diventa la posizione tra min e max (0..1) e il moltiplicatore e' in GRADI (ogni pezzo di una catena
    /// ruota di "moltiplicatore" gradi mentre il valore attraversa il suo tratto min..max); altrimenti v × moltiplicatore.
    /// Piu' nodi con lo stesso numero si muovono quindi in modo diverso.
    /// </summary>
    public static double Effective(DofInfo d, double input)
    {
        double v = d.Reverse ? -input : input;
        double lo = Math.Min(d.Min, d.Max), hi = Math.Max(d.Min, d.Max);
        if (d.Limits) v = Math.Clamp(v, lo, hi);
        if (d.Normalise)
        {
            v = hi == lo ? 0 : Math.Clamp((v - lo) / (hi - lo), 0, 1);
            return d.Kind == DofKind.Rotate ? v * d.Multiplier * Math.PI / 180 : v * d.Multiplier;
        }
        return v * d.Multiplier;
    }

    /// <summary>
    /// Switch (numero, ramo) che contengono almeno un nodo DOF con quei numeri: per esempio quelli che contengono il carrello
    /// (secondo il modello sono 1-3, 17-19 o 20-22) e che BMS accende quando il carrello e' fuori.
    /// </summary>
    public HashSet<(int, int)> SwitchesAround(IEnumerable<int> dofNumbers)
    {
        var want = dofNumbers.ToHashSet();
        var res = new HashSet<(int, int)>();
        var open = new Stack<(int, int)?>();
        foreach (var n in _nodes)
        {
            switch (n.Type)
            {
                case 4: open.Push((n.Switch!.Number, n.Switch.Branch)); break;
                case 2: case 6: open.Push(null); break;
                case 3: case 5: case 7: if (open.Count > 0) open.Pop(); break;
            }
            if (n.Type == 2 && want.Contains(n.Dof!.Number))
                foreach (var sw in open) if (sw is { } k) res.Add(k);
        }
        return res;
    }

    /// <summary>
    /// Switch delle luci (7 Tail Strobe, 8 Nav Lights, 9 Land Lights, 29 Pit Tail Strobe, 47 Nav Lights Flash, 62 Formation
    /// lights): quello che c'e' dentro e' una luce. Si possono aggiungere numeri (es. dai nomi di switch.xml).
    /// </summary>
    public static HashSet<int> LightSwitches { get; } = new() { 7, 8, 9, 29, 47, 62 };

    /// <summary>
    /// Aggiunge agli switch delle luci quelli esterni (numero sotto 100) il cui nome in switch.xml parla di luci; da 100 in su
    /// sono gli switch dell'abitacolo (spie, illuminazione strumenti), non luci dell'aereo.
    /// </summary>
    public static void AddLightSwitches(BmsNames names)
    {
        foreach (var ((num, _), name) in names.Switches)
            if (num < 100 && name.Contains("light", StringComparison.OrdinalIgnoreCase) || name.Contains("strobe", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("beacon", StringComparison.OrdinalIgnoreCase) || name.Contains("lamp", StringComparison.OrdinalIgnoreCase))
                lock (LightSwitches) LightSwitches.Add(num);
    }

    /// <summary>DOF del carrello: 19-21 gambe, 22-24 portelli.</summary>
    public static readonly int[] GearDofNumbers = { 19, 20, 21, 22, 23, 24 };

    /// <summary>Switch da accendere con il carrello fuori: quelli che contengono le gambe (19-21) e i soliti 1-3 e 20-22.</summary>
    public HashSet<(int, int)> GearSwitches()
    {
        var sw = SwitchesAround(new[] { 19, 20, 21 });
        foreach (var s in Switches.Where(s => s.Number is 1 or 2 or 3 or 20 or 21 or 22)) sw.Add((s.Number, s.Branch));
        return sw;
    }

    /// <summary>
    /// Valori dei DOF del carrello "tutto fuori": per ogni numero il limite piu' alto dei suoi pezzi; per un numero senza
    /// limiti di rotazione il limite piu' alto delle rotazioni del carrello (tra 0,5 e 2 radianti), altrimenti 90 gradi.
    /// </summary>
    public Dictionary<int, double> GearDownValues()
    {
        var nodes = DofNodes.Where(d => GearDofNumbers.Contains(d.Number)).ToList();
        double common = nodes.Where(d => d.Kind == DofKind.Rotate && d.Limits && d.Max != d.Min).Select(d => Math.Max(d.Min, d.Max)).DefaultIfEmpty(Math.PI / 2).Max();
        common = Math.Clamp(common, 0.5, 2.0);
        var res = new Dictionary<int, double>();
        foreach (var g in nodes.GroupBy(d => d.Number))
        {
            // le rotazioni decidono (una traslazione puo' avere limiti in piedi, es. 0..426): ognuna poi si ferma al suo limite
            var lim = g.Where(d => d.Limits && d.Max != d.Min && d.Kind == DofKind.Rotate).ToList();
            if (lim.Count == 0 && g.Any(d => d.Kind == DofKind.Rotate)) { res[g.Key] = common; continue; }
            if (lim.Count == 0) lim = g.Where(d => d.Limits && d.Max != d.Min).ToList();
            res[g.Key] = lim.Count > 0 ? lim.Max(d => Math.Max(d.Min * (d.Reverse ? -1 : 1), d.Max * (d.Reverse ? -1 : 1))) : common;
        }
        return res;
    }

    /// <summary>Tutti i nodi DOF (anche quelli con lo stesso numero).</summary>
    public IReadOnlyList<DofInfo> DofNodes => _nodes.Where(n => n.Dof != null).Select(n => n.Dof!).ToList();

    /// <summary>Intervallo sensato per un cursore sul valore che BMS da' a quel numero di DOF (unione dei nodi).</summary>
    public (double Min, double Max) InputRange(int number)
    {
        double lo = 0, hi = 0; bool any = false;
        foreach (var d in _nodes.Where(n => n.Dof?.Number == number).Select(n => n.Dof!))
        {
            double sg = d.Reverse ? -1 : 1, a, b;
            if (d.Limits && d.Min != d.Max) { a = d.Min * sg; b = d.Max * sg; }
            else (a, b) = d.Kind switch { DofKind.Rotate => (-Math.PI, Math.PI), DofKind.Translate => (-1.0, 1.0), _ => (0.0, 1.0) };
            if (!any) { lo = Math.Min(a, b); hi = Math.Max(a, b); any = true; }
            else { lo = Math.Min(lo, Math.Min(a, b)); hi = Math.Max(hi, Math.Max(a, b)); }
        }
        lo = Math.Min(lo, 0); hi = Math.Max(hi, 0);
        if (hi - lo < 1e-9) hi = lo + 1;
        return (lo, hi);
    }

    /// <summary>Matrice locale del DOF (3x4, riga per riga, p' = M p) per il valore dato.</summary>
    private static double[] DofLocal(DofInfo d, double input)
    {
        double v = input;
        var t = Finite(d.T) ? d.T : ZeroT;
        switch (d.Kind)
        {
            case DofKind.Translate:
                // a riposo nessuno spostamento: il vettore e' la direzione/ampiezza della corsa
                return new double[] { 1, 0, 0, v * t[0], 0, 1, 0, v * t[1], 0, 0, 1, v * t[2] };
            case DofKind.Scale:
            {
                var sc = Finite(d.Scale) ? d.Scale : new float[] { 1, 1, 1 };
                double sx = 1 + v * (sc[0] - 1), sy = 1 + v * (sc[1] - 1), sz = 1 + v * (sc[2] - 1);
                return new double[] { sx, 0, 0, t[0], 0, sy, 0, t[1], 0, 0, sz, t[2] };
            }
            default:
            {
                var frame = Local(d.ValidMatrix ? d.R : IdentityR, t);
                if (v == 0) return frame;
                // attorno a Z locale. Verso: l'esportatore ruota di +v attorno a Y di Blender e passa a BMS scambiando Y e Z
                // (una riflessione), quindi qui l'angolo cambia segno (verificato: carrelli di F-16 e A-6E escono verso il basso)
                double c = Math.Cos(-v), s = Math.Sin(-v);
                var rz = new double[] { c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0 };
                return Mul(frame, rz);
            }
        }
    }

    private static readonly float[] IdentityR = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    private static readonly float[] ZeroT = { 0, 0, 0 };
    private static bool Finite(float v) => float.IsFinite(v) && Math.Abs(v) < 1e6f;
    private static bool Finite(float[] v) => v.All(Finite);

    public static bool IsRotation(float[] r)
    {
        if (!Finite(r)) return false;
        for (int i = 0; i < 3; i++)
        {
            double len = r[i * 3] * r[i * 3] + r[i * 3 + 1] * r[i * 3 + 1] + r[i * 3 + 2] * r[i * 3 + 2];
            if (Math.Abs(len - 1) > 0.05) return false;
            for (int j = i + 1; j < 3; j++)
                if (Math.Abs(r[i * 3] * r[j * 3] + r[i * 3 + 1] * r[j * 3 + 1] + r[i * 3 + 2] * r[j * 3 + 2]) > 0.05) return false;
        }
        return true;
    }

    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };

    /// <summary>p' = Rᵀ p + T (BMS: vettori riga, p * R + T).</summary>
    private static double[] Local(float[] r, float[] t) => new double[] { r[0], r[3], r[6], t[0], r[1], r[4], r[7], t[1], r[2], r[5], r[8], t[2] };

    public static double[] Mul(double[] a, double[] b)
    {
        var c = new double[12];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) c[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j];
            c[i * 4 + 3] = a[i * 4] * b[3] + a[i * 4 + 1] * b[7] + a[i * 4 + 2] * b[11] + a[i * 4 + 3];
        }
        return c;
    }
}

/// <summary>Nomi dei DOF e degli switch (DOF.xml e switch.xml dell'esportatore ufficiale di BMS).</summary>
public sealed class BmsNames
{
    public Dictionary<int, string> Dofs { get; } = new();
    public Dictionary<(int, int), string> Switches { get; } = new();

    public static BmsNames Load(string dir)
    {
        var n = new BmsNames();
        try
        {
            string d = Path.Combine(dir, "DOF.xml");
            if (File.Exists(d))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(d), @"<DOFNum>(\d+)</DOFNum>\s*<Name>(.*?)</Name>", System.Text.RegularExpressions.RegexOptions.Singleline))
                    n.Dofs.TryAdd(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim()));
            string s = Path.Combine(dir, "switch.xml");
            if (File.Exists(s))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(s), @"<SwitchNum>(\d+)</SwitchNum>\s*<BranchNum>(\d+)</BranchNum>\s*<Name>(.*?)</Name>", System.Text.RegularExpressions.RegexOptions.Singleline))
                    n.Switches.TryAdd((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)), System.Net.WebUtility.HtmlDecode(m.Groups[3].Value.Trim()));
        }
        catch { }
        return n;
    }

    public string Dof(int num) => Dofs.TryGetValue(num, out var s) ? s : $"DOF {num}";
    public string Switch(int num, int br) => Switches.TryGetValue((num, br), out var s) ? s : Switches.TryGetValue((num, 0), out var s0) ? $"{s0} (branch {br})" : $"Switch {num}/{br}";
}
