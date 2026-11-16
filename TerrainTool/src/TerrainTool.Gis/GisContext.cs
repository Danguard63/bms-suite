using System.Globalization;
using System.IO.Compression;
using OSGeo.GDAL;
using OSGeo.OSR;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace TerrainTool.Gis;

/// <summary>Avanzamento di un passo: frazione 0..1 della fase corrente e testo.</summary>
public sealed record StepProgress(double Fraction, string Text);

/// <summary>
/// Tutto quello che serve ai passi di elaborazione: griglia del teatro, cartelle, aiuti GDAL.
/// Il sistema del teatro: proiezione TM del Theater.txt, estensione 0..size metri, riga 0 = nord.
/// </summary>
public sealed class GisContext
{
    private static bool _initialized;
    private static readonly object InitLock = new();
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public TheaterProject Project { get; }
    public TheaterConfig Theater => Project.Theater;
    /// <summary>Lato in pixel delle mappe a piena risoluzione (32768, o meno per le prove veloci).</summary>
    public int Size { get; }
    public double SizeMeters => Theater.SizeMeters;
    public string Proj4 { get; }
    /// <summary>Pipeline PROJ del vecchio teatro (gdalwarp/ogr2ogr -ct), null con la proiezione standard.</summary>
    public string? Ct { get; }
    public string Wkt { get; }

    public string SourcesDir => Path.Combine(Project.ProjectFolder, "Sources");
    public string WorkDir { get; }
    /// <summary>Uscite pronte per Terrain Builder.</summary>
    public string OutDir { get; }
    public string PreviewDir => Path.Combine(OutDir, "Previews");

    public IProgress<string>? Log { get; set; }
    public CancellationToken Token { get; set; }

    public GisContext(TheaterProject project, int size)
    {
        Init();
        Project = project;
        Size = size;
        Proj4 = Theater.ProjectionString;
        Ct = Theater.GdalCoordinateOperation;
        using var srs = new SpatialReference("");
        srs.ImportFromProj4(Proj4);
        srs.ExportToWkt(out string wkt, null);
        Wkt = wkt;
        string suffix = size == Theater.MapSizePixels ? "" : "_" + size.ToString(Ci);
        WorkDir = Path.Combine(project.ProjectFolder, "Work");
        OutDir = Path.Combine(project.ProjectFolder, "Output", "TB_Input" + suffix);
        if (project.IsProtectedPath(OutDir) || project.IsProtectedPath(WorkDir))
            throw new InvalidOperationException("Project folder is protected (BMS / Korea / old theater): processing blocked.");
        Directory.CreateDirectory(WorkDir);
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(PreviewDir);
    }

