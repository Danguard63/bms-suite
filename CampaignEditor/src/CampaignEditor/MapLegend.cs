using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// Legenda dei simboli della mappa, nella colonna di destra sotto il pannello delle proprieta' (non copre la mappa):
/// squadre, obiettivi, anelli, link, unita', rotte, copertura antiaerea. I disegni usano le stesse funzioni della mappa.
/// </summary>
public sealed class MapLegend : StackPanel
{
    private static readonly Brush Blue = Fz(new SolidColorBrush(Color.FromRgb(70, 140, 255)));
    private static readonly Brush Dark = Fz(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1E)));
    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MapLegend() { Margin = new Thickness(8, 4, 8, 8); }

    /// <summary>Ricostruisce la legenda; teams = colori e nomi delle squadre del file aperto.</summary>
    /// <param name="nato">stile NATO: cornici amico / nemico / neutrale (aff delle squadre viste dal Viewpoint)</param>
    public void Fill(IEnumerable<(string Name, Color Color, ImageSource? Flag, int Aff)> teams, bool nato = false, string viewpoint = "")
    {
        Children.Clear();
        static string AffName(int a) => a switch { 1 => "friend", 2 => "hostile", _ => "neutral" };
        Head(nato ? $"Teams (NATO icons, seen from {viewpoint})" : "Teams");
        foreach (var (name, col, flag, aff) in teams)
            Row(dc =>
            {
                if (flag != null) { dc.DrawRectangle(Brushes.Gray, null, new Rect(3, 2, 28, 16)); dc.DrawImage(flag, new Rect(4, 3, 26, 14)); }
                else dc.DrawEllipse(new SolidColorBrush(col), new Pen(Brushes.Black, 0.8), new Point(17, 10), 6, 6);
            }, nato ? $"{name} – {AffName(aff)}" : name);
        if (nato)
        {
            Head("Sides (frame and colour)");
            foreach (int a in new[] { 1, 2, 3 })
                Row(dc => ObjectiveIcons.DrawNato(dc, 15, new Point(17, 11), 20, a), a switch { 1 => "friend: rectangle (circle for ships)", 2 => "hostile: diamond", _ => "neutral: square" });
        }

        Head("Objectives (zoomed in; far out they are dots, airbases squares)");
        foreach (var (t, n) in new[]
        {
            (1, "airbase"), (2, "airstrip"), (3, "army base"), (8, "city"), (28, "town"), (29, "village"), (6, "bridge"),
            (11, "factory, chemical plant, refinery"), (10, "depot"), (19, "port"), (20, "power plant"), (17, "nuclear plant"),
            (21, "radar"), (31, "SAM site"), (22, "radio tower"), (9, "command & control"), (13, "fortification"),
            (16, "nav beacon"), (23, "railway, rail terminal"), (15, "junction, road"), (14, "scenery"),
            (18, "pass"), (5, "border"), (4, "beach, sea, ford"), (30, "HARTS"),
        })
            Row(dc => { if (nato) ObjectiveIcons.DrawNato(dc, t, new Point(17, 11), 20, 1); else ObjectiveIcons.Draw(dc, t, new Point(17, 10), 16, Blue); }, n);

        Head("Rings");
        Ring(Brushes.Red, 2, "front line (1st)");
        Ring(Brushes.Orange, 2, "second line");
        Ring(Brushes.Yellow, 2, "third line");
        Ring(Brushes.Yellow, 2.5, "selected");
        Ring(Brushes.Red, 1.5, "error found by the check (Issues)");
        Ring(Brushes.Magenta, 2.5, "place marked from the Issues list");
        Ring(Brushes.Cyan, 2, "link being made from here");

        Head("Links (road network of the ground units)");
        Line(new Pen(new SolidColorBrush(Color.FromArgb(200, 230, 230, 230)), 1), "link");
        Line(new Pen(Brushes.Orange, 1.5), "longer than 60 km");
        Line(new Pen(Brushes.Red, 2) { DashStyle = DashStyles.Dash }, "one way only (no link back)");
        Line(new Pen(Brushes.Yellow, 2), "links of the selected objective");
        Line(new Pen(Brushes.Cyan, 1.5) { DashStyle = DashStyles.Dash }, "unit → its objective / squadron → airbase");

        Head("Units");
        double dpi = 1;
        var U = nato ? ObjectiveIcons.AffBrush(1) : Blue;
        Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 7, UnitSymbols.Armor, false, false, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), "battalion (II), type sign inside");
        Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 7, UnitSymbols.Infantry, true, false, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), "brigade (X)");
        Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 7, UnitSymbols.AirDefense, false, true, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), "air defence with missiles (SAM)");
        Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 7, UnitSymbols.AirDefense, false, false, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), "air defence guns (AAA)");
        Row(dc => UnitSymbols.DrawNaval(dc, new Point(17, 10), 7, UnitSymbols.Carrier, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), "naval group (CV carrier, CG, DD, FF…)");
        Row(dc => Triangle(dc, U, new Pen(Brushes.Black, 1), new Point(17, 11), 6), "squadron");
        if (nato)
        {
            Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 5.5, UnitSymbols.Armor, false, false, ObjectiveIcons.AffBrush(2), new Pen(Brushes.Black, 1), dpi, 2), "hostile battalion (diamond)");
            Row(dc => UnitSymbols.DrawNaval(dc, new Point(17, 10), 6, UnitSymbols.Destroyer, ObjectiveIcons.AffBrush(2), new Pen(Brushes.Black, 1), dpi, 2), "hostile naval group");
        }

        Head("Ground unit types (sign inside the frame)");
        foreach (int ic in new[] { UnitSymbols.Infantry, UnitSymbols.MechInfantry, UnitSymbols.Armor, UnitSymbols.ArmoredCavalry, UnitSymbols.Airborne,
                                   UnitSymbols.Airmobile, UnitSymbols.Engineer, UnitSymbols.Hq, UnitSymbols.SpArtillery, UnitSymbols.TowedArtillery,
                                   UnitSymbols.MultiRocket, UnitSymbols.SurfaceMissile, UnitSymbols.AirDefense, UnitSymbols.ArmyAviation, UnitSymbols.Supply })
        {
            int icon = ic;
            Row(dc => UnitSymbols.DrawGround(dc, new Point(17, 11), 7, icon, false, false, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), UnitSymbols.Name(icon));
        }
        Head("Naval groups (code inside)");
        foreach (int ic in new[] { UnitSymbols.Carrier, UnitSymbols.Cruiser, UnitSymbols.Destroyer, UnitSymbols.Frigate, UnitSymbols.PtBoat,
                                   UnitSymbols.SupplyShip, UnitSymbols.Tanker, UnitSymbols.Submarine })
        {
            int icon = ic;
            Row(dc => UnitSymbols.DrawNaval(dc, new Point(17, 10), 7, icon, U, new Pen(Brushes.Black, 1), dpi, nato ? 1 : 0), UnitSymbols.Name(icon));
        }

        Head("Routes (package or flight selected)");
        Line(new Pen(Blue, 2), "route of a flight of the package (team colour)");
        Line(new Pen(new SolidColorBrush(Color.FromArgb(120, 70, 140, 255)), 1), "other flights ('All routes')");
        Line(new Pen(Brushes.Yellow, 2.5), "route of the selected flight");
        Row(dc => { dc.DrawLine(new Pen(Brushes.Yellow, 2), new Point(2, 10), new Point(32, 10)); dc.DrawEllipse(Dark, new Pen(Brushes.Yellow, 1.5), new Point(17, 10), 4, 4); }, "steerpoint (drag it to move it)");
        Row(dc => dc.DrawRectangle(Brushes.Red, new Pen(Brushes.White, 1.2), new Rect(12, 5, 10, 10)), "target steerpoint");

        Head("Air defence coverage (SAM / AAA boxes)");
        Row(dc => dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 70, 140, 255)), new Pen(new SolidColorBrush(Color.FromArgb(200, 70, 140, 255)), 1.2), new Point(17, 10), 9, 9), "SAM and ships: range in the team colour");
        Row(dc => dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, 70, 140, 255)), new Pen(new SolidColorBrush(Color.FromArgb(200, 70, 140, 255)), 1) { DashStyle = DashStyles.Dash }, new Point(17, 10), 9, 9), "AAA");
    }

    private void Head(string text) =>
        Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, Children.Count == 0 ? 0 : 8, 0, 2) });

    private void Row(Action<DrawingContext> draw, string text)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Dark, null, new Rect(0, 0, 34, 20));
            draw(dc);
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(68, 40, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        sp.Children.Add(new Image { Source = bmp, Width = 34, Height = 20, Margin = new Thickness(0, 0, 8, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 });
        Children.Add(sp);
    }

    private void Ring(Brush b, double w, string text) =>
        Row(dc => { dc.DrawEllipse(Blue, new Pen(Brushes.Black, 0.8), new Point(17, 10), 4, 4); dc.DrawEllipse(null, new Pen(b, w), new Point(17, 10), 7.5, 7.5); }, text);

    private void Line(Pen p, string text) => Row(dc => dc.DrawLine(p, new Point(2, 10), new Point(32, 10)), text);

    private static void Triangle(DrawingContext dc, Brush fill, Pen pen, Point p, double s)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(new Point(p.X, p.Y - s * 1.2), true, true); c.LineTo(new Point(p.X + s, p.Y + s * 0.8), true, false); c.LineTo(new Point(p.X - s, p.Y + s * 0.8), true, false); }
        g.Freeze();
        dc.DrawGeometry(fill, pen, g);
    }
}
