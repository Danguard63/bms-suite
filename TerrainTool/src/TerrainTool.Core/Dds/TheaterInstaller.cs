using System.Text;
using TerrainTool.Core.Project;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Installazione completa del teatro costruito (Output\Install\&lt;add-on&gt;) in BMS, i passi di INSTALL.txt fatti dal tool:
///   1. se l'add-on non c'e' in BMS, si copia quello vecchio (OldTheaterFolder);
///   2. si copiano sopra i file nuovi o cambiati della costruzione (quelli sostituiti vanno prima in Output\BmsBackup\&lt;data&gt;);
///   3. se terreno o oggetti cambiano, si tolgono le cache che BMS rifa' (come fa BmsInstaller);
///   4. si aggiunge la riga del .tdf in theater.lst (dopo una copia di sicurezza).
/// Scrive solo dentro Data\&lt;add-on&gt; e in theater.lst; mai nella Korea o nel resto di BMS.
/// Le copie di sicurezza: i file dell'add-on con il loro percorso relativo all'add-on, quelli di Data sotto "_Data\".
/// </summary>
public sealed class TheaterInstaller
{
    private readonly TheaterProject _p;
    public string DataDir { get; } = "";
    public string AddOn { get; }
    public string Target { get; }
    public string Source { get; }
    public string BackupDir { get; }
    public string TheaterLst => Path.Combine(DataDir, "TerrData", "TheaterDefinition", "theater.lst");
    /// <summary>La riga di theater.lst (es. "Add-On Mideast128\Terrdata\theaterdefinition\Mideast128_NT.tdf").</summary>
    public string? LstLine { get; }
    public List<string> Problems { get; } = new();
    /// <summary>File della costruzione nuovi o diversi da quelli in BMS (relativi all'add-on).</summary>
    public List<string> Changed { get; } = new();
    public bool CopyOldAddOn { get; }
    public bool Listed { get; }

    public const string DataBackup = "_Data";

