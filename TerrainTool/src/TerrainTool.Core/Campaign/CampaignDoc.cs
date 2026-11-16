using System.Text;
using TerrainTool.Core.Dds;

namespace TerrainTool.Core.Campaign;

/// <summary>Collegamento tra due obiettivi: 8 costi (uno per tipo di movimento) e l'obiettivo vicino.</summary>
public sealed class CampLink
{
    public VuId To;
    public byte[] Costs = new byte[8];
}

/// <summary>
/// Obiettivo della campagna (record del file .obj). Si tiene il record grezzo e si riscrivono solo i campi
/// modificabili (posizione, proprietari, flag, priorita', padre, collegamenti).
/// </summary>
public sealed class CampObjective
{
    public byte[] Raw = Array.Empty<byte>();
    public int Index;
    public int Type;                  // indice Falcon4_CT + 100
    public VuId Id;
    public int X, Y;                  // griglia km (x est, y nord)
    public byte Owner, FirstOwner;
    public short CampId;
    public uint Flags;
    public byte Priority;
    public short NameId;
    public VuId Parent;
    public List<CampLink> Links = new();
    public byte FeatureCount;
    public double SimNorth, SimEast;  // piedi
    public float HeadingRad;
    public string Name = "";
    public bool HasSim, HasName;
    internal int PrioAt, LinksAt, AfterLinks, SimAtInSuffix;

    public const uint FrontLine = 1, SecondLine = 2, ThirdLine = 4;
    public int Line => (Flags & FrontLine) != 0 ? 1 : (Flags & SecondLine) != 0 ? 2 : (Flags & ThirdLine) != 0 ? 3 : 0;

    internal static CampObjective Read(byte[] d, ref int p, int v)
    {
        int s = p;
        var o = new CampObjective { Type = BitConverter.ToInt16(d, p) };
        o.Id = VuId.Read(d, p + 2);
        o.X = BitConverter.ToInt16(d, p + 12); o.Y = BitConverter.ToInt16(d, p + 14);
        int q = p + 16;
        if (v >= 70) q += 4;
        q += 8;                                        // spotTime, spotted, baseFlags
        if (q - s != 28) throw new InvalidDataException("objective record layout not supported (version " + v + ")");
        o.Owner = d[q]; o.CampId = BitConverter.ToInt16(d, q + 1); q += 3;
        q += 4;                                        // lastRepair
        o.Flags = BitConverter.ToUInt32(d, q); q += 4;
        q += 3;                                        // supply, fuel, losses
        if (v < 100 && v >= 83) throw new InvalidDataException("objective file version " + v + " not supported");
        o.FeatureCount = d[q]; q += 1 + o.FeatureCount;
        o.PrioAt = q - s;
        o.Priority = d[q]; o.NameId = BitConverter.ToInt16(d, q + 1); o.Parent = VuId.Read(d, q + 3); o.FirstOwner = d[q + 11];
        q += 12;
        o.LinksAt = q - s;
        int nl = d[q]; q++;
        for (int i = 0; i < nl; i++)
        {
            var l = new CampLink();
            Array.Copy(d, q, l.Costs, 0, 8);
            l.To = VuId.Read(d, q + 8);
            o.Links.Add(l);
            q += 16;
        }
        o.AfterLinks = q - s;
        if (v >= 20) { int radar = d[q]; q += 1 + (radar != 0 ? 32 : 0); }
        if (v >= 103)
        {
            o.HasSim = true;
            o.SimAtInSuffix = q - s - o.AfterLinks;
            o.SimNorth = BitConverter.ToDouble(d, q); o.SimEast = BitConverter.ToDouble(d, q + 8);
            o.HeadingRad = BitConverter.ToSingle(d, q + 24);
            q += 28;
        }
        if (v >= 106)
        {
            o.HasName = true;
            int z = Array.IndexOf(d, (byte)0, q, 80);
            o.Name = Encoding.Latin1.GetString(d, q, (z < 0 ? q + 80 : z) - q).Trim();
            q += 80;
        }
        o.Raw = d.AsSpan(s, q - s).ToArray();
        p = q;
        return o;
    }

