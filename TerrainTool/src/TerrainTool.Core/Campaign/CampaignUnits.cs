using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>Identificativo VU (num, creator) usato da obiettivi e unita' per riferirsi l'uno all'altro.</summary>
public readonly record struct VuId(uint Num, uint Creator)
{
    public bool IsNull => Num == 0 && Creator == 0;
    public override string ToString() => $"{Creator}:{Num}";
    public static VuId Read(byte[] b, int o) => new(BitConverter.ToUInt32(b, o), BitConverter.ToUInt32(b, o + 4));
    public void Write(byte[] b, int o) { BitConverter.TryWriteBytes(b.AsSpan(o, 4), Num); BitConverter.TryWriteBytes(b.AsSpan(o + 4, 4), Creator); }
}

public enum UnitKind { Unknown, Flight, Package, Squadron, Brigade, Battalion, TaskForce }

/// <summary>
/// Un'unita' del file .uni. Si tiene il record grezzo (Start..End nel buffer espanso) e la posizione dei campi
/// modificabili, cosi' in scrittura si cambiano solo quei byte e il resto resta identico.
/// </summary>
/// <summary>
/// Waypoint di un'unita' (formato studiato su Mission Commander): X/Y nella griglia delle unita' (km), Z = quota / 10 piedi,
/// Arrive in ms dall'inizio della campagna, Action (WP_*), Flags (WPF_TARGET = 1, WPF_IP = 2, ...), bersaglio.
/// </summary>
public readonly record struct CampWaypoint(short X, short Y, short Z, uint Arrive, byte Action, byte RouteAction, uint Flags, VuId Target)
{
    public int AltitudeFt => Math.Abs(Z) * 10;
    public bool IsTarget => (Flags & 1) != 0;
    public bool IsIp => (Flags & 2) != 0;

    public static readonly string[] ActionNames =
    {
        "Nothing", "Takeoff", "Assemble", "Post-assemble", "Refuel", "Rearm", "Pickup", "Land", "Timing", "CAS CP", "Escort", "CA", "CAP",
        "Intercept", "Ground strike", "Naval strike", "SAD", "Strike", "Bomb", "SEAD", "ELINT", "Recon", "Rescue", "ASW", "Tanker", "Airdrop", "Jamming",
    };
    public string ActionName => Action < ActionNames.Length ? ActionNames[Action] : "Action " + Action;

    public static string Time(uint ms)
    {
        uint min = ms / 60000;
        return $"day {min / 1440 + 1} {min % 1440 / 60:00}:{min % 60:00}";
    }
}

public sealed class CampUnit
{
    public int Start, End;            // record nel buffer espanso (Start = i16 tipo)
    public int Type;                  // indice Falcon4_CT + 100
    public UnitKind Kind;
    public VuId Id;
    public int X, Y;                  // griglia in km
    public byte Owner;
    public short CampId;
    public int UnitFlags;
    public short NameId;
    public int Roster;
    public ushort NumWaypoints;
    /// <summary>Percorso letto dal file (solo lettura: le modifiche passano dai byte grezzi).</summary>
    public List<CampWaypoint> Waypoints = new();
    /// <summary>Volo: tipo di missione (AMIS_*, come MisType di MissionData), callsign, orario sul bersaglio (ms dall'inizio della campagna).</summary>
    public byte Mission, CallsignId, CallsignNum;
    public uint TimeOnTarget;
    // terra
    public byte Orders;
    public short Division;
    public VuId AssignedObj;          // aobj: obiettivo assegnato
    public VuId Cargo;                // BMS 4.37+: battaglione antiaereo / rifornimenti -> brigata che appoggia ("support element")
    public VuId Parent;               // battaglione -> brigata
    public VuId LastObj;
    public byte Supply, Fatigue, Morale, Heading, Position;
    public List<VuId> Elements = new(); // brigata -> battaglioni, package -> voli
    // aria
    public VuId Airbase;              // squadrone
    public VuId Squadron, PackageId;  // volo
    public byte Specialty;

    // posizioni dei campi nel buffer (relative a Start) per le modifiche
    internal int ElemAt, SupplyAt, CargoAt;
    internal int XAt, OwnerAt, OrdersAt, DivisionAt, AobjAt, ParentAt, PositionAt, RosterAt, NameIdAt, WpCountAt, WpEnd;
    // squadrone: magazzino (conteggi per indice WCD); volo: carichi (16 id arma + 16 conteggi ciascuno)
    public int StoresAt, StoresLen, LoadoutAt, LoadoutCount, LoadoutIdSize;
    /// <summary>Volo: posizione (dall'inizio del record) dell'id dello squadrone.</summary>
    public int SquadronAt;
    /// <summary>Volo: posizione dell'orario sul bersaglio (TOT).</summary>
    public int TotAt;
    /// <summary>Posizione (dall'inizio del record) di ogni waypoint.</summary>
    public int[] WpAt = Array.Empty<int>();
    /// <summary>Squadrone: posizioni nel record (studiate su Mission Commander) e valori.</summary>
    public int FuelAt, CsrAt, PilotsAt, PilotCount, PilotSize, ScheduleAt, RatingAt, KillsAt, PatchAt, PatchSize, RetaskAt;
    public int Fuel;
    public ushort Patch;

