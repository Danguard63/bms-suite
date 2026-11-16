using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Campaign;

/// <summary>Una riga di stations+ils.dat (struttura "Stations" di Mission Commander).</summary>
public sealed class Station
{
    public int CampId { get; set; }
    public int TcnChannel { get; set; }
    public string TcnBand { get; set; } = "X";
    public int TcnCallsign { get; set; }
    public int TcnRange { get; set; }
    public int TcnType { get; set; } = 1;
    public int Uhf { get; set; }
    public int Vhf { get; set; }
    public int Ils1 { get; set; }
    public int Ils2 { get; set; }
    public int Ils3 { get; set; }
    public int Ils4 { get; set; }
    public int OpsUhf { get; set; }
    public int GroundUhf { get; set; }
    public int ApproachUhf { get; set; }
    public int LsoUhf { get; set; }
    public int AtisVhf { get; set; }
    public int TowerFrag { get; set; }
    public int GroundFrag { get; set; }
    public int ApproachFrag { get; set; }
    public int AtisFrag { get; set; }
    public string Comment1 { get; set; } = "";
    public string Comment2 { get; set; } = "";
    /// <summary>Campi presenti nella riga (12, 16, 17, 20 o 21): si riscrivono gli stessi.</summary>
    public int Fields = 21;

    public int[] Ils => new[] { Ils1, Ils2, Ils3, Ils4 };
    public void SetIls(int i, int v) { switch (i) { case 0: Ils1 = v; break; case 1: Ils2 = v; break; case 2: Ils3 = v; break; case 3: Ils4 = v; break; } }
    public Station Clone() => (Station)MemberwiseClone();
}

/// <summary>Pista della base (PHD Type 1): un'estremita' con prua e numero della striscia.</summary>
public sealed record RunwayEnd(int Index, double Heading, int Strip)
{
    public string Label { get { int n = (int)Math.Round(((Heading % 360) + 360) % 360 / 10.0) % 36; return (n == 0 ? 36 : n).ToString("00", CultureInfo.InvariantCulture); } }
}

/// <summary>
/// File stations+ils.dat della cartella Campaign (TACAN, frequenze di torre / terra / avvicinamento / OPS / LSO / ATIS, ILS delle
/// piste 1-4, frammenti audio del nome della base), letto e scritto come Mission Commander (ReadStationIls / SaveStationIls):
/// commenti iniziali, poi per ogni base due righe "#" e la riga dei valori.
/// </summary>
public sealed class StationsFile
{
    public string Header = "";
    public List<Station> Stations = new();

