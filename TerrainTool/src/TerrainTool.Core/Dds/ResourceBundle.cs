using System.Text;

namespace TerrainTool.Core.Dds;

/// <summary>
/// Archivio di risorse dell'interfaccia di Falcon (Art\...\X.rsc + X.idx). Indice: u32 dimensione delle voci, u32 timestamp,
/// poi le voci: u32 tipo (100 immagine, 101 suono, 102 dati), nome di 32 byte, e per le immagini u32 flag (1 = 8 bit con
/// palette RGB555, 2 = 16 bit RGB555), u16 centro x/y, u16 larghezza/altezza, u32 posizione dei pixel, u32 colori, u32
/// posizione della palette (60 byte); suoni 52 byte; dati: u32 posizione, u32 dimensione (44 byte).
/// Il .rsc: u32 dimensione dei dati, u32 timestamp, poi i dati (le posizioni dell'indice partono da qui).
/// Le immagini sostituite o aggiunte si accodano ai dati: le vecchie restano dove sono, senza essere usate.
/// </summary>
public sealed class ResourceBundle
{
    public sealed class Entry
    {
        public byte[] Record = Array.Empty<byte>();
        public uint Type => BitConverter.ToUInt32(Record, 0);
        public string Name => Encoding.ASCII.GetString(Record, 4, 32).Split('\0')[0];
        public uint Flags => BitConverter.ToUInt32(Record, 36);
        public int Width => BitConverter.ToUInt16(Record, 44);
        public int Height => BitConverter.ToUInt16(Record, 46);
        public uint ImageOffset => BitConverter.ToUInt32(Record, 48);
        public uint PaletteSize => BitConverter.ToUInt32(Record, 52);
        public uint PaletteOffset => BitConverter.ToUInt32(Record, 56);
        public bool IsImage => Type == 100;
        public int BytesPerPixel => (Flags & 1) != 0 ? 1 : 2;
    }

    public List<Entry> Entries { get; } = new();
    private readonly MemoryStream _data = new();
    private uint _stamp;

    public static ResourceBundle Load(string idxPath)
    {
        string rsc = Path.ChangeExtension(idxPath, ".rsc");
        if (!File.Exists(rsc)) rsc = Path.ChangeExtension(idxPath, ".RSC");
        var b = new ResourceBundle();
        var idx = File.ReadAllBytes(idxPath);
        b._stamp = BitConverter.ToUInt32(idx, 4);
        int o = 8;
        while (o + 4 <= idx.Length)
        {
            uint t = BitConverter.ToUInt32(idx, o);
            int len = t switch { 100 => 60, 101 => 52, 102 => 44, _ => throw new InvalidDataException($"{Path.GetFileName(idxPath)}: unknown resource type {t}") };
            b.Entries.Add(new Entry { Record = idx.AsSpan(o, len).ToArray() });
            o += len;
        }
        var data = File.ReadAllBytes(rsc);
        b._data.Write(data, 8, data.Length - 8);
        return b;
    }

    public Entry? Find(string name) => Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pixel (e palette) dell'immagine così come sono nel file.</summary>
    public (byte[] Pixels, byte[] Palette) ImageBytes(Entry e)
    {
        var d = _data.GetBuffer();
        var px = d.AsSpan((int)e.ImageOffset, e.Width * e.Height * e.BytesPerPixel).ToArray();
        var pal = e.BytesPerPixel == 1 ? d.AsSpan((int)e.PaletteOffset, (int)e.PaletteSize * 2).ToArray() : Array.Empty<byte>();
        return (px, pal);
    }

    /// <summary>Immagine in BGRA (32 bit) per l'anteprima; il colore chiave (magenta) diventa trasparente.</summary>
    public byte[] ToBgra(Entry e)
    {
        var (px, pal) = ImageBytes(e);
        var res = new byte[e.Width * e.Height * 4];
        for (int i = 0; i < e.Width * e.Height; i++)
        {
            ushort v = e.BytesPerPixel == 1 ? (px[i] * 2 + 1 < pal.Length ? BitConverter.ToUInt16(pal, px[i] * 2) : (ushort)0) : BitConverter.ToUInt16(px, i * 2);
            byte r = (byte)(((v >> 10) & 31) * 255 / 31), g = (byte)(((v >> 5) & 31) * 255 / 31), b = (byte)((v & 31) * 255 / 31);
            res[4 * i] = b; res[4 * i + 1] = g; res[4 * i + 2] = r; res[4 * i + 3] = (byte)(v == 0x7C1F ? 0 : 255);
        }
        return res;
    }

