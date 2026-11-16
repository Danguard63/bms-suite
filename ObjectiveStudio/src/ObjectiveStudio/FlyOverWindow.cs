using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TerrainTool.Core.Editor;
using TerrainTool.Core.NewTerrain;

namespace ObjectiveStudio;

/// <summary>
/// Volo libero sugli obiettivi: tabella di tutti gli obiettivi (cerca, filtra per tipo, Go) e telecamera libera come
/// l'anteprima del terreno del Terrain Tool. Terreno vero (HeightMap del NewTerrain, spianato sotto le basi come BMS)
/// con la foto Photoreal, terreno lontano piu' rado, e i modelli BMS con le loro texture e i numeri di pista giusti,
/// nella posizione e con la rotazione dell'editor (anche non salvate, se aperta dall'editor degli obiettivi).
/// La scena si ricarica da sola quando si esce dall'area caricata. Solo lettura.
/// </summary>
public sealed class FlyOverWindow : Window
{
    private const double FarHalf = 98425;      // 30 km
    private const int FarN = 161, FarPhoto = 1536, NearPhoto = 2048;

    private readonly EditorData _d;
    private readonly Viewport3D _view = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 60, NearPlaneDistance = 3, FarPlaneDistance = 400000, UpDirection = new Vector3D(0, 1, 0) };
    private readonly ModelVisual3D _sceneVisual = new();
    private readonly Border _host = new() { Focusable = true, FocusVisualStyle = null };
    private readonly TextBlock _hud = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(8), IsHitTestVisible = false };
    private readonly TextBlock _status = new() { Foreground = Brushes.Yellow, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(8), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 2, 0, 4), ToolTip = "Part of the name, or the objective ID" };
    private readonly ComboBox _type = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly ListView _list = new() { Height = 430 };
    private readonly ComboBox _area = new() { Width = 80 };
    private readonly TextBlock _count = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0) };

    // telecamera: piedi del teatro (north = X di BMS, east = Y), quota piedi sul mare, direzione 0 = nord, pendenza in gradi
    private double _n, _e, _alt, _yaw, _pitch = -20, _speed = 400;
    // scena caricata: centro, quota di riferimento, quote vicine e lontane
    private double _cN, _cE, _g0;
    private PreviewScene? _near;
    private float[,]? _farH;
    private bool _building, _again;
    private EdObjective? _pendingSel;
    private readonly HashSet<Key> _keys = new();
    private Point? _drag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private readonly Dictionary<string, Brush?> _texBrush = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _water = new() { Content = "Show water (as BMS draws it)", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
    private TerrainSampler? _sampler;

    private sealed record TypeItem(int Type, string Text) { public override string ToString() => Text; }

    public FlyOverWindow(EditorData d, EdObjective? start = null)
    {
        _d = d;
        Title = "Fly over objectives";
        Width = 1500; Height = 950; MinWidth = 1000; MinHeight = 650;
        Background = Brushes.Black;

        // ── 3D
        _view.Camera = _cam;
        _view.Children.Add(_sceneVisual);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(110, 110, 110)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(190, 185, 175), new Vector3D(-0.5, -0.8, 0.4)));
        _view.Children.Add(new ModelVisual3D { Content = lights });
        var grid3d = new Grid();
        grid3d.Children.Add(_view);
        grid3d.Children.Add(_hud);
        grid3d.Children.Add(_status);
        _host.Background = new LinearGradientBrush(Color.FromRgb(120, 165, 215), Color.FromRgb(205, 220, 235), 90);
        _host.Child = grid3d;

        // ── tabella degli obiettivi
        _type.Items.Add(new TypeItem(-1, "All types"));
        _type.Items.Add(new TypeItem(-2, "Airbases and airstrips"));
        foreach (int t in d.Objectives.Select(o => o.Type).Distinct().OrderBy(EditorData.TypeName))
            _type.Items.Add(new TypeItem(t, EditorData.TypeName(t)));
        _type.SelectedIndex = 1;
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Name", Width = 175, DisplayMemberBinding = new Binding(nameof(EdObjective.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = "Type", Width = 85, DisplayMemberBinding = new Binding(nameof(EdObjective.TypeName)) });
        gv.Columns.Add(new GridViewColumn { Header = "ID", Width = 50, DisplayMemberBinding = new Binding(nameof(EdObjective.Id)) });
        _list.View = gv;
        var view = new ListCollectionView(d.Objectives.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList()) { Filter = Filter };
        _list.ItemsSource = view;
        void Refilter() { view.Refresh(); _count.Text = $"{view.Count} of {d.Objectives.Count} objectives"; }
        _search.TextChanged += (_, _) => Refilter();
        _type.SelectionChanged += (_, _) => Refilter();
        _list.MouseDoubleClick += (_, _) => GoSelected();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { GoSelected(); e.Handled = true; } };
        Refilter();

        foreach (var km in new[] { 4, 6, 8, 12 }) _area.Items.Add($"{km} km");
        _area.SelectedIndex = 1;
        _area.SelectionChanged += (_, _) => { if (IsLoaded) Rebuild(_n, _e, null); };

        var go = new Button { Content = "Go", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0) };
        go.Click += (_, _) => GoSelected();
        var reload = new Button { Content = "Reload here", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Reload the scene around the camera (after edits in the objective editor)" };
        reload.Click += (_, _) => { _texBrush.Clear(); Rebuild(_n, _e, null); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(go); buttons.Children.Add(reload);
        var area = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        area.Children.Add(new TextBlock { Text = "Detailed area ", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        area.Children.Add(_area);

        var left = new StackPanel { Width = 340, Margin = new Thickness(6) };
        left.Children.Add(new TextBlock { Text = "Search", Foreground = Brushes.White });
        left.Children.Add(_search);
        left.Children.Add(new TextBlock { Text = "Type", Foreground = Brushes.White });
        left.Children.Add(_type);
        left.Children.Add(_list);
        left.Children.Add(_count);
        left.Children.Add(buttons);
        left.Children.Add(area);
        left.Children.Add(_water);
        _water.Click += (_, _) => Rebuild(_n, _e, null);
        left.Children.Add(new TextBlock
        {
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12,
            Text = "Double-click an objective (or Go) to fly there.\n\n" +
                   "Controls (click the 3D view first):\n" +
                   "  W / S or ↑ / ↓   forward / back\n" +
                   "  A / D            sideways\n" +
                   "  ← / →            turn\n" +
                   "  E / PgUp         up\n" +
                   "  Q / PgDn         down\n" +
                   "  right mouse drag look around\n" +
                   "  mouse wheel      speed\n" +
                   "  Shift            x5 faster, Ctrl slower\n" +
                   "  Home             look straight down\n\n" +
                   "BMS models with their textures and runway numbers, on the NewTerrain heights and photo, leveled under the bases as BMS does. " +
                   "Positions and rotations as in the objective editor. Not the BMS graphics: no lights, shadows, trees or LOD changes.",
        });

        var root = new DockPanel();
        var sv = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(40, 40, 44)) };
        DockPanel.SetDock(sv, Dock.Left);
        root.Children.Add(sv);
        root.Children.Add(_host);
        Content = root;

        // ── comandi
        _host.MouseDown += (_, e) =>
        {
            _host.Focus();
            if (e.ChangedButton == MouseButton.Right) { _drag = e.GetPosition(_host); _host.CaptureMouse(); }
        };
        _host.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Right) { _drag = null; _host.ReleaseMouseCapture(); } };
        _host.MouseMove += (_, e) =>
        {
            if (_drag is not Point p0) return;
            var pt = e.GetPosition(_host);
            _yaw = (_yaw + (pt.X - p0.X) * 0.25 + 360) % 360;
            _pitch = Math.Clamp(_pitch - (pt.Y - p0.Y) * 0.25, -89, 89);
            _drag = pt;
        };
        _host.MouseWheel += (_, e) => _speed = Math.Clamp(_speed * (e.Delta > 0 ? 1.25 : 0.8), 10, 30000);
        _host.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Home) _pitch = -89;
            _keys.Add(e.Key == Key.System ? e.SystemKey : e.Key);
            e.Handled = true;
        };
        _host.KeyUp += (_, e) => { _keys.Remove(e.Key == Key.System ? e.SystemKey : e.Key); e.Handled = true; };
        _host.LostKeyboardFocus += (_, _) => _keys.Clear();

        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnFrame;
            var first = start ?? d.Objectives.Where(o => o.Type is 1 or 2).OrderBy(o => o.Name).FirstOrDefault() ?? d.Objectives.FirstOrDefault();
            if (first != null) { _list.SelectedItem = first; _list.ScrollIntoView(first); GoTo(first); }
            else _status.Text = "No objectives in this project.";
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private bool Filter(object item)
    {
        if (item is not EdObjective o) return false;
        if (_type.SelectedItem is TypeItem ti)
        {
            if (ti.Type == -2 && o.Type is not (1 or 2)) return false;
            if (ti.Type >= 0 && o.Type != ti.Type) return false;
        }
        string s = _search.Text.Trim();
        return s.Length == 0 || o.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || o.Id.ToString(CultureInfo.InvariantCulture) == s;
    }

    private void GoSelected()
    {
        if (_list.SelectedItem is EdObjective o) GoTo(o);
        else _status.Text = "Select an objective in the table first.";
    }

    /// <summary>Mostra un obiettivo (anche chiamato dall'editor degli obiettivi).</summary>
    public void GoTo(EdObjective o)
    {
        // centro = centro dei pezzi, non il punto dell'obiettivo: in alcuni add-on i pezzi stanno a km dal punto
        // (Mideast128, Al Khiran Town: 3-7 km a est) e con il punto al centro finivano fuori dall'area dettagliata
        var lay = _d.LayoutOf(o);
        double cn = o.X, ce = o.Y, r = Math.Max(600, lay.Radius);
        if (lay.Features.Count > 0)
        {
            var w = lay.Features.Select(f => EditorData.ToWorld(o, f.N, f.E)).ToList();
            double n0 = w.Min(p => p.Item1), n1 = w.Max(p => p.Item1), e0 = w.Min(p => p.Item2), e1 = w.Max(p => p.Item2);
            cn = (n0 + n1) / 2; ce = (e0 + e1) / 2;
            r = Math.Max(600, Math.Max(n1 - n0, e1 - e0) / 2 + 150);
        }
        // da sud-ovest, guardando verso il centro dei pezzi
        double dist = Math.Min(r * 1.3, AreaHalf * 0.8);
        _yaw = 45;
        _n = cn - dist * Math.Cos(_yaw * Math.PI / 180);
        _e = ce - dist * Math.Sin(_yaw * Math.PI / 180);
        _alt = double.NaN;   // si mette quando c'e' il terreno
        _pitch = -Math.Clamp(Math.Atan2(Math.Max(500, r * 0.5), dist) * 180 / Math.PI, 10, 60);
        double off = Math.Sqrt((cn - o.X) * (cn - o.X) + (ce - o.Y) * (ce - o.Y));
        Title = $"Fly over objectives – {o.Name} ({o.TypeName}, ID {o.Id})" +
                (off > 3000 ? $" – pieces {off * 0.3048 / 1000:0.0} km from the objective point" : "");
        Rebuild(cn, ce, o);
        _host.Focus();
    }

    private double AreaHalf => new[] { 4, 6, 8, 12 }[Math.Max(0, _area.SelectedIndex)] * 500 * 3.280839895;

    // ───────────────────────── scena ─────────────────────────

    private async void Rebuild(double cn, double ce, EdObjective? sel)
    {
        if (_building) { _again = true; _pendingSel = sel; return; }
        _building = true;
        double half = AreaHalf;
        bool water = _water.IsChecked == true;
        _status.Text = "Loading terrain and models…";
        try
        {
            var (scene, farH, group) = await Task.Run(() =>
            {
                lock (_d)
                {
                    var s = PreviewScene.BuildAt(_d, cn, ce, sel, half, Math.Clamp((int)(2 * half / 160) + 1, 129, 257), NearPhoto);
                    var fh = _d.HeightWindow(cn, ce, FarHalf, FarN);
                    var fp = _d.PhotoArea(cn, ce, FarHalf, FarPhoto);
                    var g = new Model3DGroup();
                    if (fh != null) g.Children.Add(TerrainModel(fh, FarN, FarHalf, fp, FarPhoto, s.GroundAtCenter, half - 2 * FarHalf / (FarN - 1), -25));
                    g.Children.Add(TerrainModel(s.Heights, s.GridN, half, s.Photo, s.PhotoPx, s.GroundAtCenter, null, -2));
                    if (water && s.Heights != null)
                    {
                        try
                        {
                            _sampler ??= new TerrainSampler(_d.Project);
                            if (_sampler.WaterSide > 0 && _sampler.WaterSurface(cn, ce, half, s.GridN) is { } wsurf && WaterModel(s.Heights, wsurf, s.GridN, half, s.GroundAtCenter) is { } wm)
                                g.Children.Add(wm);
                            if (fh != null && _sampler.WaterSide > 0 && _sampler.WaterSurface(cn, ce, FarHalf, FarN) is { } wfar && WaterModel(fh, wfar, FarN, FarHalf, s.GroundAtCenter, half - 2 * FarHalf / (FarN - 1), -25) is { } wmf)
                                g.Children.Add(wmf);
                        }
                        catch (Exception) { }
                    }
                    foreach (var gr in s.Groups.Values)
                        if (MeshModel(gr) is { } m) g.Children.Add(m);
                    g.Freeze();
                    return (s, fh, g);
                }
            });
            _near = scene; _farH = farH;
            _cN = cn; _cE = ce; _g0 = scene.GroundAtCenter;
            _sceneVisual.Content = group;
            if (double.IsNaN(_alt))
            {
                double gr = Ground(_n, _e);
                _alt = gr + Math.Clamp(Math.Tan(-_pitch * Math.PI / 180) * Math.Max(1, Math.Sqrt((_n - cn) * (_n - cn) + (_e - ce) * (_e - ce))), 300, 6000);
            }
            _status.Text = $"{scene.Objectives} objectives, {scene.Triangles:N0} triangles" +
                           (scene.Leveled > 0 ? $", {scene.Leveled} bases leveled" : "") +
                           (scene.MissingModels > 0 ? $", {scene.MissingModels} pieces without a readable model (grey boxes)" : "") +
                           (scene.Triangles > PreviewScene.MaxTriangles ? "  |  too many triangles: some far pieces left out, choose a smaller area" : "") +
                           (scene.Heights == null ? "  |  HeightMap.raw not found: flat ground" : "") +
                           (scene.Photo == null ? "  |  no Photoreal tiles: grey ground" : "");
        }
        catch (Exception ex) { _status.Text = "Scene not loaded: " + ex.Message; if (double.IsNaN(_alt)) _alt = 3000; }
        finally
        {
            _building = false;
            if (_again) { _again = false; var ps = _pendingSel; _pendingSel = null; if (ps != null) Rebuild(ps.X, ps.Y, ps); else Rebuild(_n, _e, null); }
        }
    }

    /// <summary>Maglia del terreno (coordinate relative al centro, Y = piedi sopra g0); con hole salta i quadrati dentro l'area vicina.</summary>
    private static GeometryModel3D TerrainModel(float[,]? h, int n, double half, byte[]? photo, int photoPx, double g0, double? hole, double drop)
    {
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(n * n); var uv = new PointCollection(n * n); var idx = new Int32Collection((n - 1) * (n - 1) * 6);
        double step = 2 * half / (n - 1);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double east = -half + i * step, north = half - j * step;
                pos.Add(new Point3D(east, (h == null ? 0 : h[j, i] - g0) + drop, -north));
                uv.Add(new Point((double)i / (n - 1), (double)j / (n - 1)));
            }
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                if (hole is double hh)
                {
                    double e0 = -half + i * step, n0 = half - j * step;
                    if (Math.Abs(e0) < hh && Math.Abs(e0 + step) < hh && Math.Abs(n0) < hh && Math.Abs(n0 - step) < hh) continue;
                }
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        m.Positions = pos; m.TextureCoordinates = uv; m.TriangleIndices = idx;
        m.Freeze();
        Brush br = new SolidColorBrush(Color.FromRgb(150, 140, 120));
        if (photo != null)
        {
            var bmp = BitmapSource.Create(photoPx, photoPx, 96, 96, PixelFormats.Bgra32, null, photo, photoPx * 4);
            bmp.Freeze();
            br = new ImageBrush(bmp);
        }
        br.Freeze();
        var mat = new DiffuseMaterial(br); mat.Freeze();
        return new GeometryModel3D(m, mat);
    }

    /// <summary>Pelo dell'acqua (WaterHeightMap) dove sta sopra il terreno: come lo disegna BMS, semitrasparente.</summary>
    private static GeometryModel3D? WaterModel(float[,] h, float[,] w, int n, double half, double g0, double? hole = null, double drop = 0)
    {
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(); var idx = new Int32Collection();
        var map = new int[n * n];
        Array.Fill(map, -1);
        double step = 2 * half / (n - 1);
        int V(int i, int j)
        {
            int k = j * n + i;
            if (map[k] >= 0) return map[k];
            map[k] = pos.Count;
            pos.Add(new Point3D(-half + i * step, w[j, i] - g0 + 0.5 + drop, -(half - j * step)));
            return map[k];
        }
        bool Wet(int i, int j) => w[j, i] > h[j, i] - 0.5;
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                if (!(Wet(i, j) || Wet(i + 1, j) || Wet(i, j + 1) || Wet(i + 1, j + 1))) continue;
                if (hole is double hh)
                {
                    double e0 = -half + i * step, n0 = half - j * step;
                    if (Math.Abs(e0) < hh && Math.Abs(e0 + step) < hh && Math.Abs(n0) < hh && Math.Abs(n0 - step) < hh) continue;
                }
                int a = V(i, j), b = V(i + 1, j), c = V(i, j + 1), d = V(i + 1, j + 1);
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        if (idx.Count == 0) return null;
        m.Positions = pos; m.TriangleIndices = idx;
        m.Freeze();
        var mg = new MaterialGroup();
        mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(160, 35, 90, 150))));
        mg.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 40));
        mg.Freeze();
        var gm = new GeometryModel3D(m, mg) { BackMaterial = mg };
        gm.Freeze();
        return gm;
    }

    private static readonly Brush Untextured = Frozen(new SolidColorBrush(Color.FromRgb(200, 200, 195)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private Brush? TexBrush(string id)
    {
        if (_texBrush.TryGetValue(id, out var b)) return b;
        b = null;
        if (_d.Texture(id) is { } tx)
        {
            var bmp = BitmapSource.Create(tx.W, tx.H, 96, 96, PixelFormats.Bgra32, null, tx.Bgra, tx.W * 4);
            bmp.Freeze();
            // le coordinate di texture dei modelli possono uscire da 0..1: si ripete la texture
            b = new ImageBrush(bmp) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            b.Freeze();
        }
        _texBrush[id] = b;
        return b;
    }

    private GeometryModel3D? MeshModel(PreviewScene.Group g)
    {
        var t = g.Pos;
        if (t.Count < 9) return null;
        var m = new MeshGeometry3D();
        int nv = t.Count / 3;
        var pos = new Point3DCollection(nv); var idx = new Int32Collection(nv);
        for (int i = 0; i < t.Count; i += 3) { pos.Add(new Point3D(t[i], t[i + 1], t[i + 2])); idx.Add(idx.Count); }
        m.Positions = pos; m.TriangleIndices = idx;
        Brush? br = g.Tex != null ? TexBrush(g.Tex) : null;
        if (br != null && g.Uv.Count == nv * 2)
        {
            var uv = new PointCollection(nv);
            for (int i = 0; i < g.Uv.Count; i += 2) uv.Add(new Point(g.Uv[i], g.Uv[i + 1]));
            m.TextureCoordinates = uv;
        }
        else br = Untextured;
        m.Freeze();
        var mat = new DiffuseMaterial(br); mat.Freeze();
        var gm = new GeometryModel3D(m, mat) { BackMaterial = mat };
        gm.Freeze();
        return gm;
    }

    // ───────────────────────── movimento ─────────────────────────

    /// <summary>Quota del terreno (piedi): dall'area vicina (spianata), altrimenti da quella lontana.</summary>
    private double Ground(double north, double east)
    {
        if (_near is { } s && Math.Abs(north - s.CenterX) < s.Half && Math.Abs(east - s.CenterY) < s.Half)
            return s.Heights == null ? _g0 : s.Ground(north, east);
        if (_farH is { } h)
        {
            int n = FarN;
            double fi = (east - (_cE - FarHalf)) / (2 * FarHalf) * (n - 1), fj = (_cN + FarHalf - north) / (2 * FarHalf) * (n - 1);
            int i = Math.Clamp((int)Math.Floor(fi), 0, n - 2), j = Math.Clamp((int)Math.Floor(fj), 0, n - 2);
            double ti = Math.Clamp(fi - i, 0, 1), tj = Math.Clamp(fj - j, 0, 1);
            return (h[j, i] * (1 - ti) + h[j, i + 1] * ti) * (1 - tj) + (h[j + 1, i] * (1 - ti) + h[j + 1, i + 1] * ti) * tj;
        }
        return _g0;
    }

    private Point3D P(double north, double east, double h) => new(east - _cE, h - _g0, -(north - _cN));

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        if (double.IsNaN(_alt)) return;
        bool K(params Key[] ks) => ks.Any(_keys.Contains);
        double sp = _speed * (K(Key.LeftShift, Key.RightShift) ? 5 : 1) * (K(Key.LeftCtrl, Key.RightCtrl) ? 0.2 : 1) * dt;
        double yr = _yaw * Math.PI / 180;
        double fwd = (K(Key.W, Key.Up) ? 1 : 0) - (K(Key.S, Key.Down) ? 1 : 0);
        double side = (K(Key.D) ? 1 : 0) - (K(Key.A) ? 1 : 0);
        double up = (K(Key.E, Key.PageUp) ? 1 : 0) - (K(Key.Q, Key.PageDown) ? 1 : 0);
        if (K(Key.Left)) _yaw = (_yaw - 70 * dt + 360) % 360;
        if (K(Key.Right)) _yaw = (_yaw + 70 * dt) % 360;
        _n += (fwd * Math.Cos(yr) - side * Math.Sin(yr)) * sp;
        _e += (fwd * Math.Sin(yr) + side * Math.Cos(yr)) * sp;
        _alt += up * sp;
        double g = Ground(_n, _e);
        _alt = Math.Clamp(_alt, g + 8, 80000);
        // fuori dall'area dettagliata: si ricarica intorno alla telecamera
        double half = _near?.Half ?? AreaHalf;
        if (!_building && (Math.Abs(_n - _cN) > half * 0.6 || Math.Abs(_e - _cE) > half * 0.6)) Rebuild(_n, _e, null);

        double pr = _pitch * Math.PI / 180;
        _cam.Position = P(_n, _e, _alt);
        _cam.LookDirection = new Vector3D(Math.Sin(yr) * Math.Cos(pr), Math.Sin(pr), -Math.Cos(yr) * Math.Cos(pr));
        double agl = _alt - g;
        _cam.NearPlaneDistance = Math.Clamp(agl / 25, 1, 600);
        _cam.FarPlaneDistance = 2.2 * FarHalf + _alt;

        // obiettivo piu' vicino
        EdObjective? best = null; double bd = double.MaxValue;
        foreach (var o in _d.Objectives)
        {
            double dd = (o.X - _n) * (o.X - _n) + (o.Y - _e) * (o.Y - _e);
            if (dd < bd) { bd = dd; best = o; }
        }
        string nearTxt = best != null ? $"nearest: {best.Name} ({best.TypeName}, ID {best.Id}) {Math.Sqrt(bd) / 6076.12:0.0} nm" : "";
        _hud.Text = $"BMS X {_n:0}  Y {_e:0} ft   {nearTxt}\n" +
                    $"altitude {_alt:0} ft MSL, {agl:0} ft AGL   ground {g:0} ft\n" +
                    $"heading {_yaw:000}°  pitch {_pitch:+0;-0}°  speed {_speed * 0.592484:0} kt";
    }
}
