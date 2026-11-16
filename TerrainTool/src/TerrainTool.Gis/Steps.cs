using System.Globalization;
using System.Text;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;

namespace TerrainTool.Gis;

public sealed class StepResult
{
    public List<string> Outputs { get; } = new();
    public string? Preview { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>Un passo di elaborazione (al posto di un lavoro in QGIS/Photoshop).</summary>
public abstract class ProcessingStep
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    /// <summary>Cosa serve per partire; null se è tutto presente.</summary>
    public abstract string? MissingInputs(GisContext ctx);
    public abstract StepResult Run(GisContext ctx, IProgress<StepProgress> p);

    protected static string Pct(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static IReadOnlyList<ProcessingStep> All { get; } = new ProcessingStep[]
    {
        new HeightmapStep(), new WatermapStep(), new CoastlineStep(), new OceanDepthStep(),
        new TreeCoverStep(), new LandCoverStep(), new RoadsStep(), new ImageryStep(),
        new Sentinel2PhotorealStep(testArea: true), new Sentinel2PhotorealStep(testArea: false),
    };

    // ───────── aiuti comuni ─────────

    /// <summary>Trova i file .gpkg degli estratti Geofabrik (estraendo gli zip) e il layer che contiene 'layerKey'.</summary>
    protected static List<(string Path, string Layer)> GeofabrikLayers(GisContext ctx, string layerKey)
    {
        var res = new List<(string, string)>();
        foreach (var zip in ctx.SourceFiles(@"Sources\OSM", "*.zip"))
        {
            string dir = ctx.ExtractZip(zip);
            foreach (var gpkg in Directory.EnumerateFiles(dir, "*.gpkg", SearchOption.AllDirectories))
            {
                using var ds = Ogr.Open(gpkg, 0);
                for (int i = 0; i < ds.GetLayerCount(); i++)
                {
                    string name = ds.GetLayerByIndex(i).GetName();
                    if (name.Contains(layerKey, StringComparison.OrdinalIgnoreCase)) res.Add((gpkg, name));
                }
            }
        }
        return res;
    }

    protected static string? FindFile(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault() : null;

    /// <summary>Copia un raster ENVI riga per riga applicando una funzione ai byte.</summary>
    protected static void MapBytes(string src, string dst, int size, Func<byte, byte> f, GisContext ctx, IProgress<StepProgress> p, string text, int offset = 0)
    {
        using var ds = GisContext.OpenRaster(src);
        var band = ds.GetRasterBand(1);
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = f((byte)i);
        using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
        const int rows = 256;
        var buf = new byte[size * rows];
        for (int y = 0; y < size; y += rows)
        {
            ctx.Token.ThrowIfCancellationRequested();
            int h = Math.Min(rows, size - y);
            band.ReadRaster(offset, y + offset, size, h, buf, size, h, 0, 0);
            for (int i = 0; i < size * h; i++) buf[i] = lut[buf[i]];
            fs.Write(buf, 0, size * h);
            p.Report(new StepProgress((double)(y + h) / size, text));
        }
        fs.Close();
        GisContext.WriteEnviHeader(dst, size, 1);
    }

    /// <summary>Riduce un RAW 8 bit di un fattore intero (media) e lo scrive come RAW.</summary>
    protected static void DownsampleRaw(string src, int size, int factor, string dst, GisContext ctx)
    {
        int outSize = size / factor;
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
        var rows = new byte[size * factor];
        var acc = new int[outSize];
        var line = new byte[outSize];
        for (int oy = 0; oy < outSize; oy++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            input.ReadExactly(rows, 0, rows.Length);
            Array.Clear(acc);
            for (int r = 0; r < factor; r++)
            {
                int o = r * size;
                for (int x = 0; x < size; x++) acc[x / factor] += rows[o + x];
            }
            int div = factor * factor;
            for (int x = 0; x < outSize; x++) line[x] = (byte)((acc[x] + div / 2) / div);
            output.Write(line, 0, outSize);
        }
    }

    protected static string Mb(string path) => (new FileInfo(path).Length / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

// ═══════════════════════════ 1. HEIGHTMAP ═══════════════════════════

public sealed class HeightmapStep : ProcessingStep
{
    public override string Id => "heightmap";
    public override string Name => "Heightmap (DEM)";
    public override string Description => "Copernicus DEM → theater grid, int16 feet RAW. Replaces Terrain Builder DEM mode; feed it to TB Water DEM mode.";

    /// <summary>
    /// Tile GLO-30; dove manca (GLO-30 non pubblica alcuni paesi, es. Armenia e Azerbaigian) si usa la GLO-90 della stessa cella.
    /// </summary>
    private static List<string> Tiles(GisContext ctx)
    {
        var t30 = ctx.SourceFiles(@"Sources\DEM\COP30", "*.tif");
        var t90 = ctx.SourceFiles(@"Sources\DEM\COP90", "*.tif");
        // chiave della cella: "N38_00_E045_00" dal nome Copernicus_DSM_COG_10_N38_00_E045_00_DEM.tif
        static string Key(string f)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"[NS]\d\d_00_[EW]\d\d\d_00");
            return m.Success ? m.Value : Path.GetFileName(f);
        }
        var have = new HashSet<string>(t30.Select(Key));
        var res = new List<string>(t30);
        res.AddRange(t90.Where(f => !have.Contains(Key(f))));
        return res;
    }

    public override string? MissingInputs(GisContext ctx) => Tiles(ctx).Count == 0 ? "Copernicus DEM tiles (GLO-30 or GLO-90)" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        var tiles = Tiles(ctx);
        int n90 = tiles.Count(f => f.Contains("COG_30"));
        ctx.Log?.Report($"  {tiles.Count} DEM tiles ({n90} GLO-90 tiles where GLO-30 is not published)");
        string vrt = ctx.BuildVrt(tiles, "dem", p, "Building DEM mosaic");
        string tmp = ctx.Work($"dem_m_{ctx.N}.tif");
        int pad = ctx.WarpToTheater(vrt, tmp, ctx.Size, "average", "Float32", "GTiff", p, "Reprojecting DEM", 0, 0.8,
            "-co", "TILED=YES", "-co", "COMPRESS=DEFLATE", "-co", "PREDICTOR=3", "-co", "BIGTIFF=YES");

        // metri → piedi, int16, con i limiti del teatro
        string outName = $"{ctx.Theater.Name}_{ctx.N}.RAW";
        string dst = ctx.Out(outName);
        double minFt = ctx.Theater.MinHeightFt, maxFt = ctx.Theater.MaxHeightFt;
        int size = ctx.Size, clipped = 0;
        double lo = double.MaxValue, hi = double.MinValue;
        using (var ds = GisContext.OpenRaster(tmp))
        using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            var band = ds.GetRasterBand(1);
            const int rows = 128;
            var buf = new float[size * rows];
            var outBuf = new byte[size * rows * 2];
            for (int y = 0; y < size; y += rows)
            {
                ctx.Token.ThrowIfCancellationRequested();
                int h = Math.Min(rows, size - y);
                band.ReadRaster(pad, y + pad, size, h, buf, size, h, 0, 0);
                for (int i = 0; i < size * h; i++)
                {
                    double ft = float.IsFinite(buf[i]) && buf[i] > -1000 ? buf[i] * 3.280839895 : 0;
                    if (ft < minFt) { ft = minFt; clipped++; } else if (ft > maxFt) { ft = maxFt; clipped++; }
                    if (ft < lo) lo = ft;
                    if (ft > hi) hi = ft;
                    short v = (short)Math.Round(ft);
                    outBuf[2 * i] = (byte)(v & 0xFF);
                    outBuf[2 * i + 1] = (byte)((v >> 8) & 0xFF);
                }
                fs.Write(outBuf, 0, size * h * 2);
                p.Report(new StepProgress(0.8 + 0.2 * (y + h) / size, "Writing heightmap (feet)"));
            }
        }
        GisContext.WriteEnviHeader(dst, size, 2);
        // controllo: pixel a quota esattamente 0 nelle prime/ultime righe (buchi di dati ai bordi?)
        using (var chk = GisContext.OpenRaster(tmp))
        {
            var band = chk.GetRasterBand(1);
            int rows = Math.Max(1, size / 64);
            var top = new float[size * rows];
            band.ReadRaster(pad, pad, size, rows, top, size, rows, 0, 0);
            double zeroTop = top.Count(v => v == 0) / (double)top.Length;
            res.Notes.Add($"Top edge: {zeroTop:P1} of the first {rows} rows at exactly 0 m (sea or missing data)");
        }
        res.Outputs.Add(dst);
        res.Notes.Add($"Heights {lo:0} … {hi:0} ft" + (clipped > 0 ? $", {clipped} pixels clipped to theater min/max" : ""));
        res.Preview = ctx.Preview(dst, "Heightmap", 0, Math.Max(1, hi));
        return res;
    }
}

// ═══════════════════════════ 2. WATERMAP ═══════════════════════════

public sealed class WatermapStep : ProcessingStep
{
    public override string Id => "watermap";
    public override string Name => "Watermap";
    public override string Description => "Inland water (OSM, value 25) + sea (OSM water polygons, value 45), 8-bit RAW for TB Water DEM mode. Estuaries and lagoons connected to the sea within 20 km of it become sea.";

