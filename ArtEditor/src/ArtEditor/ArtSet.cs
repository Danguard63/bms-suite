using System.IO;
using TerrainTool.Core.Dds;

namespace ArtEditor;

/// <summary>
/// L'Art che si modifica, come lo vede BMS: la cartella Art del teatro (artdir) contiene solo i file che il teatro cambia, il resto
/// viene dall'Art di BMS (Data\Art). Qui: Base = Data\Art (solo lettura), Work = cartella Art del teatro (dove si salva),
/// Reference = un altro Art (di un altro teatro o di un vecchio add-on) da cui prendere immagini, solo lettura.
/// Le modifiche restano in memoria finche' non si salva; prima di riscrivere un file se ne fa una copia in Backup.
/// </summary>
public sealed class ArtSet
{
    public string BaseRoot { get; }
    public string WorkRoot { get; private set; }

    /// <summary>
    /// Sposta il lavoro su un'altra cartella (Save as a copy): le modifiche non salvate restano in memoria e il prossimo Save le scrive
    /// nella cartella nuova. I file del teatro gia' letti restano validi perche' la cartella nuova ne e' una copia.
    /// </summary>
    public void Retarget(string root) { Writable(); WorkRoot = root; }
    public string? ReferenceRoot { get; private set; }
    public string Label { get; }

    private readonly Dictionary<string, ResourceBundle?> _base = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceBundle?> _work = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceBundle?> _ref = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Voci cambiate in questa sessione (archivio|voce), per il segno "changed".</summary>
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    public ArtSet(string baseRoot, string workRoot, string label)
    {
        BaseRoot = baseRoot; WorkRoot = workRoot; Label = label;
    }

    /// <summary>
    /// Sola lettura: la KTO (Data\Art), aperta solo per guardarla (stemmi, schermate, bandiere). Tutto viene dalla KTO, niente si
    /// cambia e niente si scrive: ogni modifica e il salvataggio danno ReadOnlyArtException.
    /// </summary>
    public bool ReadOnly { get; init; }

    private void Writable() { if (ReadOnly) throw new ReadOnlyArtException(); }

    /// <summary>
    /// La cartella che contiene davvero _FHD, Resource, Main: alcune artdir puntano alla cartella che contiene "Art"
    /// (es. Add-On Mideast128\NT), altre direttamente all'Art.
    /// </summary>
    public static string ArtRoot(string dir)
    {
        string inner = Path.Combine(dir, "Art");
        bool Has(string d) => Directory.Exists(d) && (FindDir(d, "_FHD") != null || FindDir(d, "Resource") != null || FindDir(d, "Main") != null);
        return !Has(dir) && Directory.Exists(inner) ? inner : dir;
    }

    /// <summary>Cartella cercata senza distinzione di maiuscole (FindCI trova solo file).</summary>
    public static string? FindDir(string root, string rel)
    {
        string cur = root;
        foreach (var part in rel.Split('\\', '/'))
        {
            if (!Directory.Exists(cur)) return null;
            var hit = Directory.EnumerateDirectories(cur).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            cur = hit;
        }
        return Directory.Exists(cur) ? cur : null;
    }

    public bool IsDirty => _dirty.Count > 0;
    public IEnumerable<string> DirtyFiles => _dirty;

    // ───────────────────────── archivi ─────────────────────────

    private static ResourceBundle? Open(Dictionary<string, ResourceBundle?> cache, string? root, string rel)
    {
        if (cache.TryGetValue(rel, out var b)) return b;
        ResourceBundle? r = null;
        string? f = root == null ? null : TheaterArtJob.FindCI(root, rel);
        if (f != null && (File.Exists(Path.ChangeExtension(f, ".rsc")) || File.Exists(Path.ChangeExtension(f, ".RSC"))))
            try { r = ResourceBundle.Load(f); } catch (Exception) { r = null; }
        return cache[rel] = r;
    }

    public ResourceBundle? Base(string rel) => Open(_base, BaseRoot, rel);
    public ResourceBundle? WorkOnly(string rel) => ReadOnly ? null : Open(_work, WorkRoot, rel);
    public ResourceBundle? Reference(string rel) => ReferenceRoot == null ? null : Open(_ref, ReferenceRoot, rel);
    /// <summary>L'archivio come lo vede BMS: quello del teatro se c'e', se no quello di BMS.</summary>
    public ResourceBundle? Effective(string rel) => WorkOnly(rel) ?? Base(rel);
    public bool InWork(string rel) => WorkOnly(rel) != null;
    /// <summary>L'archivio del teatro cambiato e non ancora salvato (per vederne subito le immagini), se c'e'.</summary>
    public ResourceBundle? Edited(string rel) => _dirty.Contains(rel) ? WorkOnly(rel) : null;

    /// <summary>L'archivio del teatro da modificare: se il teatro non ce l'ha ancora, si parte da una copia di quello di BMS.</summary>
    private ResourceBundle? Editable(string rel)
    {
        Writable();
        var w = WorkOnly(rel);
        if (w != null) return w;
        if (TheaterArtJob.FindCI(BaseRoot, rel) is not { } f) return null;
        w = ResourceBundle.Load(f);
        _work[rel] = w;
        return w;
    }

    public void SetReference(string? root)
    {
        ReferenceRoot = root == null ? null : ArtRoot(root);
        _ref.Clear();
    }

    // ───────────────────────── caselle ─────────────────────────

    /// <summary>(archivio, voce) dove la casella esiste nell'Art effettivo.</summary>
    public IEnumerable<(string Rel, ResourceBundle.Entry E)> Targets(ArtSlot s)
    {
        foreach (var rel in s.Archives)
        {
            var b = Effective(rel);
            if (b == null) continue;
            foreach (var n in s.Entries)
                if (b.Find(n) is { IsImage: true } e) yield return (rel, e);
        }
    }

