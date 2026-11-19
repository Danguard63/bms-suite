using System.Globalization;
using System.Text;
using TerrainTool.Core.Geo;

namespace TerrainTool.Core.Theater;

/// <summary>
/// Parametri del teatro come in NewTerrain\Theater.txt.
/// Sistema di coordinate del teatro: origine nell'angolo sud-ovest, X verso est e Y verso nord
/// in metri da 0 a SizeKm*1000 (come la Korea: x_0 = size/2, y_0 = size/2 - nord(centro)).
/// </summary>
public sealed class TheaterConfig
{
    /// <summary>Nome interno del progetto (usato anche nei nomi dei file intermedi, es. Mideast_32768.RAW): non cambiarlo a lavori avviati.</summary>
    public string Name { get; set; } = "Mideast";
    /// <summary>Nome del teatro in BMS (riga "name" della .tdf: Launcher, Campaign Editor). Vuoto = il nome della .tdf del vecchio teatro.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Descrizione del teatro in BMS (riga "desc" della .tdf). Vuota = testo standard.</summary>
    public string Description { get; set; } = "";
    public int SizeKm { get; set; } = 2048;
    public int MapSizePixels { get; set; } = 32768;
    public double CenterLat { get; set; } = 27.88;
    public double CenterLon { get; set; } = 46.44;
    public int MinHeightFt { get; set; } = -1500;
    /// <summary>Damavand (5610 m ≈ 18 400 ft) cade dentro il riquadro del Medio Oriente.</summary>
    public int MaxHeightFt { get; set; } = 19000;
    public double ScaleFactor { get; set; } = 0.9996;

    public const double FeetPerMeter = 1.0 / 0.3048;

    public double SizeMeters => SizeKm * 1000.0;
    public double MetersPerPixel => SizeMeters / MapSizePixels;
    /// <summary>Lato in pixel delle mappe a 250 m (WaterHeightMap, LOS250).</summary>
    public int Size250m => (int)Math.Round(SizeMeters / 250.0);
    /// <summary>Numero di segmenti (tile da 16 km), es. 64 per la Korea, 128 per 2048 km.</summary>
    public int Segments => SizeKm / 16;

    public TransverseMercator CreateProjection() => new(CenterLon, ScaleFactor);

    /// <summary>
    /// Georeferenziazione di un vecchio teatro (null = Trasversa di Mercatore standard sul centro).
    /// Quando c'è, comanda lei: la griglia, i warp GDAL (-ct) e le conversioni lat/lon del tool.
    /// </summary>
    public TheaterWarp? Warp { get; set; }

    /// <summary>
    /// Georeference of the old add-on this project converts, when it differs from this theater's grid (e.g. an old theater
    /// rebuilt on the standard projection BMS uses for lat/long): its objectives and campaign units are in that grid and are
    /// carried over through lat/lon. Null = the old add-on uses the same grid as this theater.
    /// </summary>
    public TheaterWarp? SourceGeoref { get; set; }

    /// <summary>When the theater grid last changed (switch to the standard projection): terrain data made before (photoreal
    /// tiles marked done, BMS campaign terrain caches) are in the old grid and are made again.</summary>
    public DateTime? GridChangedUtc { get; set; }

    /// <summary>True if a file was made before the last change of the grid.</summary>
    public bool OlderThanGrid(string file) => GridChangedUtc is { } t && File.GetLastWriteTimeUtc(file) < t;

    /// <summary>Pipeline PROJ per gdalwarp/ogr2ogr -ct, oppure null con la proiezione standard.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GdalCoordinateOperation => Warp?.GdalPipeline();

    public double FalseEasting => SizeMeters / 2.0;
    public double FalseNorthing
    {
        get
        {
            var (_, yc) = CreateProjection().Forward(CenterLat, CenterLon);
            return SizeMeters / 2.0 - yc;
        }
    }

    public string ProjectionString => Warp != null ? Warp.LabelProj4 :
        string.Create(CultureInfo.InvariantCulture,
            $"+proj=tmerc +lon_0={CenterLon:0.######} +ellps=WGS84 +k={ScaleFactor:0.######} +units=m +x_0={FalseEasting:0.###} +y_0={FalseNorthing:0.###}");

    /// <summary>Restituisce un convertitore lat/lon ↔ metri del teatro, già con i falsi est/nord.</summary>
    public TheaterGrid CreateGrid() => new(this);

