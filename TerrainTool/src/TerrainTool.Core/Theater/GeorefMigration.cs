using System.Globalization;
using System.Text;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Theater;

/// <summary>
/// Moves a conversion project from the old add-on's georeference (Theater.Warp) to the standard transverse Mercator that
/// BMS uses for lat/long (centre and size of Theater.txt, k 0.9996, as Korea), so that DED, DTC and Tacview coordinates are
/// right everywhere. The old georeference stays as Theater.SourceGeoref: the old add-on's objectives and campaign units are
/// carried over through lat/lon (Objects job, Apply object changes). The project's own files in theater feet are converted
/// once, here; files in lat/lon need nothing. A copy of every file changed goes to Backup\georef_&lt;date&gt;.
/// </summary>
public static class GeorefMigration
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Project files with positions in theater feet: file, then (north column, east column) pairs.</summary>
    private static readonly (string File, (string N, string E)[] Cols)[] CsvFiles =
    {
        ("objective_edits.csv", new[] { ("PositionX", "PositionY") }),
        ("coastal_moves.csv", new[] { ("X (ft)", "Y (ft)"), ("New X (ft)", "New Y (ft)") }),
        ("bridges.csv", new[] { ("X (ft)", "Y (ft)"), ("New X (ft)", "New Y (ft)") }),
        ("real_places.csv", new[] { ("X (ft)", "Y (ft)") }),
    };

    public static bool CanMigrate(TheaterProject p) => p.Theater.Warp != null && p.Theater.SourceGeoref == null;

    /// <summary>
    /// Converts the project. Afterwards the terrain has to be rebuilt (all GIS steps, Terrain Builder, textures), then
    /// Run Objects and Apply object changes, which carry the old add-on's objectives and units over.
    /// </summary>
    public static List<string> Migrate(TheaterProject p, string projectFile)
    {
        if (!CanMigrate(p)) throw new InvalidOperationException("The project has no old georeference to convert from, or was already converted.");
        var notes = new List<string>();
        var t = p.Theater;
        // the theater centre is the old grid's centre: the new grid covers the same area
        var (clat, clon) = t.Warp!.Inverse(t.SizeMeters / 2, t.SizeMeters / 2);
        string backup = Path.Combine(p.ProjectFolder, "Backup", "georef_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Ci));
        Directory.CreateDirectory(backup);
        void Keep(string f)
        {
            string rel = Path.GetRelativePath(p.ProjectFolder, f);
            string dst = Path.Combine(backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
        if (File.Exists(projectFile)) Keep(projectFile);

        var old = t.Warp;
        t.SourceGeoref = old;
        t.Warp = null;
        t.CenterLat = Math.Round(clat, 6);
        t.CenterLon = Math.Round(clon, 6);
        var g = t.CreateGrid();
        notes.Add($"Georeference: standard transverse Mercator, centre {t.CenterLat.ToString("0.######", Ci)}°N {t.CenterLon.ToString("0.######", Ci)}°E, k {t.ScaleFactor.ToString("0.####", Ci)} (as BMS computes lat/long)");
        notes.Add($"Old georeference kept as the source of the old add-on's positions: {old!.Description}");

        foreach (var (name, cols) in CsvFiles)
        {
            string f = Path.Combine(p.ProjectFolder, name);
            if (!File.Exists(f)) continue;
            Keep(f);
            int n = ConvertCsv(f, cols, g);
            notes.Add($"{name}: {n} positions converted");
        }

        string wp = Path.Combine(p.ProjectFolder, "water_paint.csv");
        if (File.Exists(wp)) { Keep(wp); notes.Add($"water_paint.csv: {ConvertWaterPaint(wp, g)} points converted"); }

        string ne = Path.Combine(p.ProjectFolder, NetworkEdits.FileName);
        if (File.Exists(ne))
        {
            Keep(ne);
            var net = NetworkEdits.Load(ne);
            for (int i = 0; i < net.Added.Count; i++)
            {
                var a = net.Added[i];
                var (n2, e2) = g.FromSourceFt(a.NorthFt, a.EastFt);
                net.Added[i] = a with { NorthFt = n2, EastFt = e2 };
            }
            net.Save(ne);
            notes.Add($"{NetworkEdits.FileName}: {net.Added.Count} new objectives converted");
        }

        // campaign files saved by the Campaign Editor: units, waypoints and bullseye (objectives are rewritten by Apply object changes)
        string ce = Path.Combine(p.ProjectFolder, "CampaignEdits");
        if (Directory.Exists(ce))
        {
            var db = CampaignDb.Load(ObjectChangeApplier.DbDirs(p).Where(Directory.Exists));
            foreach (var f in Directory.EnumerateFiles(ce).Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)))
            {
                Keep(f);
                var d = CampaignDoc.Load(f, db.ClassOf);
                var log = Reproject(d, g);
                d.Save(f);
                notes.Add($"CampaignEdits\\{Path.GetFileName(f)}: {log}");
            }
        }

        if (Directory.Exists(ce))
            foreach (var f in Directory.EnumerateFiles(ce, "*.pak"))
            {
                Keep(f);
                if (ReprojectPak(File.ReadAllBytes(f), g) is { } pk) { File.WriteAllBytes(f, pk); notes.Add($"CampaignEdits\\{Path.GetFileName(f)}: campaign regions resampled"); }
            }

        p.Save(projectFile);
        notes.Add($"Copies of the files before the change: {backup}");
        notes.Add("Next: rebuild the terrain (all GIS steps, Terrain Builder, textures), then Run Objects and, in the Campaign Editor, Apply object changes.");
        return notes;
    }

    /// <summary>
    /// Units, waypoints and bullseye of a campaign or TE file, from the old add-on's grid to this theater's (campaign grid cells).
    /// Objectives are not touched: Apply object changes writes their positions. Returns a one-line summary.
    /// </summary>
    public static string Reproject(CampaignDoc d, TheaterGrid g)
    {
        if (!g.Reprojects) return "same grid, nothing to do";
        int units = 0, wps = 0;
        if (d.Units != null && d.Units.Error == null)
        {
            foreach (var u in d.Units.Units)
            {
                var (x, y) = g.FromSourceGrid(u.X, u.Y);
                if (x != u.X || y != u.Y) { d.Units.SetPosition(u, x, y); units++; }
                for (int i = 0; i < u.Waypoints.Count && i < u.WpAt.Length; i++)
                {
                    var w = u.Waypoints[i];
                    var (wx, wy) = g.FromSourceGrid(w.X, w.Y);
                    if (wx == w.X && wy == w.Y) continue;
                    d.Units.SetWaypoint(u, i, (short)wx, (short)wy, w.Arrive, d.Units.WaypointDepart(u, i));
                    wps++;
                }
            }
        }
        string bulls = d.ReprojectBullseye(g) is { } b ? $", bullseye {b}" : "";
        string err = d.Units?.Error != null ? $" (units not readable: {d.Units.Error})" : "";
        return $"{units} units and {wps} waypoints moved{bulls}{err}";
    }

    /// <summary>
    /// Campaign regions (&lt;theater&gt;.pak: one byte per cell, row 0 = north, square) resampled from the old add-on's grid:
    /// each new cell takes the region of the old cell at the same place on the ground. Null if the file is not square.
    /// </summary>
    public static byte[]? ReprojectPak(byte[] old, TheaterGrid g)
    {
        int ps = (int)Math.Round(Math.Sqrt(old.Length));
        if (ps * ps != old.Length || g.Config.SourceGeoref is not { } src) return null;
        double cell = g.Config.SizeMeters / ps;
        var res = new byte[old.Length];
        for (int r = 0; r < ps; r++)
            for (int cx = 0; cx < ps; cx++)
            {
                double x = (cx + 0.5) * cell, y = (ps - r - 0.5) * cell;
                var (lat, lon) = g.ToLatLon(x, y);
                var (ox, oy) = src.Forward(lat, lon);
                int ocx = Math.Clamp((int)Math.Floor(ox / cell), 0, ps - 1);
                int ocy = Math.Clamp((int)Math.Floor(oy / cell), 0, ps - 1);
                res[r * ps + cx] = old[(ps - 1 - ocy) * ps + ocx];
            }
        return res;
    }

    private static int ConvertCsv(string path, (string N, string E)[] cols, TheaterGrid g)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl);
        if (lines.Length == 0) return 0;
        var head = lines[0].Split(';');
        var idx = cols.Select(c => (N: Array.IndexOf(head, c.N), E: Array.IndexOf(head, c.E))).Where(c => c.N >= 0 && c.E >= 0).ToList();
        int n = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var f = lines[i].Split(';');
            bool changed = false;
            foreach (var (cn, cE) in idx)
            {
                if (cn >= f.Length || cE >= f.Length) continue;
                if (!double.TryParse(f[cn], NumberStyles.Float, Ci, out double north) || !double.TryParse(f[cE], NumberStyles.Float, Ci, out double east)) continue;
                var (n2, e2) = g.FromSourceFt(north, east);
                // same number of decimals as before
                f[cn] = Fmt(n2, f[cn]); f[cE] = Fmt(e2, f[cE]);
                changed = true; n++;
            }
            if (changed) lines[i] = string.Join(';', f);
        }
        var outText = string.Join(nl, lines);
        File.WriteAllBytes(path, (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(outText)).ToArray());
        return n;
    }

    private static string Fmt(double v, string like)
    {
        int dot = like.IndexOf('.');
        int dec = dot < 0 ? 0 : like.Length - dot - 1;
        return v.ToString(dec == 0 ? "0" : "0." + new string('0', dec), Ci);
    }

    /// <summary>water_paint.csv: third column "north,east|north,east|..." in theater feet.</summary>
    private static int ConvertWaterPaint(string path, TheaterGrid g)
    {
        var lines = File.ReadAllLines(path);
        int n = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split(';');
            if (f.Length < 3) continue;
            var pts = f[2].Split('|');
            for (int k = 0; k < pts.Length; k++)
            {
                var ne = pts[k].Split(',');
                if (ne.Length != 2 || !double.TryParse(ne[0], NumberStyles.Float, Ci, out double north) || !double.TryParse(ne[1], NumberStyles.Float, Ci, out double east)) continue;
                var (n2, e2) = g.FromSourceFt(north, east);
                pts[k] = n2.ToString("0", Ci) + "," + e2.ToString("0", Ci);
                n++;
            }
            f[2] = string.Join('|', pts);
            lines[i] = string.Join(';', f);
        }
        File.WriteAllLines(path, lines);
        return n;
    }
}
