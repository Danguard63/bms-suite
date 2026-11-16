using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Campaign;

/// <summary>Obiettivo nuovo (per ora: incrocio che spezza un link troppo lungo). Posizione in piedi (x nord, y est, come CampObjData).</summary>
/// <param name="CampId">numero di campagna, unico nel teatro</param>
/// <param name="Num">VU id, libero in tutti i file della cartella Campaign (obiettivi e unita')</param>
/// <param name="TemplateCampId">obiettivo da cui si copia il record (stesso tipo e disposizione: un incrocio vicino)</param>
/// <param name="SplitA">se diverso da 0, il link SplitA–SplitB viene sostituito da SplitA–nuovo e nuovo–SplitB</param>
public sealed record AddedObjective(int CampId, uint Num, string Name, int TemplateCampId, double NorthFt, double EastFt, int SplitA = 0, int SplitB = 0);

/// <summary>Link aggiunto o tolto tra due obiettivi (CampId), in entrambe le direzioni.</summary>
public sealed record LinkEdit(int A, int B, bool Add);

/// <summary>
/// Modifiche alla rete degli obiettivi fatte nell'Object Editor: obiettivi nuovi e link. Restano nel progetto (network_edits.json,
/// accanto a objective_edits.csv) e "Apply object changes" del Campaign Editor le scrive in CampObjData.XML, nel modello del TE
/// (Te_New_Nt / te_new) e nei salvataggi della campagna (*.cam), insieme agli spostamenti di Run Objects. I TE salvati prendono gli
/// obiettivi dal modello, quindi li vedono senza toccarli.
/// </summary>
public sealed class NetworkEdits
{
    public List<AddedObjective> Added { get; set; } = new();
    public List<LinkEdit> Links { get; set; } = new();

