using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelixToolkit;
using HelixToolkit.Geometry;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using TerrainTool.Core.Models;
using HxCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HxMesh = HelixToolkit.SharpDX.MeshGeometry3D;
using Point3D = System.Windows.Media.Media3D.Point3D;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using Vector3D = System.Windows.Media.Media3D.Vector3D;
using Color = System.Windows.Media.Color;

namespace ModelEditor;

/// <summary>
/// Vista 3D con DirectX 11 (HelixToolkit.SharpDX): materiali PBR come in BMS, cioe' colore (Albedo), ombre di contatto,
/// rugosita' e metallo (ARMW, gli stessi canali dell'ORM del glTF), normal map, parti luminose; ombre proiettate dal sole,
/// antialiasing. Stesso modello in posa della vista classica (BmlScene.Pose), stessa camera (la muove ModelViewer).
/// BMS/Direct3D e' a mano sinistra: Z cambia segno (e l'ordine dei vertici dei triangoli, per non girarli al rovescio).
/// </summary>
public sealed class HelixView : Grid
{
    private readonly Viewport3DX _vp;
    private readonly HxCamera _cam = new() { FieldOfView = 40, NearPlaneDistance = 0.1, FarPlaneDistance = 5000 };
    private readonly GroupModel3D _scene = new();
    private readonly DirectionalLight3D _sun = new() { Color = Color.FromRgb(255, 250, 238), Direction = new Vector3D(-0.45, -1, -0.35) };
    private readonly DirectionalLight3D _fill = new() { Color = Color.FromRgb(70, 76, 88), Direction = new Vector3D(0.6, -0.25, 0.75) };
    private readonly AmbientLight3D _ambient = new() { Color = Color.FromRgb(46, 48, 52) };
    private readonly ShadowMap3D _shadow = new() { Resolution = new Size(4096, 4096), Intensity = 0.55, AutoCoverCompleteScene = true };
    private int _showNo;

    /// <summary>Texture gia' caricate (stessa texture per piu' materiali e per ogni nuova posa).</summary>
    private static readonly ConcurrentDictionary<string, TextureModel?> Textures = new(StringComparer.OrdinalIgnoreCase);
    public static void ForgetTextures() => Textures.Clear();

    /// <summary>Usa le normal map (si possono spegnere per confrontare).</summary>
    public bool NormalMaps { get; set; } = true;
    public bool Shadows { get; set; } = true;

    private HelixView()
    {
        _vp = new Viewport3DX
        {
            EffectsManager = new DefaultEffectsManager(),
            Camera = _cam,
            BackgroundColor = Color.FromRgb(150, 182, 214),
            MSAA = MSAALevel.Four,
            FXAALevel = FXAALevel.None,
            IsShadowMappingEnabled = true,
            ShowCoordinateSystem = false,
            ShowViewCube = false,
            ShowFrameRate = false,
            IsRotationEnabled = false,
            IsPanEnabled = false,
            IsZoomEnabled = false,
            IsMoveEnabled = false,
            IsChangeFieldOfViewEnabled = false,
            IsInertiaEnabled = false,
            ZoomExtentsWhenLoaded = false,
            Focusable = false,
        };
        _vp.Items.Add(_ambient);
        _vp.Items.Add(_sun);
        _vp.Items.Add(_fill);
        _vp.Items.Add(_shadow);
        _vp.Items.Add(_scene);
        Children.Add(_vp);
    }

    /// <summary>La vista DirectX, o null con il motivo (scheda video senza DirectX 11, librerie mancanti…).</summary>
    public static HelixView? TryCreate(out string? error)
    {
        try { error = null; return new HelixView(); }
        catch (Exception e) { error = e.Message; return null; }
    }

    public void SetCamera(Point3D position, Vector3D look, Vector3D up, double near, double far)
    {
        _cam.Position = position;
        _cam.LookDirection = look;
        _cam.UpDirection = up;
        _cam.NearPlaneDistance = near;
        _cam.FarPlaneDistance = far;
    }

