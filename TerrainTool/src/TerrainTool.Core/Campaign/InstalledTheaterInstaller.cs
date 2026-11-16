using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// "Install into BMS" of Objective Studio for a theater installed in BMS: copies into the theater's own folders only the files of the
/// build (Output\Objects → objects folder of the .tdf, Output\Campaign → campaign folder) that are new or different. Every file
/// replaced is saved first in Output\BmsBackup\&lt;date&gt;\Objects|Campaign, and can be put back with Restore. The cache files BMS
/// rebuilds (FALCON4.*, terrain leveling, campaign terrain) are removed when the objects change.
/// Objects folders shared with other theaters (the BMS one, Data\Terrdata\objects, or another theater's) are never written.
/// </summary>
public sealed class InstalledTheaterInstaller
{
    private readonly TheaterProject _p;
    public List<string> Problems { get; } = new();
    /// <summary>(build folder, BMS folder, relative path) of every file to copy.</summary>
    public List<(string Part, string From, string To)> Changed { get; } = new();
    public string BackupDir { get; }
    private readonly List<(string Part, string Src, string Dst)> _parts = new();

    public InstalledTheaterInstaller(TheaterProject p)
    {
        _p = p;
        BackupDir = Path.Combine(BackupsFolder(p), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        if (!p.IsInstalledTheater) { Problems.Add("Not a theater installed in BMS."); return; }
        string objOut = ObjectsJob.InstalledObjectsOut(p), campOut = ObjectsJob.InstalledCampaignOut(p);
        if (!Directory.Exists(objOut)) { Problems.Add("Nothing built yet: Build objects first."); return; }
        if (BmsInstaller.IsBmsRunning()) Problems.Add("Falcon BMS is running: close it first.");
        _parts.Add(("Objects", objOut, p.SourceObjectsDir));
        if (Directory.Exists(campOut)) _parts.Add(("Campaign", campOut, p.SourceCampaignDir));
        foreach (var (part, src, dst) in _parts)
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, f);
                var t = new FileInfo(Path.Combine(dst, rel));
                var s = new FileInfo(f);
                if (!t.Exists || t.Length != s.Length || (t.LastWriteTimeUtc != s.LastWriteTimeUtc && !SameBytes(f, t.FullName)))
                    Changed.Add((part, f, t.FullName));
            }
        if (ObjectsChanged && SharedWith() is string other)
            Problems.Add($"The objects folder of this theater ({p.SourceObjectsDir}) is shared with {other}: installing the edits would change that too. " +
                         "Objective Studio does not write into shared objects folders.");
    }

    public static string BackupsFolder(TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "BmsBackup");
    private const string EditsPart = "Edits";

    /// <summary>The edit files of the work folder (objective editor).</summary>
    public static IEnumerable<string> EditFiles(TheaterProject p) => new[]
    {
        Editor.ObjectiveEdits.PathFor(p.ProjectFolder), Path.Combine(p.ProjectFolder, Editor.BaseSwaps.FileName), Path.Combine(p.ProjectFolder, Editor.LayoutSwaps.FileName),
        Editor.PieceEdits.PathFor(p.ProjectFolder), Editor.PointEdits.PathFor(p.ProjectFolder), Editor.BridgePlan.PathFor(p.ProjectFolder),
    };

    private bool ObjectsChanged => Changed.Any(c => c.Part == "Objects");
    private bool LevelingChanged => Changed.Any(c => c.Part == "Objects" && c.To.Contains(@"\TerrainRelatedData\", StringComparison.OrdinalIgnoreCase));

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        if (fa.Length > 64 << 20) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Another theater (or BMS itself) that uses the same objects folder, or null.</summary>
    private string? SharedWith()
    {
        string mine = Norm(_p.SourceObjectsDir);
        if (Directory.Exists(_p.BmsDataDir) && mine == Norm(Path.Combine(_p.BmsDataDir, "Terrdata", "objects")))
            return "BMS (the Korea and every theater without its own objects)";
        try
        {
            foreach (var t in BmsTheater.List(_p.BmsDataDir))
                if (!t.Tdf.Equals(_p.InstalledTheaterTdf, StringComparison.OrdinalIgnoreCase) && Norm(t.ObjectDir) == mine) return "the theater " + t.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    private static string Norm(string d) => Path.GetFullPath(d).TrimEnd('\\', '/').ToUpperInvariant();

    public string Plan()
    {
        var l = new List<string>();
        if (Changed.Count == 0) l.Add("• BMS already has all the files of the build: nothing to copy.");
        foreach (var g in Changed.GroupBy(c => c.Part))
        {
            var dst = _parts.First(x => x.Part == g.Key).Dst;
            l.Add($"• {g.Key}: {g.Count()} files copied into {dst}");
            foreach (var c in g.Take(12)) l.Add("     " + Path.GetRelativePath(dst, c.To));
            if (g.Count() > 12) l.Add($"     … and {g.Count() - 12} more");
        }
        if (Changed.Count > 0) l.Add($"• Every file replaced is saved first in {BackupDir} ('Restore a backup' puts it back).");
        if (ObjectsChanged) l.Add("• The cache files BMS rebuilds at the next start are removed (FALCON4.*" + (LevelingChanged ? ", terrain leveling" : "") + ", campaign terrain).");
        l.Add("");
        l.Add("Nothing else in BMS is touched.");
        return string.Join("\n", l);
    }

    /// <summary>Writes are allowed only inside the theater's objects and campaign folders (and its NewTerrain\HeightMaps caches).</summary>
    private void Check(string path)
    {
        string full = Norm(path);
        bool ok = _parts.Any(x => full.StartsWith(Norm(x.Dst) + Path.DirectorySeparatorChar)) ||
                  (_p.NewTerrainFolder.Length > 0 && full.StartsWith(Norm(Path.Combine(_p.NewTerrainFolder, "HeightMaps")) + Path.DirectorySeparatorChar));
        if (!ok) throw new InvalidOperationException("Write outside the theater blocked: " + path);
    }

    public void Run(Action<string> log, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Problems.Count > 0) throw new InvalidOperationException(string.Join("\n", Problems));
        if (ObjectsChanged) ClearCaches(log);
        int k = 0;
        foreach (var (part, from, to) in Changed)
        {
            ct.ThrowIfCancellationRequested();
            Check(to);
            if (File.Exists(to))
            {
                var dst = _parts.First(x => x.Part == part).Dst;
                string b = Path.Combine(BackupDir, part, Path.GetRelativePath(dst, to));
                Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                File.Copy(to, b, true);
                File.SetAttributes(to, FileAttributes.Normal);
                File.Delete(to);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, false);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            progress?.Report((double)++k / Changed.Count);
            if (k <= 40 || k % 200 == 0) log("  Copied " + to);
        }
        log(Changed.Count == 0 ? "  Already up to date." : $"  {Changed.Count} files installed" + (Directory.Exists(BackupDir) ? $"; replaced files saved in {BackupDir}" : ""));
        // the theater in BMS now has the edits: they are put aside with the backup, so the next edits start from the installed theater
        // (pieces removed or Korean bases would otherwise be applied a second time)
        if (Changed.Count > 0)
        {
            int moved = 0;
            foreach (var f in EditFiles(_p).Where(File.Exists))
            {
                string to = Path.Combine(BackupDir, EditsPart, Path.GetFileName(f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(f, to, true);
                moved++;
            }
            if (moved > 0) log($"  The {moved} edit files are now part of the theater in BMS: kept with the backup, the editor starts from the installed theater");
        }
        log("  Start BMS and choose the theater: the first start is slower (BMS rebuilds its cache files).");
    }

    private void ClearCaches(Action<string> log)
    {
        foreach (var f in Directory.EnumerateFiles(_p.SourceObjectsDir, "FALCON4.*").Where(f => Path.GetFileName(f).StartsWith("FALCON4.", StringComparison.Ordinal)).ToList())
        { Check(f); File.Delete(f); log($"  Removed {Path.GetFileName(f)} (BMS rebuilds it from the XML files)"); }
        if (Directory.Exists(_p.SourceCampaignDir) && _parts.Any(x => x.Part == "Campaign"))
            foreach (var f in Directory.EnumerateFiles(_p.SourceCampaignDir, "*.terrain.binpb").Concat(Directory.EnumerateFiles(_p.SourceCampaignDir, "*.terrain.txtpb")).ToList())
            { Check(f); File.Delete(f); log($"  Removed cache {Path.GetFileName(f)}"); }
        if (LevelingChanged && _p.NewTerrainFolder.Length > 0)
            foreach (var c in BmsInstaller.HeightMapCaches)
                if (Path.Combine(_p.NewTerrainFolder, "HeightMaps", c) is var f && File.Exists(f)) { Check(f); File.Delete(f); log($"  Removed cache {c}"); }
    }

    /// <summary>Backups made by the installs, newest first.</summary>
    public static List<(string Dir, DateTime When, int Files)> Backups(TheaterProject p)
    {
        string root = BackupsFolder(p);
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateDirectories(root)
            .Select(d => (d, Directory.GetCreationTime(d), Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count()))
            .OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>Puts back the files of a backup (Objects → objects folder, Campaign → campaign folder) and removes the caches.</summary>
    public static List<string> Restore(TheaterProject p, string backup, bool bringBackEdits = false)
    {
        if (BmsInstaller.IsBmsRunning()) throw new InvalidOperationException("Falcon BMS is running: close it first.");
        var log = new List<string>();
        var map = new[] { ("Objects", p.SourceObjectsDir), ("Campaign", p.SourceCampaignDir) };
        int n = 0;
        foreach (var (part, dst) in map)
        {
            string src = Path.Combine(backup, part);
            if (!Directory.Exists(src)) continue;
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(dst, Path.GetRelativePath(src, f));
                if (!Norm(to).StartsWith(Norm(dst) + Path.DirectorySeparatorChar)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
                File.Copy(f, to, true);
                n++;
            }
        }
        foreach (var f in Directory.EnumerateFiles(p.SourceObjectsDir, "FALCON4.*").ToList()) File.Delete(f);
        if (p.NewTerrainFolder.Length > 0)
            foreach (var c in BmsInstaller.HeightMapCaches)
                if (Path.Combine(p.NewTerrainFolder, "HeightMaps", c) is var f && File.Exists(f)) File.Delete(f);
        log.Add($"{n} files put back from {backup}. The cache files will be rebuilt by BMS at the next start.");
        // the edits of that install come back into the work folder (unless there are newer ones)
        string ed = Path.Combine(backup, EditsPart);
        if (bringBackEdits && Directory.Exists(ed))
        {
            foreach (var f in Directory.EnumerateFiles(ed)) File.Copy(f, Path.Combine(p.ProjectFolder, Path.GetFileName(f)), true);
            log.Add("The edits of that install are back in the work folder: you can change them and install again.");
        }
        else
        {
            // the editor starts again from the theater as it is in BMS now
            foreach (var f in EditFiles(p).Where(File.Exists)) File.Delete(f);
            log.Add("The editor starts again from the theater as it is in BMS (the edits of that install stay in the backup).");
        }
        log.Add("Files added by the install (new layouts) are left in place: they are not used once the database is back as it was.");
        // the last build no longer matches BMS: removed, so that it cannot be installed again by mistake
        foreach (var d in new[] { ObjectsJob.InstalledObjectsOut(p), ObjectsJob.InstalledCampaignOut(p) })
            if (Directory.Exists(d)) Directory.Delete(d, true);
        foreach (var f in new[] { ObjectChangeSet.PathFor(p), Path.Combine(p.ProjectFolder, "Output", "Build_log.txt"), Path.Combine(p.ProjectFolder, "Output", "Install_log.txt") })
            if (File.Exists(f)) File.Delete(f);
        return log;
    }
}
