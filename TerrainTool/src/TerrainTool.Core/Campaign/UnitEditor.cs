namespace TerrainTool.Core.Campaign;

/// <summary>Riga della tabella dei tipi di unita' di terra (come la tabella delle unita' di Mission Commander).</summary>
public sealed class UnitTypeRow
{
    public int Ct { get; init; }
    public string Name { get; init; } = "";
    public UnitKind Kind { get; init; }
    public string KindText => Kind == UnitKind.Brigade ? "Brigade" : "Battalion";
    public string MainVehicle { get; init; } = "";
    public VehicleRole Role { get; init; }
    public string RoleText => Kind == UnitKind.Brigade ? "—" : VehicleRoles.Name(Role);
    public Bloc Bloc { get; init; }
    public string ProducerText => Bloc == Bloc.Unknown ? "" : Producer.Short(Bloc);
    public string Vehicles { get; init; } = "";
    public int VehicleCount { get; init; }
    public int AirRange { get; init; }
    public int InFile { get; set; }
}

/// <summary>
/// Editor di battaglioni e brigate: aggiungere (da un record esistente dello stesso file, come fa gia' la difesa aerea),
/// cambiare tipo, spostare in/da una brigata, togliere. Regole Korea: il battaglione sta sull'obiettivo della brigata, stessa
/// divisione, numero = numero della brigata x 10 + posizione (31 -> 311, 312); CampId tutti diversi; ordini Reserve.
/// Ogni operazione restituisce l'annullamento (fotografia del file unita').
/// </summary>
public sealed class UnitEditor
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    public UnitEditor(CampaignDoc d, CampaignDb db) { _d = d; _db = db; }

    /// <summary>Battaglioni per brigata: Mission Commander non ne accetta piu' di 5 (Korea: 2-4).</summary>
    public const int MaxElements = 5;

    // flag delle unita' (unit_flags), nomi come in Mission Commander
    public const int FAssigned = 0x4, FOrdered = 0x8, FNoPlan = 0x10, FParent = 0x20, FNotByHq = 0x80, FHumanCont = 0x80, FLockClientMp = 0x2, FMoving = 0x400, FInactive = 0x20000;
    public static readonly string[] FlagNames =
    {
        "Dead", "Lock Client MP", "Assigned", "Ordered", "No Plan", "Parent", "Engaged", "Human Cont (not set by HQ)", "Scripted", "Commando", "Moving", "Refused",
        "Has ECM", "Cargo", "Combat", "Broken", "Losses", "Inactive", "Fragmented", "Targetted", "Retreating", "Detached", "Supported",
        "Temp Dest", "Final", "Has Pilots", "Diverted", "Fired", "Locked", "IA Kill", "No Abort", "Bit 31",
    };
    /// <summary>Flag delle unita' nuove come in Korea: brigata 0x22, battaglione in brigata 0x02, antiaereo 0x3E, altro indipendente 0x22.</summary>
    public const int FlagsBrigade = 0x22, FlagsBnInBrigade = 0x02, FlagsBnAirDefense = 0x3E, FlagsBnIndependent = 0x22;

    /// <summary>Sottotipo CT del battaglione: 1 antiaereo (AAA/SAM), 13 rifornimenti (come Mission Commander: BatIsAD / BatIsSupply).</summary>
    public bool IsAirDefense(CampUnit u) => u.Kind == UnitKind.Battalion && _db.Ct.TryGetValue(u.Type - 100, out var c) && c.SubType == 1;
    public bool IsSupply(CampUnit u) => u.Kind == UnitKind.Battalion && _db.Ct.TryGetValue(u.Type - 100, out var c) && c.SubType == 13;

    /// <summary>
    /// Problemi di un'unita' di terra (colori della lista, come Mission Commander per BMS 4.37+, con le regole Korea):
    /// Error = rosso, Warning = arancione.
    /// </summary>
    public List<(IssueLevel Level, string Text)> Problems(CampUnit u)
    {
        var res = new List<(IssueLevel, string)>();
        if (u.Kind == UnitKind.Brigade)
        {
            if (u.Elements.Count == 0) res.Add((IssueLevel.Warning, "no battalions"));
            if (u.Elements.Count > MaxElements) res.Add((IssueLevel.Error, $"{u.Elements.Count} battalions (max {MaxElements})"));
            foreach (var e in u.Elements)
            {
                var b = _d.Unit(e);
                if (b == null) { res.Add((IssueLevel.Error, $"missing battalion {e}")); continue; }
                if (b.Owner != u.Owner) res.Add((IssueLevel.Warning, $"Bn {b.NameId} belongs to another team"));
                if (IsAirDefense(b) || IsSupply(b)) res.Add((IssueLevel.Warning, $"Bn {b.NameId} is {(IsSupply(b) ? "supply" : "air defense")}: it should be a support element, not a battalion of the brigade"));
                if (b.Parent != u.Id) res.Add((IssueLevel.Warning, $"Bn {b.NameId} points to another brigade"));
            }
            if (u.AssignedObj.IsNull) res.Add((IssueLevel.Error, "no assigned objective"));
        }
        else if (u.Kind == UnitKind.Battalion)
        {
            if ((IsAirDefense(u) || IsSupply(u)) && (u.UnitFlags & FNoPlan) == 0) res.Add((IssueLevel.Warning, "air defense / supply without No Plan"));
            if ((IsAirDefense(u) || IsSupply(u)) && _d.Unit(u.Parent)?.Kind == UnitKind.Brigade) res.Add((IssueLevel.Warning, "air defense / supply inside a brigade"));
            if (!u.Parent.IsNull && _d.Unit(u.Parent) is { } p && !p.Elements.Contains(u.Id)) res.Add((IssueLevel.Warning, "its brigade does not list it"));
            if (!u.Parent.IsNull && _d.Unit(u.Parent) == null) res.Add((IssueLevel.Error, "missing brigade"));
            if (u.AssignedObj.IsNull) res.Add((IssueLevel.Error, "no assigned objective"));
        }
        return res;
    }

    private UniFile Uf => _d.Units ?? throw new InvalidOperationException("this file has no units");

    public static List<UnitTypeRow> Types(CampaignDb db, CampaignDoc? d)
    {
        var used = d?.Units?.Units.GroupBy(u => u.Type - 100).ToDictionary(g => g.Key, g => g.Count()) ?? new();
        var res = new List<UnitTypeRow>();
        foreach (var (i, c) in db.Ct.OrderBy(k => k.Key))
        {
            if (c.EntityType != 4 || c.Domain != 3 || c.Type is not (1 or 2)) continue;
            var u = db.UcdOf(i);
            if (u == null || u.Name.Length == 0 || u.Name.Equals("Null", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = new List<(string Name, int N)>(); int tot = 0;
            for (int k = 0; k < 16; k++)
            {
                int n = u.Elements?[k] ?? 0, v = u.Vehicles[k];
                if (n <= 0 || v <= 0) continue;
                tot += n;
                string vn = db.VehicleName(v);
                int at = parts.FindIndex(p => p.Name == vn);
                if (at >= 0) parts[at] = (vn, parts[at].N + n); else parts.Add((vn, n));
            }
            var vct = db.UnitVehicleCts(i);
            string main = vct.Count > 0 ? db.VehicleName(vct[0]) : "";
            res.Add(new UnitTypeRow
            {
                Ct = i, Name = u.Name, Kind = c.Type == 2 ? UnitKind.Brigade : UnitKind.Battalion, MainVehicle = main,
                Role = vct.Count > 0 ? db.RoleOf(vct[0]) : VehicleRole.Other, Bloc = Producer.OfName(main),
                Vehicles = string.Join(", ", parts.Select(p => $"{p.N}× {p.Name}")), VehicleCount = tot, AirRange = Math.Max(0, u.RngAir), InFile = used.GetValueOrDefault(i),
            });
        }
        return res;
    }

    private Action Done(UniFile.Snapshot sn)
    {
        _d.Reindex(); _d.Dirty = true;
        var uf = Uf;
        return () => { uf.RestoreSnapshot(sn); _d.Reindex(); _d.Dirty = true; };
    }

    private short NextCampId() => (short)(Uf.Units.Select(u => (int)u.CampId).DefaultIfEmpty(0).Max() + 1);

    /// <summary>Nuova brigata vuota sull'obiettivo (poi le si aggiungono i battaglioni).</summary>
    public (CampUnit? Unit, Action? Undo, string? Error) AddBrigade(int ct, byte team, CampObjective obj, short division, byte orders = 0)
    {
        var uf = Uf;
        var template = uf.Units.FirstOrDefault(u => u.Kind == UnitKind.Brigade && u.Owner == team) ?? uf.Units.FirstOrDefault(u => u.Kind == UnitKind.Brigade);
        if (template == null) return (null, null, "This file has no brigade to copy the record from.");
        if (uf.Version < 100) return (null, null, $"File version {uf.Version}: only BMS 4.3x files (version 100 or more) can get new units.");
        var sn = uf.TakeSnapshot();
        var id = uf.FreeId(_d.Objectives.Select(x => x.Id));
        short nameId = (short)(uf.Units.Where(u => u.Kind == UnitKind.Brigade && u.Owner == team).Select(u => (int)u.NameId).DefaultIfEmpty(0).Max() + 1);
        var nu = uf.AddBrigade(template, ct + 100, id, obj.X, obj.Y, team, obj.Id, orders, division, nameId, NextCampId());
        uf.SetUnitFlags(nu, FlagsBrigade);
        uf.SetCargo(nu, default);
        return (nu, Done(sn), null);
    }

    /// <summary>Nuovo battaglione: in una brigata (va sul suo obiettivo, stessa divisione e ordini) o indipendente sull'obiettivo.</summary>
    public (CampUnit? Unit, Action? Undo, string? Error) AddBattalion(int ct, byte team, CampObjective? obj, CampUnit? brigade, byte orders = 0)
    {
        var uf = Uf;
        var bats = uf.Units.Where(u => u.Kind == UnitKind.Battalion).ToList();
        var template = bats.FirstOrDefault(u => u.Owner == team) ?? bats.FirstOrDefault();
        if (template == null) return (null, null, "This file has no battalion to copy the record from.");
        if (uf.Version < 100) return (null, null, $"File version {uf.Version}: only BMS 4.3x files (version 100 or more) can get new units.");
        bool support = brigade != null && _db.Ct.TryGetValue(ct, out var cc) && (cc.SubType == 1 || cc.SubType == 13);
        if (brigade != null && !support && brigade.Elements.Count >= MaxElements) return (null, null, $"The brigade already has {MaxElements} battalions (Mission Commander's limit; Korea brigades have 2 to 4).");
        var at = brigade != null ? _d.Obj(brigade.AssignedObj) ?? obj : obj;
        if (at == null) return (null, null, "Choose an objective (or a brigade that has one).");
        var sn = uf.TakeSnapshot();
        var id = uf.FreeId(_d.Objectives.Select(x => x.Id));
        int x = brigade?.X ?? at.X, y = brigade?.Y ?? at.Y;
        short nameId = brigade != null
            ? (short)Math.Clamp(brigade.NameId * 10 + brigade.Elements.Count + 1, 1, short.MaxValue)
            : (short)(bats.Where(u => u.Owner == team).Select(u => (int)u.NameId).DefaultIfEmpty(0).Max() + 1);
        byte ord = brigade?.Orders ?? orders;
        var nu = uf.AddBattalion(template, ct + 100, id, x, y, team, at.Id, ord, _db.FullRoster(ct), nameId, NextCampId());
        uf.SetCargo(nu, default);
        uf.SetDest(nu, x + 1, y + 1);
        uf.SetState(nu, 100, 0, 100);
        uf.SetUnitFlags(nu, brigade != null && !support ? FlagsBnInBrigade : IsAirDefense(nu) || IsSupply(nu) ? FlagsBnAirDefense : FlagsBnIndependent);
        if (support)
        {
            // antiaerea / rifornimenti chiesti "in" una brigata: appoggio (cargo), indipendente, come BMS 4.37+
            uf.SetCargo(nu, brigade!.Id);
            uf.SetDivision(nu, brigade.Division);
            uf.SetNameId(nu, (short)(bats.Where(u => u.Owner == team).Select(u => (int)u.NameId).DefaultIfEmpty(0).Max() + 1));
        }
        else if (brigade != null)
        {
            uf.SetDivision(nu, brigade.Division);
            uf.SetParent(nu, brigade.Id);
            uf.SetElements(brigade, brigade.Elements.Append(nu.Id).ToList());
        }
        return (nu, Done(sn), null);
    }

    /// <summary>Cambia il tipo di un'unita' (stesso genere: battaglione con battaglione, brigata con brigata), organico pieno.</summary>
    public (Action? Undo, string? Error) ChangeType(CampUnit u, int ct)
    {
        if (!_db.Ct.TryGetValue(ct, out var c)) return (null, "Unknown type.");
        var kind = c.Domain == 3 ? c.Type == 1 ? UnitKind.Battalion : c.Type == 2 ? UnitKind.Brigade : UnitKind.Unknown : UnitKind.Unknown;
        if (kind != u.Kind) return (null, $"A {u.Kind.ToString().ToLowerInvariant()} can only get a {u.Kind.ToString().ToLowerInvariant()} type.");
        var sn = Uf.TakeSnapshot();
        Uf.SetType(u, ct + 100, u.Kind == UnitKind.Battalion ? _db.FullRoster(ct) : u.Roster);
        return (Done(sn), null);
    }

    /// <summary>Battaglione in una brigata (o indipendente): segue l'obiettivo, la posizione e la divisione della brigata.</summary>
    public (Action? Undo, string? Error) SetBrigade(CampUnit bn, CampUnit? brigade)
    {
        if (bn.Kind != UnitKind.Battalion) return (null, "Only battalions belong to brigades.");
        if (brigade != null && brigade.Kind != UnitKind.Brigade) return (null, "Not a brigade.");
        if (brigade != null && (IsAirDefense(bn) || IsSupply(bn)))
            return (null, "Air defense and supply battalions are not battalions of a brigade (Korea, BMS 4.37+): they stay independent, at most as its support element.");
        if (brigade != null && !brigade.Elements.Contains(bn.Id) && brigade.Elements.Count >= MaxElements) return (null, $"The brigade already has {MaxElements} battalions.");
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        if (_d.Unit(bn.Parent) is { Kind: UnitKind.Brigade } old) uf.SetElements(old, old.Elements.Where(e => e != bn.Id).ToList());
        if (brigade != null)
        {
            uf.SetParent(bn, brigade.Id);
            uf.SetElements(brigade, brigade.Elements.Where(e => e != bn.Id).Append(bn.Id).ToList());
            uf.SetDivision(bn, brigade.Division);
            uf.SetAssignedObjective(bn, brigade.AssignedObj);
            uf.SetPosition(bn, brigade.X, brigade.Y);
            if (bn.Owner != brigade.Owner) uf.SetOwner(bn, brigade.Owner);
            uf.SetNameId(bn, (short)Math.Clamp(brigade.NameId * 10 + brigade.Elements.Count, 1, short.MaxValue));
        }
        else uf.SetParent(bn, default);
        return (Done(sn), null);
    }

    /// <summary>
    /// Mette l'unita' sull'obiettivo come "Place at Obj" di Mission Commander: posizione = obiettivo, destinazione = +1,+1,
    /// percorso tolto; per l'antiaerea Assigned + Ordered e non Moving. Una brigata si porta dietro i suoi battaglioni.
    /// </summary>
    public (Action? Undo, string? Error) PlaceAt(CampUnit u, CampObjective o, bool assign = true, bool withBattalions = true)
    {
        if (!u.IsGround) return (null, "Only battalions and brigades.");
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        void One(CampUnit x)
        {
            if (assign) uf.SetAssignedObjective(x, o.Id);
            uf.SetPosition(x, o.X, o.Y);
            uf.SetDest(x, o.X + 1, o.Y + 1);
            uf.ClearWaypoints(x);
            if (IsAirDefense(x)) uf.SetUnitFlags(x, (x.UnitFlags & ~FMoving) | FAssigned | FOrdered);
        }
        One(u);
        if (u.Kind == UnitKind.Brigade && withBattalions)
            foreach (var b in u.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>().ToList()) One(b);
        return (Done(sn), null);
    }

    /// <summary>Battaglioni della brigata: sulla sua posizione ("Batt > Bri location"), sul suo obiettivo, nella sua divisione.</summary>
    public (Action? Undo, string? Error) BattalionsFollow(CampUnit brigade, bool location, bool aobj, bool division)
    {
        if (brigade.Kind != UnitKind.Brigade) return (null, "Not a brigade.");
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        foreach (var b in brigade.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>().ToList())
        {
            if (location) { uf.SetPosition(b, brigade.X, brigade.Y); uf.SetDest(b, brigade.X + 1, brigade.Y + 1); uf.ClearWaypoints(b); }
            if (aobj) uf.SetAssignedObjective(b, brigade.AssignedObj);
            if (division) uf.SetDivision(b, brigade.Division);
        }
        return (Done(sn), null);
    }

    /// <summary>
    /// Tutti i battaglioni sulla posizione della loro brigata, come nei file della Korea a inizio campagna (Save0: 194 su 198) e come
    /// "Move all Battalions to there Brigade location" di Mission Commander: posizione, destinazione e obiettivo assegnato della brigata.
    /// support = anche i battaglioni di supporto legati alla brigata col cargo (difesa aerea, rifornimenti), come fa MC.
    /// team = solo una squadra (null = tutte). Restituisce quanti battaglioni sono stati spostati.
    /// </summary>
    public (Action? Undo, int Moved, int Brigades) AllBattalionsToBrigades(byte? team, bool support, bool aobj)
    {
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        int moved = 0, brigs = 0;
        var all = uf.Units.ToList();
        foreach (var br in all.Where(x => x.Kind == UnitKind.Brigade && (team == null || x.Owner == team)))
        {
            var list = br.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>().ToList();
            if (support) list.AddRange(all.Where(x => x.Kind == UnitKind.Battalion && x.Cargo == br.Id && !list.Contains(x)));
            bool any = false;
            foreach (var b in list)
            {
                bool off = b.X != br.X || b.Y != br.Y;
                bool objOff = aobj && b.AssignedObj != br.AssignedObj;
                if (!off && !objOff) continue;
                if (off) { uf.SetPosition(b, br.X, br.Y); uf.SetDest(b, br.X + 1, br.Y + 1); uf.ClearWaypoints(b); }
                if (objOff) uf.SetAssignedObjective(b, br.AssignedObj);
                moved++; any = true;
            }
            if (any) brigs++;
        }
        if (moved == 0) return (null, 0, 0);
        return (Done(sn), moved, brigs);
    }

    /// <summary>Valori della scheda del battaglione / della brigata, scritti tutti insieme (un solo annullamento).</summary>
    public sealed class Edit
    {
        public byte Owner, Orders, Supply, Fatigue, Morale;
        public short NameId, Division;
        public int Roster, Flags, X, Y;
        public VuId AssignedObj;
    }

    public (Action? Undo, string? Error) Apply(CampUnit u, Edit e)
    {
        if (!u.IsGround) return (null, "Only battalions and brigades.");
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        if (e.Owner != u.Owner) uf.SetOwner(u, e.Owner);
        uf.SetOrders(u, e.Orders);
        uf.SetNameId(u, e.NameId);
        uf.SetDivision(u, e.Division);
        uf.SetUnitFlags(u, e.Flags);
        if (e.AssignedObj != u.AssignedObj) { uf.SetAssignedObjective(u, e.AssignedObj); if (_d.Obj(e.AssignedObj) is { } o) uf.SetDest(u, o.X + 1, o.Y + 1); }
        if (e.X != u.X || e.Y != u.Y) uf.SetPosition(u, e.X, e.Y);
        if (u.Kind == UnitKind.Battalion) { uf.SetRoster(u, e.Roster); uf.SetState(u, e.Supply, e.Fatigue, e.Morale); }
        if (u.Kind == UnitKind.Brigade && e.Division != 0)
            foreach (var b in u.Elements.Select(x => _d.Unit(x)).OfType<CampUnit>()) if (b.Division != e.Division) uf.SetDivision(b, e.Division);
        return (Done(sn), null);
    }

    /// <summary>
    /// Battaglioni antiaerei e di rifornimento dentro le brigate (vecchie campagne, es. Mideast): come vuole BMS 4.37+ (Mission Commander)
    /// e come la Korea, escono dagli elementi della brigata; restano legati come appoggio (cargo = brigata) con No Plan.
    /// </summary>
    public (int Count, Action? Undo) FixSupportElements()
    {
        var uf = Uf;
        var brigades = uf.Units.Where(b => b.Kind == UnitKind.Brigade).ToList();
        var list = uf.Units.Where(b => b.Kind == UnitKind.Battalion && (IsAirDefense(b) || IsSupply(b)) &&
                                       (_d.Unit(b.Parent) is { Kind: UnitKind.Brigade } || brigades.Any(br => br.Elements.Contains(b.Id)))).ToList();
        if (list.Count == 0) return (0, null);
        var sn = uf.TakeSnapshot();
        foreach (var b in list)
        {
            var owner = _d.Unit(b.Parent) is { Kind: UnitKind.Brigade } p ? p : brigades.First(br => br.Elements.Contains(b.Id));
            foreach (var br in brigades.Where(br => br.Elements.Contains(b.Id)).ToList())
                uf.SetElements(br, br.Elements.Where(e => e != b.Id).ToList());
            uf.SetParent(b, default);
            uf.SetCargo(b, owner.Id);
            uf.SetUnitFlags(b, b.UnitFlags | FNoPlan);
        }
        return (list.Count, Done(sn));
    }

    /// <summary>Toglie un'unita': un battaglione esce anche dalla sua brigata; una brigata si porta via i suoi battaglioni.</summary>
    public (Action? Undo, string? Error) Delete(CampUnit u)
    {
        if (!u.IsGround) return (null, "Only battalions and brigades can be removed here.");
        var uf = Uf;
        var sn = uf.TakeSnapshot();
        if (u.Kind == UnitKind.Battalion && _d.Unit(u.Parent) is { Kind: UnitKind.Brigade } br)
            uf.SetElements(br, br.Elements.Where(e => e != u.Id).ToList());
        if (u.Kind == UnitKind.Brigade)
        {
            foreach (var bn in u.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>().ToList()) uf.Remove(bn);
            foreach (var sup in uf.Units.Where(x => x.Cargo == u.Id).ToList()) uf.SetCargo(sup, default);   // appoggi: restano, senza brigata
        }
        uf.Remove(u);
        return (Done(sn), null);
    }
}
