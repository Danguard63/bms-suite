using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CampaignEditor;

/// <summary>
/// Simboli delle unita' sulla mappa come quelli di BMS (Art\Resource\*.rsc, ICON_xxx: simbologia NATO), disegnati in vettoriale
/// cosi' restano nitidi a ogni zoom. Il simbolo viene da UnitIcon dell'UCD del tipo di unita' (gli stessi numeri di Art\Main\ImageIds.id).
/// </summary>
internal static class UnitSymbols
{
    public const int Infantry = 10008, MechInfantry = 10009, Armor = 10010, SpArtillery = 10011, ArmyAviation = 10012, Airborne = 10013,
        ArmoredCavalry = 10014, Engineer = 10015, Hq = 10016, Airmobile = 10017, TowedArtillery = 10018, AirDefense = 10019,
        Carrier = 10100, Cruiser = 10101, Destroyer = 10102, Frigate = 10103, PtBoat = 10104, SupplyShip = 10105, Tanker = 10106, Submarine = 10107,
        MultiRocket = 10120, SurfaceMissile = 10121, Supply = 10158;

    public static string Name(int icon) => icon switch
    {
        Infantry => "infantry", MechInfantry => "mechanized infantry", Armor => "armor", SpArtillery => "self-propelled artillery",
        ArmyAviation => "army aviation", Airborne => "airborne", ArmoredCavalry => "armored cavalry", Engineer => "engineer", Hq => "HQ",
        Airmobile => "air assault", TowedArtillery => "towed artillery", AirDefense => "air defense", MultiRocket => "rocket artillery",
        SurfaceMissile => "surface-to-surface missile", Supply => "supply", Carrier => "carrier", Cruiser => "cruiser", Destroyer => "destroyer",
        Frigate => "frigate", PtBoat => "patrol boat", SupplyShip => "supply ship", Tanker => "tanker / cargo", Submarine => "submarine",
        _ => "",
    };

    private static string NavalCode(int icon) => icon switch
    {
        Carrier => "CV", Cruiser => "CG", Destroyer => "DD", Frigate => "FF", PtBoat => "PT", SupplyShip => "AOE", Tanker => "AO", Submarine => "SS", _ => "TF",
    };

    public static bool IsNaval(int icon) => icon >= 10100 && icon <= 10107;

