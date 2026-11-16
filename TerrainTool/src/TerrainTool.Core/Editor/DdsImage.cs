namespace TerrainTool.Core.Editor;

/// <summary>
/// Lettura di una texture .dds (DXT1, DXT3, DXT5, BC7 con intestazione DX10, o 32 bit non compressa) in BGRA, scegliendo il livello mip
/// piu' grande non oltre maxSide pixel. Per le texture dei modelli nell'anteprima 3D (KoreaObj).
/// </summary>
public static class DdsImage
{
    public static (byte[] Bgra, int W, int H)? Load(string path, int maxSide = 512)
    {
        byte[] f;
        try { f = File.ReadAllBytes(path); } catch (IOException) { return null; }
        if (f.Length < 128 || f[0] != 'D' || f[1] != 'D' || f[2] != 'S') return null;
        int h0 = BitConverter.ToInt32(f, 12), w0 = BitConverter.ToInt32(f, 16), mips = Math.Max(1, BitConverter.ToInt32(f, 28));
        int pfFlags = BitConverter.ToInt32(f, 80);
        string four = System.Text.Encoding.ASCII.GetString(f, 84, 4);
        int rgbBits = BitConverter.ToInt32(f, 88);
        int kind = four switch { "DXT1" => 1, "DXT3" => 3, "DXT5" => 5, _ => (pfFlags & 0x40) != 0 && rgbBits == 32 ? 32 : 0 };
        long off = 128;
        if (four == "DX10" && f.Length >= 148)
        {
            // intestazione DX10: formato DXGI (BC1 71/72, BC2 74/75, BC3 77/78, BC7 98/99, B8G8R8A8 87/91)
            int dxgi = BitConverter.ToInt32(f, 128);
            kind = dxgi switch { 71 or 72 => 1, 74 or 75 => 3, 77 or 78 => 5, 98 or 99 => 7, 87 or 91 => 32, _ => 0 };
            off = 148;
        }
        if (kind == 0 || w0 <= 0 || h0 <= 0) return null;
        int level = 0, w = w0, h = h0;
        while (level < mips - 1 && Math.Max(w, h) > maxSide)
        {
            off += Size(kind, w, h);
            w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); level++;
        }
        if (off + Size(kind, w, h) > f.Length) return null;
        var outp = new byte[w * h * 4];
        if (kind == 32)
        {
            // A8R8G8B8 in memoria = B G R A
            Buffer.BlockCopy(f, (int)off, outp, 0, w * h * 4);
            return (outp, w, h);
        }
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), bs = kind == 1 ? 8 : 16;
        if (kind == 7)
        {
            Span<byte> px = stackalloc byte[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    Bc7.DecodeBlock(f.AsSpan((int)off + (by * bw + bx) * 16, 16), px);
                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                        {
                            int x = bx * 4 + i, y = by * 4 + j;
                            if (x < w && y < h) px.Slice((j * 4 + i) * 4, 4).CopyTo(outp.AsSpan((y * w + x) * 4, 4));
                        }
                }
            return (outp, w, h);
        }
        Span<byte> alpha = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int o = (int)off + (by * bw + bx) * bs;
                alpha.Fill(255);
                if (kind == 3) for (int i = 0; i < 16; i++) { int v = (f[o + i / 2] >> (i % 2 * 4)) & 15; alpha[i] = (byte)(v * 17); }
                else if (kind == 5)
                {
                    int a0 = f[o], a1 = f[o + 1];
                    ulong bits = 0; for (int i = 0; i < 6; i++) bits |= (ulong)f[o + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                    {
                        int c = (int)(bits >> (3 * i) & 7);
                        alpha[i] = (byte)(c == 0 ? a0 : c == 1 ? a1 : a0 > a1 ? ((8 - c) * a0 + (c - 1) * a1) / 7 : c == 6 ? 0 : c == 7 ? 255 : ((6 - c) * a0 + (c - 1) * a1) / 5);
                    }
                }
                Color(f, kind == 1 ? o : o + 8, kind == 1, outp, w, h, bx * 4, by * 4, alpha);
            }
        return (outp, w, h);
    }

    private static long Size(int kind, int w, int h) =>
        kind == 32 ? (long)w * h * 4 : (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (kind == 1 ? 8 : 16);   // BC7 = 16 byte per blocco come DXT5

    private static void Color(byte[] s, int o, bool dxt1, byte[] dst, int w, int h, int x0, int y0, ReadOnlySpan<byte> alpha)
    {
        int c0 = s[o] | s[o + 1] << 8, c1 = s[o + 2] | s[o + 3] << 8;
        Span<int> r = stackalloc int[4], g = stackalloc int[4], b = stackalloc int[4], a = stackalloc int[4];
        r[0] = (c0 >> 11 & 31) * 255 / 31; g[0] = (c0 >> 5 & 63) * 255 / 63; b[0] = (c0 & 31) * 255 / 31;
        r[1] = (c1 >> 11 & 31) * 255 / 31; g[1] = (c1 >> 5 & 63) * 255 / 63; b[1] = (c1 & 31) * 255 / 31;
        a[0] = a[1] = a[2] = a[3] = 255;
        if (c0 > c1 || !dxt1)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
            r[3] = g[3] = b[3] = 0; a[3] = 0;
        }
        uint bits = BitConverter.ToUInt32(s, o + 4);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int x = x0 + i, y = y0 + j;
                if (x >= w || y >= h) continue;
                int idx = (int)(bits >> 2 * (j * 4 + i) & 3);
                int p = (y * w + x) * 4;
                dst[p] = (byte)b[idx]; dst[p + 1] = (byte)g[idx]; dst[p + 2] = (byte)r[idx];
                dst[p + 3] = (byte)Math.Min(a[idx], alpha[j * 4 + i]);
            }
    }
}
