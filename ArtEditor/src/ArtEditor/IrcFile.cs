using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtEditor;

/// <summary>
/// Un file di testi dell'interfaccia (Theater.irc): righe "[ADDTEXT] CHIAVE "testo"". Un testo con le virgolette aperte continua
/// nelle righe dopo. Si cambiano solo le righe delle voci modificate: commenti e ordine restano quelli del file.
/// </summary>
public sealed class IrcFile
{
    private static readonly Regex AddText = new(@"^\s*\[ADDTEXT\]\s+(\S+)\s*(.*)$", RegexOptions.Compiled);
    private readonly List<string> _lines;

    public IrcFile(IEnumerable<string> lines) { _lines = lines.Select(l => l.TrimEnd('\r')).ToList(); }
    public static IrcFile Load(string path) => new(File.ReadAllLines(path, Encoding.Latin1));
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TerrainTool.Core.Dds.ResourceBundle.BreakLink(path);
        File.WriteAllText(path, string.Join("\r\n", _lines) + "\r\n", Encoding.Latin1);
    }

    /// <summary>(chiave, testo, prima riga, numero di righe) nell'ordine del file; la prima volta che compare una chiave vince.</summary>
    private List<(string Key, string Text, int Line, int Count)> Parse()
    {
        var res = new List<(string, string, int, int)>();
        for (int i = 0; i < _lines.Count; i++)
        {
            var m = AddText.Match(_lines[i]);
            if (!m.Success) continue;
            int start = i;
            var sb = new StringBuilder(m.Groups[2].Value);
            int quotes = m.Groups[2].Value.Count(c => c == '"');
            while (quotes % 2 == 1 && i + 1 < _lines.Count && !AddText.IsMatch(_lines[i + 1]))
            {
                i++; sb.Append('\n').Append(_lines[i]); quotes += _lines[i].Count(c => c == '"');
            }
            string raw = sb.ToString().Trim();
            int a = raw.IndexOf('"'), b = raw.LastIndexOf('"');
            string text = a >= 0 && b > a ? raw[(a + 1)..b] : raw.Trim('"');
            res.Add((m.Groups[1].Value, text, start, i - start + 1));
        }
        return res;
    }

    public List<(string Key, string Text)> Entries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Parse().Where(e => seen.Add(e.Key)).Select(e => (e.Key, e.Text)).ToList();
    }

    public string? Get(string key) => Parse().FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } e ? e.Text : null;

    /// <summary>Cambia (o aggiunge in fondo) una voce. Le virgolette nel testo diventano apostrofi: BMS non le accetta dentro un testo.</summary>
    public void Set(string key, string text)
    {
        text = text.Replace('"', '\'').Replace("\r", "").Replace("\n", " ");
        string line = $"[ADDTEXT] {key} \"{text}\"";
        var e = Parse().FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (e.Key != null)
        {
            _lines.RemoveRange(e.Line, e.Count);
            _lines.Insert(e.Line, line);
        }
        else
        {
            if (!_lines.Any(l => l.Contains("added by the BMS Art Editor"))) { _lines.Add(""); _lines.Add("// ----- texts added by the BMS Art Editor -----"); }
            _lines.Add(line);
        }
    }
}
