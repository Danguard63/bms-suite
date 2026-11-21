using TerrainTool.Core.Project;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// The campaign files of a TerrainTool project (theater conversion), from any program of the suite: the object changes of
/// Run Objects and of the objective network written into the campaign files (Output\Install\&lt;add-on&gt;\Campaign), and those
/// files copied into the add-on's Campaign folder in BMS, each replaced file saved first. So the program that changes the
/// objectives also brings the campaign up to date, without going through the Campaign Editor.
/// </summary>
public static class ProjectCampaign
{
    /// <summary>&lt;BMS&gt;\Data\&lt;add-on&gt;\Campaign (the Data folder above the project's Korea), or null.</summary>
    public static string? TargetCampaign(TheaterProject p)
    {
        if (string.IsNullOrWhiteSpace(p.OldTheaterFolder) || string.IsNullOrWhiteSpace(p.KoreaNewTerrain)) return null;
        var d = new DirectoryInfo(p.KoreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        if (d == null) return null;
        string t = Path.Combine(d.FullName, new DirectoryInfo(p.OldTheaterFolder).Name, "Campaign");
        return Directory.Exists(t) ? t : null;
    }

    /// <summary>Output\Install\&lt;add-on&gt;\Campaign of the project.</summary>
    public static string InstallCopy(TheaterProject p) =>
        Path.Combine(p.ProjectFolder, "Output", "Install", new DirectoryInfo(p.OldTheaterFolder).Name, "Campaign");

    /// <summary>Files of src (not in subfolders, not the tool's notes) missing in target or with other contents.</summary>
    public static List<string> Changed(string src, string target)
    {
        var res = new List<string>();
        if (!Directory.Exists(src)) return res;
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

    private static string Marker(TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "ObjectChanges_applied.txt");

    /// <summary>The object changes (Run Objects) or network edits are newer than the last time they went into the campaign files.</summary>
    public static bool HasPending(TheaterProject p)
    {
        DateTime applied = File.Exists(Marker(p)) ? File.GetLastWriteTimeUtc(Marker(p)) : DateTime.MinValue;
        foreach (var f in new[] { ObjectChangeSet.PathFor(p), NetworkEdits.PathFor(p) })
            if (File.Exists(f) && File.GetLastWriteTimeUtc(f) > applied) return true;
        return false;
    }

    /// <summary>Remembers that the object changes went into the campaign files (also when the Campaign Editor applied them).</summary>
    public static void MarkApplied(TheaterProject p)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker(p))!);
        File.WriteAllText(Marker(p), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>
    /// Writes the object changes and network edits into the campaign files (Output\Install; the Campaign Editor's saves in
    /// CampaignEdits are updated too, their versions of before saved in backupDir\CampaignEdits). Returns the notes.
    /// </summary>
    public static List<string> ApplyPending(TheaterProject p, string backupDir)
    {
        var set = ObjectChangeSet.Load(ObjectChangeSet.PathFor(p));
        var net = NetworkEdits.Load(NetworkEdits.PathFor(p));
        if (set == null && net.IsEmpty) return new() { "Campaign: no object changes to write" };
        string edits = Dds.ObjectsJob.CampaignEditsFolder(p);
        if (Directory.Exists(edits))
            foreach (var x in Directory.EnumerateFiles(edits).Where(x => x.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)))
            {
                string b = Path.Combine(backupDir, "CampaignEdits", Path.GetFileName(x));
                Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                File.Copy(x, b, true);
            }
        var notes = ObjectChangeApplier.Apply(set ?? new ObjectChangeSet(), p, InstallCopy(p), net).Select(n => "Campaign: " + n).ToList();
        MarkApplied(p);
        return notes;
    }

    /// <summary>
    /// Copies the project's campaign files that differ into the add-on's Campaign folder in BMS (BMS closed); each replaced
    /// file goes first into backupDir\Campaign. The aircraft the 4.37 Units Converter added to the TE list stay. Returns the log.
    /// </summary>
    public static List<string> Install(TheaterProject p, string backupDir)
    {
        var log = new List<string>();
        string src = InstallCopy(p);
        string? target = TargetCampaign(p);
        if (target == null) { log.Add("Campaign: the add-on's Campaign folder was not found in BMS: campaign files not installed"); return log; }
        var todo = Changed(src, target);
        if (todo.Count == 0) { log.Add("Campaign: the files in BMS are already up to date"); return log; }
        string objects = Path.Combine(Directory.GetParent(target)!.FullName, "Terrdata", Dds.ObjectsJob.FolderName);
        foreach (var f in todo)
        {
            string dst = Path.Combine(target, Path.GetFileName(f)), from = f;
            if (Path.GetFileName(f).Equals("TePlanes.lst", StringComparison.OrdinalIgnoreCase))
            {
                string merged = Path.Combine(Path.GetTempPath(), "TePlanes_merged.lst");
                if (Database.UnitsConversion.KeepConvertedTeLines(f, dst, objects, merged) > 0) from = merged;
            }
            if (File.Exists(dst))
            {
                string b = Path.Combine(backupDir, "Campaign", Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                File.Copy(dst, b, true);
            }
            File.Copy(from, dst, true);
        }
        log.Add($"Campaign: {todo.Count} file(s) → {target} ({string.Join(", ", todo.Take(8).Select(Path.GetFileName))}{(todo.Count > 8 ? " …" : "")}); replaced files saved in {Path.Combine(backupDir, "Campaign")}");
        return log;
    }
}
