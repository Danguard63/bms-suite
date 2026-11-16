namespace TerrainTool.Core.Campaign;

/// <summary>
/// Spostamento di un waypoint di un volo come nel TE di BMS: la velocita' di ogni tratto resta quella di prima, quindi
/// cambiano l'orario del punto spostato (tratto prima) e, dal punto dopo in avanti, tutti gli orari dello stesso scarto
/// (tratto dopo). Decollo e atterraggio non si spostano (sono sulla base). Il TOT del volo segue il punto bersaglio.
/// </summary>
public static class RouteEdit
{
    public sealed record Plan(int Index, short X, short Y, uint[] Arrive, uint[] Depart, uint Tot, long TotShiftMs);

    public static bool CanMove(CampUnit f, int i) =>
        f.Kind == UnitKind.Flight && i > 0 && i < f.Waypoints.Count && f.Waypoints[i].Action is not (1 or 7) && f.Waypoints[i].Arrive != 0 && i < f.WpAt.Length;

    private static double Dist(int x1, int y1, int x2, int y2) => Math.Sqrt((double)(x1 - x2) * (x1 - x2) + (double)(y1 - y2) * (y1 - y2));

    /// <summary>Nuovi orari se il waypoint i va in (x, y). wps/dep = rotta di partenza (prima del trascinamento).</summary>
    public static Plan Compute(IReadOnlyList<CampWaypoint> wps, IReadOnlyList<uint> dep, uint tot, int i, short x, short y)
    {
        int n = wps.Count;
        var A = wps.Select(w => w.Arrive).ToArray();
        var D = dep.ToArray();
        // tempo del tratto j-1 -> j alla stessa velocita' di prima, con la nuova lunghezza
        long Leg(int j, double newLen)
        {
            long t = (long)wps[j].Arrive - dep[j - 1];
            double old = Dist(wps[j - 1].X, wps[j - 1].Y, wps[j].X, wps[j].Y);
            if (t <= 0) return Math.Max(0, t);
            if (old < 0.5) return t;                         // tratto nullo: velocita' sconosciuta, tempo invariato
            return (long)Math.Round(t * newLen / old);
        }
        long d1 = (long)dep[i - 1] + Leg(i, Dist(wps[i - 1].X, wps[i - 1].Y, x, y)) - wps[i].Arrive;
        A[i] = (uint)Math.Max(0, (long)A[i] + d1);
        D[i] = (uint)Math.Max(0, (long)D[i] + d1);
        long d2 = 0;
        if (i + 1 < n)
        {
            d2 = (long)D[i] + Leg(i + 1, Dist(x, y, wps[i + 1].X, wps[i + 1].Y)) - wps[i + 1].Arrive;
            if (wps[i + 1].Arrive == 0) d2 = 0;
            // i punti senza orario (0: es. l'aeroporto alternato dopo l'atterraggio) restano senza orario
            for (int j = i + 1; j < n; j++) if (wps[j].Arrive != 0) { A[j] = (uint)Math.Max(0, (long)A[j] + d2); D[j] = (uint)Math.Max(0, (long)D[j] + d2); }
        }
        // TOT: il primo waypoint bersaglio
        int t0 = Enumerable.Range(0, n).FirstOrDefault(j => wps[j].IsTarget, -1);
        uint newTot = tot;
        long shift = 0;
        if (t0 >= 0 && tot > 0) { shift = (long)A[t0] - wps[t0].Arrive; newTot = (uint)Math.Max(0, (long)tot + shift); }
        return new Plan(i, x, y, A, D, newTot, shift);
    }

    /// <summary>Scrive il piano nel file (posizione del punto e orari da quel punto in avanti, TOT).</summary>
    public static void Apply(UniFile uf, CampUnit f, Plan p)
    {
        for (int j = p.Index; j < f.Waypoints.Count; j++)
        {
            var w = f.Waypoints[j];
            short x = j == p.Index ? p.X : w.X, y = j == p.Index ? p.Y : w.Y;
            uf.SetWaypoint(f, j, x, y, p.Arrive[j], p.Depart[j]);
        }
        if (p.Tot != f.TimeOnTarget) uf.SetTimeOnTarget(f, p.Tot);
    }
}
