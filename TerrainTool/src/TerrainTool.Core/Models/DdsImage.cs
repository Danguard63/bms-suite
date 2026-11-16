namespace TerrainTool.Core.Models;

/// <summary>
/// Lettura delle DDS dei modelli BMS (KoreaObj DXT1/DXT3/DXT5, modelli PBR BC7 sRGB con intestazione DX10, BC4/BC5, non compresse)
/// in BGRA 8 bit. Decodifica solo il primo livello mipmap non piu' grande di maxSize (le texture PBR sono 4096²).
/// </summary>
public static class DdsImage
{
    public sealed record Image(int Width, int Height, byte[] Bgra, bool HasAlpha);

    private enum Fmt { Bc1, Bc2, Bc3, Bc4, Bc5, Bc7, Bgra8, Rgba8, Bgrx8 }

    public static Image? Load(string path, int maxSize = 1024)
    {
        byte[] b;
        try { b = File.ReadAllBytes(path); } catch { return null; }
        return Decode(b, maxSize);
    }

    public static Image? Decode(byte[] b, int maxSize = 1024)
    {
        if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S') return null;
        int h = BitConverter.ToInt32(b, 12), w = BitConverter.ToInt32(b, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(b, 28));
        uint pfFlags = BitConverter.ToUInt32(b, 80);
        string four = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        int bits = BitConverter.ToInt32(b, 88);
        uint rMask = BitConverter.ToUInt32(b, 92), aMask = BitConverter.ToUInt32(b, 104);
        int data = 128;
        Fmt fmt;
        if ((pfFlags & 4) != 0)
        {
            switch (four)
            {
                case "DXT1": fmt = Fmt.Bc1; break;
                case "DXT2": case "DXT3": fmt = Fmt.Bc2; break;
                case "DXT4": case "DXT5": fmt = Fmt.Bc3; break;
                case "ATI1": case "BC4U": fmt = Fmt.Bc4; break;
                case "ATI2": case "BC5U": fmt = Fmt.Bc5; break;
                case "DX10":
                    int dxgi = BitConverter.ToInt32(b, 128);
                    data = 148;
                    switch (dxgi)
                    {
                        case 70: case 71: case 72: fmt = Fmt.Bc1; break;
                        case 73: case 74: case 75: fmt = Fmt.Bc2; break;
                        case 76: case 77: case 78: fmt = Fmt.Bc3; break;
                        case 79: case 80: case 81: fmt = Fmt.Bc4; break;
                        case 82: case 83: case 84: fmt = Fmt.Bc5; break;
                        case 97: case 98: case 99: fmt = Fmt.Bc7; break;
                        case 27: case 28: case 29: fmt = Fmt.Rgba8; break;
                        case 87: case 90: case 91: fmt = Fmt.Bgra8; break;
                        case 88: case 92: case 93: fmt = Fmt.Bgrx8; break;
                        default: return null;
                    }
                    break;
                default: return null;
            }
        }
        else if (bits == 32) fmt = rMask == 0x000000FF ? Fmt.Rgba8 : aMask != 0 ? Fmt.Bgra8 : Fmt.Bgrx8;
        else return null;

        bool block = fmt <= Fmt.Bc7;
        int blockBytes = fmt is Fmt.Bc1 or Fmt.Bc4 ? 8 : 16;
        long LevelSize(int lw, int lh) => block ? (long)Math.Max(1, (lw + 3) / 4) * Math.Max(1, (lh + 3) / 4) * blockBytes : (long)lw * lh * 4;

        // salta i livelli piu' grandi di maxSize
        long off = data;
        int level = 0;
        while (level < mips - 1 && Math.Max(w, h) > maxSize)
        {
            off += LevelSize(w, h);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); level++;
        }
        if (off + LevelSize(w, h) > b.Length) return null;

