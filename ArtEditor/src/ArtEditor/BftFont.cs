using System.IO;
using System.Text;

namespace ArtEditor;

/// <summary>
/// Font dell'interfaccia di BMS (.bft): font TrueType gia' trasformato in immagini a 1 bit, a un'altezza fissa.
/// Formato: nome (32 byte), altezza, primo/ultimo carattere (u16+u16), byte per riga, numero di caratteri, 0, byte delle immagini;
/// poi per ogni carattere 4 byte (0, larghezza, spazio prima, spazio dopo con segno); poi le immagini, una dopo l'altra,
/// 'altezza' righe di 'byte per riga', bit meno significativo = pixel a sinistra. Caratteri in Windows-1252.
/// </summary>
public sealed class BftFont
{
    public string Name { get; private init; } = "";
    public string File { get; private init; } = "";
    public int Height { get; private init; }
    private int _first, _count, _bpr;
    private byte[] _met = Array.Empty<byte>(), _bm = Array.Empty<byte>();

    public static BftFont Load(string path) => Load(System.IO.File.ReadAllBytes(path), path);

    public static BftFont Load(byte[] b, string path)
    {
        if (b.Length < 56) throw new InvalidDataException(Path.GetFileName(path) + ": not a BMS font");
        int h = BitConverter.ToInt32(b, 32), rng = BitConverter.ToInt32(b, 36), bpr = BitConverter.ToInt32(b, 40), cnt = BitConverter.ToInt32(b, 44);
        if (h <= 0 || h > 200 || bpr <= 0 || bpr > 32 || cnt <= 0 || cnt > 256) throw new InvalidDataException(Path.GetFileName(path) + ": not a BMS font");
        return new BftFont
        {
            Name = Encoding.ASCII.GetString(b, 0, 32).Split('\0')[0], File = path, Height = h,
            _first = rng & 0xFFFF, _count = cnt, _bpr = bpr,
            _met = b.AsSpan(56, cnt * 4).ToArray(), _bm = b.AsSpan(56 + cnt * 4).ToArray(),
        };
    }

    /// <summary>Nome del carattere scritto nel file (di solito il nome del TrueType di origine).</summary>
    public string Face => Name;

    private int Index(char c)
    {
        int code = c <= 255 ? c : CodePages.Win1252(c);
        if (code < 0) return -1;
        int i = code - _first;
        return i >= 0 && i < _count ? i : -1;
    }

    private (int W, int Lead, int Trail) Metrics(int i) => (_met[4 * i + 1], (sbyte)_met[4 * i + 2], (sbyte)_met[4 * i + 3]);

    public int Width(string s)
    {
        int w = 0;
        foreach (char c in s) { int i = Index(c); if (i < 0) continue; var (gw, a, t) = Metrics(i); w += a + gw + t; }
        return w;
    }

    /// <summary>Scrive il testo nella tela BGRA (larghezza cw) con il colore dato; x, y = angolo in alto a sinistra.</summary>
    public void Draw(byte[] canvas, int cw, int ch, string s, int x, int y, (byte R, byte G, byte B) col, Rect32? clip = null)
    {
        var cl = clip ?? new Rect32(0, 0, cw, ch);
        foreach (char c in s)
        {
            int i = Index(c); if (i < 0) continue;
            var (gw, a, t) = Metrics(i);
            int gx = x + a;
            for (int yy = 0; yy < Height; yy++)
            {
                int py = y + yy;
                if (py < cl.Y || py >= cl.Y + cl.H || py < 0 || py >= ch) continue;
                int ro = (i * Height + yy) * _bpr;
                for (int xx = 0; xx < Math.Min(gw, _bpr * 8); xx++)
                {
                    if (((_bm[ro + xx / 8] >> (xx % 8)) & 1) == 0) continue;
                    int px = gx + xx;
                    if (px < cl.X || px >= cl.X + cl.W || px < 0 || px >= cw) continue;
                    int o = 4 * (py * cw + px);
                    canvas[o] = col.B; canvas[o + 1] = col.G; canvas[o + 2] = col.R; canvas[o + 3] = 255;
                }
            }
            x += a + gw + t;
        }
    }
}

/// <summary>Rettangolo intero (pixel dello schermo 1920×1080).</summary>
public readonly record struct Rect32(int X, int Y, int W, int H)
{
    public bool Contains(int px, int py) => px >= X && py >= Y && px < X + W && py < Y + H;
}

/// <summary>I caratteri di Windows-1252 tra 0x80 e 0x9F (virgolette curve, trattini, euro...) che non sono in Latin-1.</summary>
public static class CodePages
{
    private static readonly Dictionary<char, int> Map = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A,
        ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };
    public static int Win1252(char c) => Map.TryGetValue(c, out int v) ? v : -1;
    /// <summary>Carattere Unicode di un codice Windows-1252 ('\0' se il codice non e' usato).</summary>
    public static char ToChar(int code) => code is >= 0x80 and <= 0x9F ? Map.FirstOrDefault(kv => kv.Value == code).Key : (char)code;
}
