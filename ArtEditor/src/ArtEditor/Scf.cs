using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtEditor;

/// <summary>Un elemento di una finestra .scf ([BUTTON], [TEXT], [TILE]...): i suoi valori e le righe del file da cui viene.</summary>
public sealed class ScfItem
{
    public string Kind { get; init; } = "";
    public string File { get; init; } = "";
    public int Line { get; init; }
    public int EndLine { get; set; }
    /// <summary>Proprieta' nell'ordine del file: nome (senza parentesi) → argomenti.</summary>
    public List<(string Key, string[] Args)> Props { get; } = new();

    public string[]? Get(string key) => Props.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Args;
    public IEnumerable<string[]> All(string key) => Props.Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Args);
    public bool Flag(string f) => All("FLAGBITON").Any(a => a.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)));
    public string? Id => Get("SETUP") is { Length: > 0 } s ? s[0] : null;
    public override string ToString() => $"{Kind} {Id}";
}

/// <summary>Una finestra di un .scf: posizione e misura sullo schermo 1920×1080, gruppo, profondita', elementi.</summary>
public sealed class ScfWindow
{
    public string File { get; init; } = "";
    public ScfItem Head { get; init; } = new();
    public List<ScfItem> Items { get; } = new();
    public string Id => Head.Id ?? Path.GetFileNameWithoutExtension(File);
    public int W, H, X, Y, Group, Depth;
    /// <summary>Posizione scritta nel file (X, Y possono essere spostate per le schermate del vecchio formato 1024×768).</summary>
    public int FileX, FileY;
    public bool Centered;
    public string? Font => Head.Get("FONT")?.FirstOrDefault();
    public bool Dragable => Head.Flag("C_BIT_DRAGABLE");
    /// <summary>Aree della finestra ([CLIENTAREA] x y w h n): gli elementi con [CLIENT] n sono relativi all'area n (senza, alla 0).</summary>
    public Dictionary<int, (int X, int Y)> Clients { get; } = new();
}

/// <summary>
/// Lettura dei file .scf dell'interfaccia di BMS (Art\_FHD\...\*.scf). Ogni blocco comincia con un'etichetta ([WINDOW], [BUTTON],
/// [TEXT], [TILE], [FILL]...) seguita da [SETUP]; le altre righe sono proprieta' del blocco. Le righe con # sono commenti.
/// </summary>
public static class Scf
{
    private static readonly Regex Tag = new(@"^\[([A-Z0-9_]+)\]\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string[] Split(string args)
    {
        var res = new List<string>();
        foreach (Match m in Regex.Matches(args, "\"([^\"]*)\"|[^\\s,]+"))
            res.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Value);
        return res.ToArray();
    }

    public static List<ScfWindow> Load(string path, string rel) => Parse(File.ReadAllLines(path, Encoding.Latin1), rel);