    public const string FileName = "network_edits.json";
    public static string PathFor(TheaterProject p) => Path.Combine(p.ProjectFolder, FileName);
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public bool IsEmpty => Added.Count == 0 && Links.Count == 0;

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts), new UTF8Encoding(false));
    public static NetworkEdits Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<NetworkEdits>(File.ReadAllText(path), Opts) ?? new() : new();

    /// <summary>Un link aggiunto e poi tolto (o viceversa) si annulla: resta solo l'ultima scelta per ogni coppia.</summary>
    public void SetLink(int a, int b, bool add)
    {
        Links.RemoveAll(l => l.A == a && l.B == b || l.A == b && l.B == a);
        Links.Add(new LinkEdit(a, b, add));
    }

    /// <summary>
    /// Numeri liberi per un obiettivo nuovo: CampId = il piu' alto + 1; VU id = il primo numero non usato da nessun obiettivo e
    /// nessuna unita' in nessuno dei file (i salvataggi in corso creano unita' con numeri sparsi, anche tra quelli degli obiettivi),
    /// a partire dal piu' alto degli obiettivi, cosi' resta vicino a loro. Anche gli obiettivi nuovi gia' decisi contano.
    /// </summary>
    public (int CampId, uint Num) NextIds(IEnumerable<string> campaignFiles, Func<int, (int Domain, int Class, int Type)?> classOf)
    {
        var nums = new HashSet<uint>(Added.Select(a => a.Num));
        int camp = Added.Select(a => a.CampId).DefaultIfEmpty(0).Max();
        uint maxObj = 0;
        foreach (var f in campaignFiles)
        {
            CampaignDoc d;
            try { d = CampaignDoc.Load(f, classOf); } catch { continue; }
            foreach (var o in d.Objectives) { nums.Add(o.Id.Num); camp = Math.Max(camp, o.CampId); maxObj = Math.Max(maxObj, o.Id.Num); }
            if (d.Units != null) foreach (var u in d.Units.Units) nums.Add(u.Id.Num);
        }
        uint n = Math.Max(maxObj + 1, Added.Select(a => a.Num + 1).DefaultIfEmpty(0u).Max());
        while (nums.Contains(n)) n++;
        return (camp + 1, n);
    }

    /// <summary>
    /// Scrive le modifiche nel documento (solo file con obiettivi propri: modello del TE e salvataggi). Idempotente: un obiettivo
    /// nuovo gia' presente (stesso CampId) viene solo rimesso nella posizione e nel nome giusti. Restituisce le note.
    /// </summary>
    public List<string> ApplyTo(CampaignDoc d)
    {
        var log = new List<string>();
        if (!d.ObjectivesOwn) { log.Add("no objectives of its own (it takes them from the scenario)"); return log; }
        var byCamp = d.Objectives.GroupBy(o => (int)o.CampId).ToDictionary(g => g.Key, g => g.First());
        int added = 0, links = 0;
        foreach (var a in Added)
        {
            if (byCamp.TryGetValue(a.CampId, out var ex))
            {
                d.MoveObjective(ex, a.NorthFt, a.EastFt);
                continue;
            }
            if (!byCamp.TryGetValue(a.TemplateCampId, out var tpl)) { log.Add($"{a.Name}: template objective {a.TemplateCampId} not in this file"); continue; }
            if (d.Objectives.Any(o => o.Id.Num == a.Num) || d.Units?.Units.Any(u => u.Id.Num == a.Num) == true)
            { log.Add($"{a.Name}: id {a.Num} already used in this file, not added"); continue; }
            var o = Clone(tpl, a, d.Version);
            var pa = a.SplitA != 0 ? byCamp.GetValueOrDefault(a.SplitA) : null;
            var pb = a.SplitB != 0 ? byCamp.GetValueOrDefault(a.SplitB) : null;
            // squadra e obiettivo principale da cui dipende: quelli del capo del link piu' vicino (se no quelli del modello)
            var near = new[] { pa, pb }.Where(x => x != null).OrderBy(x => Math.Pow(x!.X - o.X, 2) + Math.Pow(x.Y - o.Y, 2)).FirstOrDefault();
            if (near != null)
            {
                o.Owner = near.Owner; o.FirstOwner = near.FirstOwner;
                o.Parent = near.Parent.IsNull || near.Parent == near.Id ? near.Id : near.Parent;
            }
            d.Objectives.Add(o);
            byCamp[a.CampId] = o;
            d.Reindex();
            if (pa != null && pb != null)
            {
                // i due pezzi restano del tipo del link spezzato: un costo 255 (es. ruote e cingoli su un link solo aereo/navale) resta 255
                var orig = pa.Links.FirstOrDefault(l => l.To == pb.Id)?.Costs ?? pb.Links.FirstOrDefault(l => l.To == pa.Id)?.Costs;
                d.RemoveLink(pa, pb);
                d.AddLink(pa, o); d.AddLink(o, pb);
                if (orig != null)
                    foreach (var (x, y) in new[] { (pa, o), (o, pa), (o, pb), (pb, o) })
                        foreach (var l in x.Links.Where(l => l.To == y.Id))
                            for (int i = 0; i < Math.Min(orig.Length, l.Costs.Length); i++)
                                if (orig[i] == 255) l.Costs[i] = 255;
            }
            added++;
        }
        foreach (var l in Links)
        {
            if (!byCamp.TryGetValue(l.A, out var a) || !byCamp.TryGetValue(l.B, out var b)) { log.Add($"link {l.A}–{l.B}: objective not in this file"); continue; }
            if (l.Add ? d.AddLink(a, b) : d.RemoveLink(a, b)) links++;
        }
        d.Dirty = true;
        log.Insert(0, $"{added} objectives added, {links} links changed");
        return log;
    }

    /// <summary>Record di un obiettivo nuovo copiato dal modello: id, CampId, nome, posizione; niente link, pezzi integri, non in prima linea.</summary>
    private static CampObjective Clone(CampObjective tpl, AddedObjective a, int version)
    {
        var raw = (byte[])tpl.Raw.Clone();
        new VuId(a.Num, tpl.Id.Creator).Write(raw, 2);
        BitConverter.TryWriteBytes(raw.AsSpan(29, 2), (short)a.CampId);
        // stato dei pezzi: tutti integri
        Array.Clear(raw, tpl.PrioAt - tpl.FeatureCount, tpl.FeatureCount);
        // nome (ultimi 80 byte del record, se la versione lo ha)
        if (tpl.HasName)
        {
            int at = raw.Length - 80;
            Array.Clear(raw, at, 80);
            var nb = Encoding.Latin1.GetBytes(a.Name.Length > 79 ? a.Name[..79] : a.Name);
            nb.CopyTo(raw, at);
        }
        int p = 0;
        var o = CampObjective.Read(raw, ref p, version);
        o.Links.Clear();
        o.Flags &= ~(CampObjective.FrontLine | CampObjective.SecondLine | CampObjective.ThirdLine);
        o.SimNorth = a.NorthFt; o.SimEast = a.EastFt; o.HeadingRad = 0;
        o.X = CampaignDoc.SimToGrid(a.EastFt); o.Y = CampaignDoc.SimToGrid(a.NorthFt);
        return o;
    }

    /// <summary>CampObjData.XML: aggiunge (o aggiorna) gli obiettivi nuovi, con la disposizione (OCD) del loro modello.</summary>
    public string ApplyToCampObjData(string xml)
    {
        var ci = CultureInfo.InvariantCulture;
        var ocd = Regex.Matches(xml, @"<CampObj CampId=""(\d+)"">.*?<OcdIndex>(\d+)</OcdIndex>", RegexOptions.Singleline)
            .GroupBy(m => int.Parse(m.Groups[1].Value, ci)).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var sb = new StringBuilder();
        foreach (var a in Added)
        {
            string entry = $"    <CampObj CampId=\"{a.CampId}\">\r\n        <CampName>{System.Security.SecurityElement.Escape(a.Name)}</CampName>\r\n" +
                           $"        <OcdIndex>{ocd.GetValueOrDefault(a.TemplateCampId, "0")}</OcdIndex>\r\n        <Heading>0.000</Heading>\r\n" +
                           $"        <PositionX>{a.NorthFt.ToString("0.000", ci)}</PositionX>\r\n        <PositionY>{a.EastFt.ToString("0.000", ci)}</PositionY>\r\n" +
                           $"        <PositionZ>0.000</PositionZ>\r\n    </CampObj>\r\n";
            var old = Regex.Match(xml, $@"[ \t]*<CampObj CampId=""{a.CampId}"">.*?</CampObj>\r?\n?", RegexOptions.Singleline);
            if (old.Success) xml = xml.Remove(old.Index, old.Length).Insert(old.Index, entry);
            else sb.Append(entry);
        }
        int end = xml.LastIndexOf("</CampObjData>", StringComparison.Ordinal);
        return end < 0 ? xml : xml.Insert(end, sb.ToString());
    }
}
