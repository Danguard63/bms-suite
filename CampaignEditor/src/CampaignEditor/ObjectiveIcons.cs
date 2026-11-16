using System.Windows;
using System.Windows.Media;

namespace CampaignEditor;

/// <summary>
/// Icone vettoriali degli obiettivi con le forme della mappa 2D di BMS (Art\Resource\*dark: ICON_GEN_APT, ICON_ARMYBASE,
/// ICON_BRIDGE, ICON_INDUSTRY, ICON_DEPO, ICON_PORT, ICON_RADAR, ICON_SAM, ICON_NUCLEAR, ICON_ANTENNA, ...), nitide a ogni zoom.
/// Ogni forma e' disegnata in un quadrato -1..1 e scalata alla dimensione voluta; colore = squadra (o tipo), bordo scuro.
/// La stessa classe e' nel Campaign Editor e nell'Object Editor, cosi' le due mappe si somigliano.
/// </summary>
public static class ObjectiveIcons
{
    private static readonly Dictionary<int, Geometry> Cache = new();

    /// <summary>Quanto e' grande l'icona del tipo rispetto alle altre (citta' e basi piu' grandi, villaggi e incroci piccoli).</summary>
    public static double Weight(int type) => type switch
    {
        1 or 3 or 8 => 1.0,
        2 or 19 or 21 or 31 or 17 or 20 or 25 or 11 or 7 or 10 or 9 or 30 or 6 => 0.85,
        28 or 13 or 16 or 22 or 23 or 24 or 5 or 4 or 12 or 18 or 27 => 0.75,
        14 => 0.65,
        29 or 15 or 26 => 0.5,
        _ => 0.7,
    };

