using System.Text.RegularExpressions;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Sim;

/// <summary>
/// The Sim files and the database changes of a theater installed in BMS (Sim Editor without a TerrainTool project): the
/// edited files (the work folder's SimOverrides) go into the theater's own Sim folder (simdatadir of its .tdf; made the
/// first time as a copy of BMS Data\Sim) and DbEdits.txt into its database. Every file replaced or added is listed in a
/// backup (manifest.txt, as the 4.37 Units Converter's), so Restore puts the theater back. BMS's own Data\Sim, its
/// TerrData\TheaterDefinition and the database of BMS's own theaters are never written.
/// </summary>
public static class TheaterSim
{
    /// <summary>The theater's Sim folder (simdatadir of its .tdf), else BMS Data\Sim.</summary>
    public static string SimDir(BmsTheater t)
    {
        string sim = Path.Combine(t.DataDir, "Sim");
        try
        {
            foreach (var raw in File.ReadAllLines(t.Tdf))
            {
                var l = raw.Trim();
                if (!l.StartsWith("simdatadir", StringComparison.OrdinalIgnoreCase)) continue;
                string v = l["simdatadir".Length..].Trim();
                if (v.Length > 0) sim = Path.GetFullPath(Path.Combine(t.DataDir, v.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { }
        return sim;
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The theater has a Sim folder of its own (not BMS's Data\Sim).</summary>
    public static bool HasOwnSim(BmsTheater t) => !Same(SimDir(t), Path.Combine(t.DataDir, "Sim"));

    /// <summary>Why the theater's Sim or database cannot be written (null = it can): BMS's own theaters stay as they are.</summary>
    public static string? Problem(BmsTheater t) =>
        t.AddOnFolder == "BMS" ? "it is one of BMS's own theaters: its files are not changed (use an add-on theater)" : null;

    /// <summary>Where the theater's own Sim goes when it has none yet: &lt;add-on&gt;\Sim.</summary>
    public static string NewSimDir(BmsTheater t) => Path.Combine(TheaterWorkspace.AddOnRoot(t), "Sim");

    /// <summary>The files of a SimOverrides folder that go into the theater (not README, not the protected flight models).</summary>
    public static List<string> OverrideFiles(string overrides) =>
        !Directory.Exists(overrides) ? new()
        : Directory.EnumerateFiles(overrides, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(overrides, f))
            .Where(r => !(r.StartsWith("README", StringComparison.OrdinalIgnoreCase) && r.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            .Where(r => !SimLibrary.IsProtected(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What Install would do, one line each.</summary>
    public static List<string> Plan(BmsTheater t, string overrides, string? dbEdits)
    {
        var res = new List<string>();
        var files = OverrideFiles(overrides);
        if (files.Count > 0)
        {
            if (!HasOwnSim(t)) res.Add($"• the theater gets its own Sim folder: {NewSimDir(t)} = a copy of BMS Data\\Sim, and 'simdatadir' in its .tdf ({t.Tdf})");
            res.Add($"• {files.Count} Sim file(s) into {(HasOwnSim(t) ? SimDir(t) : NewSimDir(t))}: {string.Join(", ", files.Take(10))}{(files.Count > 10 ? " …" : "")}");
        }
        if (dbEdits != null) res.Add($"• the database changes (DbEdits.txt) into {t.ObjectDir} (BMS builds its FALCON4.* again)");
        return res;
    }

    /// <summary>
    /// Installs the Sim files and the database changes into the theater (BMS closed). backupDir gets manifest.txt (added /
    /// replaced files) and the replaced files. Returns the log.
    /// </summary>
    public static List<string> Install(BmsTheater t, string overrides, string? dbEdits, string backupDir, string workDir)
    {
        if (Problem(t) is { } why) throw new InvalidOperationException("Not installed: " + why);
        if (Dds.BmsInstaller.IsBmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        var log = new List<string>();
        var manifest = new List<string>();
        Directory.CreateDirectory(backupDir);
        int k = 0;
        void Write(string target, Action<string> write)
        {
            if (File.Exists(target))
            {
                string b = $"file_{k++:00000}_{Path.GetFileName(target)}";
                File.Copy(target, Path.Combine(backupDir, b), true);
                manifest.Add("replaced;" + target + ";" + b);
            }
            else manifest.Add("added;" + target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            write(target);
        }

        var files = OverrideFiles(overrides);
        if (files.Count > 0)
        {
            string sim = SimDir(t);
            if (!HasOwnSim(t))
            {
                // the theater's own Sim: a full copy of BMS's, then simdatadir in its .tdf
                string bmsSim = Path.Combine(t.DataDir, "Sim");
                sim = NewSimDir(t);
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(bmsSim, "*", SearchOption.AllDirectories))
                {
                    string to = Path.Combine(sim, Path.GetRelativePath(bmsSim, f));
                    if (File.Exists(to)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to);
                    manifest.Add("added;" + to);
                    n++;
                }
                string rel = Path.GetRelativePath(t.DataDir, sim).Replace('/', '\\');
                string? tdf = Dds.TheaterSimData.SetSimDataDir(File.ReadAllText(t.Tdf), rel);
                if (tdf != null) Write(t.Tdf, f => File.WriteAllText(f, tdf));
                log.Add($"Theater Sim: {n} files of BMS Data\\Sim copied into {sim}; 'simdatadir {rel}' in {Path.GetFileName(t.Tdf)}");
            }
            int c = 0;
            foreach (var rel in files)
            {
                string from = Path.Combine(overrides, rel), to = Path.Combine(sim, rel);
                if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(from))) continue;
                Write(to, f => File.Copy(from, f, true));
                c++;
            }
            log.Add(c == 0 ? "Theater Sim: already up to date" : $"Theater Sim: {c} file(s) written into {sim}");
        }

        if (dbEdits != null && File.Exists(dbEdits))
        {
            // the changes on a copy of the theater's tables, then the tables that changed back in place
            string tmp = Path.Combine(workDir, "dbedits_tables");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            foreach (var f in Directory.EnumerateFiles(t.ObjectDir, "Falcon4_*.xml")) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            var notes = Dds.TheaterSimData.ApplyDbEdits(tmp, dbEdits, again: true);
            int c = 0;
            foreach (var f in Directory.EnumerateFiles(tmp, "Falcon4_*.xml"))
            {
                string to = Path.Combine(t.ObjectDir, Path.GetFileName(f));
                if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(f))) continue;
                Write(to, x => File.Copy(f, x, true));
                c++;
            }
            log.AddRange(notes);
            if (c > 0)
            {
                foreach (var f in Directory.EnumerateFiles(t.ObjectDir, "FALCON4.*").Where(f => !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
                    try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Add($"{Path.GetFileName(f)}: NOT removed – {ex.Message}"); }
                manifest.Insert(0, "objects;" + t.ObjectDir);
            }
            log.Add(c == 0 ? "Database: already up to date" : $"Database: {c} table(s) written into {t.ObjectDir}; FALCON4.* removed (BMS builds them again)");
        }

        File.WriteAllLines(Path.Combine(backupDir, "manifest.txt"), manifest);
        File.WriteAllLines(Path.Combine(backupDir, "report.txt"), new[] { $"Sim Editor: Sim files and database changes into {t.Name}" }.Concat(log));
        log.Add("Backup: " + backupDir + " (Restore puts the theater back)");
        return log;
    }

    /// <summary>Backups of the theater's installs, newest first (Restore).</summary>
    public static List<(string Dir, DateTime Time, string Summary)> Backups(string backupRoot) =>
        Database.Interchange.Backups(backupRoot).Where(b => !b.Dir.EndsWith("_restored", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Puts the theater back as before an install: removes the files it added, puts back the ones it replaced.</summary>
    public static List<string> Restore(string backupDir)
    {
        if (Dds.BmsInstaller.IsBmsRunning()) throw new InvalidOperationException("BMS is running: close it first.");
        return Database.Interchange.Restore(backupDir);
    }

    /// <summary>The installed theater of a work folder (its .tdf), or null.</summary>
    public static BmsTheater? TheaterOf(Project.TheaterProject p)
    {
        if (!p.IsInstalledTheater || string.IsNullOrWhiteSpace(p.BmsDataDir)) return null;
        try { return BmsTheater.List(p.BmsDataDir).FirstOrDefault(t => string.Equals(Norm(t.Tdf), Norm(p.InstalledTheaterTdf), StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