    public static void Init()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            MaxRev.Gdal.Core.GdalBase.ConfigureAll();
            Gdal.UseExceptions();
            OSGeo.OGR.Ogr.UseExceptions();
            Osr.UseExceptions();
            // NB: niente GDAL_NUM_THREADS globale: con le versioni recenti attiva la lettura VRT multithread,
            // che sul mosaico DEM lasciava vuota la metà nord delle tile N36. Il multithread resta solo nel warp.
            Gdal.SetConfigOption("OGR_ENABLE_PARTIAL_REPROJECTION", "YES");
            _initialized = true;
        }
    }

    public static string GdalVersion
    {
        get { Init(); return Gdal.VersionInfo("RELEASE_NAME"); }
    }

    // ───────────── griglia ─────────────

    public string S => SizeMeters.ToString("0.###", Ci);
    public string N => Size.ToString(Ci);
    public double[] GeoTransform(int size) => new[] { 0, SizeMeters / size, 0, SizeMeters, 0, -SizeMeters / size };

    /// <summary>Riquadro lon/lat (con margine) per filtrare i dati sorgente: "-spat minLon minLat maxLon maxLat".</summary>
    public string[] SpatLonLat()
    {
        var g = Theater.CreateGrid();
        double s = SizeMeters, m = 20000;
        double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
        for (int i = 0; i <= 100; i++)
        {
            double t = -m + (s + 2 * m) * i / 100.0;
            foreach (var (x, y) in new[] { (t, -m), (t, s + m), (-m, t), (s + m, t) })
            {
                var (la, lo) = g.ToLatLon(x, y);
                minLat = Math.Min(minLat, la); maxLat = Math.Max(maxLat, la);
                minLon = Math.Min(minLon, lo); maxLon = Math.Max(maxLon, lo);
            }
        }
        return new[] { "-spat", F(minLon), F(minLat), F(maxLon), F(maxLat) };
    }

    public static string F(double v) => v.ToString("0.######", Ci);

    // ───────────── file e cartelle ─────────────

    public string Out(string name) => Path.Combine(OutDir, name);
    public string Work(string name) => Path.Combine(WorkDir, name);

    /// <summary>Estrae uno zip in Work\extract\&lt;nome&gt; (una sola volta) e restituisce la cartella.</summary>
    public string ExtractZip(string zipPath)
    {
        string dir = Work(Path.Combine("extract", Path.GetFileNameWithoutExtension(zipPath)));
        string done = Path.Combine(dir, ".extracted");
        if (File.Exists(done)) return dir;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Log?.Report($"  Extracting {Path.GetFileName(zipPath)}…");
        ZipFile.ExtractToDirectory(zipPath, dir);
        File.WriteAllText(done, DateTime.Now.ToString("s", Ci));
        return dir;
    }

    public List<string> SourceFiles(string relFolder, string pattern)
    {
        string dir = Path.Combine(Project.ProjectFolder, relFolder);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────── aiuti GDAL ─────────────

    /// <summary>Callback di avanzamento GDAL; restituisce 0 per annullare.</summary>
    public Gdal.GDALProgressFuncDelegate Callback(IProgress<StepProgress>? p, string text, double from = 0, double to = 1)
    {
        double last = -1;
        return (complete, _, _) =>
        {
            if (Token.IsCancellationRequested) return 0;
            double f = from + (to - from) * complete;
            if (f - last >= 0.002) { last = f; p?.Report(new StepProgress(f, text)); }
            return 1;
        };
    }

    /// <summary>Crea un raster ENVI (RAW + .hdr) nel sistema del teatro, riempito con 'fill'.</summary>
    public Dataset CreateRaw(string path, int size, DataType type, double fill = 0)
    {
        DeleteRaw(path);
        var drv = Gdal.GetDriverByName("ENVI");
        var ds = drv.Create(path, size, size, 1, type, null);
        ds.SetGeoTransform(GeoTransform(size));
        ds.SetProjection(Wkt);
        ds.GetRasterBand(1).Fill(fill, 0);
        return ds;
    }

    /// <summary>Scrive l'intestazione ENVI (.hdr) per un RAW scritto a mano, così GDAL può leggerlo (anteprime).</summary>
    public static void WriteEnviHeader(string rawPath, int size, int enviDataType)
    {
        File.WriteAllText(Path.ChangeExtension(rawPath, ".hdr"),
            $"ENVI\nsamples = {size}\nlines = {size}\nbands = 1\nheader offset = 0\nfile type = ENVI Standard\n" +
            $"data type = {enviDataType}\ninterleave = bsq\nbyte order = 0\n");
    }

    public static void DeleteRaw(string path)
    {
        foreach (var f in new[] { path, Path.ChangeExtension(path, ".hdr"), path + ".aux.xml", Path.ChangeExtension(path, ".raw.aux.xml") })
            if (File.Exists(f)) File.Delete(f);
    }

    public static Dataset OpenVector(string path) =>
        Gdal.OpenEx(path, (uint)(GdalConst.OF_VECTOR | GdalConst.OF_READONLY), null, null, null)
        ?? throw new IOException("Cannot open vector " + path);

    public static Dataset OpenRaster(string path) =>
        Gdal.Open(path, Access.GA_ReadOnly) ?? throw new IOException("Cannot open raster " + path);

    /// <summary>ogr2ogr: riproietta nel sistema del teatro, filtrando sul riquadro del teatro.</summary>
    public string ToTheaterVector(string srcPath, string? layer, string outName, IProgress<StepProgress>? p, string text,
                                  string? where = null, bool promoteMulti = true)
    {
        string dst = Work(outName + ".gpkg");
        if (File.Exists(dst)) File.Delete(dst);
        var args = new List<string> { "-f", "GPKG", "-t_srs", Proj4, "-nln", outName, "-skipfailures" };
        if (Ct != null)
        {
            using (var chk = OSGeo.OGR.Ogr.Open(srcPath, 0)) RequireWgs84(chk?.GetLayerByIndex(0)?.GetSpatialRef(), srcPath);
            args.AddRange(new[] { "-s_srs", "EPSG:4326", "-ct", Ct });
        }
        args.AddRange(SpatLonLat());
        if (where != null) { args.Add("-where"); args.Add(where); }
        if (promoteMulti) { args.Add("-nlt"); args.Add("PROMOTE_TO_MULTI"); }
        if (layer != null) args.Add(layer);
        using var src = OpenVector(srcPath);
        var cb = Callback(p, text);
        using var res = Gdal.wrapper_GDALVectorTranslateDestName(dst, src, new GDALVectorTranslateOptions(args.ToArray()), cb, null);
        Token.ThrowIfCancellationRequested();
        if (res == null) throw new IOException("Vector translate failed: " + srcPath);
        return dst;
    }

    /// <summary>
    /// Con la georeferenziazione del vecchio teatro la pipeline parte da lat/lon WGS84: ogni sorgente deve esserlo.
    /// </summary>
    private static void RequireWgs84(SpatialReference? sr, string path)
    {
        if (sr == null) return; // senza sistema dichiarato: si assume WGS84 lat/lon (es. alcuni GeoTIFF)
        if (sr.IsGeographic() != 1 || Math.Abs(sr.GetSemiMajor() - 6378137.0) > 1)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not in WGS84 lat/lon: the old-theater georeference needs WGS84 sources.");
    }

    /// <summary>gdal_rasterize in un raster esistente.</summary>
    public void Rasterize(Dataset target, string vectorPath, string layer, string[] burnArgs, IProgress<StepProgress>? p, string text)
    {
        using var src = OpenVector(vectorPath);
        var args = new List<string>(burnArgs) { "-l", layer };
        var cb = Callback(p, text);
        int ok = Gdal.wrapper_GDALRasterizeDestDS(target, src, new GDALRasterizeOptions(args.ToArray()), cb, null);
        Token.ThrowIfCancellationRequested();
        if (ok == 0) throw new IOException("Rasterize failed: " + vectorPath);
    }

    /// <summary>Mosaico VRT di più raster sorgente.</summary>
    public string BuildVrt(IEnumerable<string> files, string name, IProgress<StepProgress>? p, string text, params string[] extra)
    {
        string vrt = Work(name + ".vrt");
        if (File.Exists(vrt)) File.Delete(vrt);
        var cb = Callback(p, text);
        using var ds = Gdal.BuildVRT(vrt, files.ToArray(), new GDALBuildVRTOptions(extra), cb, null);
        Token.ThrowIfCancellationRequested();
        if (ds == null) throw new IOException("BuildVRT failed");
        return vrt;
    }

    /// <summary>
    /// gdalwarp verso la griglia del teatro, con un bordo extra di 'pad' pixel su ogni lato
    /// (alcune versioni di GDAL lasciano vuota una fascia lungo il bordo curvo del teatro).
    /// Il raster prodotto è (size+2·pad)²: il teatro vero parte dal pixel (pad, pad). Restituisce pad.
    /// </summary>
    public int WarpToTheater(string src, string dst, int size, string resampling, string outType, string format,
                             IProgress<StepProgress>? p, string text, double from, double to, params string[] extra)
    {
        if (format == "ENVI") DeleteRaw(dst); else if (File.Exists(dst)) File.Delete(dst);
        int pad = Math.Max(16, size / 32);
        double px = SizeMeters / size, padM = pad * px;
        string lo = F(-padM), hi = F(SizeMeters + padM), n = (size + 2 * pad).ToString(Ci);
        var args = new List<string>
        {
            "-t_srs", Proj4, "-te", lo, lo, hi, hi, "-ts", n, n,
            "-r", resampling, "-ot", outType, "-of", format, "-multi", "-wo", "NUM_THREADS=ALL_CPUS", "-wm", "1024",
            "-wo", "INIT_DEST=0", "-overwrite",
            // sempre i dati a piena risoluzione e campionamento fitto dei bordi: evita buchi ai margini del teatro
            "-ovr", "NONE", "-wo", "SAMPLE_GRID=YES", "-wo", "SAMPLE_STEPS=101", "-wo", "SOURCE_EXTRA=64",
        };
        args.AddRange(extra);
        if (Ct != null) args.AddRange(new[] { "-s_srs", "EPSG:4326", "-ct", Ct });
        // Sorgenti: un singolo raster, oppure (src = elenco separato da '|') le tile una per una, senza mosaico VRT
        var names = src.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var sources = names.Select(OpenRaster).ToArray();
        if (Ct != null)
            foreach (var (d, nm) in sources.Zip(names))
            {
                string wkt = d.GetProjectionRef();
                using var sr = string.IsNullOrEmpty(wkt) ? null : new SpatialReference(wkt);
                RequireWgs84(sr, nm);
            }
        var cb = Callback(p, text, from, to);
        try
        {
            using var res = Gdal.Warp(dst, sources, new GDALWarpAppOptions(args.ToArray()), cb, null);
            Token.ThrowIfCancellationRequested();
            if (res == null) throw new IOException("Warp failed: " + names[0]);
        }
        finally
        {
            foreach (var d in sources) d.Dispose();
        }
        return pad;
    }


    /// <summary>Anteprima PNG 1024×1024 di un raster (scala automatica o fissa).</summary>
    public string Preview(string rasterPath, string name, double? min = null, double? max = null)
    {
        string png = Path.Combine(PreviewDir, name + ".png");
        try
        {
            if (File.Exists(png)) File.Delete(png);
            using var ds = OpenRaster(rasterPath);
            var args = new List<string> { "-of", "PNG", "-ot", "Byte", "-outsize", "1024", "1024", "-r", "average" };
            if (min.HasValue && max.HasValue) { args.Add("-scale"); args.Add(F(min.Value)); args.Add(F(max.Value)); args.Add("0"); args.Add("255"); }
            else args.Add("-scale");
            using var res = Gdal.wrapper_GDALTranslate(png, ds, new GDALTranslateOptions(args.ToArray()), null, null);
            foreach (var aux in Directory.EnumerateFiles(PreviewDir, "*.aux.xml")) File.Delete(aux);
        }
        catch (Exception ex) { Log?.Report("  (preview not created: " + ex.Message + ")"); }
        return png;
    }
}
