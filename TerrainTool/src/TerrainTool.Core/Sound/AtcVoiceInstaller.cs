using System.Text;
using TerrainTool.Core.Campaign;

namespace TerrainTool.Core.Sound;

/// <summary>
/// Puts a theater's new ATC voice files into BMS. The Sounds folder of a theater (sounddir of its .tdf) replaces Data\Sounds
/// for every sound of BMS (FalconSoundThrDirectory: Falcon.tlk, fragfile, evalFile, commFile, TTS, sound effects), so a theater
/// that has none gets a complete copy of Data\Sounds first (about 100 MB, as the Israel theater ships it), then the changed
/// files. The .tdf gets "sounddir" and "subtitlesdir" lines. Every replaced file is copied into the backup folder first;
/// manifest.txt lists what was added or replaced, and Restore puts it back. BMS's own Data\Sounds and Korea's .tdf are never written.
/// </summary>
public static class AtcVoiceInstaller
{
    public static string DataSounds(BmsTheater t) => Path.GetFullPath(Path.Combine(t.DataDir, "Sounds"));

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    /// <summary>Why the theater cannot get its own voices, or null.</summary>
    public static string? Refusal(BmsTheater t)
    {
        if (t.AddOnFolder == "BMS") return $"{t.Name} is a theater of BMS itself (its .tdf is in Data\\TerrData\\TheaterDefinition): its sounds are BMS's own Data\\Sounds, which are never changed.";
        return null;
    }

    /// <summary>Where the theater's sounds go: its sounddir if it has one, else Data\&lt;add-on&gt;\Sounds.</summary>
    public static string TargetDir(BmsTheater t) =>
        Norm(t.SoundDir) != Norm(DataSounds(t)) ? Norm(t.SoundDir) : Path.Combine(t.DataDir, t.AddOnFolder, "Sounds");

    /// <param name="campaignFiles">Files for the theater's Campaign folder (stations+ils.dat with the new fragments), same backup.</param>
    public static List<string> Install(BmsTheater t, IEnumerable<string> files, string backupRoot, IProgress<string>? log = null, IEnumerable<string>? campaignFiles = null)
    {
        if (Refusal(t) is { } why) throw new InvalidOperationException(why);
        string target = TargetDir(t);
        if (Norm(target) == Norm(DataSounds(t))) throw new InvalidOperationException("Refused: the target is BMS's own Data\\Sounds.");
        var res = new List<string>();
        string bak = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + CampaignWorkspace.SafeName(t.Name));
        Directory.CreateDirectory(bak);
        var manifest = new List<string> { "theater;" + t.Name, "target;" + target };
        int k = 0;
        void Put(string src, string dst)
        {
            if (File.Exists(dst))
            {
                string b = $"file_{k++:00000}_{Path.GetFileName(dst)}";
                File.Copy(dst, Path.Combine(bak, b), true);
                manifest.Add("replaced;" + dst + ";" + b);
            }
            else manifest.Add("added;" + dst);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
        }
        // 1) a complete Sounds folder for the theater
        if (!Directory.Exists(target) || AtcVoices.Find(target, AtcVoices.TlkName) == null)
        {
            string from = DataSounds(t);
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(target, Path.GetRelativePath(from, f));
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst);
                manifest.Add("added;" + dst);
                if (++n % 200 == 0) log?.Report($"copying BMS's sounds: {n} files…");
            }
            res.Add($"{n} files of BMS's Data\\Sounds copied into {target}");
        }
        // 2) the changed voice files (same names as in the target when it has them)
        foreach (var f in files)
        {
            string dst = AtcVoices.Find(target, Path.GetFileName(f)) ?? Path.Combine(target, Path.GetFileName(f));
            Put(f, dst);
            res.Add($"{Path.GetFileName(dst)} written");
        }
        foreach (var f in campaignFiles ?? Enumerable.Empty<string>())
        {
            string dst = AtcVoices.Find(t.CampaignDir, Path.GetFileName(f)) ?? Path.Combine(t.CampaignDir, Path.GetFileName(f));
            Put(f, dst);
            res.Add($"Campaign\\{Path.GetFileName(dst)} written");
        }
        // 3) the .tdf points to it
        string rel = Path.GetRelativePath(t.DataDir, target);
        var lines = File.ReadAllLines(t.Tdf).ToList();
        bool changed = false;
        foreach (var key in new[] { "sounddir", "subtitlesdir" })
        {
            int i = lines.FindIndex(l => l.TrimStart().StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) || l.TrimStart().StartsWith(key + "\t", StringComparison.OrdinalIgnoreCase));
            string want = $"{key} {rel}";
            if (i >= 0) { if (lines[i].Trim() != want) { lines[i] = want; changed = true; } }
            else { lines.Add(want); changed = true; }
        }
        if (changed)
        {
            string b = $"file_{k++:00000}_{Path.GetFileName(t.Tdf)}";
            File.Copy(t.Tdf, Path.Combine(bak, b), true);
            manifest.Add("replaced;" + t.Tdf + ";" + b);
            File.WriteAllLines(t.Tdf, lines, Encoding.Latin1);
            res.Add($"{Path.GetFileName(t.Tdf)}: sounddir / subtitlesdir {rel}");
        }
        File.WriteAllLines(Path.Combine(bak, "manifest.txt"), manifest);
        res.Add($"Backup and manifest: {bak}");
        return res;
    }

    public static List<(string Dir, DateTime Time)> Backups(string backupRoot) =>
        !Directory.Exists(backupRoot) ? new()
        : Directory.EnumerateDirectories(backupRoot).Where(d => File.Exists(Path.Combine(d, "manifest.txt"))).OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => (d, Directory.GetCreationTime(d))).ToList();

    /// <summary>Deletes what an installation added and puts back what it replaced (the .tdf included).</summary>
    public static string Restore(string backupDir)
    {
        int del = 0, rep = 0;
        var all = File.ReadAllLines(Path.Combine(backupDir, "manifest.txt"));
        // a file this installation added (the copy of BMS's sounds) and then replaced (a voice file) did not exist before: deleted, not put back
        var added = all.Where(l => l.StartsWith("added;")).Select(l => Norm(l[6..])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in all)
        {
            var x = l.Split(';');
            if (x[0] == "added" && File.Exists(x[1])) { File.Delete(x[1]); del++; }
            else if (x[0] == "replaced" && x.Length >= 3 && !added.Contains(Norm(x[1])) && File.Exists(Path.Combine(backupDir, x[2]))) { File.Copy(Path.Combine(backupDir, x[2]), x[1], true); rep++; }
        }
        // empty folders left by the copy of BMS's sounds (only inside the theater's Sounds folder, the folder itself included)
        var lines = File.ReadAllLines(Path.Combine(backupDir, "manifest.txt"));
        string? target = lines.FirstOrDefault(l => l.StartsWith("target;"))?[7..];
        if (target != null)
        {
            string root = Norm(target);
            foreach (var l in lines.Where(l => l.StartsWith("added;")))
            {
                var dir = Path.GetDirectoryName(l[6..]);
                while (dir != null && (Norm(dir) + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                { Directory.Delete(dir); dir = Path.GetDirectoryName(dir); }
            }
        }
        return $"{del} added files removed, {rep} files put back";
    }
}