    public static StationsFile Load(string path)
    {
        var f = new StationsFile();
        var lines = File.ReadAllLines(path, Encoding.Latin1);
        int first = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string l = Regex.Replace(lines[i].Replace('\t', ' '), " +", " ").Trim();
            if (l.StartsWith('#') || l.Length <= 3) continue;
            var a = l.Split(' ');
            if (a.Length < 12 || !int.TryParse(a[0], out _)) continue;
            int I(int k) => k < a.Length && int.TryParse(a[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            var s = new Station
            {
                CampId = I(0), TcnChannel = I(1), TcnBand = a[2], TcnCallsign = I(3), TcnRange = I(4), TcnType = I(5), Uhf = I(6), Vhf = I(7),
                Ils1 = I(8), Ils2 = I(9), Ils3 = I(10), Ils4 = I(11), OpsUhf = I(12), GroundUhf = I(13), ApproachUhf = I(14), LsoUhf = I(15),
                AtisVhf = I(16), TowerFrag = I(17), GroundFrag = I(18), ApproachFrag = I(19), AtisFrag = I(20), Fields = Math.Min(21, a.Length),
            };
            string C(int k) => k >= 0 && lines[k].TrimStart().StartsWith('#') ? lines[k].Trim().TrimStart('#').Trim() : "";
            s.Comment1 = i >= 2 && lines[i - 1].TrimStart().StartsWith('#') ? C(i - 2) : "";
            s.Comment2 = i >= 1 ? C(i - 1) : "";
            if (first < 0)
            {
                // intestazione = tutto prima delle due righe "#" che precedono la prima base
                first = i;
                int back = 0;
                while (back < 2 && i - back - 1 >= 0 && lines[i - back - 1].TrimStart().StartsWith('#')) back++;
                f.Header = string.Join("\r\n", lines.Take(i - back));
            }
            f.Stations.Add(s);
        }
        return f;
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        if (Header.Length > 0) sb.Append(Header.TrimEnd()).Append("\r\n");
        foreach (var s in Stations)
        {
            var v = new List<string>
            {
                s.CampId.ToString(CultureInfo.InvariantCulture), s.TcnChannel.ToString("000", CultureInfo.InvariantCulture), s.TcnBand,
                s.TcnCallsign.ToString(CultureInfo.InvariantCulture), s.TcnRange.ToString(CultureInfo.InvariantCulture), s.TcnType.ToString(CultureInfo.InvariantCulture),
                s.Uhf.ToString(CultureInfo.InvariantCulture), s.Vhf.ToString(CultureInfo.InvariantCulture),
                s.Ils1.ToString(CultureInfo.InvariantCulture), s.Ils2.ToString(CultureInfo.InvariantCulture), s.Ils3.ToString(CultureInfo.InvariantCulture), s.Ils4.ToString(CultureInfo.InvariantCulture),
            };
            int[] more = { s.OpsUhf, s.GroundUhf, s.ApproachUhf, s.LsoUhf, s.AtisVhf, s.TowerFrag, s.GroundFrag, s.ApproachFrag, s.AtisFrag };
            int n = Math.Max(s.Fields, 21) - 12;   // si scrive sempre il formato completo della 4.38 (21 campi)
            for (int i = 0; i < Math.Min(n, more.Length); i++) v.Add(more[i].ToString(CultureInfo.InvariantCulture));
            sb.Append("# ").Append(s.Comment1).Append("\r\n");
            sb.Append("# ").Append(s.Comment2).Append("\r\n");
            sb.Append(string.Join(" ", v)).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
    }

    // ───── controlli (da Mission Commander: CheckValidUHF / VHF / ILS, distanza 50 NM) ─────

    public const double ConflictKm = 50 * 1.852;

    /// <summary>UHF in kHz (306075 = 306.075): 225-400 MHz a passi di 25 kHz; 243.0 = guardia.</summary>
    public static string? UhfProblem(int khz)
    {
        if (khz == 0) return null;
        double m = khz / 1000.0;
        if (m < 225 || m > 400) return "UHF out of range (225-400 MHz)";
        if (khz == 243000) return "UHF guard frequency 243.000";
        if (khz % 25 != 0) return "UHF not on a 25 kHz step";
        return null;
    }

    /// <summary>VHF in kHz: 116-152 MHz come la radio VHF di BMS (Korea usa anche 117.4) o 30-88 FM, a passi di 25 kHz; 121.5 guardia.</summary>
    public static string? VhfProblem(int khz)
    {
        if (khz == 0) return null;
        double m = khz / 1000.0;
        if (!(m >= 116 && m < 152) && !(m >= 30 && m <= 88)) return "VHF out of range (116-152 MHz)";
        if (khz == 121500) return "VHF guard frequency 121.500";
        if (khz % 25 != 0) return "VHF not on a 25 kHz step";
        return null;
    }

    /// <summary>ILS in centinaia di kHz (11130 = 111.30): 108-111.95 MHz a passi di 50 kHz (Korea usa anche frequenze VOR fino a 117.95).</summary>
    public static string? IlsProblem(int v)
    {
        if (v == 0) return null;
        double m = v / 100.0;
        if (m < 108 || m >= 118) return "ILS out of range (108-117.95 MHz)";
        if (m >= 112) return "VOR frequency (above 111.95) in an ILS slot";
        if (v % 5 != 0) return "ILS not on a 50 kHz step";
        return null;
    }

    public static string Mhz(int khz) => khz == 0 ? "" : (khz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
    public static string IlsMhz(int v) => v == 0 ? "" : (v / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Controllo del file stazioni rispetto alla campagna e alle piste della nuova disposizione delle basi.</summary>
public sealed class StationsCheck
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly string? _ordDir;
    private readonly Dictionary<int, List<RunwayEnd>> _rwys = new();

    public StationsCheck(CampaignDoc d, CampaignDb db)
    {
        _d = d; _db = db;
        string? dir = db.CtPath == null ? null : Path.Combine(Path.GetDirectoryName(db.CtPath)!, "ObjectiveRelatedData");
        _ordDir = dir != null && Directory.Exists(dir) ? dir : null;
    }

    public CampObjective? Objective(int campId) => _d.Objectives.FirstOrDefault(o => o.CampId == campId);
    public bool IsAirfield(CampObjective o) => _db.ObjectiveType(o.Type) is 1 or 2;

    /// <summary>Piste della disposizione dell'obiettivo (PHD Type 1, in ordine: come gli slot ILS 1-4 di Mission Commander).</summary>
    public List<RunwayEnd> Runways(CampObjective o)
    {
        int ocd = _db.Ct.TryGetValue(o.Type - 100, out var c) ? c.EntityIdx : -1;
        if (_rwys.TryGetValue(ocd, out var r)) return r;
        r = new List<RunwayEnd>();
        string? f = _ordDir == null || ocd < 0 || !Directory.Exists(Path.Combine(_ordDir, $"OCD_{ocd:00000}")) ? null
            : Directory.EnumerateFiles(Path.Combine(_ordDir, $"OCD_{ocd:00000}"), "PHD_*.XML").FirstOrDefault();
        if (f != null)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"<PHD Num=""\d+"">(.*?)</PHD>", RegexOptions.Singleline))
            {
                string b = m.Groups[1].Value;
                if (Regex.Match(b, @"<Type>(\d+)</Type>").Groups[1].Value != "1") continue;
                double.TryParse(Regex.Match(b, @"<Data>([-\d.]+)</Data>").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h);
                int.TryParse(Regex.Match(b, @"<RunwayNumber>(\d+)</RunwayNumber>").Groups[1].Value, out int strip);
                r.Add(new RunwayEnd(r.Count, h, strip));
            }
        return _rwys[ocd] = r;
    }

    public bool HasLayout(CampObjective o) => _ordDir != null && _db.Ct.TryGetValue(o.Type - 100, out var c) && Directory.Exists(Path.Combine(_ordDir, $"OCD_{c.EntityIdx:00000}"));

    public sealed record Problem(Station? S, IssueLevel Level, string Text);

    public List<Problem> Run(StationsFile f)
    {
        var res = new List<Problem>();
        var byCamp = f.Stations.GroupBy(s => s.CampId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var g in byCamp.Where(g => g.Value.Count > 1)) res.Add(new Problem(g.Value[1], IssueLevel.Error, $"campaign ID {g.Key} has {g.Value.Count} lines"));
        foreach (var s in f.Stations)
        {
            var o = Objective(s.CampId);
            if (o == null) { res.Add(new Problem(s, IssueLevel.Warning, $"no objective with campaign ID {s.CampId} in this campaign")); continue; }
            if (!IsAirfield(o)) res.Add(new Problem(s, IssueLevel.Info, $"{o.Name} is a {Editor.EditorData.TypeName(_db.ObjectiveType(o.Type))}, not an airbase"));
            void F(string? p, string what) { if (p != null) res.Add(new Problem(s, p.Contains("range") ? IssueLevel.Error : p.StartsWith("VOR") ? IssueLevel.Info : IssueLevel.Warning, $"{o.Name}: {what} – {p}")); }
            F(StationsFile.UhfProblem(s.Uhf), "tower UHF"); F(StationsFile.VhfProblem(s.Vhf), "tower VHF");
            F(StationsFile.UhfProblem(s.OpsUhf), "OPS"); F(StationsFile.UhfProblem(s.GroundUhf), "ground"); F(StationsFile.UhfProblem(s.ApproachUhf), "approach");
            F(StationsFile.UhfProblem(s.LsoUhf), "LSO"); F(StationsFile.VhfProblem(s.AtisVhf), "ATIS");
            for (int i = 0; i < 4; i++) F(StationsFile.IlsProblem(s.Ils[i]), $"ILS {i + 1}");
            if (s.TcnChannel < 0 || s.TcnChannel > 126) res.Add(new Problem(s, IssueLevel.Error, $"{o.Name}: TACAN channel {s.TcnChannel} (1-126)"));
            if (s.TcnBand is not ("X" or "Y")) res.Add(new Problem(s, IssueLevel.Error, $"{o.Name}: TACAN band '{s.TcnBand}' (X or Y)"));
            // ILS e piste della disposizione nuova
            if (IsAirfield(o) && HasLayout(o))
            {
                var rw = Runways(o);
                for (int i = 0; i < 4; i++)
                    if (s.Ils[i] != 0 && i >= rw.Count)
                        res.Add(new Problem(s, IssueLevel.Warning, $"{o.Name}: ILS {i + 1} ({StationsFile.IlsMhz(s.Ils[i])}) but the base has only {rw.Count} runway ends ({string.Join(", ", rw.Select(r => r.Label))}): BMS ignores it"));
                var old = OldRunways?.Invoke(s.CampId);
                if (old != null && old.Count > 0 && !SameStructure(old, rw) && s.Ils.Any(v => v != 0))
                    res.Add(new Problem(s, IssueLevel.Warning, $"{o.Name}: different runways in the new layout ({Labels(old)} → {Labels(rw)}): the ILS slots may point to the wrong runway – use Remap ILS"));
                if (rw.Count > 0 && s.Ils.Take(rw.Count).All(v => v == 0) && _db.ObjectiveType(o.Type) == 1)
                    res.Add(new Problem(s, IssueLevel.Info, $"{o.Name}: no ILS on its {rw.Count} runway ends"));
            }
        }
        // stessa frequenza / TACAN entro 50 NM (come i controlli di distanza di Mission Commander)
        var withObj = f.Stations.Select(s => (S: s, O: Objective(s.CampId))).Where(x => x.O != null).ToList();
        void Same(string what, Func<Station, IEnumerable<int>> vals, Func<int, string> fmt)
        {
            for (int a = 0; a < withObj.Count; a++)
                for (int b = a + 1; b < withObj.Count; b++)
                {
                    double km = Math.Sqrt(Math.Pow(withObj[a].O!.X - withObj[b].O!.X, 2) + Math.Pow(withObj[a].O!.Y - withObj[b].O!.Y, 2));
                    if (km > StationsCheck.ConflictKm) continue;
                    foreach (var v in vals(withObj[a].S).Where(v => v != 0).Intersect(vals(withObj[b].S).Where(v => v != 0)))
                        res.Add(new Problem(withObj[b].S, IssueLevel.Warning, $"{what} {fmt(v)} used by {withObj[a].O!.Name} and {withObj[b].O!.Name} ({km / 1.852:0} NM apart)"));
                }
        }
        Same("TACAN", s => s.TcnChannel > 0 && s.TcnRange > 0 ? new[] { s.TcnChannel * 2 + (s.TcnBand == "Y" ? 1 : 0) } : Array.Empty<int>(), v => $"{v / 2}{(v % 2 == 1 ? "Y" : "X")}");
        Same("UHF", s => new[] { s.Uhf, s.OpsUhf, s.GroundUhf, s.ApproachUhf, s.LsoUhf }, StationsFile.Mhz);
        Same("VHF", s => new[] { s.Vhf, s.AtisVhf }, StationsFile.Mhz);
        Same("ILS", s => s.Ils.Distinct(), StationsFile.IlsMhz);
        // basi della campagna senza stazione
        foreach (var o in _d.Objectives.Where(o => _db.ObjectiveType(o.Type) == 1 && !byCamp.ContainsKey(o.CampId)))
            res.Add(new Problem(null, IssueLevel.Warning, $"{o.Name} (campaign ID {o.CampId}): airbase without a line (no TACAN, tower, ILS)"));
        return res;
    }

    public const double ConflictKm = StationsFile.ConflictKm;

    /// <summary>Piste della disposizione del vecchio add-on per CampId (per riportare gli ILS sulle piste giuste dopo i cambi di base).</summary>
    public Func<int, List<RunwayEnd>?>? OldRunways;

    public static string Labels(List<RunwayEnd> r) => r.Count == 0 ? "none" : string.Join("/", r.Select(x => x.Label));

    /// <summary>Posizione di ogni estremita': (striscia nell'ordine in cui compare, estremita' dentro la striscia).</summary>
    private static List<(int Strip, int End)> Keys(List<RunwayEnd> r)
    {
        var order = new List<int>(); var seen = new Dictionary<int, int>(); var res = new List<(int, int)>();
        foreach (var e in r)
        {
            if (!order.Contains(e.Strip)) order.Add(e.Strip);
            seen[e.Strip] = seen.GetValueOrDefault(e.Strip) + 1;
            res.Add((order.IndexOf(e.Strip), seen[e.Strip] - 1));
        }
        return res;
    }

    /// <summary>Stesse piste nello stesso ordine, al massimo ruotate tutte dello stesso angolo (come fa il lavoro Objects con le piste vere).</summary>
    public static bool SameStructure(List<RunwayEnd> a, List<RunwayEnd> b)
    {
        if (a.Count != b.Count || !Keys(a).SequenceEqual(Keys(b))) return false;
        if (a.Count == 0) return true;
        double d0 = ((b[0].Heading - a[0].Heading) % 360 + 360) % 360;
        return a.Zip(b).All(p => Math.Abs((((p.Second.Heading - p.First.Heading) % 360 + 360) % 360 - d0 + 540) % 360 - 180) <= 3);
    }

    /// <summary>
    /// Riporta gli ILS sulle piste giuste dopo un cambio di disposizione (numero o ordine delle piste diverso: base coreana, due strisce
    /// diventate una...). Gli slot ILS 1-4 sono le estremita' delle piste nell'ordine della disposizione: ogni frequenza va sull'estremita'
    /// nuova nella stessa posizione (1a striscia / 1a estremita', 1a striscia / 2a, 2a striscia / 1a...); se la striscia non c'e' piu',
    /// la frequenza si perde (lo si dice). Le piste solo ruotate (stessa struttura) non si toccano.
    /// </summary>
    public List<string> RemapIls(StationsFile f)
    {
        var log = new List<string>();
        foreach (var s in f.Stations)
        {
            var o = Objective(s.CampId);
            if (o == null || !IsAirfield(o) || !HasLayout(o) || OldRunways == null) continue;
            var old = OldRunways(s.CampId);
            var nw = Runways(o);
            if (old == null || old.Count == 0 || SameStructure(old, nw) || s.Ils.All(v => v == 0)) continue;
            var ko = Keys(old); var kn = Keys(nw);
            var res = new int[4];
            var lost = new List<string>();
            for (int i = 0; i < Math.Min(4, old.Count); i++)
            {
                int v = s.Ils[i];
                if (v == 0) continue;
                int j = kn.IndexOf(ko[i]);
                if (j < 0 || j >= 4) { lost.Add($"{StationsFile.IlsMhz(v)} (rwy {old[i].Label})"); continue; }
                res[j] = v;
            }
            var before = string.Join(" ", s.Ils.Select(StationsFile.IlsMhz));
            for (int i = 0; i < 4; i++) s.SetIls(i, res[i]);
            string after = string.Join(" ", s.Ils.Select(StationsFile.IlsMhz));
            if (after == before && lost.Count == 0) continue;
            log.Add($"{o.Name}: runways {Labels(old)} → {Labels(nw)}; ILS [{before}] → [{after}]" + (lost.Count > 0 ? $"; no runway any more for {string.Join(", ", lost.Distinct())}" : ""));
        }
        return log;
    }

    /// <summary>Basi tolte (diventate incroci): le loro righe non servono piu' (TACAN e frequenze restano libere).</summary>
    public List<string> RemoveJunctionLines(StationsFile f)
    {
        var gone = f.Stations.Where(s => Objective(s.CampId) is { } o && _db.ObjectiveType(o.Type) == 15).ToList();
        foreach (var s in gone) f.Stations.Remove(s);
        return gone.Select(s => $"{Objective(s.CampId)!.Name} (campaign ID {s.CampId})").ToList();
    }

    /// <summary>Toglie gli ILS degli slot oltre il numero di piste della disposizione nuova; restituisce quanti.</summary>
    public int TrimIls(StationsFile f)
    {
        int n = 0;
        foreach (var s in f.Stations)
        {
            var o = Objective(s.CampId);
            if (o == null || !IsAirfield(o) || !HasLayout(o)) continue;
            int rc = Runways(o).Count;
            for (int i = rc; i < 4; i++) if (s.Ils[i] != 0) { s.SetIls(i, 0); n++; }
        }
        return n;
    }

    /// <summary>Frequenza ILS libera (108.10-111.95, decimi dispari come gli ILS veri) non usata entro 50 NM.</summary>
    public int FreeIls(StationsFile f, CampObjective at)
    {
        var used = f.Stations.Select(s => (S: s, O: Objective(s.CampId))).Where(x => x.O != null &&
                       Math.Sqrt(Math.Pow(x.O!.X - at.X, 2) + Math.Pow(x.O!.Y - at.Y, 2)) <= ConflictKm).SelectMany(x => x.S.Ils).ToHashSet();
        for (int v = 10810; v < 11200; v += 5)
            if ((v / 10) % 2 == 1 && !used.Contains(v)) return v;
        return 0;
    }
}
