using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

public enum AdClass { Long, Medium, Short }

/// <summary>Riga del piano delle difese aeree: un battaglione esistente da spostare, o uno nuovo da creare.</summary>
public sealed class AdPlanRow
{
    public CampObjective Target = null!;
    public CampUnit? Unit;                    // esistente (spostamento)
    public int NewType = -1;                  // indice CT del tipo nuovo (creazione)
    public double MoveKm;
    public bool Apply = true;
    public string What = "";
}

/// <summary>
/// Difese aeree della campagna, con le regole viste nelle campagne Korea 4.38: battaglioni antiaerei indipendenti (UCD MainRole 5,
/// ordine Air defense), uno per obiettivo (sulle basi aeree fino al numero di liste SAM/AAA della disposizione), schierati
/// sull'obiettivo; SAM a lungo raggio sui SAM site, medi su SAM site/porti/basi, corto raggio e contraerea su basi, radar e il resto.
/// </summary>
public sealed class AirDefense
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly string? _ordDir;
    private readonly Dictionary<int, int[]> _lists = new();

    public AirDefense(CampaignDoc d, CampaignDb db)
    {
        _d = d; _db = db;
        string? dir = db.CtPath == null ? null : Path.Combine(Path.GetDirectoryName(db.CtPath)!, "ObjectiveRelatedData");
        _ordDir = dir != null && Directory.Exists(dir) ? dir : null;
    }

    public int ObjType(CampObjective o) => _db.ObjectiveType(o.Type);

    /// <summary>Numero di liste di punti per tipo (PHD Type) nella disposizione dell'obiettivo: 4 = SAM, 6 = AAA.</summary>
    public int[] Lists(CampObjective o)
    {
        int ocd = _db.Ct.TryGetValue(o.Type - 100, out var c) ? c.EntityIdx : -1;
        if (_lists.TryGetValue(ocd, out var r)) return r;
        r = new int[32];
        string? f = _ordDir == null || ocd < 0 ? null : Directory.Exists(Path.Combine(_ordDir, $"OCD_{ocd:00000}"))
            ? Directory.EnumerateFiles(Path.Combine(_ordDir, $"OCD_{ocd:00000}"), "PHD_*.XML").FirstOrDefault() : null;
        if (f != null)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"<Type>(\d+)</Type>"))
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) && t < 32) r[t]++;
        return _lists[ocd] = r;
    }

    /// <summary>Tipi di obiettivo che in Korea hanno difese aeree (basi, SAM site, radar, porti, army base, industria).</summary>
    public static bool Eligible(int t) => t is 1 or 2 or 31 or 21 or 19 or 3 or 20 or 25 or 11 or 7 or 17 or 9 or 30;

    /// <summary>Quanti battaglioni antiaerei puo' ospitare: basi = liste SAM + AAA della disposizione (almeno 1), il resto 1.</summary>
    public int Capacity(CampObjective o)
    {
        int t = ObjType(o);
        if (!Eligible(t)) return 0;
        if (t is 1 or 2) { var l = Lists(o); return Math.Max(1, l[4] + l[6]); }
        return 1;
    }

    /// <summary>Distanza massima per prendere un battaglione da un obiettivo meno importante (km).</summary>
    public double DonorKm { get; set; } = 50;

    public AdClass ClassOf(int ctIndex)
    {
        int r = _db.UcdOf(ctIndex)?.RngAir ?? 0;
        return r >= 40 ? AdClass.Long : r >= 15 ? AdClass.Medium : AdClass.Short;
    }

    public static string ClassName(AdClass c) => c switch { AdClass.Long => "long range SAM", AdClass.Medium => "medium SAM", _ => "short range / AAA" };

    /// <summary>Dove puo' andare ogni classe (in ordine di preferenza del tipo di obiettivo).</summary>
    public static int TypeFit(AdClass c, int t) => c switch
    {
        AdClass.Long => t switch { 31 => 3, _ => 0 },                      // Korea: SA-10, SA-5, Patriot solo sui SAM site
        AdClass.Medium => t switch { 31 => 3, 19 => 2, 1 or 2 => 2, 3 => 1, 20 or 25 or 17 => 1, _ => 0 },
        _ => t switch { 1 or 2 or 30 => 3, 21 => 3, 3 => 2, 19 or 20 or 25 or 11 or 7 or 17 or 9 => 2, 31 => 1, _ => 0 },
    };

    public bool IsAdUnit(CampUnit u) => u.Kind == UnitKind.Battalion && (u.Orders == 9 || _db.IsAirDefense(u.Type - 100)) && u.Parent.IsNull;

    public List<CampUnit> AdUnits(int team) => _d.Units?.Units.Where(u => u.Owner == team && IsAdUnit(u)).ToList() ?? new();

    public List<CampUnit> AdAt(CampObjective o) => _d.Units?.Units.Where(u => u.AssignedObj == o.Id && IsAdUnit(u)).ToList() ?? new();

    private static double Km(CampUnit u, CampObjective o) => Math.Sqrt(Math.Pow(u.X - o.X, 2) + Math.Pow(u.Y - o.Y, 2));

    /// <param name="redistribute">i battaglioni antiaerei esistenti della squadra si rimettono sugli obiettivi piu' importanti</param>
    /// <param name="newTypes">tipi (indice CT) per i battaglioni nuovi sugli obiettivi rimasti scoperti; vuoto = non crearne</param>
    /// <param name="minPriority">obiettivi con priorita' (del file) sotto questa soglia: niente difese nuove</param>
    public List<AdPlanRow> Plan(int team, bool redistribute, IReadOnlyList<int> newTypes, int minPriority, int maxNew)
    {
        var rows = new List<AdPlanRow>();
        var targets = _d.Objectives.Where(o => o.Owner == team && Capacity(o) > 0)
            .OrderByDescending(o => o.Priority).ThenByDescending(o => ObjType(o) is 1 or 2 ? 2 : ObjType(o) == 31 ? 1 : 0).ToList();
        var free = targets.ToDictionary(o => o, Capacity);
        var units = AdUnits(team);
        if (redistribute)
        {
            // Come in Korea ognuno resta dov'e' se il posto gli va bene; si spostano solo i battaglioni su posti non adatti
            // (o persi) e quelli che servono a coprire obiettivi piu' importanti rimasti scoperti, prendendoli dove contano meno.
            var at = new Dictionary<CampUnit, CampObjective?>();
            foreach (var u in units)
            {
                var o = _d.Obj(u.AssignedObj);
                bool ok = o != null && free.ContainsKey(o) && free[o] > 0 && TypeFit(ClassOf(u.Type - 100), ObjType(o)) > 0;
                if (ok) free[o!]--;
                at[u] = ok ? o : null;
            }
            void Move(CampUnit u, CampObjective dst)
            {
                if (at[u] is { } src) free[src]++;
                free[dst]--; at[u] = dst;
            }
            // 1) quelli senza un posto adatto: al posto libero migliore per la loro classe
            foreach (var u in units.Where(u => at[u] == null).OrderByDescending(u => _db.UcdOf(u.Type - 100)?.RngAir ?? 0))
            {
                var c = ClassOf(u.Type - 100);
                var best = targets.Where(o => free[o] > 0 && TypeFit(c, ObjType(o)) > 0)
                    .OrderByDescending(o => TypeFit(c, ObjType(o)) * 1000 + o.Priority * 10 - Km(u, o) * 0.2).FirstOrDefault();
                if (best != null) Move(u, best);
            }
            // 2) obiettivi importanti scoperti: si prende un battaglione adatto vicino (DonorKm) da un obiettivo meno importante (almeno 10 punti
            //    sotto); SAM site e radar non si svuotano (la difesa aerea e' il loro scopo)
            foreach (var o in targets.Where(o => free[o] == Capacity(o)).ToList())
            {
                var donor = units.Where(u => at[u] is { } src && src != o && TypeFit(ClassOf(u.Type - 100), ObjType(o)) >= 2
                                             && src.Priority + 10 <= o.Priority && AdAtCount(at, src) >= 1
                                             && ObjType(src) is not (31 or 21) && Km(u, o) <= DonorKm)
                    .OrderBy(u => at[u]!.Priority).ThenBy(u => Km(u, o)).FirstOrDefault();
                if (donor != null) Move(donor, o);
            }
            foreach (var u in units)
                if (at[u] is { } dst && dst.Id != u.AssignedObj)
                    rows.Add(new AdPlanRow { Target = dst, Unit = u, MoveKm = Km(u, dst),
                        What = $"move {_db.UnitTypeName(u.Type)} (Bn {u.NameId}) from {_d.Obj(u.AssignedObj)?.Name ?? "?"} ({Km(u, dst):0} km)" });
        }
        else
            foreach (var u in units) if (_d.Obj(u.AssignedObj) is { } o && free.ContainsKey(o)) free[o]--;

        if (newTypes.Count > 0)
        {
            int made = 0;
            foreach (var o in targets)
            {
                if (made >= maxNew) break;
                if (o.Priority < minPriority || free[o] <= 0) continue;
                bool covered = free[o] < Capacity(o);
                if (covered) continue;                               // ha gia' una difesa: in Korea quasi sempre una sola
                int t = ObjType(o);
                int pick = newTypes.OrderByDescending(n => TypeFit(ClassOf(n), t)).ThenByDescending(n => _db.UcdOf(n)?.RngAir ?? 0).FirstOrDefault(-1);
                if (pick < 0 || TypeFit(ClassOf(pick), t) == 0) continue;
                free[o]--; made++;
                rows.Add(new AdPlanRow { Target = o, NewType = pick, What = $"new {_db.UnitTypeName(pick + 100)}" });
            }
        }
        return rows;
    }

    private static int AdAtCount(Dictionary<CampUnit, CampObjective?> at, CampObjective o) => at.Values.Count(x => x == o);

    /// <summary>Applica le righe scelte; restituisce un'azione che le annulla tutte.</summary>
    public Action Apply(IEnumerable<AdPlanRow> rows)
    {
        var undo = new List<Action>();
        var uf = _d.Units ?? throw new InvalidOperationException("no units in this file");
        foreach (var r in rows.Where(r => r.Apply))
        {
            if (r.Unit is { } u)
            {
                var (ox, oy, oa, oo) = (u.X, u.Y, u.AssignedObj, u.Orders);
                uf.SetAssignedObjective(u, r.Target.Id); uf.SetPosition(u, r.Target.X, r.Target.Y); uf.SetOrders(u, 9);
                undo.Add(() => { uf.SetAssignedObjective(u, oa); uf.SetPosition(u, ox, oy); uf.SetOrders(u, oo); });
            }
            else if (r.NewType >= 0)
            {
                var nu = AddUnit(r.NewType, r.Target);
                if (nu != null) undo.Add(() => { uf.Remove(nu); _d.Reindex(); });
            }
        }
        _d.Reindex();
        _d.Dirty = true;
        return () => { for (int i = undo.Count - 1; i >= 0; i--) undo[i](); _d.Reindex(); };
    }

    /// <summary>Nuovo battaglione antiaereo sull'obiettivo (squadra = proprietario dell'obiettivo).</summary>
    public CampUnit? AddUnit(int ctType, CampObjective o)
    {
        var uf = _d.Units;
        if (uf == null) return null;
        var bats = uf.Units.Where(u => u.Kind == UnitKind.Battalion).ToList();
        var template = bats.FirstOrDefault(u => u.Owner == o.Owner && IsAdUnit(u)) ?? bats.FirstOrDefault(u => IsAdUnit(u)) ?? bats.FirstOrDefault();
        if (template == null) return null;
        var id = uf.FreeId(_d.Objectives.Select(x => x.Id));
        short nameId = (short)(bats.Where(u => u.Owner == o.Owner).Select(u => (int)u.NameId).DefaultIfEmpty(0).Max() + 1);
        short campId = (short)(uf.Units.Select(u => (int)u.CampId).DefaultIfEmpty(0).Max() + 1);
        var nu = uf.AddBattalion(template, ctType + 100, id, o.X, o.Y, o.Owner, o.Id, 9, _db.FullRoster(ctType), nameId, campId);
        uf.SetCargo(nu, default);                                   // niente legami presi dal battaglione copiato
        uf.SetUnitFlags(nu, UnitEditor.FlagsBnAirDefense);          // come la Korea: 0x3E (Assigned, Ordered, No Plan...)
        uf.SetDest(nu, o.X + 1, o.Y + 1);
        _d.Reindex();
        _d.Dirty = true;
        return nu;
    }
}