    public override string? MissingInputs(GisContext ctx)
    {
        var miss = new List<string>();
        if (ctx.SourceFiles(@"Sources\Water\Sea", "*.zip").Count == 0) miss.Add("OSM water polygons");
        if (ctx.SourceFiles(@"Sources\OSM", "*.zip").Count == 0) miss.Add("Geofabrik OSM extracts");
        return miss.Count == 0 ? null : string.Join(", ", miss);
    }

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        string dst = ctx.Out("Watermap.raw");
        using (var target = ctx.CreateRaw(dst, ctx.Size, DataType.GDT_Byte))
        {
            var inland = GeofabrikLayers(ctx, "water_a");
            int i = 0;
            foreach (var (path, layer) in inland)
            {
                string name = "water_inland_" + i++;
                ctx.Log?.Report($"  Inland water: {Path.GetFileNameWithoutExtension(path)}");
                string v = ctx.ToTheaterVector(path, layer, name, p, "Reprojecting inland water " + i,
                    where: "fclass NOT IN ('wetland','glacier')");
                ctx.Rasterize(target, v, name, new[] { "-burn", "25" }, p, "Rasterizing inland water " + i);
            }
            res.Notes.Add($"{inland.Count} inland water layers");

            string seaDir = ctx.ExtractZip(ctx.SourceFiles(@"Sources\Water\Sea", "*.zip")[0]);
            string shp = FindFile(seaDir, "*.shp") ?? throw new IOException("Water polygons .shp not found");
            string sea = ctx.ToTheaterVector(shp, null, "water_sea", p, "Reprojecting sea polygons");
            ctx.Rasterize(target, sea, "water_sea", new[] { "-burn", "45" }, p, "Rasterizing sea");
            target.FlushCache();
        }
        if (CoastalWaterKm > 0)
        {
            long n = MergeCoastalWater(dst, ctx, p);
            double km2 = n * Math.Pow(ctx.Theater.SizeMeters / ctx.Size, 2) / 1e6;
            res.Notes.Add($"Estuaries/lagoons: {n:N0} inland-water pixels ({km2:0} km²) connected to the sea within {CoastalWaterKm:0} km of it set to sea (45)");
        }
        // acqua aggiunta o tolta a mano nell'editor (water_paint.csv), solo a piena dimensione
        if (ctx.Size == ctx.Theater.MapSizePixels)
        {
            string? wp = TerrainTool.Core.Editor.WaterPaint.ApplyToWatermap(ctx.Project.ProjectFolder, dst, ctx.Theater.SizeMeters, watermapIsFresh: true);
            if (wp != null) res.Notes.Add(wp);
        }
        res.Outputs.Add(dst);
        res.Preview = ctx.Preview(dst, "Watermap", 0, 45);
        return res;
    }

