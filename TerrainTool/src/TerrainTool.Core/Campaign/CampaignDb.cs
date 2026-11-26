using System.Globalization;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Database delle classi (Falcon4_CT, UCD, VCD) per l'editor di campagna: dominio/classe/tipo di ogni voce CT,
/// nomi dei tipi di unita' e dei loro mezzi. Ogni file si cerca nelle cartelle date, in ordine (prima l'add-on).
/// </summary>
public sealed partial class CampaignDb
{
    public sealed record CtEntry(int Domain, int Class, int Type, int SubType, int Specific, int EntityType, int EntityIdx, int Graphics = 0);
    /// <summary>Capacita' di un mezzo (VCD): forza contro fanteria / mezzi cingolati / aerei, raggio antiaereo, radar, armi.</summary>
    public sealed record VcdStats(int StrFoot, int StrTracked, int StrAir, int RngAir, int RadarIdx, double DetAir, bool HasWeapons, int MaxSpeed);
    public sealed record UcdEntry(string Name, int[] Vehicles, int MainRole = -1, int RngAir = 0, int StrAir = 0, int[]? Elements = null,
                                  int Fuel = 0, int StoresIdx = -1, int MoveSpeed = 0, int[]? RoleScores = null, int UnitIcon = 0,
                                  int RngLowAir = 0);

    public Dictionary<int, CtEntry> Ct { get; } = new();
    private readonly Dictionary<int, (string Name, int Vehicle)> _ucd = new();
    private readonly Dictionary<int, string> _vcd = new();
    public Dictionary<int, UcdEntry> Ucd { get; } = new();
    public IReadOnlyDictionary<int, string> Vcd => _vcd;
    public Dictionary<int, VcdStats> VcdInfo { get; } = new();
    /// <summary>Arma del pilone 0 del mezzo (per gli aerei il cannone) e quantita' (VCD WpnOrHpIdx_0 / WpnCount_0).</summary>
    public Dictionary<int, (int Wcd, int Count)> VcdGun { get; } = new();
    /// <summary>Aerei: lista armi (WLD) di ogni pilone 1-15 (VCD WpnOrHpIdx_n; 0 = pilone che non c'e').</summary>
    public Dictionary<int, int[]> VcdHardpoints { get; } = new();
    /// <summary>Pesi del mezzo in libbre: decollo massimo, a vuoto, carburante interno (VCD MaxWeight / EmptyWeight / FuelWeight).</summary>
    public Dictionary<int, (int Max, int Empty, int Fuel)> VcdWeights { get; } = new();
    /// <summary>Liste armi dei piloni (Falcon4_WLD.xml): nome e, per ogni arma ammessa, quante ce ne stanno sul pilone.</summary>
    public Dictionary<int, (string Name, List<(int Wcd, int Max)> Weapons)> Wld { get; } = new();
    /// <summary>Peso dell'arma in libbre (WCD Weight).</summary>
    public Dictionary<int, int> WcdWeight { get; } = new();
    public Dictionary<int, string> Wcd { get; } = new();
    /// <summary>Magazzino tipo degli squadroni (Falcon4_SSD.xml): indice SSD -> (indice WCD -> quantita').</summary>
    public Dictionary<int, Dictionary<int, int>> Ssd { get; } = new();

    /// <summary>Magazzino tipo dello squadrone di questo tipo (UCD SquadronStoresIdx), come lo crea Mission Commander.</summary>
    public Dictionary<int, int>? StoresOf(int ctIndex) => UcdOf(ctIndex) is { StoresIdx: >= 0 } u && Ssd.TryGetValue(u.StoresIdx, out var s) ? s : null;
    public string? CtPath { get; private set; }
    private List<string> _dirs = new();
    private readonly Dictionary<int, int> _objIcon = new();

