using TerrainTool.Core.Project;

namespace CampaignEditor;

/// <summary>Installazione dei file di campagna nella cartella Campaign in BMS (la logica e' in TerrainTool.Core.Campaign.ProjectCampaign).</summary>
public static class ProjectInstall
{
    public static string? TargetCampaign(TheaterProject p) => TerrainTool.Core.Campaign.ProjectCampaign.TargetCampaign(p);
    public static List<string> Changed(string src, string target) => TerrainTool.Core.Campaign.ProjectCampaign.Changed(src, target);
}
