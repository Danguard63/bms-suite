using System.IO;
using TerrainTool.Core.Campaign;

namespace ModelEditor;

/// <summary>
/// Dove stanno i modelli: quelli di BMS (Terrdata\objects\Models\&lt;n&gt;) e le cartelle "esterne" aperte da disco, per esempio
/// un modello esportato da Blender. Per quelle esterne texture condivise e texture numerate si cercano comunque in BMS.
/// </summary>
public static class ModelPlaces
{
    /// <summary>Cartella Models di BMS caricata (null se non ancora scelta).</summary>
    public static string? BmsModelsRoot { get; set; }

    /// <summary>Models folder of the chosen theater, when it has its own (add-on): looked in first, then BMS.</summary>
    public static string? TheaterModelsRoot { get; set; }

    private static bool Same(string? a, string? b) => a != null && b != null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static bool IsExternal(string dir)
    {
        if (BmsModelsRoot == null) return true;
        var parent = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\', '/'));
        return !Same(parent, BmsModelsRoot) && !Same(parent, TheaterModelsRoot);
    }

    /// <summary>Folder of model n: the theater's own first, then BMS's.</summary>
    public static string DirFor(int n)
    {
        string s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (TheaterModelsRoot != null && Directory.Exists(Path.Combine(TheaterModelsRoot, s))) return Path.Combine(TheaterModelsRoot, s);
        return Path.Combine(BmsModelsRoot ?? "", s);
    }

    /// <summary>Radice per i percorsi relativi di Materials.mtl ("1909/PilotUS_ALB.dds"): Models di BMS anche per le cartelle esterne.</summary>
    public static string RootFor(string dir) => IsExternal(dir) && BmsModelsRoot != null && Directory.Exists(BmsModelsRoot) ? BmsModelsRoot : Path.GetDirectoryName(dir) ?? dir;

    /// <summary>KoreaObj_HiRes / KoreaObj accanto al modello o, per una cartella esterna, quelle di BMS.</summary>
    public static List<string> TexDirsFor(string dir)
    {
        var l = new List<string>();
        if (IsExternal(dir) && BmsModelsRoot != null) l.AddRange(CampaignDb.ModelTextureDirs(Path.Combine(BmsModelsRoot, "0")));
        try { l.AddRange(CampaignDb.ModelTextureDirs(dir)); } catch { /* cartella alla radice di un disco */ }
        // i modelli di un add-on usano spesso le texture numerate di BMS
        if (!IsExternal(dir) && BmsModelsRoot != null) l.AddRange(CampaignDb.ModelTextureDirs(Path.Combine(BmsModelsRoot, "0")));
        return l.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>La cartella (o una sua sottocartella diretta) che contiene Parent.dat.</summary>
    public static string? FindModelFolder(string path)
    {
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(path)) return null;
        if (File.Exists(Path.Combine(path, "Parent.dat"))) return path;
        var subs = Directory.EnumerateDirectories(path).Where(d => File.Exists(Path.Combine(d, "Parent.dat"))).ToList();
        return subs.Count == 1 ? subs[0] : null;
    }
}