    public static Geometry Shape(int type)
    {
        if (Cache.TryGetValue(type, out var g)) return g;
        g = type switch
        {
            1 => P("F0 M -0.72,0 A 0.72,0.72 0 1 1 0.72,0 A 0.72,0.72 0 1 1 -0.72,0 Z M -0.38,0 A 0.38,0.38 0 1 0 0.38,0 A 0.38,0.38 0 1 0 -0.38,0 Z " +
                   "M -0.16,-1 H 0.16 V -0.68 H -0.16 Z M -0.16,0.68 H 0.16 V 1 H -0.16 Z M -1,-0.16 H -0.68 V 0.16 H -1 Z M 0.68,-0.16 H 1 V 0.16 H 0.68 Z"),   // aeroporto
            2 => P("M -1,-0.17 H 1 V 0.17 H -1 Z M 0,-0.6 L 0.42,0 0,0.6 -0.42,0 Z"),                                              // pista
            3 => P("M -0.92,-0.72 L -0.72,-0.92 0.92,0.72 0.72,0.92 Z M 0.92,-0.72 L 0.72,-0.92 -0.92,0.72 -0.72,0.92 Z " +
                   "M -0.95,0.35 L -0.35,0.95 -0.22,0.82 -0.82,0.22 Z M 0.95,0.35 L 0.35,0.95 0.22,0.82 0.82,0.22 Z"),              // base militare: sciabole incrociate
            4 or 27 or 12 => P("M -1,-0.25 C -0.6,-0.7 -0.4,0.2 0,-0.25 C 0.4,-0.7 0.6,0.2 1,-0.25 V 0.05 C 0.6,0.5 0.4,-0.4 0,0.05 C -0.4,0.5 -0.6,-0.4 -1,0.05 Z " +
                               "M -1,0.35 C -0.6,-0.1 -0.4,0.8 0,0.35 C 0.4,-0.1 0.6,0.8 1,0.35 V 0.65 C 0.6,1.1 0.4,0.2 0,0.65 C -0.4,1.1 -0.6,0.2 -1,0.65 Z"),  // onde
            5 => P("M -0.95,-0.45 H 0.95 V 0.05 H -0.95 Z M -0.85,0.05 H -0.6 V 0.9 H -0.85 Z M 0.6,0.05 H 0.85 V 0.9 H 0.6 Z"),       // sbarra di confine
            6 => P("M -0.62,-0.55 L -0.32,-0.14 H 0.32 L 0.62,-0.55 0.8,-0.42 0.48,0 0.8,0.42 0.62,0.55 0.32,0.14 H -0.32 L -0.62,0.55 -0.8,0.42 -0.48,0 -0.8,-0.42 Z"), // ponte )(
            7 or 11 or 25 => P("F0 M -0.85,0.85 V -0.95 H -0.45 V -0.05 H 0.9 V 0.85 Z M -0.05,0.2 H 0.2 V 0.5 H -0.05 Z M 0.42,0.2 H 0.67 V 0.5 H 0.42 Z"), // industria
            8 => P("M -0.9,0 A 0.9,0.9 0 1 1 0.9,0 A 0.9,0.9 0 1 1 -0.9,0 Z"),                                                    // citta'
            28 => P("M -0.75,0 A 0.75,0.75 0 1 1 0.75,0 A 0.75,0.75 0 1 1 -0.75,0 Z"),                                              // paese
            29 => P("M -0.6,0 A 0.6,0.6 0 1 1 0.6,0 A 0.6,0.6 0 1 1 -0.6,0 Z"),                                                    // villaggio
            9 => P("M -0.75,1 V -1 H -0.5 V -0.95 L 0.9,-0.52 -0.5,-0.1 V 1 Z"),                                                  // comando e controllo: bandiera
            10 => P("F0 M -0.85,-0.75 H 0.85 V 0.85 H -0.85 Z M -0.58,-0.48 H -0.12 V -0.06 H -0.58 Z M 0.12,-0.48 H 0.58 V -0.06 H 0.12 Z " +
                    "M -0.58,0.16 H -0.12 V 0.58 H -0.58 Z M 0.12,0.16 H 0.58 V 0.58 H 0.12 Z"),                                    // deposito
            13 => P("M -0.9,0.85 V -0.65 H -0.58 V -0.3 H -0.18 V -0.65 H 0.18 V -0.3 H 0.58 V -0.65 H 0.9 V 0.85 Z"),              // fortificazione
            14 => P("F0 M -0.95,0.8 L 0,-0.85 0.95,0.8 Z M -0.45,0.52 L 0,-0.28 0.45,0.52 Z"),                                     // paesaggio
            15 or 26 => P("M 0,-0.9 L 0.9,0 0,0.9 -0.9,0 Z"),                                                                     // incrocio, strada
            16 => P("F0 M -0.95,-0.7 H 0.95 L 0.38,0.15 V 0.95 H -0.38 V 0.15 Z M -0.16,-0.3 H 0.16 V 0.02 H -0.16 Z"),            // radiofaro
            17 => Trefoil(),                                                                                                      // nucleare
            18 => P("M -0.95,0.65 L 0,-0.65 0.95,0.65 H 0.55 L 0,-0.1 -0.55,0.65 Z"),                                              // passo
            19 => P("M -0.09,-0.55 H 0.09 V 0.8 H -0.09 Z M -0.5,-0.42 H 0.5 V -0.26 H -0.5 Z " +
                    "M -0.78,0.22 L -0.6,0.16 C -0.5,0.62 0.5,0.62 0.6,0.16 L 0.78,0.22 C 0.68,0.98 -0.68,0.98 -0.78,0.22 Z " +
                    "M -0.24,-0.78 A 0.24,0.24 0 1 1 0.24,-0.78 A 0.24,0.24 0 1 1 -0.24,-0.78 Z"),                               // porto: ancora
            20 => P("M 0.22,-1 L -0.62,0.16 -0.06,0.16 -0.28,1 0.62,-0.22 0.06,-0.22 Z"),                                          // centrale: fulmine
            21 => P("F0 M -1,-0.72 H 1 V 0.28 H -1 Z M -0.78,-0.5 H -0.12 V -0.32 H -0.78 Z M 0.12,-0.5 H 0.78 V -0.32 H 0.12 Z " +
                    "M -0.78,-0.12 H -0.12 V 0.06 H -0.78 Z M 0.12,-0.12 H 0.78 V 0.06 H 0.12 Z M -0.13,0.28 H 0.13 V 1 H -0.13 Z"),   // radar
            22 => P("F0 M -0.75,1 L -0.08,-1 H 0.08 L 0.75,1 Z M -0.32,0.78 L 0,-0.18 0.32,0.78 Z"),                              // antenna
            23 or 24 => P("M -1,-0.38 H 1 V -0.2 H -1 Z M -1,0.2 H 1 V 0.38 H -1 Z M -0.78,-0.62 H -0.56 V 0.62 H -0.78 Z " +
                          "M -0.11,-0.62 H 0.11 V 0.62 H -0.11 Z M 0.56,-0.62 H 0.78 V 0.62 H 0.56 Z"),                             // ferrovia
            30 => P("M -0.72,-0.85 H -0.36 V -0.16 H 0.36 V -0.85 H 0.72 V 0.85 H 0.36 V 0.16 H -0.36 V 0.85 H -0.72 Z"),          // HARTS
            31 => P("M -0.66,0.6 V -0.55 L -0.52,-0.85 -0.38,-0.55 V 0.6 Z M -0.14,0.6 V -0.72 L 0,-1 0.14,-0.72 V 0.6 Z " +
                    "M 0.38,0.6 V -0.55 L 0.52,-0.85 0.66,-0.55 V 0.6 Z M -0.85,0.66 H 0.85 V 0.95 H -0.85 Z"),                      // SAM
            _ => P("M -0.7,0 A 0.7,0.7 0 1 1 0.7,0 A 0.7,0.7 0 1 1 -0.7,0 Z"),
        };
        g.Freeze();
        Cache[type] = g;
        return g;
    }

    private static Geometry P(string s) => Geometry.Parse(s);

