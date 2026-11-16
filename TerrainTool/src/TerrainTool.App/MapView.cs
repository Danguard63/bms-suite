using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Theater;

namespace TerrainTool.App;

/// <summary>
/// Mappa di verifica: riquadro del teatro, griglia segmenti, reticolo lat/lon,
/// coste e confini, basi colorate per livello (pieno = reale, vuoto = da verificare), vecchio teatro in viola.
/// Rotella = zoom, trascina = sposta, doppio clic = adatta.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public TheaterGrid? Grid { get; set; }
    public IList<Airbase> Bases { get; set; } = new List<Airbase>();
    public IList<BaseMapLine> Lines { get; set; } = new List<BaseMapLine>();
    public bool ShowOld { get; set; }
    public Airbase? Selected { get; set; }

    public event Action<Airbase>? BaseClicked;
    public event Action<string>? HoverText;

    private double _zoom = 1, _panX, _panY;
    private Point? _dragStart;
    private Point _dragPan;

    private static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)));
    private static readonly Brush TheaterFill = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x2D, 0x37)));
    private static readonly Pen TheaterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), 2));
    private static readonly Pen SegPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen GratPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x6F, 0xA8, 0xDC)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen CoastPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x7F, 0xB8, 0xE0)), 1.2));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Brush MainBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x3B)));
    private static readonly Brush SecBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x6B)));
    private static readonly Brush MicroBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xD3, 0x2E)));
    private static readonly Pen MainPen = Freeze(new Pen(MainBrush, 1.6));
    private static readonly Pen SecPen = Freeze(new Pen(SecBrush, 1.6));
    private static readonly Pen MicroPen = Freeze(new Pen(MicroBrush, 1.6));
    private static readonly Brush ServerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xD0, 0xF0)));
    private static readonly Pen ServerPen = Freeze(new Pen(ServerBrush, 1.6));
    private static readonly Pen ServerRing = Freeze(new Pen(ServerBrush, 1.5));
    private static readonly Brush OldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0x7C, 0xE8)));
    private static readonly Pen OldLinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xB0, 0x7C, 0xE8)), 1));
    private static readonly Pen SelPen = Freeze(new Pen(Brushes.White, 2));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDA)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void ResetView() { _zoom = 1; _panX = _panY = 0; InvalidateVisual(); }

    // Teatro (metri) → schermo
    private double Scale
    {
        get
        {
            if (Grid == null) return 1;
            double size = Grid.Config.SizeMeters * 1.15;
            return Math.Min(ActualWidth, ActualHeight) / size * _zoom;
        }
    }

    private Point ToScreen(double x, double y)
    {
        double s = Scale, half = (Grid?.Config.SizeMeters ?? 0) / 2;
        return new Point(ActualWidth / 2 + (x - half) * s + _panX, ActualHeight / 2 - (y - half) * s + _panY);
    }

    private Point Project(double lat, double lon)
    {
        var (x, y) = Grid!.ToTheater(lat, lon);
        return ToScreen(x, y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Grid == null) return;
        double size = Grid.Config.SizeMeters;

        // Riquadro teatro
        var tl = ToScreen(0, size);
        var br = ToScreen(size, 0);
        dc.DrawRectangle(TheaterFill, null, new Rect(tl, br));

        // Griglia ogni 16 segmenti (256 km)
        double step = 16000 * 16;
        for (double v = step; v < size; v += step)
        {
            dc.DrawLine(SegPen, ToScreen(v, 0), ToScreen(v, size));
            dc.DrawLine(SegPen, ToScreen(0, v), ToScreen(size, v));
        }

        // Reticolo lat/lon ogni 2°
        var corners = Grid.Corners();
        double minLat = Math.Floor(corners.Min(c => c.Lat)) - 2, maxLat = Math.Ceiling(corners.Max(c => c.Lat)) + 2;
        double minLon = Math.Floor(corners.Min(c => c.Lon)) - 2, maxLon = Math.Ceiling(corners.Max(c => c.Lon)) + 2;
        for (double lat = Math.Ceiling(minLat / 2) * 2; lat <= maxLat; lat += 2)
        {
            DrawPolyline(dc, GratPen, Enumerable.Range(0, 61).Select(i => (lat, minLon + (maxLon - minLon) * i / 60.0)));
            var p = Project(lat, Grid.Config.CenterLon);
            DrawLabel(dc, $"{lat:0}°N", new Point(tl.X + 4, p.Y - 14));
        }
        for (double lon = Math.Ceiling(minLon / 2) * 2; lon <= maxLon; lon += 2)
        {
            DrawPolyline(dc, GratPen, Enumerable.Range(0, 61).Select(i => (minLat + (maxLat - minLat) * i / 60.0, lon)));
            var p = Project(Grid.Config.CenterLat, lon);
            DrawLabel(dc, $"{lon:0}°E", new Point(p.X + 3, br.Y - 16));
        }

        // Coste e confini
        foreach (var l in Lines)
            DrawPolyline(dc, l.Kind == 'C' ? CoastPen : BorderPen, l.Points);

        dc.DrawRectangle(null, TheaterPen, new Rect(tl, br));

        // Basi
        foreach (var b in Bases)
        {
            if (ShowOld && b.HasOld)
            {
                var po = Project(b.LatOld, b.LonOld);
                if (b.HasReal) dc.DrawLine(OldLinePen, po, Project(b.Lat, b.Lon));
                dc.DrawEllipse(OldBrush, null, po, 2.5, 2.5);
            }
            // Colore = livello; pieno = posizione reale, vuoto = da verificare (disegnato alla posizione vecchia)
            var (brush, pen) = b.Tier switch { "Main" => (MainBrush, MainPen), "Micro" => (MicroBrush, MicroPen), "Server" => (ServerBrush, ServerPen), _ => (SecBrush, SecPen) };
            if (b.HasReal) dc.DrawEllipse(brush, null, Project(b.Lat, b.Lon), 3.5, 3.5);
            if (b.HasReal && b.Tier == "Server") dc.DrawEllipse(null, ServerRing, Project(b.Lat, b.Lon), 7, 7);
            else if (b.HasOld) dc.DrawEllipse(null, pen, Project(b.LatOld, b.LonOld), 3.5, 3.5);
        }
        DrawLegend(dc);

        if (Selected != null)
        {
            Point? p = Selected.HasReal ? Project(Selected.Lat, Selected.Lon) : Selected.HasOld ? Project(Selected.LatOld, Selected.LonOld) : null;
            if (p.HasValue)
            {
                dc.DrawEllipse(null, SelPen, p.Value, 8, 8);
                DrawLabel(dc, Selected.Name, new Point(p.Value.X + 10, p.Value.Y - 8), 13);
            }
        }
    }

    private static readonly Brush LegendBg = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x18, 0x1E)));
    private static readonly Pen LegendBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1));

    /// <summary>Legenda in basso a sinistra.</summary>
    private void DrawLegend(DrawingContext dc)
    {
        var items = new List<(string Text, Action<Point> Symbol)>
        {
            ("Main", p => dc.DrawEllipse(MainBrush, null, p, 4, 4)),
            ("Secondary", p => dc.DrawEllipse(SecBrush, null, p, 4, 4)),
            ("Micro", p => dc.DrawEllipse(MicroBrush, null, p, 4, 4)),
            ("Server (PvP, neutral)", p => { dc.DrawEllipse(ServerBrush, null, p, 4, 4); dc.DrawEllipse(null, ServerRing, p, 7, 7); }),
            ("Filled = real position", p => dc.DrawEllipse(SecBrush, null, p, 4, 4)),
            ("Hollow = to verify", p => dc.DrawEllipse(null, SecPen, p, 4, 4)),
        };
        if (ShowOld) items.Add(("Old theater position", p => dc.DrawEllipse(OldBrush, null, p, 3, 3)));
        items.Add(("Coastline", p => dc.DrawLine(CoastPen, new Point(p.X - 8, p.Y), new Point(p.X + 8, p.Y))));
        items.Add(("Border", p => dc.DrawLine(BorderPen, new Point(p.X - 8, p.Y), new Point(p.X + 8, p.Y))));
        items.Add(("Theater edge", p => dc.DrawLine(TheaterPen, new Point(p.X - 8, p.Y), new Point(p.X + 8, p.Y))));
        items.Add(("Grid 256 km", p => dc.DrawLine(SegPen, new Point(p.X - 8, p.Y), new Point(p.X + 8, p.Y))));

        const double row = 18, pad = 8, w = 180;
        double h = items.Count * row + pad * 2;
        var box = new Rect(10, ActualHeight - h - 10, w, h);
        dc.DrawRoundedRectangle(LegendBg, LegendBorder, box, 4, 4);
        for (int i = 0; i < items.Count; i++)
        {
            double y = box.Top + pad + i * row + row / 2;
            items[i].Symbol(new Point(box.Left + pad + 10, y));
            DrawLabel(dc, items[i].Text, new Point(box.Left + pad + 26, y - 8), 12);
            if (i == 3 || i == 5) // separatori dopo i livelli e dopo pieno/vuoto
                dc.DrawLine(LegendBorder, new Point(box.Left + pad, y + row / 2), new Point(box.Right - pad, y + row / 2));
        }
    }

    private void DrawPolyline(DrawingContext dc, Pen pen, IEnumerable<(double Lat, double Lon)> pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool first = true;
            foreach (var (lat, lon) in pts)
            {
                var p = Project(lat, lon);
                if (first) { ctx.BeginFigure(p, false, false); first = false; }
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawLabel(DrawingContext dc, string text, Point at, double size = 11)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, at);
    }

    private Airbase? HitTest(Point screen)
    {
        if (Grid == null) return null;
        Airbase? best = null;
        double bestD = 10;
        foreach (var b in Bases)
        {
            Point? p = b.HasReal ? Project(b.Lat, b.Lon) : b.HasOld ? Project(b.LatOld, b.LonOld) : null;
            if (!p.HasValue) continue;
            double d = (p.Value - screen).Length;
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var m = e.GetPosition(this);
        double f = e.Delta > 0 ? 1.25 : 0.8;
        double nz = Math.Clamp(_zoom * f, 0.5, 40);
        f = nz / _zoom;
        // zoom attorno al cursore
        _panX = (_panX + ActualWidth / 2 - m.X) * f + m.X - ActualWidth / 2;
        _panY = (_panY + ActualHeight / 2 - m.Y) * f + m.Y - ActualHeight / 2;
        _zoom = nz;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2) { ResetView(); return; }
        var hit = HitTest(e.GetPosition(this));
        if (hit != null) { BaseClicked?.Invoke(hit); return; }
        _dragStart = e.GetPosition(this);
        _dragPan = new Point(_panX, _panY);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var m = e.GetPosition(this);
        if (_dragStart.HasValue)
        {
            _panX = _dragPan.X + m.X - _dragStart.Value.X;
            _panY = _dragPan.Y + m.Y - _dragStart.Value.Y;
            InvalidateVisual();
            return;
        }
        if (Grid == null) return;
        double s = Scale, half = Grid.Config.SizeMeters / 2;
        double x = (m.X - ActualWidth / 2 - _panX) / s + half;
        double y = -(m.Y - ActualHeight / 2 - _panY) / s + half;
        var (lat, lon) = Grid.ToLatLon(x, y);
        var hit = HitTest(m);
        string txt = string.Create(CultureInfo.InvariantCulture,
            $"Lat {lat:0.0000}  Lon {lon:0.0000}   Theater X {x / 1000:0.0} km  Y {y / 1000:0.0} km   Segment {Math.Floor(x / 16000):0},{Math.Floor(y / 16000):0}");
        if (hit != null) txt += $"   ▶ {hit.Name} ({hit.Country})";
        HoverText?.Invoke(txt);
    }
}
