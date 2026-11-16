using System.IO;
using System.Text;
using TerrainTool.Core.Dds;

namespace ArtEditor;

/// <summary>
/// Le risorse dell'interfaccia come le trova BMS per il teatro aperto: prima la cartella Art del teatro, poi quella di BMS
/// (la KTO, il "master" di tutti i teatri). Finestre (.scf), immagini (archivi .idx/.rsc), testi (.irc) e font (.bft).
/// Per ogni risorsa si sa da dove viene: del teatro o della KTO. Le modifiche non ancora salvate (ArtSet) si vedono subito.
/// </summary>
public sealed class UiArt
{
    private readonly ArtSet _art;
    public UiArt(ArtSet art) { _art = art; }

    public string Work => _art.WorkRoot;
    public string Base => _art.BaseRoot;

    // ───────────────────────── finestre ─────────────────────────

    private Dictionary<string, (string Path, bool Theater)>? _scf;

    /// <summary>Tutti i .scf dell'interfaccia Full HD (_FHD): quelli del teatro sostituiscono quelli della KTO con lo stesso percorso.</summary>
    public Dictionary<string, (string Path, bool Theater)> ScfFiles()
    {
        if (_scf != null) return _scf;
        var d = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        // tutte le finestre dell'Art (non CkptArt, non Help): quelle in _FHD (Full HD) sostituiscono le vecchie con lo stesso percorso
        void Scan(string root, bool theater)
        {
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.EnumerateFiles(root, "*.scf", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('/', '\\');
                string top = rel.Split('\\')[0];
                if (top.Equals("CkptArt", StringComparison.OrdinalIgnoreCase) || top.Equals("Help", StringComparison.OrdinalIgnoreCase)) continue;
                d[rel] = (f, theater);
            }
        }
        Scan(Base, false);
        Scan(Work, true);
        // le finestre vecchie (1024×768) di una cartella che ha la sua versione _FHD non si usano (es. Dgft\Load\df_pua.scf rimasto in un
        // teatro: in BMS non compare); quelle delle cartelle senza _FHD (Comms, Logbook) si'
        var fhdTops = d.Keys.Where(k => k.StartsWith(@"_FHD\", StringComparison.OrdinalIgnoreCase)).Select(k => k.Split('\\')[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in d.Keys.Where(k => !k.StartsWith(@"_FHD\", StringComparison.OrdinalIgnoreCase) && (d.ContainsKey(@"_FHD\" + k) || fhdTops.Contains(k.Split('\\')[0]))).ToList()) d.Remove(k);
        return _scf = d;
    }

    private readonly Dictionary<string, List<ScfWindow>> _wins = new(StringComparer.OrdinalIgnoreCase);

    public List<ScfWindow> Windows(string rel)
    {
        if (_wins.TryGetValue(rel, out var w)) return w;
        var f = ScfFiles();
        w = _art.ScfEdited(rel) is { } ed ? Scf.Parse(ed, rel) : f.TryGetValue(rel, out var p) ? SafeLoad(p.Path, rel) : new List<ScfWindow>();
        return _wins[rel] = w;
    }

    private static List<ScfWindow> SafeLoad(string path, string rel)
    {
        try { return Scf.Load(path, rel); } catch (Exception) { return new List<ScfWindow>(); }
    }

    public List<ScfWindow> AllWindows() => ScfFiles().Keys.SelectMany(Windows).ToList();
    public bool ScfFromTheater(string rel) => _art.ScfEdited(rel) != null || ScfFiles().TryGetValue(rel, out var p) && p.Theater;

    // ───────────────────────── immagini ─────────────────────────

    public sealed record ImgRef(string Archive, bool Theater, ResourceBundle.Entry Entry);
    private Dictionary<string, List<ImgRef>>? _img;

    /// <summary>Indice nome → archivi che hanno l'immagine, nell'ordine in cui BMS li usa (teatro prima della KTO, _FHD prima di resource).</summary>
    private Dictionary<string, List<ImgRef>> Index()
    {
        if (_img != null) return _img;
        var d = new Dictionary<string, List<ImgRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, theater) in new[] { (Work, true), (Base, false) })
            foreach (var sub in new[] { "_FHD", "resource" })
            {
                string? dir = ArtSet.FindDir(root, sub);
                if (dir == null) continue;
                foreach (var idx in Directory.EnumerateFiles(dir, "*.idx"))
                {
                    string rel = Path.GetRelativePath(root, idx).Replace('/', '\\');
                    foreach (var e in ReadIndex(idx))
                    {
                        if (!d.TryGetValue(e.Name, out var l)) d[e.Name] = l = new List<ImgRef>();
                        l.Add(new ImgRef(rel, theater, e));
                    }
                }
            }
        return _img = d;
    }

