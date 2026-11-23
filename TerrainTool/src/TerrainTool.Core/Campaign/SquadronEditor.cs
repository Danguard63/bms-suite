namespace TerrainTool.Core.Campaign;

/// <summary>
/// Squadroni come in Mission Commander (fclsSquadron, fclsAddSquadron, CreateSquadron) e come sono nei file Korea:
/// magazzino = SSD del tipo, carburante = Fuel UCD x 24 x 4 x 119.993769 x 2 / 60, Camp Specific Rating 255, flag 0x22
/// (0x32 su portaerei o fuori mappa: No Plan), posizione = quella della base, voce nella lista squadroni del .cmp.
/// Ogni operazione restituisce l'annullamento (unita' + .cmp).
/// </summary>
public sealed class SquadronEditor
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    public SquadronEditor(CampaignDoc d, CampaignDb db) { _d = d; _db = db; }

    public static readonly string[] Specialties = { "General", "Air-To-Air", "Air-To-Ground", "ASUW", "SEAD", "CSAR" };
    public static readonly string[] RoleNames =
        { "None", "CAP", "TAC", "STRIKE", "CAS/Int", "Strat Bomb", "ECM", "SEAD", "ASW", "Anti-Ship", "Recon", "Transport", "AEW/ABCCC", "ELINT/JSTAR", "Tanker", "FAC" };
    public const int FlagsLand = 0x22, FlagsCarrier = 0x32;

    /// <summary>
    /// "Human Cont" (flag 0x80, in Mission Commander anche "Control: Set by HQ" spento): lo squadrone si puo' volare dai giocatori.
    /// Korea: solo l'F-15C U.S. di Gunsan ce l'ha (gli F-16 si volano sempre, sono l'aereo di BMS); Mideast: nessuno.
    /// </summary>
    public static bool IsHuman(CampUnit u) => (u.UnitFlags & UnitEditor.FHumanCont) != 0;

    /// <summary>Human Cont acceso/spento su piu' squadroni insieme (anche nella lista squadroni del .cmp).</summary>
    public (Action? Undo, int Count) SetHuman(IEnumerable<CampUnit> units, bool on)
    {
        var list = units.Where(u => u.Kind == UnitKind.Squadron && IsHuman(u) != on).ToList();
        if (list.Count == 0) return (null, 0);
        var sn = Snap();
        foreach (var u in list)
        {
            Uf.SetUnitFlags(u, on ? u.UnitFlags | UnitEditor.FHumanCont : u.UnitFlags & ~UnitEditor.FHumanCont);
            SquadInfo.Upsert(_d, _db, u);
        }
        return (Done(sn), list.Count);
    }

    private UniFile Uf => _d.Units ?? throw new InvalidOperationException("this file has no units");

    private (UniFile.Snapshot Units, byte[]? Cmp) Snap() => (Uf.TakeSnapshot(), _d.CmpRaw == null ? null : (byte[])_d.CmpRaw.Clone());

    private Action Done((UniFile.Snapshot Units, byte[]? Cmp) sn)
    {
        _d.Reindex(); _d.Dirty = true;
        var uf = Uf;
        return () => { uf.RestoreSnapshot(sn.Units); if (sn.Cmp != null) { _d.CmpRaw = (byte[])sn.Cmp.Clone(); _d.CmpChanged(); } _d.Reindex(); _d.Dirty = true; };
    }

    public int FuelFor(int ct) => _db.UcdOf(ct) is { } u ? (int)Math.Round(u.Fuel * 24 * 4 * 119.993769 * 2.0 / 60.0) : 0;

    /// <summary>Magazzino tipo (SSD) come array per indice WCD, lungo quanto quello dello squadrone.</summary>
    public byte[] StandardStores(int ct, int len)
    {
        var st = new byte[len];
        if (_db.StoresOf(ct) is { } s) foreach (var (w, n) in s) if (w >= 0 && w < len) st[w] = (byte)Math.Min(255, n);
        return st;
    }

    /// <summary>Tipi di squadrone del database (CT dominio aria, tipo 3), per scegliere l'aereo.</summary>
    public List<(int Ct, string Text)> SquadronTypes() =>
        _db.Ct.Where(kv => kv.Value.EntityType == 4 && kv.Value.Domain == 2 && kv.Value.Type == 3)
            .Select(kv => (kv.Key, Ac: _db.UnitVehicles(kv.Key).FirstOrDefault() ?? "", Role: _db.UcdName(kv.Key)))
            .Where(x => x.Ac.Length > 0 && !x.Ac.StartsWith("-"))
            .OrderBy(x => x.Ac).ThenBy(x => x.Role)
            .Select(x => (x.Key, $"{x.Ac} - {x.Role}")).ToList();

    /// <summary>Basi possibili: aeroporti, piste (anche autostradali), army base (elicotteri), e le task force (portaerei).</summary>
    public bool IsAirfield(CampObjective o) => _db.ObjectiveType(o.Type) is 1 or 2;
    public bool IsArmyBase(CampObjective o) => _db.ObjectiveType(o.Type) == 3;

    private void SetBase(CampUnit u, CampObjective? o, CampUnit? tf)
    {
        var uf = Uf;
        if (o != null)
        {
            int x = o.HasSim ? CampaignDoc.SimToGrid(o.SimEast) : o.X;
            int y = o.HasSim ? CampaignDoc.SimToGrid(o.SimNorth) : o.Y;
            uf.SetAirbase(u, o.Id); uf.SetPosition(u, x, y);
            uf.SetUnitFlags(u, u.UnitFlags & ~UnitEditor.FNoPlan);
        }
        else if (tf != null)
        {
            uf.SetAirbase(u, tf.Id); uf.SetPosition(u, tf.X, tf.Y);
            uf.SetUnitFlags(u, u.UnitFlags | UnitEditor.FNoPlan);
        }
        uf.SetDest(u, 0, 0);
    }

    public (CampUnit? Unit, Action? Undo, string? Error) Add(int ct, byte team, CampObjective? airbase, CampUnit? carrier, short nameId, ushort patch, byte specialty, bool human = false)
    {
        var uf = Uf;
        if (airbase == null && carrier == null) return (null, null, "Choose an airbase or a carrier.");
        if (uf.Version < 102) return (null, null, $"File version {uf.Version}: only BMS 4.3x files (version 102 or more) can get new squadrons.");
        var sqs = uf.Units.Where(u => u.Kind == UnitKind.Squadron).ToList();
        var template = sqs.FirstOrDefault(u => u.Owner == team) ?? sqs.FirstOrDefault();
        if (template == null) return (null, null, "This file has no squadron to copy the record from (pilots included).");
        if (SquadInfo.Locate(_d) < 0) return (null, null, "The squadron list of the campaign file (.cmp) was not found: the squadron would not appear in BMS.");
        var sn = Snap();
        var id = uf.FreeId(_d.Objectives.Select(x => x.Id));
        short campId = (short)(uf.Units.Select(u => (int)u.CampId).DefaultIfEmpty(0).Max() + 1);
        int flags = (carrier != null ? FlagsCarrier : FlagsLand) | (human ? UnitEditor.FHumanCont : 0);
        var nu = uf.AddSquadron(template, ct + 100, id, 0, 0, team, default, _db.FullRoster(ct), nameId, campId, FuelFor(ct),
                                StandardStores(ct, template.StoresLen), patch, flags);
        SetBase(nu, airbase, carrier);
        uf.SetSpecialty(nu, specialty);
        _d.Reindex();
        SquadInfo.Upsert(_d, _db, nu);
        return (nu, Done(sn), null);
    }

    /// <summary>Altro aereo: tipo, organico pieno, magazzino tipo e carburante del nuovo tipo (come "Change Aircraft Type").</summary>
    public (Action? Undo, string? Error) ChangeAircraft(CampUnit u, int ct)
    {
        var sn = Snap();
        var uf = Uf;
        uf.SetType(u, ct + 100, _db.FullRoster(ct));
        uf.SetStores(u, StandardStores(ct, u.StoresLen));
        uf.SetFuel(u, FuelFor(ct));
        foreach (var f in FlightsOf(u)) RetypeFlight(f, ct);       // i voli gia' pianificati volano il nuovo aereo, con il carico da rifare
        SquadInfo.Upsert(_d, _db, u);
        return (Done(sn), null);
    }

    public List<CampUnit> FlightsOf(CampUnit sq) => Uf.Units.Where(f => f.Kind == UnitKind.Flight && f.Squadron == sq.Id).ToList();

    /// <summary>Il volo prende il tipo dell'aereo dello squadrone; il carico torna vuoto (solo il cannone), perche' i piloni dell'altro aereo non valgono.</summary>
    private void RetypeFlight(CampUnit f, int squadronCt)
    {
        int fct = _db.FlightCtOf(squadronCt);
        if (fct < 0) return;
        Uf.SetType(f, fct + 100, f.Roster);
        var (gun, rounds) = _db.GunOf(squadronCt);
        Uf.ResetLoadout(f, gun, rounds);
    }

    /// <summary>Squadroni a cui si puo' passare il volo: stessa squadra e stessa base (la rotta parte e torna li').</summary>
    public List<CampUnit> FlightTargets(CampUnit f)
    {
        var cur = _d.Unit(f.Squadron);
        return Uf.Units.Where(s => s.Kind == UnitKind.Squadron && s.Owner == f.Owner && s.Id != f.Squadron && (cur == null || s.Airbase == cur.Airbase)).ToList();
    }

    /// <summary>"Move to squadron": il volo prende gli aerei da un altro squadrone della stessa base; se l'aereo cambia, il carico torna vuoto.</summary>
    public (Action? Undo, string? Error, bool LoadoutReset) MoveFlight(CampUnit f, CampUnit sq)
    {
        if (f.Kind != UnitKind.Flight || sq.Kind != UnitKind.Squadron) return (null, "Choose a flight and a squadron.", false);
        if (f.SquadronAt == 0) return (null, "This file format has no squadron in the flight record.", false);
        if (sq.Owner != f.Owner) return (null, "The squadron belongs to another team.", false);
        var cur = _d.Unit(f.Squadron);
        if (cur != null && cur.Airbase != sq.Airbase) return (null, "The squadron is at another base: the flight's route starts and ends at its own base.", false);
        var sn = Snap();
        Uf.SetFlightSquadron(f, sq.Id);
        bool reset = _db.FlightCtOf(sq.Type - 100) is int fct && fct >= 0 && fct + 100 != f.Type;
        if (reset) RetypeFlight(f, sq.Type - 100);
        return (Done(sn), null, reset);
    }

    /// <summary>Altra base o portaerei ("Move To Airbase"). I voli gia' pianificati dello squadrone non vengono spostati.</summary>
    public (Action? Undo, string? Error, int Flights) Move(CampUnit u, CampObjective? airbase, CampUnit? carrier)
    {
        if (airbase == null && carrier == null) return (null, "Choose an airbase or a carrier.", 0);
        int flights = Uf.Units.Count(f => f.Kind == UnitKind.Flight && f.Squadron == u.Id);
        var sn = Snap();
        SetBase(u, airbase, carrier);
        SquadInfo.Upsert(_d, _db, u);
        return (Done(sn), null, flights);
    }

    public sealed class Edit
    {
        public byte Owner, Specialty;
        public short NameId;
        public ushort Patch;
        public int Roster, Flags, Fuel;
        public int? Skin;
        public byte[] Stores = Array.Empty<byte>();
        public byte[] Csr = Array.Empty<byte>();
        public bool ResetPilots, ResetPilotStats, ClearSchedule;
    }

    public (Action? Undo, string? Error) Apply(CampUnit u, Edit e)
    {
        var sn = Snap();
        var uf = Uf;
        if (e.Owner != u.Owner) uf.SetOwner(u, e.Owner);
        uf.SetNameId(u, e.NameId);
        uf.SetSpecialty(u, e.Specialty);
        uf.SetPatch(u, e.Patch);
        uf.SetRoster(u, e.Roster);
        uf.SetUnitFlags(u, e.Flags);
        uf.SetFuel(u, e.Fuel);
        if (e.Skin is int sk) uf.SetTexSet(u, sk);
        if (e.Stores.Length > 0) uf.SetStores(u, e.Stores);
        if (e.Csr.Length == 16) uf.SetCampSpecificRating(u, e.Csr);
        if (e.ResetPilots || e.ResetPilotStats) uf.ResetPilots(u, e.ResetPilotStats);
        if (e.ClearSchedule) uf.ClearSchedule(u);
        SquadInfo.Upsert(_d, _db, u);
        return (Done(sn), null);
    }

    /// <summary>Toglie lo squadrone (anche dalla lista del .cmp); non se ha voli pianificati.</summary>
    public (Action? Undo, string? Error) Delete(CampUnit u)
    {
        var uf = Uf;
        int flights = uf.Units.Count(f => f.Kind == UnitKind.Flight && f.Squadron == u.Id);
        if (flights > 0) return (null, $"The squadron has {flights} planned flights: delete them first (or let BMS finish them).");
        var sn = Snap();
        SquadInfo.Remove(_d, u.Id);
        uf.Remove(u);
        return (Done(sn), null);
    }

    /// <summary>Problemi dello squadrone (per i colori dell'elenco).</summary>
    public List<(IssueLevel Level, string Text)> Problems(CampUnit u)
    {
        var res = new List<(IssueLevel, string)>();
        if (u.Kind != UnitKind.Squadron) return res;
        var ab = _d.Obj(u.Airbase);
        var tf = ab == null ? _d.Unit(u.Airbase) : null;
        if (ab == null && tf == null && !u.Airbase.IsNull) res.Add((IssueLevel.Error, "its airbase does not exist"));
        if (ab != null && ab.Owner != u.Owner && !CampaignValidator.Allied(_d, u.Owner, ab.Owner))
            res.Add((IssueLevel.Warning, $"airbase belongs to {CampaignValidator.TeamName(_d, ab.Owner)}, not an ally"));
        if (_db.StoresOf(u.Type - 100) is { } st)
        {
            var cur = Uf.Stores(u);
            int empty = st.Count(kv => kv.Key < cur.Length && cur[kv.Key] == 0 && kv.Value > 0);
            if (empty > 0 && empty == st.Count) res.Add((IssueLevel.Warning, "no stores (Resupply)"));
        }
        int at = SquadInfo.Locate(_d);
        if (at >= 0 && SquadInfo.Find(_d, at, u.Id) < 0) res.Add((IssueLevel.Warning, "not in the squadron list of the .cmp"));
        return res;
    }
}