    /// <summary>Mette in questo archivio l'immagine 'name' di un altro (sostituisce la voce con lo stesso nome, o la aggiunge).</summary>
    public bool CopyImageFrom(ResourceBundle src, string name)
    {
        var se = src.Find(name);
        if (se == null || !se.IsImage) return false;
        var (px, pal) = src.ImageBytes(se);
        var rec = (byte[])se.Record.Clone();
        Align();
        BitConverter.TryWriteBytes(rec.AsSpan(48, 4), (uint)_data.Length);
        _data.Write(px);
        if (pal.Length > 0)
        {
            BitConverter.TryWriteBytes(rec.AsSpan(56, 4), (uint)_data.Length);
            _data.Write(pal);
        }
        var de = Find(name);
        if (de != null) de.Record = rec;
        else Entries.Add(new Entry { Record = rec });
        return true;
    }

    /// <summary>Cancella il file (se c'è) prima di riscriverlo: un collegamento fisico non viene scritto attraverso.</summary>
    public static void BreakLink(string path) { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }

    /// <summary>
    /// Mette un'immagine RGB (3 byte per pixel, riga 0 in alto) come voce 'name' (sostituisce quella con lo stesso nome,
    /// tenendone i flag, o la aggiunge a 16 bit). Si tiene il formato della voce di BMS: se era a 8 bit con palette resta
    /// a 8 bit (con piu' di 256 colori si riduce ai 256 piu' usati), perche' l'interfaccia di BMS non accetta sempre
    /// un'immagine a 16 bit al posto di una a 8 (es. KOREA_256 della finestra delle priorita'). Come nei file di BMS,
    /// anche le voci a 16 bit hanno una palette di 256 colori.
    /// </summary>
    public void SetImageRgb(string name, byte[] rgb, int w, int h, bool magentaIsTransparent = false)
    {
        var de = Find(name);
        var rec = de != null ? (byte[])de.Record.Clone() : new byte[60];
        if (de == null)
        {
            BitConverter.TryWriteBytes(rec.AsSpan(0, 4), 100u);
            Encoding.ASCII.GetBytes(name).CopyTo(rec.AsSpan(4, 32));
        }
        bool eight = de != null && de.BytesPerPixel == 1;
        uint flags = de != null ? de.Flags : 2u;
        BitConverter.TryWriteBytes(rec.AsSpan(36, 4), flags);
        BitConverter.TryWriteBytes(rec.AsSpan(40, 2), (ushort)(w / 2));
        BitConverter.TryWriteBytes(rec.AsSpan(42, 2), (ushort)(h / 2));
        BitConverter.TryWriteBytes(rec.AsSpan(44, 2), (ushort)w);
        BitConverter.TryWriteBytes(rec.AsSpan(46, 2), (ushort)h);
        var v = new ushort[w * h];
        for (int i = 0; i < w * h; i++)
        {
            ushort c = (ushort)(((rgb[3 * i] >> 3) << 10) | ((rgb[3 * i + 1] >> 3) << 5) | (rgb[3 * i + 2] >> 3));
            if (c == 0x7C1F && !magentaIsTransparent) c = 0x7C1E; // il magenta e' il colore trasparente
            v[i] = c;
        }
        Align();
        BitConverter.TryWriteBytes(rec.AsSpan(48, 4), (uint)_data.Length);
        ushort[] pal;
        if (eight)
        {
            pal = Palette(v);
            var map = new Dictionary<ushort, byte>();
            for (int k = 0; k < pal.Length; k++) map.TryAdd(pal[k], (byte)k);
            var px = new byte[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                if (!map.TryGetValue(v[i], out byte k)) { k = Nearest(pal, v[i]); map[v[i]] = k; }
                px[i] = k;
            }
            _data.Write(px);
        }
        else
        {
            pal = Enumerable.Repeat((ushort)0x7C1F, 256).ToArray();
            var px = new byte[w * h * 2];
            for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(px.AsSpan(2 * i, 2), v[i]);
            _data.Write(px);
        }
        Align();
        BitConverter.TryWriteBytes(rec.AsSpan(52, 4), (uint)pal.Length);
        BitConverter.TryWriteBytes(rec.AsSpan(56, 4), (uint)_data.Length);
        foreach (var c in pal) _data.Write(BitConverter.GetBytes(c));
        if (de != null) de.Record = rec; else Entries.Add(new Entry { Record = rec });
    }

