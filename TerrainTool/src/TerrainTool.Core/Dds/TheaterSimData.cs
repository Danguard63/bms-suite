using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Dati di simulazione propri del teatro (come fa il teatro Israel con "simdatadir Add-On Israel\Sim"):
///  - Sim: copia completa di &lt;BMS&gt;\Data\Sim della 4.38 in Output\Install\&lt;add-on&gt;\NT\Sim, poi i file della cartella
///    del progetto "SimOverrides" (stessi percorsi relativi, es. SimOverrides\Misdata\SA-2F.dat) messi al loro posto.
///    Si attiva solo se la cartella SimOverrides esiste; a ogni Assemble la copia si rifà da zero, così gli
///    aggiornamenti di BMS arrivano da soli e restano solo le nostre modifiche.
///  - Database: il file del progetto "DbEdits.txt" (righe "WCD 417 Range 43.0  # commento") applicato agli
///    Falcon4_*.xml che il lavoro Objects copia dalla 4.38, sempre dopo la copia.
/// </summary>
public static class TheaterSimData
{
    public const string SimRel = @"NT\Sim";
    public static string SimOverridesFolder(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "SimOverrides");
    public static string DbEditsFile(Project.TheaterProject p) => Path.Combine(p.ProjectFolder, "DbEdits.txt");

    public static bool Enabled(Project.TheaterProject p) => Directory.Exists(SimOverridesFolder(p));

