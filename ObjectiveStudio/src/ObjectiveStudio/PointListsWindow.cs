using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Runways &amp; points: the point lists of an objective's layout (PHD headers + PDX points), as BMS uses them —
/// runway taxi routes (one per landing direction), runway outlines, SAM/AAA batteries, parking and other vehicle points.
/// Table of the lists with all their header fields, table of the points of the chosen list, and a map of the layout with
/// the chosen list drawn in order. Read only (vehicle points are moved in the 3D preview with "Edit pieces").
/// </summary>
public sealed class PointListsWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private readonly EditorData _d;
    private readonly EdObjective _o;
    private readonly EdLayout _l;
    private readonly DataGrid _lists = Grid0();
    private readonly DataGrid _points = Grid0();
    private readonly TextBlock _head = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), FontSize = 11.5 };
    private readonly ListMap _map;
    private readonly List<string> _treeModels;
    private readonly TextBlock _mapStatus = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };

    public sealed record ListRow(int Num, string Kind, int Points, string Runway, string Data, string World, string Pattern, string Texture, string DependsOn, EdPath Path);
    public sealed record PointRow(int Num, int Order, string Type, string North, string East, string Heading, string Flags);

    public static string ListKind(int t) => t switch
    {
        // nomi dell'editor di BMS (RunwayListType, SAMListType...)
        1 => "Runway (taxi route)", 2 => "Hot pit refuel", 4 => "SAM battery", 5 => "Artillery", 6 => "AAA battery", 8 => "Runway dimensions",
        10 => "Static radar", 11 => "Parking", 12 => "Runway (left)", 13 => "Runway (right)", 14 => "Helicopter", 15 => "Follow-me",
        16 => "Dock", 17 => "Track", 18 => "Engineer", 19 => "Generic", 20 => "Radar", 21 => "Trees", 22 => "No-go area",
        _ => $"List type {t}",
    };

    public PointListsWindow(EditorData d, EdObjective o)
    {
        _d = d; _o = o; _l = d.LayoutOf(o);
        Title = $"Runways, points & areas – {o.Name}";
        Width = 1350; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _map = new ListMap(o, _l);
        foreach (var f in _l.Features)
            if (int.TryParse(d.FeatureData(f.CtIdx).FirstOrDefault(x => x.Field == "Flags").Value, NumberStyles.Integer, Ci, out int fl) && (fl & 4) != 0)
                _map.Lit.Add(f.Num);
        try { var ar = d.AreasOf(o); _map.Areas = ar.Areas; _map.Trees = ar.Trees; _treeModels = ar.TreeModels; }
        catch (Exception ex) { _treeModels = new List<string> { "areas could not be read: " + ex.Message }; }

        _lists.Columns.Add(Col("List", nameof(ListRow.Num), 40));
        _lists.Columns.Add(Col("Kind", nameof(ListRow.Kind), 130));
        _lists.Columns.Add(Col("Pts", nameof(ListRow.Points), 40));
        _lists.Columns.Add(Col("Rwy", nameof(ListRow.Runway), 38, "RunwayNumber: which runway of the base (0 = first, 1 = second…); -1 = none"));
        _lists.Columns.Add(Col("Data", nameof(ListRow.Data), 55, "Data: for runway lists the landing direction in the layout (degrees)"));
        _lists.Columns.Add(Col("True hdg", nameof(ListRow.World), 60, "Landing direction on the map (Data + rotation of the objective), true degrees"));
        _lists.Columns.Add(Col("Pattern", nameof(ListRow.Pattern), 52, "LandingPattern of the header (-1, 0 or 1), as in the file"));
        _lists.Columns.Add(Col("Texture", nameof(ListRow.Texture), 52, "RunwayTexture: number painted on the runway end (magnetic heading / 10) when set"));
        _lists.Columns.Add(Col("Depends on (pieces)", nameof(ListRow.DependsOn), 260, "FeatureDependencyIdx: pieces that close this runway when destroyed"));
        _points.Columns.Add(Col("#", nameof(PointRow.Order), 34, "Position in the list"));
        _points.Columns.Add(Col("Point", nameof(PointRow.Num), 46, "Number in the PDX file"));
        _points.Columns.Add(Col("Type", nameof(PointRow.Type), 130));
        _points.Columns.Add(Col("North m", nameof(PointRow.North), 70, "From the centre of the layout, metres"));
        _points.Columns.Add(Col("East m", nameof(PointRow.East), 70));
        _points.Columns.Add(Col("Heading", nameof(PointRow.Heading), 60));
        _points.Columns.Add(Col("Flags", nameof(PointRow.Flags), 80, "1 = first point of the list, 2 = last"));

        var rows = _l.Paths.Select(Row).OrderBy(r => r.Num).ToList();
        _lists.ItemsSource = rows;
        _lists.SelectionChanged += (_, _) => ShowList();
        _points.SelectionChanged += (_, _) => { _map.SelPoint = (_points.SelectedItem as PointRow)?.Num ?? -1; _map.InvalidateVisual(); };
        _map.PointClicked += num =>
        {
            if (_points.ItemsSource is not List<PointRow> pr) return;
            var r = pr.FirstOrDefault(x => x.Num == num);
            if (r == null)
            {
                // il punto e' in un'altra lista: scegli quella
                var lr = rows.FirstOrDefault(x => x.Path.Idx.Contains(num));
                if (lr == null) return;
                _lists.SelectedItem = lr; _lists.ScrollIntoView(lr);
                r = ((List<PointRow>)_points.ItemsSource).FirstOrDefault(x => x.Num == num);
            }
            if (r != null) { _points.SelectedItem = r; _points.ScrollIntoView(r); }
        };

        int runways = _l.Paths.Where(p => p.Type == 1).Select(p => Field(p, "RunwayNumber")).Distinct().Count();
        _head.Text = $"{o.Name} · layout OCD {o.Ocd} · {_l.Paths.Count} lists, {_l.Points.Count} points" +
                     (runways > 0 ? $" · {runways} runway(s), {_l.Paths.Count(p => p.Type == 1)} landing directions" : "") +
                     AreasText() +
                     "\nRead only. Each runway has one taxi route per landing direction (takeoff, taxi, parking and hold points in order). " +
                     "Vehicle points (SAM, AAA, radar, parking…) are moved in the 3D preview with 'Edit pieces'. Click a point on the map to find it.";

        var left = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_head, Dock.Top); left.Children.Add(_head);
        var split = new Grid();
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        split.Children.Add(_lists);
        var gs = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Gainsboro };
        Grid.SetRow(gs, 1); split.Children.Add(gs);
        Grid.SetRow(_points, 2); split.Children.Add(_points);
        left.Children.Add(split);

        var mapBox = new DockPanel();
        var mapBar = new WrapPanel { Margin = new Thickness(6, 4, 6, 2) };
        var photo = new CheckBox { Content = "Ground photo", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var outlines = new CheckBox { Content = "Piece outlines", IsChecked = false, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Draw the footprint of every piece over the drawing" };
        var allPts = new CheckBox { Content = "All points", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        mapBar.Children.Add(photo); mapBar.Children.Add(outlines); mapBar.Children.Add(allPts);
        foreach (var (label, kind, tip) in new[]
        {
            ("Leveling", "Leveling", "TLD: where BMS flattens the ground (orange; full = 100% at the base height, lighter = 66%/33% blend)"),
            ("Land cover", "Land cover", "LCD: ground type around the objective (cyan dashed), e.g. GTP_AIRBASE"),
            ("Special", "Special", "SPD: special polygons of the objective (magenta)"),
            ("Trees", "Trees", "OTD: trees of the layout (dark green dots)"),
            ("Lit pieces", "Lit", "Pieces with 'Has Lights' (yellow): their model lights (runway, taxiway, approach…) are switched by the tower"),
        })
        {
            var cb = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            cb.Checked += (_, _) => { _map.Hidden.Remove(kind); _map.InvalidateVisual(); };
            cb.Unchecked += (_, _) => { _map.Hidden.Add(kind); _map.InvalidateVisual(); };
            mapBar.Children.Add(cb);
        }
        mapBar.Children.Add(_mapStatus);
        DockPanel.SetDock(mapBar, Dock.Top); mapBox.Children.Add(mapBar);
        photo.Checked += (_, _) => LoadDrawing(true); photo.Unchecked += (_, _) => LoadDrawing(false);
        outlines.Checked += (_, _) => { _map.Outlines = true; _map.InvalidateVisual(); };
        outlines.Unchecked += (_, _) => { _map.Outlines = false; _map.InvalidateVisual(); };
        allPts.Checked += (_, _) => { _map.AllPoints = true; _map.InvalidateVisual(); };
        allPts.Unchecked += (_, _) => { _map.AllPoints = false; _map.InvalidateVisual(); };
        Loaded += (_, _) => LoadDrawing(true);
        var legend = new TextBlock
        {
            Margin = new Thickness(6, 2, 6, 4), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Text = "The base from above with the real BMS models and textures. Orange = leveling, cyan dashed = land cover, magenta = special polygons, green dots = trees, yellow = pieces with lights. White dots = all points, coloured line = chosen list in order (green = first point, red = last). " +
                   "Wheel = zoom, drag = move, double-click = fit. North is up.",
        };
        DockPanel.SetDock(legend, Dock.Bottom); mapBox.Children.Add(legend);
        mapBox.Children.Add(new Border { Child = _map, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), ClipToBounds = true });

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(820) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(left);
        var vs = new GridSplitter { Width = 5, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Gainsboro };
        Grid.SetColumn(vs, 1); root.Children.Add(vs);
        Grid.SetColumn(mapBox, 2); root.Children.Add(mapBox);
        Content = root;

        if (rows.Count > 0) _lists.SelectedIndex = 0;
        else _head.Text += "\n\nThis layout has no point lists.";
    }

    private string AreasText()
    {
        var a = _map.Areas;
        var parts = new List<string>();
        var lv = a.Where(x => x.Kind == "Leveling").ToList();
        if (lv.Count > 0) parts.Add($"leveling: {lv.Count} polygon(s), {lv[0].Label.Split(',')[0]}");
        foreach (var g in a.Where(x => x.Kind == "Land cover").GroupBy(x => x.Label)) parts.Add($"land cover {g.Key} ({g.Count()})");
        int sp = a.Count(x => x.Kind == "Special");
        if (sp > 0) parts.Add($"{sp} special polygon(s)");
        if (_map.Trees.Count > 0) parts.Add($"{_map.Trees.Count} trees");
        if (_map.Lit.Count > 0) parts.Add($"{_map.Lit.Count} pieces with lights");
        string res = parts.Count == 0 ? "\nNo leveling, land cover, special polygons or trees for this objective." : "\nAreas: " + string.Join(" · ", parts) + ".";
        if (_treeModels.Count > 0) res += "\nTrees: " + string.Join("; ", _treeModels);
        return res;
    }

    /// <summary>Disegno della base dall'alto (modelli veri di BMS) sotto la pianta; senza, restano le sagome grigie.</summary>
    private void LoadDrawing(bool photo)
    {
        _mapStatus.Text = "drawing the base…";
        Cursor = Cursors.Wait;
        // prima si mostra la finestra, poi si disegna (qualche secondo)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                double half = Math.Clamp(_l.Radius * 1.2, 1500, 4000 / 0.3048);
                var img = Preview3DWindow.TopDown(_d, _o, half, 2048, photo);
                _map.SetDrawing(img, half);
                _mapStatus.Text = img == null ? "the drawing could not be made: piece outlines shown" : "";
            }
            finally { Cursor = null; }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static DataGrid Grid0() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, FontSize = 12,
    };

    private static DataGridTextColumn Col(string head, string path, double w, string? tip = null)
    {
        var c = new DataGridTextColumn { Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(w) };
        c.Header = tip == null ? head : new TextBlock { Text = head, ToolTip = tip };
        return c;
    }

    private static string Field(EdPath p, string k) => p.Header.FirstOrDefault(x => x.Field == k).Value ?? "";

    private ListRow Row(EdPath p)
    {
        string data = Field(p, "Data");
        string world = "";
        if (p.Type is 1 or 8 && double.TryParse(data, NumberStyles.Float, Ci, out double dv))
            world = (((dv + _o.Heading) % 360 + 360) % 360).ToString("0", Ci) + "°";
        var deps = p.Header.Where(x => x.Field.StartsWith("FeatureDependencyIdx", StringComparison.Ordinal))
            .Select(x => int.TryParse(x.Value, NumberStyles.Integer, Ci, out int n) ? n : -1).Where(n => n >= 0)
            .Select(n => _l.Features.FirstOrDefault(f => f.Num == n) is { } f ? $"{n} {f.Name}" : $"{n} (removed)").ToList();
        if (double.TryParse(data, NumberStyles.Float, Ci, out double dd)) data = dd.ToString("0.#", Ci);
        return new ListRow(p.HeaderNum, ListKind(p.Type), p.Points.Count, Field(p, "RunwayNumber"), data, world,
                           Field(p, "LandingPattern"), Field(p, "RunwayTexture"), string.Join(", ", deps), p);
    }

    private void ShowList()
    {
        if (_lists.SelectedItem is not ListRow r) { _points.ItemsSource = null; _map.Sel = null; _map.InvalidateVisual(); return; }
        var pr = new List<PointRow>();
        for (int i = 0; i < r.Path.Idx.Count; i++)
        {
            int num = r.Path.Idx[i];
            var q = num < _l.Points.Count ? _l.Points[num] : null;
            int fl = num < _l.PointFlags.Count ? _l.PointFlags[num] : 0;
            string fls = fl == 0 ? "" : $"{fl}" + (fl == 1 ? " first" : fl == 2 ? " last" : fl == 3 ? " first+last" : "");
            var (n, e, t) = r.Path.Points[i];
            pr.Add(new PointRow(num, i + 1, $"{t} {EditorMap.PointName(t)}", (n * 0.3048).ToString("0", Ci), (e * 0.3048).ToString("0", Ci),
                                q == null ? "" : q.Heading.ToString("0", Ci), fls));
        }
        _points.ItemsSource = pr;
        _map.Sel = r.Path; _map.SelPoint = -1;
        _map.InvalidateVisual();
    }

    /// <summary>Pianta della disposizione: pezzi, tutti i punti, la lista scelta in ordine.</summary>
    private sealed class ListMap : FrameworkElement
    {
        private readonly EdObjective _o;
        private readonly EdLayout _l;
        public EdPath? Sel;
        public bool Outlines, AllPoints = true;
        public List<EditorData.AreaPoly> Areas = new();
        public List<(double X, double Y)> Trees = new();
        public readonly HashSet<string> Hidden = new();
        public readonly HashSet<int> Lit = new();
        private BitmapSource? _img; private double _imgHalf;

        public void SetDrawing(BitmapSource? img, double half)
        {
            _img = img; _imgHalf = half;
            if (img == null) Outlines = true;
            InvalidateVisual();
        }
        public int SelPoint = -1;
        public event Action<int>? PointClicked;
        private double _scale = -1, _cx, _cy;   // centro della vista in piedi (nord, est) dal centro dell'obiettivo
        private Point? _drag; private bool _moved;

        public ListMap(EdObjective o, EdLayout l)
        {
            _o = o; _l = l;
            Focusable = true;
            MouseWheel += (_, e) =>
            {
                var p = e.GetPosition(this);
                var (n, ea) = ToFt(p);
                _scale *= e.Delta > 0 ? 1.2 : 1 / 1.2;
                var (n2, e2) = ToFt(p);
                _cx += n - n2; _cy += ea - e2;
                InvalidateVisual();
            };
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { _scale = -1; InvalidateVisual(); return; }
                _drag = e.GetPosition(this); _moved = false; CaptureMouse();
            };
            MouseMove += (_, e) =>
            {
                if (_drag is not Point p0) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - p0.X) + Math.Abs(p.Y - p0.Y) > 3) _moved = true;
                if (!_moved) return;
                _cy -= (p.X - p0.X) / _scale; _cx += (p.Y - p0.Y) / _scale;
                _drag = p; InvalidateVisual();
            };
            MouseLeftButtonUp += (_, e) =>
            {
                ReleaseMouseCapture();
                if (_drag != null && !_moved) Click(e.GetPosition(this));
                _drag = null;
            };
        }

        // coordinate della pianta: ruotata come sulla mappa (nord in alto), centro dell'obiettivo
        private (double N, double E) Rot(double n, double e)
        {
            double h = _o.Heading * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
            return (n * ch - e * sh, n * sh + e * ch);
        }
        private Point ToScreen(double n, double e) => new(ActualWidth / 2 + (e - _cy) * _scale, ActualHeight / 2 - (n - _cx) * _scale);
        private (double N, double E) ToFt(Point p) => (_cx - (p.Y - ActualHeight / 2) / _scale, _cy + (p.X - ActualWidth / 2) / _scale);

        private void Fit()
        {
            double r = Math.Max(200, _l.Radius);
            _scale = Math.Min(ActualWidth, ActualHeight) / (2.1 * r);
            _cx = _cy = 0;
        }

        private void Click(Point p)
        {
            int best = -1; double bd = 12 * 12;
            for (int i = 0; i < _l.Points.Count; i++)
            {
                var q = _l.Points[i];
                var (n, e) = Rot(q.N, q.E);
                var s = ToScreen(n, e);
                double d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
                if (d < bd) { bd = d; best = q.Num; }
            }
            if (best >= 0) PointClicked?.Invoke(best);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 245, 238)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (ActualWidth < 10) return;
            if (_scale <= 0) Fit();
            if (_img != null)
            {
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                dc.DrawImage(_img, new Rect(ToScreen(_imgHalf, -_imgHalf), ToScreen(-_imgHalf, _imgHalf)));
            }
            var piece = new SolidColorBrush(Color.FromRgb(196, 196, 190)); var pieceEdge = new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 145)), 0.6);
            var o0 = new EdObjective { X = 0, Y = 0, Heading = _o.Heading };
            foreach (var f in _l.Features)
            {
                if (_img != null && !Outlines) break;
                var c = EditorData.FeatureCorners(o0, f);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(ToScreen(c[0].X, c[0].Y), true, true);
                    for (int i = 1; i < 4; i++) ctx.LineTo(ToScreen(c[i].X, c[i].Y), true, false);
                }
                dc.DrawGeometry(_img != null ? null : piece, _img != null ? new Pen(Brushes.Yellow, 1) : pieceEdge, g);
            }
            // aree: spianamento, copertura del suolo, poligoni speciali, alberi (coordinate assolute → dal centro dell'obiettivo)
            Point W(double x, double y) => ToScreen(x - _o.X, y - _o.Y);
            foreach (var a in Areas.OrderBy(a => a.Kind == "Leveling" ? 0 : 1))
            {
                if (Hidden.Contains(a.Kind) || a.Poly.Count < 3) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(W(a.Poly[0].X, a.Poly[0].Y), true, true);
                    foreach (var q in a.Poly.Skip(1)) ctx.LineTo(W(q.X, q.Y), true, false);
                }
                Brush? fill; Pen pen;
                switch (a.Kind)
                {
                    case "Leveling":
                        bool full = a.Label.EndsWith("100%", StringComparison.Ordinal);
                        fill = new SolidColorBrush(Color.FromArgb((byte)(full ? 45 : 22), 255, 140, 0));
                        pen = new Pen(Brushes.DarkOrange, full ? 2 : 1);
                        break;
                    case "Land cover":
                        fill = null; pen = new Pen(Brushes.DeepSkyBlue, 2) { DashStyle = DashStyles.Dash };
                        break;
                    default:
                        fill = new SolidColorBrush(Color.FromArgb(40, 220, 0, 220)); pen = new Pen(Brushes.Magenta, 1.6) { DashStyle = DashStyles.DashDot };
                        break;
                }
                dc.DrawGeometry(fill, pen, g);
            }
            if (!Hidden.Contains("Trees"))
            {
                var tb = new SolidColorBrush(Color.FromRgb(20, 110, 30)); var tp = new Pen(Brushes.White, 0.6);
                double tr = Math.Clamp(15 * _scale, 1.8, 6);   // chioma ~ 30 piedi
                foreach (var (x, y) in Trees) dc.DrawEllipse(tb, tp, W(x, y), tr, tr);
            }
            if (!Hidden.Contains("Lit") && Lit.Count > 0)
            {
                var glow = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 230, 0)), 2.2);
                var o0l = new EdObjective { X = 0, Y = 0, Heading = _o.Heading };
                foreach (var f in _l.Features)
                {
                    if (!Lit.Contains(f.Num)) continue;
                    var c = EditorData.FeatureCorners(o0l, f);
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(ToScreen(c[0].X, c[0].Y), true, true);
                        for (int i = 1; i < 4; i++) ctx.LineTo(ToScreen(c[i].X, c[i].Y), true, false);
                    }
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 255, 230, 0)), glow, g);
                }
            }
            var dot = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var dotPen = new Pen(new SolidColorBrush(Color.FromRgb(40, 40, 60)), 0.8);
            if (AllPoints)
            foreach (var q in _l.Points)
            {
                var (n, e) = Rot(q.N, q.E);
                dc.DrawEllipse(dot, dotPen, ToScreen(n, e), 2.2, 2.2);
            }
            // lista scelta: linea in ordine e numeri
            if (Sel != null && Sel.Points.Count > 0)
            {
                var col = Sel.Type switch { 1 => Colors.RoyalBlue, 8 => Colors.DarkOrange, 4 => Colors.Crimson, 6 => Colors.OrangeRed, _ => Colors.Purple };
                var pen = new Pen(new SolidColorBrush(col), 2.5);
                var halo = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 5);
                var pts = Sel.Points.Select(p => { var (n, e) = Rot(p.N, p.E); return ToScreen(n, e); }).ToList();
                bool route = Sel.Type is 1 or 8;
                if (route) for (int i = 1; i < pts.Count; i++) { dc.DrawLine(halo, pts[i - 1], pts[i]); dc.DrawLine(pen, pts[i - 1], pts[i]); }
                if (Sel.Type == 8 && pts.Count > 2) dc.DrawLine(pen, pts[^1], pts[0]);
                var tf = new Typeface("Segoe UI");
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                bool labels = pts.Count <= 120 || _scale > 0.35;
                for (int i = 0; i < pts.Count; i++)
                {
                    var b = i == 0 ? Brushes.Green : i == pts.Count - 1 ? Brushes.Red : new SolidColorBrush(col);
                    dc.DrawEllipse(b, new Pen(Brushes.White, 1), pts[i], 4.5, 4.5);
                    if (labels)
                    {
                        var ft = new FormattedText((i + 1).ToString(Ci), Ci, FlowDirection.LeftToRight, tf, 10.5, Brushes.Black, dpi) { };
                        var tp = new Point(pts[i].X + 5, pts[i].Y - 7);
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, new Rect(tp, new Size(ft.Width + 2, ft.Height)));
                        dc.DrawText(ft, new Point(tp.X + 1, tp.Y));
                    }
                }
            }
            if (SelPoint >= 0 && SelPoint < _l.Points.Count)
            {
                var q = _l.Points[SelPoint];
                var (n, e) = Rot(q.N, q.E);
                var s = ToScreen(n, e);
                dc.DrawEllipse(null, new Pen(Brushes.Gold, 3), s, 10, 10);
                // direzione del punto
                double h = (_o.Heading + q.Heading) * Math.PI / 180;
                dc.DrawLine(new Pen(Brushes.Gold, 2), s, new Point(s.X + Math.Sin(h) * 22, s.Y - Math.Cos(h) * 22));
            }
            // nord
            var np = new Point(ActualWidth - 24, 26);
            dc.DrawLine(new Pen(Brushes.Black, 1.5), new Point(np.X, np.Y + 12), new Point(np.X, np.Y - 10));
            dc.DrawText(new FormattedText("N", Ci, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(np.X - 4, np.Y + 12));
            // scala 100 m
            double px = 100 / 0.3048 * _scale;
            dc.DrawLine(new Pen(Brushes.Black, 2), new Point(12, ActualHeight - 14), new Point(12 + px, ActualHeight - 14));
            dc.DrawText(new FormattedText("100 m", Ci, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(14, ActualHeight - 30));
        }
    }
}