    public Pic? Image(ArtSlot s, string? which = null)
    {
        string n = s.PreviewEntry;
        // prima gli archivi che il teatro ha davvero (es. Israel ha solo resource\select: BMS mostra quelle, non le _FHD della Korea)
        var order = which == null ? s.Archives.OrderBy(a => InWork(a) ? 0 : 1).ToArray()
                  : which == "ref" ? s.Archives.OrderBy(a => Reference(a) != null ? 0 : 1).ToArray() : s.Archives;
        foreach (var rel in order)
        {
            var b = which switch { "base" => Base(rel), "ref" => Reference(rel), _ => Effective(rel) };
            if (b?.Find(n) is { IsImage: true } e) return ImageOps.FromBundle(b, e);
        }
        return null;
    }

    public enum State { Missing, Default, Theater, Changed }

    public State StateOf(ArtSlot s)
    {
        if (s.Entries.Any(n => s.Archives.Any(a => _changed.Contains(a + "|" + n)))) return State.Changed;
        bool any = false, diff = false;
        foreach (var rel in s.Archives)
        {
            var e = Effective(rel);
            if (e == null) continue;
            var b = Base(rel);
            foreach (var n in s.Entries)
            {
                if (e.Find(n) is not { IsImage: true } we) continue;
                any = true;
                if (!InWork(rel)) continue;
                if (b?.Find(n) is not { IsImage: true } be || !Same(e, we, b, be)) diff = true;
            }
        }
        return !any ? State.Missing : diff ? State.Theater : State.Default;
    }

    private static bool Same(ResourceBundle a, ResourceBundle.Entry ae, ResourceBundle b, ResourceBundle.Entry be)
    {
        if (ae.Width != be.Width || ae.Height != be.Height || ae.Flags != be.Flags) return false;
        var (pa, la) = a.ImageBytes(ae); var (pb, lb) = b.ImageBytes(be);
        return pa.AsSpan().SequenceEqual(pb) && la.AsSpan().SequenceEqual(lb);
    }

    /// <summary>Misure delle voci, per il pannello dei dettagli (es. "_FHD\Main.idx  MAIN_SCRN 1920×1080").</summary>
    public List<string> Sizes(ArtSlot s) => Targets(s).Select(t => $"{t.Rel}  {t.E.Name}  {t.E.Width}×{t.E.Height}{(ImageOps.UsesKey(t.E) ? "  (transparent key)" : "")}").ToList();

