using System.Globalization;
using System.Text;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Airbase alignment report: for every airbase and airstrip, how far the main runway of its layout (as placed now in the
/// editor) is from the real runway of airbase_targets.csv (OurAirports or OpenStreetMap). Centre offset split along and
/// across the real runway, angle, lengths and runway ends. Read only: nothing is moved.
/// </summary>
public static class AirbaseAlignment
{
    public const double FtToM = 0.3048;
    /// <summary>Limits of the status: centre offset in metres and angle in degrees.</summary>
    public const double WarnM = 50, BadM = 200, WarnDeg = 0.5, BadDeg = 2.0;

    public enum Level { Ok, Warn, Bad, NoData }

    public sealed record Row(EdObjective Objective, Level Status, string Source, double? CentreM, double? AlongM, double? AcrossM,
                             double? AngleDeg, double? LayoutLenM, double? RealLenM, double? EndsM, string Note)
    {
        public int Id => Objective.Id;
        public string Name => Objective.Name;
        public string Type => Objective.TypeName;
        public string StatusText => Status switch { Level.Ok => "OK", Level.Warn => "check", Level.Bad => "OFF", _ => "no data" };
    }

    public static bool IsAirbase(EdObjective o) => !o.Junction && (o.Type is 1 or 2 || o.SwapOcd >= 0);

    public static List<Row> Build(EditorData d) =>
        d.Objectives.Where(IsAirbase).Select(o => Compute(d, o))
            .OrderBy(r => r.Status switch { Level.Bad => 0, Level.Warn => 1, Level.Ok => 2, _ => 3 })
            .ThenByDescending(r => r.CentreM ?? -1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static Row Compute(EditorData d, EdObjective o)
    {
        var rw = d.MainRunway(o);
        if (o.Real is not { } real)
            return new Row(o, Level.NoData, "", null, null, null, null, rw is Runway r0 ? r0.Len * FtToM : null, null, null,
                           "no real runway in airbase_targets.csv: run 'Real runways from OpenStreetMap' or type it in tab 2");
        if (rw is not Runway r)
            return new Row(o, Level.NoData, real.Source, null, null, null, null, null, real.LenFt * FtToM, null, "the layout has no runway");

        // offsets in theater feet: X = north, Y = east; headings clockwise from north on the map grid
        double h = real.MapHdg * Math.PI / 180, ch = Math.Cos(h), sh = Math.Sin(h);
        double dx = r.X - real.X, dy = r.Y - real.Y;
        double along = (dx * ch + dy * sh) * FtToM, across = (-dx * sh + dy * ch) * FtToM;
        double centre = Math.Sqrt(along * along + across * across);
        double angle = ((real.MapHdg - r.Hdg) % 180 + 270) % 180 - 90;   // -90..90, positive = layout to turn clockwise

        // runway ends: pair them the way that fits best (a runway works in both directions)
        var (la, lb) = Ends(r.X, r.Y, r.Hdg, r.Len);
        var (ra, rb) = Ends(real.X, real.Y, real.MapHdg, real.LenFt);
        double same = Math.Max(Dist(la, ra), Dist(lb, rb)), swapped = Math.Max(Dist(la, rb), Dist(lb, ra));
        double ends = Math.Min(same, swapped) * FtToM;

        double absAng = Math.Abs(angle);
        var level = centre > BadM || absAng > BadDeg ? Level.Bad : centre > WarnM || absAng > WarnDeg ? Level.Warn : Level.Ok;
        double lenDiff = (r.Len - real.LenFt) * FtToM;
        string note = Math.Abs(lenDiff) > 300 ? $"layout runway {Math.Abs(lenDiff):0} m {(lenDiff < 0 ? "shorter" : "longer")} than the real one" : "";
        if (level == Level.Bad && centre > 600 && absAng < BadDeg)
            note = Join(note, "parallel offset: the layout's longest runway may be another real runway, or the base is not aligned");
        if (o.HadEdit) note = Join(note, "saved edit (wins over the automatic alignment)");
        return new Row(o, level, real.Source, centre, along, across, angle, r.Len * FtToM, real.LenFt * FtToM, ends, note);
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "; " + b;

    private static ((double X, double Y), (double X, double Y)) Ends(double x, double y, double hdg, double len)
    {
        double h = hdg * Math.PI / 180, half = len / 2;
        return ((x + Math.Cos(h) * half, y + Math.Sin(h) * half), (x - Math.Cos(h) * half, y - Math.Sin(h) * half));
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Semicolon CSV of the report (opens in Excel with the invariant decimal point).</summary>
    public static string ToCsv(IEnumerable<Row> rows)
    {
        var ci = CultureInfo.InvariantCulture;
        string F(double? v, string fmt) => v is double x ? x.ToString(fmt, ci) : "";
        var sb = new StringBuilder("Status;CampId;Name;Type;Source;CentreM;AlongM;AcrossM;AngleDeg;LayoutLenM;RealLenM;EndsM;Note\r\n");
        foreach (var r in rows)
            sb.Append(r.StatusText).Append(';').Append(r.Id.ToString(ci)).Append(';').Append(r.Name.Replace(';', ',')).Append(';').Append(r.Type).Append(';')
              .Append(r.Source).Append(';').Append(F(r.CentreM, "0")).Append(';').Append(F(r.AlongM, "0")).Append(';').Append(F(r.AcrossM, "0")).Append(';')
              .Append(F(r.AngleDeg, "0.00")).Append(';').Append(F(r.LayoutLenM, "0")).Append(';').Append(F(r.RealLenM, "0")).Append(';')
              .Append(F(r.EndsM, "0")).Append(';').Append(r.Note.Replace(';', ',')).Append("\r\n");
        return sb.ToString();
    }
}
