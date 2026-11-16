using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace TerrainTool.App;

/// <summary>
/// Anteprima del terreno finito (Output\NewTerrain): si sceglie un punto sulla mappa del teatro (o una base, o lat/lon) e
/// ci si muove liberamente con una telecamera. Terreno vicino alla risoluzione della HeightMap (62,5 m), terreno lontano
/// piu' rado, foto Photoreal 4K sopra, acqua semitrasparente alla quota della WaterHeightMap. Si ricarica da solo quando
/// ci si sposta. Non e' la grafica di BMS: serve a controllare forme, rive, acqua e texture. Solo lettura.
/// </summary>
public sealed class TerrainPreviewWindow : Window
{
    private const double NearHalf = 19685;     // 6 km
    private const double FarHalf = 131234;     // 40 km
    private const int FarN = 257, PhotoNear = 2048, PhotoFar = 2048, OverviewPx = 512;
    private const double RebuildDist = 8000;   // ~2,4 km dal centro del terreno vicino

    private readonly TheaterProject _p;
    private readonly TerrainSampler _s;
    private readonly TheaterGrid _grid;
    private readonly List<Airbase> _bases;

    private readonly Viewport3D _view = new() { ClipToBounds = true };
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 60, NearPlaneDistance = 5, FarPlaneDistance = 400000, UpDirection = new Vector3D(0, 1, 0) };
    private readonly ModelVisual3D _sceneVisual = new();
    private readonly Border _host = new() { Focusable = true, FocusVisualStyle = null };
    private readonly TextBlock _hud = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(8), IsHitTestVisible = false };
    private readonly TextBlock _status = new() { Foreground = Brushes.Yellow, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(8), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Image _overview = new() { Width = 300, Height = 300, Stretch = Stretch.Fill };
    private readonly Canvas _ovCanvas = new() { Width = 300, Height = 300, Background = Brushes.DimGray, ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Ellipse _ovDot = new() { Width = 8, Height = 8, Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Line _ovDir = new() { Stroke = Brushes.Red, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle _ovArea = new() { Stroke = Brushes.Yellow, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly ComboBox _baseCombo = new() { Margin = new Thickness(0, 2, 0, 2), IsEditable = true, IsTextSearchEnabled = true };
    private readonly TextBox _latLon = new() { Margin = new Thickness(0, 2, 4, 2), ToolTip = "lat, lon in degrees (e.g. 29.37, 47.98)" };
    private readonly TextBox _bmsX = new() { Width = 110, Margin = new Thickness(0, 2, 4, 2), ToolTip = "BMS X: feet north of the south-west corner (as PositionX in CampObjData.XML)" };
    private readonly TextBox _bmsY = new() { Width = 110, Margin = new Thickness(0, 2, 4, 2), ToolTip = "BMS Y: feet east of the south-west corner (as PositionY in CampObjData.XML)" };
    private readonly ComboBox _exag = new() { Width = 70 };
    private readonly CheckBox _water = new() { Content = "Show water surface", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _photo = new() { Content = "Photoreal texture (off = height colours)", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };

    // telecamera: piedi del teatro (north da sud, east da ovest), quota piedi sul mare, direzione 0 = nord, pendenza in gradi
    private double _n, _e, _alt, _yaw, _pitch = -15, _speed = 600;
    private double _oN, _oE;                    // centro della scena caricata
    private double _ex = 1;
    private float[,]? _nearH; private double _nearN, _nearE; private int _nearCount;
    private bool _building, _rebuildAgain;
    private readonly HashSet<Key> _keys = new();
    private Point? _drag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    public TerrainPreviewWindow(TheaterProject p)
    {
        _p = p;
        _s = new TerrainSampler(p);
        _grid = p.Theater.CreateGrid();
        _bases = File.Exists(p.AirbasesCsv) ? AirbaseCsv.Load(p.AirbasesCsv) : new List<Airbase>();
        Title = $"Terrain preview — {p.Theater.Name} ({p.OutputNewTerrain})";
        Width = 1400; Height = 900; MinWidth = 1000; MinHeight = 650;
        Background = Brushes.Black;

        // ── 3D
        _view.Camera = _cam;
        _view.Children.Add(_sceneVisual);
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(105, 105, 105)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(200, 195, 185), new Vector3D(-0.5, -0.8, 0.4)));
        _view.Children.Add(new ModelVisual3D { Content = lights });
        var sky = new LinearGradientBrush(Color.FromRgb(120, 165, 215), Color.FromRgb(205, 220, 235), 90);
        var stack = new Grid();
        stack.Children.Add(_view);
        stack.Children.Add(_hud);
        stack.Children.Add(_status);
        _host.Background = sky;
        _host.Child = stack;

        // ── pannello di sinistra
        _ovCanvas.Children.Add(_overview);
        _ovCanvas.Children.Add(_ovArea);
        _ovCanvas.Children.Add(_ovDir);
        _ovCanvas.Children.Add(_ovDot);
        _ovCanvas.MouseLeftButtonDown += (_, e) =>
        {
            var pt = e.GetPosition(_ovCanvas);
            Teleport(_s.SizeFt * (1 - pt.Y / _ovCanvas.Height), _s.SizeFt * pt.X / _ovCanvas.Width, 3000);
        };
        foreach (var b in _bases.Where(b => b.HasReal || !double.IsNaN(b.LatOld)).OrderBy(b => b.Name))
            _baseCombo.Items.Add(new ComboBoxItem { Content = $"{b.Name} {(string.IsNullOrEmpty(b.Icao) ? "" : "(" + b.Icao + ")")}", Tag = b });
        _baseCombo.SelectionChanged += (_, _) =>
        {
            if (_baseCombo.SelectedItem is ComboBoxItem { Tag: Airbase b })
            {
                double lat = b.HasReal ? b.Lat : b.LatOld, lon = b.HasReal ? b.Lon : b.LonOld;
                GoLatLon(lat, lon, 2500);
            }
        };
        var go = new Button { Content = "Go", Padding = new Thickness(10, 1, 10, 1) };
        go.Click += (_, _) =>
        {
            var parts = _latLon.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double la) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
                GoLatLon(la, lo, 3000);
            else MessageBox.Show(this, "Write latitude and longitude in degrees, e.g. 29.37, 47.98", Title);
        };
        var goXy = new Button { Content = "Go", Padding = new Thickness(10, 1, 10, 1) };
        goXy.Click += (_, _) =>
        {
            static bool D(string t, out double v) => double.TryParse(t.Trim().Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            if (D(_bmsX.Text, out double bx) && D(_bmsY.Text, out double by))
            {
                if (bx < 0 || by < 0 || bx > _s.SizeFt || by > _s.SizeFt) MessageBox.Show(this, $"X and Y must be between 0 and {_s.SizeFt:0} feet", Title);
                else Teleport(bx, by, 3000);
            }
            else MessageBox.Show(this, "Write BMS X (north) and Y (east) in feet, e.g. 3360000 and 3950000", Title);
        };
        foreach (var x in new[] { "1×", "1.5×", "2×", "3×" }) _exag.Items.Add(x);
        _exag.SelectedIndex = 0;
        _exag.SelectionChanged += (_, _) => { _ex = double.Parse(((string)_exag.SelectedItem).TrimEnd('×'), CultureInfo.InvariantCulture); Rebuild(); };
        _water.Click += (_, _) => Rebuild();
        _photo.Click += (_, _) => Rebuild();

        var left = new StackPanel { Width = 310, Margin = new Thickness(6) };
        left.Children.Add(new TextBlock { Text = "Click on the map to go there", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        left.Children.Add(new Border { Child = _ovCanvas, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Left });
        left.Children.Add(new TextBlock { Text = "Airbase", Foreground = Brushes.White });
        left.Children.Add(_baseCombo);
        var ll = new DockPanel();
        DockPanel.SetDock(go, Dock.Right); ll.Children.Add(go); ll.Children.Add(_latLon);
        left.Children.Add(new TextBlock { Text = "Latitude, longitude", Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) });
        left.Children.Add(ll);
        left.Children.Add(new TextBlock { Text = "BMS X (north, ft)   BMS Y (east, ft)", Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) });
        var xy = new StackPanel { Orientation = Orientation.Horizontal };
        xy.Children.Add(_bmsX); xy.Children.Add(_bmsY); xy.Children.Add(goXy);
        left.Children.Add(xy);
        var ex = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        ex.Children.Add(new TextBlock { Text = "Height exaggeration ", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        ex.Children.Add(_exag);
        left.Children.Add(ex);
        _water.Foreground = Brushes.White; _photo.Foreground = Brushes.White;
        left.Children.Add(_water);
        left.Children.Add(_photo);
        left.Children.Add(new TextBlock
        {
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12,
            Text = "Controls (click the 3D view first):\n" +
                   "  W / S or ↑ / ↓   forward / back\n" +
                   "  A / D            sideways\n" +
                   "  ← / →            turn\n" +
                   "  E / PgUp         up\n" +
                   "  Q / PgDn         down\n" +
                   "  right mouse drag look around\n" +
                   "  mouse wheel      speed\n" +
                   "  Shift            x5 faster, Ctrl slower\n" +
                   "  Home             look straight down\n\n" +
                   "Shows Output\\NewTerrain of the project (what 'Install' copies into BMS), not the BMS graphics: no sea shader, waves, shadows or objects. " +
                   "Water: the WaterHeightMap surface, drawn where it is above the ground. Near terrain at the HeightMap resolution (62.5 m), far terrain coarser.",
        });
        foreach (var w in _s.Warnings) left.Children.Add(new TextBlock { Text = "⚠ " + w, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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
            if (_drag is not Point d) return;
            var pt = e.GetPosition(_host);
            _yaw = (_yaw + (pt.X - d.X) * 0.25 + 360) % 360;
            _pitch = Math.Clamp(_pitch - (pt.Y - d.Y) * 0.25, -89, 89);
            _drag = pt;
        };
        _host.MouseWheel += (_, e) => _speed = Math.Clamp(_speed * (e.Delta > 0 ? 1.25 : 0.8), 20, 30000);
        _host.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Home) _pitch = -89;
            _keys.Add(e.Key == Key.System ? e.SystemKey : e.Key);
            e.Handled = true;
        };
        _host.KeyUp += (_, e) => { _keys.Remove(e.Key == Key.System ? e.SystemKey : e.Key); e.Handled = true; };
        _host.LostKeyboardFocus += (_, _) => _keys.Clear();

        // punto di partenza: centro del teatro, poi la mappa d'insieme
        Loaded += async (_, _) =>
        {
            Teleport(_s.SizeFt / 2, _s.SizeFt / 2, 6000);
            CompositionTarget.Rendering += OnFrame;
            try
            {
                var px = await Task.Run(() => _s.Overview(OverviewPx));
                var bmp = BitmapSource.Create(OverviewPx, OverviewPx, 96, 96, PixelFormats.Bgra32, null, px, OverviewPx * 4);
                bmp.Freeze();
                _overview.Source = bmp;
            }
            catch (Exception ex2) { _status.Text = "Overview map not made: " + ex2.Message; }
            _host.Focus();
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private void GoLatLon(double lat, double lon, double agl)
    {
        var (x, y) = _grid.ToTheater(lat, lon);
        if (!_grid.Contains(x, y)) { MessageBox.Show(this, $"{lat:0.####}, {lon:0.####} is outside the theater", Title); return; }
        Teleport(y * TerrainSampler.FtPerM, x * TerrainSampler.FtPerM, agl);
    }

    private void Teleport(double north, double east, double agl)
    {
        _n = Math.Clamp(north, 0, _s.SizeFt); _e = Math.Clamp(east, 0, _s.SizeFt);
        double g = _s.GroundAt(_n, _e);
        _alt = Math.Max(g, _s.WaterAt(_n, _e) ?? g) + agl;
        _pitch = -20;
        _nearH = null;
        Rebuild();
        _host.Focus();
    }

    // ───────────────────────── scena ─────────────────────────

    private sealed record Patch(double Half, int N, float[,] H, float[,]? W, byte[]? Photo, int PhotoPx);

    private async void Rebuild()
    {
        if (_building) { _rebuildAgain = true; return; }
        _building = true;
        double cn = _n, ce = _e;
        bool water = _water.IsChecked == true, photo = _photo.IsChecked == true;
        _status.Text = "Loading terrain…";
        try
        {
            int nearN = (int)Math.Round(2 * NearHalf / _s.HeightPxFt) + 1;
            var (near, far) = await Task.Run(() =>
            {
                var nh = _s.Heights(cn, ce, NearHalf, nearN) ?? throw new InvalidOperationException("outside the HeightMap");
                var fh = _s.Heights(cn, ce, FarHalf, FarN) ?? throw new InvalidOperationException("outside the HeightMap");
                var nw = water ? _s.WaterSurface(cn, ce, NearHalf, nearN) : null;
                var fw = water ? _s.WaterSurface(cn, ce, FarHalf, FarN) : null;
                var np = photo ? _s.PhotoArea(cn, ce, NearHalf, PhotoNear) : null;
                var fp = photo ? _s.PhotoArea(cn, ce, FarHalf, PhotoFar) : null;
                return (new Patch(NearHalf, nearN, nh, nw, np, PhotoNear), new Patch(FarHalf, FarN, fh, fw, fp, PhotoFar));
            });
            _oN = cn; _oE = ce;
            var group = new Model3DGroup();
            group.Children.Add(TerrainModel(far, NearHalf - 2 * FarHalf / (FarN - 1)));
            group.Children.Add(TerrainModel(near, null));
            if (far.W != null) { var m = WaterModel(far, NearHalf - 2 * FarHalf / (FarN - 1)); if (m != null) group.Children.Add(m); }
            if (near.W != null) { var m = WaterModel(near, null); if (m != null) group.Children.Add(m); }
            group.Freeze();
            _sceneVisual.Content = group;
            _nearH = near.H; _nearN = cn; _nearE = ce; _nearCount = near.N;
            _status.Text = "";
        }
        catch (Exception ex) { _status.Text = "Terrain not loaded: " + ex.Message; }
        finally
        {
            _building = false;
            if (_rebuildAgain) { _rebuildAgain = false; Rebuild(); }
        }
    }

    private Point3D P(double north, double east, double h) => new(east - _oE, h * _ex, -(north - _oN));

    /// <summary>Maglia del terreno; con hole, salta i quadrati interamente dentro il terreno vicino (niente sovrapposizioni).</summary>
    private GeometryModel3D TerrainModel(Patch t, double? hole)
    {
        int n = t.N;
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(n * n); var uv = new PointCollection(n * n); var idx = new Int32Collection((n - 1) * (n - 1) * 6);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double no = _oN + t.Half - 2 * t.Half * j / (n - 1), ea = _oE - t.Half + 2 * t.Half * i / (n - 1);
                // il lontano un po' sotto: dove tocca il vicino non si vedono fessure
                pos.Add(P(no, ea, t.H[j, i] - (hole != null ? 15 : 0)));
                uv.Add(new Point((double)i / (n - 1), (double)j / (n - 1)));
            }
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                if (hole is double hh && Inside(t, i, j, hh)) continue;
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        m.Positions = pos; m.TextureCoordinates = uv; m.TriangleIndices = idx;
        m.Freeze();
        Brush br;
        if (t.Photo != null)
        {
            var bmp = BitmapSource.Create(t.PhotoPx, t.PhotoPx, 96, 96, PixelFormats.Bgra32, null, t.Photo, t.PhotoPx * 4);
            bmp.Freeze();
            br = new ImageBrush(bmp);
        }
        else br = new ImageBrush(HeightColours(t));
        br.Freeze();
        var mat = new DiffuseMaterial(br); mat.Freeze();
        return new GeometryModel3D(m, mat);
    }

    private static bool Inside(Patch t, int i, int j, double hole)
    {
        double step = 2 * t.Half / (t.N - 1);
        double e0 = -t.Half + i * step, e1 = e0 + step, n0 = t.Half - j * step, n1 = n0 - step;
        return Math.Abs(e0) < hole && Math.Abs(e1) < hole && Math.Abs(n0) < hole && Math.Abs(n1) < hole;
    }

    /// <summary>Colori per quota (senza foto): verde basso, marrone, grigio, bianco in alto.</summary>
    private static BitmapSource HeightColours(Patch t)
    {
        int n = t.N;
        var px = new byte[n * n * 4];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double h = t.H[j, i];
                (double r, double g, double b) c = h < 0 ? (90, 120, 150) : h < 1500 ? (150, 160, 110) : h < 5000 ? (175, 150, 110) : h < 10000 ? (150, 140, 130) : (235, 235, 240);
                int o = (j * n + i) * 4;
                px[o] = (byte)c.b; px[o + 1] = (byte)c.g; px[o + 2] = (byte)c.r; px[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Superficie dell'acqua: solo i triangoli dove il pelo dell'acqua sta sopra il terreno in almeno un vertice.</summary>
    private GeometryModel3D? WaterModel(Patch t, double? hole)
    {
        if (t.W == null) return null;
        int n = t.N;
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(); var idx = new Int32Collection();
        var map = new int[n * n];
        Array.Fill(map, -1);
        int V(int i, int j)
        {
            int k = j * n + i;
            if (map[k] >= 0) return map[k];
            double no = _oN + t.Half - 2 * t.Half * j / (n - 1), ea = _oE - t.Half + 2 * t.Half * i / (n - 1);
            map[k] = pos.Count;
            pos.Add(P(no, ea, t.W[j, i] + 0.5 - (hole != null ? 15 : 0)));
            return map[k];
        }
        bool Wet(int i, int j) => t.W[j, i] > t.H[j, i] - 0.5;
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                if (!(Wet(i, j) || Wet(i + 1, j) || Wet(i, j + 1) || Wet(i + 1, j + 1))) continue;
                if (hole is double hh && Inside(t, i, j, hh)) continue;
                int a = V(i, j), b = V(i + 1, j), c = V(i, j + 1), d = V(i + 1, j + 1);
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        if (idx.Count == 0) return null;
        m.Positions = pos; m.TriangleIndices = idx;
        m.Freeze();
        var mg = new MaterialGroup();
        mg.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(150, 35, 90, 150))));
        mg.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 40));
        mg.Freeze();
        return new GeometryModel3D(m, mg) { BackMaterial = mg };
    }

    // ───────────────────────── movimento ─────────────────────────

    /// <summary>Quota del terreno sotto la telecamera: dalla griglia vicina in memoria, altrimenti dal file.</summary>
    private double Ground(double north, double east)
    {
        if (_nearH is { } h)
        {
            int n = _nearCount;
            double fi = (east - (_nearE - NearHalf)) / (2 * NearHalf) * (n - 1), fj = (_nearN + NearHalf - north) / (2 * NearHalf) * (n - 1);
            if (fi >= 0 && fj >= 0 && fi < n - 1 && fj < n - 1)
            {
                int i = (int)fi, j = (int)fj;
                double ti = fi - i, tj = fj - j;
                return (h[j, i] * (1 - ti) + h[j, i + 1] * ti) * (1 - tj) + (h[j + 1, i] * (1 - ti) + h[j + 1, i + 1] * ti) * tj;
            }
        }
        return _s.GroundAt(north, east);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        bool K(params Key[] ks) => ks.Any(_keys.Contains);
        double sp = _speed * (K(Key.LeftShift, Key.RightShift) ? 5 : 1) * (K(Key.LeftCtrl, Key.RightCtrl) ? 0.2 : 1) * dt;
        double yr = _yaw * Math.PI / 180;
        double fwd = (K(Key.W, Key.Up) ? 1 : 0) - (K(Key.S, Key.Down) ? 1 : 0);
        double side = (K(Key.D) ? 1 : 0) - (K(Key.A) ? 1 : 0);
        double up = (K(Key.E, Key.PageUp) ? 1 : 0) - (K(Key.Q, Key.PageDown) ? 1 : 0);
        if (K(Key.Left)) _yaw = (_yaw - 70 * dt + 360) % 360;
        if (K(Key.Right)) _yaw = (_yaw + 70 * dt) % 360;
        _n = Math.Clamp(_n + (fwd * Math.Cos(yr) - side * Math.Sin(yr)) * sp, 0, _s.SizeFt);
        _e = Math.Clamp(_e + (fwd * Math.Sin(yr) + side * Math.Cos(yr)) * sp, 0, _s.SizeFt);
        _alt += up * sp;
        double g = Ground(_n, _e);
        _alt = Math.Clamp(_alt, g + 15, 80000);
        if (!_building && (Math.Abs(_n - _oN) > RebuildDist || Math.Abs(_e - _oE) > RebuildDist)) Rebuild();

        // telecamera
        double pr = _pitch * Math.PI / 180;
        var dir = new Vector3D(Math.Sin(yr) * Math.Cos(pr), Math.Sin(pr) * 1, -Math.Cos(yr) * Math.Cos(pr));
        _cam.Position = P(_n, _e, _alt);
        _cam.LookDirection = dir;
        double agl = _alt - g;
        _cam.NearPlaneDistance = Math.Clamp(agl * _ex / 30, 2, 800);
        _cam.FarPlaneDistance = 2.2 * FarHalf + _alt * _ex;

        // indicazioni
        var (lat, lon) = _grid.ToLatLon(_e / TerrainSampler.FtPerM, _n / TerrainSampler.FtPerM);
        double? ws = _s.WaterSide > 0 ? WaterHere() : null;
        string wtxt = ws is double w ? (w > g - 0.5 ? $"water surface {w:0} ft, depth {w - g:0} ft" : "land") : "";
        _hud.Text = $"{Math.Abs(lat):0.0000}°{(lat >= 0 ? "N" : "S")} {Math.Abs(lon):0.0000}°{(lon >= 0 ? "E" : "W")}   BMS X {_n:0} Y {_e:0} ft ({_n / 3280.84:0.0} / {_e / 3280.84:0.0} km)\n" +
                    $"altitude {_alt:0} ft MSL, {agl:0} ft AGL   ground {g:0} ft   {wtxt}\n" +
                    $"heading {_yaw:000}°  pitch {_pitch:+0;-0}°  speed {_speed * 0.592484:0} kt ({_speed:0} ft/s){(_ex != 1 ? $"   heights ×{_ex:0.#}" : "")}";
        double s = _ovCanvas.Width / _s.SizeFt;
        double x = _e * s, y = (_s.SizeFt - _n) * s;
        Canvas.SetLeft(_ovDot, x - 4); Canvas.SetTop(_ovDot, y - 4);
        _ovDir.X1 = x; _ovDir.Y1 = y; _ovDir.X2 = x + Math.Sin(yr) * 14; _ovDir.Y2 = y - Math.Cos(yr) * 14;
        double a = FarHalf * s;
        _ovArea.Width = 2 * a; _ovArea.Height = 2 * a;
        Canvas.SetLeft(_ovArea, _oE * s - a); Canvas.SetTop(_ovArea, (_s.SizeFt - _oN) * s - a);
    }

    private double _wsCacheN = double.NaN, _wsCacheE, _wsCache;
    private double WaterHere()
    {
        // lettura dal file solo quando ci si sposta di almeno 100 piedi
        if (double.IsNaN(_wsCacheN) || Math.Abs(_n - _wsCacheN) > 100 || Math.Abs(_e - _wsCacheE) > 100)
        {
            _wsCache = _s.WaterAt(_n, _e) ?? double.NaN;
            _wsCacheN = _n; _wsCacheE = _e;
        }
        return _wsCache;
    }
}