    /// <summary>
    /// Acque interne (25) collegate al mare ed entro questa distanza dal mare diventano mare (45):
    /// estuari, khor e lagune. Altrimenti TB le rende come laghi piatti con bordi a gradini (Shatt al-Arab).
    /// 0 = disattivato.
    /// </summary>
    public double CoastalWaterKm { get; set; } = 20;

    private long MergeCoastalWater(string path, GisContext ctx, IProgress<StepProgress> p)
    {
        int size = ctx.Size;
        p.Report(new StepProgress(0, "Estuaries: loading watermap"));
        byte[] w = File.ReadAllBytes(path);
        if (w.LongLength != (long)size * size) throw new InvalidDataException("Watermap.raw has an unexpected size");

        // zona costiera su una griglia grossolana (1/16): mare dilatato di CoastalWaterKm
        const int f = 16;
        int cs = (size + f - 1) / f;
        var coarse = new bool[cs * cs];
        Parallel.For(0, cs, cy =>
        {
            for (int cx = 0; cx < cs; cx++)
            {
                bool any = false;
                for (int y = cy * f; y < Math.Min(size, cy * f + f) && !any; y++)
                {
                    long row = (long)y * size;
                    for (int x = cx * f; x < Math.Min(size, cx * f + f); x++)
                        if (w[row + x] == 45) { any = true; break; }
                }
                coarse[cy * cs + cx] = any;
            }
        });
        double cellM = ctx.Theater.SizeMeters / size * f;
        int r = (int)Math.Ceiling(CoastalWaterKm * 1000 / cellM);
        var tmp = new bool[coarse.Length];
        Parallel.For(0, cs, cy =>   // dilatazione orizzontale
        {
            int last = -1_000_000, next = 1_000_000;
            var nextIdx = new int[cs];
            for (int x = cs - 1; x >= 0; x--) { if (coarse[cy * cs + x]) next = x; nextIdx[x] = next; }
            for (int x = 0; x < cs; x++)
            {
                if (coarse[cy * cs + x]) last = x;
                tmp[cy * cs + x] = x - last <= r || nextIdx[x] - x <= r;
            }
        });
        var zone = new bool[coarse.Length];
        Parallel.For(0, cs, cx =>   // dilatazione verticale
        {
            int last = -1_000_000, next = 1_000_000;
            var nextIdx = new int[cs];
            for (int y = cs - 1; y >= 0; y--) { if (tmp[y * cs + cx]) next = y; nextIdx[y] = next; }
            for (int y = 0; y < cs; y++)
            {
                if (tmp[y * cs + cx]) last = y;
                zone[y * cs + cx] = y - last <= r || nextIdx[y] - y <= r;
            }
        });
        bool InZone(long i) { int y = (int)(i / size), x = (int)(i % size); return zone[(y / f) * cs + x / f]; }

        // riempimento dal mare dentro le acque interne della zona costiera (4-vicini)
        p.Report(new StepProgress(0.3, "Estuaries: finding inland water connected to the sea"));
        var stack = new Stack<long>();
        long changed = 0;
        void Try(long j)
        {
            if (w[j] == 25 && InZone(j)) { w[j] = 45; changed++; stack.Push(j); }
        }
        for (int y = 0; y < size; y++)
        {
            if ((y & 1023) == 0) { ctx.Token.ThrowIfCancellationRequested(); p.Report(new StepProgress(0.3 + 0.6 * y / size, "Estuaries: flood fill")); }
            long row = (long)y * size;
            for (int x = 0; x < size; x++)
            {
                long i = row + x;
                if (w[i] != 45) continue;
                bool edge = (x > 0 && w[i - 1] == 25) || (x < size - 1 && w[i + 1] == 25)
                         || (y > 0 && w[i - size] == 25) || (y < size - 1 && w[i + size] == 25);
                if (!edge) continue;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    long j = stack.Pop();
                    int jy = (int)(j / size), jx = (int)(j % size);
                    if (jx > 0) Try(j - 1);
                    if (jx < size - 1) Try(j + 1);
                    if (jy > 0) Try(j - size);
                    if (jy < size - 1) Try(j + size);
                }
            }
        }
        p.Report(new StepProgress(0.95, "Estuaries: writing watermap"));
        File.WriteAllBytes(path, w);
        return changed;
    }
}

