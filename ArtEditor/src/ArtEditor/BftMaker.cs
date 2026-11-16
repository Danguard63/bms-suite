using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ArtEditor;

/// <summary>
/// Trasforma un font TrueType/OpenType in un font dell'interfaccia di BMS (.bft), come faceva il tool originale (Fonts\Fonts.txt:
/// CreateFont con lfHeight negativo, glifi a 1 bit da GetGlyphOutline). Caratteri da 31 a 255 (Windows-1252), altezza = altezza
/// della riga del font (tmHeight), per ogni carattere: larghezza del disegno, spazio prima e spazio dopo.
/// </summary>
public static class BftMaker
{
    public sealed record Options(string Face, int EmPx, bool Bold, bool Italic, string? File = null);

    public const int First = 31, Last = 255;

    /// <summary>Nome di famiglia (per Windows) di un file .ttf/.otf, da passare come Face; il file viene registrato solo per questo programma.</summary>
    public static string RegisterFile(string path)
    {
        var gt = new System.Windows.Media.GlyphTypeface(new Uri(Path.GetFullPath(path)));
        string? name = gt.Win32FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var n) ? n : gt.Win32FamilyNames.Values.FirstOrDefault();
        name ??= gt.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
        AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero);
        return name;
    }

    /// <summary>Altezza della riga (pixel) che avrebbe il font a questa misura.</summary>
    public static int LineHeight(Options o)
    {
        using var g = new Gdi(o);
        return g.Tm.tmHeight;
    }

    /// <summary>La misura (em, pixel) piu' grande che sta in 'height' pixel di riga: per avere un font alto come quello che sostituisce.</summary>
    public static int FitEm(Options o, int height)
    {
        int best = 4;
        for (int em = 4; em <= 200; em++)
        {
            int h = LineHeight(o with { EmPx = em });
            if (h <= height) best = em; else break;
        }
        return best;
    }

    /// <summary>Il file .bft (byte) e l'altezza della riga.</summary>
    public static (byte[] Bytes, int Height, int Missing) Make(Options o)
    {
        using var g = new Gdi(o);
        int height = g.Tm.tmHeight, ascent = g.Tm.tmAscent;
        int count = Last - First + 1;
        var glyphs = new (int W, int Lead, int Trail, int Top, int H, byte[]? Rows, int Stride)[count];
        var mat = new MAT2 { eM11 = new FIXED { value = 1 }, eM22 = new FIXED { value = 1 } };
        int maxW = 1, missing = 0;
        for (int i = 0; i < count; i++)
        {
            int code = First + i;
            char ch = code < 32 ? '\0' : CodePages.ToChar(code);
            if (ch == '\0' || code is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D) { glyphs[i] = (0, 0, 0, 0, 0, null, 0); continue; }
            if (code >= 33 && !g.Has(ch)) missing++;
            uint size = GetGlyphOutlineW(g.Dc, ch, GGO_BITMAP, out var gm, 0, IntPtr.Zero, ref mat);
            if (size == GDI_ERROR) { glyphs[i] = (0, 0, 0, 0, 0, null, 0); continue; }
            if (size == 0)   // carattere vuoto (spazio): solo l'avanzamento
            {
                glyphs[i] = (0, 0, gm.gmCellIncX, 0, 0, null, 0);
                continue;
            }
            var buf = new byte[size];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { GetGlyphOutlineW(g.Dc, ch, GGO_BITMAP, out gm, size, h.AddrOfPinnedObject(), ref mat); }
            finally { h.Free(); }
            int w = (int)gm.gmBlackBoxX, gh = (int)gm.gmBlackBoxY, stride = ((w + 31) / 32) * 4;
            int lead = gm.gmptGlyphOrigin.X, trail = gm.gmCellIncX - lead - w;
            glyphs[i] = (w, lead, trail, ascent - gm.gmptGlyphOrigin.Y, gh, buf, stride);
            maxW = Math.Max(maxW, w);
        }
        maxW = Math.Min(maxW, 255);
        int bpr = (maxW + 7) / 8;
        int bmSize = count * height * bpr;
        var outb = new byte[56 + count * 4 + bmSize];
        var name = Encoding.ASCII.GetBytes(o.Face.Length > 31 ? o.Face[..31] : o.Face);
        Array.Copy(name, outb, name.Length);
        BitConverter.GetBytes(height).CopyTo(outb, 32);
        BitConverter.GetBytes(First | (Last << 16)).CopyTo(outb, 36);
        BitConverter.GetBytes(bpr).CopyTo(outb, 40);
        BitConverter.GetBytes(count).CopyTo(outb, 44);
        BitConverter.GetBytes(0).CopyTo(outb, 48);
        BitConverter.GetBytes(bmSize).CopyTo(outb, 52);
        int bmBase = 56 + count * 4;
        for (int i = 0; i < count; i++)
        {
            var gl = glyphs[i];
            int w = Math.Min(gl.W, 255);
            outb[56 + 4 * i] = 0;
            outb[56 + 4 * i + 1] = (byte)w;
            outb[56 + 4 * i + 2] = (byte)(sbyte)Math.Clamp(gl.Lead, -128, 127);
            outb[56 + 4 * i + 3] = (byte)(sbyte)Math.Clamp(gl.Trail + (gl.W - w), -128, 127);
            if (gl.Rows == null) continue;
            for (int y = 0; y < gl.H; y++)
            {
                int cy = gl.Top + y;
                if (cy < 0 || cy >= height) continue;
                int ro = bmBase + (i * height + cy) * bpr;
                for (int x = 0; x < w; x++)
                {
                    // GGO_BITMAP: righe allineate a 4 byte, bit piu' significativo = pixel a sinistra; .bft: bit meno significativo a sinistra
                    if (((gl.Rows[y * gl.Stride + x / 8] >> (7 - x % 8)) & 1) == 0) continue;
                    outb[ro + x / 8] |= (byte)(1 << (x % 8));
                }
            }
        }
        return (outb, height, missing);
    }

    // ───────────────────────── GDI ─────────────────────────

    private sealed class Gdi : IDisposable
    {
        public readonly IntPtr Dc, Font, Old;
        public TEXTMETRIC Tm;
        private readonly HashSet<char>? _cmap;

        public Gdi(Options o)
        {
            Dc = CreateCompatibleDC(IntPtr.Zero);
            Font = CreateFontW(-Math.Max(1, o.EmPx), 0, 0, 0, o.Bold ? 700 : 400, o.Italic ? 1u : 0u, 0, 0, 0 /*ANSI*/, 4 /*OUT_TT_PRECIS*/, 0, 2 /*PROOF_QUALITY*/, 0, o.Face);
            Old = SelectObject(Dc, Font);
            GetTextMetricsW(Dc, out Tm);
            // caratteri presenti nel font (per avvisare se mancano lettere accentate ecc.)
            uint sz = GetFontUnicodeRanges(Dc, IntPtr.Zero);
            if (sz > 0)
            {
                var buf = Marshal.AllocHGlobal((int)sz);
                try
                {
                    GetFontUnicodeRanges(Dc, buf);
                    int n = Marshal.ReadInt32(buf, 12);
                    _cmap = new HashSet<char>();
                    for (int k = 0; k < n; k++)
                    {
                        int lo = (ushort)Marshal.ReadInt16(buf, 16 + 4 * k), cnt = (ushort)Marshal.ReadInt16(buf, 18 + 4 * k);
                        for (int c = lo; c < lo + cnt && c <= 0xFFFF; c++) _cmap.Add((char)c);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        public bool Has(char c) => _cmap == null || _cmap.Contains(c);

        public void Dispose()
        {
            SelectObject(Dc, Old);
            DeleteObject(Font);
            DeleteDC(Dc);
        }
    }

    private const uint GGO_BITMAP = 1, GDI_ERROR = 0xFFFFFFFF, FR_PRIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct FIXED { public short fract; public short value; }
    [StructLayout(LayoutKind.Sequential)] private struct MAT2 { public FIXED eM11, eM12, eM21, eM22; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GLYPHMETRICS { public uint gmBlackBoxX, gmBlackBoxY; public POINT gmptGlyphOrigin; public short gmCellIncX, gmCellIncY; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRIC
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC tm);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphOutlineW(IntPtr hdc, uint ch, uint format, out GLYPHMETRICS gm, uint size, IntPtr buf, ref MAT2 mat);
    [DllImport("gdi32.dll")] private static extern uint GetFontUnicodeRanges(IntPtr hdc, IntPtr lpgs);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceEx(string name, uint fl, IntPtr res);
}
