using System.IO;
using TerrainTool.Core.Project;

namespace CampaignEditor;

/// <summary>Installazione dei file di campagna di un progetto di TerrainTool nella cartella Campaign del suo add-on in BMS.</summary>
public static class ProjectInstall
{
    /// <summary>&lt;BMS&gt;\Data\&lt;add-on&gt;\Campaign (la cartella Data si trova risalendo dal percorso della Korea del progetto), o null.</summary>
    public static string? TargetCampaign(TheaterProject p)
    {
        if (string.IsNullOrWhiteSpace(p.OldTheaterFolder) || string.IsNullOrWhiteSpace(p.KoreaNewTerrain)) return null;
        var d = new DirectoryInfo(p.KoreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        if (d == null) return null;
        string t = Path.Combine(d.FullName, new DirectoryInfo(p.OldTheaterFolder).Name, "Campaign");
        return Directory.Exists(t) ? t : null;
    }

    /// <summary>File di src (non nelle sottocartelle, tranne le note del tool) che in target mancano o hanno un contenuto diverso.</summary>
    public static List<string> Changed(string src, string target)
    {
        var res = new List<string>();
        foreach (var f in Directory.EnumerateFiles(src).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(f).Equals("relocated.txt", StringComparison.OrdinalIgnoreCase)) continue;
            string t = Path.Combine(target, Path.GetFileName(f));
            if (!File.Exists(t) || !Same(f, t)) res.Add(f);
        }
        return res;
    }

    private static bool Same(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
        var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
        while (true)
        {
            int na = sa.Read(ba, 0, ba.Length), nb = sb.Read(bb, 0, bb.Length);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }
}