    public TheaterInstaller(TheaterProject p)
    {
        _p = p;
        AddOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        var d = new DirectoryInfo(p.KoreaNewTerrain);
        while (d != null && !d.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
        DataDir = d?.FullName ?? "";
        Target = Path.Combine(DataDir, AddOn);
        Source = Path.Combine(p.ProjectFolder, "Output", "Install", AddOn);
        BackupDir = Path.Combine(BackupsFolder(p), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        if (d == null) { Problems.Add($"BMS Data folder not found above {p.KoreaNewTerrain} (tab 1: BMS Korea NewTerrain folder)"); return; }
        if (!Directory.Exists(Source)) { Problems.Add($"{Source} does not exist: build the theater first (tab 8)"); return; }
        string? tdf = Directory.EnumerateFiles(Source, "*_NT.tdf", SearchOption.AllDirectories).FirstOrDefault();
        if (tdf == null) Problems.Add("The build has no *_NT.tdf: build the theater again (tab 8)");
        else LstLine = AddOn + "\\" + Path.GetRelativePath(Source, tdf).Replace('/', '\\');
        CopyOldAddOn = !Directory.Exists(Target);
        if (CopyOldAddOn && !Directory.Exists(p.OldTheaterFolder)) Problems.Add($"{Target} is not in BMS and the old add-on {p.OldTheaterFolder} was not found");
        if (BmsInstaller.IsBmsRunning()) Problems.Add("Falcon BMS is running: close it first");
        Listed = LstLine != null && File.Exists(TheaterLst) && ReadLst().Any(l => Same(l, LstLine));
        foreach (var f in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Source, f);
            var t = new FileInfo(Path.Combine(Target, rel));
            var s = new FileInfo(f);
            if (!t.Exists || t.Length != s.Length || t.LastWriteTimeUtc != s.LastWriteTimeUtc) Changed.Add(rel);
        }
    }

    public static string BackupsFolder(TheaterProject p) => Path.Combine(p.ProjectFolder, "Output", "BmsBackup");

    private static bool Same(string a, string b) => a.Trim().Trim('"').Equals(b.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    private List<string> ReadLst() => File.Exists(TheaterLst) ? File.ReadAllLines(TheaterLst, Encoding.Latin1).ToList() : new List<string>();

    private bool TerrainChanged => Changed.Any(r => r.Contains(@"\NewTerrain\HeightMaps\", StringComparison.OrdinalIgnoreCase));
    private bool ObjectsChanged => Changed.Any(r => r.StartsWith(@"Terrdata\" + ObjectsJob.FolderName + "\\", StringComparison.OrdinalIgnoreCase));

    public string Plan()
    {
        var l = new List<string>();
        if (CopyOldAddOn) l.Add($"• {AddOn} is not in BMS yet: first the old add-on is copied ({_p.OldTheaterFolder} → {Target}).");
        long bytes = Changed.Sum(r => new FileInfo(Path.Combine(Source, r)).Length);
        l.Add(Changed.Count == 0 ? "• The theater in BMS already has all the files of the build." :
            $"• {Changed.Count} files of the build are new or changed ({bytes / 1048576.0:0} MB) and are copied into {Target}." +
            "\n   The files they replace are saved first in " + BackupDir + ".");
        foreach (var g in Changed.GroupBy(r => r.Split('\\').Take(2).Aggregate((a, b) => a + "\\" + b)).OrderByDescending(g => g.Count()).Take(8))
            l.Add($"     {g.Key}: {g.Count()}");
        if (TerrainChanged || ObjectsChanged) l.Add("• Terrain or objects change: the cache files BMS rebuilds at the next start (terrain leveling, campaign terrain) are removed.");
        if (LstLine != null) l.Add(Listed ? "• theater.lst already lists the theater." : $"• theater.lst gets the line {LstLine} (a copy of the old file is saved first).");
        l.Add("");
        l.Add("Nothing else in BMS is touched (the Korea, Data\\Art, Data\\Sim and the other theaters stay as they are).");
        return string.Join("\n", l);
    }

    private void Check(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !full.Equals(Path.GetFullPath(TheaterLst), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Write outside the theater blocked: " + full);
        string korea = Path.GetFullPath(_p.KoreaNewTerrain).TrimEnd('\\', '/');
        if (full.StartsWith(korea, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Write into the Korea blocked: " + full);
    }

    private void Backup(string file, string rel)
    {
        if (!File.Exists(file)) return;
        string b = Path.Combine(BackupDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Copy(file, b, true);
    }

    public void Run(Action<string> log, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Problems.Count > 0) throw new InvalidOperationException(string.Join("\n", Problems));
        if (CopyOldAddOn)
        {
            log($"  Copying the old add-on {_p.OldTheaterFolder} → {Target}");
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(_p.OldTheaterFolder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string to = Path.Combine(Target, Path.GetRelativePath(_p.OldTheaterFolder, f));
                Check(to);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, true);
                n++;
            }
            log($"  {n} files of the old add-on copied");
        }
        // le cache vanno tolte prima di copiare (BMS le rifa' con il terreno e gli oggetti nuovi)
        if (TerrainChanged || ObjectsChanged) ClearCaches(log);
        long done = 0, total = Math.Max(1, Changed.Sum(r => new FileInfo(Path.Combine(Source, r)).Length));
        int k = 0;
        foreach (var rel in Changed)
        {
            ct.ThrowIfCancellationRequested();
            string from = Path.Combine(Source, rel), to = Path.Combine(Target, rel);
            Check(to);
            if (File.Exists(to))
            {
                Backup(to, rel);
                File.SetAttributes(to, FileAttributes.Normal);
                File.Delete(to);      // file nuovo: non si scrive dentro un eventuale collegamento (hard link) ai file del progetto
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, false);
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            done += new FileInfo(from).Length; k++;
            progress?.Report((double)done / total);
            if (k <= 40 || k % 200 == 0) log($"  Copied {rel}");
        }
        log(Changed.Count == 0 ? "  Files: already up to date in BMS" : $"  {Changed.Count} files copied into {Target}" + (Directory.Exists(BackupDir) ? $" (replaced files saved in {BackupDir})" : ""));
        if (LstLine != null && !Listed) AddToLst(log);
        log("  Done. Start BMS, choose the theater in the Launcher: the first start is slow (BMS rebuilds its cache files).");
    }

    private void ClearCaches(Action<string> log)
    {
        string camp = Path.Combine(Target, "Campaign");
        if (Directory.Exists(camp))
            foreach (var f in Directory.EnumerateFiles(camp, "*.terrain.binpb").Concat(Directory.EnumerateFiles(camp, "*.terrain.txtpb")).ToList())
            {
                // una copia per il lavoro Objects (celle dei ponti) finche' BMS non la rifa'
                string keep = ObjectsJob.KeptTerrainCacheFolder(_p);
                Directory.CreateDirectory(keep);
                File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), true);
                Check(f); File.Delete(f);
                log($"  Removed cache {Path.GetFileName(f)} (a copy kept in {keep})");
            }
        foreach (var nt in Directory.Exists(Target) ? Directory.EnumerateDirectories(Target, "NewTerrain", SearchOption.AllDirectories).ToList() : new())
            foreach (var c in BmsInstaller.HeightMapCaches)
                if (Path.Combine(nt, "HeightMaps", c) is var f && File.Exists(f)) { Check(f); File.Delete(f); log($"  Removed cache {c}"); }
        // line-of-sight maps that BMS made from an older heightmap: it makes them again when they are missing
        foreach (var nt in Directory.Exists(Target) ? Directory.EnumerateDirectories(Target, "NewTerrain", SearchOption.AllDirectories).ToList() : new())
        {
            string hm = Path.Combine(nt, "HeightMaps", "HeightMap.raw");
            if (!File.Exists(hm)) continue;
            foreach (var c in BmsInstaller.LosCaches)
                if (Path.Combine(nt, "HeightMaps", c) is var f && File.Exists(f) && File.GetLastWriteTimeUtc(f) < File.GetLastWriteTimeUtc(hm))
                { Check(f); File.Delete(f); log($"  Removed {c} (made by BMS from an older heightmap; it makes it again)"); }
        }
        string obj = Path.Combine(Target, "Terrdata", ObjectsJob.FolderName);
        if (ObjectsChanged && Directory.Exists(obj))
            foreach (var f in Directory.EnumerateFiles(obj, "FALCON4.*").Where(f => Path.GetFileName(f).StartsWith("FALCON4.", StringComparison.Ordinal)).ToList())
            { Check(f); File.Delete(f); log($"  Removed {Path.GetFileName(f)} (BMS rebuilds it from the XML files)"); }
    }

    private void AddToLst(Action<string> log)
    {
        Check(TheaterLst);
        var lines = ReadLst();
        if (File.Exists(TheaterLst)) Backup(TheaterLst, Path.Combine(DataBackup, "TerrData", "TheaterDefinition", "theater.lst"));
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(LstLine!);
        Directory.CreateDirectory(Path.GetDirectoryName(TheaterLst)!);
        File.WriteAllText(TheaterLst, string.Join("\r\n", lines) + "\r\n", Encoding.Latin1);
        log($"  theater.lst: added {LstLine}");
    }

    /// <summary>Toglie il teatro dall'elenco di BMS (theater.lst, dopo una copia): i file restano, il teatro non compare piu' nel Launcher.</summary>
    public string RemoveFromLst()
    {
        if (LstLine == null || !Listed) return "The theater is not in theater.lst.";
        if (BmsInstaller.IsBmsRunning()) throw new InvalidOperationException("Falcon BMS is running: close it first");
        Check(TheaterLst);
        Backup(TheaterLst, Path.Combine(DataBackup, "TerrData", "TheaterDefinition", "theater.lst"));
        var lines = ReadLst().Where(l => !Same(l, LstLine)).ToList();
        File.WriteAllText(TheaterLst, string.Join("\r\n", lines) + "\r\n", Encoding.Latin1);
        return $"Removed {LstLine} from theater.lst (old file saved in {BackupDir}). The theater's files are still in {Target}.";
    }

    /// <summary>Le copie di sicurezza fatte dalle installazioni, dalla piu' recente.</summary>
    public static List<(string Dir, DateTime When, int Files)> Backups(TheaterProject p)
    {
        string root = BackupsFolder(p);
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateDirectories(root).Select(d => (d, Directory.GetCreationTime(d), Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Count()))
            .Where(x => x.Item3 > 0).OrderByDescending(x => x.d).ToList();
    }

    /// <summary>Rimette in BMS i file di una copia di sicurezza (quelli che l'installazione aveva sostituito).</summary>
    public List<string> Restore(string backup, Action<string> log)
    {
        if (BmsInstaller.IsBmsRunning()) throw new InvalidOperationException("Falcon BMS is running: close it first");
        var done = new List<string>();
        foreach (var f in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(backup, f);
            string to;
            if (rel.StartsWith(DataBackup + "\\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(DataBackup + "/", StringComparison.OrdinalIgnoreCase))
                to = Path.Combine(DataDir, rel[(DataBackup.Length + 1)..]);
            else if (!rel.Contains('\\') && !rel.Contains('/') && rel.EndsWith(".tdf", StringComparison.OrdinalIgnoreCase))
            {
                // le copie del .tdf fatte da "Install theater Sim" sono nella radice della copia
                var def = Directory.Exists(Target) ? Directory.EnumerateDirectories(Target, "theaterdefinition", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true, MaxRecursionDepth = 2 }).FirstOrDefault() : null;
                if (def == null) { log("  skipped " + rel + " (theaterdefinition folder not found)"); continue; }
                to = Path.Combine(def, rel);
            }
            else to = Path.Combine(Target, rel);
            Check(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) { File.SetAttributes(to, FileAttributes.Normal); File.Delete(to); }
            File.Copy(f, to, false);
            done.Add(rel);
        }
        log($"  Restored {done.Count} files from {backup}");
        return done;
    }
}
