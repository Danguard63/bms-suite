using System.Globalization;
using TerrainTool.Core.Theater;

namespace TerrainTool.Core.Data;

/// <summary>Un file da scaricare.</summary>
public sealed class DownloadItem
{
    public required string Url { get; init; }
    /// <summary>Percorso relativo alla cartella del progetto.</summary>
    public required string RelPath { get; init; }
    /// <summary>Se true un 404 è normale (es. tile completamente in mare) e non è un errore.</summary>
    public bool MayNotExist { get; init; }
}

public sealed class DataSource
{
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required string Url { get; init; }
    public required string Login { get; init; }
    /// <summary>Sottocartella del progetto (Sources\...) dove vanno i file.</summary>
    public required string Folder { get; init; }
    /// <summary>Estensioni attese per contare i file presenti.</summary>
    public string[] Extensions { get; init; } = Array.Empty<string>();
    public string Step { get; init; } = "";
    /// <summary>Elenco dei file da scaricare per il teatro; null = solo download manuale.</summary>
    public Func<TheaterConfig, IReadOnlyList<DownloadItem>>? Plan { get; init; }
    /// <summary>Dimensione indicativa totale, solo per informazione.</summary>
    public string ApproxSize { get; init; } = "";
    public bool SelectedByDefault { get; init; }

    public bool IsAutomatic => Plan != null;
}