// ═══════════════════════════ 3. COASTLINE ═══════════════════════════

public sealed class CoastlineStep : ProcessingStep
{
    /// <summary>Isolette con costa chiusa più corta di così (metri) vengono tolte.</summary>
    public double MinIslandPerimeter { get; set; } = 2000;
    /// <summary>Distanza tra i punti costa (metri), come "Convert lines to points" di SAGA.</summary>
    public double PointSpacing { get; set; } = 10;

    public override string Id => "coast";
    public override string Name => "Coastline + coast points";
    public override string Description => "OSM coastlines without tiny islands → coastline RAW (255) and CoastPoints.shp (a point every 10 m) for TB Water DEM mode.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\Water\Coast", "*.zip").Count == 0 ? "OSM coastlines" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        string dir = ctx.ExtractZip(ctx.SourceFiles(@"Sources\Water\Coast", "*.zip")[0]);
        string shp = FindFile(dir, "*.shp") ?? throw new IOException("Coastlines .shp not found");
        string coast = ctx.ToTheaterVector(shp, null, "coast_all", p, "Reprojecting coastlines", promoteMulti: false);

        // Filtra le isolette e scrive linee tenute + punti
        string kept = ctx.Work("coast_kept.gpkg");
        if (File.Exists(kept)) File.Delete(kept);
        string pts = ctx.Out("CoastPoints.shp");
        foreach (var ext in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg" })
            if (File.Exists(Path.ChangeExtension(pts, ext))) File.Delete(Path.ChangeExtension(pts, ext));

        using var srs = new SpatialReference("");
        srs.ImportFromProj4(ctx.Proj4);
        int total = 0, removed = 0;
        long points = 0, outside = 0;
        using (var src = Ogr.Open(coast, 0))
        using (var keptDs = Ogr.GetDriverByName("GPKG").CreateDataSource(kept, Array.Empty<string>()))
        using (var ptDs = Ogr.GetDriverByName("ESRI Shapefile").CreateDataSource(pts, Array.Empty<string>()))
        {
            var inLayer = src.GetLayerByIndex(0);
            var keptLayer = keptDs.CreateLayer("coast", srs, wkbGeometryType.wkbLineString, Array.Empty<string>());
            var ptLayer = ptDs.CreateLayer("CoastPoints", srs, wkbGeometryType.wkbPoint, Array.Empty<string>());
            ptLayer.CreateField(new FieldDefn("id", FieldType.OFTInteger), 1);
            keptLayer.StartTransaction();
            long count = Math.Max(1, inLayer.GetFeatureCount(1));
            inLayer.ResetReading();
            Feature? f;
            while ((f = inLayer.GetNextFeature()) != null)
            {
                using (f)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var g = f.GetGeometryRef();
                    if (g == null) continue;
                    foreach (var line in Lines(g))
                    {
                        total++;
                        int n = line.GetPointCount();
                        if (n < 2) continue;
                        bool closed = Math.Abs(line.GetX(0) - line.GetX(n - 1)) < 0.01 && Math.Abs(line.GetY(0) - line.GetY(n - 1)) < 0.01;
                        if (closed && line.Length() < MinIslandPerimeter) { removed++; continue; }
                        using (var kf = new Feature(keptLayer.GetLayerDefn()))
                        {
                            kf.SetGeometry(line);
                            keptLayer.CreateFeature(kf);
                        }
                        points += WritePoints(line, ptLayer, points, ctx.SizeMeters, ref outside);
                    }
                    if (total % 200 == 0) p.Report(new StepProgress(Math.Min(0.6, 0.6 * total / count), "Filtering islands, writing coast points"));
                }
            }
            keptLayer.CommitTransaction();
        }
        res.Notes.Add($"{total} coast lines, {removed} tiny islands removed (closed perimeter < {MinIslandPerimeter:0} m), {points} coast points ({outside} outside the theater skipped)");

        string dst = ctx.Out("Coastline.raw");
        using (var target = ctx.CreateRaw(dst, ctx.Size, DataType.GDT_Byte))
        {
            ctx.Rasterize(target, kept, "coast", new[] { "-burn", "255" }, p, "Rasterizing coastline");
            target.FlushCache();
        }
        res.Outputs.Add(dst);
        res.Outputs.Add(pts);
        res.Preview = ctx.Preview(dst, "Coastline", 0, 32);
        return res;
    }

    private static IEnumerable<Geometry> Lines(Geometry g)
    {
        var t = (wkbGeometryType)((int)g.GetGeometryType() & 0xFF);
        if (t == wkbGeometryType.wkbLineString) { yield return g; yield break; }
        for (int i = 0; i < g.GetGeometryCount(); i++)
            foreach (var l in Lines(g.GetGeometryRef(i))) yield return l;
    }

    // TB usa le coordinate dei punti come indici della mappa senza controlli: un punto fuori dal teatro lo fa chiudere.
    // Si tengono solo i punti ad almeno 1 pixel dal bordo.
    private long WritePoints(Geometry line, OSGeo.OGR.Layer layer, long startId, double size, ref long outside)
    {
        long written = 0;
        double margin = size / 32768.0;
        double lo = margin, hi = size - margin;
        void Put(double x, double y, ref long outsideRef)
        {
            if (x < lo || x > hi || y < lo || y > hi) { outsideRef++; return; }
            AddPoint(layer, x, y, startId + written++);
        }
        double carry = 0; // distanza percorsa dall'ultimo punto
        int n = line.GetPointCount();
        double px = line.GetX(0), py = line.GetY(0);
        Put(px, py, ref outside);
        for (int i = 1; i < n; i++)
        {
            double x = line.GetX(i), y = line.GetY(i);
            double seg = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            double d = PointSpacing - carry;
            while (d <= seg)
            {
                double t = d / seg;
                Put(px + (x - px) * t, py + (y - py) * t, ref outside);
                d += PointSpacing;
            }
            carry = seg - (d - PointSpacing);
            px = x; py = y;
        }
        return written;
    }

    private static void AddPoint(OSGeo.OGR.Layer layer, double x, double y, long id)
    {
        using var f = new Feature(layer.GetLayerDefn());
        f.SetField("id", (int)Math.Min(int.MaxValue, id));
        using var pt = new Geometry(wkbGeometryType.wkbPoint);
        pt.AddPoint_2D(x, y);
        f.SetGeometry(pt);
        layer.CreateFeature(f);
    }
}

