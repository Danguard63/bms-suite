using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Anteprima 3D di collaudo: terreno vero (HeightMap del NewTerrain) con la foto Photoreal sopra e i modelli BMS
/// (BML) di tutti gli obiettivi dell'area, nella posizione e con la rotazione dell'editor (anche non salvate).
/// Mouse: sinistro = gira intorno, destro = sposta, rotella = avvicina. "Refresh" rilegge dopo una modifica.
/// "Edit pieces": i pezzi dell'obiettivo scelto sono modelli separati; clic = scegli, trascina = sposta sul terreno, Q/E = gira,
/// frecce = sposta di poco, Canc = togli, Ctrl+Z = annulla. Le modifiche vanno nell'obiettivo (EditorData.SetPiece) e si salvano
/// con "Save edits" dell'editor (piece_edits.csv); Run Objects le mette in una copia della disposizione solo per quell'obiettivo.
/// </summary>
public sealed class Preview3DWindow : Window
{
    private readonly EditorData _d;
    private EdObjective _o;
    private readonly Viewport3D _view = new();
    private readonly PerspectiveCamera _cam = new() { FieldOfView = 50, NearPlaneDistance = 5, FarPlaneDistance = 400000, UpDirection = new Vector3D(0, 1, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2) };
    private readonly ComboBox _area = new() { Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
    private readonly CheckBox _exag = new() { Content = "Relief ×3", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _water = new() { Content = "Water (BMS)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Where BMS 4.38 has water (water surface above the ground): blue = water, cyan = shore. Useful on marshes and shallow water where the photo shows the bottom." };
    private readonly CheckBox _photo = new() { Content = "Ground photo", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Untick to see the ground plain grey: the pieces and the relief stand out better" };
    private readonly CheckBox _night = new() { Content = "Night", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        ToolTip = "Night view: dark ground and models, the lights of the models on (runway, approach, VASI, obstruction lights, street lights…), the light they throw on the ground and the parts that glow (PBR emission)" };
    private ModelVisual3D? _lightsVis;
    private double _lightsDist;
    private Border? _sky;
    private readonly TextBlock _legend = new() { Margin = new Thickness(6, 2, 6, 2), TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private double _yaw = 200, _pitch = 35, _dist = 3000;
    private Point3D _target;
    private Point? _drag; private bool _pan;
    private double _ground;

    // ── modifica dei pezzi ──
    private readonly CheckBox _edit = new() { Content = "Edit pieces", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold,
        ToolTip = "Move, turn or remove the single pieces of the selected objective (only this objective: the others with the same layout do not change)" };
    private readonly Button _remove = new() { Content = "Remove piece", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
    private readonly Button _group = new() { Content = "Select group", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "All the points of the list of the selected point(s): the whole SAM battery, the AAA guns... (also: double-click a point). They move and turn as one block." };
    private readonly Button _restore = new() { Content = "Restore removed", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _resetPieces = new() { Content = "Reset all pieces", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _saveBtn = new() { Content = "Save edits", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _editBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 4) };
    private PreviewScene? _scene;
    private EdObjective? _lastO;
    // riquadro di scelta (Shift+trascina)
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Rectangle _box = new()
    {
        Stroke = Brushes.Yellow, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 220, 0)), Visibility = Visibility.Collapsed,
    };
    private Point? _boxStart; private bool _boxPointsOnly, _boxAdd;
    private double _ex = 1;
    private readonly Dictionary<ModelVisual3D, int> _pieceOf = new();      // modello → numero del pezzo
    private readonly Dictionary<int, ModelVisual3D> _pieceVis = new();      // numero del pezzo → modello
    private readonly List<int> _sel = new();                                 // pezzi scelti (l'ultimo = quello cliccato per ultimo)
    private readonly Dictionary<int, ModelVisual3D> _selBox = new();        // riquadri gialli
    private bool _dragPiece; private Point3D _grab; private double _planeY;
    private Dictionary<int, (double N, double E)> _grabPos = new();         // posizioni dei pezzi scelti all'inizio del trascinamento
    private readonly Stack<List<(int Key, object? Prev)>> _undo = new();    // un passo = tutti i pezzi/punti toccati insieme (Prev: PieceEdit/PointEdit o null)
    /// <summary>Chiave dei punti (PDX) nella scelta: PointKey + numero del punto; sotto = numero del pezzo (FED).</summary>
    private const int PointKey = 1_000_000;

    /// <summary>Pezzi cambiati (l'editor aggiorna mappa e lista).</summary>
    public event Action? PiecesChanged;
    /// <summary>"Save edits" premuto qui.</summary>
    public event Action? SaveRequested;
    public Preview3DWindow(EditorData d, EdObjective o)
    {
        _d = d; _o = o;
        Width = 1200; Height = 850;
        _view.Camera = _cam;
        _view.ClipToBounds = true;
        foreach (var km in new[] { 1, 2, 4, 8 }) _area.Items.Add($"{km * 2} km");
        _area.SelectedIndex = 1;

        var refresh = new Button { Content = "Refresh", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        var top = new Button { Content = "From above", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        var pieces = new Button { Content = "Pieces…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Piece inspector: every building of this objective with its 3D model, textures and data (hit points, damage, repair time).\nWith Edit pieces: double-click a piece to open it." };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(refresh);
        bar.Children.Add(top);
        bar.Children.Add(pieces);
        bar.Children.Add(new TextBlock { Text = "Area ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_area);
        bar.Children.Add(_exag);
        bar.Children.Add(_photo);
        bar.Children.Add(_night);
        bar.Children.Add(_water);
        bar.Children.Add(_edit);
        bar.Children.Add(new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Text = "Yellow ring = selected objective." });
        _legend.Inlines.Add(new System.Windows.Documents.Run("Move the view:  ") { FontWeight = FontWeights.SemiBold });
        _legend.Inlines.Add(new System.Windows.Documents.Run(
            "W / S = forward / back   ·   A / D = left / right   ·   R / F (or Page Up / Page Down) = up / down   ·   " +
            "arrows = turn left/right, tilt up/down" + " (in Edit pieces, with a piece selected, the arrows move the piece)   ·   + / − or wheel = closer / farther   ·   " +
            "keep the key pressed to keep moving, Shift = faster   ·   Home = back to the objective   ·   mouse: left drag = orbit, right drag = pan"));
        _editBar.Children.Add(_remove);
        _editBar.Children.Add(_group);
        _editBar.Children.Add(_restore);
        _editBar.Children.Add(_resetPieces);
        _editBar.Children.Add(_saveBtn);
        _editBar.Children.Add(_hint);
        _editBar.Visibility = Visibility.Collapsed;
        _hint.Text = "Coloured cylinders = vehicle points (red SAM, orange AAA, pink radar, green parking, grey others). Click a piece or point = select (yellow box), Shift+drag a box = select what is inside (one SAM/AAA ring; Shift+Alt = only vehicle points, Ctrl+Shift = add), double-click a point = its whole list, double-click a piece = piece inspector, Ctrl+click = add/remove more (they move and turn together), drag = move on the ground, " +
                     "Q/E = turn 5° (Shift 15°, Ctrl 1°), arrows = move 10 ft (Shift 50 ft), Del = remove, Ctrl+A = all, Ctrl+Z = undo, Esc = deselect.";

        var viewGrid = new Grid();
        viewGrid.Children.Add(_view);
        viewGrid.Children.Add(_overlay);
        _overlay.Children.Add(_box);
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(0x9C, 0xB8, 0xD6)), Child = viewGrid };
        _sky = border;
        var root = new DockPanel();
        var tops = new StackPanel();
        tops.Children.Add(bar);
        tops.Children.Add(_editBar);
        DockPanel.SetDock(tops, Dock.Top); root.Children.Add(tops);
        var sb = new Border { Child = _status, Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)) };
        DockPanel.SetDock(sb, Dock.Bottom); root.Children.Add(sb);
        var lg = new Border { Child = _legend, Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF0)), BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(lg, Dock.Bottom); root.Children.Add(lg);
        root.Children.Add(border);
        Content = root;

        refresh.Click += (_, _) => Rebuild();
        pieces.Click += (_, _) => PieceInspector.Open(this, _d, _o, _sel.Count == 1 ? FeatureOf(_sel[0])?.CtIdx : null);
        top.Click += (_, _) => { _pitch = 89; _yaw = 180; UpdateCamera(); };
        _area.SelectionChanged += (_, _) => { if (IsLoaded) Rebuild(); };
        _exag.Checked += (_, _) => Rebuild();
        _exag.Unchecked += (_, _) => Rebuild();
        _photo.Checked += (_, _) => Rebuild();
        _night.Checked += (_, _) => Rebuild();
        _night.Unchecked += (_, _) => Rebuild();
        _photo.Unchecked += (_, _) => Rebuild();
        _water.Checked += (_, _) => Rebuild();
        _water.Unchecked += (_, _) => Rebuild();
        border.Focusable = true;
        border.MouseDown += (_, e) =>
        {
            border.Focus();
            _drag = e.GetPosition(border); _pan = e.ChangedButton != MouseButton.Left; _dragPiece = false;
            // Shift+trascina = riquadro: sceglie pezzi e punti dentro (Alt: solo i punti dei mezzi; Ctrl: aggiunge)
            if (e.ChangedButton == MouseButton.Left && EditOn && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _boxStart = e.GetPosition(_view);
                _boxPointsOnly = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                _boxAdd = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                Canvas.SetLeft(_box, _boxStart.Value.X); Canvas.SetTop(_box, _boxStart.Value.Y); _box.Width = _box.Height = 0;
                _box.Visibility = Visibility.Visible;
                border.CaptureMouse();
                return;
            }
            if (e.ChangedButton == MouseButton.Left && EditOn && PickPiece(e.GetPosition(_view)) is int num)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                // doppio clic su un punto = tutta la sua lista (tutte le batterie SAM, o tutti i cannoni AAA, dell'obiettivo);
                // una batteria sola (la corona intorno al radar): Shift+trascina un riquadro intorno
                if (num >= PointKey && !alt && e.ClickCount == 2)
                {
                    var grp = GroupOf(num);
                    if (ctrl && grp.All(_sel.Contains)) { foreach (var k in grp) _sel.Remove(k); ShowSelection(); _drag = null; border.CaptureMouse(); return; }
                    if (!ctrl && !grp.All(_sel.Contains)) _sel.Clear();
                    foreach (var k in grp) if (!_sel.Contains(k)) _sel.Add(k);
                    _sel.Remove(num); _sel.Add(num);
                    ShowSelection();
                }
                // Ctrl+clic = aggiungi/togli dalla scelta (come in Windows); clic su un pezzo gia' scelto = tieni il gruppo e trascinalo
                else if (ctrl)
                {
                    if (_sel.Contains(num)) { _sel.Remove(num); ShowSelection(); _drag = null; border.CaptureMouse(); return; }
                    _sel.Add(num);
                    ShowSelection();
                }
                else
                {
                    if (!_sel.Contains(num)) { _sel.Clear(); _sel.Add(num); }
                    else { _sel.Remove(num); _sel.Add(num); }
                    ShowSelection();
                    // doppio clic su un pezzo = ispettore (modello, texture, dati)
                    if (num < PointKey && e.ClickCount == 2 && FeatureOf(num) is EdFeature df)
                    {
                        _drag = null;
                        PieceInspector.Open(this, _d, _o, df.CtIdx);
                        return;
                    }
                }
                // trascinamento sul piano orizzontale alla quota della base del pezzo cliccato; tutti i pezzi scelti insieme
                if (_pieceVis.TryGetValue(num, out var vis) && vis.Transform is Transform3DGroup tg && tg.Children[1] is TranslateTransform3D tt
                    && RayToPlane(e.GetPosition(_view), tt.OffsetY) is Point3D hit)
                {
                    _dragPiece = true; _grab = hit; _planeY = tt.OffsetY;
                    _grabPos = _sel.Where(k => PosOf(k) != null).ToDictionary(k => k, k => (PosOf(k)!.Value.N, PosOf(k)!.Value.E));
                    PushUndo(_sel);
                }
            }
            border.CaptureMouse();
        };
        border.MouseUp += (_, e) =>
        {
            if (_boxStart is Point b0)
            {
                var b1 = e.GetPosition(_view);
                var r = new Rect(b0, b1);
                _box.Visibility = Visibility.Collapsed; _boxStart = null;
                if (!_boxAdd) _sel.Clear();
                if (r.Width > 3 && r.Height > 3)
                    foreach (var (k, vis) in _pieceVis)
                    {
                        if (_boxPointsOnly && k < PointKey) continue;
                        if (vis.Transform is Transform3DGroup tg && tg.Children[1] is TranslateTransform3D tt
                            && ToScreen(new Point3D(tt.OffsetX, tt.OffsetY, tt.OffsetZ)) is Point sp && r.Contains(sp) && !_sel.Contains(k)) _sel.Add(k);
                    }
                ShowSelection();
                _drag = null; border.ReleaseMouseCapture();
                return;
            }
            if (_dragPiece && _sel.Count > 0) { PiecesChanged?.Invoke(); ShowPieceInfo(); }
            _drag = null; _dragPiece = false; border.ReleaseMouseCapture();
        };
        border.MouseMove += (_, e) =>
        {
            if (_boxStart is Point bs)
            {
                var bp = e.GetPosition(_view);
                Canvas.SetLeft(_box, Math.Min(bs.X, bp.X)); Canvas.SetTop(_box, Math.Min(bs.Y, bp.Y));
                _box.Width = Math.Abs(bp.X - bs.X); _box.Height = Math.Abs(bp.Y - bs.Y);
                return;
            }
            if (_drag is not Point p0) return;
            if (_dragPiece)
            {
                if (RayToPlane(e.GetPosition(_view), _planeY) is Point3D q)
                {
                    // spostamento sulla scena (x est, z sud) → nord/est del teatro → disposizione (senza la rotazione dell'obiettivo)
                    double dE = q.X - _grab.X, dN = -(q.Z - _grab.Z);
                    double h = _o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
                    double ln = dN * ch + dE * sh, le = -dN * sh + dE * ch;
                    foreach (var k in _sel)
                        if (_grabPos.TryGetValue(k, out var g0) && PosOf(k) is { } cur) { SetPos(k, g0.N + ln, g0.E + le, cur.H); PlacePiece(k); }
                }
                return;
            }
            var p = e.GetPosition(border);
            double dx = p.X - p0.X, dy = p.Y - p0.Y;
            _drag = p;
            if (_pan)
            {
                double k = _dist / 600, y = _yaw * Math.PI / 180;
                // destra e avanti dello schermo sul piano del terreno
                _target = new Point3D(_target.X - (dx * Math.Cos(y) + dy * Math.Sin(y)) * k, _target.Y, _target.Z - (-dx * Math.Sin(y) + dy * Math.Cos(y)) * k);
            }
            else { _yaw += dx * 0.4; _pitch = Math.Clamp(_pitch + dy * 0.3, 3, 89); }
            UpdateCamera();
        };
        border.MouseWheel += (_, e) => { _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.85 : 1 / 0.85), 50, 60000); UpdateCamera(); };
        PreviewKeyDown += OnKey;
        PreviewKeyUp += (_, e) => { _held.Remove(e.Key); if (e.Key == Key.Add) _held.Remove(Key.OemPlus); };
        Deactivated += (_, _) => StopFlying();
        Closed += (_, _) => StopFlying();
        _edit.Checked += (_, _) => { _editBar.Visibility = Visibility.Visible; Rebuild(); };
        _edit.Unchecked += (_, _) => { _editBar.Visibility = Visibility.Collapsed; _sel.Clear(); Rebuild(); };
        _remove.Click += (_, _) => RemoveSelected();
        _group.Click += (_, _) =>
        {
            var pts = _sel.Where(k => k >= PointKey).ToList();
            if (pts.Count == 0) { _status.Text = "Select a point first (a coloured cylinder)."; return; }
            foreach (var k in pts.SelectMany(GroupOf).Distinct().ToList()) if (!_sel.Contains(k)) _sel.Add(k);
            ShowSelection();
        };
        _restore.Click += (_, _) =>
        {
            var list = _d.RemovedPieces(_o).ToList();
            if (list.Count == 0) { _status.Text = "No removed pieces."; return; }
            _undo.Push(list.Select(pe => (pe.Num, (object?)pe)).ToList());
            foreach (var pe in list) _o.Pieces[pe.Num] = pe with { Removed = false };
            _o.PieceVersion++; _o.PiecesDirty = true;
            PiecesChanged?.Invoke(); Rebuild();
        };
        _resetPieces.Click += (_, _) =>
        {
            if (_o.Pieces.Count == 0) { _status.Text = "No piece of this objective was changed."; return; }
            if (MessageBox.Show(this, $"Put all the pieces of {_o.Name} back as in the add-on?", "Edit pieces", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _d.ResetPieces(_o); _undo.Clear(); _sel.Clear();
            PiecesChanged?.Invoke(); Rebuild();
        };
        _saveBtn.Click += (_, _) => SaveRequested?.Invoke();
        Loaded += (_, _) => Rebuild();
    }

    /// <summary>Mostra un altro obiettivo (chiamato dall'editor).</summary>
    public void ShowObjective(EdObjective o) { _o = o; _dist = 3000; _sel.Clear(); _undo.Clear(); Rebuild(); Activate(); }

    private bool EditOn => _edit.IsChecked == true && EditorData.CanEditPieces(_o);

    private double HalfFt => new[] { 1, 2, 4, 8 }[Math.Max(0, _area.SelectedIndex)] * 1000 * 3.280839895;

    private void Rebuild()
    {
        Title = $"3D preview – {_o.Name}";
        Cursor = Cursors.Wait;
        try
        {
            double half = Math.Max(HalfFt, _d.LayoutOf(_o).Radius * 1.2);
            double ex = _exag.IsChecked == true ? 3 : 1;
            bool edit = _edit.IsChecked == true && EditorData.CanEditPieces(_o);
            _edit.IsEnabled = EditorData.CanEditPieces(_o) || _edit.IsChecked == true;
            if (_edit.IsChecked == true && !edit) _status.Text = "This objective's pieces cannot be edited (bridge, junction or replaced base not saved yet).";
            var s = PreviewScene.Build(_d, _o, half, 129, 2048, ex, separateSelected: edit);
            _scene = s; _ex = ex;
            _ground = s.GroundAtCenter;
            var group = new Model3DGroup();
            bool night = Night;
            group.Children.Add(new AmbientLight(night ? Color.FromRgb(28, 30, 40) : Color.FromRgb(95, 95, 95)));
            group.Children.Add(new DirectionalLight(night ? Color.FromRgb(38, 42, 58) : Color.FromRgb(165, 165, 158), new Vector3D(-0.5, -1, -0.3)));
            if (_sky != null) _sky.Background = new SolidColorBrush(night ? Color.FromRgb(8, 10, 22) : Color.FromRgb(0x9C, 0xB8, 0xD6));
            _waterMissing = false;
            group.Children.Add(Terrain(s, ex));
            foreach (var g in s.Groups.Values)
            {
                var gm = Mesh(g);
                if (gm != null) group.Children.Add(gm);
            }
            group.Children.Add(Ring(_d.LayoutOf(_o).Radius));
            group.Freeze();
            _view.Children.Clear();
            _view.Children.Add(new ModelVisual3D { Content = group });
            _lightsVis = null;
            if (night) { _lightsDist = _dist; _lightsVis = LightsVisual(s); if (_lightsVis != null) _view.Children.Add(_lightsVis); }
            _pieceOf.Clear(); _pieceVis.Clear(); _selBox.Clear();
            if (edit)
            {
                foreach (var f in _d.LayoutOf(_o).Features)
                {
                    var vis = PieceVisual(f, s.SelectedRunwayNumbers);
                    if (vis == null) continue;
                    _pieceOf[vis] = f.Num; _pieceVis[f.Num] = vis;
                    _view.Children.Add(vis);
                    PlacePiece(f.Num);
                }
                _sel.RemoveAll(n => !_pieceVis.ContainsKey(n) && n < PointKey);
            }
            // punti dove BMS mette i mezzi (lanciatori, cannoni, radar, camion, parcheggi): cilindretti colorati, sempre visibili;
            // si scelgono e si spostano solo con "Edit pieces"
            foreach (var q in _d.LayoutOf(_o).Points.Where(q => EditorData.CanEditPoint(q.T)))
            {
                var vis = PointVisual(q);
                _pieceOf[vis] = PointKey + q.Num; _pieceVis[PointKey + q.Num] = vis;
                _view.Children.Add(vis);
                PlacePiece(PointKey + q.Num);
            }
            if (edit)
            {
                _sel.RemoveAll(n => !_pieceVis.ContainsKey(n));
                ShowSelection();
                ShowPieceInfo();
            }
            if (_lastO != _o) { _target = new Point3D(0, 0, 0); _lastO = _o; }   // rifatta per una modifica: la telecamera resta dov'e'
            if (_dist == 3000) _dist = Math.Max(1500, _d.LayoutOf(_o).Radius * 2.5);
            UpdateCamera();
            _status.Text = $"{s.Objectives} objectives, {s.Triangles:N0} triangles, {s.Groups.Keys.Select(k => k.Tex).Where(t => t.Length > 0).Distinct().Count()} textures" +
                           (s.Leveled > 0 ? $", {s.Leveled} airbases leveled as in BMS" : "") +
                           (s.MissingModels > 0 ? $", {s.MissingModels} pieces without a readable model (drawn as boxes, list in Output\\preview_missing_models.txt)" : "") +
                           (s.Heights == null ? "  |  HeightMap.raw not found: flat ground" : "") +
                           (s.Photo == null ? "  |  no Photoreal tiles: grey ground" : "") +
                           (Night ? $"  |  night: {s.Lights.Count} model lights on, {s.Pools.Count} lit areas on the ground" : "") + (_waterMissing ? "  |  water: HeightMap.raw / WaterHeightMap.raw not found" : _water.IsChecked == true ? "  |  blue = BMS water, cyan = shore" : "") +
                           $"  |  ground {s.GroundAtCenter:0} ft";
            if (edit) ShowPieceInfo();
            else
            {
                int np = _pieceVis.Keys.Count(k => k >= PointKey);
                _status.Text += np > 0 ? $"  |  {np} vehicle points (coloured cylinders): tick 'Edit pieces' to move them" : "  |  this layout has no vehicle points";
            }
        }
        catch (Exception ex) { _status.Text = "3D preview failed: " + ex.Message; }
        finally { Cursor = null; }
        try
        {
            if (_d.ModelErrors.Count > 0)
                Directory.CreateDirectory(Path.Combine(_d.Project.ProjectFolder, "Output"));
            if (_d.ModelErrors.Count > 0)
                File.WriteAllLines(Path.Combine(_d.Project.ProjectFolder, "Output", "preview_missing_models.txt"),
                    _d.ModelErrors.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key};{kv.Value.Name};{kv.Value.Why}"));
        }
        catch (IOException) { }
    }

    /// <summary>Anello giallo a terra intorno all'obiettivo scelto (al posto di colorarne i modelli).</summary>
    private static GeometryModel3D Ring(double r)
    {
        r = Math.Max(150, r * 1.05);
        double w = Math.Max(8, r * 0.015);
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(); var idx = new Int32Collection();
        const int n = 96;
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pos.Add(new Point3D(Math.Sin(a) * r, 3, -Math.Cos(a) * r));
            pos.Add(new Point3D(Math.Sin(a) * (r + w), 3, -Math.Cos(a) * (r + w)));
            if (i < n) { int k = 2 * i; idx.Add(k); idx.Add(k + 1); idx.Add(k + 2); idx.Add(k + 1); idx.Add(k + 3); idx.Add(k + 2); }
        }
        m.Positions = pos; m.TriangleIndices = idx; m.Freeze();
        var mat = new EmissiveMaterial(Frozen(new SolidColorBrush(Color.FromRgb(255, 220, 0)))); mat.Freeze();
        return new GeometryModel3D(m, mat) { BackMaterial = mat };
    }