    internal CampUnit Copy() { var c = (CampUnit)MemberwiseClone(); c.Elements = new List<VuId>(Elements); c.Waypoints = new List<CampWaypoint>(Waypoints); return c; }
    internal void CopyFrom(CampUnit c)
    {
        foreach (var f in typeof(CampUnit).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            f.SetValue(this, f.GetValue(c));
        Elements = new List<VuId>(c.Elements);
    }

    public bool IsGround => Kind is UnitKind.Brigade or UnitKind.Battalion;
    public bool IsAir => Kind is UnitKind.Flight or UnitKind.Package or UnitKind.Squadron;

    public static readonly string[] MissionNames =
    {
        "None", "BARCAP", "BARCAP", "HAVCAP", "TARCAP", "RESCORT", "Ambush CAP", "Sweep", "Alert", "Intercept", "Escort", "SEAD strike",
        "SEAD escort", "OCA strike", "Interdiction strike", "Strike", "Deep strike", "Strategic strike", "Strategic bomb", "FAC", "On-call CAS",
        "Pre-planned CAS", "CAS", "SAD", "Interdiction", "BAI", "AWACS", "JSTARS", "Tanker", "Recon", "BDA", "ECM", "Air cavalry", "Airlift",
        "SAR", "ASW", "Anti-ship", "Patrol", "Recon patrol", "Abort", "Training", "Relocate",
    };
    public string MissionName => Mission < MissionNames.Length ? MissionNames[Mission] : "Mission " + Mission;

    public static string OrdersName(int o) => o switch
    {
        0 => "Reserve", 1 => "Capture", 2 => "Secure", 3 => "Assault", 4 => "Airborne", 5 => "Commando",
        6 => "Defend", 7 => "Support", 8 => "Repair", 9 => "Air defense", 10 => "Recon", 11 => "Radar", _ => o.ToString()
    };
}

/// <summary>
/// Lettura e scrittura del file .uni (unita') di una campagna Falcon BMS.
/// Formato studiato su Mission Commander (BMSUtils, con il permesso dell'autore, nessun codice copiato) e verificato sui file 4.38.
/// Intestazione: i32 dimensione compressa, i16 numero unita', i32 dimensione espansa, poi LZSS.
/// Ogni record: i16 tipo (indice CT + 100), poi CampaignBase, Unit, e i campi della classe (volo, package,
/// squadrone, brigata, battaglione, task force) secondo Domain/Type della Falcon4_CT.
/// </summary>
public sealed class UniFile
{
    public int Version;
    public List<CampUnit> Units = new();
    public byte[] Data = Array.Empty<byte>();   // buffer espanso
    public string? Error;
    /// <summary>I voli hanno il campo CFT di 4 byte (della versione 109). BMS 4.38 salva la campagna in corso con .ver 108
    /// ma i voli gia' con il CFT: all'inizio della campagna non ci sono voli, per questo save0 si leggeva comunque.</summary>
    public bool FlightCft;

    /// <summary>classOf(tipo) restituisce (Domain, Type) dalla Falcon4_CT; null se sconosciuto.
    /// Se con la versione del .ver la lettura non torna, riprova con i voli nella forma della versione successiva.</summary>
    public static UniFile Parse(byte[] file, int version, Func<int, (int Domain, int Type)?> classOf)
    {
        var u = Parse(file, version, classOf, version >= 109);
        if (u.Error != null && version == 108 && u.Units.Any(x => x.Kind == UnitKind.Flight))
        {
            var t = Parse(file, version, classOf, true);
            if (t.Error == null) return t;
        }
        return u;
    }

