using System.Globalization;
using System.Text.Json;

namespace TerrainTool.Core.Models;

/// <summary>
/// Modello 3D di BMS 4.3x (cartella Terrdata\objects\Models\&lt;parent&gt;): Parent.dat (LOD, set di texture), Model_N.bml,
/// per i modelli PBR Materials.mtl e Model_N.mti. Formato BML come lo scrive l'esportatore ufficiale di BenchmarkSims
/// (bms-blender-plugin): intestazione "BML\0", versione, compressione (0 nessuna, 1 LZ4, 2 LZMA), dimensioni; poi
/// script, materiali (v1: numeri delle texture di KoreaObj, v2: nomi), nodi (primitive, DOF, switch, slot), indici, vertici.
/// I vertici sono in piedi con Y in alto, X a destra, Z in avanti (Direct3D, mano sinistra).
/// Solo lettura: serve per vedere i mezzi delle unita' nell'editor della campagna.
/// </summary>
public sealed class BmlModel
{
    public sealed class Mesh
    {
        public float[] Positions = Array.Empty<float>();  // x,y,z (Y in alto, mano sinistra come in BMS/D3D)
        public float[] Normals = Array.Empty<float>();
        public float[] Uvs = Array.Empty<float>();
        public int[] Indices = Array.Empty<int>();
        public string? Texture;                            // file DDS (percorso completo) o null
        public bool Glass;                                 // vetro: semitrasparente
        public string Material = "";
        public bool Light;                                 // luce (si disegna emissiva); Color = colore ABGR dei modelli vecchi
        public uint Color = 0xFFFFFFFF;
    }

    public sealed record ParentInfo(string Folder, int TextureSets, List<(string File, double Distance)> Lods, double Radius);

    public List<Mesh> Meshes { get; } = new();
    /// <summary>Primitive e dove sono finite (mesh, primo indice, numero di indici); riempito da BmlScene.Pose se richiesto.</summary>
    public List<(int Node, Mesh Mesh, int FirstIndex, int IndexCount)> Parts { get; } = new();
    public int Version { get; private set; }
    public int Triangles => Meshes.Sum(m => m.Indices.Length / 3);
    public List<string> Notes { get; } = new();

    // ───────────────────────── Parent.dat