    internal byte[] Write()
    {
        var pre = Raw.AsSpan(0, LinksAt).ToArray();
        BitConverter.TryWriteBytes(pre.AsSpan(12, 2), (short)X);
        BitConverter.TryWriteBytes(pre.AsSpan(14, 2), (short)Y);
        pre[28] = Owner;
        BitConverter.TryWriteBytes(pre.AsSpan(35, 4), Flags);
        pre[PrioAt] = Priority;
        Parent.Write(pre, PrioAt + 3);
        pre[PrioAt + 11] = FirstOwner;
        var suf = Raw.AsSpan(AfterLinks).ToArray();
        if (HasSim)
        {
            BitConverter.TryWriteBytes(suf.AsSpan(SimAtInSuffix, 8), SimNorth);
            BitConverter.TryWriteBytes(suf.AsSpan(SimAtInSuffix + 8, 8), SimEast);
            BitConverter.TryWriteBytes(suf.AsSpan(SimAtInSuffix + 24, 4), HeadingRad);
        }
        var res = new byte[pre.Length + 1 + Links.Count * 16 + suf.Length];
        pre.CopyTo(res, 0);
        int q = pre.Length;
        res[q++] = (byte)Links.Count;
        foreach (var l in Links) { l.Costs.CopyTo(res, q); l.To.Write(res, q + 8); q += 16; }
        suf.CopyTo(res, q);
        return res;
    }

    public override string ToString() => Name.Length > 0 ? Name : $"Objective {CampId}";
}

/// <summary>Variazione di un obiettivo (.obd): proprietario e stato, usata dalle missioni TE senza .obj.</summary>
public sealed class ObjDelta
{
    public VuId Id;
    public uint LastRepair;
    public byte Owner, Supply, Fuel, Losses;
    public byte[] Status = Array.Empty<byte>();
}

public enum IssueLevel { Error, Warning, Info }

public sealed record CampIssue(IssueLevel Level, string Category, string Message, double X, double Y, VuId? Objective = null, VuId? Unit = null);

/// <summary>
/// Una campagna (.cam) o missione TE (.tac) aperta nell'editor: archivio, intestazione (.cmp), obiettivi (.obj
/// o, per le TE senza .obj, quelli dello scenario indicato nel .cmp), variazioni (.obd), unita' (.uni), squadre (.tea).
/// Formati studiati su Mission Commander (BMSUtils, con il permesso dell'autore, nessun codice copiato) e verificati sui file 4.38.
/// </summary>
public sealed class CampaignDoc
{
    public const double FeetPerKm = 3280.84;
    /// <summary>Lato di una cella della griglia della campagna in BMS (SimToGrid/GridToSim di BMS 4.38: 3279.98 ft, non 1 km esatto).</summary>
    public const double BmsFtPerGrid = 3279.98;
    /// <summary>Cella della griglia di BMS per una coordinata in piedi (SimToGrid).</summary>
    public static int SimToGrid(double ft) => (int)(ft / BmsFtPerGrid);
    /// <summary>Centro della cella in piedi (GridToSim).</summary>
    public static double GridToSim(int g) => g * BmsFtPerGrid + BmsFtPerGrid / 2;

