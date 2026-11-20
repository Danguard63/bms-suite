using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Lettura (solo geometria) dei modelli .bml di BMS 4.38, per l'anteprima 3D dell'editor.
/// File: "BML\0", u32 versione, u32 compressione (0 nessuna, 2 LZMA), u64 dimensione, u64 dimensione compressa, dati.
/// Dati (versione 1, ricavata dai file e dall'esportatore BenchmarkSims, licenza MIT): u32 script, u32 N materiali,
/// N × u32, u32 formato indici (1 = 16 bit, 2 = 32 bit), u32 indici, u32 vertici, u32 nodi, i nodi
/// (u32 tipo, u32 indice, u32 versione + dati: primitiva 66 byte, interruttore 9, controllo 36, DOF 84, slot 52, fine 0),
/// u32 byte indici + indici, u32 byte vertici + vertici. Il vertice comincia con la posizione (3 float, piedi,
/// come in Direct3D: x destra, y su, z avanti; verificato con le dimensioni dei Parent.dat).
/// Delle parti dentro un interruttore si tengono solo quelle accese all'avvio (niente versioni distrutte).
/// </summary>
public sealed class BmlModel
{
    /// <summary>Triangoli: 9 float per triangolo (x, y, z dei tre vertici) nelle coordinate del modello.</summary>
    public float[] Triangles { get; private set; } = Array.Empty<float>();
    public int TriangleCount => Triangles.Length / 9;

    /// <summary>
    /// Parti del modello per texture: Tex = numero della texture (file Terrdata\Objects\KoreaObj\&lt;n&gt;.dds) o -1;
    /// Pos = 9 float per triangolo, Uv = 6 float per triangolo (vuoto se la parte non ha coordinate di texture).
    /// </summary>
    public List<(string? Tex, float[] Pos, float[] Uv)> Parts { get; } = new();

    /// <summary>Separates the colour texture from the emission texture in a part's Tex ("albedo|E|emission"): PBR parts that glow at night.</summary>
    public const string EmissionSep = "|E|";

    /// <summary>
    /// Light points of the model (classic models: billboard sprites, 6 vertices at the same place, vertex size 48 with the colour
    /// RGBA at +24 and the half size at +32): runway, approach, VASI, obstruction lights. All of them, also those in switches
    /// that are off at the start (flashing sequences). Model coordinates (x right, y up, z forward), size in feet.
    /// </summary>
    public List<(float X, float Y, float Z, byte R, byte G, byte B, float Size)> Lights { get; } = new();

    /// <summary>
    /// Pools of light on the ground (street lights, floodlights): flat untextured triangles (vertex size 40, colour RGBA at +24,
    /// strongest at the lamp and fading to 0 at the edge), in switches that are on at night. Colour with its strongest alpha;
    /// triangles (9 floats each, model coordinates) and for every vertex its fade 0 (full) .. 1 (none).
    /// </summary>
    public List<(byte R, byte G, byte B, byte A, float[] Pos, float[] Fade)> Pools { get; } = new();

