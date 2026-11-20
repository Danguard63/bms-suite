namespace TerrainTool.Core.Sim;

/// <summary>
/// The Sim files a project can change: BMS's Data\Sim (read only), the project's SimOverrides (its own copies, built into the
/// theater's NT\Sim by Build theater) and, next to them, SimOverrides_BMS: the BMS file as it was when it was copied, to see
/// what a later BMS update changed under a copy of the theater.
/// </summary>
public sealed class SimLibrary
{
    public string BmsSim { get; }
    public string Overrides { get; }
    public string Baseline { get; }

    /// <summary>Folders the editor shows, as in BMS Data\Sim (Acdata comes later).</summary>
    public static readonly string[] Folders = { "Misdata", "Radar", "Vehdef", "Bombdata" };
    public static readonly string[] Extensions = { ".dat", ".veh", ".lst", ".txt" };

    public SimLibrary(string bmsSim, string overrides)
    {
        BmsSim = bmsSim;
        Overrides = overrides;
        Baseline = overrides.TrimEnd('\\', '/') + "_BMS";
    }

    public sealed record Entry(string Folder, string Rel, string Name, bool InBms, bool Changed);

    /// <summary>The files of a folder: BMS's, plus the ones only the project has; Changed = the project has its own copy.</summary>
    public List<Entry> List(string folder)
    {
        var res = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        string b = Path.Combine(BmsSim, folder), o = Path.Combine(Overrides, folder);
        if (Directory.Exists(b))
            foreach (var f in Directory.EnumerateFiles(b).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                string rel = Path.Combine(folder, Path.GetFileName(f));
                res[rel] = new Entry(folder, rel, Path.GetFileName(f), true, File.Exists(Path.Combine(Overrides, rel)));
            }
        if (Directory.Exists(o))
            foreach (var f in Directory.EnumerateFiles(o).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                string rel = Path.Combine(folder, Path.GetFileName(f));
                if (!res.ContainsKey(rel)) res[rel] = new Entry(folder, rel, Path.GetFileName(f), false, true);
            }
        return res.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string BmsPath(string rel) => Path.Combine(BmsSim, rel);
    public string OverridePath(string rel) => Path.Combine(Overrides, rel);
    public string BaselinePath(string rel) => Path.Combine(Baseline, rel);

    /// <summary>The file the editor shows: the project's copy when there is one, else BMS's.</summary>
    public string Current(string rel) => File.Exists(OverridePath(rel)) ? OverridePath(rel) : BmsPath(rel);

    /// <summary>Saves the project's copy; the first time, it also keeps the BMS file as it is now (SimOverrides_BMS).</summary>
    public void SaveOverride(string rel, SimTextFile file)
    {
        if (!File.Exists(BaselinePath(rel)) && File.Exists(BmsPath(rel)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath(rel))!);
            File.Copy(BmsPath(rel), BaselinePath(rel), true);
        }
        file.Save(OverridePath(rel));
    }

    /// <summary>
    /// Back to BMS's file: the project's copy is moved to SimOverrides_Removed\&lt;time&gt; (not deleted), its baseline goes too.
    /// Returns where the copy went.
    /// </summary>
    public string? Reset(string rel)
    {
        if (!File.Exists(OverridePath(rel))) return null;
        string bin = Path.Combine(Overrides.TrimEnd('\\', '/') + "_Removed", DateTime.Now.ToString("yyyyMMdd_HHmmss"), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.Move(OverridePath(rel), bin, true);
        if (File.Exists(BaselinePath(rel))) File.Delete(BaselinePath(rel));
        return bin;
    }
}