    public static List<ScfWindow> Parse(IReadOnlyList<string> src, string rel)
    {
        var lines = src.ToArray();
        var wins = new List<ScfWindow>();
        ScfWindow? win = null;
        ScfItem? cur = null;
        // prossima riga utile (non vuota, non commento)
        int Next(int i) { for (int k = i + 1; k < lines.Length; k++) { var t = lines[k].Trim(); if (t.Length > 0 && !t.StartsWith('#')) return k; } return -1; }
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (l.StartsWith('#') && l.Contains(HiddenMark, StringComparison.OrdinalIgnoreCase)) { cur?.Props.Add(("EDITORHIDDEN", Array.Empty<string>())); continue; }
            if (l.Length == 0 || l.StartsWith('#')) continue;
            var m = Tag.Match(l);
            if (!m.Success) continue;
            string key = m.Groups[1].Value.ToUpperInvariant();
            int n = Next(i);
            bool block = key == "WINDOW" || key != "SETUP" && n >= 0 && lines[n].TrimStart().StartsWith("[SETUP]", StringComparison.OrdinalIgnoreCase);
            if (block)
            {
                if (cur != null) cur.EndLine = i - 1;
                cur = new ScfItem { Kind = key, File = rel, Line = i };
                if (key == "WINDOW") { win = new ScfWindow { File = rel, Head = cur }; wins.Add(win); }
                else (win ??= NewWin(rel, wins)).Items.Add(cur);
                continue;
            }
            cur?.Props.Add((key, Split(m.Groups[2].Value)));
        }
        if (cur != null) cur.EndLine = lines.Length - 1;
        foreach (var w in wins) Measure(w);
        return wins;
    }

    private static ScfWindow NewWin(string rel, List<ScfWindow> wins)
    {
        var w = new ScfWindow { File = rel, Head = new ScfItem { Kind = "WINDOW", File = rel } };
        wins.Add(w);
        return w;
    }

    private static int I(string[]? a, int k, int def = 0) => a != null && a.Length > k && int.TryParse(a[k], out int v) ? v : def;

    public const string HiddenMark = "BMS Art Editor: hidden";

    /// <summary>
    /// Nasconde (o rimostra) un elemento: aggiunge dopo [SETUP] una riga di commento riconoscibile e [FLAGBITON] C_BIT_INVISIBLE
    /// (o le toglie). Un elemento che BMS stesso tiene nascosto non viene toccato quando si rimostra: si tolgono solo le righe dell'editor.
    /// </summary>
    public static List<string> SetHidden(IReadOnlyList<string> lines, ScfItem it, bool hide)
    {
        var res = lines.ToList();
        int end = Math.Min(it.EndLine, res.Count - 1);
        if (hide)
        {
            int at = it.Line + 1;
            for (int i = it.Line; i <= end; i++) if (res[i].TrimStart().StartsWith("[SETUP]", StringComparison.OrdinalIgnoreCase)) { at = i + 1; break; }
            res.Insert(at, "[FLAGBITON] C_BIT_INVISIBLE");
            res.Insert(at, "#" + HiddenMark + " (remove these two lines to show it again)");
            return res;
        }
        for (int i = end; i >= it.Line; i--)
        {
            var t = res[i].Trim();
            if (!t.StartsWith('#') || !t.Contains(HiddenMark, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < res.Count && res[i + 1].Trim().Replace("\t", " ").Equals("[FLAGBITON] C_BIT_INVISIBLE", StringComparison.OrdinalIgnoreCase)) res.RemoveAt(i + 1);
            res.RemoveAt(i);
        }
        return res;
    }

    /// <summary>
    /// Cambia il font di un elemento nelle righe del file: sostituisce le righe [FONT] dell'elemento (Line..EndLine), o ne aggiunge una
    /// dopo [SETUP] se l'elemento usava il font della finestra.
    /// </summary>
    public static List<string> SetFont(IReadOnlyList<string> lines, ScfItem it, string font)
    {
        var res = lines.ToList();
        int end = Math.Min(it.EndLine, res.Count - 1);
        bool done = false;
        for (int i = end; i >= it.Line; i--)
        {
            var t = res[i].TrimStart();
            if (!t.StartsWith("[FONT]", StringComparison.OrdinalIgnoreCase)) continue;
            if (!done) { res[i] = "[FONT] " + font; done = true; } else res.RemoveAt(i);
        }
        if (!done)
        {
            int at = it.Line + 1;
            for (int i = it.Line; i <= end; i++) if (res[i].TrimStart().StartsWith("[SETUP]", StringComparison.OrdinalIgnoreCase)) { at = i + 1; break; }
            res.Insert(at, "[FONT] " + font);
        }
        return res;
    }

    /// <summary>
    /// Sostituisce nelle righe dell'elemento quelle che 'match' riconosce con 'add' (al posto della prima tolta, o dopo [SETUP] se non
    /// ce n'erano). Serve per [IMAGES], [BUTTONIMAGE], [TEXTOFFSET], [UPCOLOR], [SOUNDBITE]...
    /// </summary>
    public static List<string> SetLines(IReadOnlyList<string> lines, ScfItem it, Func<string, bool> match, IEnumerable<string> add)
    {
        var res = lines.ToList();
        int end = Math.Min(it.EndLine, res.Count - 1);
        int at = -1;
        for (int i = end; i >= it.Line; i--)
        {
            var t = res[i].TrimStart();
            if (t.StartsWith('#') || !match(t)) continue;
            res.RemoveAt(i);
            at = i;
        }
        if (at < 0)
        {
            at = it.Line + 1;
            for (int i = it.Line; i <= Math.Min(end, res.Count - 1); i++) if (res[i].TrimStart().StartsWith("[SETUP]", StringComparison.OrdinalIgnoreCase)) { at = i + 1; break; }
        }
        res.InsertRange(Math.Min(at, res.Count), add);
        return res;
    }

    /// <summary>La riga comincia con l'etichetta (es. "[IMAGES]") e, se dato, il primo argomento (es. "C_STATE_DOWN").</summary>
    public static Func<string, bool> Key(string tag, string? firstArg = null) => t =>
    {
        if (!t.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) return false;
        if (firstArg == null) return true;
        var a = Split(t[tag.Length..]);
        return a.Length > 0 && a[0].Equals(firstArg, StringComparison.OrdinalIgnoreCase);
    };

    private static void Measure(ScfWindow w)
    {
        var s = w.Head.Get("SETUP");
        w.W = I(s, 2, 1920); w.H = I(s, 3, 1080);
        var xy = w.Head.Get("XY");
        if (xy is { Length: > 0 } && xy[0].Equals("centralized", StringComparison.OrdinalIgnoreCase)) { w.Centered = true; w.X = (1920 - w.W) / 2; w.Y = (1080 - w.H) / 2; }
        else { w.X = I(xy, 0); w.Y = I(xy, 1); }
        foreach (var c in w.Head.All("CLIENTAREA")) if (c.Length >= 5) w.Clients[I(c, 4)] = (I(c, 0), I(c, 1));
        w.FileX = w.X; w.FileY = w.Y;
        w.Group = I(w.Head.Get("GROUP"), 0);
        w.Depth = I(w.Head.Get("DEPTH"), 0);
    }
}
