using TerrainTool.Core.Campaign;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Editor;

/// <summary>
/// Collegamenti e gerarchia degli obiettivi della campagna, per l'editor degli obiettivi (sola lettura).
/// Gerarchia (campo Parent del file .obj, come in KTO 4.38): livello 0 = obiettivi primari (PO, senza padre: le grandi
/// citta'), 1 = secondari (SO, il padre e' un PO), 2 e 3 = gli obiettivi sotto i secondari. KTO: 56 / 1308 / 3649 / 986.
/// Collegamenti (Links): la rete su cui si muovono le unita' di terra; Ground = usato da ruote o cingoli.
/// Letti dallo stesso Te_New_Nt.tac che usa Run Objects (CampaignEdits, poi Output\Install, poi BMS, poi il vecchio add-on).
/// </summary>
public sealed class CampaignTree
{
    public string Source { get; private set; } = "";
    /// <summary>CampId → CampId del padre.</summary>
    public Dictionary<int, int> Parent { get; } = new();
    /// <summary>CampId → livello (0 = primario, 1 = secondario, 2, 3...).</summary>
    public Dictionary<int, int> Level { get; } = new();
    /// <summary>CampId → numero di figli.</summary>
    public Dictionary<int, int> Children { get; } = new();
    /// <summary>Coppie di CampId collegate, una volta sola. Ground = ruote o cingoli; OneWay = solo da A a B.</summary>
    public List<(int A, int B, bool Ground, bool OneWay)> Links { get; } = new();
    public Dictionary<int, int> LinkCount { get; } = new();
    /// <summary>Collegamenti di terra senza passaggio sulla mappa della campagna di BMS (coppie di CampId, il minore prima).</summary>
    public HashSet<(int, int)> Blocked { get; } = new();
    /// <summary>Da dove vengono i collegamenti interrotti, o perche' non ci sono.</summary>
    public string BlockedNote { get; private set; } = "";

    public static string? FindTemplate(TheaterProject p)
    {
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        string? data = CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain);
        string? bmsCamp = data != null ? Path.Combine(data, addOn, "Campaign") : null;
        static string? Find(string? dir) => dir != null && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("Te_New_Nt.tac", StringComparison.OrdinalIgnoreCase))
              ?? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("te_new.tac", StringComparison.OrdinalIgnoreCase))
            : null;
        if (p.IsInstalledTheater) return Find(p.SourceCampaignDir);
        return Find(Path.Combine(p.ProjectFolder, "CampaignEdits")) ?? Find(Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign"))
               ?? Find(bmsCamp) ?? Find(Path.Combine(p.OldTheaterFolder, "Campaign"));
    }

    public static CampaignTree? Load(TheaterProject p, out string? error)
    {
        error = null;
        string? tpl = FindTemplate(p);
        if (tpl == null) { error = "no Te_New_Nt.tac found (CampaignEdits, Output\\Install, BMS or the old add-on)"; return null; }
        CampaignDoc doc;
        try { doc = CampaignDoc.Load(tpl, _ => null, new[] { Path.GetDirectoryName(tpl)! }); }
        catch (Exception ex) { error = ex.Message; return null; }
        var t = new CampaignTree { Source = tpl };
        var byId = doc.Objectives.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var o in doc.Objectives)
            if (!o.Parent.IsNull && byId.TryGetValue(o.Parent, out var par) && par.CampId != o.CampId)
            {
                t.Parent[o.CampId] = par.CampId;
                t.Children[par.CampId] = t.Children.GetValueOrDefault(par.CampId) + 1;
            }
        foreach (var o in doc.Objectives)
        {
            int k = 0, c = o.CampId;
            var seen = new HashSet<int> { c };
            while (t.Parent.TryGetValue(c, out int up) && seen.Add(up)) { c = up; k++; }
            t.Level[o.CampId] = k;
        }
        var dir = new HashSet<(int, int)>();
        var ground = new Dictionary<(int, int), bool>();
        foreach (var a in doc.Objectives)
            foreach (var l in a.Links)
            {
                if (!byId.TryGetValue(l.To, out var b) || a.CampId == b.CampId) continue;
                dir.Add((a.CampId, b.CampId));
                var key = a.CampId < b.CampId ? (a.CampId, b.CampId) : (b.CampId, a.CampId);
                bool g = !(l.Costs.Length > 3 && l.Costs[2] == 255 && l.Costs[3] == 255);
                ground[key] = ground.GetValueOrDefault(key) || g;
            }
        foreach (var (key, g) in ground)
        {
            bool oneWay = !(dir.Contains(key) && dir.Contains((key.Item2, key.Item1)));
            t.Links.Add((key.Item1, key.Item2, g, oneWay));
            t.LinkCount[key.Item1] = t.LinkCount.GetValueOrDefault(key.Item1) + 1;
            t.LinkCount[key.Item2] = t.LinkCount.GetValueOrDefault(key.Item2) + 1;
        }
        // collegamenti interrotti: celle della campagna rigenerate da BMS (*.terrain.binpb nella cartella Campaign dell'add-on)
        try
        {
            string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
            string? data = CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain);
            string? bmsCamp = p.IsInstalledTheater ? p.SourceCampaignDir : data != null ? Path.Combine(data, addOn, "Campaign") : null;
            string? cache = bmsCamp != null && Directory.Exists(bmsCamp) ? Directory.EnumerateFiles(bmsCamp, "*.terrain.binpb").FirstOrDefault() : null;
            if (cache == null) t.BlockedNote = "blocked links not checked: no *.terrain.binpb in the add-on's Campaign folder (load a campaign in BMS once)";
            else
            {
                var (issues, n) = CampaignTerrainCheck.Check(doc, CampaignTerrainCache.Read(cache));
                foreach (var i in issues) t.Blocked.Add(i.CampA < i.CampB ? (i.CampA, i.CampB) : (i.CampB, i.CampA));
                t.BlockedNote = $"{t.Blocked.Count} of {n} ground links blocked by water ({Path.GetFileName(cache)} of {File.GetLastWriteTime(cache):dd/MM HH:mm})";
            }
        }
        catch (Exception ex) { t.BlockedNote = "blocked links not checked: " + ex.Message; }
        return t;
    }

    public static string LevelName(int level) => level switch { 0 => "primary (PO)", 1 => "secondary (SO)", 2 => "third level", 3 => "fourth level", _ => $"level {level + 1}" };
}