    public string Path = "";
    public List<(string Name, byte[] Data)> Files = new();
    public int Version;
    public string TheaterName = "", Scenario = "", SaveFile = "";
    public int TheaterSizeX, TheaterSizeY;     // km
    public byte[]? CmpRaw;                     // intestazione espansa (contiene anche nome, bandiera e colore delle squadre)
    private bool _cmpDirty, _obdDirty;
    /// <summary>Il .cmp e' cambiato (lista squadroni...): va riscritto al salvataggio.</summary>
    public void CmpChanged() => _cmpDirty = true;
    private const int CmpTeamsAt = 128, CmpTeamSize = 222;
    public List<CampObjective> Objectives = new();
    public bool ObjectivesOwn;          // false = presi dallo scenario (sola lettura per posizione e collegamenti)
    public string? ObjectivesFrom;
    public List<ObjDelta> Deltas = new();
    public UniFile? Units;
    public TeamFile? Teams;
    public List<string> Log = new();
    public bool Dirty;
    public Func<int, (int Domain, int Class, int Type)?> ClassOf = _ => null;

    private Dictionary<VuId, CampObjective> _objById = new();
    private Dictionary<VuId, CampUnit> _unitById = new();

    public CampObjective? Obj(VuId id) => _objById.GetValueOrDefault(id);
    public CampUnit? Unit(VuId id) => _unitById.GetValueOrDefault(id);