        var px = new byte[w * h * 4];
        bool alpha = fmt is Fmt.Bc2 or Fmt.Bc3 or Fmt.Bc7 or Fmt.Bgra8 or Fmt.Rgba8;
        if (!block)
        {
            int p = (int)off;
            for (int i = 0; i < w * h; i++, p += 4)
            {
                int o = i * 4;
                if (fmt == Fmt.Rgba8) { px[o] = b[p + 2]; px[o + 1] = b[p + 1]; px[o + 2] = b[p]; px[o + 3] = b[p + 3]; }
                else { px[o] = b[p]; px[o + 1] = b[p + 1]; px[o + 2] = b[p + 2]; px[o + 3] = fmt == Fmt.Bgrx8 ? (byte)255 : b[p + 3]; }
            }
            return new Image(w, h, px, alpha);
        }

        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var rgba = new byte[64];
        bool anyAlpha = false;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int src = (int)off + (by * bw + bx) * blockBytes;
                switch (fmt)
                {
                    case Fmt.Bc1: Bc1(b, src, rgba, true); break;
                    case Fmt.Bc2: Bc1(b, src + 8, rgba, false); Bc2Alpha(b, src, rgba); break;
                    case Fmt.Bc3: Bc1(b, src + 8, rgba, false); Bc4(b, src, rgba, 3); break;
                    case Fmt.Bc4: Bc4(b, src, rgba, 0); for (int i = 0; i < 16; i++) { rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4]; rgba[i * 4 + 3] = 255; } break;
                    case Fmt.Bc5: Bc4(b, src, rgba, 0); Bc4(b, src + 8, rgba, 1); for (int i = 0; i < 16; i++) { rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; } break;
                    case Fmt.Bc7: Bc7(b, src, rgba); break;
                }
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y; if (py >= h) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int pxx = bx * 4 + x; if (pxx >= w) break;
                        int s = (y * 4 + x) * 4, d = (py * w + pxx) * 4;
                        px[d] = rgba[s + 2]; px[d + 1] = rgba[s + 1]; px[d + 2] = rgba[s]; px[d + 3] = rgba[s + 3];
                        if (rgba[s + 3] < 250) anyAlpha = true;
                    }
                }
            }
        return new Image(w, h, px, alpha && anyAlpha || fmt == Fmt.Bc1 && anyAlpha);
    }

    // ───────────────────────── BC1..BC5

    private static void Bc1(byte[] b, int o, byte[] rgba, bool allowAlpha)
    {
        int c0 = b[o] | (b[o + 1] << 8), c1 = b[o + 2] | (b[o + 3] << 8);
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4], a = stackalloc int[4];
        static void Exp(int c, out int rr, out int gg, out int bb)
        {
            rr = (c >> 11) & 31; gg = (c >> 5) & 63; bb = c & 31;
            rr = (rr << 3) | (rr >> 2); gg = (gg << 2) | (gg >> 4); bb = (bb << 3) | (bb >> 2);
        }
        Exp(c0, out r[0], out g[0], out bl[0]); Exp(c1, out r[1], out g[1], out bl[1]);
        a[0] = a[1] = a[2] = a[3] = 255;
        if (c0 > c1 || !allowAlpha)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2;
            r[3] = g[3] = bl[3] = 0; a[3] = 0;
        }
        uint idx = BitConverter.ToUInt32(b, o + 4);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((idx >> (2 * i)) & 3);
            rgba[i * 4] = (byte)r[k]; rgba[i * 4 + 1] = (byte)g[k]; rgba[i * 4 + 2] = (byte)bl[k]; rgba[i * 4 + 3] = (byte)a[k];
        }
    }

    private static void Bc2Alpha(byte[] b, int o, byte[] rgba)
    {
        for (int i = 0; i < 16; i++)
        {
            int v = (b[o + i / 2] >> (4 * (i & 1))) & 15;
            rgba[i * 4 + 3] = (byte)(v * 17);
        }
    }

    private static void Bc4(byte[] b, int o, byte[] rgba, int channel)
    {
        int a0 = b[o], a1 = b[o + 1];
        Span<int> v = stackalloc int[8];
        v[0] = a0; v[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i < 5; i++) v[i + 1] = ((5 - i) * a0 + i * a1) / 5; v[6] = 0; v[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[o + 2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) rgba[i * 4 + channel] = (byte)v[(int)((bits >> (3 * i)) & 7)];
    }

    // ───────────────────────── BC7 (tabelle e procedimento come il decodificatore di Pillow, BcnDecode.c)

    private static readonly byte[,] Modes =
    {
        // ns pb rb isb cb ab epb spb ib ib2
        { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
        { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
        { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
        { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
        { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
        { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
        { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
        { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
    };

    private static readonly ushort[] Si2 =
    {
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec,
        0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000, 0xf710, 0x008e, 0x7100, 0x08ce,
        0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0,
        0x718e, 0x399c, 0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
        0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660, 0x0272, 0x04e4,
        0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718,
        0xccf0, 0x0fcc, 0x7744, 0xee22,
    };

    private static readonly uint[] Si3 =
    {
        0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0,
        0x5a5a5050, 0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4,
        0xa9a59450, 0x2a0a4250, 0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454,
        0x6a6a4040, 0xa4a45000, 0x1a1a0500, 0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400,
        0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200, 0xa9a58000, 0x5090a0a8, 0xa8a09050,
        0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50, 0x500aa550, 0xaaaa4444,
        0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600, 0xaa444444,
        0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
        0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44,
        0x2a4a5254,
    };

    private static readonly byte[] Ai0 =
    {
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8,
        8, 15, 2, 8, 2, 2, 8, 8, 2, 2, 15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2,
        2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    };

    private static readonly byte[] Ai1 =
    {
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3,
        6, 10, 5, 8, 8, 6, 8, 5, 15, 15, 8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5,
        15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    };

    private static readonly byte[] Ai2 =
    {
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15,
        15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15,
        6, 10, 15, 15, 10, 8, 15, 3, 15, 10, 10, 8, 9,
        10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    };

    private static readonly byte[] W2 = { 0, 21, 43, 64 };
    private static readonly byte[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly byte[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
    private static byte[] Weights(int n) => n == 2 ? W2 : n == 3 ? W3 : W4;

    private static int Bits(byte[] b, int o, int bit, int n)
    {
        if (n == 0) return 0;
        int v = 0;
        for (int i = 0; i < n; i++)
        {
            int p = bit + i;
            v |= ((b[o + (p >> 3)] >> (p & 7)) & 1) << i;
        }
        return v;
    }

    private static int Expand(int v, int bits)
    {
        v = (v << (8 - bits)) & 0xFF;
        return v | (v >> bits);
    }

    private static void Bc7(byte[] b, int o, byte[] rgba)
    {
        int first = b[o];
        if (first == 0)
        {
            for (int i = 0; i < 16; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; }
            return;
        }
        int mode = 0;
        while ((first & (1 << mode)) == 0) mode++;
        int bit = mode + 1;
        int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3], cb = Modes[mode, 4], ab = Modes[mode, 5],
            epb = Modes[mode, 6], spb = Modes[mode, 7], ib = Modes[mode, 8], ib2 = Modes[mode, 9];
        var cw = Weights(ib);
        var aw = Weights(ab != 0 && ib2 != 0 ? ib2 : ib);
        int partition = Bits(b, o, bit, pb); bit += pb;
        int rotation = Bits(b, o, bit, rb); bit += rb;
        int indexSel = Bits(b, o, bit, isb); bit += isb;
        int numep = ns << 1;
        Span<int> er = stackalloc int[6], eg = stackalloc int[6], eb = stackalloc int[6], ea = stackalloc int[6];
        for (int i = 0; i < numep; i++) { er[i] = Bits(b, o, bit, cb); bit += cb; }
        for (int i = 0; i < numep; i++) { eg[i] = Bits(b, o, bit, cb); bit += cb; }
        for (int i = 0; i < numep; i++) { eb[i] = Bits(b, o, bit, cb); bit += cb; }
        for (int i = 0; i < numep; i++) { if (ab != 0) { ea[i] = Bits(b, o, bit, ab); bit += ab; } else ea[i] = 255; }
        if (epb != 0)
        {
            cb++; if (ab != 0) ab++;
            for (int i = 0; i < numep; i++)
            {
                int p = Bits(b, o, bit, 1); bit++;
                er[i] = (er[i] << 1) | p; eg[i] = (eg[i] << 1) | p; eb[i] = (eb[i] << 1) | p;
                if (ab != 0) ea[i] = (ea[i] << 1) | p;
            }
        }
        if (spb != 0)
        {
            cb++; if (ab != 0) ab++;
            for (int i = 0; i < numep; i += 2)
            {
                int p = Bits(b, o, bit, 1); bit++;
                for (int j = 0; j < 2; j++)
                {
                    er[i + j] = (er[i + j] << 1) | p; eg[i + j] = (eg[i + j] << 1) | p; eb[i + j] = (eb[i + j] << 1) | p;
                    if (ab != 0) ea[i + j] = (ea[i + j] << 1) | p;
                }
            }
        }
        for (int i = 0; i < numep; i++)
        {
            er[i] = Expand(er[i], cb); eg[i] = Expand(eg[i], cb); eb[i] = Expand(eb[i], cb);
            if (ab != 0) ea[i] = Expand(ea[i], ab);
        }
        int cibit = bit, aibit = cibit + 16 * ib - ns;
        for (int i = 0; i < 16; i++)
        {
            int s = (ns == 2 ? 1 & (Si2[partition] >> i) : ns == 3 ? (int)(3 & (Si3[partition] >> (2 * i))) : 0) << 1;
            int n = ib;
            if (i == 0) n--;
            else if (ns == 2) { if (i == Ai0[partition]) n--; }
            else if (ns == 3) { if (i == Ai1[partition] || i == Ai2[partition]) n--; }
            int i0 = Bits(b, o, cibit, n); cibit += n;
            int wc, wa;
            if (ab != 0 && ib2 != 0)
            {
                int n2 = ib2; if (i == 0) n2--;
                int i1 = Bits(b, o, aibit, n2); aibit += n2;
                if (indexSel != 0) { wc = aw[i1]; wa = cw[i0]; } else { wc = cw[i0]; wa = aw[i1]; }
            }
            else wc = wa = cw[i0];
            int r = ((64 - wc) * er[s] + wc * er[s + 1] + 32) >> 6;
            int g = ((64 - wc) * eg[s] + wc * eg[s + 1] + 32) >> 6;
            int bl = ((64 - wc) * eb[s] + wc * eb[s + 1] + 32) >> 6;
            int a = ((64 - wa) * ea[s] + wa * ea[s + 1] + 32) >> 6;
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (bl, a) = (a, bl); break;
            }
            rgba[i * 4] = (byte)r; rgba[i * 4 + 1] = (byte)g; rgba[i * 4 + 2] = (byte)bl; rgba[i * 4 + 3] = (byte)a;
        }
    }
}
