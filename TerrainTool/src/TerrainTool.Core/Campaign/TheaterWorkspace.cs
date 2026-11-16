using System.Text;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Work folder of a theater installed in BMS, for Objective Studio: a small project (&lt;theater&gt;.ttproj) that points to the
/// theater's own objects, campaign and NewTerrain (all read only) and keeps the edits next to it. Nothing is written into BMS.
/// </summary>
public static class TheaterWorkspace
{
    /// <summary>Folder name for a theater: its name without the characters Windows does not allow.</summary>
    public static string FolderName(BmsTheater t)
    {
        var sb = new StringBuilder();
        foreach (char c in t.Name.Trim()) sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' or ':' ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 0 ? s : Path.GetFileNameWithoutExtension(t.Tdf);
    }

    /// <summary>Opens (or creates) the work folder of the theater under <paramref name="workRoot"/>; the paths are refreshed from the .tdf each time.</summary>
    public static TheaterProject Open(BmsTheater t, string workRoot)
    {
        string dir = Path.Combine(workRoot, FolderName(t));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, FolderName(t) + ".ttproj");
        TheaterProject p;
        try { p = File.Exists(file) ? TheaterProject.Load(file) : new TheaterProject(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { p = new TheaterProject(); }
        p.ProjectFolder = dir;
        p.InstalledTheaterTdf = t.Tdf;
        p.BmsDataDir = t.DataDir;
        p.SourceObjectsDir = t.ObjectDir;
        p.SourceCampaignDir = t.CampaignDir;
        p.NewTerrainFolder = t.NewTerrainDir;
        // "old theater" = the installed theater itself: what the edits start from (read only)
        p.OldTheaterFolder = AddOnRoot(t);
        p.KoreaNewTerrain = Path.Combine(t.DataDir, "TerrData", "Korea", "NewTerrain");
        p.TerrainBuilderFolder = "";
        p.TbOutputFolder = "";
        if (t.IsNewTerrain)
        {
            try
            {
                var cfg = TheaterConfig.ParseTheaterTxt(File.ReadAllText(t.TheaterTxt));
                cfg.Name = FolderName(t);
                cfg.DisplayName = t.Name;
                p.Theater = cfg;
            }
            catch (Exception ex) when (ex is IOException or FormatException or OverflowException) { }
        }
        p.Theater.Name = FolderName(t);
        p.Save(file);
        return p;
    }

    /// <summary>Data\&lt;add-on&gt; of the theater, or Data itself for the theaters of BMS (Korea).</summary>
    public static string AddOnRoot(BmsTheater t) =>
        t.AddOnFolder == "BMS" ? t.DataDir : Path.Combine(t.DataDir, t.AddOnFolder);

    /// <summary>The files an installed theater needs for Objective Studio; empty when everything is there.</summary>
    public static List<string> Problems(BmsTheater t)
    {
        var res = new List<string>();
        if (!File.Exists(Path.Combine(t.ObjectDir, "Falcon4_CT.xml"))) res.Add($"Objects database not found: {Path.Combine(t.ObjectDir, "Falcon4_CT.xml")}");
        if (!Directory.Exists(Path.Combine(t.ObjectDir, "ObjectiveRelatedData"))) res.Add($"Objective layouts not found: {Path.Combine(t.ObjectDir, "ObjectiveRelatedData")}");
        if (FindCampObjData(t.CampaignDir) == null) res.Add($"CampObjData.XML not found in {t.CampaignDir}");
        if (!t.IsNewTerrain) res.Add("Not a New Terrain theater (no NewTerrain\\Theater.txt): BMS 4.38 theaters only.");
        return res;
    }

    public static string? FindCampObjData(string campaignDir)
    {
        if (!Directory.Exists(campaignDir)) return null;
        string f = Path.Combine(campaignDir, "CampObjData.XML");
        return File.Exists(f) ? f : Directory.EnumerateFiles(campaignDir).FirstOrDefault(x => Path.GetFileName(x).Equals("CampObjData.XML", StringComparison.OrdinalIgnoreCase));
    }
}