public static class DataSources
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Paesi coperti dal teatro, come estratti Geofabrik (GeoPackage).</summary>
    public static readonly string[] GeofabrikRegions =
    {
        "asia/iraq", "asia/iran", "asia/gcc-states", "asia/jordan", "asia/syria", "asia/lebanon",
        "asia/israel-and-palestine", "asia/yemen", "africa/sudan",
        // teatro Mideast128 (fino a 38,9°N): sud della Turchia, Caucaso meridionale, Turkmenistan
        "europe/turkey", "asia/azerbaijan", "asia/armenia", "asia/turkmenistan",
    };

    public static IReadOnlyList<DataSource> All { get; } = new List<DataSource>
    {
        new() { Name = "Copernicus DEM GLO-30", Purpose = "30 m heights → theater heightmap (replaces TB DEM mode)",
                Url = "https://registry.opendata.aws/copernicus-dem/", Login = "No",
                Folder = @"Sources\DEM\COP30", Extensions = new[] { ".tif" }, Step = "Heightmap",
                ApproxSize = "~5–10 GB", SelectedByDefault = true,
                Plan = c => DegreeTiles(c, 1, 1).Select(t => CopDem(t.Lat, t.Lon, 30)).ToList() },
        new() { Name = "Copernicus DEM GLO-90", Purpose = "90 m heights (quick test, lower quality)",
                Url = "https://registry.opendata.aws/copernicus-dem/", Login = "No",
                Folder = @"Sources\DEM\COP90", Extensions = new[] { ".tif" }, Step = "Heightmap (test)",
                ApproxSize = "~1 GB",
                Plan = c => DegreeTiles(c, 1, 1).Select(t => CopDem(t.Lat, t.Lon, 90)).ToList() },
        new() { Name = "OSM water polygons", Purpose = "Sea (value 45)",
                Url = "https://osmdata.openstreetmap.de/data/water-polygons.html", Login = "No",
                Folder = @"Sources\Water\Sea", Extensions = new[] { ".zip", ".shp" }, Step = "Watermap",
                ApproxSize = "~900 MB", SelectedByDefault = true,
                Plan = _ => new[] { new DownloadItem { Url = "https://osmdata.openstreetmap.de/download/water-polygons-split-4326.zip",
                                                        RelPath = @"Sources\Water\Sea\water-polygons-split-4326.zip" } } },
        new() { Name = "OSM coastlines", Purpose = "Coastline map and coast points",
                Url = "https://osmdata.openstreetmap.de/data/coastlines.html", Login = "No",
                Folder = @"Sources\Water\Coast", Extensions = new[] { ".zip", ".shp" }, Step = "Coastline",
                ApproxSize = "~700 MB", SelectedByDefault = true,
                Plan = _ => new[] { new DownloadItem { Url = "https://osmdata.openstreetmap.de/download/coastlines-split-4326.zip",
                                                        RelPath = @"Sources\Water\Coast\coastlines-split-4326.zip" } } },
        new() { Name = "Geofabrik OSM extracts", Purpose = "Inland water (value 25), roads, bridges",
                Url = "https://download.geofabrik.de/asia.html", Login = "No",
                Folder = @"Sources\OSM", Extensions = new[] { ".zip", ".gpkg" }, Step = "Watermap / roads",
                ApproxSize = "~1.5 GB", SelectedByDefault = true,
                Plan = _ => GeofabrikRegions.Select(r => new DownloadItem
                {
                    Url = $"https://download.geofabrik.de/{r}-latest-free.gpkg.zip",
                    RelPath = $@"Sources\OSM\{r.Split('/')[1]}-latest-free.gpkg.zip",
                }).ToList() },
        new() { Name = "GEBCO derived polygons", Purpose = "Sea depth (OceanDepth)",
                Url = "https://opendem.info/", Login = "No",
                Folder = @"Sources\GEBCO", Extensions = new[] { ".zip", ".shp" }, Step = "Ocean depth",
                ApproxSize = "~400 MB", SelectedByDefault = true,
                Plan = _ => new[] { new DownloadItem { Url = "https://opendem.info/downloads/gebco_derived_polygons.zip",
                                                        RelPath = @"Sources\GEBCO\gebco_derived_polygons.zip" } } },
        new() { Name = "ESA WorldCover 10 m", Purpose = "Land cover",
                Url = "https://esa-worldcover.org/en/data-access", Login = "No",
                Folder = @"Sources\LandCover", Extensions = new[] { ".tif" }, Step = "Land cover",
                ApproxSize = "~3–6 GB", SelectedByDefault = true,
                Plan = c => DegreeTiles(c, 3, 3).Select(t => new DownloadItem
                {
                    Url = $"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{LatLonName(t.Lat, t.Lon, 2, 3, "")}_Map.tif",
                    RelPath = $@"Sources\LandCover\ESA_WorldCover_10m_2021_v200_{LatLonName(t.Lat, t.Lon, 2, 3, "")}_Map.tif",
                    MayNotExist = true,
                }).ToList() },
        new() { Name = "Hansen tree cover 2000", Purpose = "Tree cover → forest map",
                Url = "https://storage.googleapis.com/earthenginepartners-hansen/GFC-2023-v1.11/download.html", Login = "No",
                Folder = @"Sources\TreeCover", Extensions = new[] { ".tif" }, Step = "Forest",
                ApproxSize = "~0.5–1 GB", SelectedByDefault = true,
                Plan = c => DegreeTiles(c, 10, 10).Select(t =>
                {
                    // Nome = angolo in alto a sinistra (nord-ovest)
                    string n = HansenName(t.Lat + 10, t.Lon);
                    return new DownloadItem
                    {
                        Url = $"https://storage.googleapis.com/earthenginepartners-hansen/GFC-2023-v1.11/Hansen_GFC-2023-v1.11_treecover2000_{n}.tif",
                        RelPath = $@"Sources\TreeCover\Hansen_GFC-2023-v1.11_treecover2000_{n}.tif",
                    };
                }).ToList() },

        new() { Name = "NASA Blue Marble NG (July)", Purpose = "Satellite colour (500 m) → GlobalColorMap and water ColorMap",
                Url = "https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/base-map/", Login = "No",
                Folder = @"Sources\Imagery\BMNG", Extensions = new[] { ".tif" }, Step = "Satellite colour map",
                ApproxSize = "~1.4 GB per 90° tile", SelectedByDefault = true,
                Plan = c => DegreeTiles(c, 90, 90).Select(t =>
                {
                    // tile 21600² da 90°: colonne A..D da -180° di longitudine, riga 1 = nord, 2 = sud
                    string n = $"{(char)('A' + (t.Lon + 180) / 90)}{(t.Lat >= 0 ? 1 : 2)}";
                    return new DownloadItem
                    {
                        Url = $"https://assets.science.nasa.gov/content/dam/science/esd/eo/images/bmng/bmng-base/july/world.200407.3x21600x21600.{n}_geo.tif",
                        RelPath = $@"Sources\Imagery\BMNG\world.200407.3x21600x21600.{n}_geo.tif",
                    };
                }).ToList() },

        // Solo manuali: alternative per il DEM mode di Terrain Builder
        new() { Name = "SRTM 1 arc-second", Purpose = "30 m heights for TB DEM mode (alternative)", Url = "https://earthexplorer.usgs.gov/",
                Login = "Yes (USGS, free)", Folder = @"Sources\DEM\SRTM_1", Extensions = new[] { ".tif", ".hgt", ".bil" }, Step = "TB DEM mode" },
        new() { Name = "ALOS AW3D30", Purpose = "30 m heights for TB DEM mode (alternative)", Url = "https://www.eorc.jaxa.jp/ALOS/en/aw3d30/",
                Login = "Yes (JAXA, free)", Folder = @"Sources\DEM\ALOS", Extensions = new[] { ".tif" }, Step = "TB DEM mode" },
    };

    /// <summary>
    /// Tile di 'stepLat'×'stepLon' gradi (angolo sud-ovest allineato alla griglia) che toccano il teatro,
    /// con un margine di 10 km.
    /// </summary>
    public static List<(int Lat, int Lon)> DegreeTiles(TheaterConfig cfg, int stepLat, int stepLon)
    {
        var g = cfg.CreateGrid();
        double s = cfg.SizeMeters, margin = 10000;
        // Riquadro lat/lon campionando il bordo del teatro
        double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
        for (int i = 0; i <= 200; i++)
        {
            double t = -margin + (s + 2 * margin) * i / 200.0;
            foreach (var (x, y) in new[] { (t, -margin), (t, s + margin), (-margin, t), (s + margin, t) })
            {
                var (la, lo) = g.ToLatLon(x, y);
                minLat = Math.Min(minLat, la); maxLat = Math.Max(maxLat, la);
                minLon = Math.Min(minLon, lo); maxLon = Math.Max(maxLon, lo);
            }
        }
        int FloorTo(double v, int step) => (int)Math.Floor(v / step) * step;
        var res = new List<(int, int)>();
        for (int la = FloorTo(minLat, stepLat); la < maxLat; la += stepLat)
            for (int lo = FloorTo(minLon, stepLon); lo < maxLon; lo += stepLon)
                if (TileTouchesTheater(g, la, lo, stepLat, stepLon, margin)) res.Add((la, lo));
        return res;
    }

    private static bool TileTouchesTheater(TheaterGrid g, int lat, int lon, int dLat, int dLon, double margin)
    {
        double s = g.Config.SizeMeters;
        const int n = 12;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                var (x, y) = g.ToTheater(lat + dLat * (double)i / n, lon + dLon * (double)j / n);
                if (x >= -margin && y >= -margin && x <= s + margin && y <= s + margin) return true;
            }
        // Teatro più piccolo della tile: controlla gli angoli del teatro
        foreach (var (la, lo) in g.Corners())
            if (la >= lat && la <= lat + dLat && lo >= lon && lo <= lon + dLon) return true;
        return false;
    }

    private static DownloadItem CopDem(int lat, int lon, int res)
    {
        string arc = res == 30 ? "10" : "30";
        string ns = lat >= 0 ? "N" : "S", ew = lon >= 0 ? "E" : "W";
        string name = $"Copernicus_DSM_COG_{arc}_{ns}{Math.Abs(lat):00}_00_{ew}{Math.Abs(lon):000}_00_DEM";
        string bucket = res == 30 ? "copernicus-dem-30m" : "copernicus-dem-90m";
        return new DownloadItem
        {
            Url = $"https://{bucket}.s3.amazonaws.com/{name}/{name}.tif",
            RelPath = $@"Sources\DEM\COP{res}\{name}.tif",
            MayNotExist = true, // tile interamente in mare: non esiste
        };
    }

    private static string LatLonName(int lat, int lon, int latDigits, int lonDigits, string sep)
    {
        string ns = lat >= 0 ? "N" : "S", ew = lon >= 0 ? "E" : "W";
        return ns + Math.Abs(lat).ToString(new string('0', latDigits), Ci) + sep + ew + Math.Abs(lon).ToString(new string('0', lonDigits), Ci);
    }

    private static string HansenName(int lat, int lon) =>
        Math.Abs(lat).ToString("00", Ci) + (lat >= 0 ? "N" : "S") + "_" + Math.Abs(lon).ToString("000", Ci) + (lon >= 0 ? "E" : "W");

    public static (int Files, long Bytes) Scan(string projectFolder, DataSource s)
    {
        string dir = Path.Combine(projectFolder, s.Folder);
        if (!Directory.Exists(dir)) return (0, 0);
        int n = 0; long b = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (s.Extensions.Length > 0 && !s.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            n++; b += new FileInfo(f).Length;
        }
        return (n, b);
    }
}