    private static UniFile Parse(byte[] file, int version, Func<int, (int Domain, int Type)?> classOf, bool cft)
    {
        var u = new UniFile { Version = version, FlightCft = cft };
        int count = BitConverter.ToInt16(file, 4), size = BitConverter.ToInt32(file, 6);
        if (size <= 0) return u;
        u.Data = Dds.CampaignArchive.Expand(file.AsSpan(10), size);
        var b = u.Data; int o = 0;
        try
        {
            for (int n = 0; n < count && o + 2 <= b.Length; n++)
            {
                var cu = new CampUnit { Start = o, Type = BitConverter.ToInt16(b, o) };
                o += 2;
                var cls = classOf(cu.Type);
                if (cls == null) { u.Error = $"unit {n}: unknown class type {cu.Type} at byte {cu.Start}"; break; }
                var (dom, typ) = cls.Value;
                cu.Kind = dom switch
                {
                    2 => typ switch { 1 => UnitKind.Flight, 2 => UnitKind.Package, 3 => UnitKind.Squadron, _ => UnitKind.Unknown },
                    3 => typ switch { 1 => UnitKind.Battalion, 2 => UnitKind.Brigade, _ => UnitKind.Unknown },
                    4 => typ == 1 ? UnitKind.TaskForce : UnitKind.Unknown,
                    _ => UnitKind.Unknown
                };
                if (cu.Kind == UnitKind.Unknown) { u.Error = $"unit {n}: class {dom}/{typ} (type {cu.Type}) is not a unit"; break; }
                ReadUnit(b, ref o, version, cu, cft);
                cu.End = o;
                u.Units.Add(cu);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            u.Error = $"unit {u.Units.Count}: record runs past the end of the file ({ex.Message})";
        }
        if (u.Error == null && u.Units.Count != count) u.Error = $"read {u.Units.Count} of {count} units";
        return u;
    }

    private static void ReadUnit(byte[] b, ref int o, int v, CampUnit u, bool cft)
    {
        int s = u.Start;
        // CampaignBase
        u.Id = VuId.Read(b, o); o += 8;
        o += 2;                                   // entityType
        u.XAt = o - s; u.X = BitConverter.ToInt16(b, o); u.Y = BitConverter.ToInt16(b, o + 2); o += 4;
        if (v >= 70) o += 4;                      // z
        o += 4 + 2 + 2;                           // spotTime, spotted, baseFlags
        u.OwnerAt = o - s; u.Owner = b[o]; o += 1;
        u.CampId = BitConverter.ToInt16(b, o); o += 2;
        // Unit
        o += 4;                                   // last_check
        u.RosterAt = o - s; u.Roster = BitConverter.ToInt32(b, o); o += 4;
        u.UnitFlags = BitConverter.ToInt32(b, o); o += 4;
        o += 4;                                   // dest_x, dest_y
        o += 8;                                   // target_id
        if (v > 1) { u.CargoAt = o - s; u.Cargo = VuId.Read(b, o); o += 8; }   // cargo_id
        o += 3;                                   // moved, losses, tactic
        if (v < 100 && v >= 83) o += 4;
        o += v >= 71 ? 2 : 1;                     // current_wp
        u.NameIdAt = o - s; u.NameId = BitConverter.ToInt16(b, o); o += 2;
        o += 2;                                   // reinforcement
        u.WpCountAt = o - s;
        if (v >= 71) { u.NumWaypoints = BitConverter.ToUInt16(b, o); o += 2; } else { u.NumWaypoints = b[o]; o += 1; }
        u.Waypoints = new List<CampWaypoint>(u.NumWaypoints);
        u.WpAt = new int[u.NumWaypoints];
        for (int i = 0; i < u.NumWaypoints; i++) { u.WpAt[i] = o - s; u.Waypoints.Add(ReadWaypoint(b, ref o, v)); }
        u.WpEnd = o - s;

        switch (u.Kind)
        {
            case UnitKind.Battalion:
            case UnitKind.Brigade:
                u.OrdersAt = o - s; u.Orders = b[o]; o += 1;
                u.DivisionAt = o - s; u.Division = BitConverter.ToInt16(b, o); o += 2;
                u.AobjAt = o - s; u.AssignedObj = VuId.Read(b, o); o += 8;
                if (u.Kind == UnitKind.Battalion)
                {
                    o += 8;                       // last_move, last_combat
                    u.ParentAt = o - s; u.Parent = VuId.Read(b, o); o += 8;
                    u.LastObj = VuId.Read(b, o); o += 8;
                    u.SupplyAt = o - s; u.Supply = b[o]; u.Fatigue = b[o + 1]; u.Morale = b[o + 2]; u.Heading = b[o + 3]; o += 5; // + final_heading
                    if (v < 15) o += 1;
                    u.PositionAt = o - s; u.Position = b[o]; o += 1;
                    if (v < 100 && v >= 83 && u.Position > 0) o += 1;
                }
                else
                {
                    u.ElemAt = o - s;
                    int n = b[o]; o += 1;
                    for (int i = 0; i < n; i++) { u.Elements.Add(VuId.Read(b, o)); o += 8; }
                }
                break;
            case UnitKind.TaskForce:
                u.OrdersAt = o - s; u.Orders = b[o]; o += 2;   // orders, supply
                if (v < 100 && v >= 83) o += 9;
                break;
            case UnitKind.Squadron: ReadSquadron(b, ref o, v, u); break;
            case UnitKind.Flight: ReadFlight(b, ref o, v, u, cft); break;
            case UnitKind.Package: ReadPackage(b, ref o, v, u); break;
        }
    }

    private static CampWaypoint ReadWaypoint(byte[] b, ref int o, int v)
    {
        byte haves = b[o];
        var w = new CampWaypoint(BitConverter.ToInt16(b, o + 1), BitConverter.ToInt16(b, o + 3), BitConverter.ToInt16(b, o + 5),
            BitConverter.ToUInt32(b, o + 7), b[o + 11], b[o + 12], v < 73 ? BitConverter.ToUInt16(b, o + 14) : BitConverter.ToUInt32(b, o + 14),
            (haves & 2) != 0 ? VuId.Read(b, o + 14 + (v < 73 ? 2 : 4)) : default);
        SkipWaypoint(b, ref o, v);
        return w;
    }

    private static void SkipWaypoint(byte[] b, ref int o, int v)
    {
        byte haves = b[o]; o += 1;
        o += 6 + 4 + 3;                           // grid x/y/z, arrive, action, route action, formation
        o += v < 73 ? 2 : 4;                      // flags
        if ((haves & 2) != 0) { o += 9; if (v > 103) o += 4 * 9; }
        if ((haves & 1) != 0) o += 4;             // depart
        if (v < 100 && v >= 86) o += 4;
    }

    private static void ReadSquadron(byte[] b, ref int o, int v, CampUnit u)
    {
        int s = u.Start;
        u.FuelAt = o - s; u.Fuel = BitConverter.ToInt32(b, o); o += 4;   // fuel
        u.Specialty = b[o]; o += 1;
        if (v >= 101) { u.CsrAt = o - s; o += 16; }                       // campSpecificRating
        int st = v < 69 ? 200 : v == 73 ? 600 : v == 74 || v >= 100 ? 1000 : v >= 83 ? 600 : 220;
        u.StoresAt = o - u.Start; u.StoresLen = st;
        o += st;
        u.PilotsAt = o - s; u.PilotCount = v < 47 && v < 29 ? 36 : 48; u.PilotSize = v < 47 ? 8 : 10;
        o += u.PilotCount * u.PilotSize;                                  // piloti
        u.ScheduleAt = o - s; o += 16 * 4;                                // schedule
        u.Airbase = VuId.Read(b, o); o += 8;
        o += 8;                                   // hot_spot
        if (v >= 6 && v < 16) o += 8;
        u.RatingAt = o - s; o += 16;              // rating
        u.KillsAt = o - s; o += 6 * 2 + 1;        // kills aa/ag/as/an, missions, score, total_losses
        if (v >= 9) o += 1;                       // pilot_losses
        u.PatchAt = o - s;
        if (v >= 100) { u.Patch = BitConverter.ToUInt16(b, o); u.PatchSize = 2; o += 2; } else if (v >= 45) { u.Patch = b[o]; u.PatchSize = 1; o += 1; }   // patch
        // il nome a 80 caratteri (v >= 101) MC lo legge solo con "Export": nei file normali non c'e'
        if (v < 100 && v >= 83) o += 3;
        if (v >= 102) { u.RetaskAt = o - s; o += 5; }   // squadronRetaskAt, veh_relocate
        if (v >= 105) o += 4;                            // texSet
    }

    private static void ReadFlight(byte[] b, ref int o, int v, CampUnit u, bool cft)
    {
        o += 4 + 4;                               // z, fuel_burnt
        if (v == 74 || v >= 100) o += 16 + 8;     // fuel_initial, laserCode
        u.TotAt = o + 8 - u.Start;
        u.TimeOnTarget = BitConverter.ToUInt32(b, o + 8);
        o += 4 * 4 + 2;                           // last_move, last_combat, tot, mission_over_time, mission_target
        if (v < 100 && v >= 83) o += 6;
        if (v < 24)
        {
            int use = 0;
            if (v >= 8) { use = b[o]; o += 1; if (use != 0) o += 5 * 32; }
            o += v < 18 ? 32 : 16;
            o += 16;
        }
        else
        {
            int n = b[o]; o += 1;
            u.LoadoutAt = o - u.Start; u.LoadoutCount = n; u.LoadoutIdSize = v >= 73 ? 2 : 1;
            o += n * ((v >= 73 ? 32 : 16) + 16);
        }
        u.Mission = b[o]; o += 1;                 // mission
        if (v > 65) o += 1;
        o += 3;                                   // last_direction, priority, mission_id
        if (v < 14) o += 1;
        o += 1;                                   // eval_flags
        if (v > 65) o += 1;
        u.PackageId = VuId.Read(b, o); o += 8;
        u.SquadronAt = o - u.Start;
        u.Squadron = VuId.Read(b, o); o += 8;
        if (v > 65) o += 8;                       // requester
        u.CallsignId = b[o + 17]; u.CallsignNum = b[o + 18];
        o += 16 + 3 + 4;                          // slots, pilots, plane_stats, player_slots, last slot, callsign, refuel
        if (v >= 105) o += 16;
        if (v >= 108) o += 8;
        if (cft) o += 4;                          // CFT
        if (v < 100 && v >= 83) o += 244;
    }

    private static void ReadPackage(byte[] b, ref int o, int v, CampUnit u)
    {
        int n = b[o]; o += 1;
        for (int i = 0; i < n; i++) { u.Elements.Add(VuId.Read(b, o)); o += 8; }
        o += 8;                                   // interceptor
        if (v >= 7) o += 32;                      // awacs, jstar, ecm, tanker
        int wait = b[o]; o += 1;
        if ((u.UnitFlags & 0x100000) != 0 && wait == 0)
        {
            o += 2; if (v < 35) o += 2; o += 2;
            o += 4 + 16;
            if (v >= 16) o += 4;
            if (v >= 35) o += 1;
            if (v >= 41) o += 2;
            return;
        }
        o += 1 + 2 + 16 + 4 + 4 + 4 + 2 + 2;      // flights .. requests
        if (v < 35) o += 2;
        o += 2;                                   // responses
        int ni = b[o]; o += 1; for (int i = 0; i < ni; i++) SkipWaypoint(b, ref o, v);
        int ne = b[o]; o += 1; for (int i = 0; i < ne; i++) SkipWaypoint(b, ref o, v);
        o += 32 + 2 + 2 + 4 + 4 + 4 + 2 * 5 + 6;  // mis_request
        if (v >= 35) o += 3 + 4 + 2 + 3;
    }

    // ---- scrittura ----

    public void SetPosition(CampUnit u, int x, int y)
    {
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.XAt, 2), (short)x);
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.XAt + 2, 2), (short)y);
        u.X = x; u.Y = y;
    }

    public void SetOwner(CampUnit u, byte owner) { Data[u.Start + u.OwnerAt] = owner; u.Owner = owner; }

    public void SetOrders(CampUnit u, byte orders)
    {
        if (u.OrdersAt == 0) return;
        Data[u.Start + u.OrdersAt] = orders; u.Orders = orders;
    }

    public void SetDivision(CampUnit u, short div)
    {
        if (!u.IsGround) return;
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.DivisionAt, 2), div); u.Division = div;
    }

    public void SetAssignedObjective(CampUnit u, VuId obj)
    {
        if (!u.IsGround) return;
        obj.Write(Data, u.Start + u.AobjAt); u.AssignedObj = obj;
    }

    /// <summary>Nuovo VU_ID libero (sopra tutti quelli di unita' e obiettivi), creatore come le altre unita'.</summary>
    public VuId FreeId(IEnumerable<VuId> others)
    {
        uint max = Units.Select(u => u.Id.Num).Concat(others.Select(o => o.Num)).DefaultIfEmpty(0u).Max();
        uint creator = Units.Count > 0 ? Units[0].Id.Creator : 0;
        return new VuId(max + 1, creator);
    }

    /// <summary>
    /// Nuovo battaglione copiato da un battaglione esistente (stessi flag e valori di partenza), con un altro tipo, posizione,
    /// squadra, obiettivo e ordini; niente percorso, niente brigata, organico pieno dal tipo di unita' (roster: 2 bit per elemento).
    /// </summary>
    public CampUnit AddBattalion(CampUnit template, int fileType, VuId id, int x, int y, byte owner, VuId aobj, byte orders,
                                 int roster, short nameId, short campId)
    {
        if (template.Kind != UnitKind.Battalion || Version < 100) throw new InvalidOperationException("needs a battalion of a BMS 4.3x file as template");
        var src = Data.AsSpan(template.Start, template.End - template.Start);
        var rec = new byte[template.WpCountAt + 2 + (src.Length - template.WpEnd)];
        src[..template.WpCountAt].CopyTo(rec);
        src[template.WpEnd..].CopyTo(rec.AsSpan(template.WpCountAt + 2));   // numero di tappe = 0
        int shift = template.WpEnd - (template.WpCountAt + 2);
        int Post(int at) => at - shift;
        BitConverter.TryWriteBytes(rec.AsSpan(0, 2), (short)fileType);
        id.Write(rec, 2);
        BitConverter.TryWriteBytes(rec.AsSpan(10, 2), (ushort)fileType);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt, 2), (short)x);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt + 2, 2), (short)y);
        BitConverter.TryWriteBytes(rec.AsSpan(template.OwnerAt - 4, 2), (short)0);   // spotted
        rec[template.OwnerAt] = owner;
        BitConverter.TryWriteBytes(rec.AsSpan(template.OwnerAt + 1, 2), campId);
        BitConverter.TryWriteBytes(rec.AsSpan(template.RosterAt, 4), roster);
        int p = template.RosterAt + 8;                                               // dopo roster e unit_flags
        BitConverter.TryWriteBytes(rec.AsSpan(p, 2), (short)x); BitConverter.TryWriteBytes(rec.AsSpan(p + 2, 2), (short)y);   // dest
        Array.Clear(rec, p + 4, 16);                                                 // target, cargo
        rec[p + 20] = 0; rec[p + 21] = 0;                                            // moved, losses
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt - 2, 2), (ushort)0); // current_wp
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt, 2), nameId);
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt + 2, 2), (short)0);  // reinforcement
        rec[Post(template.OrdersAt)] = orders;
        BitConverter.TryWriteBytes(rec.AsSpan(Post(template.DivisionAt), 2), (short)0);
        aobj.Write(rec, Post(template.AobjAt));
        new VuId(0, 0).Write(rec, Post(template.ParentAt));
        aobj.Write(rec, Post(template.ParentAt) + 8);                                // last_obj
        rec[Post(template.PositionAt)] = 0;
        return Append(rec, UnitKind.Battalion);
    }

    private CampUnit Append(byte[] rec, UnitKind kind)
    {
        int start = Data.Length;
        var nd = new byte[Data.Length + rec.Length];
        Data.CopyTo(nd, 0); rec.CopyTo(nd, start);
        Data = nd;
        var cu = new CampUnit { Start = start, Type = BitConverter.ToInt16(Data, start), Kind = kind };
        int o = start + 2;
        ReadUnit(Data, ref o, Version, cu, FlightCft);
        cu.End = o;
        if (o != Data.Length) throw new InvalidDataException("new unit record check failed");
        Units.Add(cu);
        return cu;
    }

    /// <summary>Rimette un'unita' tolta (annulla): il record torna in fondo al file.</summary>
    public void Restore(byte[] rec, CampUnit u)
    {
        var nu = Append(rec, u.Kind);
        // stessa istanza per chi la tiene in mano (undo, selezione)
        u.Start = nu.Start; u.End = nu.End;
        Units.Remove(nu); Units.Add(u);
    }

    /// <summary>Toglie un'unita' (solo quelle senza legami: battaglioni senza brigata, es. difese aeree).</summary>
    public void Remove(CampUnit u)
    {
        int len = u.End - u.Start;
        var nd = new byte[Data.Length - len];
        Data.AsSpan(0, u.Start).CopyTo(nd);
        Data.AsSpan(u.End).CopyTo(nd.AsSpan(u.Start));
        Data = nd;
        foreach (var x in Units) if (x.Start > u.Start) { x.Start -= len; x.End -= len; }
        Units.Remove(u);
    }

    // ---- modifiche con cambio di lunghezza del record e annullamento ----

    /// <summary>Copia di tutto il file unita' (buffer e campi di ogni unita'), per annullare un'operazione qualsiasi.</summary>
    public sealed class Snapshot
    {
        internal byte[] Data = Array.Empty<byte>();
        internal List<(CampUnit U, CampUnit Copy)> Units = new();
    }

    public Snapshot TakeSnapshot() => new() { Data = (byte[])Data.Clone(), Units = Units.Select(u => (u, u.Copy())).ToList() };

    /// <summary>Rimette tutto com'era; le istanze restano le stesse (selezione, altri annullamenti).</summary>
    public void RestoreSnapshot(Snapshot sn)
    {
        Data = (byte[])sn.Data.Clone();
        Units.Clear();
        foreach (var (u, c) in sn.Units) { u.CopyFrom(c); Units.Add(u); }
    }

    /// <summary>Sostituisce il record di un'unita' (anche di lunghezza diversa) e lo rilegge.</summary>
    private void ReplaceRecord(CampUnit u, byte[] rec)
    {
        int oldLen = u.End - u.Start, delta = rec.Length - oldLen;
        var nd = new byte[Data.Length + delta];
        Data.AsSpan(0, u.Start).CopyTo(nd);
        rec.CopyTo(nd, u.Start);
        Data.AsSpan(u.End).CopyTo(nd.AsSpan(u.Start + rec.Length));
        Data = nd;
        foreach (var x in Units) if (x != u && x.Start > u.Start) { x.Start += delta; x.End += delta; }
        Reread(u);
        if (u.End - u.Start != rec.Length) throw new InvalidDataException("unit record check failed");
    }

    private void Reread(CampUnit u)
    {
        u.Elements.Clear();
        u.Type = BitConverter.ToInt16(Data, u.Start);
        int o = u.Start + 2;
        ReadUnit(Data, ref o, Version, u, FlightCft);
        u.End = o;
    }

    /// <summary>Battaglioni di una brigata (lista degli elementi).</summary>
    public void SetElements(CampUnit brigade, IList<VuId> ids)
    {
        if (brigade.Kind != UnitKind.Brigade || brigade.ElemAt == 0) return;
        if (ids.Count > 255) throw new InvalidOperationException("too many battalions");
        var rec = new byte[brigade.ElemAt + 1 + 8 * ids.Count];
        Data.AsSpan(brigade.Start, brigade.ElemAt).CopyTo(rec);
        rec[brigade.ElemAt] = (byte)ids.Count;
        for (int i = 0; i < ids.Count; i++) ids[i].Write(rec, brigade.ElemAt + 1 + 8 * i);
        ReplaceRecord(brigade, rec);
    }

    /// <summary>Brigata del battaglione (VuId nullo = indipendente).</summary>
    public void SetParent(CampUnit bn, VuId parent)
    {
        if (bn.Kind != UnitKind.Battalion) return;
        parent.Write(Data, bn.Start + bn.ParentAt); bn.Parent = parent;
    }

    public void SetUnitFlags(CampUnit u, int flags) { BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.RosterAt + 4, 4), flags); u.UnitFlags = flags; }
    public void SetRoster(CampUnit u, int roster) { BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.RosterAt, 4), roster); u.Roster = roster; }
    public void SetDest(CampUnit u, int x, int y)
    {
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.RosterAt + 8, 2), (short)x);
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.RosterAt + 10, 2), (short)y);
    }
    public void SetCargo(CampUnit u, VuId id) { if (u.CargoAt == 0) return; id.Write(Data, u.Start + u.CargoAt); u.Cargo = id; }

    /// <summary>Rifornimenti, fatica, morale (solo battaglioni).</summary>
    public void SetState(CampUnit u, byte supply, byte fatigue, byte morale)
    {
        if (u.Kind != UnitKind.Battalion || u.SupplyAt == 0) return;
        Data[u.Start + u.SupplyAt] = supply; Data[u.Start + u.SupplyAt + 1] = fatigue; Data[u.Start + u.SupplyAt + 2] = morale;
        u.Supply = supply; u.Fatigue = fatigue; u.Morale = morale;
    }

    /// <summary>Toglie il percorso (tappe) dell'unita', come "Place at Obj" di Mission Commander.</summary>
    public void ClearWaypoints(CampUnit u)
    {
        if (u.NumWaypoints == 0) return;
        int cnt = Version >= 71 ? 2 : 1;
        var src = Data.AsSpan(u.Start, u.End - u.Start);
        var rec = new byte[u.WpCountAt + cnt + (src.Length - u.WpEnd)];
        src[..u.WpCountAt].CopyTo(rec);
        src[u.WpEnd..].CopyTo(rec.AsSpan(u.WpCountAt + cnt));
        if (Version >= 71) BitConverter.TryWriteBytes(rec.AsSpan(u.NameIdAt - 2, 2), (ushort)0);    // current_wp
        ReplaceRecord(u, rec);
    }

    public void SetNameId(CampUnit u, short id) { BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.NameIdAt, 2), id); u.NameId = id; }

    /// <summary>Cambia il tipo (indice CT + 100, anche nell'entityType) e l'organico.</summary>
    public void SetType(CampUnit u, int fileType, int roster)
    {
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start, 2), (short)fileType);
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + 10, 2), (ushort)fileType);
        BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.RosterAt, 4), roster);
        u.Type = fileType; u.Roster = roster;
    }

    /// <summary>Nuova brigata copiata da una brigata esistente: nessun percorso, nessun battaglione (si aggiungono dopo).</summary>
    public CampUnit AddBrigade(CampUnit template, int fileType, VuId id, int x, int y, byte owner, VuId aobj, byte orders,
                               short division, short nameId, short campId)
    {
        if (template.Kind != UnitKind.Brigade || Version < 100) throw new InvalidOperationException("needs a brigade of a BMS 4.3x file as template");
        var src = Data.AsSpan(template.Start, template.End - template.Start);
        int tail = template.ElemAt - template.WpEnd;                               // orders, division, aobj
        var rec = new byte[template.WpCountAt + 2 + tail + 1];
        src[..template.WpCountAt].CopyTo(rec);
        src.Slice(template.WpEnd, tail).CopyTo(rec.AsSpan(template.WpCountAt + 2));
        int shift = template.WpEnd - (template.WpCountAt + 2);
        int Post(int at) => at - shift;
        BitConverter.TryWriteBytes(rec.AsSpan(0, 2), (short)fileType);
        id.Write(rec, 2);
        BitConverter.TryWriteBytes(rec.AsSpan(10, 2), (ushort)fileType);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt, 2), (short)x);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt + 2, 2), (short)y);
        BitConverter.TryWriteBytes(rec.AsSpan(template.OwnerAt - 4, 2), (short)0);   // spotted
        rec[template.OwnerAt] = owner;
        BitConverter.TryWriteBytes(rec.AsSpan(template.OwnerAt + 1, 2), campId);
        BitConverter.TryWriteBytes(rec.AsSpan(template.RosterAt, 4), 0);
        int p = template.RosterAt + 8;
        BitConverter.TryWriteBytes(rec.AsSpan(p, 2), (short)x); BitConverter.TryWriteBytes(rec.AsSpan(p + 2, 2), (short)y);
        Array.Clear(rec, p + 4, 16);
        rec[p + 20] = 0; rec[p + 21] = 0;
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt - 2, 2), (ushort)0);
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt, 2), nameId);
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt + 2, 2), (short)0);
        rec[Post(template.OrdersAt)] = orders;
        BitConverter.TryWriteBytes(rec.AsSpan(Post(template.DivisionAt), 2), division);
        aobj.Write(rec, Post(template.AobjAt));
        rec[^1] = 0;                                                                 // nessun elemento
        return Append(rec, UnitKind.Brigade);
    }

    // ---- squadroni ----

    // ---- rotte ----

    /// <summary>Posizione del campo "depart" del waypoint i nel buffer, o -1 se il waypoint non ce l'ha (parte quando arriva).</summary>
    private int DepartAt(CampUnit u, int i)
    {
        if (i < 0 || i >= u.WpAt.Length) return -1;
        int o = u.Start + u.WpAt[i];
        byte haves = Data[o];
        if ((haves & 1) == 0) return -1;
        int p = o + 1 + 6 + 4 + 3 + (Version < 73 ? 2 : 4);
        if ((haves & 2) != 0) { p += 9; if (Version > 103) p += 4 * 9; }
        return p;
    }

    /// <summary>Orario di partenza dal waypoint (ms): il campo depart, o l'arrivo se non c'e'.</summary>
    public uint WaypointDepart(CampUnit u, int i)
    {
        int p = DepartAt(u, i);
        return p < 0 ? u.Waypoints[i].Arrive : BitConverter.ToUInt32(Data, p);
    }

    /// <summary>Waypoint i: posizione (griglia, km) e orari di arrivo e partenza.</summary>
    public void SetWaypoint(CampUnit u, int i, short x, short y, uint arrive, uint depart)
    {
        if (i < 0 || i >= u.WpAt.Length) return;
        int o = u.Start + u.WpAt[i];
        BitConverter.TryWriteBytes(Data.AsSpan(o + 1, 2), x);
        BitConverter.TryWriteBytes(Data.AsSpan(o + 3, 2), y);
        BitConverter.TryWriteBytes(Data.AsSpan(o + 7, 4), arrive);
        int p = DepartAt(u, i);
        if (p >= 0) BitConverter.TryWriteBytes(Data.AsSpan(p, 4), depart);
        u.Waypoints[i] = u.Waypoints[i] with { X = x, Y = y, Arrive = arrive };
    }

    /// <summary>Waypoint i: quota in piedi (nel file decine di piedi; il segno del valore di prima resta).</summary>
    public void SetWaypointAltitude(CampUnit u, int i, int feet)
    {
        if (i < 0 || i >= u.WpAt.Length) return;
        int o = u.Start + u.WpAt[i];
        short z = (short)Math.Clamp((int)Math.Round(feet / 10.0), 0, short.MaxValue);
        if (u.Waypoints[i].Z < 0) z = (short)-z;
        BitConverter.TryWriteBytes(Data.AsSpan(o + 5, 2), z);
        u.Waypoints[i] = u.Waypoints[i] with { Z = z };
    }

    public void SetTimeOnTarget(CampUnit f, uint tot)
    {
        if (f.Kind != UnitKind.Flight || f.TotAt == 0) return;
        BitConverter.TryWriteBytes(Data.AsSpan(f.Start + f.TotAt, 4), tot);
        f.TimeOnTarget = tot;
    }

    /// <summary>Volo: lo squadrone da cui prende gli aerei.</summary>
    public void SetFlightSquadron(CampUnit f, VuId sq)
    {
        if (f.Kind != UnitKind.Flight || f.SquadronAt == 0) return;
        sq.Write(Data, f.Start + f.SquadronAt);
        f.Squadron = sq;
    }

    /// <summary>
    /// Volo: carico vuoto (tutti i piloni a zero) tranne il cannone sul pilone 0, come lo mette BMS (arma e colpi del VCD dell'aereo).
    /// Ogni carico: 16 id arma (i16) + 16 quantita' (byte).
    /// </summary>
    public void ResetLoadout(CampUnit f, int gunWcd, int gunCount)
    {
        if (f.Kind != UnitKind.Flight || f.LoadoutAt == 0 || f.LoadoutIdSize != 2) return;
        int o = f.Start + f.LoadoutAt;
        for (int k = 0; k < f.LoadoutCount; k++, o += 48)
        {
            Array.Clear(Data, o, 48);
            if (gunWcd > 0 && gunCount > 0)
            {
                BitConverter.TryWriteBytes(Data.AsSpan(o, 2), (short)gunWcd);
                Data[o + 32] = (byte)Math.Min(255, gunCount);
            }
        }
    }

    /// <summary>Carico k del volo (0 .. LoadoutCount-1): id arma (WCD) e quantita' dei 16 piloni.</summary>
    public (short[] Ids, byte[] Counts) ReadLoadout(CampUnit f, int k)
    {
        var ids = new short[16]; var cnt = new byte[16];
        if (f.Kind != UnitKind.Flight || f.LoadoutAt == 0 || f.LoadoutIdSize != 2 || k < 0 || k >= f.LoadoutCount) return (ids, cnt);
        int o = f.Start + f.LoadoutAt + 48 * k;
        for (int i = 0; i < 16; i++) { ids[i] = BitConverter.ToInt16(Data, o + 2 * i); cnt[i] = Data[o + 32 + i]; }
        return (ids, cnt);
    }

    public void WriteLoadout(CampUnit f, int k, short[] ids, byte[] counts)
    {
        if (f.Kind != UnitKind.Flight || f.LoadoutAt == 0 || f.LoadoutIdSize != 2 || k < 0 || k >= f.LoadoutCount) return;
        int o = f.Start + f.LoadoutAt + 48 * k;
        for (int i = 0; i < 16; i++)
        {
            bool empty = ids[i] == 0 || counts[i] == 0;
            BitConverter.TryWriteBytes(Data.AsSpan(o + 2 * i, 2), empty ? (short)0 : ids[i]);
            Data[o + 32 + i] = empty ? (byte)0 : counts[i];
        }
    }

    public void SetFuel(CampUnit u, int fuel) { if (u.FuelAt == 0) return; BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.FuelAt, 4), fuel); u.Fuel = fuel; }
    public void SetSpecialty(CampUnit u, byte v) { if (u.FuelAt == 0) return; Data[u.Start + u.FuelAt + 4] = v; u.Specialty = v; }
    public void SetPatch(CampUnit u, ushort v)
    {
        if (u.PatchSize == 2) BitConverter.TryWriteBytes(Data.AsSpan(u.Start + u.PatchAt, 2), v);
        else if (u.PatchSize == 1) Data[u.Start + u.PatchAt] = (byte)Math.Min(255, (int)v);
        u.Patch = v;
    }
    public byte[] Stores(CampUnit u) => Data.AsSpan(u.Start + u.StoresAt, u.StoresLen).ToArray();
    public void SetStores(CampUnit u, byte[] st) => st.AsSpan(0, Math.Min(st.Length, u.StoresLen)).CopyTo(Data.AsSpan(u.Start + u.StoresAt));
    public byte[] CampSpecificRating(CampUnit u) => u.CsrAt == 0 ? Array.Empty<byte>() : Data.AsSpan(u.Start + u.CsrAt, 16).ToArray();
    public void SetCampSpecificRating(CampUnit u, byte[] v) { if (u.CsrAt != 0) v.AsSpan(0, 16).CopyTo(Data.AsSpan(u.Start + u.CsrAt)); }
    public byte[] Rating(CampUnit u) => Data.AsSpan(u.Start + u.RatingAt, 16).ToArray();
    public int[] Schedule(CampUnit u) => Enumerable.Range(0, 16).Select(i => BitConverter.ToInt32(Data, u.Start + u.ScheduleAt + 4 * i)).ToArray();
    public void ClearSchedule(CampUnit u) => Array.Clear(Data, u.Start + u.ScheduleAt, 64);
    public void SetAirbase(CampUnit u, VuId ab) { ab.Write(Data, u.Start + u.ScheduleAt + 64); u.Airbase = ab; }

    /// <summary>Statistiche dello squadrone: uccisioni aa/ag/as/an, missioni, punteggio, perdite, piloti persi.</summary>
    public (short AA, short AG, short AS, short AN, short Missions, short Score, byte Losses, byte PilotLosses) Stats(CampUnit u)
    {
        int o = u.Start + u.KillsAt;
        return (BitConverter.ToInt16(Data, o), BitConverter.ToInt16(Data, o + 2), BitConverter.ToInt16(Data, o + 4), BitConverter.ToInt16(Data, o + 6),
                BitConverter.ToInt16(Data, o + 8), BitConverter.ToInt16(Data, o + 10), Data[o + 12], Version >= 9 ? Data[o + 13] : (byte)0);
    }

    /// <summary>Pilota: id (nome), abilita' e valutazione, stato (0 disponibile), uccisioni aa/ag/as/an, missioni.</summary>
    public sealed record PilotRec(int Nr, short Id, byte Skill, byte Status, byte AA, byte AG, byte AS, byte AN, short Missions);

    public List<PilotRec> Pilots(CampUnit u)
    {
        var res = new List<PilotRec>();
        for (int i = 0; i < u.PilotCount; i++)
        {
            int o = u.Start + u.PilotsAt + i * u.PilotSize;
            res.Add(new PilotRec(i, BitConverter.ToInt16(Data, o), Data[o + 2], Data[o + 3], Data[o + 4], Data[o + 5], Data[o + 6], Data[o + 7],
                                 u.PilotSize >= 10 ? BitConverter.ToInt16(Data, o + 8) : (short)0));
        }
        return res;
    }

    /// <summary>Piloti di nuovo disponibili (stato 0); con reset anche uccisioni e missioni azzerate.</summary>
    public void ResetPilots(CampUnit u, bool stats)
    {
        for (int i = 0; i < u.PilotCount; i++)
        {
            int o = u.Start + u.PilotsAt + i * u.PilotSize;
            Data[o + 3] = 0;
            if (stats) Array.Clear(Data, o + 4, u.PilotSize - 4);
        }
    }

    /// <summary>
    /// Nuovo squadrone copiato da uno squadrone esistente (piloti compresi, come squadra), con tutto il resto come lo crea Mission Commander:
    /// carburante dal tipo, specialita' 0, Camp Specific Rating 255, magazzino tipo (SSD), programma, valutazioni e statistiche a zero.
    /// </summary>
    public CampUnit AddSquadron(CampUnit template, int fileType, VuId id, int x, int y, byte owner, VuId airbase, int roster, short nameId, short campId,
                                int fuel, byte[] stores, ushort patch, int flags)
    {
        if (template.Kind != UnitKind.Squadron || Version < 100) throw new InvalidOperationException("needs a squadron of a BMS 4.3x file as template");
        var src = Data.AsSpan(template.Start, template.End - template.Start);
        var rec = new byte[template.WpCountAt + 2 + (src.Length - template.WpEnd)];
        src[..template.WpCountAt].CopyTo(rec);
        src[template.WpEnd..].CopyTo(rec.AsSpan(template.WpCountAt + 2));
        int shift = template.WpEnd - (template.WpCountAt + 2);
        int Post(int at) => at - shift;
        BitConverter.TryWriteBytes(rec.AsSpan(0, 2), (short)fileType);
        id.Write(rec, 2);
        BitConverter.TryWriteBytes(rec.AsSpan(10, 2), (ushort)fileType);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt, 2), (short)x);
        BitConverter.TryWriteBytes(rec.AsSpan(template.XAt + 2, 2), (short)y);
        if (Version >= 70) Array.Clear(rec, template.XAt + 4, 4);                      // z
        Array.Clear(rec, template.OwnerAt - 8, 8);                                    // spotTime, spotted, baseFlags
        rec[template.OwnerAt] = owner;
        BitConverter.TryWriteBytes(rec.AsSpan(template.OwnerAt + 1, 2), campId);
        Array.Clear(rec, template.RosterAt - 4, 4);                                   // last_check
        BitConverter.TryWriteBytes(rec.AsSpan(template.RosterAt, 4), roster);
        BitConverter.TryWriteBytes(rec.AsSpan(template.RosterAt + 4, 4), flags);
        int p = template.RosterAt + 8;
        Array.Clear(rec, p, 4 + 16 + 3);                                              // dest, target, cargo, moved, losses, tactic
        if (Version >= 71) BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt - 2, 2), (ushort)0);
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt, 2), nameId);
        BitConverter.TryWriteBytes(rec.AsSpan(template.NameIdAt + 2, 2), (short)0);
        int f = Post(template.FuelAt);
        BitConverter.TryWriteBytes(rec.AsSpan(f, 4), fuel);
        rec[f + 4] = 0;                                                               // specialita'
        if (template.CsrAt != 0) Array.Fill(rec, (byte)255, Post(template.CsrAt), 16);
        Array.Clear(rec, Post(template.StoresAt), template.StoresLen);
        stores.AsSpan(0, Math.Min(stores.Length, template.StoresLen)).CopyTo(rec.AsSpan(Post(template.StoresAt)));
        for (int i = 0; i < template.PilotCount; i++)                                // stessi piloti, disponibili e senza storia
        {
            int o = Post(template.PilotsAt) + i * template.PilotSize;
            Array.Clear(rec, o + 3, template.PilotSize - 3);
        }
        int sc = Post(template.ScheduleAt);
        Array.Clear(rec, sc, 64);
        airbase.Write(rec, sc + 64);
        Array.Clear(rec, sc + 72, 8);                                                 // hot_spot
        Array.Clear(rec, Post(template.RatingAt), 16);
        Array.Clear(rec, Post(template.KillsAt), 13 + (Version >= 9 ? 1 : 0));
        int pa = Post(template.PatchAt);
        if (template.PatchSize == 2) BitConverter.TryWriteBytes(rec.AsSpan(pa, 2), patch); else if (template.PatchSize == 1) rec[pa] = (byte)Math.Min(255, (int)patch);
        if (template.RetaskAt != 0) Array.Clear(rec, Post(template.RetaskAt), 5);
        if (Version >= 105) Array.Clear(rec, rec.Length - 4, 4);                     // texSet
        return Append(rec, UnitKind.Squadron);
    }

    /// <summary>File .uni con l'LZSS di soli letterali (stesso schema del .obj).</summary>
    public byte[] Build(byte[] original)
    {
        var comp = Dds.CampaignArchive.CompressLiteral(Data);
        var outp = new byte[10 + comp.Length];
        BitConverter.TryWriteBytes(outp.AsSpan(0, 4), comp.Length);
        BitConverter.TryWriteBytes(outp.AsSpan(4, 2), (short)Units.Count);
        BitConverter.TryWriteBytes(outp.AsSpan(6, 4), Data.Length);
        comp.CopyTo(outp, 10);
        return outp;
    }

    /// <summary>Versione dal file .ver dell'archivio (numero all'inizio del testo).</summary>
    public static int ReadVersion(byte[] ver)
    {
        var s = Encoding.Latin1.GetString(ver).Trim();
        int n = 0, i = 0;
        while (i < s.Length && char.IsDigit(s[i])) n = n * 10 + (s[i++] - '0');
        return n;
    }
}