    /// <summary>
    /// Simbolo di terra: cornice rettangolare piena col colore della squadra, segno del tipo dentro, grado sopra (II battaglione, X brigata).
    /// sam: per la difesa aerea con missili (BMS usa lo stesso simbolo per SAM e AAA: qui ai SAM si aggiunge il missile).
    /// </summary>
    /// <param name="aff">stile NATO: 1 amico (rettangolo), 2 nemico (rombo), 3 neutrale (quadrato); 0 = rettangolo nel colore della squadra</param>
    public static void DrawGround(DrawingContext dc, Point c, double s, int icon, bool brigade, bool sam, Brush fill, Pen frame, double dpi, int aff = 0)
    {
        double w = brigade ? 3.0 * s : 2.3 * s, h = brigade ? 1.9 * s : 1.45 * s;
        var r = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
        double frameTop = r.Top;
        if (aff == 2)
        {
            // rombo: il segno sta nel rettangolo inscritto
            double a = h * 1.25;
            var dg = new StreamGeometry();
            using (var k = dg.Open())
            {
                k.BeginFigure(new Point(c.X, c.Y - a), true, true);
                k.PolyLineTo(new[] { new Point(c.X + a, c.Y), new Point(c.X, c.Y + a), new Point(c.X - a, c.Y) }, true, false);
            }
            dg.Freeze();
            dc.DrawGeometry(fill, frame, dg);
            w = a * 1.2; h = a * 0.72;
            r = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
            frameTop = c.Y - a;
        }
        else if (aff == 3)
        {
            double q = h * 1.3;
            var sq = new Rect(c.X - q / 2, c.Y - q / 2, q, q);
            dc.DrawRectangle(fill, frame, sq);
            w = q; h = q;
            r = sq;
            frameTop = sq.Top;
        }
        else dc.DrawRectangle(fill, frame, r);
        var g = new Pen(Brushes.Black, Math.Max(0.8, s * 0.16)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        g.Freeze();
        double x0 = r.Left, y0 = r.Top, x1 = r.Right, y1 = r.Bottom, cx = c.X, cy = c.Y;
        void L(double ax, double ay, double bx, double by) => dc.DrawLine(g, new Point(ax, ay), new Point(bx, by));
        void X() { L(x0, y0, x1, y1); L(x0, y1, x1, y0); }
        void Oval(double fx = 0.36, double fy = 0.28) => dc.DrawEllipse(null, g, new Point(cx, cy), w * fx, h * fy);
        void Dot(double y) => dc.DrawEllipse(Brushes.Black, null, new Point(cx, y), h * 0.13, h * 0.13);
        switch (icon)
        {
            case Infantry: X(); break;
            case MechInfantry: X(); Oval(); break;
            case Armor: Oval(); break;
            case ArmoredCavalry: Oval(); L(x0, y1, x1, y0); break;
            case Airmobile:
                X();
                L(cx, cy, cx, y0 + h * 0.18); L(cx, y0 + h * 0.18, cx - w * 0.1, y0 + h * 0.04); L(cx, y0 + h * 0.18, cx + w * 0.1, y0 + h * 0.04);
                break;
            case Airborne:
                X();
                foreach (double dx in new[] { -1.0, 1.0 })
                {
                    var arc = new StreamGeometry();
                    using (var k = arc.Open())
                    {
                        k.BeginFigure(new Point(cx + dx * w * 0.16, y1 - h * 0.04), false, false);
                        k.ArcTo(new Point(cx, y1 - h * 0.04), new Size(w * 0.08, h * 0.14), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze();
                    dc.DrawGeometry(null, g, arc);
                }
                break;
            case Engineer:
            {
                double top = y0 + h * 0.3, bot = y0 + h * 0.75, a = x0 + w * 0.27, b = x1 - w * 0.27;
                L(a, top, b, top); L(a, top, a, bot); L(cx, top, cx, bot); L(b, top, b, bot);
                break;
            }
            case Hq:
            {
                var t = new FormattedText("HQ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(4, h * 0.72), Brushes.Black, dpi);
                dc.DrawText(t, new Point(cx - t.Width / 2, cy - t.Height / 2));
                break;
            }
            case SpArtillery: Oval(); Dot(cy); break;
            case TowedArtillery: Dot(cy); break;
            case MultiRocket:
                foreach (double yy in new[] { y0 + h * 0.18, y0 + h * 0.42 }) { L(cx - w * 0.1, yy + h * 0.16, cx, yy); L(cx, yy, cx + w * 0.1, yy + h * 0.16); }
                L(cx, y0 + h * 0.62, cx, y1 - h * 0.1); L(cx - w * 0.07, y0 + h * 0.74, cx + w * 0.07, y0 + h * 0.74);
                break;
            case SurfaceMissile:
                L(cx, y0 + h * 0.12, cx, y1); L(cx - w * 0.06, y0 + h * 0.3, cx + w * 0.06, y0 + h * 0.3);
                L(cx - w * 0.09, y0 + h * 0.45, cx - w * 0.09, y1); L(cx + w * 0.09, y0 + h * 0.45, cx + w * 0.09, y1);
                break;
            case AirDefense:
            {
                var dome = new StreamGeometry();
                using (var k = dome.Open())
                {
                    k.BeginFigure(new Point(x0 + w * 0.08, y1), false, false);
                    k.ArcTo(new Point(x1 - w * 0.08, y1), new Size(w * 0.42, h * 0.5), 0, false, SweepDirection.Clockwise, true, false);
                }
                dome.Freeze();
                dc.DrawGeometry(null, g, dome);
                if (sam) { L(cx, y1 - h * 0.05, cx, y0 + h * 0.22); L(cx - w * 0.06, y0 + h * 0.36, cx, y0 + h * 0.22); L(cx, y0 + h * 0.22, cx + w * 0.06, y0 + h * 0.36); }
                break;
            }
            case Supply: L(x0, y0 + h * 0.75, x1, y0 + h * 0.75); break;
            case ArmyAviation:
                dc.DrawEllipse(null, g, new Point(cx - w * 0.13, cy), w * 0.13, h * 0.22);
                dc.DrawEllipse(null, g, new Point(cx + w * 0.13, cy), w * 0.13, h * 0.22);
                break;
            default:
            {
                var t = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(4, h * 0.8), Brushes.Black, dpi);
                dc.DrawText(t, new Point(cx - t.Width / 2, cy - t.Height / 2));
                break;
            }
        }
        // grado sopra la cornice (APP-6): II = battaglione, X = brigata
        double ty = frameTop - Math.Max(2, s * 0.25), th = Math.Max(2.5, s * 0.55);
        if (brigade) { L(cx - th * 0.45, ty - th, cx + th * 0.45, ty); L(cx - th * 0.45, ty, cx + th * 0.45, ty - th); }
        else { L(cx - s * 0.2, ty - th, cx - s * 0.2, ty); L(cx + s * 0.2, ty - th, cx + s * 0.2, ty); }
    }

    /// <summary>Navi e gruppi navali: capsula col codice della nave (come ICON_CARRIER "CVN", ICON_CRUISER "CG"… di BMS).</summary>
    public static void DrawNaval(DrawingContext dc, Point c, double s, int icon, Brush fill, Pen frame, double dpi, int aff = 0)
    {
        double w = 3.0 * s, h = 1.3 * s;
        var r = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
        if (aff == 1) { h = 1.9 * s; dc.DrawEllipse(fill, frame, c, h * 0.55, h * 0.55); }            // NATO: amico cerchio
        else if (aff == 2)
        {
            double a = 1.2 * s;
            var dg = new StreamGeometry();
            using (var k = dg.Open()) { k.BeginFigure(new Point(c.X, c.Y - a), true, true); k.PolyLineTo(new[] { new Point(c.X + a, c.Y), new Point(c.X, c.Y + a), new Point(c.X - a, c.Y) }, true, false); }
            dg.Freeze();
            dc.DrawGeometry(fill, frame, dg);
            h = 1.6 * s;
        }
        else if (aff == 3) { double q = 1.9 * s; dc.DrawRectangle(fill, frame, new Rect(c.X - q / 2, c.Y - q / 2, q, q)); h = 1.6 * s; }
        else dc.DrawRoundedRectangle(fill, frame, r, h / 2, h / 2);
        var t = new FormattedText(NavalCode(icon), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(4, h * 0.75), Brushes.Black, dpi);
        dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
    }
}
