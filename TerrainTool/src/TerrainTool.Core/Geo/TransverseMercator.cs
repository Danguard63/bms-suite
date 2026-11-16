namespace TerrainTool.Core.Geo;

/// <summary>
/// Trasversa di Mercatore sull'ellissoide WGS84 con la serie di Krüger al 6° ordine
/// (Karney 2011). Precisione sub-millimetrica entro qualche migliaio di km dal meridiano
/// centrale: sufficiente per un teatro da 2048 km senza dipendere da PROJ/GDAL.
/// Coordinate restituite in metri, senza falso est/nord (x=0 sul meridiano centrale, y=0 all'equatore).
/// </summary>
public sealed class TransverseMercator
{
    public const double WgsA = 6378137.0;
    public const double WgsF = 1.0 / 298.257223563;

    private readonly double _k0, _lon0Rad, _e, _e2, _aHat;
    private readonly double[] _alpha = new double[7];
    private readonly double[] _beta = new double[7];

    public double CentralMeridian { get; }
    public double ScaleFactor => _k0;

    public TransverseMercator(double centralMeridianDeg, double k0 = 0.9996, double a = WgsA, double f = WgsF)
    {
        CentralMeridian = centralMeridianDeg;
        _k0 = k0;
        _lon0Rad = Deg2Rad(centralMeridianDeg);
        _e2 = f * (2 - f);
        _e = Math.Sqrt(_e2);
        double n = f / (2 - f), n2 = n * n, n3 = n2 * n, n4 = n3 * n, n5 = n4 * n, n6 = n5 * n;
        _aHat = a / (1 + n) * (1 + n2 / 4 + n4 / 64 + n6 / 256);

        _alpha[1] = n / 2 - 2 * n2 / 3 + 5 * n3 / 16 + 41 * n4 / 180 - 127 * n5 / 288 + 7891 * n6 / 37800;
        _alpha[2] = 13 * n2 / 48 - 3 * n3 / 5 + 557 * n4 / 1440 + 281 * n5 / 630 - 1983433 * n6 / 1935360;
        _alpha[3] = 61 * n3 / 240 - 103 * n4 / 140 + 15061 * n5 / 26880 + 167603 * n6 / 181440;
        _alpha[4] = 49561 * n4 / 161280 - 179 * n5 / 168 + 6601661 * n6 / 7257600;
        _alpha[5] = 34729 * n5 / 80640 - 3418889 * n6 / 1995840;
        _alpha[6] = 212378941 * n6 / 319334400;

        _beta[1] = n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360 - 81 * n5 / 512 + 96199 * n6 / 604800;
        _beta[2] = n2 / 48 + n3 / 15 - 437 * n4 / 1440 + 46 * n5 / 105 - 1118711 * n6 / 3870720;
        _beta[3] = 17 * n3 / 480 - 37 * n4 / 840 - 209 * n5 / 4480 + 5569 * n6 / 90720;
        _beta[4] = 4397 * n4 / 161280 - 11 * n5 / 504 - 830251 * n6 / 7257600;
        _beta[5] = 4583 * n5 / 161280 - 108847 * n6 / 3991680;
        _beta[6] = 20648693 * n6 / 638668800;
    }

    /// <summary>lat/lon in gradi → (x est, y nord) in metri.</summary>
    public (double X, double Y) Forward(double latDeg, double lonDeg)
    {
        double phi = Deg2Rad(latDeg);
        double lam = Deg2Rad(lonDeg) - _lon0Rad;
        double sinPhi = Math.Sin(phi);
        double t = Math.Sinh(Math.Atanh(sinPhi) - _e * Math.Atanh(_e * sinPhi));
        double xiP = Math.Atan2(t, Math.Cos(lam));
        double etaP = Math.Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double xi = xiP, eta = etaP;
        for (int j = 1; j <= 6; j++)
        {
            xi += _alpha[j] * Math.Sin(2 * j * xiP) * Math.Cosh(2 * j * etaP);
            eta += _alpha[j] * Math.Cos(2 * j * xiP) * Math.Sinh(2 * j * etaP);
        }
        return (_k0 * _aHat * eta, _k0 * _aHat * xi);
    }

    /// <summary>(x est, y nord) in metri → lat/lon in gradi.</summary>
    public (double Lat, double Lon) Inverse(double x, double y)
    {
        double xi = y / (_k0 * _aHat), eta = x / (_k0 * _aHat);
        double xiP = xi, etaP = eta;
        for (int j = 1; j <= 6; j++)
        {
            xiP -= _beta[j] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaP -= _beta[j] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        double sinhEtaP = Math.Sinh(etaP), cosXiP = Math.Cos(xiP);
        double tauP = Math.Sin(xiP) / Math.Sqrt(sinhEtaP * sinhEtaP + cosXiP * cosXiP);
        double lam = Math.Atan2(sinhEtaP, cosXiP);

        // Newton per tau = tan(phi) a partire da tau' (latitudine conforme)
        double tau = tauP;
        for (int i = 0; i < 6; i++)
        {
            double sigma = Math.Sinh(_e * Math.Atanh(_e * tau / Math.Sqrt(1 + tau * tau)));
            double tauI = tau * Math.Sqrt(1 + sigma * sigma) - sigma * Math.Sqrt(1 + tau * tau);
            double dTau = (tauP - tauI) / Math.Sqrt(1 + tauI * tauI)
                          * (1 + (1 - _e2) * tau * tau) / ((1 - _e2) * Math.Sqrt(1 + tau * tau));
            tau += dTau;
            if (Math.Abs(dTau) < 1e-14) break;
        }
        return (Rad2Deg(Math.Atan(tau)), Rad2Deg(lam + _lon0Rad));
    }

    public static double Deg2Rad(double d) => d * Math.PI / 180.0;
    public static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
