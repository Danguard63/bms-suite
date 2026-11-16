using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TerrainTool.Core.Models;

namespace ModelEditor;

/// <summary>Dai modelli BML (gia' in posa) agli oggetti 3D di WPF. BMS/Direct3D e' a mano sinistra: Z cambia segno.</summary>
public static class SceneBuilder
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> TexCache = new(StringComparer.OrdinalIgnoreCase);

    public static void ForgetTextures() => TexCache.Clear();

    public sealed record Part(BmlModel Model, double[]? Matrix = null, bool Highlight = false);

    /// <summary>Costruisce la scena. bounds = ingombro del primo pezzo (il modello), per la camera.</summary>
    public static (Model3DGroup Group, Rect3D Bounds) Build(IReadOnlyList<Part> parts, IReadOnlyList<(int Slot, double[] M)>? markers, double markerSize, bool ground,
                                                            IReadOnlyList<(double X, double Y, double Z, Color C)>? halos = null)
    {
        var group = new Model3DGroup();
        var bounds = Rect3D.Empty;
        var glass = new List<GeometryModel3D>();
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            foreach (var m in part.Model.Meshes)
            {
                var mesh = Mesh(m, part.Matrix);
                if (pi == 0) bounds.Union(mesh.Bounds);
                if (m.Light)
                {
                    // luce: colore del file (modelli vecchi) o texture emissiva (PBR), che brilla anche al buio
                    Brush lb;
                    var lt = m.Texture != null ? Texture(m.Texture, opaque: true) : null;   // l'alfa delle texture emissive spesso e' 0
                    if (lt != null) lb = new ImageBrush(lt) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile };
                    else lb = new SolidColorBrush(Color.FromRgb((byte)(m.Color & 0xFF), (byte)((m.Color >> 8) & 0xFF), (byte)((m.Color >> 16) & 0xFF)));
                    lb.Freeze();
                    var lm = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(20, 20, 20))), new EmissiveMaterial(lb) } };
                    lm.Freeze();
                    var lg = new GeometryModel3D(mesh, lm) { BackMaterial = lm }; lg.Freeze();
                    glass.Add(lg);
                    continue;
                }
                if (m.Glass)
                {
                    var mg = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(90, 120, 150, 180))), new SpecularMaterial(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 40) } };
                    mg.Freeze();
                    var g = new GeometryModel3D(mesh, mg) { BackMaterial = mg }; g.Freeze();
                    glass.Add(g);
                    continue;
                }
                Brush brush;
                var bmp = m.Texture != null ? Texture(m.Texture) : null;
                if (bmp != null) brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile };
                else brush = new SolidColorBrush(part.Highlight ? Color.FromRgb(150, 160, 175) : Color.FromRgb(165, 168, 172));
                brush.Freeze();
                var mat = new DiffuseMaterial(brush); mat.Freeze();
                var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat }; gm.Freeze();
                group.Children.Add(gm);
            }
        }
        foreach (var g in glass) group.Children.Add(g);   // la trasparenza va disegnata per ultima

        if (markers != null && markers.Count > 0)
        {
            var mb = new SolidColorBrush(Color.FromRgb(40, 110, 235)); mb.Freeze();
            var mm = new EmissiveMaterial(mb); var md = new DiffuseMaterial(mb);
            var mg = new MaterialGroup { Children = { md, mm } }; mg.Freeze();
            foreach (var (_, M) in markers)
            {
                var c = new Point3D(M[3], M[7], -M[11]);
                var cube = Cube(c, markerSize);
                var g = new GeometryModel3D(cube, mg) { BackMaterial = mg }; g.Freeze();
                group.Children.Add(g);
            }
        }

        // aloni delle luci: le lenti sono piccole (pochi centimetri), un cubetto luminoso del loro colore le rende visibili
        if (halos != null)
            foreach (var (x, y, z, c) in halos)
            {
                var hb = new SolidColorBrush(c); hb.Freeze();
                var hm = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(10, 10, 10))), new EmissiveMaterial(hb) } }; hm.Freeze();
                var g = new GeometryModel3D(Cube(new Point3D(x, y, -z), markerSize * 0.6), hm) { BackMaterial = hm }; g.Freeze();
                group.Children.Add(g);
            }

        if (ground && !bounds.IsEmpty)
        {
            double s = Math.Max(bounds.SizeX, bounds.SizeZ) * 1.2, cx = bounds.X + bounds.SizeX / 2, cz = bounds.Z + bounds.SizeZ / 2, y = bounds.Y - 0.05;
            var g = new MeshGeometry3D
            {
                Positions = new Point3DCollection { new(cx - s, y, cz - s), new(cx + s, y, cz - s), new(cx + s, y, cz + s), new(cx - s, y, cz + s) },
                TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
            };
            g.Freeze();
            var gb = new RadialGradientBrush(Color.FromArgb(150, 120, 125, 110), Color.FromArgb(0, 120, 125, 110)); gb.Freeze();
            var gmat = new DiffuseMaterial(gb); gmat.Freeze();
            var gr = new GeometryModel3D(g, gmat); gr.Freeze();
            group.Children.Insert(0, gr);
        }
        group.Freeze();
        return (group, bounds.IsEmpty ? new Rect3D(0, 0, 0, 1, 1, 1) : bounds);
    }

    private static MeshGeometry3D Mesh(BmlModel.Mesh m, double[]? M)
    {
        var mesh = new MeshGeometry3D();
        var pos = new Point3DCollection(m.Positions.Length / 3);
        var nor = new Vector3DCollection(m.Normals.Length / 3);
        var uv = new PointCollection(m.Uvs.Length / 2);
        for (int i = 0; i + 2 < m.Positions.Length; i += 3)
        {
            double x = m.Positions[i], y = m.Positions[i + 1], z = m.Positions[i + 2];
            if (M != null) (x, y, z) = (M[0] * x + M[1] * y + M[2] * z + M[3], M[4] * x + M[5] * y + M[6] * z + M[7], M[8] * x + M[9] * y + M[10] * z + M[11]);
            pos.Add(new Point3D(x, y, -z));
        }
        for (int i = 0; i + 2 < m.Normals.Length; i += 3)
        {
            double x = m.Normals[i], y = m.Normals[i + 1], z = m.Normals[i + 2];
            if (M != null) (x, y, z) = (M[0] * x + M[1] * y + M[2] * z, M[4] * x + M[5] * y + M[6] * z, M[8] * x + M[9] * y + M[10] * z);
            nor.Add(new Vector3D(x, y, -z));
        }
        for (int i = 0; i + 1 < m.Uvs.Length; i += 2) uv.Add(new Point(m.Uvs[i], m.Uvs[i + 1]));
        mesh.Positions = pos; mesh.Normals = nor; mesh.TextureCoordinates = uv;
        mesh.TriangleIndices = new Int32Collection(m.Indices);
        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D Cube(Point3D c, double s)
    {
        var g = new MeshGeometry3D();
        for (int i = 0; i < 8; i++) g.Positions.Add(new Point3D(c.X + ((i & 1) != 0 ? s : -s), c.Y + ((i & 2) != 0 ? s : -s), c.Z + ((i & 4) != 0 ? s : -s)));
        int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
        foreach (int i in f) g.TriangleIndices.Add(i);
        g.Freeze();
        return g;
    }

    public static BitmapSource? Texture(string path, bool opaque = false) => TexCache.GetOrAdd(opaque ? path + "|opaque" : path, _ =>
    {
        try
        {
            var im = DdsImage.Load(path, 1024);
            if (im == null) return null;
            var bmp = BitmapSource.Create(im.Width, im.Height, 96, 96, im.HasAlpha && !opaque ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, im.Bgra, im.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    });
}