// ═══════════════════════════ 4. OCEAN DEPTH ═══════════════════════════

public sealed class OceanDepthStep : ProcessingStep
{
    public int BlurRadius { get; set; } = 7; // ≈ raggio 15 a 32K, come nel manuale
    public override string Id => "depth";
    public override string Name => "Ocean depth";
    public override string Description => "GEBCO derived polygons → depth map at half size (16384), auto-levels + gaussian blur. Goes into the Green channel of DepthMap.dds.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\GEBCO", "*.zip").Count == 0 && ctx.SourceFiles(@"Sources\GEBCO", "*.shp").Count == 0 ? "GEBCO derived polygons" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        string? shp = ctx.SourceFiles(@"Sources\GEBCO", "*.shp").FirstOrDefault();
        if (shp == null)
        {
            string dir = ctx.ExtractZip(ctx.SourceFiles(@"Sources\GEBCO", "*.zip")[0]);
            shp = FindFile(dir, "*.shp") ?? throw new IOException("GEBCO .shp not found");
        }
        string v = ctx.ToTheaterVector(shp, null, "gebco", p, "Reprojecting GEBCO polygons");

        // campo della profondità (gridcode)
        string field;
        using (var ds = Ogr.Open(v, 0))
        {
            var defn = ds.GetLayerByIndex(0).GetLayerDefn();
            var names = Enumerable.Range(0, defn.GetFieldCount()).Select(i => defn.GetFieldDefn(i).GetName()).ToList();
            field = names.FirstOrDefault(n => n.Equals("gridcode", StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(n => n.Contains("depth", StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("No gridcode/depth field in GEBCO data: " + string.Join(", ", names));
        }

        int size = ctx.Size / 2;
        string tmp = ctx.Work($"gebco_{size}.raw");
        using (var target = ctx.CreateRaw(tmp, size, DataType.GDT_Float32))
        {
            ctx.Rasterize(target, v, "gebco", new[] { "-a", field }, p, "Rasterizing depth");
            target.FlushCache();
        }

        // auto-levels (min..max → 0..255) poi sfocatura gaussiana (3 passate di box blur)
        var data = new float[(long)size * size];
        using (var ds = GisContext.OpenRaster(tmp))
            ds.GetRasterBand(1).ReadRaster(0, 0, size, size, data, size, size, 0, 0);
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var x in data) { if (x < mn) mn = x; if (x > mx) mx = x; }
        float scale = mx > mn ? 255f / (mx - mn) : 0;
        var img = new byte[data.LongLength];
        for (long i = 0; i < data.LongLength; i++) img[i] = (byte)Math.Clamp((int)Math.Round((data[i] - mn) * scale), 0, 255);
        data = Array.Empty<float>();
        p.Report(new StepProgress(0.7, "Blurring depth"));
        Blur.Gaussian(img, size, BlurRadius, ctx.Token);

        string dst = ctx.Out($"OceanDepth_{size}.raw");
        File.WriteAllBytes(dst, img);
        GisContext.DeleteRaw(tmp);
        res.Outputs.Add(dst);
        res.Notes.Add($"Field '{field}', values {mn} … {mx} stretched to 0…255, blur radius {BlurRadius}");
        GisContext.WriteEnviHeader(dst, size, 1);
        res.Preview = ctx.Preview(dst, "OceanDepth", 0, 255);
        return res;
    }
}

// ═══════════════════════════ 5. TREE COVER ═══════════════════════════

public sealed class TreeCoverStep : ProcessingStep
{
    public override string Id => "trees";
    public override string Name => "Tree cover / forest density";
    public override string Description => "Hansen tree cover 2000 (0–100 %) → 0–255 RAW: TreesCover at full size plus ForestMap at 1/2 and 1/4 size.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\TreeCover", "*.tif").Count == 0 ? "Hansen tree cover tiles" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        string vrt = ctx.BuildVrt(ctx.SourceFiles(@"Sources\TreeCover", "*.tif"), "trees", p, "Building tree cover mosaic");
        string tmp = ctx.Work($"trees_{ctx.N}.raw");
        int pad = ctx.WarpToTheater(vrt, tmp, ctx.Size, "average", "Byte", "ENVI", p, "Reprojecting tree cover", 0, 0.7);
        string dst = ctx.Out("TreesCover.raw");
        MapBytes(tmp, dst, ctx.Size, b => (byte)Math.Min(255, (int)Math.Round(b * 2.55)), ctx, p, "Stretching 0–100 → 0–255", pad);
        GisContext.DeleteRaw(tmp);
        res.Outputs.Add(dst);
        foreach (int f in new[] { 2, 4 })
        {
            int s = ctx.Size / f;
            string fm = ctx.Out($"ForestMap{s}.raw");
            DownsampleRaw(dst, ctx.Size, f, fm, ctx);
            res.Outputs.Add(fm);
        }
        GisContext.WriteEnviHeader(dst, ctx.Size, 1);
        res.Preview = ctx.Preview(dst, "TreesCover", 0, 255);
        return res;
    }
}