    private bool _waterMissing;
    private GeometryModel3D Terrain(PreviewScene s, double ex) => Terrain(s, ex, _photo.IsChecked == true, _water.IsChecked == true);

    private GeometryModel3D Terrain(PreviewScene s, double ex, bool usePhoto, bool water)
    {
        int n = s.GridN;
        var m = new MeshGeometry3D();
        var pos = new Point3DCollection(n * n); var uv = new PointCollection(n * n); var idx = new Int32Collection((n - 1) * (n - 1) * 6);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double east = -s.Half + 2 * s.Half * i / (n - 1), north = s.Half - 2 * s.Half * j / (n - 1);
                double h = s.Heights == null ? _ground : s.Heights[j, i];
                pos.Add(new Point3D(east, (h - _ground) * ex - 2, -north));   // 2 piedi sotto, per non coprire le basi dei modelli
                uv.Add(new Point((double)i / (n - 1), (double)j / (n - 1)));
            }
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        m.Positions = pos; m.TextureCoordinates = uv; m.TriangleIndices = idx;
        m.Freeze();
        Brush br = new SolidColorBrush(usePhoto ? Color.FromRgb(150, 140, 120) : Color.FromRgb(178, 176, 168));
        byte[]? photo = usePhoto ? s.Photo : null;
        if (water)
        {
            // acqua di BMS sopra la foto (stessi pixel di PhotoArea)
            int px = s.PhotoPx;
            photo ??= Enumerable.Repeat((byte)(usePhoto ? 150 : 176), px * px * 4).ToArray();
            photo = (byte[])photo.Clone();
            try
            {
                if (_d.WaterOverlay(s.CenterX, s.CenterY, s.Half, px) is { } wo)
                    for (int k = 0; k < wo.Length; k += 4)
                    {
                        int a = wo[k + 3];
                        if (a == 0) continue;
                        for (int c = 0; c < 3; c++) photo[k + c] = (byte)((photo[k + c] * (255 - a) + wo[k + c] * a) / 255);
                        photo[k + 3] = 255;
                    }
                else _waterMissing = true;
            }
            catch (Exception) { _waterMissing = true; }
        }
        if (Night)
        {
            // notte: foto (o grigio) molto scura, un po' blu
            int px = s.PhotoPx;
            photo = photo == null ? Enumerable.Repeat((byte)176, px * px * 4).ToArray() : (byte[])photo.Clone();
            for (int k = 0; k + 3 < photo.Length; k += 4)
            {
                photo[k] = (byte)(photo[k] * 0.30); photo[k + 1] = (byte)(photo[k + 1] * 0.24); photo[k + 2] = (byte)(photo[k + 2] * 0.22); photo[k + 3] = 255;
            }
        }
        if (photo != null)
        {
            var bmp = BitmapSource.Create(s.PhotoPx, s.PhotoPx, 96, 96, PixelFormats.Bgra32, null, photo, s.PhotoPx * 4);
            bmp.Freeze();
            br = new ImageBrush(bmp);
        }
        br.Freeze();
        var mat = new DiffuseMaterial(br);
        return new GeometryModel3D(m, mat) { BackMaterial = mat };
    }

    /// <summary>
    /// The base seen from straight above (orthographic), with the real BMS models and textures on the ground: the drawing of the
    /// airbase (runways, taxiways, buildings). Square image of 2·half feet around the objective, north up. Used under the point lists.
    /// </summary>
    public static BitmapSource? TopDown(EditorData d, EdObjective o, double half, int px, bool photo)
    {
        var w = new Preview3DWindow(d, o);
        try
        {
            var s = PreviewScene.Build(d, o, half, 65, 2048, 1, false);
            w._ground = s.GroundAtCenter;
            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(185, 185, 185)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(110, 110, 104), new Vector3D(-0.4, -1, -0.3)));
            group.Children.Add(w.Terrain(s, 1, photo, false));
            foreach (var g in s.Groups.Values) if (w.Mesh(g) is { } gm) group.Children.Add(gm);
            group.Freeze();
            var vp = new Viewport3D
            {
                Width = px, Height = px, ClipToBounds = true,
                // dall'alto: su dello schermo = -Z (nord), destra = +X (est)
                Camera = new OrthographicCamera(new Point3D(0, 40000, 0), new Vector3D(0, -1, 0), new Vector3D(0, 0, -1), 2 * half) { NearPlaneDistance = 10, FarPlaneDistance = 120000 },
            };
            vp.Children.Add(new ModelVisual3D { Content = group });
            vp.Measure(new Size(px, px)); vp.Arrange(new Rect(0, 0, px, px)); vp.UpdateLayout();
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(vp);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception) { return null; }
        finally { try { w.Close(); } catch (InvalidOperationException) { } }
    }

    private readonly Dictionary<string, Brush> _texBrush = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Brush Untextured = Frozen(new SolidColorBrush(Color.FromRgb(200, 200, 195)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private bool Night => _night.IsChecked == true;

    private readonly Dictionary<string, Brush?> _emiBrush = new(StringComparer.OrdinalIgnoreCase);
    private Brush? EmiBrush(string id)
    {
        if (_emiBrush.TryGetValue(id, out var b)) return b;
        b = null;
        if (_d.EmissionTexture(id) is { } tx)
        {
            var bmp = BitmapSource.Create(tx.W, tx.H, 96, 96, PixelFormats.Bgra32, null, tx.Bgra, tx.W * 4);
            bmp.Freeze();
            b = new ImageBrush(bmp) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            b.Freeze();
        }
        _emiBrush[id] = b;
        return b;
    }

    /// <summary>
    /// Luci dei modelli di notte: un piccolo ottaedro luminoso per luce, del colore della luce. In BMS sono sprite che restano
    /// visibili da lontano: qui la grandezza cresce con la distanza della telecamera (si rifanno quando lo zoom cambia molto).
    /// </summary>
    private ModelVisual3D? LightsVisual(PreviewScene s)
    {
        if (s.Lights.Count == 0 && s.Pools.Count == 0) return null;
        double minR = _dist / 450;
        var groups = new Dictionary<(int, int, int), (Point3DCollection P, Int32Collection I, Color C)>();
        foreach (var (x, y, z, cr, cg, cb, sz) in s.Lights.Take(40000))
        {
            var key = (cr >> 5, cg >> 5, cb >> 5);
            if (!groups.TryGetValue(key, out var gr))
                groups[key] = gr = (new Point3DCollection(), new Int32Collection(), Color.FromRgb((byte)Math.Max((int)cr, 40), (byte)Math.Max((int)cg, 40), (byte)Math.Max((int)cb, 40)));
            double rr = Math.Max(sz / 2, minR);
            int b0 = gr.P.Count;
            gr.P.Add(new Point3D(x + rr, y, z)); gr.P.Add(new Point3D(x - rr, y, z)); gr.P.Add(new Point3D(x, y + rr, z));
            gr.P.Add(new Point3D(x, y - rr, z)); gr.P.Add(new Point3D(x, y, z + rr)); gr.P.Add(new Point3D(x, y, z - rr));
            int[] f = { 0, 2, 4, 4, 2, 1, 1, 2, 5, 5, 2, 0, 4, 3, 0, 1, 3, 4, 5, 3, 1, 0, 3, 5 };
            foreach (int k in f) gr.I.Add(b0 + k);
        }
        var mg = new Model3DGroup();
        // chiazze di luce a terra (lampioni, fari): i triangoli del modello, sfumati dal lampione verso il bordo (u = sfumatura)
        var pools = new Dictionary<(byte, byte, byte, byte), (Point3DCollection P, PointCollection Uv)>();
        foreach (var (cr, cg, cb, ca, wp, fade) in s.Pools)
        {
            var key = (cr, cg, cb, ca);
            if (!pools.TryGetValue(key, out var pg)) pools[key] = pg = (new Point3DCollection(), new PointCollection());
            for (int k = 0, v = 0; k + 2 < wp.Length; k += 3, v++)
            {
                pg.P.Add(new Point3D(wp[k], wp[k + 1], wp[k + 2]));
                pg.Uv.Add(new Point(Math.Clamp(fade[v], 0, 1), 0.5));
            }
        }
        foreach (var ((cr, cg, cb, ca), (pp, uv)) in pools)
        {
            var mesh = new MeshGeometry3D { Positions = pp, TextureCoordinates = uv }; mesh.Freeze();
            byte a0 = (byte)Math.Min(255, ca * 3);   // in BMS si somma alla luce: qui un po' piu' forte per vederla
            var br = new LinearGradientBrush(Color.FromArgb(a0, cr, cg, cb), Color.FromArgb(0, cr, cg, cb), new Point(0, 0.5), new Point(1, 0.5))
            { MappingMode = BrushMappingMode.Absolute };
            br.Freeze();
            var mat = new MaterialGroup { Children = { new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))), new EmissiveMaterial(br) } };
            mat.Freeze();
            mg.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }
        foreach (var (pts, idx, c) in groups.Values)
        {
            var mesh = new MeshGeometry3D { Positions = pts, TriangleIndices = idx }; mesh.Freeze();
            var mat = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(new SolidColorBrush(c)) } };
            mat.Freeze();
            mg.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }
        mg.Freeze();
        return new ModelVisual3D { Content = mg };
    }

    private Brush? TexBrush(string id)
    {
        if (_texBrush.TryGetValue(id, out var b)) return b;
        var t = _d.Texture(id);
        if (t is not { } tx) return null;
        var bmp = BitmapSource.Create(tx.W, tx.H, 96, 96, PixelFormats.Bgra32, null, tx.Bgra, tx.W * 4);
        bmp.Freeze();
        // le coordinate di texture dei modelli possono uscire da 0..1: si ripete la texture
        b = new ImageBrush(bmp) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        b.Freeze();
        _texBrush[id] = b;
        return b;
    }

    private GeometryModel3D? Mesh(PreviewScene.Group g)
    {
        var t = g.Pos;
        if (t.Count < 9) return null;
        var m = new MeshGeometry3D();
        int nv = t.Count / 3;
        var pos = new Point3DCollection(nv); var idx = new Int32Collection(nv);
        for (int i = 0; i < t.Count; i += 3)
        {
            pos.Add(new Point3D(t[i], t[i + 1], t[i + 2]));
            idx.Add(idx.Count);
        }
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
        Material mat = new DiffuseMaterial(br);
        if (Night && g.Tex != null && m.TextureCoordinates.Count > 0 && EmiBrush(g.Tex) is { } eb)
            mat = new MaterialGroup { Children = { mat, new EmissiveMaterial(eb) } };   // parti che si illuminano (finestre, insegne)
        mat.Freeze();
        return new GeometryModel3D(m, mat) { BackMaterial = mat };
    }

    // ───────────────────────── modifica dei pezzi ─────────────────────────

    private EdFeature? FeatureOf(int key) => key < 0 || key >= PointKey ? null : _d.LayoutOf(_o).Features.FirstOrDefault(f => f.Num == key);
    private EdPoint? PointOf(int key) => key < PointKey ? null : _d.LayoutOf(_o).Points.FirstOrDefault(q => q.Num == key - PointKey);

    /// <summary>Posizione (nella disposizione) e direzione di un pezzo o di un punto.</summary>
    private (double N, double E, double H)? PosOf(int key) =>
        FeatureOf(key) is EdFeature f ? (f.N, f.E, f.Heading) : PointOf(key) is EdPoint q ? (q.N, q.E, q.Heading) : null;

    private void SetPos(int key, double n, double e, double h)
    {
        if (FeatureOf(key) is EdFeature f) _d.SetPiece(_o, f, n, e, h, false);
        else if (PointOf(key) is EdPoint q) _d.SetPoint(_o, q, n, e, h);
    }

    private object? PrevOf(int key) =>
        key >= PointKey ? (_o.PointMoves.TryGetValue(key - PointKey, out var pm) ? pm : null) : (_o.Pieces.TryGetValue(key, out var pe) ? pe : null);

    private static readonly Dictionary<int, Brush> PointBrushes = new();
    /// <summary>Colori come nella mappa: verde parcheggi, rosso SAM, arancione AAA, rosa radar, grigio chiaro il resto.</summary>
    private static Brush PointBrush(int t)
    {
        if (PointBrushes.TryGetValue(t, out var b)) return b;
        var c = t switch { 11 or 12 or 16 => Color.FromRgb(90, 255, 90), 4 => Color.FromRgb(255, 60, 60), 5 or 6 or 20 => Color.FromRgb(255, 170, 0), 7 or 10 => Color.FromRgb(255, 80, 220), _ => Color.FromRgb(225, 225, 225) };
        b = Frozen(new SolidColorBrush(c));
        PointBrushes[t] = b;
        return b;
    }

    /// <summary>Cilindretto (diametro 24 ft, alto 14 ft) con una tacca davanti, colorato per tipo: un punto della disposizione.</summary>
    private ModelVisual3D PointVisual(EdPoint q)
    {
        var pos = new Point3DCollection(); var idx = new Int32Collection();
        const int n = 10; const double r = 12, h = 14;
        void T(Point3D a, Point3D b, Point3D c) { idx.Add(pos.Count); pos.Add(a); idx.Add(pos.Count); pos.Add(b); idx.Add(pos.Count); pos.Add(c); }
        for (int i = 0; i < n; i++)
        {
            double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
            var b0 = new Point3D(Math.Sin(a0) * r, 0, -Math.Cos(a0) * r); var b1 = new Point3D(Math.Sin(a1) * r, 0, -Math.Cos(a1) * r);
            var t0 = new Point3D(b0.X, h, b0.Z); var t1 = new Point3D(b1.X, h, b1.Z);
            T(b0, t0, t1); T(b0, t1, b1); T(new Point3D(0, h, 0), t1, t0);
        }
        // tacca davanti (direzione del punto): -z
        T(new Point3D(-4, h + 0.5, -r + 2), new Point3D(0, h + 0.5, -r - 10), new Point3D(4, h + 0.5, -r + 2));
        var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx }; mesh.Freeze();
        var mg = new MaterialGroup();
        mg.Children.Add(new DiffuseMaterial(PointBrush(q.T)));
        mg.Children.Add(new EmissiveMaterial(Frozen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)))));
        mg.Freeze();
        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0)));
        tg.Children.Add(new TranslateTransform3D());
        return new ModelVisual3D { Content = new GeometryModel3D(mesh, mg) { BackMaterial = mg }, Transform = tg };
    }

    /// <summary>Modello di un pezzo nel suo sistema (x destra, y su, z indietro); la posizione la mette PlacePiece.</summary>
    private ModelVisual3D? PieceVisual(EdFeature f, Dictionary<int, int> rwy)
    {
        var parts = PreviewScene.PieceParts(_d, _o, f, rwy);
        if (parts.Count == 0)
        {
            // pezzo senza superfici (luci): una piccola scatola, per poterlo comunque scegliere
            var b = new List<float>();
            foreach (var (x, y, z) in BoxTris(-15, 15, 0, 6, -15, 15)) { b.Add((float)x); b.Add((float)y); b.Add((float)z); }
            parts.Add((null, b.ToArray(), Array.Empty<float>()));
        }
        var g = new Model3DGroup();
        foreach (var (tex, pos, uv) in parts)
        {
            var grp = new PreviewScene.Group { Tex = tex };
            grp.Pos.AddRange(pos); grp.Uv.AddRange(uv);
            if (Mesh(grp) is GeometryModel3D gm) g.Children.Add(gm);
        }
        if (g.Children.Count == 0) return null;
        g.Freeze();
        var tg = new Transform3DGroup();
        tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0)));
        tg.Children.Add(new TranslateTransform3D());
        return new ModelVisual3D { Content = g, Transform = tg };
    }

    /// <summary>Mette il modello del pezzo (o del punto) dove dice la disposizione dell'obiettivo (con le modifiche).</summary>
    private void PlacePiece(int key)
    {
        if (_scene == null || !_pieceVis.TryGetValue(key, out var vis)) return;
        double n, e, hdg, up;
        if (FeatureOf(key) is EdFeature f)
        {
            (n, e, hdg) = (f.N, f.E, f.Heading);
            var (fx, fy) = EditorData.ToWorld(_o, n, e);
            up = _scene.PieceBase(_d, _o, f, fx, fy, _ex);
        }
        else if (PointOf(key) is EdPoint q)
        {
            (n, e, hdg) = (q.N, q.E, q.Heading);
            var (qx, qy) = EditorData.ToWorld(_o, n, e);
            up = (_scene.Ground(qx, qy) - _scene.GroundAtCenter) * _ex + 0.5;
        }
        else return;
        var (cx, cy) = EditorData.ToWorld(_o, n, e);
        var tg = (Transform3DGroup)vis.Transform;
        ((AxisAngleRotation3D)((RotateTransform3D)tg.Children[0]).Rotation).Angle = -(_o.Heading + hdg);
        var tt = (TranslateTransform3D)tg.Children[1];
        tt.OffsetX = cy - _scene.CenterY; tt.OffsetY = up; tt.OffsetZ = -(cx - _scene.CenterX);
    }

    private int? PickPiece(Point p)
    {
        int? found = null;
        VisualTreeHelper.HitTest(_view, null, r =>
        {
            if (r is RayHitTestResult rr)
            {
                DependencyObject? v = rr.VisualHit;
                while (v != null && !(v is ModelVisual3D mv && _pieceOf.ContainsKey(mv))) v = v is Visual3D ? VisualTreeHelper.GetParent(v) : null;
                if (v is ModelVisual3D m && _pieceOf.TryGetValue(m, out int n)) found = n;
                return HitTestResultBehavior.Stop;   // il primo colpito (il piu' vicino): se e' il terreno, niente
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    /// <summary>Il punto e' in una lista di una batteria (PHD 4 = SAM, 5 = artiglieria, 6 = AAA)?</summary>
    private bool IsBattery(int key)
    {
        int num = key - PointKey;
        return _d.LayoutOf(_o).Paths.FirstOrDefault(p => p.Idx.Contains(num)) is EdPath p && p.Type is 4 or 5 or 6;
    }

    /// <summary>Chiavi dei punti della stessa lista (PHD) del punto: la batteria intera.</summary>
    private List<int> GroupOf(int key)
    {
        int num = key - PointKey;
        var path = _d.LayoutOf(_o).Paths.FirstOrDefault(p => p.Idx.Contains(num));
        if (path == null) return new List<int> { key };
        return path.Idx.Select(i => PointKey + i).Where(_pieceVis.ContainsKey).ToList();
    }

    private List<EdFeature> Selected() => _sel.Select(FeatureOf).Where(f => f != null).Select(f => f!).ToList();

    private void PushUndo(IEnumerable<int> keys) => _undo.Push(keys.Select(k => (k, PrevOf(k))).ToList());

    /// <summary>Riquadri gialli sui pezzi scelti (piu' acceso sull'ultimo cliccato).</summary>
    private void ShowSelection()
    {
        foreach (var (num, box) in _selBox) if (_pieceVis.TryGetValue(num, out var v)) v.Children.Remove(box);
        _selBox.Clear();
        for (int i = 0; i < _sel.Count; i++)
        {
            int num = _sel[i];
            if (!_pieceVis.TryGetValue(num, out var vis) || vis.Content is not Model3D m) continue;
            var b = m.Bounds;
            double pad = Math.Max(3, Math.Max(b.SizeX, b.SizeZ) * 0.04);
            var pos = new Point3DCollection(); var idx = new Int32Collection();
            foreach (var (x, y, z) in BoxTris(b.X - pad, b.X + b.SizeX + pad, b.Y, b.Y + b.SizeY + pad, b.Z - pad, b.Z + b.SizeZ + pad)) { idx.Add(pos.Count); pos.Add(new Point3D(x, y, z)); }
            var mesh = new MeshGeometry3D { Positions = pos, TriangleIndices = idx }; mesh.Freeze();
            byte a = (byte)(i == _sel.Count - 1 ? 80 : 50);
            var mg = new MaterialGroup();
            mg.Children.Add(new DiffuseMaterial(Frozen(new SolidColorBrush(Color.FromArgb(a, 255, 220, 0)))));
            mg.Children.Add(new EmissiveMaterial(Frozen(new SolidColorBrush(Color.FromArgb(a, 255, 220, 0)))));
            mg.Freeze();
            var box = new ModelVisual3D { Content = new GeometryModel3D(mesh, mg) { BackMaterial = mg } };
            vis.Children.Add(box);
            _selBox[num] = box;
        }
        _remove.IsEnabled = _sel.Count > 0;
        ShowPieceInfo();
    }

    private void ShowPieceInfo()
    {
        int changed = _o.Pieces.Values.Count(p => !p.IsNoOp), gone = _o.Pieces.Values.Count(p => p.Removed), pts = _o.PointMoves.Values.Count(p => !p.IsNoOp);
        int nPieces = _pieceVis.Keys.Count(k => k < PointKey), nPts = _pieceVis.Count - nPieces;
        string sum = $"{_o.Name}: {nPieces} pieces ({changed} changed, {gone} removed), {nPts} vehicle points ({pts} moved)" + (_o.PiecesDirty ? " (not saved)" : "");
        if (_sel.Count > 1) _status.Text = $"{_sel.Count} selected (they move and turn together)   |   {sum}";
        else if (_sel.Count == 1 && FeatureOf(_sel[0]) is EdFeature f)
        {
            string st = _o.Pieces.TryGetValue(f.Num, out var pe) && !pe.IsNoOp
                ? $", moved {Math.Sqrt((pe.N - pe.OrigN) * (pe.N - pe.OrigN) + (pe.E - pe.OrigE) * (pe.E - pe.OrigE)) * 0.3048:0} m, turned {((pe.Heading - pe.OrigHeading) % 360 + 540) % 360 - 180:+0;-0;0}°" : "";
            _status.Text = $"Piece {f.Num}: {f.Name}, heading {f.Heading:0}°{st}   |   {sum}";
        }
        else if (_sel.Count == 1 && PointOf(_sel[0]) is EdPoint q)
        {
            string st = _o.PointMoves.TryGetValue(q.Num, out var pm) && !pm.IsNoOp
                ? $", moved {Math.Sqrt((pm.N - pm.OrigN) * (pm.N - pm.OrigN) + (pm.E - pm.OrigE) * (pm.E - pm.OrigE)) * 0.3048:0} m" : "";
            _status.Text = $"Point {q.Num}: {EditorMap.PointName(q.T)}, heading {q.Heading:0}°{st}   |   {sum}";
        }
        else _status.Text = sum;
    }

    private void RemoveSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) { _status.Text = "Vehicle points cannot be removed (BMS counts them): move them instead."; return; }
        PushUndo(sel.Select(f => f.Num));
        foreach (var f in sel)
        {
            _d.SetPiece(_o, f, f.N, f.E, f.Heading, true);
            if (_pieceVis.TryGetValue(f.Num, out var vis)) { _view.Children.Remove(vis); _pieceVis.Remove(f.Num); _pieceOf.Remove(vis); }
        }
        _sel.RemoveAll(k => k < PointKey); ShowSelection();
        PiecesChanged?.Invoke();
        ShowPieceInfo();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var step = _undo.Pop();
        bool rebuild = false;
        foreach (var (key, prev) in step)
        {
            if (key >= PointKey)
            {
                if (prev is PointEdit pm) _o.PointMoves[key - PointKey] = pm; else _o.PointMoves.Remove(key - PointKey);
                continue;
            }
            bool wasRemoved = _o.Pieces.TryGetValue(key, out var cur) && cur.Removed;
            if (prev is PieceEdit pe) _o.Pieces[key] = pe; else _o.Pieces.Remove(key);
            rebuild |= wasRemoved || (prev is PieceEdit p2 && p2.Removed);
        }
        _o.PieceVersion++; _o.PiecesDirty = true;
        PiecesChanged?.Invoke();
        _sel.Clear(); _sel.AddRange(step.Select(x => x.Key));
        if (rebuild) { Rebuild(); return; }
        foreach (var (key, _) in step) PlacePiece(key);
        ShowSelection();
    }

    // ── telecamera da tastiera: movimento continuo finche' il tasto resta premuto ──
    private readonly HashSet<Key> _held = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private bool _flying;
    private static readonly Key[] NavKeys = { Key.W, Key.S, Key.A, Key.D, Key.R, Key.F, Key.PageUp, Key.PageDown,
                                              Key.Add, Key.OemPlus, Key.Subtract, Key.OemMinus, Key.Left, Key.Right, Key.Up, Key.Down };

    /// <summary>Tasti della telecamera (sempre attivi): W/S avanti/indietro, A/D lato, R/F su/giu', frecce gira/inclina, +/- zoom,
    /// Home = torna all'obiettivo. Si muove finche' il tasto e' premuto; Shift = piu' veloce.</summary>
    private bool NavKey(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return false;
        if (e.Key == Key.Home) { _target = new Point3D(0, 0, 0); UpdateCamera(); return true; }
        if (!NavKeys.Contains(e.Key)) return false;
        bool arrow = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
        if (arrow && EditOn && _sel.Count > 0) return false;   // con un pezzo scelto le frecce spostano il pezzo
        _held.Add(e.Key);
        if (!_flying)
        {
            _flying = true; _clock.Restart();
            CompositionTarget.Rendering += Fly;
        }
        return true;
    }

    private void StopFlying()
    {
        _held.Clear();
        if (_flying) { _flying = false; CompositionTarget.Rendering -= Fly; }
    }

    private void Fly(object? sender, EventArgs e)
    {
        if (_held.Count == 0) { StopFlying(); return; }
        double dt = Math.Min(0.1, _clock.Elapsed.TotalSeconds); _clock.Restart();
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool H(Key k) => _held.Contains(k);
        double move = _dist * (shift ? 1.5 : 0.35) * dt;        // per secondo: 35% della distanza (Shift 150%)
        var look = _cam.LookDirection; look.Normalize();
        var fwd = new Vector3D(look.X, 0, look.Z);
        if (fwd.Length < 1e-6) { double y = _yaw * Math.PI / 180; fwd = new Vector3D(Math.Sin(y), 0, Math.Cos(y)); }
        fwd.Normalize();
        var right = Vector3D.CrossProduct(fwd, new Vector3D(0, 1, 0)); right.Normalize();   // destra dello schermo (WPF: look x up)
        if (H(Key.W)) _target += fwd * move;
        if (H(Key.S)) _target -= fwd * move;
        if (H(Key.D)) _target += right * move;
        if (H(Key.A)) _target -= right * move;
        if (H(Key.R) || H(Key.PageUp)) _target.Y += move * 0.5;
        if (H(Key.F) || H(Key.PageDown)) _target.Y -= move * 0.5;
        double turn = (shift ? 120 : 40) * dt, tilt = (shift ? 80 : 25) * dt;   // gradi al secondo
        if (H(Key.Left)) _yaw -= turn;
        if (H(Key.Right)) _yaw += turn;
        if (H(Key.Up)) _pitch = Math.Clamp(_pitch + tilt, 3, 89);
        if (H(Key.Down)) _pitch = Math.Clamp(_pitch - tilt, 3, 89);
        double zoom = Math.Pow(shift ? 0.15 : 0.5, dt);          // per secondo: meta' distanza (Shift molto di piu')
        if (H(Key.Add) || H(Key.OemPlus)) _dist = Math.Clamp(_dist * zoom, 50, 60000);
        if (H(Key.Subtract) || H(Key.OemMinus)) _dist = Math.Clamp(_dist / zoom, 50, 60000);
        UpdateCamera();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
        if (NavKey(e)) { e.Handled = true; return; }
        if (!EditOn) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0, ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Z && ctrl) { Undo(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { _sel.Clear(); ShowSelection(); e.Handled = true; return; }
        if (e.Key == Key.A && ctrl) { _sel.Clear(); _sel.AddRange(_pieceVis.Keys); ShowSelection(); e.Handled = true; return; }
        var keys = _sel.Where(k => PosOf(k) != null).ToList();
        if (keys.Count == 0) return;
        switch (e.Key)
        {
            case Key.Delete: RemoveSelected(); e.Handled = true; return;
            case Key.Q: case Key.E:
            {
                // uno solo gira su se stesso; un gruppo gira intorno al suo centro, i pezzi restano dove sono l'uno rispetto all'altro
                double step = (ctrl ? 1 : shift ? 15 : 5) * (e.Key == Key.Q ? -1 : 1);
                var pos = keys.ToDictionary(k => k, k => PosOf(k)!.Value);
                double cn = pos.Values.Average(p => p.N), ce = pos.Values.Average(p => p.E);
                double a = step * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
                PushUndo(keys);
                foreach (var (k, p) in pos)
                {
                    double vn = p.N - cn, ve = p.E - ce;
                    // in senso orario visto dall'alto (come la direzione dei pezzi): nord → est
                    double nn = keys.Count == 1 ? p.N : cn + vn * ca - ve * sa, ne = keys.Count == 1 ? p.E : ce + vn * sa + ve * ca;
                    SetPos(k, nn, ne, p.H + step);
                }
                break;
            }
            case Key.Up: case Key.Down: case Key.Left: case Key.Right:
            {
                // rispetto alla telecamera: su = lontano, destra = destra dello schermo
                double step = shift ? 50 : 10, y = _yaw * Math.PI / 180;
                double fx = Math.Sin(y), fz = Math.Cos(y);            // avanti della telecamera sulla scena (x est, z sud)
                double rx = -fz, rz = fx;                              // destra
                (double ax, double az) = e.Key switch { Key.Up => (fx, fz), Key.Down => (-fx, -fz), Key.Right => (-rx, -rz), _ => (rx, rz) };
                double dE = ax * step, dN = -az * step;
                double h = _o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
                PushUndo(keys);
                foreach (var k in keys) { var p = PosOf(k)!.Value; SetPos(k, p.N + dN * ch + dE * sh, p.E - dN * sh + dE * ch, p.H); }
                break;
            }
            default: return;
        }
        e.Handled = true;
        foreach (var k in keys) PlacePiece(k);
        PiecesChanged?.Invoke();
        ShowPieceInfo();
    }

    /// <summary>Punto della scena → punto dello schermo (null se dietro la telecamera).</summary>
    private Point? ToScreen(Point3D p)
    {
        double W = _view.ActualWidth, H = _view.ActualHeight;
        if (W < 2 || H < 2) return null;
        var look = _cam.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, _cam.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, look); up.Normalize();
        var v = p - _cam.Position;
        double z = Vector3D.DotProduct(v, look);
        if (z <= 1) return null;
        double t = Math.Tan(_cam.FieldOfView * Math.PI / 360);
        double nx = Vector3D.DotProduct(v, right) / (z * t), ny = Vector3D.DotProduct(v, up) / (z * t * H / W);
        return new Point((nx + 1) / 2 * W, (1 - ny) / 2 * H);
    }

    /// <summary>Raggio dalla telecamera attraverso il punto dello schermo, fino al piano orizzontale y = planeY (coordinate della scena).</summary>
    private Point3D? RayToPlane(Point p, double planeY)
    {
        double W = _view.ActualWidth, H = _view.ActualHeight;
        if (W < 2 || H < 2) return null;
        var look = _cam.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, _cam.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, look); up.Normalize();
        double t = Math.Tan(_cam.FieldOfView * Math.PI / 360);   // FieldOfView di WPF = orizzontale
        double nx = 2 * p.X / W - 1, ny = 1 - 2 * p.Y / H;
        var dir = look + right * (nx * t) + up * (ny * t * H / W);
        if (Math.Abs(dir.Y) < 1e-6) return null;
        double k = (planeY - _cam.Position.Y) / dir.Y;
        if (k <= 0) return null;
        return _cam.Position + dir * k;
    }

    private static IEnumerable<(double, double, double)> BoxTris(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        (double, double, double)[] c = { (x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1), (x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1) };
        int[] idx = { 4, 6, 5, 4, 7, 6, 0, 1, 2, 0, 2, 3, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0 };
        foreach (int i in idx) yield return c[i];
    }

    private void UpdateCamera()
    {
        // di notte le luci si rifanno quando lo zoom cambia molto (grandezza minima legata alla distanza)
        if (Night && _scene != null && _lightsVis != null && (_dist > _lightsDist * 1.6 || _dist < _lightsDist / 1.6))
        {
            _lightsDist = _dist;
            int at = _view.Children.IndexOf(_lightsVis);
            var nv = LightsVisual(_scene);
            if (at >= 0 && nv != null) { _view.Children[at] = nv; _lightsVis = nv; }
        }
        double y = _yaw * Math.PI / 180, p = _pitch * Math.PI / 180;
        var dir = new Vector3D(Math.Sin(y) * Math.Cos(p), -Math.Sin(p), Math.Cos(y) * Math.Cos(p));
        _cam.Position = _target - dir * _dist;
        _cam.LookDirection = dir * _dist;
        // piano vicino lontano quanto si puo': la precisione della profondita' dipende da lontano/vicino (meno sfarfallio tra superfici quasi sovrapposte)
        _cam.NearPlaneDistance = Math.Max(5, _dist / 40);
        _cam.FarPlaneDistance = Math.Max(100000, _dist * 60);
    }
}
