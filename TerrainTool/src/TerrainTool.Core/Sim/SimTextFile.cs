using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerrainTool.Core.Sim;

/// <summary>
/// A BMS Sim text file (missile .dat, radar .dat, vehicle .veh, bomb .dat) read as a list of values with their names, without
/// losing anything: every value is a token on a line of the file, and saving rewrites only the tokens that changed (comments,
/// spacing and order stay as they are). Two shapes are understood:
/// <list type="bullet">
/// <item>a "# Label" comment followed by one or more lines of numbers (missiles, vehicles): the label names the value or table;</item>
/// <item>"Key value [value…] [# comment]" lines (radars, bombs, the end of missile and vehicle files).</item>
/// </list>
/// Comment blocks between "#-----" lines are section titles.
/// </summary>
public sealed class SimTextFile
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public sealed class Token
    {
        public int Line { get; init; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class Item
    {
        public string Section { get; init; } = "";
        public string Label { get; init; } = "";
        /// <summary>The key of a "Key value" line, or null for a value named by the comment above it.</summary>
        public string? Key { get; init; }
        /// <summary>Comment at the end of a "Key value" line.</summary>
        public string Note { get; init; } = "";
        public List<Token> Tokens { get; } = new();
        /// <summary>Same section and label met before in the file (0 for the first): with it the item is found again in another version.</summary>
        public int Occurrence { get; set; }
        public string Id => $"{Section}|{Label}|{Occurrence}";
        public string Value => string.Join(" ", Tokens.Select(t => t.Text));
        /// <summary>A numeric table (more numbers than one line holds, or several lines): the number of values cannot change.</summary>
        public bool FixedCount => Key == null || Fixed;
        /// <summary>A list in brackets (.txtpb): its number of values cannot change.</summary>
        public bool Fixed { get; init; }
    }

    public List<string> Lines { get; } = new();
    public List<Item> Items { get; } = new();
    public string Header { get; private set; } = "";
    private readonly string _newline;

    private SimTextFile(string text)
    {
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        Lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        if (IsTxtpb()) ParseTxtpb(); else Parse();
    }

    public static SimTextFile Load(string path) => new(File.ReadAllText(path, Encoding.Latin1));
    public static SimTextFile FromText(string text) => new(text);

    public string Text() => string.Join(_newline, Lines);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Text(), Encoding.Latin1);
    }

    /// <summary>True for an aircraft file of BMS 4.38 (protobuf text: "name: value" and "name { … }").</summary>
    public bool IsProto { get; private set; }

    private bool IsTxtpb()
    {
        foreach (var l in Lines)
        {
            string t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            return Regex.IsMatch(t, @"^[A-Za-z_]\w*\s*(:\s*\S|\{)");
        }
        return false;
    }

    /// <summary>
    /// Protobuf text (Acdata\*.txtpb): every "name: value" line is a value; blocks "name { … }" make the section path
    /// (repeated blocks numbered, with the comment after "{", e.g. "data 3 (Mach: 0.8)"); a list "[a, b, c]" is one value
    /// with a fixed number of items.
    /// </summary>
    private void ParseTxtpb()
    {
        IsProto = true;
        var stack = new List<string>();
        var counts = new List<Dictionary<string, int>> { new() };
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < Lines.Count; i++)
        {
            string raw = Lines[i];
            string code = StripComment(raw, out string note);
            string t = code.Trim();
            if (t.Length == 0) continue;
            var open = Regex.Match(t, @"^([A-Za-z_]\w*)\s*\{$");
            if (open.Success)
            {
                string name = open.Groups[1].Value;
                var c = counts[^1];
                int n = c.TryGetValue(name, out int k) ? k : 0;
                c[name] = n + 1;
                stack.Add(n == 0 && !RepeatedLater(i, name) ? name : $"{name} {n + 1}" + (note.Length > 0 ? $" ({note})" : ""));
                counts.Add(new());
                continue;
            }
            if (t == "}")
            {
                if (stack.Count > 0) { stack.RemoveAt(stack.Count - 1); counts.RemoveAt(counts.Count - 1); }
                continue;
            }
            var kv = Regex.Match(code, @"^(\s*)([A-Za-z_]\w*)\s*:\s*(.*?)\s*$");
            if (!kv.Success) continue;
            string key = kv.Groups[2].Value;
            int vStart = kv.Groups[3].Index;
            string val = kv.Groups[3].Value;
            var it = new Item { Section = string.Join(" › ", stack), Label = key, Key = key, Note = note, Fixed = val.StartsWith('[') };
            if (val.StartsWith('['))
                foreach (Match m in Regex.Matches(val, @"[^\[\],\s]+"))
                    it.Tokens.Add(new Token { Line = i, Start = vStart + m.Index, Length = m.Length, Text = m.Value });
            else if (val.Length > 0)
                it.Tokens.Add(new Token { Line = i, Start = vStart, Length = val.Length, Text = val });
            string sk = $"{it.Section}|{it.Label}";
            it.Occurrence = seen.TryGetValue(sk, out int o) ? o : 0;
            seen[sk] = it.Occurrence + 1;
            Items.Add(it);
        }
    }

    /// <summary>A block met again at the same level before its parent closes (gear, data…): numbered from the first one.</summary>
    private bool RepeatedLater(int i, string name)
    {
        int d = 1;     // inside the block opened on line i
        for (int j = i + 1; j < Lines.Count; j++)
        {
            string t = StripComment(Lines[j], out _).Trim();
            if (t.EndsWith('{'))
            {
                if (d == 0 && Regex.IsMatch(t, $@"^{Regex.Escape(name)}\s*\{{$")) return true;
                d++;
            }
            else if (t == "}") { d--; if (d < 0) return false; }
        }
        return false;
    }

    /// <summary>The line without a "# comment" (a # inside quotes is kept).</summary>
    private static string StripComment(string line, out string note)
    {
        bool q = false;
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '"') q = !q;
            else if (line[k] == '#' && !q) { note = line[(k + 1)..].Trim(); return line[..k]; }
        }
        note = "";
        return line;
    }

    private static bool IsNumber(string s) => double.TryParse(s, NumberStyles.Float, Ci, out _);
    private static bool IsSeparator(string comment) => comment.Length >= 3 && comment.All(c => c is '-' or '=' or '*');

    private void Parse()
    {
        string section = "";
        var pending = new List<string>();
        Item? current = null;              // numeric block being read (continues on the next numeric lines)
        bool afterSeparator = false;
        var seen = new Dictionary<string, int>();
        var headerLines = new List<string>();
        bool inHeader = true;
        bool skipNextSeparatorClear = false;

        void Add(Item it)
        {
            string k = $"{it.Section}|{it.Label}";
            it.Occurrence = seen.TryGetValue(k, out int n) ? n : 0;
            seen[k] = it.Occurrence + 1;
            Items.Add(it);
        }

        for (int i = 0; i < Lines.Count; i++)
        {
            string raw = Lines[i];
            string t = raw.Trim();
            if (t.Length == 0) { current = null; continue; }
            if (t.StartsWith('#'))
            {
                current = null;
                string c = t.TrimStart('#').Trim();
                if (IsSeparator(c))
                {
                    afterSeparator = true; inHeader = false;
                    if (skipNextSeparatorClear) skipNextSeparatorClear = false; else pending.Clear();
                    continue;
                }
                if (c.Length == 0) continue;
                if (inHeader && Items.Count == 0) headerLines.Add(c);
                // a title between two separator lines: the next line is a separator too
                // ("Number of Altitude Breakpoints" between separators names the values below it, not a new part of the file)
                if (afterSeparator && NextIsSeparator(i) && !c.StartsWith("number of", StringComparison.OrdinalIgnoreCase))
                { section = c; pending.Clear(); afterSeparator = false; continue; }
                if (afterSeparator && NextIsSeparator(i)) { pending.Clear(); pending.Add(c); afterSeparator = false; skipNextSeparatorClear = true; continue; }
                afterSeparator = false;
                pending.Add(c);
                continue;
            }
            afterSeparator = false;
            inHeader = false;
            // the code part and a comment at the end
            int hash = raw.IndexOf('#');
            string code = hash >= 0 ? raw[..hash] : raw;
            string note = hash >= 0 ? raw[(hash + 1)..].Trim() : "";
            var toks = Regex.Matches(code, @"\S+").Select(m => new Token { Line = i, Start = m.Index, Length = m.Length, Text = m.Value }).ToList();
            if (toks.Count == 0) continue;
            bool numeric = toks.All(x => IsNumber(x.Text));
            if (!numeric && !IsNumber(toks[0].Text) && Regex.IsMatch(toks[0].Text, @"^[A-Za-z_][\w\-]*$"))
            {
                current = null;
                var it = new Item { Section = section, Label = toks[0].Text, Key = toks[0].Text, Note = Join(pending, note) };
                it.Tokens.AddRange(toks.Skip(1));
                Add(it);
                pending.Clear();
                continue;
            }
            if (current != null && pending.Count == 0) { current.Tokens.AddRange(toks); continue; }
            string label = pending.Count > 0 ? string.Join(", ", pending) : section.Length > 0 ? section : "Values";
            current = new Item { Section = section, Label = label, Note = note };
            current.Tokens.AddRange(toks);
            Add(current);
            pending.Clear();
        }
        Header = string.Join("\n", headerLines.Take(6));
    }

    private static string Join(List<string> pending, string note)
    {
        var all = pending.Where(p => p.Length > 0).ToList();
        if (note.Length > 0) all.Add(note);
        return string.Join("; ", all);
    }

    private bool NextIsSeparator(int i)
    {
        for (int j = i + 1; j < Lines.Count; j++)
        {
            string t = Lines[j].Trim();
            if (t.Length == 0) continue;
            return t.StartsWith('#') && IsSeparator(t.TrimStart('#').Trim());
        }
        return false;
    }

    /// <summary>
    /// New values for an item (space separated). A numeric table keeps its number of values; a number is written in the
    /// style of the old one (sign, decimals). Returns an error text, or null when done.
    /// </summary>
    public string? SetValue(Item it, string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (IsProto && !it.Fixed)
        {
            // one value after "name:": written as typed (a text keeps its quotes, a word or a number as it is)
            string v = text.Trim();
            if (v.Length == 0) return $"{it.Label}: a value is needed";
            if (it.Tokens.Count == 0) return $"{it.Label}: this line has no value to change";
            Replace(it.Tokens[0], IsNumber(v) ? Like(it.Tokens[0].Text, v) : v);
            return null;
        }
        if (it.FixedCount && parts.Length != it.Tokens.Count)
            return $"{it.Label}: {it.Tokens.Count} values expected, {parts.Length} given";
        if (it.FixedCount && parts.Any(p => !IsNumber(p)))
            return $"{it.Label}: only numbers here";
        if (!it.FixedCount)
        {
            // "Key value…": the rest of the line after the key is rewritten (its comment stays)
            if (parts.Length == 0) return $"{it.Label}: a value is needed";
            if (it.Tokens.Count == 0) return $"{it.Label}: this line has no value to change";
            if (parts.Length == it.Tokens.Count)
            {
                for (int k = 0; k < parts.Length; k++) Replace(it.Tokens[k], Like(it.Tokens[k].Text, parts[k]));
                return null;
            }
            int line = it.Tokens[0].Line;
            if (it.Tokens.Any(x => x.Line != line)) return $"{it.Label}: the number of values cannot change here";
            int start = it.Tokens[0].Start, end = it.Tokens[^1].Start + it.Tokens[^1].Length;
            string newText = string.Join(" ", parts);
            Lines[line] = Lines[line][..start] + newText + Lines[line][end..];
            it.Tokens.Clear();
            foreach (Match m in Regex.Matches(newText, @"\S+")) it.Tokens.Add(new Token { Line = line, Start = start + m.Index, Length = m.Length, Text = m.Value });
            return null;
        }
        for (int k = 0; k < parts.Length; k++) Replace(it.Tokens[k], Like(it.Tokens[k].Text, parts[k]));
        return null;
    }

    /// <summary>A number written like the old one: same sign style and decimals ("+0.130000", "10.0", "376000").</summary>
    public static string Like(string old, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, Ci, out double v) || !double.TryParse(old, NumberStyles.Float, Ci, out _)) return value;
        if (double.TryParse(old, NumberStyles.Float, Ci, out double ov) && ov == v) return old;
        int dot = old.IndexOf('.');
        int dec = dot < 0 || old.Contains('e') || old.Contains('E') ? -1 : old.Length - dot - 1;
        int vdot = value.IndexOf('.');
        int vdec = vdot < 0 ? 0 : value.Length - vdot - 1;
        string s = dec < 0 ? (vdot < 0 ? value.TrimStart('+') : v.ToString("0.########", Ci)) : v.ToString("F" + Math.Max(dec, vdec), Ci);
        if (old.StartsWith('+') && v >= 0) s = "+" + s;
        return s;
    }

    private void Replace(Token tok, string text)
    {
        if (tok.Text == text) return;
        string line = Lines[tok.Line];
        Lines[tok.Line] = line[..tok.Start] + text + line[(tok.Start + tok.Length)..];
        int delta = text.Length - tok.Length;
        tok.Text = text; tok.Length = text.Length;
        if (delta == 0) return;
        foreach (var other in Items.SelectMany(x => x.Tokens).Where(x => x.Line == tok.Line && x.Start > tok.Start))
            other.Start += delta;
    }

    /// <summary>The item with the same section, label and occurrence in another version of the file.</summary>
    public Item? Find(Item other) => Items.FirstOrDefault(x => x.Id == other.Id);

    /// <summary>Same values (as numbers when both are numbers).</summary>
    public static bool SameValue(Item a, Item b)
    {
        if (a.Tokens.Count != b.Tokens.Count) return false;
        for (int i = 0; i < a.Tokens.Count; i++)
        {
            string x = a.Tokens[i].Text, y = b.Tokens[i].Text;
            if (double.TryParse(x, NumberStyles.Float, Ci, out double dx) && double.TryParse(y, NumberStyles.Float, Ci, out double dy)) { if (Math.Abs(dx - dy) > 1e-9 * Math.Max(1, Math.Abs(dx))) return false; }
            else if (!string.Equals(x, y, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