    private static List<ResourceBundle.Entry> ReadIndex(string idx)
    {
        var res = new List<ResourceBundle.Entry>();
        try
        {
            var b = File.ReadAllBytes(idx);
            for (int o = 8; o + 44 <= b.Length;)
            {
                uint t = BitConverter.ToUInt32(b, o);
                int len = t switch { 100 => 60, 101 => 52, 102 => 44, _ => -1 };
                if (len < 0 || o + len > b.Length) break;
                if (t == 100) res.Add(new ResourceBundle.Entry { Record = b.AsSpan(o, len).ToArray() });
                o += len;
            }
        }
        catch (Exception) { }
        return res;
    }

    public ImgRef? FindImage(string name) => Index().TryGetValue(name, out var l) && l.Count > 0 ? l[0] : null;

    /// <summary>Immagine di un file .tga ([LOADIMAGE] NOME "art\...\x.tga" negli ImageRc.irc), o una sua parte ([ADDIMAGE] NOME x y w h).</summary>
    public sealed record TgaRef(string File, bool Theater, int X, int Y, int W, int H);
    private Dictionary<string, TgaRef>? _tga;

    public TgaRef? FindTga(string name)
    {
        if (_tga == null)
        {
            _tga = new Dictionary<string, TgaRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var (root, _) in new[] { (Work, true), (Base, false) })
            {
                if (!Directory.Exists(root)) continue;
                var files = Directory.EnumerateFiles(root, "*.irc", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).Equals("ImageRc.irc", StringComparison.OrdinalIgnoreCase) && !f.Contains("CkptArt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Contains("_FHD", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                foreach (var f in files)
                {
                    string? cur = null; bool curTh = false;
                    foreach (var l in File.ReadAllLines(f, Encoding.Latin1))
                    {
                        var t = l.Trim();
                        if (t.StartsWith("[LOADIMAGE]", StringComparison.OrdinalIgnoreCase))
                        {
                            var a = Scf.Split(t[11..]);
                            if (a.Length < 2) continue;
                            string rel = a[1].Replace('/', '\\');
                            if (rel.StartsWith(@"art\", StringComparison.OrdinalIgnoreCase)) rel = rel[4..];
                            // il file del teatro sostituisce quello della KTO
                            string? path = TheaterArtJob.FindCI(Work, rel);
                            curTh = path != null;
                            path ??= TheaterArtJob.FindCI(Base, rel);
                            cur = path;
                            if (path != null) _tga.TryAdd(a[0], new TgaRef(path, curTh, 0, 0, 0, 0));
                        }
                        else if (t.StartsWith("[ADDIMAGE]", StringComparison.OrdinalIgnoreCase) && cur != null)
                        {
                            var a = Scf.Split(t[10..]);
                            if (a.Length >= 5 && int.TryParse(a[1], out int x) && int.TryParse(a[2], out int y) && int.TryParse(a[3], out int w) && int.TryParse(a[4], out int h))
                                _tga.TryAdd(a[0], new TgaRef(cur, curTh, x, y, w, h));
                        }
                    }
                }
            }
        }
        return _tga.TryGetValue(name, out var r) ? r : null;
    }

    private readonly Dictionary<string, Pic?> _tgaFiles = new(StringComparer.OrdinalIgnoreCase);

    private Pic? TgaImage(TgaRef r)
    {
        if (!_tgaFiles.TryGetValue(r.File, out var full))
        {
            full = null;
            try
            {
                var (rgba, w, h) = TerrainTool.Core.Models.TgaImage.Read(File.ReadAllBytes(r.File));
                var b = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    byte R = rgba[4 * i], G = rgba[4 * i + 1], B = rgba[4 * i + 2];
                    bool key = R >= 248 && G <= 8 && B >= 248;     // magenta = trasparente, come negli archivi
                    b[4 * i] = B; b[4 * i + 1] = G; b[4 * i + 2] = R; b[4 * i + 3] = key ? (byte)0 : rgba[4 * i + 3];
                }
                full = new Pic(w, h, b);
            }
            catch (Exception) { }
            _tgaFiles[r.File] = full;
        }
        if (full == null) return null;
        if (r.W <= 0 || r.H <= 0) return full;
        return ImageOps.Crop(full, new System.Windows.Int32Rect(r.X, r.Y, r.W, r.H));
    }
    /// <summary>Tutti gli archivi (relativi) che hanno l'immagine, del teatro e della KTO.</summary>
    public List<string> ImageArchives(string name) => Index().TryGetValue(name, out var l) ? l.Select(x => x.Archive).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new();

    private readonly Dictionary<string, Pic?> _pics = new(StringComparer.OrdinalIgnoreCase);

    public Pic? Image(string name)
    {
        if (_pics.TryGetValue(name, out var p)) return p;
        p = null;
        var r = FindImage(name);
        if (r != null)
        {
            // modifiche non salvate: l'archivio in memoria dell'ArtSet
            if (_art.Edited(r.Archive) is { } eb && eb.Find(name) is { IsImage: true } ee) p = ImageOps.FromBundle(eb, ee);
            else p = Read(Path.Combine(r.Theater ? Work : Base, r.Archive.Replace('\\', Path.DirectorySeparatorChar)), r.Entry);
        }
        if (p == null && FindTga(name) is { } tr) p = TgaImage(tr);
        return _pics[name] = p;
    }

    public void Forget() { _pics.Clear(); _img = null; _scf = null; _wins.Clear(); _texts = null; _tga = null; _tgaFiles.Clear(); _fontFiles = null; _fonts.Clear(); }

    private static Pic? Read(string idx, ResourceBundle.Entry e)
    {
        try
        {
            string rsc = Path.ChangeExtension(idx, ".rsc");
            if (!File.Exists(rsc)) rsc = Path.ChangeExtension(idx, ".RSC");
            using var fs = new FileStream(rsc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int w = e.Width, h = e.Height, bpp = e.BytesPerPixel;
            var px = new byte[(long)w * h * bpp];
            fs.Position = 8 + e.ImageOffset; fs.ReadExactly(px);
            var pal = new byte[bpp == 1 ? e.PaletteSize * 2 : 0];
            if (pal.Length > 0) { fs.Position = 8 + e.PaletteOffset; fs.ReadExactly(pal); }
            var res = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                ushort v = bpp == 1 ? (px[i] * 2 + 1 < pal.Length ? BitConverter.ToUInt16(pal, px[i] * 2) : (ushort)0) : BitConverter.ToUInt16(px, i * 2);
                res[4 * i] = (byte)((v & 31) * 255 / 31); res[4 * i + 1] = (byte)(((v >> 5) & 31) * 255 / 31); res[4 * i + 2] = (byte)(((v >> 10) & 31) * 255 / 31);
                res[4 * i + 3] = (byte)(v == 0x7C1F ? 0 : 255);
            }
            return new Pic(w, h, res);
        }
        catch (Exception) { return null; }
    }

    // ───────────────────────── testi ─────────────────────────

    public sealed record TextRef(string Text, string File, bool Theater)
    {
        /// <summary>I testi di Theater.irc sono del teatro (anche se il teatro non ha ancora il suo file): si possono cambiare.</summary>
        public bool TheaterText => System.IO.Path.GetFileName(File).Equals("Theater.irc", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, TextRef>? _texts;

    public TextRef? Text(string key)
    {
        if (_texts == null)
        {
            _texts = new Dictionary<string, TextRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var (root, theater) in new[] { (Work, true), (Base, false) })
                foreach (var sub in new[] { @"_FHD\Main", "Main" })
                {
                    string? dir = ArtSet.FindDir(root, sub);
                    if (dir == null) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.irc"))
                    {
                        string rel = Path.GetRelativePath(root, f);
                        try
                        {
                            foreach (var (k, t) in IrcFile.Load(f).Entries()) _texts.TryAdd(k, new TextRef(t, rel, theater));
                        }
                        catch (Exception) { }
                    }
                }
            // modifiche non salvate di Theater.irc
            if (_art.Irc() is { } irc)
                foreach (var (k, t) in irc.Entries())
                    if (_texts.TryGetValue(k, out var old) && old.TheaterText) _texts[k] = old with { Text = t };
        }
        return _texts.TryGetValue(key, out var r) ? r : null;
    }

    // ───────────────────────── font ─────────────────────────

    private Dictionary<string, string>? _fontFiles;
    private readonly Dictionary<string, BftFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Font dell'interfaccia (nome → file .bft) da Fonts\FontRc.irc, del teatro o della KTO.</summary>
    public Dictionary<string, string> FontFiles()
    {
        if (_fontFiles != null) return _fontFiles;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _art.FontRcLines())
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*\[LOADFONT\]\s+(\S+)\s+""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string p = m.Groups[2].Value.Replace('/', '\\');
                if (p.StartsWith(@"art\", StringComparison.OrdinalIgnoreCase)) p = p[4..];
                d[m.Groups[1].Value] = p;
            }
        return _fontFiles = d;
    }

    public BftFont? Font(string? name)
    {
        name ??= "DEFAULT";
        if (_fonts.TryGetValue(name, out var f)) return f;
        f = null;
        if (FontFiles().TryGetValue(name, out var rel) || FontFiles().TryGetValue("DEFAULT", out rel))
        {
            try { if (_art.FontBytes(rel) is { } b) f = BftFont.Load(b, rel); } catch (Exception) { }
        }
        return _fonts[name] = f;
    }

    /// <summary>Quante volte ogni font e' scelto nelle finestre (.scf) dell'interfaccia: righe [FONT] e file in cui compare.</summary>
    public Dictionary<string, (int Count, SortedSet<string> Files)> FontUsage()
    {
        var d = new Dictionary<string, (int, SortedSet<string>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in ScfFiles().Keys)
            foreach (var w in Windows(rel))
                foreach (var it in w.Items.Prepend(w.Head))
                    foreach (var a in it.All("FONT"))
                    {
                        if (a.Length == 0) continue;
                        if (!d.TryGetValue(a[0], out var u)) u = (0, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                        u.Item2.Add(rel);
                        d[a[0]] = (u.Item1 + 1, u.Item2);
                    }
        return d;
    }
}