    /// <summary>I colori dell'immagine (al massimo 256: i piu' usati; il magenta trasparente, se c'e', resta sempre).</summary>
    private static ushort[] Palette(ushort[] v)
    {
        var count = new Dictionary<ushort, int>();
        foreach (var c in v) count[c] = count.TryGetValue(c, out int n) ? n + 1 : 1;
        var pal = count.OrderByDescending(kv => kv.Key == 0x7C1F ? int.MaxValue : kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).Take(256).ToList();
        return pal.ToArray();
    }

    private static byte Nearest(ushort[] pal, ushort c)
    {
        int r = (c >> 10) & 31, g = (c >> 5) & 31, b = c & 31, best = 0, bd = int.MaxValue;
        for (int k = 0; k < pal.Length; k++)
        {
            if (pal[k] == 0x7C1F) continue;  // un colore vero non diventa trasparente
            int dr = ((pal[k] >> 10) & 31) - r, dg = ((pal[k] >> 5) & 31) - g, db = (pal[k] & 31) - b, d = dr * dr + dg * dg + db * db;
            if (d < bd) { bd = d; best = k; }
        }
        return (byte)best;
    }

    /// <summary>Legge una sola immagine (RGB, riga 0 in alto) da un archivio senza caricarlo tutto (campmap.rsc è di 370 MB).</summary>
    public static (byte[] Rgb, int W, int H)? ReadImageRgb(string idxPath, string name)
    {
        var idx = File.ReadAllBytes(idxPath);
        for (int o = 8; o + 60 <= idx.Length;)
        {
            uint t = BitConverter.ToUInt32(idx, o);
            int len = t switch { 100 => 60, 101 => 52, _ => 44 };
            var e = new Entry { Record = idx.AsSpan(o, len).ToArray() };
            o += len;
            if (!e.IsImage || !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string rsc = Path.ChangeExtension(idxPath, ".rsc");
            using var fs = new FileStream(rsc, FileMode.Open, FileAccess.Read, FileShare.Read);
            int w = e.Width, h = e.Height, bpp = e.BytesPerPixel;
            var px = new byte[(long)w * h * bpp];
            fs.Position = 8 + e.ImageOffset; fs.ReadExactly(px);
            var pal = new byte[bpp == 1 ? e.PaletteSize * 2 : 0];
            if (pal.Length > 0) { fs.Position = 8 + e.PaletteOffset; fs.ReadExactly(pal); }
            var rgb = new byte[(long)w * h * 3];
            for (long i = 0; i < (long)w * h; i++)
            {
                ushort v = bpp == 1 ? BitConverter.ToUInt16(pal, Math.Min(px[i] * 2, pal.Length - 2)) : BitConverter.ToUInt16(px, (int)(i * 2));
                rgb[3 * i] = (byte)(((v >> 10) & 31) * 255 / 31); rgb[3 * i + 1] = (byte)(((v >> 5) & 31) * 255 / 31); rgb[3 * i + 2] = (byte)((v & 31) * 255 / 31);
            }
            return (rgb, w, h);
        }
        return null;
    }

    private void Align() { while (_data.Length % 4 != 0) _data.WriteByte(0); }

    public void Save(string idxPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(idxPath)!);
        string rsc = Path.ChangeExtension(idxPath, ".rsc");
        // i file di Output\Art possono essere collegamenti fisici (hard link) a quelli installati: si cancellano prima,
        // così si scrive un file nuovo e non dentro le altre copie
        BreakLink(rsc); BreakLink(idxPath);
        using (var fs = new FileStream(rsc, FileMode.Create, FileAccess.Write))
        {
            fs.Write(BitConverter.GetBytes((uint)_data.Length));
            fs.Write(BitConverter.GetBytes(_stamp));
            fs.Write(_data.GetBuffer(), 0, (int)_data.Length);
        }
        using var ms = new MemoryStream();
        int size = Entries.Sum(e => e.Record.Length);
        ms.Write(BitConverter.GetBytes((uint)size));
        ms.Write(BitConverter.GetBytes(_stamp));
        foreach (var e in Entries) ms.Write(e.Record);
        File.WriteAllBytes(idxPath, ms.ToArray());
    }
}