    /// <summary>Copia completa di bmsSim in dst (da zero) + i file di overrides. Restituisce le note.</summary>
    public static List<string> BuildSim(string bmsSim, string dst, string overrides)
    {
        var notes = new List<string>();
        if (!Directory.Exists(bmsSim)) throw new DirectoryNotFoundException($"BMS Sim folder not found: {bmsSim}");
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        int n = 0; long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(bmsSim, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(bmsSim, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
            n++; bytes += new FileInfo(f).Length;
        }
        notes.Add($@"Sim: full copy of {bmsSim} ({n} files, {bytes / 1048576.0:0.0} MB) → {SimRel}");
        if (!Directory.Exists(overrides)) return notes;
        var changed = new List<string>(); var added = new List<string>(); var skipped = new List<string>();
        foreach (var f in Directory.EnumerateFiles(overrides, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(overrides, f);
            if (rel.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && rel.StartsWith("README", StringComparison.OrdinalIgnoreCase)) continue;
            // the flight models stay BMS's (the developers do not want them changed or passed around)
            if (Sim.SimLibrary.IsProtected(rel)) { skipped.Add(rel); continue; }
            // stesso nome del file di BMS anche con maiuscole diverse
            string? existing = FindCaseInsensitive(dst, rel);
            string to = existing ?? Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
            (existing != null ? changed : added).Add(rel);
        }
        if (changed.Count > 0) notes.Add($"Sim: {changed.Count} files replaced from SimOverrides: {string.Join(", ", changed)}");
        if (added.Count > 0) notes.Add($"Sim: {added.Count} files added from SimOverrides (not in BMS): {string.Join(", ", added)}");
        if (skipped.Count > 0) notes.Add($"Sim: {skipped.Count} SimOverrides files in Acdata NOT used (flight models stay BMS's): {string.Join(", ", skipped)}");
        if (changed.Count + added.Count == 0) notes.Add("Sim: SimOverrides is empty (identical to the BMS one)");
        return notes;
    }

    private static string? FindCaseInsensitive(string root, string rel)
    {
        string cur = root;
        foreach (var part in rel.Split('\\', '/'))
        {
            if (!Directory.Exists(cur)) return null;
            string? m = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (m == null) return null;
            cur = m;
        }
        return File.Exists(cur) ? cur : null;
    }

    /// <summary>Riga di DbEdits.txt: tabella (WCD, VCD, RCD, UCD, SSD, ...), numero del record, campo, valore.</summary>
    public sealed record DbEdit(string Table, int Num, string Field, string Value, int Line);

    /// <summary>Copia di un record: "COPY TABLE Sorgente Nuovo" (il nuovo numero dev'essere il primo libero in fondo alla tabella).</summary>
    public const string CopyField = "@COPY";

    public static List<DbEdit> ParseDbEdits(string file)
    {
        var res = new List<DbEdit>();
        int ln = 0;
        foreach (var raw in File.ReadAllLines(file))
        {
            ln++;
            string l = raw; int h = l.IndexOf('#'); if (h >= 0) l = l[..h];
            l = l.Trim();
            if (l.Length == 0) continue;
            var c = Regex.Match(l, @"^COPY\s+([A-Za-z]{2,4})\s+(\d+)\s+(\d+)$", RegexOptions.IgnoreCase);
            if (c.Success)
            {
                res.Add(new DbEdit(c.Groups[1].Value.ToUpperInvariant(), int.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture), CopyField, c.Groups[2].Value, ln));
                continue;
            }
            // TABLE Num Field Value (il valore e' il resto della riga: puo' contenere spazi, es. un nome)
            var m = Regex.Match(l, @"^([A-Za-z]{2,4})\s+(\d+)\s+(\w+)\s+(.+)$");
            if (!m.Success)
                throw new FormatException($"DbEdits.txt line {ln}: expected 'TABLE Num Field Value' or 'COPY TABLE From To', got '{raw.Trim()}'");
            res.Add(new DbEdit(m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, m.Groups[4].Value.Trim(), ln));
        }
        return res;
    }

    /// <summary>Applica DbEdits.txt agli Falcon4_*.xml di dbDir. Errore se il record non c'e'; un campo assente si aggiunge.</summary>
    public static List<string> ApplyDbEdits(string dbDir, string editsFile)
    {
        var notes = new List<string>();
        if (!File.Exists(editsFile)) return notes;
        var edits = ParseDbEdits(editsFile);
        foreach (var g in edits.GroupBy(e => e.Table))
        {
            string? xmlPath = Directory.EnumerateFiles(dbDir, "*.xml").FirstOrDefault(f => Path.GetFileName(f).Equals($"Falcon4_{g.Key}.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"DbEdits.txt: Falcon4_{g.Key}.xml not found in {dbDir}");
            string xml = File.ReadAllText(xmlPath);
            foreach (var e in g)
            {
                if (e.Field == CopyField)
                {
                    int src = int.Parse(e.Value, CultureInfo.InvariantCulture);
                    var srcRec = Regex.Match(xml, $@"([ \t]*)<{e.Table} Num=""{src}"">(.*?)</{e.Table}>", RegexOptions.Singleline);
                    if (!srcRec.Success) throw new InvalidOperationException($"DbEdits.txt line {e.Line}: COPY {e.Table} {src}: record not found");
                    int max = Regex.Matches(xml, $@"<{e.Table} Num=""(\d+)"">").Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max();
                    // the database is copied fresh from BMS every time: a record already there is BMS's own (an update added it),
                    // and the edits after the COPY would overwrite it
                    if (Regex.IsMatch(xml, $@"<{e.Table} Num=""{e.Num}"">"))
                        throw new InvalidOperationException($"DbEdits.txt line {e.Line}: COPY {e.Table} {src} {e.Num}: this BMS version already has {e.Table} {e.Num} " +
                            $"(a BMS update added it). Use the next free number, {max + 1}, here and in the lines that refer to {e.Num}");
                    if (e.Num != max + 1) throw new InvalidOperationException($"DbEdits.txt line {e.Line}: COPY {e.Table} {src} {e.Num}: the new record must be the next free number, {max + 1}");
                    int close = xml.LastIndexOf($"</{e.Table}Records>", StringComparison.Ordinal);
                    if (close < 0) throw new InvalidOperationException($"DbEdits.txt line {e.Line}: </{e.Table}Records> not found");
                    string nl2 = xml.Contains("\r\n") ? "\r\n" : "\n";
                    string copy = srcRec.Groups[1].Value + $"<{e.Table} Num=\"{e.Num}\">{srcRec.Groups[2].Value}</{e.Table}>{nl2}";
                    int ls = xml.LastIndexOf('\n', close - 1) + 1;
                    xml = xml[..ls] + copy + xml[ls..];
                    notes.Add($"DB: {e.Table} {e.Num} = copy of {e.Table} {src}");
                    continue;
                }
                string val = global::System.Security.SecurityElement.Escape(e.Value) ?? e.Value;
                var rec = Regex.Match(xml, $@"<{e.Table} Num=""{e.Num}"">(.*?)</{e.Table}>", RegexOptions.Singleline);
                if (!rec.Success) throw new InvalidOperationException($"DbEdits.txt line {e.Line}: {e.Table} {e.Num} not found");
                var fld = Regex.Match(rec.Groups[1].Value, $@"<{e.Field}>(.*?)</{e.Field}>", RegexOptions.Singleline);
                string name = Regex.Match(rec.Groups[1].Value, "<Name>(.*?)</Name>").Groups[1].Value;
                if (!fld.Success)
                {
                    // campo assente (BMS non scrive i valori a zero, es. ElementCount_8): si aggiunge in fondo al record
                    int end = rec.Groups[1].Index + rec.Groups[1].Length;
                    string indent = Regex.Match(rec.Groups[1].Value, @"\n([ \t]*)<").Groups[1].Value;
                    string nl = rec.Groups[1].Value.Contains("\r\n") ? "\r\n" : "\n";
                    string add = $"{indent}<{e.Field}>{val}</{e.Field}>{nl}";
                    // prima dell'indentazione della riga di chiusura
                    int lineStart = xml.LastIndexOf('\n', end - 1) + 1;
                    xml = xml[..lineStart] + add + xml[lineStart..];
                    notes.Add($"DB: {e.Table} {e.Num} {(name.Length > 0 ? $"'{name}' " : "")}{e.Field} (new) → {e.Value}");
                    continue;
                }
                int at = rec.Groups[1].Index + fld.Groups[1].Index;
                xml = xml[..at] + val + xml[(at + fld.Groups[1].Length)..];
                notes.Add($"DB: {e.Table} {e.Num} {(name.Length > 0 ? $"'{name}' " : "")}{e.Field} {fld.Groups[1].Value} → {e.Value}");
            }
            File.WriteAllText(xmlPath, xml, new UTF8Encoding(false));
        }
        return notes;
    }

    /// <summary>Riga simdatadir di un .tdf sostituita (o aggiunta). null se era gia' quella.</summary>
    public static string? SetSimDataDir(string tdfText, string value)
    {
        var lines = tdfText.Replace("\r\n", "\n").Split('\n').ToList();
        int i = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*simdatadir\s", RegexOptions.IgnoreCase));
        string line = "simdatadir " + value;
        if (i >= 0) { if (lines[i].Trim().Equals(line, StringComparison.OrdinalIgnoreCase)) return null; lines[i] = line; }
        else { if (lines.Count > 0 && lines[^1].Length == 0) lines.Insert(lines.Count - 1, line); else lines.Add(line); }
        return string.Join("\r\n", lines);
    }
}