    /// <summary>Lato del teatro in km scritto nel .cmp del file (0 se non si legge): per i teatri senza Theater.txt.</summary>
    public static int ReadTheaterSizeKm(string path)
    {
        try
        {
            var cmp = CampaignArchive.Read(path).FirstOrDefault(f => f.Name.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase)).Data;
            if (cmp == null || cmp.Length <= 8) return 0;
            int cs = BitConverter.ToInt32(cmp, 0), us = BitConverter.ToInt32(cmp, 4);
            var raw = CampaignArchive.Expand(cmp.AsSpan(8, Math.Min(cs, cmp.Length - 8)), us);
            int at = 128 + 8 * 222 + 16 + 14;
            return raw.Length >= at + 4 ? Math.Max(BitConverter.ToInt16(raw, at), BitConverter.ToInt16(raw, at + 2)) : 0;
        }
        catch (Exception) { return 0; }
    }

    public static CampaignDoc Load(string path, Func<int, (int Domain, int Class, int Type)?> classOf, IEnumerable<string>? scenarioDirs = null)
    {
        var doc = new CampaignDoc { Path = path, ClassOf = classOf, Files = CampaignArchive.Read(path) };
        byte[]? F(string ext) => doc.Files.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).Data;

        var ver = F(".ver");
        doc.Version = ver != null ? UniFile.ReadVersion(ver) : 0;
        if (doc.Version < 100) doc.Log.Add($"file version {doc.Version}: only BMS 4.3x files (version 100 or later) are fully supported");

        var cmp = F(".cmp");
        if (cmp != null && cmp.Length > 8)
        {
            int cs = BitConverter.ToInt32(cmp, 0), us = BitConverter.ToInt32(cmp, 4);
            var raw = CampaignArchive.Expand(cmp.AsSpan(8, Math.Min(cs, cmp.Length - 8)), us);
            if (raw.Length == us) doc.CmpRaw = raw;
            const int nameAt = 128 + 8 * 222 + 16 + 18 + 8 + 4;
            if (raw.Length >= nameAt + 120)
            {
                doc.TheaterName = CStr(raw, nameAt, 40);
                doc.Scenario = CStr(raw, nameAt + 40, 40);
                doc.SaveFile = CStr(raw, nameAt + 80, 40);
                doc.TheaterSizeX = BitConverter.ToInt16(raw, 128 + 8 * 222 + 16 + 14);
                doc.TheaterSizeY = BitConverter.ToInt16(raw, 128 + 8 * 222 + 16 + 16);
            }
        }

        var obj = F(".obj");
        if (obj != null) { doc.Objectives = ReadObjectives(obj, doc.Version); doc.ObjectivesOwn = true; }
        else if (doc.Scenario.Length > 0)
        {
            // lo scenario accanto al file, poi nelle altre cartelle (copia salvata fuori da BMS: lo scenario resta nella cartella Campaign originale)
            string sn = doc.Scenario + System.IO.Path.GetExtension(path);
            string sp = new[] { System.IO.Path.GetDirectoryName(path) ?? "" }.Concat(scenarioDirs ?? Enumerable.Empty<string>())
                .Select(d => System.IO.Path.Combine(d, sn)).FirstOrDefault(File.Exists) ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", sn);
            if (File.Exists(sp))
            {
                var sf = CampaignArchive.Read(sp);
                var so = sf.FirstOrDefault(f => f.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)).Data;
                var sv = sf.FirstOrDefault(f => f.Name.EndsWith(".ver", StringComparison.OrdinalIgnoreCase)).Data;
                if (so != null) { doc.Objectives = ReadObjectives(so, sv != null ? UniFile.ReadVersion(sv) : doc.Version); doc.ObjectivesFrom = sp; }
            }
            if (doc.ObjectivesFrom == null) doc.Log.Add($"objectives: this file has none and the scenario '{doc.Scenario}' was not found next to it");
            else doc.Log.Add($"objectives taken from {System.IO.Path.GetFileName(doc.ObjectivesFrom)} (positions and links are read-only here; owners are saved in this file)");
        }

        var obd = F(".obd");
        if (obd != null && obd.Length >= 10) doc.Deltas = ReadDeltas(obd, doc.Version);

        var uni = F(".uni");
        if (uni != null && uni.Length >= 10)
        {
            doc.Units = UniFile.Parse(uni, doc.Version, t => classOf(t - 100) is { } c ? (c.Domain, c.Type) : null);
            if (doc.Units.Error != null) doc.Log.Add("units: " + doc.Units.Error + " — units will not be saved");
        }
        var tea = F(".tea");
        if (tea != null)
        {
            doc.Teams = TeamFile.Parse(tea, doc.Version);
            if (doc.Teams.Error != null) doc.Log.Add(doc.Teams.Error + " — teams will not be saved");
        }
        // proprietari dalle variazioni (TE senza .obj)
        doc.Reindex();
        foreach (var dl in doc.Deltas) if (doc.Obj(dl.Id) is { } o) o.Owner = dl.Owner;
        return doc;
    }

    public void Reindex()
    {
        for (int i = 0; i < Objectives.Count; i++) Objectives[i].Index = i;
        _objById = Objectives.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        _unitById = Units?.Units.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First()) ?? new();
    }

    private static string CStr(byte[] b, int o, int len)
    {
        int z = Array.IndexOf(b, (byte)0, o, len);
        return Encoding.Latin1.GetString(b, o, (z < 0 ? o + len : z) - o).Trim();
    }

    public static List<CampObjective> ReadObjectives(byte[] file, int v)
    {
        int count = BitConverter.ToInt16(file, 0), size = BitConverter.ToInt32(file, 2);
        var d = CampaignArchive.Expand(file.AsSpan(10), size);
        var list = new List<CampObjective>(count);
        int p = 0;
        for (int i = 0; i < count; i++) list.Add(CampObjective.Read(d, ref p, v));
        if (p != d.Length) throw new InvalidDataException($"objective file: {p} of {d.Length} bytes read");
        return list;
    }

    private static List<ObjDelta> ReadDeltas(byte[] file, int v)
    {
        var res = new List<ObjDelta>();
        int count = BitConverter.ToInt16(file, 4), size = BitConverter.ToInt32(file, 6);
        if (size <= 0 || count <= 0) return res;
        var d = CampaignArchive.Expand(file.AsSpan(10), size);
        int o = 0;
        for (int i = 0; i < count && o < d.Length; i++)
        {
            var dl = new ObjDelta { Id = VuId.Read(d, o), LastRepair = BitConverter.ToUInt32(d, o + 8) };
            o += 12;
            dl.Owner = d[o]; dl.Supply = d[o + 1]; dl.Fuel = d[o + 2]; dl.Losses = d[o + 3]; o += 4;
            int n = d[o++];
            dl.Status = d.AsSpan(o, v < 64 ? 1 : n).ToArray(); o += dl.Status.Length;
            res.Add(dl);
        }
        return res;
    }

    private static byte[] WriteDeltas(List<ObjDelta> deltas)
    {
        var ms = new MemoryStream();
        foreach (var dl in deltas)
        {
            ms.Write(BitConverter.GetBytes(dl.Id.Num)); ms.Write(BitConverter.GetBytes(dl.Id.Creator));
            ms.Write(BitConverter.GetBytes(dl.LastRepair));
            ms.WriteByte(dl.Owner); ms.WriteByte(dl.Supply); ms.WriteByte(dl.Fuel); ms.WriteByte(dl.Losses);
            ms.WriteByte((byte)dl.Status.Length); ms.Write(dl.Status);
        }
        var raw = ms.ToArray();
        var comp = CampaignArchive.CompressLiteral(raw);
        var outp = new byte[10 + comp.Length];
        BitConverter.TryWriteBytes(outp.AsSpan(0, 4), comp.Length);
        BitConverter.TryWriteBytes(outp.AsSpan(4, 2), (short)deltas.Count);
        BitConverter.TryWriteBytes(outp.AsSpan(6, 4), raw.Length);
        comp.CopyTo(outp, 10);
        return outp;
    }

    // ---- modifiche ----

    public bool CanEditObjectives => ObjectivesOwn;

    public void MoveObjective(CampObjective o, double northFt, double eastFt)
    {
        if (!ObjectivesOwn) return;
        o.SimNorth = northFt; o.SimEast = eastFt;
        o.X = SimToGrid(eastFt); o.Y = SimToGrid(northFt);
        RecomputeCosts(o);
        Dirty = true;
    }

    public void SetOwner(CampObjective o, byte owner)
    {
        o.Owner = owner;
        var dl = Deltas.FirstOrDefault(d => d.Id == o.Id);
        if (dl != null) { dl.Owner = owner; _obdDirty = true; }
        else if (!ObjectivesOwn) { Deltas.Add(new ObjDelta { Id = o.Id, Owner = owner, Status = new byte[o.FeatureCount] }); _obdDirty = true; }
        Dirty = true;
    }

    /// <summary>Proprietario prima della guerra (in Korea: owner = chi lo tiene ora, first owner = di chi era all'inizio).</summary>
    public void SetFirstOwner(CampObjective o, byte owner)
    {
        if (!ObjectivesOwn) return;
        o.FirstOwner = owner; Dirty = true;
    }

    public static byte[] CostsFor(int x1, int y1, int x2, int y2)
    {
        // come Mission Commander: costi proporzionali alla distanza in km (BMS li ricalcola per il terreno)
        double dist = Math.Round(Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)));
        float[] k = { 0.87f, 1.58f, 4.32f, 2.94f, 1.06f, 1.06f, 1.19f, 0f };
        var c = new byte[8];
        for (int i = 0; i < 8; i++) c[i] = (byte)(i == 7 ? 255 : Math.Min(255, (int)Math.Round(dist * k[i])));
        return c;
    }

    public bool Linked(CampObjective a, CampObjective b) => a.Links.Any(l => l.To == b.Id);

    public bool AddLink(CampObjective a, CampObjective b)
    {
        if (!ObjectivesOwn || a == b) return false;
        bool any = false;
        if (!Linked(a, b) && a.Links.Count < 255) { a.Links.Add(new CampLink { To = b.Id, Costs = CostsFor(a.X, a.Y, b.X, b.Y) }); any = true; }
        if (!Linked(b, a) && b.Links.Count < 255) { b.Links.Add(new CampLink { To = a.Id, Costs = CostsFor(b.X, b.Y, a.X, a.Y) }); any = true; }
        Dirty |= any;
        return any;
    }

    public bool RemoveLink(CampObjective a, CampObjective b)
    {
        if (!ObjectivesOwn) return false;
        int n = a.Links.RemoveAll(l => l.To == b.Id) + b.Links.RemoveAll(l => l.To == a.Id);
        Dirty |= n > 0;
        return n > 0;
    }

    /// <summary>Dopo uno spostamento i costi dei collegamenti (in entrambe le direzioni) seguono la nuova distanza.</summary>
    private void RecomputeCosts(CampObjective o)
    {
        foreach (var l in o.Links)
        {
            if (Obj(l.To) is not { } b) continue;
            l.Costs = CostsFor(o.X, o.Y, b.X, b.Y);
            foreach (var bl in b.Links.Where(x => x.To == o.Id)) bl.Costs = CostsFor(b.X, b.Y, o.X, o.Y);
        }
    }

    /// <summary>
    /// Linee del fronte come le calcola BMS (verificato su Te_New_Nt, Save1, Save2, Save5 e Instant della Korea: coincidono al 100%):
    /// 1a linea = obiettivo collegato a un obiettivo di una squadra verso cui il proprietario e' ostile o in guerra (rapporto ≥ 4);
    /// 2a = collegato a una 1a linea; 3a = collegato a una 2a. Restituisce quanti obiettivi cambiano.
    /// </summary>
    public int RecomputeFrontLines()
    {
        if (!ObjectivesOwn || Teams == null) return 0;
        var line = ComputeFrontLines();
        int changed = 0;
        foreach (var o in Objectives)
        {
            int nl = line.GetValueOrDefault(o);
            if (o.Line == nl) continue;
            o.Flags &= ~(CampObjective.FrontLine | CampObjective.SecondLine | CampObjective.ThirdLine);
            if (nl == 1) o.Flags |= CampObjective.FrontLine; else if (nl == 2) o.Flags |= CampObjective.SecondLine; else if (nl == 3) o.Flags |= CampObjective.ThirdLine;
            changed++;
        }
        if (changed > 0) Dirty = true;
        return changed;
    }

    /// <summary>Linee del fronte calcolate (senza toccare i flag): obiettivo → 1, 2, 3.</summary>
    public Dictionary<CampObjective, int> ComputeFrontLines()
    {
        var line = new Dictionary<CampObjective, int>();
        if (Teams == null) return line;
        var T = Teams.Teams;
        bool Enemy(int a, int b) => a != b && a < T.Count && b < T.Count && T[a].Stance[b] >= 4;
        foreach (var o in Objectives)
            if (o.Links.Any(l => Obj(l.To) is { } b && Enemy(o.Owner, b.Owner))) line[o] = 1;
        for (int k = 2; k <= 3; k++)
            foreach (var o in Objectives)
                if (!line.ContainsKey(o) && o.Links.Any(l => Obj(l.To) is { } b && line.TryGetValue(b, out int lv) && lv == k - 1)) line[o] = k;
        return line;
    }

    public void SetLine(CampObjective o, int line)
    {
        if (!ObjectivesOwn) return;
        o.Flags &= ~(CampObjective.FrontLine | CampObjective.SecondLine | CampObjective.ThirdLine);
        if (line == 1) o.Flags |= CampObjective.FrontLine;
        else if (line == 2) o.Flags |= CampObjective.SecondLine;
        else if (line == 3) o.Flags |= CampObjective.ThirdLine;
        Dirty = true;
    }

    // ---- squadre: il nome, la bandiera e il colore stanno sia nel .tea sia nell'intestazione (.cmp) ----

    public void SetTeamName(CampTeam t, string name)
    {
        Teams?.SetName(t, name);
        if (CmpRaw != null && t.Index < 8)
        {
            int o = CmpTeamsAt + t.Index * CmpTeamSize + 2;
            Array.Clear(CmpRaw, o, 20);
            var nb = Encoding.Latin1.GetBytes(t.Name);
            Array.Copy(nb, 0, CmpRaw, o, Math.Min(19, nb.Length));
            _cmpDirty = true;
        }
        Dirty = true;
    }

    public void SetTeamFlag(CampTeam t, byte flag)
    {
        Teams?.SetFlag(t, flag);
        if (CmpRaw != null && t.Index < 8) { CmpRaw[CmpTeamsAt + t.Index * CmpTeamSize] = flag; _cmpDirty = true; }
        Dirty = true;
    }

    public void SetTeamColor(CampTeam t, byte color)
    {
        Teams?.SetColor(t, color);
        if (CmpRaw != null && t.Index < 8) { CmpRaw[CmpTeamsAt + t.Index * CmpTeamSize + 1] = color; _cmpDirty = true; }
        Dirty = true;
    }

    /// <param name="both">true = anche l'altra squadra prende lo stesso rapporto verso questa</param>
    public void SetStance(CampTeam t, int other, short value, bool both)
    {
        if (Teams == null) return;
        Teams.SetStance(t, other, value);
        if (both && other != t.Index && other < Teams.Teams.Count) Teams.SetStance(Teams.Teams[other], t.Index, value);
        Dirty = true;
    }

    public void SetSide(CampTeam t, byte side) { Teams?.SetCTeam(t, side); Dirty = true; }

    // ---- salvataggio ----

    public void Save(string dst)
    {
        var files = Files.ToList();
        void Put(string ext, byte[] data)
        {
            int i = files.FindIndex(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) files[i] = (files[i].Name, data);
        }
        if (ObjectivesOwn)
        {
            var ms = new MemoryStream();
            foreach (var o in Objectives) ms.Write(o.Write());
            var raw = ms.ToArray();
            // controllo: il file nuovo deve rileggersi uguale
            int p = 0; for (int i = 0; i < Objectives.Count; i++) CampObjective.Read(raw, ref p, Version);
            if (p != raw.Length) throw new InvalidDataException("objective file check failed");
            var c = CampaignArchive.CompressLiteral(raw);
            var nd = new byte[10 + c.Length];
            BitConverter.TryWriteBytes(nd.AsSpan(0, 2), (short)Objectives.Count);
            BitConverter.TryWriteBytes(nd.AsSpan(2, 4), raw.Length);
            BitConverter.TryWriteBytes(nd.AsSpan(6, 4), c.Length);
            c.CopyTo(nd, 10);
            Put(".obj", nd);
        }
        if (_obdDirty) Put(".obd", WriteDeltas(Deltas));
        if (Units != null && Units.Error == null && Units.Units.Count > 0) Put(".uni", Units.Build(Array.Empty<byte>()));
        if (Teams != null && Teams.Error == null) Put(".tea", Teams.Data);
        if (_cmpDirty && CmpRaw != null)
        {
            var c = CampaignArchive.CompressLiteral(CmpRaw);
            var nd = new byte[8 + c.Length];
            BitConverter.TryWriteBytes(nd.AsSpan(0, 4), c.Length);
            BitConverter.TryWriteBytes(nd.AsSpan(4, 4), CmpRaw.Length);
            c.CopyTo(nd, 8);
            Put(".cmp", nd);
        }
        // nome nuovo (Save As): BMS cerca i file interni con il nome del file (save0.cmp, save0.uni ...), quindi cambiano anche loro
        string ob = System.IO.Path.GetFileNameWithoutExtension(Path), nb = System.IO.Path.GetFileNameWithoutExtension(dst);
        if (ob.Length > 0 && !ob.Equals(nb, StringComparison.OrdinalIgnoreCase))
            for (int i = 0; i < files.Count; i++)
                if (files[i].Name.StartsWith(ob + ".", StringComparison.OrdinalIgnoreCase))
                    files[i] = (nb + files[i].Name[ob.Length..], files[i].Data);
        CampaignArchive.Write(dst, files);
        Files = files;
        Dirty = false; _cmpDirty = false; _obdDirty = false;
    }
}