// ═══════════════════════════ 6. LAND COVER ═══════════════════════════

public sealed class LandCoverStep : ProcessingStep
{
    public override string Id => "landcover";
    public override string Name => "Land cover";
    public override string Description => "ESA WorldCover 10 m → theater grid with majority filter, ESA codes (10…100). Also writes the TB mapping file for ESA codes.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\LandCover", "*.tif").Count == 0 ? "ESA WorldCover tiles" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        string vrt = ctx.BuildVrt(ctx.SourceFiles(@"Sources\LandCover", "*.tif"), "landcover", p, "Building land cover mosaic");
        string dst = ctx.Out("LandCover.raw");
        string tmp = ctx.Work($"landcover_{ctx.N}.raw");
        int pad = ctx.WarpToTheater(vrt, tmp, ctx.Size, "mode", "Byte", "ENVI", p, "Reprojecting land cover (majority)", 0, 0.85);
        GisContext.DeleteRaw(dst);
        MapBytes(tmp, dst, ctx.Size, b => b, ctx, new Progress<StepProgress>(_ => { }), "Cropping", pad);
        GisContext.DeleteRaw(tmp);

        // Il mare della Watermap (45) diventa codice 200 = WATER: ESA usa 80 sia per il mare sia per le acque interne
        string water = ctx.Out("Watermap.raw");
        if (File.Exists(water) && new FileInfo(water).Length == (long)ctx.Size * ctx.Size)
        {
            int sea = 0;
            using (var lc = new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 22))
            using (var wm = new FileStream(water, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22))
            {
                var a = new byte[ctx.Size];
                var w = new byte[ctx.Size];
                for (int y = 0; y < ctx.Size; y++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    long pos = (long)y * ctx.Size;
                    lc.Position = pos; lc.ReadExactly(a);
                    wm.ReadExactly(w);
                    bool changed = false;
                    for (int x = 0; x < ctx.Size; x++)
                        if (w[x] == 45 && a[x] != 200) { a[x] = 200; changed = true; sea++; }
                    if (changed) { lc.Position = pos; lc.Write(a); }
                    if ((y & 255) == 0) p.Report(new StepProgress(0.9 + 0.1 * y / ctx.Size, "Marking sea from the Watermap"));
                }
            }
            res.Notes.Add($"{sea} sea pixels set to code 200 (WATER) from the Watermap");
        }
        else res.Notes.Add("Watermap not found at this size: run the Watermap step first to mark the sea as WATER (code 200).");
        res.Outputs.Add(dst);
        string map = ctx.Out("LandCoverTypesMap_ESA.txt");
        File.WriteAllText(map, EsaMapping(), new UTF8Encoding(false));
        res.Outputs.Add(map);
        res.Notes.Add("Use LandCoverTypesMap_ESA.txt in TB LandCover mode instead of the Copernicus conversion file (which uses LC100 codes).");
        res.Preview = ctx.Preview(dst, "LandCover", 0, 200);
        return res;
    }

    /// <summary>Mappatura ESA WorldCover → GroundType BMS, nello stesso formato del file di esempio di TB.</summary>
    public static string EsaMapping()
    {
        var types = new (string Name, int Id, int[] Esa)[]
        {
            ("NODATA", 0, new[] { 0 }),
            ("WATER (Ocean, Sea)", 1, new[] { 200 }),        // 200 = mare preso dalla Watermap
            ("RIVER (In-Land Water)", 2, new[] { 80 }),
            ("SWAMP", 3, new[] { 90, 95 }),
            ("PLAINS", 4, new[] { 30 }),
            ("BRUSH", 5, new[] { 60 }),
            ("THIN FOREST", 6, new[] { 10 }),
            ("THICK FOREST", 7, Array.Empty<int>()),
            ("ROCKY", 8, Array.Empty<int>()),
            ("URBAN", 9, new[] { 50 }),
            ("ROAD", 10, Array.Empty<int>()),
            ("RAIL", 11, Array.Empty<int>()),
            ("BRIDGE", 12, Array.Empty<int>()),
            ("RUNWAY", 13, Array.Empty<int>()),
            ("STATION", 14, Array.Empty<int>()),
            ("OBJECT", 15, Array.Empty<int>()),
            ("SHRUBS", 16, new[] { 20 }),
            ("SNOW AND ICE", 17, new[] { 70 }),
            ("CROPLAND", 18, new[] { 40 }),
            ("MOSS_LICHEN", 19, new[] { 100 }),
        };
        var sb = new StringBuilder("defaultIndex: 5\n");
        foreach (var (name, id, esa) in types)
        {
            sb.Append("protoLandCoverType {\n");
            sb.Append($"  name: \"{name}\"\n  id: {id}\n");
            foreach (var e in esa) sb.Append($"  landcoverMapIDs: {e}\n");
            sb.Append("}\n");
        }
        return sb.ToString();
    }
}