    public string ToTheaterTxt()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Theater name=").Append(Name).Append('\n');
        sb.Append("Theater size in KM=").Append(SizeKm.ToString(ci)).Append('\n');
        sb.Append("Map size in pixels=").Append(MapSizePixels.ToString(ci)).Append('\n');
        sb.Append("Center latitude=").Append(CenterLat.ToString("0.######", ci)).Append('\n');
        sb.Append("Center longitude=").Append(CenterLon.ToString("0.######", ci)).Append('\n');
        sb.Append("Min height in theater=").Append(MinHeightFt.ToString(ci)).Append('\n');
        sb.Append("Max height in theater=").Append(MaxHeightFt.ToString(ci)).Append('\n');
        sb.Append("Projection string=").Append(ProjectionString);
        return sb.ToString();
    }

    /// <summary>Legge un Theater.txt esistente (es. quello della Korea).</summary>
    public static TheaterConfig ParseTheaterTxt(string text)
    {
        var ci = CultureInfo.InvariantCulture;
        var cfg = new TheaterConfig();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().ToLowerInvariant(), val = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "theater name": cfg.Name = val; break;
                case "theater size in km": cfg.SizeKm = int.Parse(val, ci); break;
                case "map size in pixels": cfg.MapSizePixels = int.Parse(val, ci); break;
                case "center latitude": cfg.CenterLat = double.Parse(val, ci); break;
                case "center longitude": cfg.CenterLon = double.Parse(val, ci); break;
                case "min height in theater": cfg.MinHeightFt = (int)double.Parse(val, ci); break;
                case "max height in theater": cfg.MaxHeightFt = (int)double.Parse(val, ci); break;
                case "projection string":
                    foreach (var tok in val.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (tok.StartsWith("+k=")) cfg.ScaleFactor = double.Parse(tok[3..], ci);
                    break;
            }
        }
        return cfg;
    }
}

public sealed class TheaterGrid
{
    private readonly TransverseMercator _tm;
    private readonly double _fe, _fn;
    public TheaterConfig Config { get; }

    public TheaterGrid(TheaterConfig cfg)
    {
        Config = cfg;
        _tm = cfg.CreateProjection();
        _fe = cfg.FalseEasting;
        _fn = cfg.FalseNorthing;
    }

    /// <summary>lat/lon → metri del teatro (0..size), X est, Y nord.</summary>
    public (double X, double Y) ToTheater(double lat, double lon)
    {
        if (Config.Warp != null) return Config.Warp.Forward(lat, lon);
        var (x, y) = _tm.Forward(lat, lon);
        return (x + _fe, y + _fn);
    }

    public (double Lat, double Lon) ToLatLon(double x, double y) =>
        Config.Warp != null ? Config.Warp.Inverse(x, y) : _tm.Inverse(x - _fe, y - _fn);

    public bool Contains(double x, double y) => x >= 0 && y >= 0 && x <= Config.SizeMeters && y <= Config.SizeMeters;

    /// <summary>True when the old add-on's positions are in another grid (SourceGeoref) and have to be carried over.</summary>
    public bool Reprojects => Config.SourceGeoref != null;

    /// <summary>Old add-on grid → this theater, in BMS feet (north, east). Unchanged when the grids are the same.</summary>
    public (double NorthFt, double EastFt) FromSourceFt(double northFt, double eastFt)
    {
        if (Config.SourceGeoref is not { } s) return (northFt, eastFt);
        var (lat, lon) = s.Inverse(eastFt * 0.3048, northFt * 0.3048);
        var (x, y) = ToTheater(lat, lon);
        // a position that would fall outside the theater stays where it was: things parked on the old grid's edge
        // (carriers waiting at cell 0,0) are placeholders, not places on the ground
        double size = Config.SizeMeters;
        if (x < 0 || y < 0 || x >= size || y >= size) return (northFt, eastFt);
        return (y * TheaterConfig.FeetPerMeter, x * TheaterConfig.FeetPerMeter);
    }

    /// <summary>Old add-on grid → this theater, in campaign grid cells (x east, y north), as BMS SimToGrid/GridToSim.</summary>
    public (int X, int Y) FromSourceGrid(int x, int y)
    {
        if (!Reprojects) return (x, y);
        var (n, e) = FromSourceFt(Campaign.CampaignDoc.GridToSim(y), Campaign.CampaignDoc.GridToSim(x));
        return (Campaign.CampaignDoc.SimToGrid(e), Campaign.CampaignDoc.SimToGrid(n));
    }

    /// <summary>Lat/lon dei quattro angoli (SW, SE, NE, NW).</summary>
    public (double Lat, double Lon)[] Corners()
    {
        double s = Config.SizeMeters;
        return new[] { ToLatLon(0, 0), ToLatLon(s, 0), ToLatLon(s, s), ToLatLon(0, s) };
    }
}
