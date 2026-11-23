using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Models;

namespace ObjectiveStudio;

/// <summary>
/// Vista 3D dei mezzi di un'unita' con le texture di BMS (modelli BML di Terrdata\objects\Models, sola lettura).
/// Trascina con il tasto sinistro per ruotare, con il destro per spostare, rotellina per lo zoom, doppio clic per ricentrare.
/// </summary>
public sealed class ModelViewWindow : Window
{
    public sealed record Vehicle(string Name, int Ct, string? ModelDir, IReadOnlyList<string>? TexDirs = null);

    /// <summary>Optional panel on the right for the chosen item (the piece inspector: FCD data, usage); textures used are listed under it.</summary>
    public Func<Vehicle, UIElement?>? SidePanel { get; init; }
    /// <summary>Item shown first (index in the list).</summary>
    public int StartItem { get; init; } = -1;
    private readonly StackPanel _side = new() { Margin = new Thickness(8) };
    private readonly ScrollViewer _sideScroll = new() { Width = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private UIElement? _sideItem;
    private readonly StackPanel _texPanel = new();

    private static readonly ConcurrentDictionary<string, BitmapSource?> TexCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Vehicle> _vehicles;
    private readonly ComboBox _vehicle = new() { MinWidth = 240, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _lod = new() { MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _set = new() { MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _info = new() { Margin = new Thickness(8, 4, 8, 4), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly Viewport3D _view = new();
    private readonly ModelVisual3D _content = new();
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 40, UpDirection = new Vector3D(0, 1, 0) };
    private BmlModel.ParentInfo? _parent;
    private bool _filling;
    private int _loadNo;

    // camera in orbita attorno al centro del modello
    private Point3D _target;
    private double _yaw = -2.3, _pitch = 0.32, _dist = 50, _radius = 20;
    private Point? _drag; private bool _panning;
    private Rect3D _bounds = Rect3D.Empty;

    /// <summary>Mezzi di un'unita' (per una brigata quelli dei suoi battaglioni).</summary>
    public static List<Vehicle> VehiclesOf(CampaignDoc d, CampaignDb db, CampUnit u)
    {
        var unitCts = new List<int>();
        if (u.Kind == UnitKind.Brigade)
            unitCts.AddRange(u.Elements.Select(e => d.Unit(e)).OfType<CampUnit>().Select(b => b.Type - 100));
        else unitCts.Add(u.Type - 100);
        return VehiclesOfTypes(db, unitCts);
    }

    public static List<Vehicle> VehiclesOfTypes(CampaignDb db, IEnumerable<int> unitCts)
    {
        var res = new List<Vehicle>();
        foreach (int ct in unitCts)
            foreach (int v in db.UnitVehicleCts(ct))
                if (res.All(x => x.Ct != v)) res.Add(new Vehicle(db.VehicleName(v), v, db.ModelDir(v)));
        return res;
    }

    /// <summary>Livrea da mostrare all'apertura (da 0).</summary>
    public int StartSet { get; set; }

    public static ModelViewWindow? Open(Window? owner, string title, List<Vehicle> vehicles, int startSet = 0)
    {
        if (vehicles.Count == 0)
        {
            MessageBox.Show(owner!, "This unit type has no vehicles in the database (brigades: their battalions have them).", "3D view");
            return null;
        }
        var w = new ModelViewWindow(title, vehicles) { Owner = owner, StartSet = startSet };
        w.Show();
        return w;
    }

    /// <summary>Pieces (buildings) of an objective: one item per model, with a data panel on the right.</summary>
    public static ModelViewWindow? OpenPieces(Window? owner, string title, List<Vehicle> pieces, int start, Func<Vehicle, UIElement?> side)
    {
        if (pieces.Count == 0) { MessageBox.Show(owner!, "This objective has no pieces.", "Pieces"); return null; }
        var w = new ModelViewWindow(title, pieces, "Piece") { Owner = owner, SidePanel = side, StartItem = start };
        w._sideScroll.Visibility = Visibility.Visible;
        w.Width = 1180;
        w.Show();
        return w;
    }

    private ModelViewWindow(string title, List<Vehicle> vehicles, string itemLabel = "Vehicle")
    {
        _vehicles = vehicles;
        Title = "3D view – " + title;
        Width = 900; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var bar = new WrapPanel { Margin = new Thickness(8, 8, 8, 2) };
        void L(string s) => bar.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        L(itemLabel); bar.Children.Add(_vehicle);
        L("Detail (LOD)"); bar.Children.Add(_lod);
        L("Textures"); bar.Children.Add(_set);
        bar.Children.Add(Btn3("Reset view", ResetView, "Or double-click on the model"));

        foreach (var v in vehicles)
            _vehicle.Items.Add(new ComboBoxItem { Content = v.Name + (v.ModelDir == null ? "  (no model)" : ""), Tag = v });
        _vehicle.SelectionChanged += (_, _) => { if (!_filling) VehicleChanged(); };
        _lod.SelectionChanged += (_, _) => { if (!_filling) Reload(); };
        _set.SelectionChanged += (_, _) => { if (!_filling) Reload(); };

        _view.Camera = _cam;
        _view.Children.Add(new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    new AmbientLight(Color.FromRgb(120, 120, 120)),
                    new DirectionalLight(Color.FromRgb(215, 210, 200), new Vector3D(-0.5, -1, -0.6)),
                    new DirectionalLight(Color.FromRgb(95, 100, 110), new Vector3D(0.6, -0.2, 0.8)),
                },
            },
        });
        _view.Children.Add(_content);

        var surface = new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(196, 214, 232), Color.FromRgb(236, 236, 230), 90),
            Child = _view, ClipToBounds = true,
        };
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ResetView(); return; }
            _drag = e.GetPosition(surface); _panning = false; surface.CaptureMouse();
        };
        surface.MouseRightButtonDown += (_, e) => { _drag = e.GetPosition(surface); _panning = true; surface.CaptureMouse(); };
        surface.MouseLeftButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.MouseRightButtonUp += (_, _) => { _drag = null; surface.ReleaseMouseCapture(); };
        surface.MouseMove += (_, e) =>
        {
            if (_drag is not { } p0) return;
            var p = e.GetPosition(surface);
            double dx = p.X - p0.X, dy = p.Y - p0.Y;
            _drag = p;
            if (_panning)
            {
                var look = _cam.LookDirection; look.Normalize();
                var right = Vector3D.CrossProduct(look, _cam.UpDirection); right.Normalize();
                var up = Vector3D.CrossProduct(right, look);
                double k = _dist * 0.0016;
                _target += -right * dx * k + up * dy * k;
            }
            else
            {
                _yaw -= dx * 0.01;
                _pitch = Math.IEEERemainder(_pitch + dy * 0.01, 2 * Math.PI);   // giro completo anche sopra e sotto
            }
            UpdateCamera();
        };
        surface.MouseWheel += (_, e) =>
        {
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.88 : 1.14), _radius * 0.3, _radius * 30);
            UpdateCamera();
        };

        var help = new TextBlock
        {
            Margin = new Thickness(8, 2, 8, 6), Foreground = Brushes.Gray, FontSize = 11,
            Text = "Left drag = rotate · right drag = move · wheel = zoom · double-click = reset. Models and textures are read from BMS (read only); lights and glows are not shown.",
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_info, Dock.Top); root.Children.Add(_info);
        DockPanel.SetDock(help, Dock.Bottom); root.Children.Add(help);
        _sideScroll.Content = _side;
        DockPanel.SetDock(_sideScroll, Dock.Right); root.Children.Add(_sideScroll);
        root.Children.Add(surface);
        Content = root;

        Loaded += (_, _) =>
        {
            int first = StartItem >= 0 && StartItem < _vehicles.Count ? StartItem : _vehicles.FindIndex(v => v.ModelDir != null);
            _vehicle.SelectedIndex = Math.Max(0, first);
        };
    }

    /// <summary>Textures used by the model, as big thumbnails at the top of the side panel; click = full size.</summary>
    private void ShowTextures(BmlModel model, IReadOnlyList<string> texDirs)
    {
        _texPanel.Children.Clear();
        var used = model.Meshes.Where(m => m.Texture != null).GroupBy(m => m.Texture!, StringComparer.OrdinalIgnoreCase).OrderBy(g => Path.GetFileName(g.Key)).ToList();
        _texPanel.Children.Add(new TextBlock { Text = $"Textures ({used.Count}) – click one to see it full size", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        if (used.Count == 0) _texPanel.Children.Add(new TextBlock { Text = "(no texture found)", Foreground = Brushes.Gray });
        var wrap = new WrapPanel();
        foreach (var g in used)
        {
            string dir = Path.GetDirectoryName(g.Key) ?? "";
            var bmp = Texture(g.Key);
            var cell = new StackPanel { Width = 164, Margin = new Thickness(0, 0, 6, 8) };
            var img = new Border
            {
                Width = 160, Height = 160, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(70, 70, 70)), Cursor = Cursors.Hand,
                Child = bmp != null ? new Image { Source = bmp, Stretch = Stretch.Uniform } : new TextBlock { Text = "not readable", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = g.Key,
            };
            string file = g.Key;
            img.MouseLeftButtonUp += (_, _) => ShowTextureBig(file);
            cell.Children.Add(img);
            cell.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(g.Key)}\n{Path.GetFileName(Path.GetDirectoryName(dir))}\\{Path.GetFileName(dir)} · {g.Count()} part(s)" + (bmp != null ? $" · {bmp.PixelWidth}×{bmp.PixelHeight}" : ""),
                FontSize = 11, TextWrapping = TextWrapping.Wrap, ToolTip = g.Key,
            });
            wrap.Children.Add(cell);
        }
        _texPanel.Children.Add(wrap);
        int none = model.Meshes.Count(m => m.Texture == null && !m.Glass);
        if (none > 0) _texPanel.Children.Add(new TextBlock { Text = $"{none} part(s) without texture (grey)", Foreground = Brushes.Gray, FontSize = 11 });
    }

    /// <summary>Texture a grandezza piena (rotella = zoom), sola lettura.</summary>
    private void ShowTextureBig(string file)
    {
        BitmapSource? bmp;
        try
        {
            var im = DdsImage.Load(file, 4096);
            bmp = im == null ? null : BitmapSource.Create(im.Width, im.Height, 96, 96, im.HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, im.Bgra, im.Width * 4);
        }
        catch (Exception) { bmp = null; }
        bmp ??= Texture(file);
        if (bmp == null) return;
        var img = new Image { Source = bmp, Stretch = Stretch.None };
        var scale = new ScaleTransform(1, 1);
        img.LayoutTransform = scale;
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var sv = new ScrollViewer { Content = img, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                    Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)) };
        var w = new Window
        {
            Title = $"{Path.GetFileName(file)} – {bmp.PixelWidth}×{bmp.PixelHeight}   (wheel = zoom)", Owner = this, Content = sv,
            Width = Math.Min(1100, bmp.PixelWidth + 40), Height = Math.Min(900, bmp.PixelHeight + 60), WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        // si apre adattata alla finestra se e' piu' grande
        double fit = Math.Min(1, Math.Min(1060.0 / bmp.PixelWidth, 840.0 / bmp.PixelHeight));
        scale.ScaleX = scale.ScaleY = fit;
        sv.PreviewMouseWheel += (_, e) =>
        {
            double k = Math.Clamp(scale.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.05, 8);
            scale.ScaleX = scale.ScaleY = k; e.Handled = true;
        };
        w.Show();
    }

    private static Button Btn3(string text, Action a, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>Modello mostrato (cartella), per aggiornare la vista dopo una sostituzione di texture.</summary>
    public string? ModelDir => Current?.ModelDir;

    /// <summary>Dimentica le texture lette (dopo una sostituzione) e ricarica il modello mantenendo la vista.</summary>
    public void RefreshTextures(string? file = null)
    {
        if (file != null) TexCache.TryRemove(file, out _); else TexCache.Clear();
        Reload();
    }

    /// <summary>Sceglie la livrea (set di texture, da 0).</summary>
    public void SelectSet(int set)
    {
        if (set >= 0 && set < _set.Items.Count) _set.SelectedIndex = set;
    }

    private Vehicle? Current => (_vehicle.SelectedItem as ComboBoxItem)?.Tag as Vehicle;

    private void VehicleChanged()
    {
        _content.Content = null;
        _parent = null;
        _filling = true;
        _lod.Items.Clear(); _set.Items.Clear();
        _filling = false;
        var v = Current;
        if (v == null) return;
        if (SidePanel != null)
        {
            _side.Children.Clear();
            _sideItem = SidePanel(v);
            _texPanel.Children.Clear();
            _side.Children.Add(_texPanel);          // texture in alto (si vedono subito), dati sotto
            if (_sideItem != null) _side.Children.Add(_sideItem);
        }
        if (v.ModelDir == null)
        {
            _info.Text = $"{v.Name}: no 3D model found (CT {v.Ct} has no graphics, or its Models folder is missing).";
            return;
        }
        try { _parent = BmlModel.ReadParent(v.ModelDir); }
        catch (Exception e) { _info.Text = "Parent.dat: " + e.Message; return; }
        if (_parent == null || _parent.Lods.Count == 0) { _info.Text = $"{v.Name}: Parent.dat has no LOD."; return; }

        _filling = true;
        for (int i = 0; i < _parent.Lods.Count; i++)
        {
            var f = Path.Combine(_parent.Folder, _parent.Lods[i].File);
            long len = File.Exists(f) ? new FileInfo(f).Length : 0;
            _lod.Items.Add(new ComboBoxItem { Content = $"{i} – {(i == 0 ? "most detailed" : i == _parent.Lods.Count - 1 ? "least detailed" : "")} {len / 1024.0 / 1024.0:0.0} MB".Replace("  ", " ") });
        }
        for (int i = 0; i < _parent.TextureSets; i++) _set.Items.Add(new ComboBoxItem { Content = i == 0 ? "0 (default)" : i.ToString(CultureInfo.InvariantCulture) });
        _lod.SelectedIndex = BmlModel.DefaultLod(_parent);
        _set.SelectedIndex = Math.Clamp(StartSet, 0, Math.Max(0, _set.Items.Count - 1));
        _filling = false;
        Reload(resetView: true);
    }

    private async void Reload() => await ReloadAsync(false);
    private async void Reload(bool resetView) => await ReloadAsync(resetView);

    private async Task ReloadAsync(bool resetView)
    {
        var v = Current; var parent = _parent;
        if (v?.ModelDir == null || parent == null) return;
        int lod = Math.Max(0, _lod.SelectedIndex), set = Math.Max(0, _set.SelectedIndex);
        int no = ++_loadNo;
        _info.Text = $"{v.Name}: loading…";
        Cursor = Cursors.Wait;
        try
        {
            var modelsRoot = Path.GetDirectoryName(v.ModelDir)!;
            var texDirs = v.TexDirs ?? CampaignDb.ModelTextureDirs(v.ModelDir);
            var (group, model, bounds) = await Task.Run(() => Build(parent, lod, set, modelsRoot, texDirs));
            if (no != _loadNo) return;
            _content.Content = group;
            _bounds = bounds;
            if (resetView || _radius <= 0)
            {
                _target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
                _radius = Math.Max(1, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
                ResetView();
            }
            int noTex = model.Meshes.Count(m => m.Texture == null && !m.Glass);
            if (SidePanel != null) ShowTextures(model, texDirs);
            _info.Text = $"{v.Name}  ·  model {Path.GetFileName(v.ModelDir)} ({(model.Version == 1 ? "classic textures" : "PBR")})  ·  LOD {lod}: {model.Triangles:N0} triangles  ·  " +
                         $"{bounds.SizeZ:0} × {bounds.SizeX:0} × {bounds.SizeY:0} ft (length × width × height)" +
                         (noTex > 0 ? $"  ·  {noTex} part(s) without texture (grey)" : "") +
                         (model.Notes.Count > 0 ? "  ·  " + string.Join("; ", model.Notes) : "");
        }
        catch (Exception e)
        {
            if (no == _loadNo) _info.Text = $"{v.Name}: the model could not be read – {e.Message}";
        }
        finally { if (no == _loadNo) Cursor = null; }
    }

    private static (Model3DGroup Group, BmlModel Model, Rect3D Bounds) Build(BmlModel.ParentInfo parent, int lod, int set, string modelsRoot, IReadOnlyList<string> texDirs)
    {
        var model = BmlModel.Load(parent, lod, set, modelsRoot, texDirs);
        var group = new Model3DGroup();
        var bounds = Rect3D.Empty;
        foreach (var m in model.Meshes)
        {
            var mesh = new MeshGeometry3D();
            var pos = new Point3DCollection(m.Positions.Length / 3);
            var nor = new Vector3DCollection(m.Normals.Length / 3);
            var uv = new PointCollection(m.Uvs.Length / 2);
            // BMS/Direct3D usa la mano sinistra (Z in avanti), WPF la destra: Z cambiato di segno (anche l'ordine dei vertici torna giusto)
            for (int i = 0; i + 2 < m.Positions.Length; i += 3) pos.Add(new Point3D(m.Positions[i], m.Positions[i + 1], -m.Positions[i + 2]));
            for (int i = 0; i + 2 < m.Normals.Length; i += 3) nor.Add(new Vector3D(m.Normals[i], m.Normals[i + 1], -m.Normals[i + 2]));
            for (int i = 0; i + 1 < m.Uvs.Length; i += 2) uv.Add(new Point(m.Uvs[i], m.Uvs[i + 1]));
            mesh.Positions = pos; mesh.Normals = nor; mesh.TextureCoordinates = uv;
            mesh.TriangleIndices = new Int32Collection(m.Indices);
            mesh.Freeze();
            bounds.Union(mesh.Bounds);

            Brush brush;
            var bmp = m.Texture != null ? Texture(m.Texture) : null;
            if (m.Glass) brush = new SolidColorBrush(Color.FromArgb(90, 150, 175, 200));
            else if (bmp != null) brush = new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile };
            else brush = new SolidColorBrush(Color.FromRgb(165, 168, 172));
            brush.Freeze();
            var mat = new DiffuseMaterial(brush); mat.Freeze();
            var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
            gm.Freeze();
            if (m.Glass) continue;   // il vetro va dopo, per la trasparenza
            group.Children.Add(gm);
        }
        // vetri per ultimi (WPF disegna la trasparenza nell'ordine)
        foreach (var m in model.Meshes.Where(x => x.Glass))
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(Enumerable.Range(0, m.Positions.Length / 3).Select(i => new Point3D(m.Positions[i * 3], m.Positions[i * 3 + 1], -m.Positions[i * 3 + 2]))),
                TriangleIndices = new Int32Collection(m.Indices),
            };
            var mat = new SpecularMaterial(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 40);
            var dif = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(90, 120, 150, 180)));
            var mg = new MaterialGroup { Children = { dif, mat } };
            mg.Freeze(); mesh.Freeze();
            var gm = new GeometryModel3D(mesh, mg) { BackMaterial = mg };
            gm.Freeze();
            group.Children.Add(gm);
        }
        if (!bounds.IsEmpty)
        {
            // terreno: un quadrato grigio sotto il mezzo, per capire dove sta il basso
            double s = Math.Max(bounds.SizeX, bounds.SizeZ) * 1.2, cx = bounds.X + bounds.SizeX / 2, cz = bounds.Z + bounds.SizeZ / 2, y = bounds.Y - 0.05;
            var g = new MeshGeometry3D
            {
                Positions = new Point3DCollection { new(cx - s, y, cz - s), new(cx + s, y, cz - s), new(cx + s, y, cz + s), new(cx - s, y, cz + s) },
                TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
            };
            g.Freeze();
            var gb = new RadialGradientBrush(Color.FromArgb(150, 120, 125, 110), Color.FromArgb(0, 120, 125, 110));
            gb.Freeze();
            var gmat = new DiffuseMaterial(gb); gmat.Freeze();
            var ground = new GeometryModel3D(g, gmat);   // solo la faccia di sopra: da sotto non copre il mezzo
            ground.Freeze();
            group.Children.Insert(0, ground);
        }
        group.Freeze();
        return (group, model, bounds.IsEmpty ? new Rect3D(0, 0, 0, 1, 1, 1) : bounds);
    }

    private static BitmapSource? Texture(string path) => TexCache.GetOrAdd(path, p =>
    {
        try
        {
            var im = DdsImage.Load(p, 1024);
            if (im == null) return null;
            var bmp = BitmapSource.Create(im.Width, im.Height, 96, 96, im.HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, im.Bgra, im.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    });

    private void ResetView()
    {
        _yaw = -2.3; _pitch = 0.32;
        _dist = _radius * 2.6;
        var b = _bounds;
        if (!b.IsEmpty) _target = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var off = new Vector3D(Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch), Math.Cos(_pitch) * Math.Cos(_yaw)) * _dist;
        _cam.Position = _target + off;
        _cam.LookDirection = -off;
        // "su" della camera perpendicolare alla direzione di vista: non degenera quando si guarda dall'alto o dal basso
        _cam.UpDirection = new Vector3D(-Math.Sin(_pitch) * Math.Sin(_yaw), Math.Cos(_pitch), -Math.Sin(_pitch) * Math.Cos(_yaw));
        _cam.NearPlaneDistance = Math.Max(0.05, _dist / 200);
        _cam.FarPlaneDistance = _dist * 50;
    }
}