// ═══════════════════════════ 7. ROADS / BRIDGES ═══════════════════════════

public sealed class RoadsStep : ProcessingStep
{
    public const string RoadFilter =
        "fclass IN ('motorway','motorway_link','trunk','trunk_link','primary','primary_link','secondary','secondary_link')";

    public override string Id => "roads";
    public override string Name => "Roads + bridges";
    public override string Description => "Main OSM roads (motorway, trunk, primary, secondary) → Roads.raw and Bridges.raw (255) for TB LandCover mode.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\OSM", "*.zip").Count == 0 ? "Geofabrik OSM extracts" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        var layers = GeofabrikLayers(ctx, "roads");
        string roads = ctx.Out("Roads.raw"), bridges = ctx.Out("Bridges.raw");
        using (var r = ctx.CreateRaw(roads, ctx.Size, DataType.GDT_Byte))
        using (var b = ctx.CreateRaw(bridges, ctx.Size, DataType.GDT_Byte))
        {
            int i = 0;
            foreach (var (path, layer) in layers)
            {
                i++;
                ctx.Log?.Report($"  Roads: {Path.GetFileNameWithoutExtension(path)}");
                string v = ctx.ToTheaterVector(path, layer, "roads_" + i, p, "Reprojecting roads " + i, where: RoadFilter);
                ctx.Rasterize(r, v, "roads_" + i, new[] { "-burn", "255" }, p, "Rasterizing roads " + i);
                string vb = ctx.ToTheaterVector(path, layer, "bridges_" + i, p, "Reprojecting bridges " + i, where: RoadFilter + " AND bridge = 'T'");
                ctx.Rasterize(b, vb, "bridges_" + i, new[] { "-burn", "255" }, p, "Rasterizing bridges " + i);
            }
            r.FlushCache();
            b.FlushCache();
        }
        res.Outputs.Add(roads);
        res.Outputs.Add(bridges);
        res.Notes.Add($"{layers.Count} road layers");
        res.Preview = ctx.Preview(roads, "Roads", 0, 64);
        return res;
    }
}


