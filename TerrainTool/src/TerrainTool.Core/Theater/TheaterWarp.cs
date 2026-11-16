using System.Globalization;
using System.Text;
using TerrainTool.Core.Geo;

namespace TerrainTool.Core.Theater;

/// <summary>
/// Georeferenziazione "deformata" di un vecchio teatro (es. Mideast128): non una proiezione standard ma
/// una Trasversa di Mercatore di base (k = 1, origine in BaseLat0/BaseLon0) seguita da un polinomio
/// (metri TM → metri del teatro, origine nell'angolo SW). Ricavata confrontando l'altimetria del vecchio
/// teatro con il DEM Copernicus. Stessa forma della trasformazione PROJ "horner", così GDAL e il tool
/// usano esattamente gli stessi numeri.
/// Termini del polinomio: e^i · n^j per i = 0..Degree, j = 0..Degree-i (in quest'ordine).
/// </summary>
public sealed class TheaterWarp
{
    public string Description { get; set; } = "";
    public double BaseLat0 { get; set; }
    public double BaseLon0 { get; set; }
    public int Degree { get; set; }
    /// <summary>Origine sottratta prima del polinomio diretto (metri TM).</summary>
    public double[] FwdOrigin { get; set; } = new double[2];
    /// <summary>Origine sottratta prima del polinomio inverso (metri del teatro).</summary>
    public double[] InvOrigin { get; set; } = new double[2];
    public double[] FwdU { get; set; } = Array.Empty<double>();
    public double[] FwdV { get; set; } = Array.Empty<double>();
    public double[] InvU { get; set; } = Array.Empty<double>();
    public double[] InvV { get; set; } = Array.Empty<double>();

    private TransverseMercator? _tm;
    private double _n0;

    private TransverseMercator Tm
    {
        get
        {
            if (_tm == null)
            {
                _tm = new TransverseMercator(BaseLon0, 1.0);
                _n0 = _tm.Forward(BaseLat0, BaseLon0).Y;
            }
            return _tm;
        }
    }

    private IEnumerable<(int I, int J)> Terms()
    {
        for (int i = 0; i <= Degree; i++)
            for (int j = 0; j <= Degree - i; j++)
                yield return (i, j);
    }

    private double Eval(double[] c, double e, double n)
    {
        double s = 0; int k = 0;
        foreach (var (i, j) in Terms()) s += c[k++] * Math.Pow(e, i) * Math.Pow(n, j);
        return s;
    }

    /// <summary>lat/lon → metri del teatro.</summary>
    public (double X, double Y) Forward(double lat, double lon)
    {
        var (e, n) = Tm.Forward(lat, lon);
        n -= _n0;
        e -= FwdOrigin[0]; n -= FwdOrigin[1];
        return (Eval(FwdU, e, n), Eval(FwdV, e, n));
    }

    /// <summary>metri del teatro → lat/lon (polinomio inverso, errore di qualche metro).</summary>
    public (double Lat, double Lon) Inverse(double x, double y)
    {
        double u = x - InvOrigin[0], v = y - InvOrigin[1];
        double e = Eval(InvU, u, v), n = Eval(InvV, u, v);
        return Tm.Inverse(e, n + _n0);
    }

    /// <summary>Sistema usato solo come etichetta dei raster/vettori prodotti (le coordinate vere sono quelle del teatro).</summary>
    public string LabelProj4 => string.Create(CultureInfo.InvariantCulture,
        $"+proj=tmerc +lat_0={BaseLat0:0.######} +lon_0={BaseLon0:0.######} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m");

    // PROJ "horner": u usa i coefficienti raggruppati per potenza di n, v per potenza di e (verificato con PROJ 9.4).
    private string Coefs(double[] c, bool byN)
    {
        var d = new Dictionary<(int, int), double>(); int k = 0;
        foreach (var t in Terms()) d[t] = c[k++];
        var seq = new List<(int, int)>();
        if (byN) { for (int j = 0; j <= Degree; j++) for (int i = 0; i <= Degree - j; i++) seq.Add((i, j)); }
        else { for (int i = 0; i <= Degree; i++) for (int j = 0; j <= Degree - i; j++) seq.Add((i, j)); }
        return string.Join(",", seq.Select(t => d[t].ToString("R", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Pipeline PROJ da passare a gdalwarp/ogr2ogr con -ct (sorgente EPSG:4326 in ordine lat/lon, come vuole GDAL con -s_srs EPSG:4326).
    /// </summary>
    public string GdalPipeline()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("+proj=pipeline +step +proj=axisswap +order=2,1 +step +proj=unitconvert +xy_in=deg +xy_out=rad ");
        sb.Append(string.Create(ci, $"+step +proj=tmerc +lat_0={BaseLat0:R} +lon_0={BaseLon0:R} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 "));
        sb.Append(string.Create(ci, $"+step +proj=horner +ellps=WGS84 +range=4000000 +deg={Degree} "));
        sb.Append(string.Create(ci, $"+fwd_origin={FwdOrigin[0]:R},{FwdOrigin[1]:R} +inv_origin={InvOrigin[0]:R},{InvOrigin[1]:R} "));
        sb.Append("+fwd_u=").Append(Coefs(FwdU, true)).Append(" +fwd_v=").Append(Coefs(FwdV, false));
        sb.Append(" +inv_u=").Append(Coefs(InvU, true)).Append(" +inv_v=").Append(Coefs(InvV, false));
        return sb.ToString();
    }
}