    public static BmlModel Load(string path, string? koreaObjDir = null, string? fallbackKoreaObjDir = null)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 28 || b[0] != 'B' || b[1] != 'M' || b[2] != 'L') throw new InvalidDataException("not a BML file");
        int ver = BitConverter.ToInt32(b, 4), comp = BitConverter.ToInt32(b, 8);
        long size = BitConverter.ToInt64(b, 12);
        byte[] p = comp switch
        {
            0 => b[28..],
            2 => Lzma.Decode(b, 28, 33, size),
            _ => throw new InvalidDataException($"BML compression {comp} not supported"),
        };
        return Parse(p, ver, Path.GetDirectoryName(path)!, koreaObjDir, fallbackKoreaObjDir);
    }

    /// <summary>
    /// The BML version and, for version 1, the KoreaObj texture numbers of its materials (read only: the file is not changed).
    /// Version 2 names materials whose textures are in the model's own folder: no numbers.
    /// </summary>
    public static (int Version, List<int> Textures) TextureIds(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 28 || b[0] != 'B' || b[1] != 'M' || b[2] != 'L') throw new InvalidDataException("not a BML file");
        int ver = BitConverter.ToInt32(b, 4), comp = BitConverter.ToInt32(b, 8);
        long size = BitConverter.ToInt64(b, 12);
        byte[] p = comp switch { 0 => b[28..], 2 => Lzma.Decode(b, 28, 33, size), _ => throw new InvalidDataException($"BML compression {comp} not supported") };
        var ids = new List<int>();
        if (ver >= 2) return (ver, ids);
        int nm = BitConverter.ToInt32(p, 4);
        for (int i = 0; i < nm; i++) { int id = BitConverter.ToInt32(p, 8 + 4 * i); if (id >= 0 && !ids.Contains(id)) ids.Add(id); }
        return (ver, ids);
    }

    /// <summary>Materiale (nome) → file della texture di colore (slot 0) dal Materials.mtl della cartella del modello.</summary>
    private static Dictionary<string, string> Albedos(string modelDir)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string f = Path.Combine(modelDir, "Materials.mtl");
        if (!File.Exists(f)) return res;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
            foreach (var m in doc.RootElement.GetProperty("Materials").EnumerateArray())
            {
                string? name = m.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (name == null || !m.TryGetProperty("Textures", out var tx)) continue;
                string? alb = null, emi = null;
                foreach (var t in tx.EnumerateArray())
                    if (t.TryGetProperty("Slot", out var sl) && t.TryGetProperty("File", out var file) && file.GetString() is string fn)
                    {
                        if (sl.GetInt32() == 0) alb = Path.Combine(modelDir, fn);
                        else if (sl.GetInt32() == 3) emi = Path.Combine(modelDir, fn);
                    }
                if (alb != null) res[name] = emi != null && File.Exists(emi) ? alb + EmissionSep + emi : alb;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
        return res;
    }

    private static BmlModel Parse(byte[] p, int ver, string modelDir, string? koreaObjDir, string? fallbackKoreaObjDir = null)
    {
        int o = 0;
        int U() { int v = BitConverter.ToInt32(p, o); o += 4; return v; }
        U();                         // script
        int nm = U();
        // v1: numeri delle texture (KoreaObj\<n>.dds); v2: nomi dei materiali (Materials.mtl della cartella)
        var mats = new string?[nm];
        var albedo = ver >= 2 ? Albedos(modelDir) : null;
        for (int i = 0; i < nm; i++)
        {
            if (ver >= 2)
            {
                int len = U();
                string name = System.Text.Encoding.ASCII.GetString(p, o, len); o += len;
                mats[i] = albedo!.TryGetValue(name, out var a) ? a : null;
            }
            else
            {
                int id = U();
                string? f = koreaObjDir == null ? null : Path.Combine(koreaObjDir, id.ToString(CultureInfo.InvariantCulture) + ".dds");
                // a texture the theater does not have: the BMS one
                if (fallbackKoreaObjDir != null && (f == null || !File.Exists(f)))
                {
                    string g = Path.Combine(fallbackKoreaObjDir, id.ToString(CultureInfo.InvariantCulture) + ".dds");
                    if (File.Exists(g)) f = g;
                }
                mats[i] = f;
            }
        }
        int ibf = U(); U(); U(); int nodes = U();
        var prims = new List<(int Topo, int ICnt, int SI, int VSI, int VSO, int VC, int VS, string? Tex)>();
        var lightPrims = new List<(int VSO, int VC)>();
        var bulbPrims = new List<(int VSO, int VC, int VS)>();
        var poolPrims = new List<(int VSO, int VC)>();
        var sw = new Stack<bool>();
        bool on = true;
        for (int k = 0; k < nodes; k++)
        {
            int type = U(); U(); int nver = U();
            switch (type)
            {
                case 1:
                {
                    int topo = BitConverter.ToInt32(p, o);
                    int icnt = BitConverter.ToInt32(p, o + 8), si = BitConverter.ToInt32(p, o + 12), vsi = BitConverter.ToInt32(p, o + 16),
                        vso = BitConverter.ToInt32(p, o + 20), vc = BitConverter.ToInt32(p, o + 24), vs = BitConverter.ToInt32(p, o + 28);
                    // indice del materiale: nodo v2 (48 byte) u16 a +46; nodo v1 (66 byte) i32 a +58 (-1 = nessuno)
                    int ti = nver >= 2 ? BitConverter.ToUInt16(p, o + 46) : BitConverter.ToInt32(p, o + 58);
                    if (on) prims.Add((topo, icnt, si, vsi, vso, vc, vs, ti >= 0 && ti < nm ? mats[ti] : null));
                    if (ver < 2 && vs == 48 && icnt == 0) lightPrims.Add((vso, vc));   // sprite di luce (anche negli interruttori spenti)
                    else if (ver < 2 && topo == 1 && vs >= 28 && icnt == 0) bulbPrims.Add((vso, vc, vs));   // punto: la lampadina di un lampione
                    else if (ver < 2 && topo == 4 && vs == 40 && ti < 0 && icnt == 0 && vc >= 3) poolPrims.Add((vso, vc));   // chiazza di luce a terra?
                    o += nver >= 2 ? 48 : 66;
                    break;
                }
                case 2: o += 84; break;                          // DOF
                case 3: case 7: break;                           // fine DOF / fine slot
                case 4:                                          // interruttore: acceso all'avvio?
                    sw.Push(on);
                    on = on && p[o + 8] != 0;
                    o += 9;
                    break;
                case 5: on = sw.Count > 0 ? sw.Pop() : true; break;
                case 6: o += 52; break;                          // slot
                case 8: o += 36; break;                          // controllo di rendering
                default: throw new InvalidDataException($"BML node type {type} not supported");
            }
        }
        int ibBytes = U(); int ibAt = o; o += ibBytes;
        int vbBytes = U(); int vbAt = o;
        if (vbAt + vbBytes != p.Length) throw new InvalidDataException("BML layout not understood (node sizes)");
        var all = new List<float>();
        var parts = new Dictionary<string, (List<float> Pos, List<float> Uv)>();
        foreach (var (topo, icnt, si, vsi, vso, vc, vs, tex) in prims)
        {
            if (topo != 4 || vs < 12) continue;
            // vertice v1: posizione, normale, colore/i, poi u v (2 float) in fondo; v2 PBR (48 byte): posizione, normale,
            // tangente, u v a +36, verso della tangente
            bool hasUv = vs >= 36 && tex != null;
            int uvAt = ver >= 2 && vs >= 44 ? 36 : vs - 8;
            string key = hasUv ? tex! : "";
            if (!parts.TryGetValue(key, out var part)) parts[key] = part = (new List<float>(), new List<float>());
            void V(int i)
            {
                int at = vbAt + vso + i * vs;
                if (at + vs > vbAt + vbBytes) { for (int k = 0; k < 3; k++) { part.Pos.Add(0); all.Add(0); } if (hasUv) { part.Uv.Add(0); part.Uv.Add(0); } return; }
                float x = BitConverter.ToSingle(p, at), y = BitConverter.ToSingle(p, at + 4), z = BitConverter.ToSingle(p, at + 8);
                part.Pos.Add(x); part.Pos.Add(y); part.Pos.Add(z); all.Add(x); all.Add(y); all.Add(z);
                if (hasUv) { part.Uv.Add(BitConverter.ToSingle(p, at + uvAt)); part.Uv.Add(BitConverter.ToSingle(p, at + uvAt + 4)); }
            }
            if (icnt > 0)
                for (int i = 0; i + 2 < icnt; i += 3)
                    for (int j = 0; j < 3; j++)
                    {
                        int ix = ibf == 2 ? BitConverter.ToInt32(p, ibAt + (si + i + j) * 4) : BitConverter.ToUInt16(p, ibAt + (si + i + j) * 2);
                        V(ix + vsi);
                    }
            else
                for (int i = 0; i + 2 < vc; i += 3) { V(i); V(i + 1); V(i + 2); }
        }
        var m = new BmlModel { Triangles = all.ToArray() };
        foreach (var (vso, vc, vs) in bulbPrims)
            for (int i = 0; i < vc; i++)
            {
                int at = vbAt + vso + i * vs;
                if (at + 28 > vbAt + vbBytes) break;
                m.Lights.Add((BitConverter.ToSingle(p, at), BitConverter.ToSingle(p, at + 4), BitConverter.ToSingle(p, at + 8), p[at + 24], p[at + 25], p[at + 26], 1.5f));
            }
        foreach (var (vso, vc) in poolPrims)
        {
            // piatta, rivolta in su, colore con trasparenza che sfuma verso il bordo: una chiazza di luce
            if (vbAt + vso + vc * 40 > vbAt + vbBytes) continue;
            float y0 = BitConverter.ToSingle(p, vbAt + vso + 4);
            int best = -1; byte ba = 0; bool flat = true;
            for (int i = 0; i < vc && flat; i++)
            {
                int at = vbAt + vso + i * 40;
                if (Math.Abs(BitConverter.ToSingle(p, at + 4) - y0) > 1 || BitConverter.ToSingle(p, at + 16) < 0.9f) flat = false;
                if (p[at + 27] > ba) { ba = p[at + 27]; best = at; }
            }
            if (!flat || best < 0 || ba == 0) continue;
            int n3 = vc / 3 * 3;
            var pos = new float[n3 * 3]; var fade = new float[n3];
            for (int i = 0; i < n3; i++)
            {
                int at = vbAt + vso + i * 40;
                pos[i * 3] = BitConverter.ToSingle(p, at); pos[i * 3 + 1] = BitConverter.ToSingle(p, at + 4); pos[i * 3 + 2] = BitConverter.ToSingle(p, at + 8);
                fade[i] = 1 - p[at + 27] / (float)ba;
            }
            m.Pools.Add((p[best + 24], p[best + 25], p[best + 26], ba, pos, fade));
        }
        var seen = new HashSet<(float, float, float)>();   // stesso posto (VASI rossa/bianca, versioni attenuate): una luce sola
        foreach (var (vso, vc) in lightPrims)
        {
            for (int i = 0; i < vc; i++)
            {
                int at = vbAt + vso + i * 48;
                if (at + 48 > vbAt + vbBytes) break;
                float x = BitConverter.ToSingle(p, at), y = BitConverter.ToSingle(p, at + 4), z = BitConverter.ToSingle(p, at + 8);
                if (!seen.Add((MathF.Round(x * 2), MathF.Round(y * 2), MathF.Round(z * 2)))) continue;   // mezzo piede
                float sz = Math.Max(Math.Abs(BitConverter.ToSingle(p, at + 32)), Math.Abs(BitConverter.ToSingle(p, at + 36))) * 2;
                m.Lights.Add((x, y, z, p[at + 24], p[at + 25], p[at + 26], float.IsFinite(sz) && sz > 0 ? sz : 2));
            }
        }
        foreach (var (tex, (pos, uv)) in parts) m.Parts.Add((tex.Length > 0 ? tex : null, pos.ToArray(), uv.ToArray()));
        return m;
    }

    /// <summary>
    /// I livelli di dettaglio di un oggetto (cartella Models\&lt;n&gt;) nell'ordine in cui provarli: prima il secondo
    /// del Parent.dat (piu' leggero), poi il primo, poi gli altri; se il Parent.dat non li elenca, i .bml della cartella.
    /// </summary>
    public static List<string> LodOrder(string modelDir)
    {
        string pd = Path.Combine(modelDir, "Parent.dat");
        var lods = new List<string>();
        if (File.Exists(pd))
            foreach (var l in File.ReadLines(pd))
            {
                var m = Regex.Match(l, @"^\s*AddLOD\s*=\s*(\S+)");
                if (m.Success) lods.Add(m.Groups[1].Value);
            }
        if (Directory.Exists(modelDir))
            foreach (var f in Directory.EnumerateFiles(modelDir, "*.bml").Select(Path.GetFileName).OfType<string>().OrderBy(x => x))
                if (!lods.Contains(f, StringComparer.OrdinalIgnoreCase)) lods.Add(f);
        if (lods.Count > 1) { var second = lods[1]; lods.RemoveAt(1); lods.Insert(0, second); }
        return lods.Select(x => Path.Combine(modelDir, x)).Where(File.Exists).ToList();
    }
}