// ═══════════════════════════ 8. IMMAGINI SATELLITARI ═══════════════════════════

/// <summary>
/// NASA Blue Marble Next Generation (500 m, luglio) → mappa colore RGB del teatro a 1/4 della dimensione della mappa
/// (8192² per 32768): ingresso di GlobalColorMap e del ColorMap dell'acqua nella scheda 5.
/// </summary>
public sealed class ImageryStep : ProcessingStep
{
    public override string Id => "imagery";
    public override string Name => "Satellite colour map";
    public override string Description => "NASA Blue Marble Next Generation (500 m, July) → theater colour map, RGB at 1/4 of the map size (8192² for 32768). Input of GlobalColorMap and water ColorMap in tab 5.";

    public override string? MissingInputs(GisContext ctx) =>
        ctx.SourceFiles(@"Sources\Imagery\BMNG", "*.tif").Count == 0 ? "Blue Marble tiles" : null;

    public override StepResult Run(GisContext ctx, IProgress<StepProgress> p)
    {
        var res = new StepResult();
        int size = Math.Max(256, ctx.Size / 4);
        string src = string.Join('|', ctx.SourceFiles(@"Sources\Imagery\BMNG", "*.tif"));
        string tmp = ctx.Work($"color_{size}.tif");
        int pad = ctx.WarpToTheater(src, tmp, size, "cubic", "Byte", "GTiff", p, "Reprojecting satellite imagery", 0, 0.8);
        string dst = ctx.Out("ColorMap_RGB.raw");
        using (var ds = GisContext.OpenRaster(tmp))
        {
            if (ds.RasterCount < 3) throw new IOException("The imagery has less than 3 bands");
            var bands = new[] { ds.GetRasterBand(1), ds.GetRasterBand(2), ds.GetRasterBand(3) };
            const int rows = 256;
            var buf = new byte[3][];
            for (int b = 0; b < 3; b++) buf[b] = new byte[size * rows];
            var outBuf = new byte[size * rows * 3];
            using var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
            for (int y = 0; y < size; y += rows)
            {
                ctx.Token.ThrowIfCancellationRequested();
                int n = Math.Min(rows, size - y);
                for (int b = 0; b < 3; b++)
                    bands[b].ReadRaster(pad, pad + y, size, n, buf[b], size, n, 0, 0);
                for (int i = 0; i < size * n; i++)
                {
                    outBuf[3 * i] = buf[0][i]; outBuf[3 * i + 1] = buf[1][i]; outBuf[3 * i + 2] = buf[2][i];
                }
                fs.Write(outBuf, 0, size * n * 3);
                p.Report(new StepProgress(0.8 + 0.15 * (y + n) / size, "Writing colour map"));
            }
        }
        res.Outputs.Add(dst);
        res.Notes.Add($"{size}² RGB ({size * size * 3L / 1048576} MB), source: NASA Earth Observatory Blue Marble Next Generation");
        res.Preview = ctx.Preview(tmp, "ColorMap", 0, 255);
        return res;
    }
}