    /// <summary>Icona dell'obiettivo (OCD ObjectiveIcon), quella che la lista squadroni del .cmp mette come "airbaseIcon".</summary>
    public int ObjectiveIcon(int ctIndex)
    {
        if (!Ct.TryGetValue(ctIndex, out var c)) return 0;
        if (_objIcon.TryGetValue(c.EntityIdx, out int v)) return v;
        v = 0;
        string n = $"OCD_{c.EntityIdx:00000}";
        foreach (var d in _dirs)
        {
            var f = Path.Combine(d, "ObjectiveRelatedData", n, n + ".XML");
            if (!File.Exists(f)) continue;
            var m = Regex.Match(File.ReadAllText(f), @"<ObjectiveIcon>(\d+)</ObjectiveIcon>");
            if (m.Success) { v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); break; }
        }
        _objIcon[c.EntityIdx] = v;
        return v;
    }

    /// <param name="compiledInFirstDir">
    /// Teatro installato: se la prima cartella che ha un database lo ha solo compilato (FALCON4.CT senza Falcon4_CT.xml,
    /// es. Korea TvT), si legge quello invece di passare alle tabelle XML di BMS, che hanno altri record.
    /// </param>
    public static CampaignDb Load(IEnumerable<string> dirs, bool compiledInFirstDir = false)
    {
        var db = new CampaignDb();
        var list = dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).ToList();
        db._dirs = list;
        if (compiledInFirstDir && list.FirstOrDefault(HasDatabase) is { } first && !HasXmlCt(first) && CompiledCt(first) is { } ct)
        {
            db.LoadCompiled(first, ct);
            return db;
        }
        string? Find(string f) => list.Select(d => Path.Combine(d, f)).FirstOrDefault(File.Exists);
        db.CtPath = Find("Falcon4_CT.xml");
        if (db.CtPath != null)
            foreach (var (n, b) in Records(File.ReadAllText(db.CtPath), "CT"))
                db.Ct[n] = new CtEntry(Int(b, "Domain"), Int(b, "Class"), Int(b, "Type"), Int(b, "SubType"), Int(b, "Specific"), Int(b, "EntityType"), Int(b, "EntityIdx"), Int(b, "GraphicsNormal"));
        if (Find("Falcon4_UCD.xml") is { } u)
            foreach (var (n, b) in Records(File.ReadAllText(u), "UCD"))
            {
                db._ucd[n] = (Str(b, "Name"), Int(b, "VehicleCtIdx_0"));
                db.Ucd[n] = new UcdEntry(Str(b, "Name"), Enumerable.Range(0, 16).Select(i => Int(b, "VehicleCtIdx_" + i)).ToArray(),
                    Int(b, "MainRole"), Int(b, "Rng_Air"), Int(b, "Str_Air"), Enumerable.Range(0, 16).Select(i => Math.Max(0, Int(b, "ElementCount_" + i))).ToArray(),
                    Int(b, "Fuel"), Int(b, "SquadronStoresIdx"), Int(b, "MoveSpeed"), Enumerable.Range(0, 16).Select(i => Math.Max(0, Int(b, "RoleScore_" + i))).ToArray(), Int(b, "UnitIcon"),
                    Int(b, "Rng_LowAir"));
            }
        if (Find("Falcon4_VCD.xml") is { } v)
            foreach (var (n, b) in Records(File.ReadAllText(v), "VCD"))
            {
                db._vcd[n] = Str(b, "Name");
                bool armed = Enumerable.Range(0, 16).Any(i => Int(b, "WpnOrHpIdx_" + i) > 0 && Int(b, "WpnCount_" + i) > 0);
                double.TryParse(Str(b, "Det_Air"), NumberStyles.Float, CultureInfo.InvariantCulture, out double det);
                db.VcdGun[n] = (Int(b, "WpnOrHpIdx_0"), Int(b, "WpnCount_0"));
                db.VcdHardpoints[n] = Enumerable.Range(0, 16).Select(i => i == 0 ? 0 : Math.Max(0, Int(b, "WpnOrHpIdx_" + i))).ToArray();
                db.VcdWeights[n] = (Int(b, "MaxWeight"), Int(b, "EmptyWeight"), Int(b, "FuelWeight"));
                db.VcdInfo[n] = new VcdStats(Int(b, "Str_Foot"), Int(b, "Str_Tracked"), Int(b, "Str_Air"), Int(b, "Rng_Air"), Int(b, "RadarIdx"), det, armed, Int(b, "MaxSpeed"));
            }
        if (Find("Falcon4_SSD.xml") is { } ssd)
            foreach (var (n, b) in Records(File.ReadAllText(ssd), "SSD"))
            {
                var st = new Dictionary<int, int>();
                foreach (Match m in Regex.Matches(b, @"<WpnStores_(\d+)>(\d+)</WpnStores_\d+>"))
                    st[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                db.Ssd[n] = st;
            }
        if (Find("Falcon4_WCD.xml") is { } w)
            foreach (var (n, b) in Records(File.ReadAllText(w), "WCD"))
            {
                db.Wcd[n] = Str(b, "Name");
                db.WcdWeight[n] = Int(b, "Weight");
            }
        if (Find("Falcon4_WLD.xml") is { } wl)
            foreach (var (n, b) in Records(File.ReadAllText(wl), "WLD"))
            {
                var wl2 = new List<(int, int)>();
                for (int i = 0; i < 64; i++)
                {
                    int wi = Int(b, "WpnIdx_" + i);
                    if (wi <= 0) { if (!b.Contains($"<WpnIdx_{i + 1}>")) break; continue; }
                    wl2.Add((wi, Math.Max(1, Int(b, "WpnCount_" + i))));
                }
                db.Wld[n] = (Str(b, "Name"), wl2);
            }
        return db;
    }

    /// <summary>Nomi dei mezzi (VCD) di un'unita' (indice CT), senza doppioni, nell'ordine degli elementi.</summary>
    public List<string> UnitVehicles(int ctIndex)
    {
        var res = new List<string>();
        if (!Ct.TryGetValue(ctIndex, out var c) || !Ucd.TryGetValue(c.EntityIdx, out var u)) return res;
        foreach (int v in u.Vehicles)
            if (v > 0 && Ct.TryGetValue(v, out var vc) && _vcd.TryGetValue(vc.EntityIdx, out var vn) && !res.Contains(vn)) res.Add(vn);
        return res;
    }

    /// <summary>Mezzi del database (voci CT con EntityType 5 = VCD) per nome normalizzato.</summary>
    public Dictionary<string, int> VehicleByName()
    {
        var d = new Dictionary<string, int>();
        foreach (var (i, c) in Ct.OrderBy(kv => kv.Key))
            if (c.EntityType == 5 && _vcd.TryGetValue(c.EntityIdx, out var n) && n.Length > 0) d.TryAdd(UnitTypeConverter.Norm(n), i);
        return d;
    }

    /// <summary>Ruolo di un mezzo (indice CT): per sostituire una difesa aerea solo con una difesa aerea, un carro con un carro...</summary>
    public VehicleRole RoleOf(int ctIndex)
    {
        if (!Ct.TryGetValue(ctIndex, out var c) || c.EntityType != 5) return VehicleRole.Other;
        VcdInfo.TryGetValue(c.EntityIdx, out var st);
        return VehicleRoles.Of(c, st, _vcd.GetValueOrDefault(c.EntityIdx, ""));
    }

    /// <summary>
    /// Cartella del modello 3D (Terrdata\objects\Models\&lt;parent&gt;) del mezzo: il primo dei database caricati che ce l'ha
    /// (l'add-on puo' avere modelli suoi, altrimenti quelli di BMS). Null se il mezzo non ha modello.
    /// </summary>
    public string? ModelDir(int vehicleCt)
    {
        if (!Ct.TryGetValue(vehicleCt, out var c) || c.Graphics <= 0) return null;
        foreach (var d in _dirs)
        {
            var f = Path.Combine(d, "Models", c.Graphics.ToString(CultureInfo.InvariantCulture));
            if (File.Exists(Path.Combine(f, "Parent.dat"))) return f;
        }
        return null;
    }

    /// <summary>Cartelle delle texture numerate dei modelli vecchi (KoreaObj_HiRes prima, poi KoreaObj) accanto a quella del modello.</summary>
    public static List<string> ModelTextureDirs(string modelDir)
    {
        var objects = Path.GetDirectoryName(Path.GetDirectoryName(modelDir)!)!;
        return new[] { "KoreaObj_HiRes", "KoreaObj" }.Select(n => Path.Combine(objects, n)).Where(Directory.Exists).ToList();
    }

    public string VehicleName(int ctIndex) => Ct.TryGetValue(ctIndex, out var c) && _vcd.TryGetValue(c.EntityIdx, out var n) ? n : "";

    /// <summary>Indici CT dei mezzi di un'unita' (senza doppioni).</summary>
    public List<int> UnitVehicleCts(int ctIndex)
    {
        var res = new List<int>();
        if (Ct.TryGetValue(ctIndex, out var c) && Ucd.TryGetValue(c.EntityIdx, out var u))
            foreach (int v in u.Vehicles) if (v > 0 && Ct.ContainsKey(v) && !res.Contains(v)) res.Add(v);
        return res;
    }

    /// <summary>Voce CT di un'arma (EntityType 6 = WCD) dal suo indice WCD, per sapere che genere di arma e'.</summary>
    public CtEntry? WeaponCt(int wcd) => _weaponCt.Value.TryGetValue(wcd, out var c) ? c : null;
    private Lazy<Dictionary<int, CtEntry>> _weaponCt => _wct ??= new Lazy<Dictionary<int, CtEntry>>(() =>
        Ct.Where(kv => kv.Value.EntityType == 6).GroupBy(kv => kv.Value.EntityIdx).ToDictionary(g => g.Key, g => g.First().Value));
    private Lazy<Dictionary<int, CtEntry>>? _wct;

    /// <summary>Tipo (CT) del volo che corrisponde a un tipo di squadrone: stesso SubType e Specific, Type 1 invece di 3. -1 se manca.</summary>
    public int FlightCtOf(int squadronCt)
    {
        if (!Ct.TryGetValue(squadronCt, out var s)) return -1;
        if (s.Type == 1) return squadronCt;
        foreach (var (k, c) in Ct)
            if (c.EntityType == 4 && c.Domain == 2 && c.Type == 1 && c.SubType == s.SubType && c.Specific == s.Specific) return k;
        return -1;
    }

    /// <summary>VCD dell'aereo di un tipo di squadrone o di volo, o -1.</summary>
    public int AircraftVcdOf(int unitCt)
    {
        int v = UnitVehicleCts(unitCt).FirstOrDefault();
        return Ct.TryGetValue(v, out var vc) ? vc.EntityIdx : -1;
    }

    /// <summary>Cannone dell'aereo di un tipo di squadrone o di volo (arma WCD e colpi come li conta BMS), o (0, 0).</summary>
    public (int Wcd, int Count) GunOf(int unitCt)
    {
        int v = UnitVehicleCts(unitCt).FirstOrDefault();
        return Ct.TryGetValue(v, out var vc) && VcdGun.TryGetValue(vc.EntityIdx, out var g) ? g : (0, 0);
    }

    public UcdEntry? UcdOf(int ctIndex) => Ct.TryGetValue(ctIndex, out var c) && Ucd.TryGetValue(c.EntityIdx, out var u) ? u : null;

    /// <summary>Tipo di battaglione antiaereo (UCD MainRole 5, come tutte le difese aeree delle campagne Korea).</summary>
    public bool IsAirDefense(int ctIndex) => Ct.TryGetValue(ctIndex, out var c) && c.Domain == 3 && c.Type == 1 && UcdOf(ctIndex)?.MainRole == 5;

    /// <summary>Tutti i tipi di battaglione antiaereo del database (indice CT).</summary>
    public List<int> AirDefenseTypes() => Ct.Where(kv => kv.Value.EntityType == 4 && IsAirDefense(kv.Key)).Select(kv => kv.Key).ToList();

    /// <summary>Organico pieno: 2 bit per elemento (numero di mezzi 0..3), come il roster delle unita' Korea.</summary>
    public int FullRoster(int ctIndex)
    {
        var e = UcdOf(ctIndex)?.Elements;
        int r = 0;
        if (e != null) for (int i = 0; i < 16; i++) r |= Math.Min(3, e[i]) << (2 * i);
        return r;
    }

    public string UcdName(int ctIndex) => Ct.TryGetValue(ctIndex, out var c) && Ucd.TryGetValue(c.EntityIdx, out var u) ? u.Name : "";

    private static IEnumerable<(int, string)> Records(string xml, string tag)
    {
        foreach (Match m in Regex.Matches(xml, $@"<{tag} Num=""(\d+)"">(.*?)</{tag}>", RegexOptions.Singleline))
            yield return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value);
    }
    private static int Int(string b, string t) { var x = Regex.Match(b, $"<{t}>(-?\\d+)</{t}>"); return x.Success ? int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture) : -1; }
    private static string Str(string b, string t) { var x = Regex.Match(b, $"<{t}>(.*?)</{t}>", RegexOptions.Singleline); return x.Success ? System.Net.WebUtility.HtmlDecode(x.Groups[1].Value).Trim() : ""; }

    public (int Domain, int Class, int Type)? ClassOf(int ctIndex) => Ct.TryGetValue(ctIndex, out var c) ? (c.Domain, c.Class, c.Type) : null;

    /// <summary>Tipo dell'obiettivo (1 base aerea, 15 incrocio ...) dal suo tipo nel file (indice CT + 100).</summary>
    public int ObjectiveType(int fileType) => Ct.TryGetValue(fileType - 100, out var c) ? c.Type : -1;

    /// <summary>Nome del tipo di unita' (UCD) e del suo mezzo principale (VCD), es. "Armor (T-72)".</summary>
    public string UnitTypeName(int fileType)
    {
        if (!Ct.TryGetValue(fileType - 100, out var c) || !_ucd.TryGetValue(c.EntityIdx, out var u)) return "";
        string veh = "";
        if (u.Vehicle >= 0 && Ct.TryGetValue(u.Vehicle, out var vc) && _vcd.TryGetValue(vc.EntityIdx, out var vn)) veh = vn;
        return veh.Length > 0 && !u.Name.Contains(veh, StringComparison.OrdinalIgnoreCase) ? $"{u.Name} ({veh})" : u.Name;
    }
}