    /// <summary>Simbolo nucleare: punto centrale e tre spicchi a 120 gradi.</summary>
    private static Geometry Trefoil()
    {
        var gg = new GeometryGroup { FillRule = FillRule.Nonzero };
        gg.Children.Add(new EllipseGeometry(new Point(0, 0), 0.2, 0.2));
        for (int k = 0; k < 3; k++)
        {
            double a0 = (-90 - 30 + k * 120) * Math.PI / 180, a1 = (-90 + 30 + k * 120) * Math.PI / 180;
            Point P0(double r, double a) => new(r * Math.Cos(a), r * Math.Sin(a));
            var f = new PathFigure { StartPoint = P0(0.32, a0), IsClosed = true, IsFilled = true };
            f.Segments.Add(new LineSegment(P0(0.98, a0), true));
            f.Segments.Add(new ArcSegment(P0(0.98, a1), new Size(0.98, 0.98), 0, false, SweepDirection.Clockwise, true));
            f.Segments.Add(new LineSegment(P0(0.32, a1), true));
            f.Segments.Add(new ArcSegment(P0(0.32, a0), new Size(0.32, 0.32), 0, false, SweepDirection.Counterclockwise, true));
            gg.Children.Add(new PathGeometry(new[] { f }));
        }
        return gg;
    }

    // ---- stile NATO (APP-6 / MIL-STD-2525): cornice = parte (amico, nemico, neutrale) vista dalla squadra scelta, segno nero dentro ----

    /// <summary>Parti APP-6: 1 amico, 2 nemico, 3 neutrale (0 = nessuna, usa il colore della squadra).</summary>
    public static readonly Color[] AffColors = { Colors.White, Color.FromRgb(0x80, 0xE0, 0xFF), Color.FromRgb(0xFF, 0x80, 0x80), Color.FromRgb(0xAA, 0xFF, 0xAA) };
    private static readonly Brush[] AffBrushes = AffColors.Select(c => { var b = new SolidColorBrush(c); b.Freeze(); return (Brush)b; }).ToArray();
    public static Brush AffBrush(int aff) => AffBrushes[Math.Clamp(aff, 0, 3)];
    private static readonly Dictionary<int, Geometry> NatoGlyphs = new();
    private static readonly Geometry[] Frames =
    {
        Fr("M -1.2,-0.8 H 1.2 V 0.8 H -1.2 Z"), Fr("M -1.2,-0.8 H 1.2 V 0.8 H -1.2 Z"),       // amico: rettangolo
        Fr("M 0,-1.25 L 1.25,0 0,1.25 -1.25,0 Z"),                                              // nemico: rombo
        Fr("M -0.95,-0.95 H 0.95 V 0.95 H -0.95 Z"),                                            // neutrale: quadrato
    };
    private static readonly double[] FrameTops = { -0.8, -0.8, -1.25, -0.95 };
    private static Geometry Fr(string s) { var g = Geometry.Parse(s); g.Freeze(); return g; }

    /// <summary>Segno del tipo per lo stile NATO: base aerea = due piste oblique parallele, pista = una sola; il resto come le icone BMS.</summary>
    private static Geometry NatoGlyph(int type)
    {
        if (NatoGlyphs.TryGetValue(type, out var g)) return g;
        g = type switch
        {
            1 => Fr("M -0.742,0.864 L 0.348,-0.692 L 0.102,-0.864 L -0.988,0.692 Z M -0.102,0.864 L 0.988,-0.692 L 0.742,-0.864 L -0.348,0.692 Z"),
            2 => Fr("M -0.426,0.922 L 0.721,-0.716 L 0.426,-0.922 L -0.721,0.716 Z"),
            _ => Shape(type),
        };
        NatoGlyphs[type] = g;
        return g;
    }

    /// <summary>Icona NATO dell'obiettivo: cornice della parte, barretta nera di "installazione" sopra, segno del tipo; larga circa size pixel.</summary>
    public static void DrawNato(DrawingContext dc, int type, Point c, double size, int aff)
    {
        aff = Math.Clamp(aff, 1, 3);
        double k = size / 2.6;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, c.X, c.Y));
        dc.DrawGeometry(AffBrushes[aff], new Pen(Brushes.Black, 1.1 / k), Frames[aff]);
        dc.DrawRectangle(Brushes.Black, null, new Rect(-0.35, FrameTops[aff] - 0.28, 0.7, 0.28));
        dc.PushTransform(new ScaleTransform(0.55, 0.55));
        dc.DrawGeometry(Brushes.Black, null, NatoGlyph(type));
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Disegna l'icona del tipo centrata in c, larga size pixel; bordo scuro di 1 pixel.</summary>
    public static void Draw(DrawingContext dc, int type, Point c, double size, Brush fill, Brush? rim = null)
    {
        double k = size / 2;
        dc.PushTransform(new MatrixTransform(k, 0, 0, k, c.X, c.Y));
        dc.DrawGeometry(fill, new Pen(rim ?? Brushes.Black, 1.1 / k), Shape(type));
        dc.Pop();
    }
}