    /// <summary>
    /// Regole per l'immagine da mettere nella casella (mostrate nel pannello e nella finestra del ritaglio): misura, colori,
    /// trasparenza, consigli della casella.
    /// </summary>
    public List<string> Rules(ArtSlot s)
    {
        var t = Targets(s).ToList();
        var r = new List<string>();
        if (t.Count == 0) return r;
        var main = s.Kind == SlotKind.Flag ? t.Where(x => x.E.Name.EndsWith("_HRZ", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(t[0]).First() : t.OrderByDescending(x => x.E.Width * x.E.Height).First();
        int w = main.E.Width, h = main.E.Height;
        var sizes = t.Select(x => $"{x.E.Width}×{x.E.Height}").Distinct().ToList();
        if (s.Kind == SlotKind.Flag)
            r.Add($"Size: BMS needs the flag at {w}×{h} (horizontal), 20×12 (small) and {h}×{w} (vertical). Use an image of at least {w}×{h}: a smaller one is enlarged and looks blurred.");
        else
            r.Add($"Size: BMS needs {string.Join(" and ", sizes)} pixels. Use an image of at least {w}×{h}: a smaller one is enlarged and looks blurred.");
        r.Add($"Shape: about {w}:{h} ({(double)w / h:0.00} : 1). Any image can be used: you choose the part to keep, nothing is stretched." +
              (s.Kind != SlotKind.Flag && sizes.Count > 1 && t.Select(x => Math.Round((double)x.E.Width / x.E.Height, 2)).Distinct().Count() > 1 ? " The classic interface has a different shape: keep the important part in the centre." : ""));
        bool eight = t.Any(x => x.E.BytesPerPixel == 1), key = t.Any(x => ImageOps.UsesKey(x.E));
        r.Add(eight ? "Colours: 256 colours (palette). Flat colours look best; photos and smooth gradients lose quality."
                    : "Colours: 32,768 colours (16 bit). Photos are fine; very smooth gradients can show bands.");
        r.Add(key ? "Transparency: yes, but only on/off (no soft edges or shadows). Use a PNG with a transparent background. Pure magenta (255,0,255) is the transparent colour of BMS: do not use it."
                  : "Transparency: no. Transparent parts of the image become black.");
        r.Add("Files: PNG, JPG, BMP, GIF, TIFF, TGA or DDS.");
        switch (s.Kind)
        {
            case SlotKind.Flag: r.Add("Flag: one horizontal flag. The editor also makes the small (20×12), vertical and darker versions; small details disappear at 20×12, simple designs work best."); break;
            case SlotKind.Patch: r.Add("Patch: emblem on a transparent background; it is shown small in the lists, keep it bold and simple."); break;
        }
        if (s.Category == ArtCatalog.Campaigns) r.Add("Campaign picture: BMS writes the campaign name over the lower part. Do not put text in the picture and keep the bottom simple.");
        if (s.Category == ArtCatalog.Maps) r.Add("Small map: north up, the whole theater square. Normally made by the TerrainTool from the terrain.");
        if (s.Category == ArtCatalog.Menu && s.Entries[0].StartsWith("Korea", StringComparison.OrdinalIgnoreCase)) r.Add("Banner: the theater name as text on a transparent background, light colour.");
        return r;
    }

    /// <summary>File che l'editor gestisce (archivi delle caselle, patch, testi): solo questi vengono copiati in un teatro, mai CkptArt o altro.</summary>
    public static bool Managed(string rel)
    {
        rel = rel.Replace('/', '\\');
        string noExt = Path.ChangeExtension(rel, null) ?? rel;
        var archives = ArtCatalog.Slots.SelectMany(x => x.Archives).Append(ArtCatalog.PatchArchive).Select(a => Path.ChangeExtension(a, null)!);
        bool arc = rel.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase);
        if (arc && archives.Any(a => a.Equals(noExt, StringComparison.OrdinalIgnoreCase))) return true;
        // archivi dell'interfaccia cambiati dall'editor visuale (_FHD\x o resource\x, non la mappa 2D campmap)
        var parts = rel.Split('\\');
        if (arc && parts.Length == 2 && (parts[0].Equals("_FHD", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("resource", StringComparison.OrdinalIgnoreCase))
            && !parts[1].StartsWith("campmap", StringComparison.OrdinalIgnoreCase)) return true;
        // finestre dell'interfaccia del teatro (copie cambiate dall'editor visuale)
        if (rel.EndsWith(".scf", StringComparison.OrdinalIgnoreCase) && parts[0].Equals("_FHD", StringComparison.OrdinalIgnoreCase)) return true;
        // suoni dell'interfaccia del teatro (sezione Sounds)
        if (rel.Equals(SoundList, StringComparison.OrdinalIgnoreCase) || rel.Equals(TheaterSoundRc, StringComparison.OrdinalIgnoreCase)) return true;
        if (parts.Length == 2 && parts[0].Equals("Sounds", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return true;
        // font dell'interfaccia del teatro (cambiati o aggiunti con la sezione Fonts)
        if (parts.Length == 2 && parts[0].Equals("Fonts", StringComparison.OrdinalIgnoreCase)
            && (rel.EndsWith(".bft", StringComparison.OrdinalIgnoreCase) || rel.Equals(FontRc, StringComparison.OrdinalIgnoreCase) || rel.Equals(FontIds, StringComparison.OrdinalIgnoreCase))) return true;
        return rel.Equals(ArtCatalog.PatchIds, StringComparison.OrdinalIgnoreCase) || IrcFiles.Any(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mette un'immagine nella casella. 'crop' = riquadro scelto nell'immagine per la prima voce (le altre misure ne ricavano uno
    /// con lo stesso centro). Restituisce le voci scritte.
    /// </summary>
    public List<string> Replace(ArtSlot s, Pic src, System.Windows.Int32Rect crop)
    {
        Writable();
        var done = new List<string>();
        if (s.Kind == SlotKind.Flag)
        {
            var hor = ImageOps.Crop(src, crop);
            foreach (var (rel, e) in Targets(s).ToList())
            {
                Pic p = e.Name.EndsWith("_DRK", StringComparison.OrdinalIgnoreCase) ? ImageOps.Vertical(hor, e.Width, e.Height, true)
                      : e.Name.StartsWith("OOB_FLAGS_", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith("_HRZ", StringComparison.OrdinalIgnoreCase) ? ImageOps.Vertical(hor, e.Width, e.Height, false)
                      : ImageOps.Resize(hor, e.Width, e.Height);
                Put(rel, e, p); done.Add($"{rel} {e.Name}");
            }
            return done;
        }
        foreach (var (rel, e) in Targets(s).ToList())
        {
            var r = ImageOps.Derive(crop, src.W, src.H, (double)e.Width / e.Height);
            Put(rel, e, ImageOps.Resize(ImageOps.Crop(src, r), e.Width, e.Height));
            done.Add($"{rel} {e.Name}");
        }
        return done;
    }

    private void Put(string rel, ResourceBundle.Entry e, Pic p)
    {
        var w = Editable(rel)!;
        bool key = ImageOps.UsesKey(e);
        w.SetImageRgb(e.Name, ImageOps.ToRgb(p, key), p.W, p.H, key);
        _dirty.Add(rel); _changed.Add(rel + "|" + e.Name);
    }

    /// <summary>Riporta la casella all'immagine di BMS (solo negli archivi che il teatro ha).</summary>
    public int Reset(ArtSlot s)
    {
        Writable();
        int n = 0;
        foreach (var rel in s.Archives)
        {
            if (!InWork(rel) || Base(rel) is not { } b) continue;
            var w = WorkOnly(rel)!;
            foreach (var name in s.Entries)
                if (b.Find(name) is { IsImage: true } && w.CopyImageFrom(b, name)) { n++; _dirty.Add(rel); _changed.Add(rel + "|" + name); }
        }
        return n;
    }

    /// <summary>
    /// Prende la casella dall'Art di riferimento: voce uguale se ha la stessa misura, se no ridimensionata (es. un vecchio add-on con
    /// l'interfaccia a 1024×768). Per le bandiere si prende la versione orizzontale e si rifanno le altre.
    /// </summary>
    public List<string> FromReference(ArtSlot s)
    {
        Writable();
        var done = new List<string>();
        if (s.Kind == SlotKind.Flag)
        {
            var hor = Image(s, "ref");
            if (hor != null) done.AddRange(Replace(s, hor, new System.Windows.Int32Rect(0, 0, hor.W, hor.H)));
            return done;
        }
        foreach (var (rel, e) in Targets(s).ToList())
        {
            // la stessa voce nell'archivio corrispondente del riferimento, o in uno qualsiasi degli archivi della casella
            ResourceBundle? rb = null; ResourceBundle.Entry? re = null;
            foreach (var r in new[] { rel }.Concat(s.Archives))
                if (Reference(r)?.Find(e.Name) is { IsImage: true } x) { rb = Reference(r); re = x; break; }
            if (rb == null || re == null) continue;
            if (re.Width == e.Width && re.Height == e.Height && re.Flags == e.Flags) { Editable(rel)!.CopyImageFrom(rb, e.Name); _dirty.Add(rel); _changed.Add(rel + "|" + e.Name); }
            else
            {
                var p = ImageOps.FromBundle(rb, re);
                Put(rel, e, ImageOps.Resize(ImageOps.Crop(p, ImageOps.CenterCrop(p.W, p.H, (double)e.Width / e.Height)), e.Width, e.Height));
            }
            done.Add($"{rel} {e.Name}");
        }
        return done;
    }

    // ───────────────────────── campagne ─────────────────────────

    /// <summary>
    /// Le immagini delle campagne come le usa il teatro: la finestra di scelta (Campaign\Select\Cs_Load.scf, del teatro o di BMS)
    /// dice quale immagine ha il pulsante di ogni campagna (Israel per esempio scambia la 4 e la 5) e quali campagne sono spente
    /// (riga [IMAGES] commentata).
    /// </summary>
    public List<ArtSlot> CampaignSlots()
    {
        var def = ArtCatalog.Slots.Where(x => x.Category == ArtCatalog.Campaigns).ToList();
        string? scf = null;
        foreach (var rel in new[] { @"_FHD\Campaign\Select\Cs_Load.scf", @"Campaign\Select\Cs_Load.scf" })
            if ((TheaterArtJob.FindCI(WorkRoot, rel) ?? TheaterArtJob.FindCI(BaseRoot, rel)) is { } f) { scf = f; break; }
        if (scf == null) return def;
        var res = new List<ArtSlot>();
        int cur = 0;
        var found = new Dictionary<int, (string Img, bool Off)>();
        foreach (var raw in File.ReadAllLines(scf, System.Text.Encoding.Latin1))
        {
            string l = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^\[SETUP\]\s*CS_LOAD_SCENARIO(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { cur = int.Parse(m.Groups[1].Value); continue; }
            m = System.Text.RegularExpressions.Regex.Match(l, @"^(#?)\s*\[IMAGES\]\s*([A-Za-z0-9_]+)");
            if (m.Success && cur > 0 && !found.ContainsKey(cur)) found[cur] = (m.Groups[2].Value, m.Groups[1].Value == "#");
        }
        var names = Irc();
        for (int i = 1; i <= Math.Max(6, found.Keys.DefaultIfEmpty(0).Max()); i++)
        {
            var d = def.ElementAtOrDefault(i - 1);
            if (!found.TryGetValue(i, out var f)) { if (d != null) res.Add(d); continue; }
            string title = names?.Get($"TXT_SCENARIO_{i}") is { Length: > 0 } t ? $"Campaign {i} · {t}" : $"Campaign {i} picture";
            res.Add(new ArtSlot(ArtCatalog.Campaigns, title,
                $"Campaign selection screen, button of campaign {i} (image {f.Img}, set in {Path.GetFileName(scf)})." +
                (f.Off ? " This campaign is switched off in the selection screen (its [IMAGES] line is commented)." : "") +
                " BMS writes the campaign name over the picture.",
                SlotKind.Image, d?.Archives ?? new[] { @"_FHD\Select.idx", @"Resource\Select.idx" }, new[] { f.Img }));
        }
        return res;
    }

    // ───────────────────────── patch ─────────────────────────

    private List<string>? _ids;
    private bool _idsDirty;

    /// <summary>Righe di Patches\ImageIds.id (nome TAB numero), del teatro o di BMS.</summary>
    public List<string> PatchIdLines()
    {
        if (_ids != null) return _ids;
        string? f = TheaterArtJob.FindCI(WorkRoot, ArtCatalog.PatchIds) ?? TheaterArtJob.FindCI(BaseRoot, ArtCatalog.PatchIds);
        return _ids = f != null ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : new List<string>();
    }

    public Dictionary<string, int> PatchIds()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in PatchIdLines())
        {
            if (l.StartsWith('#')) continue;
            var v = l.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (v.Length >= 2 && int.TryParse(v[1], out int id)) d.TryAdd(v[0], id);
        }
        return d;
    }

    public List<ArtSlot> PatchSlots()
    {
        var b = Effective(ArtCatalog.PatchArchive);
        if (b == null) return new();
        var ids = PatchIds();
        return b.Entries.Where(e => e.IsImage).Select(e => ArtCatalog.PatchSlot(e.Name, ids.TryGetValue(e.Name, out int id) ? id : null)).ToList();
    }

    /// <summary>Patch nuova: voce nell'archivio (alla misura delle altre) e riga in ImageIds.id con il numero dopo l'ultimo.</summary>
    public int AddPatch(string name, Pic src, System.Windows.Int32Rect crop)
    {
        Writable();
        var w = Editable(ArtCatalog.PatchArchive) ?? throw new InvalidOperationException("Patches archive not found in BMS");
        var model = w.Entries.FirstOrDefault(e => e.IsImage);
        int pw = model?.Width ?? 96, ph = model?.Height ?? 96;
        var p = ImageOps.Resize(ImageOps.Crop(src, ImageOps.Derive(crop, src.W, src.H, (double)pw / ph)), pw, ph);
        w.SetImageRgb(name, ImageOps.ToRgb(p, true), pw, ph, true);
        // stesso tipo delle altre patch (16 bit con trasparenza)
        if (model != null && w.Find(name) is { } ne) BitConverter.TryWriteBytes(ne.Record.AsSpan(36, 4), (model.Flags & 0x40000000u) | 2u);
        int id = PatchIds().Values.DefaultIfEmpty(70000).Max() + 1;
        PatchIdLines().Add($"{name}\t{id}");
        _idsDirty = true;
        _dirty.Add(ArtCatalog.PatchArchive); _changed.Add(ArtCatalog.PatchArchive + "|" + name);
        return id;
    }

    // ───────────────────────── testi ─────────────────────────

    public static readonly string[] IrcFiles = { @"Main\Theater.irc", @"_FHD\Main\Theater.irc" };
    private IrcFile? _irc, _ircBase;
    private bool _ircDirty;

    public IrcFile? Irc()
    {
        if (_irc != null) return _irc;
        foreach (var rel in IrcFiles)
            if ((TheaterArtJob.FindCI(WorkRoot, rel) ?? TheaterArtJob.FindCI(BaseRoot, rel)) is { } f) return _irc = IrcFile.Load(f);
        return null;
    }

    public IrcFile? IrcBase()
    {
        if (_ircBase != null) return _ircBase;
        foreach (var rel in IrcFiles)
            if (TheaterArtJob.FindCI(BaseRoot, rel) is { } f) return _ircBase = IrcFile.Load(f);
        return null;
    }

    public void SetText(string key, string text)
    {
        Writable();
        var i = Irc();
        if (i == null || i.Get(key) == text) return;
        i.Set(key, text);
        _ircDirty = true; _dirty.Add(IrcFiles[0]);
    }

    // ───────────────────────── finestre (.scf) ─────────────────────────

    private readonly Dictionary<string, List<string>> _scf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Righe di un .scf cambiato e non ancora salvato (null se non cambiato).</summary>
    public List<string>? ScfEdited(string rel) => _scf.TryGetValue(rel, out var l) ? l : null;

    /// <summary>
    /// Righe attuali di un .scf: quelle cambiate, se no il file del teatro, se no quello della KTO (da cui il teatro avra' la sua copia
    /// al salvataggio: la KTO non si tocca).
    /// </summary>
    public List<string>? ScfLines(string rel)
    {
        if (_scf.TryGetValue(rel, out var l)) return l;
        string? f = TheaterArtJob.FindCI(WorkRoot, rel) ?? TheaterArtJob.FindCI(BaseRoot, rel);
        return f != null ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : null;
    }

    public void SetScf(string rel, List<string> lines) { Writable(); _scf[rel] = lines; _dirty.Add(rel); }

    // ───────────────────────── immagini nuove (stati dei pulsanti) ─────────────────────────

    /// <summary>
    /// Aggiunge (o rifa') un'immagine con un nome nuovo nello stesso archivio di un'altra (es. la versione "premuta" di un pulsante):
    /// stesso formato (8/16 bit, trasparenza magenta) dell'immagine di riferimento. L'archivio della KTO non si tocca: se il teatro
    /// non ce l'ha ancora, se ne fa una copia nel teatro.
    /// </summary>
    public void AddImage(string archive, string name, Pic pic, string? like = null)
    {
        Writable();
        var w = Editable(archive) ?? throw new InvalidOperationException(archive + " not found");
        var model = like != null ? w.Find(like) : null;
        bool key = model != null && (model.Flags & 0x40000000u) != 0;
        w.SetImageRgb(name, ImageOps.ToRgb(pic, key), pic.W, pic.H, key);
        if (model != null && w.Find(name) is { } ne)
        {
            // stesso tipo dell'immagine di partenza (a 16 bit: 8 bit con palette ridotta perderebbe le sfumature del rilievo)
            BitConverter.TryWriteBytes(ne.Record.AsSpan(36, 4), (model.Flags & 0x40000000u) | 2u);
        }
        _dirty.Add(archive); _changed.Add(archive + "|" + name);
    }

    // ───────────────────────── suoni dell'interfaccia (SoundRc.irc, Main_Snd.lst) ─────────────────────────

    /// <summary>Cartella Data di BMS (quella che contiene Art).</summary>
    public string DataDir => Path.GetDirectoryName(BaseRoot.TrimEnd('\\', '/'))!;

    /// <summary>
    /// Percorso dell'Art del teatro rispetto a Data, come lo vuole Main_Snd.lst (es. "Add-On Israel\art"); null se la cartella non e'
    /// dentro Data di BMS (es. una copia fatta con Save as a copy: allora il percorso va sistemato quando la si copia nel teatro).
    /// </summary>
    public string? WorkRelToData()
    {
        string data = Path.GetFullPath(DataDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string work = Path.GetFullPath(WorkRoot);
        return work.StartsWith(data, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(DataDir, work).Replace('/', '\\') : null;
    }

    public const string SoundList = "Main_Snd.lst", TheaterSoundRc = @"main\SoundRc.irc", KtoSoundRc = @"_FHD\Main\SoundRc.irc";
    private List<string>? _sndLines;
    private bool _sndDirty;
    /// <summary>Suoni nuovi: file da copiare nell'Art del teatro (Sounds\nome.ogg) → file scelto dall'utente.</summary>
    private readonly Dictionary<string, string> _sndNew = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Il SoundRc.irc che BMS usa per il teatro: quello indicato dal Main_Snd.lst del teatro, se c'e', se no quello della KTO.</summary>
    public string? SoundRcFile()
    {
        if (TheaterArtJob.FindCI(WorkRoot, SoundList) is { } lst)
            foreach (var l in File.ReadAllLines(lst, System.Text.Encoding.Latin1))
            {
                var t = l.Trim().Trim('"');
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (TheaterArtJob.FindCI(DataDir, t) is { } f) return f;
                // il teatro puo' essere stato copiato altrove: si cerca il file nella sua Art
                int k = t.IndexOf(@"\art\", StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && TheaterArtJob.FindCI(WorkRoot, t[(k + 5)..]) is { } g) return g;
            }
        return TheaterArtJob.FindCI(BaseRoot, KtoSoundRc);
    }

    public bool SoundsFromTheater => _sndLines != null || TheaterArtJob.FindCI(WorkRoot, SoundList) != null;

    public List<string> SoundLines() => _sndLines ?? (SoundRcFile() is { } f ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : new List<string>());
    public List<string> SoundBaseLines() => TheaterArtJob.FindCI(BaseRoot, KtoSoundRc) is { } f ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : new List<string>();

    private static readonly System.Text.RegularExpressions.Regex SndRx = new(@"^\s*\[STREAMSOUND\]\s+(\S+)\s+""([^""]+)""(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>I suoni: nome (SND_...) → percorso del file (rispetto a Data) e opzioni (S_BIT_...).</summary>
    public static List<(string Name, string File, string Opts, int Line)> ParseSounds(IReadOnlyList<string> lines)
    {
        var res = new List<(string, string, string, int)>();
        for (int i = 0; i < lines.Count; i++)
            if (SndRx.Match(lines[i]) is { Success: true } m) res.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim(), i));
        return res;
    }

    /// <summary>File del suono sul disco (anche uno scelto e non ancora salvato), per ascoltarlo.</summary>
    public string? SoundFile(string path)
    {
        if (_sndNew.TryGetValue(path, out var src)) return src;
        if (TheaterArtJob.FindCI(DataDir, path) is { } f) return f;
        int k = path.IndexOf(@"\art\", StringComparison.OrdinalIgnoreCase);
        return k >= 0 ? TheaterArtJob.FindCI(WorkRoot, path[(k + 5)..]) : null;
    }

    /// <summary>
    /// Cambia il file di un suono per il teatro: il file .ogg va nell'Art del teatro (Sounds\SND_NOME.ogg) e SoundRc.irc (copia del
    /// teatro) punta li'. Main_Snd.lst del teatro dice a BMS di usare quella copia. La KTO non si tocca.
    /// </summary>
    public string SetSound(string name, string oggFile)
    {
        Writable();
        string artRel = WorkRelToData() ?? (Path.GetFileName(Path.GetDirectoryName(WorkRoot.TrimEnd('\\', '/'))!) + @"\Art");
        string file = "Sounds\\" + name.ToLowerInvariant() + ".ogg";
        string path = artRel + "\\" + file;
        var lines = SoundLines();
        bool done = false;
        for (int i = 0; i < lines.Count; i++)
            if (SndRx.Match(lines[i]) is { Success: true } m && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"[STREAMSOUND] {m.Groups[1].Value.PadRight(18)} \"{path}\" {m.Groups[3].Value.Trim()}";
                done = true;
            }
        if (!done) lines.Add($"[STREAMSOUND] {name.PadRight(18)} \"{path}\" S_BIT_NORMAL");
        _sndLines = lines; _sndDirty = true;
        _sndNew[path] = oggFile;
        _dirty.Add(TheaterSoundRc);
        return path;
    }

    /// <summary>
    /// Dopo aver copiato l'Art in un altro teatro: Main_Snd.lst e i percorsi dei suoni del teatro in SoundRc.irc puntano all'Art di
    /// destinazione (sono percorsi rispetto a Data, es. "Add-On Israel\Art\Sounds\snd_push.ogg").
    /// </summary>
    public static void RelinkSounds(string artDir, string dataDir, string? oldArtRel)
    {
        if (TheaterArtJob.FindCI(artDir, SoundList) is not { } lst) return;
        string data = Path.GetFullPath(dataDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(artDir);
        string newRel = full.StartsWith(data, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(dataDir, full).Replace('/', '\\')
            : Path.GetFileName(Path.GetDirectoryName(full.TrimEnd('\\', '/'))!) + @"\Art";
        File.WriteAllText(lst, newRel + "\\" + TheaterSoundRc + "\r\n", System.Text.Encoding.Latin1);
        if (oldArtRel == null || TheaterArtJob.FindCI(artDir, TheaterSoundRc) is not { } rc) return;
        var lines = File.ReadAllLines(rc, System.Text.Encoding.Latin1);
        for (int i = 0; i < lines.Length; i++)
            lines[i] = System.Text.RegularExpressions.Regex.Replace(lines[i], "\"" + System.Text.RegularExpressions.Regex.Escape(oldArtRel) + @"\\",
                "\"" + newRel.Replace("$", "$$") + "\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        File.WriteAllLines(rc, lines, System.Text.Encoding.Latin1);
    }

    /// <summary>Rimette il suono della KTO (la riga di SoundRc.irc della KTO).</summary>
    public void ResetSound(string name)
    {
        Writable();
        var kto = ParseSounds(SoundBaseLines()).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (kto.Name == null) return;
        var baseLine = SoundBaseLines()[kto.Line];
        var lines = SoundLines();
        for (int i = 0; i < lines.Count; i++)
            if (SndRx.Match(lines[i]) is { Success: true } m && m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) lines[i] = baseLine;
        _sndLines = lines; _sndDirty = true;
        _dirty.Add(TheaterSoundRc);
    }

    // ───────────────────────── font (Fonts\*.bft, FontRc.irc, FontIds.id) ─────────────────────────

    public const string FontRc = @"Fonts\FontRc.irc", FontIds = @"Fonts\FontIds.id";
    private readonly Dictionary<string, byte[]> _fontNew = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fontDel = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _fontRcLines, _fontIdLines;

    private List<string>? ReadLines(string rel)
    {
        string? f = TheaterArtJob.FindCI(WorkRoot, rel) ?? TheaterArtJob.FindCI(BaseRoot, rel);
        return f != null ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : null;
    }

    /// <summary>Righe attuali di FontRc.irc (cambiate, del teatro o della KTO).</summary>
    public List<string> FontRcLines() => _fontRcLines ?? ReadLines(FontRc) ?? new List<string>();
    public List<string> FontIdLines() => _fontIdLines ?? ReadLines(FontIds) ?? new List<string>();
    public List<string> FontRcBaseLines() => TheaterArtJob.FindCI(BaseRoot, FontRc) is { } f ? File.ReadAllLines(f, System.Text.Encoding.Latin1).ToList() : new List<string>();

    /// <summary>Il font (byte del .bft) come lo userebbe BMS adesso: modifiche non salvate, poi il teatro, poi la KTO.</summary>
    public byte[]? FontBytes(string rel)
    {
        if (_fontNew.TryGetValue(rel, out var b)) return b;
        string? f = (_fontDel.Contains(rel) ? null : TheaterArtJob.FindCI(WorkRoot, rel)) ?? TheaterArtJob.FindCI(BaseRoot, rel);
        return f != null ? File.ReadAllBytes(f) : null;
    }

    public byte[]? FontBaseBytes(string rel) => TheaterArtJob.FindCI(BaseRoot, rel) is { } f ? File.ReadAllBytes(f) : null;

    public enum FontState { Kto, Theater, Changed, Missing }

    public FontState StateOfFont(string rel)
    {
        if (_fontNew.ContainsKey(rel) || _fontDel.Contains(rel)) return FontState.Changed;
        if (TheaterArtJob.FindCI(WorkRoot, rel) != null) return FontState.Theater;
        return TheaterArtJob.FindCI(BaseRoot, rel) != null ? FontState.Kto : FontState.Missing;
    }

    /// <summary>Il file del font e' nella KTO? (allora "rimettere quello della KTO" ha senso)</summary>
    public bool FontInKto(string rel) => TheaterArtJob.FindCI(BaseRoot, rel) != null;

    /// <summary>Mette un nuovo .bft al posto di quello esistente (nella cartella del teatro: la KTO non si tocca).</summary>
    public void SetFontFile(string rel, byte[] bytes) { Writable(); _fontNew[rel] = bytes; _fontDel.Remove(rel); _dirty.Add(rel); }

    /// <summary>Toglie il .bft del teatro: torna quello della KTO.</summary>
    public void ResetFontFile(string rel)
    {
        Writable();
        _fontNew.Remove(rel);
        if (TheaterArtJob.FindCI(WorkRoot, rel) != null) { _fontDel.Add(rel); _dirty.Add(rel); }
    }

    /// <summary>Aggiunge un font nuovo: file .bft, riga [LOADFONT] in FontRc.irc e numero in FontIds.id (copie del teatro).</summary>
    public int AddFont(string name, string rel, byte[] bytes)
    {
        Writable();
        var rc = FontRcLines().ToList();
        var ids = FontIdLines().ToList();
        int next = 1;
        foreach (var l in ids)
        {
            var p = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && int.TryParse(p[1], out int v)) next = Math.Max(next, v + 1);
        }
        string path = "art\\" + rel.ToLowerInvariant();
        rc.Add($"[LOADFONT] {name.PadRight(15)} \"{path}\"");
        ids.Add($"{name.PadRight(16)}\t{next}");
        _fontRcLines = rc; _fontIdLines = ids;
        _dirty.Add(FontRc); _dirty.Add(FontIds);
        SetFontFile(rel, bytes);
        return next;
    }

    /// <summary>Toglie un font aggiunto dal teatro (non quelli della KTO).</summary>
    public void RemoveFont(string name, string rel)
    {
        Writable();
        var rx = new System.Text.RegularExpressions.Regex(@"^\s*\[LOADFONT\]\s+" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        _fontRcLines = FontRcLines().Where(l => !rx.IsMatch(l)).ToList();
        var rx2 = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        _fontIdLines = FontIdLines().Where(l => !rx2.IsMatch(l)).ToList();
        _dirty.Add(FontRc); _dirty.Add(FontIds);
        // il file si toglie solo se nessun altro nome lo usa
        bool shared = _fontRcLines.Any(l => l.Contains(Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase));
        if (!shared)
        {
            _fontNew.Remove(rel);
            if (TheaterArtJob.FindCI(WorkRoot, rel) != null) { _fontDel.Add(rel); _dirty.Add(rel); }
        }
    }

    // ───────────────────────── salvataggio ─────────────────────────

    /// <summary>
    /// Scrive i file cambiati nella cartella del teatro. Ogni file che viene riscritto si copia prima in backupDir (stessa struttura).
    /// Restituisce i file scritti.
    /// </summary>
    public List<string> Save(string backupDir)
    {
        Writable();
        var written = new List<string>();
        void Backup(string path)
        {
            if (!File.Exists(path)) return;
            string dst = Path.Combine(backupDir, Path.GetRelativePath(WorkRoot, path));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(path, dst, true);
        }
        string WorkPath(string rel) => TheaterArtJob.FindCI(WorkRoot, rel) ?? Path.Combine(WorkRoot, (TheaterArtJob.FindCI(BaseRoot, rel) is { } b ? Path.GetRelativePath(BaseRoot, b) : rel).Replace('\\', Path.DirectorySeparatorChar));
        foreach (var rel in _dirty.Where(r => r.EndsWith(".idx", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (WorkOnly(rel) is not { } w) continue;
            string idx = WorkPath(rel);
            Backup(idx); Backup(Path.ChangeExtension(idx, ".rsc")); Backup(Path.ChangeExtension(idx, ".RSC"));
            if (File.Exists(Path.ChangeExtension(idx, ".RSC")) && !File.Exists(Path.ChangeExtension(idx, ".rsc"))) File.Delete(Path.ChangeExtension(idx, ".RSC"));
            w.Save(idx);
            written.Add(Path.GetRelativePath(WorkRoot, idx));
        }
        if (_idsDirty && _ids != null)
        {
            string f = WorkPath(ArtCatalog.PatchIds);
            Backup(f);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            ResourceBundle.BreakLink(f);
            File.WriteAllLines(f, _ids, System.Text.Encoding.Latin1);
            written.Add(Path.GetRelativePath(WorkRoot, f));
            _idsDirty = false;
        }
        foreach (var (rel, lines) in _scf.ToList())
        {
            string f = WorkPath(rel);
            Backup(f);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            ResourceBundle.BreakLink(f);
            File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n", System.Text.Encoding.Latin1);
            written.Add(Path.GetRelativePath(WorkRoot, f));
        }
        _scf.Clear();
        foreach (var (rel, bytes) in _fontNew)
        {
            string f = WorkPath(rel);
            Backup(f);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            ResourceBundle.BreakLink(f);
            File.WriteAllBytes(f, bytes);
            written.Add(Path.GetRelativePath(WorkRoot, f));
        }
        foreach (var rel in _fontDel)
        {
            if (TheaterArtJob.FindCI(WorkRoot, rel) is not { } f) continue;
            Backup(f);
            ResourceBundle.BreakLink(f);   // cancella il file del teatro
            written.Add(Path.GetRelativePath(WorkRoot, f) + " (removed: the KTO font is used again)");
        }
        _fontNew.Clear(); _fontDel.Clear();
        foreach (var (rel, lines) in new[] { (FontRc, _fontRcLines), (FontIds, _fontIdLines) })
        {
            if (lines == null) continue;
            string f = WorkPath(rel);
            Backup(f);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            ResourceBundle.BreakLink(f);
            File.WriteAllText(f, string.Join("\r\n", lines) + "\r\n", System.Text.Encoding.Latin1);
            written.Add(Path.GetRelativePath(WorkRoot, f));
        }
        _fontRcLines = null; _fontIdLines = null;
        if (_sndDirty && _sndLines != null)
        {
            foreach (var (path, src) in _sndNew)
            {
                int k = path.IndexOf(@"\art\", StringComparison.OrdinalIgnoreCase);
                string rel = k >= 0 ? path[(k + 5)..] : Path.GetFileName(path);
                string dst = Path.Combine(WorkRoot, rel.Replace('\\', Path.DirectorySeparatorChar));
                Backup(dst);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                written.Add(Path.GetRelativePath(WorkRoot, dst));
            }
            _sndNew.Clear();
            string rc = Path.Combine(WorkRoot, TheaterSoundRc.Replace('\\', Path.DirectorySeparatorChar));
            rc = TheaterArtJob.FindCI(WorkRoot, TheaterSoundRc) ?? rc;
            Backup(rc);
            Directory.CreateDirectory(Path.GetDirectoryName(rc)!);
            ResourceBundle.BreakLink(rc);
            File.WriteAllText(rc, string.Join("\r\n", _sndLines) + "\r\n", System.Text.Encoding.Latin1);
            written.Add(Path.GetRelativePath(WorkRoot, rc));
            // Main_Snd.lst: BMS usa il SoundRc.irc del teatro (percorso rispetto a Data, come in Israel)
            string artRel = WorkRelToData() ?? (Path.GetFileName(Path.GetDirectoryName(WorkRoot.TrimEnd('\\', '/'))!) + @"\Art");
            string lst = TheaterArtJob.FindCI(WorkRoot, SoundList) ?? Path.Combine(WorkRoot, SoundList);
            Backup(lst);
            ResourceBundle.BreakLink(lst);
            File.WriteAllText(lst, artRel + "\\" + TheaterSoundRc + "\r\n", System.Text.Encoding.Latin1);
            written.Add(Path.GetRelativePath(WorkRoot, lst));
            _sndLines = null; _sndDirty = false;
            _dirty.Remove(TheaterSoundRc);
        }
        if (_ircDirty && _irc != null)
        {
            foreach (var rel in IrcFiles)
            {
                if (TheaterArtJob.FindCI(BaseRoot, rel) == null && TheaterArtJob.FindCI(WorkRoot, rel) == null) continue;
                string f = WorkPath(rel);
                Backup(f);
                _irc.Save(f);
                written.Add(Path.GetRelativePath(WorkRoot, f));
            }
            _ircDirty = false;
        }
        _dirty.Clear(); _changed.Clear();
        return written;
    }

    // ───────────────────────── controllo ─────────────────────────

    /// <summary>Problemi dell'Art del teatro: misure diverse da quelle di BMS, voci mancanti, patch senza numero, testi mancanti.</summary>
    public List<string> Check()
    {
        var res = new List<string>();
        foreach (var s in ArtCatalog.Slots)
            foreach (var rel in s.Archives)
            {
                if (!InWork(rel) || Base(rel) is not { } b) continue;
                var w = WorkOnly(rel)!;
                foreach (var n in s.Entries)
                {
                    var be = b.Find(n); var we = w.Find(n);
                    if (be == null) continue;
                    if (we == null) res.Add($"{s.Title}: {n} missing in {rel} (BMS will not show it)");
                    else if (we.Width != be.Width || we.Height != be.Height) res.Add($"{s.Title}: {n} in {rel} is {we.Width}×{we.Height}, BMS 4.38 expects {be.Width}×{be.Height} (old add-on art? replace it)");
                }
            }
        // archivi del teatro senza voci che BMS 4.38 ha (es. un archivio di un vecchio add-on 4.36)
        foreach (var rel in ArtCatalog.Slots.SelectMany(s => s.Archives).Append(ArtCatalog.PatchArchive).Distinct(StringComparer.OrdinalIgnoreCase))
            if (InWork(rel) && Base(rel) is { } b)
            {
                var w = WorkOnly(rel)!;
                int miss = b.Entries.Count(e => w.Find(e.Name) == null);
                if (miss > 0) res.Add($"{rel}: {miss} entries of BMS 4.38 are missing (an archive from an older BMS? parts of the interface may be blank)");
            }
        var ids = PatchIds();
        var pb = Effective(ArtCatalog.PatchArchive);
        if (pb != null)
        {
            var noId = pb.Entries.Where(e => e.IsImage && !ids.ContainsKey(e.Name)).Select(e => e.Name).ToList();
            if (noId.Count > 0) res.Add($"Patches without a number in ImageIds.id: {string.Join(", ", noId.Take(8))}{(noId.Count > 8 ? "…" : "")}");
            var noImg = ids.Keys.Where(k => pb.Find(k) == null).ToList();
            if (noImg.Count > 0) res.Add($"Numbers in ImageIds.id without a patch image: {string.Join(", ", noImg.Take(8))}{(noImg.Count > 8 ? "…" : "")}");
            var bad = ids.Keys.Where(k => k.Length > 0 && char.IsDigit(k[0]) && !k.Equals("1ST_BMS", StringComparison.OrdinalIgnoreCase)).ToList();
            if (bad.Count > 0) res.Add($"Patch names starting with a number (BMS does not show them in game): {string.Join(", ", bad.Take(8))}");
            var dup = ids.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) res.Add($"Patch numbers used twice: {string.Join(", ", dup.Take(8))}");
        }
        if (Irc() is { } irc && IrcBase() is { } ib)
        {
            var have = irc.Entries().Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var miss = ib.Entries().Where(e => !have.Contains(e.Key)).Select(e => e.Key).ToList();
            if (miss.Count > 0) res.Add($"Theater.irc: {miss.Count} texts of BMS 4.38 missing ({string.Join(", ", miss.Take(5))}…): BMS shows them empty");
        }
        if (IsDirty) res.Add("There are changes not saved yet.");
        return res;
    }
}

/// <summary>A change or a save tried on the KTO art opened read only.</summary>
public sealed class ReadOnlyArtException : InvalidOperationException
{
    public ReadOnlyArtException() : base("The Korea (KTO) art is open read only: it is the master used by every theater and is never changed. " +
        "To change it for a theater, use Open… → New theater art…, which starts a theater art from it.") { }
}
