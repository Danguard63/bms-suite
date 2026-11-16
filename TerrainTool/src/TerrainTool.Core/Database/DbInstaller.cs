using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Database;

/// <summary>
/// Writes the changed tables (Falcon4_XXX.xml) and installs them into the theater's objects folder: only the record blocks that
/// changed are replaced (new records go before the closing tag), the rest of every file stays byte for byte. Before a file is
/// replaced the original goes to Backup\&lt;time&gt;; the compiled caches FALCON4.* are removed so that BMS builds them again from
/// the XML at the next start. The shared Terrdata\objects of BMS (Korea and every theater that uses it) is never written.
/// </summary>
public static class DbInstaller
{
    /// <summary>True when the objects folder is BMS's own Terrdata\objects (used by Korea and by theaters without their own).</summary>
    public static bool IsShared(string dataDir, string objectDir) =>
        string.Equals(Norm(Path.Combine(dataDir, "Terrdata", "objects")), Norm(objectDir), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    public static bool BmsRunning()
    {
        try { return Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Falcon BMS", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return false; }
    }

    private static string Esc(string v) => v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Block(string tag, int num, IEnumerable<string[]> fields)
    {
        var sb = new StringBuilder();
        sb.Append($"<{tag} Num=\"{num}\">\n");
        foreach (var f in fields) sb.Append($"        <{f[0]}>{Esc(f[1])}</{f[0]}>\n");
        sb.Append($"    </{tag}>");
        return sb.ToString();
    }

    /// <summary>Writes Falcon4_XXX.xml of every changed table into outDir (from the theater's originals). Returns the files written.</summary>
    public static List<string> Write(DbEdits edits, string objectDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.EnumerateFiles(outDir, "Falcon4_*.xml")) File.Delete(old);
        var written = new List<string>();
        foreach (var g in edits.Data.Records.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase))
        {
            string table = g.Key.ToUpperInvariant();
            string src = Directory.EnumerateFiles(objectDir, $"Falcon4_{table}.xml").FirstOrDefault()
                         ?? throw new FileNotFoundException($"Falcon4_{table}.xml not found in {objectDir}");
            string text = File.ReadAllText(src);
            bool crlf = text.Contains("\r\n");
            if (crlf) text = text.Replace("\r\n", "\n");
            foreach (var r in g.OrderBy(r => r.Num))
            {
                string block = Block(table, r.Num, r.Fields);
                var m = Regex.Match(text, $@"<{table} Num=""{r.Num}"">.*?</{table}>", RegexOptions.Singleline);
                if (m.Success) text = text[..m.Index] + block + text[(m.Index + m.Length)..];
                else
                {
                    int end = text.LastIndexOf($"</{table}Records>", StringComparison.Ordinal);
                    if (end < 0) throw new InvalidDataException($"{Path.GetFileName(src)}: </{table}Records> not found");
                    text = text[..end] + "    " + block + "\n" + text[end..];
                }
            }
            if (crlf) text = text.Replace("\n", "\r\n");
            string dst = Path.Combine(outDir, Path.GetFileName(src));
            File.WriteAllText(dst, text, new UTF8Encoding(false));
            written.Add(dst);
        }
        return written;
    }

    /// <summary>Copies the written tables into the objects folder (backup of every file replaced and of the caches), removes FALCON4.*.</summary>
    public static List<string> Install(string objectDir, string outDir, string backupRoot)
    {
        var log = new List<string>();
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string bak = Path.Combine(backupRoot, ts);
        Directory.CreateDirectory(bak);
        foreach (var f in Directory.EnumerateFiles(outDir, "Falcon4_*.xml"))
        {
            string dst = Path.Combine(objectDir, Path.GetFileName(f));
            if (File.Exists(dst))
            {
                if (File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(f))) { log.Add($"{Path.GetFileName(f)}: already the same"); continue; }
                File.Copy(dst, Path.Combine(bak, Path.GetFileName(dst)), true);
            }
            File.Copy(f, dst, true);
            log.Add($"{Path.GetFileName(f)}: installed (original in Backup\\{ts})");
        }
        log.AddRange(ClearCaches(objectDir, bak));
        if (!Directory.EnumerateFileSystemEntries(bak).Any()) Directory.Delete(bak);
        return log;
    }

    /// <summary>FALCON4.* in the objects folder are what BMS compiles from the XML: removed so that it compiles them again.</summary>
    private static List<string> ClearCaches(string objectDir, string? bak)
    {
        var log = new List<string>();
        foreach (var f in Directory.EnumerateFiles(objectDir, "FALCON4.*"))
        {
            if (Path.GetExtension(f).Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (bak != null) { Directory.CreateDirectory(Path.Combine(bak, "caches")); File.Copy(f, Path.Combine(bak, "caches", Path.GetFileName(f)), true); }
                File.Delete(f);
                log.Add($"{Path.GetFileName(f)}: removed (BMS builds it again)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Add($"{Path.GetFileName(f)}: NOT removed – {ex.Message}"); }
        }
        return log;
    }

    public static List<(string Dir, DateTime Time, List<string> Files)> Backups(string backupRoot) =>
        !Directory.Exists(backupRoot) ? new()
        : Directory.EnumerateDirectories(backupRoot).OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => (d, Directory.GetCreationTime(d), Directory.EnumerateFiles(d, "Falcon4_*.xml").Select(f => Path.GetFileName(f)).ToList()))
            .Where(x => x.Item3.Count > 0).ToList();

    /// <summary>Puts back the tables of a backup and removes FALCON4.* again.</summary>
    public static List<string> Restore(string objectDir, string backupDir)
    {
        var log = new List<string>();
        foreach (var f in Directory.EnumerateFiles(backupDir, "Falcon4_*.xml"))
        {
            File.Copy(f, Path.Combine(objectDir, Path.GetFileName(f)), true);
            log.Add($"{Path.GetFileName(f)}: restored");
        }
        log.AddRange(ClearCaches(objectDir, null));
        return log;
    }
}
