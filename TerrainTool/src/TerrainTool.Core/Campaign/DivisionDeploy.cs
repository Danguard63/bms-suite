namespace TerrainTool.Core.Campaign;

public enum Echelon { Front, Second, Reserve }

public sealed class DeployRow
{
    public CampUnit Brigade = null!;
    public CampObjective? From;
    public CampObjective Target = null!;
    public Echelon Echelon;
    public double MoveKm, FrontKm;
    public bool Light;
    public bool Apply = true;
}

/// <summary>
/// Schieramento delle brigate di una squadra come nelle campagne Korea 4.38 (Save1/Save4, situazione di partenza):
/// ~55% entro 35 km dal fronte (soprattutto brigate leggere: fanteria, aviotrasportate, marines), ~20% a 35-100 km
/// (soprattutto corazzati, meccanizzati, artiglieria), il resto in riserva oltre 100 km; mai su un obiettivo di 1a linea;
/// una brigata per obiettivo, quasi sempre army base, la brigata sulla casella dell'obiettivo, i battaglioni con lei
/// (stesso obiettivo, stessa posizione); ordini Reserve (gli ordini veri li da' BMS in base al fronte).
/// </summary>
public sealed class DivisionDeploy
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    public double FrontShare { get; set; } = 0.55;
    public double SecondShare { get; set; } = 0.20;
    public double FrontKm { get; set; } = 35;
    public double SecondKm { get; set; } = 100;
    /// <summary>Peso dello spostamento: piu' alto = le brigate restano piu' vicine a dove sono.</summary>
    public double MoveWeight { get; set; } = 1.0;

    public DivisionDeploy(CampaignDoc d, CampaignDb db) { _d = d; _db = db; }

    public static bool IsLight(string ucdName)
    {
        string n = ucdName.ToLowerInvariant();
        return n.Contains("infantry") || n.Contains("airmobile") || n.Contains("marine") || n.Contains("airborne") || n.Contains("air assault") || n.Contains("recon");
    }

    /// <summary>Preferenza del tipo di obiettivo per una brigata (Korea: quasi sempre army base, poi citta').</summary>
    public static int TypePref(int t) => t switch { 3 => 3, 8 or 28 or 10 => 2, 30 or 29 or 13 => 1, _ => 0 };

    private static double Km(CampObjective a, CampObjective b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double Km(CampUnit u, CampObjective o) => Math.Sqrt(Math.Pow(u.X - o.X, 2) + Math.Pow(u.Y - o.Y, 2));

    /// <summary>Distanza (km) di ogni obiettivo della squadra dal piu' vicino obiettivo nemico di 1a linea; vuoto se non c'e' fronte.</summary>
    public Dictionary<CampObjective, double> FrontDistance(int team, out Dictionary<CampObjective, int> lines)
    {
        lines = _d.ComputeFrontLines();
        var T = _d.Teams!.Teams;
        var enemyFront = lines.Where(kv => kv.Value == 1 && kv.Key.Owner != team && T[team].Stance[kv.Key.Owner] >= 4).Select(kv => kv.Key).ToList();
        var res = new Dictionary<CampObjective, double>();
        if (enemyFront.Count == 0) return res;
        foreach (var o in _d.Objectives.Where(o => o.Owner == team))
            res[o] = enemyFront.Min(e => Km(o, e));
        return res;
    }

    public List<DeployRow> Plan(int team, out string message)
    {
        message = "";
        var rows = new List<DeployRow>();
        var brigades = _d.Units?.Units.Where(u => u.Kind == UnitKind.Brigade && u.Owner == team).ToList() ?? new();
        if (brigades.Count == 0) { message = "This team has no brigades."; return rows; }
        var fd = FrontDistance(team, out var lines);
        if (fd.Count == 0) { message = "This team has no front: no hostile/at war team touches its objectives (set relations and owners first)."; return rows; }

        var cands = fd.Where(kv => TypePref(_db.ObjectiveType(kv.Key.Type)) > 0 && lines.GetValueOrDefault(kv.Key) != 1).Select(kv => kv.Key).ToList();
        Echelon Band(CampObjective o) => fd[o] <= FrontKm ? Echelon.Front : fd[o] <= SecondKm ? Echelon.Second : Echelon.Reserve;
        // una brigata per obiettivo; quelle di altre squadre o gia' sistemate non contano (si rifa' tutto per questa squadra)
        var used = new HashSet<CampObjective>();

        int n = brigades.Count, nF = (int)Math.Round(n * FrontShare), nS = (int)Math.Round(n * SecondShare);
        bool Light(CampUnit b) => IsLight(_db.UcdName(b.Type - 100));
        double CurFront(CampUnit b) => _d.Obj(b.AssignedObj) is { } o && fd.TryGetValue(o, out var k) ? k : 9999;
        // si parte da dove sono ora e si sposta il minimo: chi manca in una fascia si prende dalla fascia vicina
        // (verso il fronte le leggere piu' vicine, indietro le pesanti piu' lontane), come lo schieramento Korea
        Echelon Cur(CampUnit b) { double k = CurFront(b); return k <= FrontKm ? Echelon.Front : k <= SecondKm ? Echelon.Second : Echelon.Reserve; }
        var front = brigades.Where(b => Cur(b) == Echelon.Front).ToList();
        var second = brigades.Where(b => Cur(b) == Echelon.Second).ToList();
        var reserve = brigades.Where(b => Cur(b) == Echelon.Reserve).ToList();
        void Shift(List<CampUnit> from, List<CampUnit> to, int count, bool forward)
        {
            var pick = (forward ? from.OrderByDescending(Light).ThenBy(CurFront) : from.OrderBy(Light).ThenByDescending(CurFront)).Take(count).ToList();
            foreach (var x in pick) { from.Remove(x); to.Add(x); }
        }
        if (front.Count > nF) Shift(front, second, front.Count - nF, false);
        if (front.Count < nF) { Shift(second, front, Math.Min(second.Count, nF - front.Count), true); if (front.Count < nF) Shift(reserve, front, nF - front.Count, true); }
        if (second.Count > nS) Shift(second, reserve, second.Count - nS, false);
        if (second.Count < nS) Shift(reserve, second, Math.Min(reserve.Count, nS - second.Count), true);

        // chi e' gia' su un obiettivo adatto della sua fascia resta li'
        bool Valid(CampObjective? o, Echelon e) => o != null && cands.Contains(o) && Band(o) == e && !used.Contains(o);
        foreach (var (g, e) in new[] { (front, Echelon.Front), (second, Echelon.Second), (reserve, Echelon.Reserve) })
            foreach (var b in g)
                if (_d.Obj(b.AssignedObj) is { } o && Valid(o, e)) used.Add(o);

        void Place(List<CampUnit> group, Echelon e)
        {
            // prima chi ha meno scelta (piu' lontano dalla sua fascia), poi gli altri
            foreach (var b in group.OrderByDescending(b => cands.Where(o => Band(o) == e).Select(o => Km(b, o)).DefaultIfEmpty(0).Min()))
            {
                var cur = _d.Obj(b.AssignedObj);
                if (cur != null && used.Contains(cur) && cands.Contains(cur) && Band(cur) == e && !rows.Any(r => r.Target == cur))
                {
                    rows.Add(new DeployRow { Brigade = b, From = cur, Target = cur, Echelon = e, FrontKm = fd[cur], Light = Light(b),
                                             Apply = b.X != cur.X || b.Y != cur.Y || b.Orders != 0 || b.Elements.Any(x => _d.Unit(x) is { } bn && (bn.AssignedObj != cur.Id || bn.X != cur.X || bn.Y != cur.Y)) });
                    continue;
                }
                var pool = cands.Where(o => !used.Contains(o) && Band(o) == e).ToList();
                if (pool.Count == 0) pool = cands.Where(o => !used.Contains(o)).ToList();   // fascia piena: il posto libero piu' adatto
                if (pool.Count == 0) continue;
                var best = pool.OrderBy(o => Km(b, o) * MoveWeight - TypePref(_db.ObjectiveType(o.Type)) * 25 - (o == cur ? 40 : 0)
                                             + (Band(o) == e ? 0 : 500)).First();
                used.Add(best);
                rows.Add(new DeployRow { Brigade = b, From = cur, Target = best, Echelon = e, MoveKm = Km(b, best), FrontKm = fd[best], Light = Light(b),
                                         Apply = best != cur || b.X != best.X || b.Y != best.Y || b.Orders != 0 });
            }
        }
        Place(front, Echelon.Front);
        Place(second, Echelon.Second);
        Place(reserve, Echelon.Reserve);
        int moves = rows.Count(r => r.Target != r.From);
        message = $"{n} brigades: {rows.Count(r => r.Echelon == Echelon.Front)} front (≤{FrontKm:0} km), {rows.Count(r => r.Echelon == Echelon.Second)} second ({FrontKm:0}-{SecondKm:0} km), " +
                  $"{rows.Count(r => r.Echelon == Echelon.Reserve)} reserve; {moves} change objective.";
        return rows;
    }

    /// <summary>Applica: brigata e suoi battaglioni sull'obiettivo (stessa casella), ordini Reserve. Restituisce l'annullamento.</summary>
    public Action Apply(IEnumerable<DeployRow> rows, bool reserveOrders)
    {
        var uf = _d.Units!;
        var undo = new List<Action>();
        foreach (var r in rows.Where(r => r.Apply))
        {
            foreach (var u in new[] { r.Brigade }.Concat(r.Brigade.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>()))
            {
                var (ox, oy, oa, oo) = (u.X, u.Y, u.AssignedObj, u.Orders);
                uf.SetAssignedObjective(u, r.Target.Id);
                uf.SetPosition(u, r.Target.X, r.Target.Y);
                if (reserveOrders && u.Orders != 9) uf.SetOrders(u, 0);
                undo.Add(() => { uf.SetAssignedObjective(u, oa); uf.SetPosition(u, ox, oy); uf.SetOrders(u, oo); });
            }
        }
        _d.Dirty = true;
        return () => { for (int i = undo.Count - 1; i >= 0; i--) undo[i](); };
    }

    /// <summary>Come e' schierata ora la squadra (stessi scaglioni), per confronto con la Korea.</summary>
    public string Describe(int team)
    {
        var fd = FrontDistance(team, out _);
        var br = _d.Units?.Units.Where(u => u.Kind == UnitKind.Brigade && u.Owner == team).ToList() ?? new();
        if (br.Count == 0 || fd.Count == 0) return "";
        int f = 0, s = 0, r = 0;
        foreach (var b in br)
        {
            double k = _d.Obj(b.AssignedObj) is { } o && fd.TryGetValue(o, out var x) ? x : 9999;
            if (k <= FrontKm) f++; else if (k <= SecondKm) s++; else r++;
        }
        return $"now: {f} front, {s} second, {r} reserve (Korea start: about {FrontShare:P0}, {SecondShare:P0}, {1 - FrontShare - SecondShare:P0})";
    }
}