    public static ParentInfo? ReadParent(string folder)
    {
        string pd = Path.Combine(folder, "Parent.dat");
        if (!File.Exists(pd)) return null;
        int sets = 1; double radius = 0;
        var lods = new List<(string, double)>();
        foreach (var raw in File.ReadAllLines(pd))
        {
            int eq = raw.IndexOf('=');
            if (eq < 0) continue;
            string key = raw[..eq].Trim(), val = raw[(eq + 1)..].Trim();
            var parts = val.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (key.Equals("TextureSets", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out sets);
            else if (key.Equals("Dimensions", StringComparison.OrdinalIgnoreCase) && parts.Length > 0)
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
            else if (key.Equals("AddLOD", StringComparison.OrdinalIgnoreCase) && parts.Length > 0)
            {
                double d = 0;
                if (parts.Length > 1) double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                lods.Add((parts[0], d));
            }
        }
        return new ParentInfo(folder, Math.Max(1, sets), lods, radius);
    }

    /// <summary>LOD da mostrare: il piu' dettagliato che non sia enorme (i modelli PBR hanno LOD 0 da 10 MB compressi).</summary>
    public static int DefaultLod(ParentInfo p)
    {
        for (int i = 0; i < p.Lods.Count; i++)
        {
            var f = Path.Combine(p.Folder, p.Lods[i].File);
            if (File.Exists(f) && new FileInfo(f).Length <= 4_000_000) return i;
        }
        return Math.Max(0, p.Lods.Count - 1);
    }

    // ───────────────────────── lettura BML

    public static byte[] ReadPayload(string file, out int version)
    {
        var b = File.ReadAllBytes(file);
        if (b.Length < 28 || b[0] != 'B' || b[1] != 'M' || b[2] != 'L') throw new InvalidDataException("not a BML file");
        version = BitConverter.ToInt32(b, 4);
        int comp = BitConverter.ToInt32(b, 8);
        long size = BitConverter.ToInt64(b, 12), csize = BitConverter.ToInt64(b, 20);
        if (size <= 0 || size > 1_000_000_000 || 28 + csize > b.Length) throw new InvalidDataException("bad BML header");
        return comp switch
        {
            0 => b.AsSpan(28, (int)size).ToArray(),
            1 => Decompress.Lz4Frame(b, 28, (int)csize, (int)size),
            2 => Decompress.Lzma(b, 28, (int)csize, (int)size),
            _ => throw new InvalidDataException($"unknown BML compression {comp}"),
        };
    }

    private sealed class Mtl { public string Name = ""; public string? Albedo; public string Template = ""; }

    /// <summary>
    /// Carica un LOD. modelsRoot = cartella Models (per i percorsi "1909/xxx.dds" dei materiali PBR),
    /// textureDirs = cartelle dei numeri DDS dei modelli vecchi (KoreaObj_HiRes, KoreaObj).
    /// </summary>
    public static BmlModel Load(ParentInfo parent, int lod, int textureSet, string modelsRoot, IReadOnlyList<string> textureDirs)
    {
        var m = new BmlModel();
        if (parent.Lods.Count == 0) throw new InvalidDataException("Parent.dat lists no LOD");
        lod = Math.Clamp(lod, 0, parent.Lods.Count - 1);
        string file = Path.Combine(parent.Folder, parent.Lods[lod].File);
        var b = ReadPayload(file, out int ver);
        m.Version = ver;
        int o = 0;
        int U() { int v = BitConverter.ToInt32(b, o); o += 4; return v; }
        float F() { float v = BitConverter.ToSingle(b, o); o += 4; return v; }

        U();                                   // script
        int mc = U();
        var texIds = new List<int>(); var names = new List<string>();
        for (int i = 0; i < mc; i++)
        {
            if (ver == 1) texIds.Add(U());
            else { int n = U(); names.Add(System.Text.Encoding.ASCII.GetString(b, o, n)); o += n; }
        }
        int ibFormat = U(); U(); U(); int nodeCount = U();

        // nodi
        var nodes = new List<Node>(nodeCount);
        for (int k = 0; k < nodeCount; k++)
        {
            int type = U(); U(); int nodeVer = U();
            switch (type)
            {
                case 1:
                {
                    var p = new Node { Type = 1 };
                    U(); F();                                         // topology, z bias
                    p.IndexCount = U(); p.StartIndex = U(); p.VertexStartIndex = U(); p.VertexOffset = U(); p.VertexCount = U(); p.VertexSize = U();
                    o += 12;                                          // punto di riferimento
                    if (ver == 1)
                    {
                        o += 2; p.VertexType = U(); U(); U(); p.Material = U(); U();
                    }
                    else { o += 2; p.Material = BitConverter.ToUInt16(b, o); o += 2; }
                    nodes.Add(p);
                    break;
                }
                case 2:
                {
                    var d = new Node { Type = 2 };
                    U(); d.DofType = U(); F(); F(); F(); U();       // numero, tipo, min, max, moltiplicatore, flag
                    o += 12;                                          // scala
                    d.T = new[] { F(), F(), F() };
                    d.R = new float[9]; for (int i = 0; i < 9; i++) d.R[i] = F();
                    nodes.Add(d);
                    break;
                }
                case 4:
                {
                    var s = new Node { Type = 4 };
                    s.SwitchNum = U(); s.Branch = U(); s.Enabled = b[o] != 0; o += 1;
                    nodes.Add(s);
                    break;
                }
                case 6: o += 4 + 36 + 12; nodes.Add(new Node { Type = 6 }); break;
                case 3: case 5: case 7: nodes.Add(new Node { Type = type }); break;
                case 8: o += 36; nodes.Add(new Node { Type = 8 }); break;     // render control: tipo + operazione (36 byte), non serve per vedere il modello
                default: throw new InvalidDataException($"unknown BML node type {type} (version {nodeVer})");
            }
        }
        int ibSize = U(); int ibAt = o; o += ibSize;
        // switch che BMS accende da solo (numero dell'aereo sulla deriva): come in volo, vedi BmlScene.SimDefaults
        var simOn = BmlScene.SimDefaults(nodes.Where(n => n.Type == 4).Select(n => new BmlScene.SwitchInfo(n.SwitchNum, n.Branch, n.Enabled)));
        foreach (var n in nodes.Where(n => n.Type == 4)) n.Enabled = n.Enabled || simOn.Contains((n.SwitchNum, n.Branch));
        // esportatore Blender: tutte le primitive con start index 0 e indici uno dopo l'altro nel buffer
        var prims = nodes.Where(n => n.Type == 1).ToList();
        if (prims.Count > 1 && prims.All(p => p.StartIndex == 0))
        {
            int cur = 0;
            foreach (var p in prims) { p.StartIndex = cur; cur += p.IndexCount; }
        }
        int vbSize = U(); int vbAt = o;
        if (vbAt + vbSize > b.Length) throw new InvalidDataException("BML vertex buffer out of range");

        // materiali e texture
        var setNames = names;
        string? Tex(int matIndex, out bool glass, out bool skip, out string matName)
        {
            glass = false; skip = false; matName = "";
            if (ver == 1)
            {
                if (matIndex < 0) return null;
                int per = parent.TextureSets > 1 && texIds.Count % parent.TextureSets == 0 ? texIds.Count / parent.TextureSets : texIds.Count;
                int set = per == texIds.Count ? 0 : Math.Clamp(textureSet, 0, parent.TextureSets - 1);
                int idx = set * per + matIndex;
                if (idx >= texIds.Count) idx = matIndex;
                if (idx < 0 || idx >= texIds.Count) return null;
                matName = texIds[idx].ToString(CultureInfo.InvariantCulture);
                foreach (var d in textureDirs)
                {
                    var f = Path.Combine(d, texIds[idx] + ".dds");
                    if (File.Exists(f)) return f;
                }
                return null;
            }
            if (matIndex < 0 || matIndex >= setNames.Count) return null;
            matName = setNames[matIndex];
            if (!m._mtl.TryGetValue(matName, out var mt)) return null;
            string t = mt.Template;
            if (t.Contains("Emissive", StringComparison.OrdinalIgnoreCase) || t.Contains("AfterBurner", StringComparison.OrdinalIgnoreCase)) skip = true;
            if (t.Contains("Glass", StringComparison.OrdinalIgnoreCase)) glass = true;
            if (mt.Albedo == null) return null;
            string rel = mt.Albedo.Replace('/', Path.DirectorySeparatorChar);
            var f1 = Path.Combine(parent.Folder, rel);
            if (File.Exists(f1)) return f1;
            var f2 = Path.Combine(modelsRoot, rel);
            return File.Exists(f2) ? f2 : null;
        }
        if (ver != 1)
        {
            m.LoadMaterials(parent.Folder);
            string mti = Path.Combine(parent.Folder, Path.ChangeExtension(parent.Lods[lod].File, ".mti"));
            if (textureSet > 0 && File.Exists(mti))
            {
                var lines = File.ReadAllLines(mti).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (names.Count > 0 && lines.Count >= (textureSet + 1) * names.Count) setNames = lines.Skip(textureSet * names.Count).Take(names.Count).ToList();
            }
        }

        // geometria: per ogni primitiva visibile, i vertici portati nello spazio del modello attraverso i DOF
        var stack = new Stack<(double[] M, int Hidden)>();
        var M = Identity(); int hidden = 0;
        var byMat = new Dictionary<string, (Mesh Mesh, List<float> P, List<float> N, List<float> T, List<int> I)>();
        foreach (var n in nodes)
        {
            switch (n.Type)
            {
                case 2:
                    stack.Push((M, hidden));
                    // solo i DOF di rotazione (tipo 0) hanno una matrice valida: negli altri (traslazione, scala…) quel campo
                    // contiene altro (NaN, valori enormi). A riposo (DOF a zero) il pezzo resta dove lo mette T.
                    M = Mul(M, Local(n.DofType == 0 && IsRotation(n.R!) ? n.R! : IdentityR, Finite(n.T!) ? n.T! : ZeroT));
                    break;
                case 4:
                    stack.Push((M, hidden));
                    if (!n.Enabled) hidden++;
                    break;
                case 6:
                    stack.Push((M, hidden));
                    break;
                case 3: case 5: case 7:
                    if (stack.Count > 0) (M, hidden) = stack.Pop();
                    break;
                case 1:
                {
                    if (hidden > 0 || n.VertexCount <= 0) break;
                    if (ver == 1 && n.Material < 0 && n.VertexSize != 36) break;     // luci e bagliori
                    string? tex = Tex(n.Material, out bool glass, out bool skip, out string matName);
                    if (skip) break;
                    int uvAt = ver == 1 ? 28 : n.VertexSize >= 48 ? 36 : 28;
                    bool hasNormal = n.VertexSize >= 24;
                    string key = (tex ?? "-") + "|" + glass + "|" + matName;
                    if (!byMat.TryGetValue(key, out var acc))
                    {
                        acc = (new Mesh { Texture = tex, Glass = glass, Material = matName }, new List<float>(), new List<float>(), new List<float>(), new List<int>());
                        byMat[key] = acc;
                    }
                    int baseV = acc.P.Count / 3;
                    for (int v = 0; v < n.VertexCount; v++)
                    {
                        int p = vbAt + n.VertexOffset + v * n.VertexSize;
                        if (p + n.VertexSize > vbAt + vbSize) break;
                        double x = BitConverter.ToSingle(b, p), y = BitConverter.ToSingle(b, p + 4), z = BitConverter.ToSingle(b, p + 8);
                        acc.P.Add((float)(M[0] * x + M[1] * y + M[2] * z + M[3]));
                        acc.P.Add((float)(M[4] * x + M[5] * y + M[6] * z + M[7]));
                        acc.P.Add((float)(M[8] * x + M[9] * y + M[10] * z + M[11]));
                        if (hasNormal)
                        {
                            double nx = BitConverter.ToSingle(b, p + 12), ny = BitConverter.ToSingle(b, p + 16), nz = BitConverter.ToSingle(b, p + 20);
                            acc.N.Add((float)(M[0] * nx + M[1] * ny + M[2] * nz));
                            acc.N.Add((float)(M[4] * nx + M[5] * ny + M[6] * nz));
                            acc.N.Add((float)(M[8] * nx + M[9] * ny + M[10] * nz));
                        }
                        else { acc.N.Add(0); acc.N.Add(1); acc.N.Add(0); }
                        if (uvAt + 8 <= n.VertexSize) { acc.T.Add(BitConverter.ToSingle(b, p + uvAt)); acc.T.Add(BitConverter.ToSingle(b, p + uvAt + 4)); }
                        else { acc.T.Add(0); acc.T.Add(0); }
                    }
                    int added = acc.P.Count / 3 - baseV;
                    if (n.IndexCount > 0 && ibSize > 0)
                    {
                        int isz = ibFormat == 2 ? 4 : 2;
                        int vbase = n.VertexSize > 0 ? n.VertexOffset / n.VertexSize : 0;
                        int ib0 = IndexBase(k => Idx(b, ibAt, isz, k), n.StartIndex, n.IndexCount, added, vbase, n.VertexStartIndex);
                        for (int i = 0; i + 2 < n.IndexCount; i += 3)
                        {
                            int a = Idx(b, ibAt, isz, n.StartIndex + i) - ib0, c = Idx(b, ibAt, isz, n.StartIndex + i + 1) - ib0,
                                d = Idx(b, ibAt, isz, n.StartIndex + i + 2) - ib0;
                            if ((uint)a >= added || (uint)c >= added || (uint)d >= added) continue;
                            acc.I.Add(baseV + a); acc.I.Add(baseV + c); acc.I.Add(baseV + d);
                        }
                    }
                    else
                        for (int i = 0; i + 2 < added; i += 3) { acc.I.Add(baseV + i); acc.I.Add(baseV + i + 1); acc.I.Add(baseV + i + 2); }
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

    /// <summary>
    /// Quanto togliere agli indici di una primitiva per avere indici locali (0..count-1), deciso sulla primitiva intera e non
    /// indice per indice. I modelli esportati da Blender scrivono indici globali (contati dall'inizio del vertex buffer,
    /// vertexBase = primo vertice della primitiva); i modelli vecchi indici gia' locali; alcuni contano da VertexStartIndex.
    /// Prima si decideva per ogni indice: in una primitiva globale con base piccola (es. 1812 su 5490 vertici) gli indici
    /// sotto il numero di vertici venivano presi per locali e il pezzo si deformava (ruota sinistra dell'F-16CM-52).
    /// </summary>
    internal static int IndexBase(Func<int, int> idxAt, int start, int indexCount, int count, int vertexBase, int vertexStart)
    {
        int mn = int.MaxValue, mx = int.MinValue;
        for (int i = 0; i < indexCount; i++) { int v = idxAt(start + i); if (v < 0) continue; if (v < mn) mn = v; if (v > mx) mx = v; }
        if (mn == int.MaxValue) return 0;
        bool Fits(int b0) => mn >= b0 && mx - b0 < count;
        if (vertexBase > 0 && Fits(vertexBase)) return vertexBase;
        if (Fits(0)) return 0;
        if (vertexStart > 0 && Fits(vertexStart)) return vertexStart;
        return vertexBase > 0 && mn >= vertexBase ? vertexBase : 0;
    }

    private static int Idx(byte[] b, int at, int size, int i)
    {
        int p = at + i * size;
        if (p + size > b.Length) return -1;
        return size == 2 ? BitConverter.ToUInt16(b, p) : BitConverter.ToInt32(b, p);
    }

    private sealed class Node
    {
        public int Type, IndexCount, StartIndex, VertexStartIndex, VertexOffset, VertexCount, VertexSize, VertexType, Material, DofType;
        public bool Enabled;
        public int SwitchNum, Branch;
        public float[]? T, R;
    }

    private readonly Dictionary<string, Mtl> _mtl = new(StringComparer.OrdinalIgnoreCase);

    private void LoadMaterials(string folder)
    {
        string f = Path.Combine(folder, "Materials.mtl");
        if (!File.Exists(f)) { Notes.Add("Materials.mtl not found"); return; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("Materials", out var arr)) return;
            foreach (var mat in arr.EnumerateArray())
            {
                var mt = new Mtl { Name = mat.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "" : "" };
                if (mat.TryGetProperty("Template", out var tp) && tp.TryGetProperty("Material", out var tm)) mt.Template = tm.GetString() ?? "";
                if (mat.TryGetProperty("Textures", out var tx))
                    foreach (var t in tx.EnumerateArray())
                        if (t.TryGetProperty("Slot", out var sl) && sl.GetInt32() == 0 && t.TryGetProperty("File", out var fl)) mt.Albedo = fl.GetString();
                _mtl.TryAdd(mt.Name, mt);
            }
        }
        catch (Exception e) { Notes.Add("Materials.mtl: " + e.Message); }
    }

    // ───────────────────────── matrici 3x4 (riga per riga): p' = M * p, DOF come in BMS (p * R + T)

    private static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
    private static readonly float[] IdentityR = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    private static readonly float[] ZeroT = { 0, 0, 0 };

    private static bool Finite(float[] v) => v.All(x => float.IsFinite(x) && Math.Abs(x) < 1e6f);

    /// <summary>Vero se r (3x3) e' una rotazione: righe di lunghezza 1 e perpendicolari tra loro (con tolleranza).</summary>
    private static bool IsRotation(float[] r)
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

    /// <summary>Matrice locale del DOF: p' = Rᵀ p + T (BMS usa vettori riga: p * R + T).</summary>
    private static double[] Local(float[] r, float[] t) => new double[]
    {
        r[0], r[3], r[6], t[0],
        r[1], r[4], r[7], t[1],
        r[2], r[5], r[8], t[2],
    };

    private static double[] Mul(double[] a, double[] b)
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