    // ───────────────────────── scena

    private sealed record Built(HxMesh Geometry, BmlModel.Mesh Source, bool Highlight);

    /// <summary>
    /// Mostra il modello in posa (e le armi montate). La geometria (con le tangenti per le normal map) si prepara in
    /// background, i materiali e gli oggetti sulla finestra.
    /// </summary>
    public async Task ShowAsync(IReadOnlyList<SceneBuilder.Part> parts, IReadOnlyDictionary<string, BmlScene.PbrMaps> pbr,
                                IReadOnlyList<(int Slot, double[] M)>? markers, double markerSize, bool ground,
                                IReadOnlyList<(double X, double Y, double Z, Color C)>? halos)
    {
        int no = ++_showNo;
        bool normals = NormalMaps;
        var built = await Task.Run(() =>
        {
            var list = new List<Built>();
            foreach (var part in parts)
                foreach (var m in part.Model.Meshes)
                    if (m.Indices.Length >= 3) list.Add(new Built(Geometry(m, part.Matrix, normals), m, part.Highlight));
            return list;
        });
        if (no != _showNo) return;

        _scene.Children.Clear();
        var materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        float minY = float.MaxValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var transparent = new List<MeshGeometryModel3D>();
        foreach (var b in built)
        {
            var m = b.Source;
            pbr.TryGetValue(m.Material, out var maps);
            string key = (m.Light ? "L|" : "") + (m.Glass ? "G|" : "") + (b.Highlight ? "W|" : "") + (maps != null ? m.Material : m.Texture ?? "#grey") + "|" + m.Color;
            if (!materials.TryGetValue(key, out var mat)) materials[key] = mat = MakeMaterial(m, maps, b.Highlight, normals);
            var model = new MeshGeometryModel3D
            {
                Geometry = b.Geometry, Material = mat,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                IsThrowingShadow = !m.Light && !m.Glass,
                IsTransparent = m.Glass || (maps?.Alpha ?? false),
            };
            if (model.IsTransparent) transparent.Add(model); else _scene.Children.Add(model);
            if (b.Geometry.Positions is { Count: > 0 } ps && !b.Highlight)
                foreach (var p in ps) { minY = Math.Min(minY, p.Y); minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
        }
        foreach (var t in transparent) _scene.Children.Add(t);   // trasparenze per ultime

        // segni degli slot (cubetti blu) e aloni delle luci, come nella vista classica
        if (markers != null)
        {
            var blue = new PhongMaterial { DiffuseColor = new HelixToolkit.Maths.Color4(0.16f, 0.43f, 0.92f, 1), EmissiveColor = new HelixToolkit.Maths.Color4(0.10f, 0.25f, 0.6f, 1) };
            foreach (var (_, M) in markers) _scene.Children.Add(new MeshGeometryModel3D { Geometry = Cube(new V3((float)M[3], (float)M[7], (float)-M[11]), (float)markerSize), Material = blue });
        }
        if (halos != null)
            foreach (var (x, y, z, c) in halos)
                _scene.Children.Add(new MeshGeometryModel3D
                {
                    Geometry = Cube(new V3((float)x, (float)y, (float)-z), (float)(markerSize * 0.6)),
                    Material = new PhongMaterial { DiffuseColor = new HelixToolkit.Maths.Color4(0, 0, 0, 1), EmissiveColor = new HelixToolkit.Maths.Color4(c.R / 255f, c.G / 255f, c.B / 255f, 1) },
                });

        // terreno: riceve l'ombra dell'aereo
        if (ground && minY < float.MaxValue)
        {
            float s = Math.Max(maxX - minX, maxZ - minZ) * 1.6f, cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2, gy = minY - 0.02f;
            var g = new HxMesh
            {
                Positions = new Vector3Collection { new V3(cx - s, gy, cz - s), new V3(cx + s, gy, cz - s), new V3(cx + s, gy, cz + s), new V3(cx - s, gy, cz + s) },
                Normals = new Vector3Collection { V3.UnitY, V3.UnitY, V3.UnitY, V3.UnitY },
                TextureCoordinates = new Vector2Collection { new V2(0, 0), new V2(1, 0), new V2(1, 1), new V2(0, 1) },
                Indices = new IntCollection { 0, 2, 1, 0, 3, 2 },
            };
            var gm = new PBRMaterial
            {
                AlbedoColor = new HelixToolkit.Maths.Color4(0.52f, 0.53f, 0.50f, 1), RoughnessFactor = 0.95, MetallicFactor = 0, ReflectanceFactor = 0.2,
                RenderShadowMap = Shadows,
            };
            _scene.Children.Insert(0, new MeshGeometryModel3D { Geometry = g, Material = gm, CullMode = SharpDX.Direct3D11.CullMode.None, IsThrowingShadow = false });
        }
        _vp.IsShadowMappingEnabled = Shadows;
    }

    private Material MakeMaterial(BmlModel.Mesh m, BmlScene.PbrMaps? maps, bool highlight, bool normals)
    {
        if (m.Light)
        {
            // luce: brilla del suo colore (texture emissiva PBR o colore dei modelli vecchi)
            var tex = Tex(maps?.Emissive ?? maps?.Albedo ?? m.Texture);
            var col = tex != null ? new HelixToolkit.Maths.Color4(1, 1, 1, 1) : new HelixToolkit.Maths.Color4((m.Color & 0xFF) / 255f, ((m.Color >> 8) & 0xFF) / 255f, ((m.Color >> 16) & 0xFF) / 255f, 1);
            return new PBRMaterial { AlbedoColor = new HelixToolkit.Maths.Color4(0, 0, 0, 1), EmissiveColor = col, EmissiveMap = tex, RoughnessFactor = 1, MetallicFactor = 0 };
        }
        if (m.Glass)
            return new PBRMaterial
            {
                AlbedoColor = new HelixToolkit.Maths.Color4(0.55f, 0.62f, 0.68f, 0.28f), RoughnessFactor = 0.08, MetallicFactor = 0, ReflectanceFactor = 0.9,
                AlbedoMap = null, RenderShadowMap = false,
            };
        if (maps != null)
        {
            var mat = new PBRMaterial
            {
                AlbedoColor = new HelixToolkit.Maths.Color4(1, 1, 1, 1),
                AlbedoMap = Tex(maps.Albedo),
                // ARMW = R ombre di contatto, G rugosita', B metallo: come lo legge HelixToolkit (fattori = moltiplicatori)
                RoughnessMetallicMap = Tex(maps.Armw),
                AmbientOcculsionMap = Tex(maps.Armw),
                RoughnessFactor = maps.Armw != null ? 1 : 0.7,
                MetallicFactor = maps.Armw != null ? 1 : 0,
                AmbientOcclusionFactor = 1,
                ReflectanceFactor = 0.5,
                // normal map BC5 (solo X e Y): con le tangenti calcolate HelixToolkit usa solo X e Y
                NormalMap = normals ? Tex(maps.Normal) : null,
                EnableAutoTangent = false,
                RenderShadowMap = Shadows,
            };
            if (maps.Emissive != null && !maps.EmissiveOnly)
            {
                mat.EmissiveMap = Tex(maps.Emissive);
                float k = (float)Math.Clamp(maps.EmissionIntensity, 0, 3) * 0.5f;
                mat.EmissiveColor = new HelixToolkit.Maths.Color4(k, k, k, 1);
            }
            return mat;
        }
        // modelli vecchi: solo il colore
        return new PBRMaterial
        {
            AlbedoColor = highlight && m.Texture == null ? new HelixToolkit.Maths.Color4(0.59f, 0.63f, 0.69f, 1) : m.Texture == null ? new HelixToolkit.Maths.Color4(0.65f, 0.66f, 0.67f, 1) : new HelixToolkit.Maths.Color4(1, 1, 1, 1),
            AlbedoMap = Tex(m.Texture), RoughnessFactor = 0.75, MetallicFactor = 0.05, ReflectanceFactor = 0.4, RenderShadowMap = Shadows,
        };
    }

    /// <summary>
    /// Texture DDS (o altro formato) letta una volta sola. I formati sRGB diventano "normali": la vista disegna senza
    /// correzione di gamma, cosi' i colori escono come nella texture (senza, l'albedo verrebbe troppo scuro).
    /// </summary>
    private static TextureModel? Tex(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Textures.GetOrAdd(path, p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                var bytes = File.ReadAllBytes(p);
                if (bytes.Length > 148 && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[84] == 'D' && bytes[85] == 'X' && bytes[86] == '1' && bytes[87] == '0')
                {
                    int fmt = BitConverter.ToInt32(bytes, 128);
                    int plain = fmt switch { 29 => 28, 72 => 71, 75 => 74, 78 => 77, 91 => 87, 93 => 88, 99 => 98, _ => fmt };
                    if (plain != fmt) BitConverter.GetBytes(plain).CopyTo(bytes, 128);
                }
                return new TextureModel(new MemoryStream(bytes, writable: false));
            }
            catch { return null; }
        });
    }

    private static HxMesh Geometry(BmlModel.Mesh m, double[]? M, bool tangents)
    {
        int n = m.Positions.Length / 3;
        var pos = new Vector3Collection(n);
        var nor = new Vector3Collection(n);
        var uv = new Vector2Collection(n);
        for (int i = 0; i < n; i++)
        {
            double x = m.Positions[i * 3], y = m.Positions[i * 3 + 1], z = m.Positions[i * 3 + 2];
            if (M != null) (x, y, z) = (M[0] * x + M[1] * y + M[2] * z + M[3], M[4] * x + M[5] * y + M[6] * z + M[7], M[8] * x + M[9] * y + M[10] * z + M[11]);
            pos.Add(new V3((float)x, (float)y, (float)-z));
            if (i * 3 + 2 < m.Normals.Length)
            {
                double nx = m.Normals[i * 3], ny = m.Normals[i * 3 + 1], nz = m.Normals[i * 3 + 2];
                if (M != null) (nx, ny, nz) = (M[0] * nx + M[1] * ny + M[2] * nz, M[4] * nx + M[5] * ny + M[6] * nz, M[8] * nx + M[9] * ny + M[10] * nz);
                var v = new V3((float)nx, (float)ny, (float)-nz);
                nor.Add(v.LengthSquared() > 1e-12f ? V3.Normalize(v) : V3.UnitY);
            }
            else nor.Add(V3.UnitY);
            uv.Add(i * 2 + 1 < m.Uvs.Length ? new V2(m.Uvs[i * 2], m.Uvs[i * 2 + 1]) : V2.Zero);
        }
        var idx = new IntCollection(m.Indices.Length);
        for (int i = 0; i + 2 < m.Indices.Length; i += 3) { idx.Add(m.Indices[i]); idx.Add(m.Indices[i + 2]); idx.Add(m.Indices[i + 1]); }
        var g = new HxMesh { Positions = pos, Normals = nor, TextureCoordinates = uv, Indices = idx };
        if (tangents && n > 0)
        {
            try
            {
                MeshBuilder.ComputeTangents(pos, nor, uv, idx, out var t, out var bt);
                g.Tangents = new Vector3Collection(t);
                g.BiTangents = new Vector3Collection(bt);
            }
            catch { /* senza tangenti: la normal map non si usa */ }
        }
        return g;
    }

    private static HxMesh Cube(V3 c, float s)
    {
        var b = new MeshBuilder();
        b.AddBox(c, s * 2, s * 2, s * 2);
        return b.ToMesh().ToMeshGeometry3D();
    }
}
