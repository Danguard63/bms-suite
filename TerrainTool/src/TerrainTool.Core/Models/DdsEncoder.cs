using TerrainTool.Core.Dds;

namespace TerrainTool.Core.Models;

/// <summary>
/// Riscrive un'immagine RGBA nello stesso formato DDS di una texture esistente (stessa intestazione, dimensioni e numero di
/// mipmap): DXT1 (anche con trasparenza a 1 bit), DXT3, DXT5, BC4, BC5, BC7 (modo 6), BGRA/BGRX/RGBA 8 bit. Serve a
/// sostituire le texture dei modelli BMS con un'immagine qualsiasi. Le mipmap si fanno con media 2×2.
/// </summary>
public static class DdsEncoder
{
    public enum Kind { Bc1, Bc2, Bc3, Bc4, Bc5, Bc7, Bgra8, Bgrx8, Rgba8 }

    public sealed record Info(int Width, int Height, int Mips, Kind Format, int HeaderLength, bool Srgb, string Label)
    {
        public bool Block => Format <= Kind.Bc7;
        public int BlockBytes => Format is Kind.Bc1 or Kind.Bc4 ? 8 : 16;
        public long LevelSize(int w, int h) => Block ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes : (long)w * h * 4;
        public long DataSize() { long s = 0; int w = Width, h = Height; for (int i = 0; i < Mips; i++) { s += LevelSize(w, h); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); } return s; }
    }

    /// <summary>Formato di una DDS (dai primi 148 byte), o null se non e' uno dei formati gestiti.</summary>
    public static Info? Inspect(byte[] b)
    {
        if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S' || b[3] != ' ') return null;
        int h = BitConverter.ToInt32(b, 12), w = BitConverter.ToInt32(b, 16);
        uint flags = BitConverter.ToUInt32(b, 8);
        int mips = (flags & 0x20000) != 0 ? Math.Max(1, BitConverter.ToInt32(b, 28)) : 1;
        uint pf = BitConverter.ToUInt32(b, 80);
        string four = System.Text.Encoding.ASCII.GetString(b, 84, 4);
        int bits = BitConverter.ToInt32(b, 88);
        uint rMask = BitConverter.ToUInt32(b, 92), aMask = BitConverter.ToUInt32(b, 104);
        if ((pf & 4) != 0)
        {
            switch (four)
            {
                case "DXT1": return new Info(w, h, mips, Kind.Bc1, 128, false, "DXT1");
                case "DXT2": case "DXT3": return new Info(w, h, mips, Kind.Bc2, 128, false, "DXT3");
                case "DXT4": case "DXT5": return new Info(w, h, mips, Kind.Bc3, 128, false, "DXT5");
                case "ATI1": case "BC4U": return new Info(w, h, mips, Kind.Bc4, 128, false, "BC4");
                case "ATI2": case "BC5U": return new Info(w, h, mips, Kind.Bc5, 128, false, "BC5");
                case "DX10":
                    if (b.Length < 148) return null;
                    int dxgi = BitConverter.ToInt32(b, 128);
                    (Kind k, bool srgb, string l)? f = dxgi switch
                    {
                        70 or 71 => (Kind.Bc1, false, "BC1"), 72 => (Kind.Bc1, true, "BC1 sRGB"),
                        73 or 74 => (Kind.Bc2, false, "BC2"), 75 => (Kind.Bc2, true, "BC2 sRGB"),
                        76 or 77 => (Kind.Bc3, false, "BC3"), 78 => (Kind.Bc3, true, "BC3 sRGB"),
                        79 or 80 or 81 => (Kind.Bc4, false, "BC4"), 82 or 83 or 84 => (Kind.Bc5, false, "BC5"),
                        97 or 98 => (Kind.Bc7, false, "BC7"), 99 => (Kind.Bc7, true, "BC7 sRGB"),
                        27 or 28 => (Kind.Rgba8, false, "RGBA8"), 29 => (Kind.Rgba8, true, "RGBA8 sRGB"),
                        87 or 90 => (Kind.Bgra8, false, "BGRA8"), 91 => (Kind.Bgra8, true, "BGRA8 sRGB"),
                        88 or 92 => (Kind.Bgrx8, false, "BGRX8"), 93 => (Kind.Bgrx8, true, "BGRX8 sRGB"),
                        _ => null,
                    };
                    return f is { } x ? new Info(w, h, mips, x.k, 148, x.srgb, x.l) : null;
                default: return null;
            }
        }
        if (bits == 32) return rMask == 0x000000FF ? new Info(w, h, mips, Kind.Rgba8, 128, false, "RGBA8") : aMask != 0 ? new Info(w, h, mips, Kind.Bgra8, 128, false, "BGRA8") : new Info(w, h, mips, Kind.Bgrx8, 128, false, "BGRX8");
        return null;
    }

    /// <summary>
    /// File DDS completo: intestazione dell'originale (header) + tutti i livelli codificati da rgba (Width×Height, 4 byte per
    /// pixel RGBA, riga 0 in alto).
    /// </summary>
    public static byte[] Encode(Info info, ReadOnlySpan<byte> header, byte[] rgba, IProgress<double>? p = null, CancellationToken ct = default)
    {
        if (rgba.Length != (long)info.Width * info.Height * 4) throw new ArgumentException("image size differs from the texture");
        var outp = new byte[info.HeaderLength + info.DataSize()];
        header[..info.HeaderLength].CopyTo(outp);
        long off = info.HeaderLength;
        int w = info.Width, h = info.Height;
        byte[] level = rgba;
        long total = info.DataSize(), done = 0;
        for (int m = 0; m < info.Mips; m++)
        {
            ct.ThrowIfCancellationRequested();
            EncodeLevel(info, level, w, h, outp.AsSpan((int)off, (int)info.LevelSize(w, h)), ct);
            off += info.LevelSize(w, h);
            done += info.LevelSize(w, h);
            p?.Report((double)done / total);
            if (m < info.Mips - 1) { level = Half(level, w, h, out int nw, out int nh); w = nw; h = nh; }
        }
        return outp;
    }

    /// <summary>Media 2×2 (con i lati dispari si ripete l'ultima riga/colonna).</summary>
    public static byte[] Half(byte[] src, int w, int h, out int nw, out int nh)
    {
        nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
        var d = new byte[nw * nh * 4];
        int ow = nw, oh = nh;
        Parallel.For(0, oh, y =>
        {
            int y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
            for (int x = 0; x < ow; x++)
            {
                int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1);
                for (int c = 0; c < 4; c++)
                    d[(y * ow + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c] + 2) >> 2);
            }
        });
        return d;
    }

    /// <summary>Ridimensionamento bilineare RGBA (per immagini di dimensione diversa dall'originale).</summary>
    public static byte[] Resize(byte[] src, int w, int h, int nw, int nh)
    {
        var d = new byte[nw * nh * 4];
        // prima si dimezza finche' serve (evita l'aliasing nelle riduzioni forti)
        while (w >= nw * 2 && h >= nh * 2) { src = Half(src, w, h, out int hw, out int hh); w = hw; h = hh; }
        int sw = w, sh = h; var s = src;
        Parallel.For(0, nh, y =>
        {
            double fy = (y + 0.5) * sh / nh - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, sh - 1), y1 = Math.Min(sh - 1, y0 + 1);
            double ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < nw; x++)
            {
                double fx = (x + 0.5) * sw / nw - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, sw - 1), x1 = Math.Min(sw - 1, x0 + 1);
                double tx = Math.Clamp(fx - x0, 0, 1);
                for (int c = 0; c < 4; c++)
                {
                    double a = s[(y0 * sw + x0) * 4 + c] * (1 - tx) + s[(y0 * sw + x1) * 4 + c] * tx;
                    double b = s[(y1 * sw + x0) * 4 + c] * (1 - tx) + s[(y1 * sw + x1) * 4 + c] * tx;
                    d[(y * nw + x) * 4 + c] = (byte)Math.Round(a * (1 - ty) + b * ty);
                }
            }
        });
        return d;
    }

    private static void EncodeLevel(Info info, byte[] px, int w, int h, Span<byte> dst, CancellationToken ct)
    {
        if (!info.Block)
        {
            for (int i = 0; i < w * h; i++)
            {
                byte r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2], a = px[i * 4 + 3];
                int o = i * 4;
                switch (info.Format)
                {
                    case Kind.Rgba8: dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = a; break;
                    case Kind.Bgra8: dst[o] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = a; break;
                    default: dst[o] = b; dst[o + 1] = g; dst[o + 2] = r; dst[o + 3] = 255; break;
                }
            }
            return;
        }
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), bb = info.BlockBytes;
        var outBlocks = new byte[(long)bw * bh * bb];
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, bh, po, by =>
        {
            Span<byte> blk = stackalloc byte[64];
            Span<byte> tmp = stackalloc byte[16];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(w - 1, bx * 4 + x), sy = Math.Min(h - 1, by * 4 + y);
                        int s = (sy * w + sx) * 4, d = (y * 4 + x) * 4;
                        blk[d] = px[s]; blk[d + 1] = px[s + 1]; blk[d + 2] = px[s + 2]; blk[d + 3] = px[s + 3];
                    }
                var o = outBlocks.AsSpan((by * bw + bx) * bb, bb);
                switch (info.Format)
                {
                    case Kind.Bc1: Bc1(blk, o, allowAlpha: true); break;
                    case Kind.Bc2: Bc2Alpha(blk, o[..8]); Bc1(blk, o[8..], allowAlpha: false); break;
                    case Kind.Bc3: Bc4(blk, 3, o[..8]); Bc1(blk, o[8..], allowAlpha: false); break;
                    case Kind.Bc4: Bc4(blk, 0, o); break;
                    case Kind.Bc5: Bc4(blk, 0, o[..8]); Bc4(blk, 1, o[8..]); break;
                    case Kind.Bc7: Bc7Mode6(blk, o); break;
                }
            }
        });
        outBlocks.CopyTo(dst);
    }

    // ───────────────────────── BC1 (con trasparenza a 1 bit se richiesta)

    private static void Bc1(ReadOnlySpan<byte> rgba, Span<byte> dst, bool allowAlpha)
    {
        bool transparent = false;
        if (allowAlpha) for (int i = 0; i < 16; i++) if (rgba[i * 4 + 3] < 128) { transparent = true; break; }
        if (!transparent)
        {
            Span<byte> rgb = stackalloc byte[48];
            for (int i = 0; i < 16; i++) { rgb[i * 3] = rgba[i * 4]; rgb[i * 3 + 1] = rgba[i * 4 + 1]; rgb[i * 3 + 2] = rgba[i * 4 + 2]; }
            Bc1Encoder.EncodeBlock(rgb, dst);
            if (allowAlpha)
            {
                // DXT1 opaco: deve restare in modo 4 colori (c0 > c1); se uguali va bene comunque (nessun pixel usa l'indice 3)
                ushort c0 = (ushort)(dst[0] | dst[1] << 8), c1 = (ushort)(dst[2] | dst[3] << 8);
                if (c0 < c1)
                {
                    dst[0] = (byte)c1; dst[1] = (byte)(c1 >> 8); dst[2] = (byte)c0; dst[3] = (byte)(c0 >> 8);
                    uint idx = (uint)(dst[4] | dst[5] << 8 | dst[6] << 16 | dst[7] << 24), ni = 0;
                    for (int i = 0; i < 16; i++) { uint v = (idx >> (2 * i)) & 3; ni |= (v ^ 1u) << (2 * i); }   // 0<->1, 2<->3
                    dst[4] = (byte)ni; dst[5] = (byte)(ni >> 8); dst[6] = (byte)(ni >> 16); dst[7] = (byte)(ni >> 24);
                }
            }
            return;
        }
        // modo 3 colori: c0 <= c1, indice 3 = trasparente
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, n = 0;
        for (int i = 0; i < 16; i++)
        {
            if (rgba[i * 4 + 3] < 128) continue;
            n++;
            minR = Math.Min(minR, rgba[i * 4]); minG = Math.Min(minG, rgba[i * 4 + 1]); minB = Math.Min(minB, rgba[i * 4 + 2]);
            maxR = Math.Max(maxR, rgba[i * 4]); maxG = Math.Max(maxG, rgba[i * 4 + 1]); maxB = Math.Max(maxB, rgba[i * 4 + 2]);
        }
        ushort a = n > 0 ? Bc1Encoder.Pack(minR, minG, minB) : (ushort)0, b = n > 0 ? Bc1Encoder.Pack(maxR, maxG, maxB) : (ushort)0;
        if (a > b) (a, b) = (b, a);
        var (ar, ag, ab) = Bc1Encoder.Unpack(a); var (br, bg, bbb) = Bc1Encoder.Unpack(b);
        Span<int> pr = stackalloc int[] { ar, br, (ar + br) / 2 }, pg = stackalloc int[] { ag, bg, (ag + bg) / 2 }, pb = stackalloc int[] { ab, bbb, (ab + bbb) / 2 };
        uint ix = 0;
        for (int i = 0; i < 16; i++)
        {
            uint best = 3;
            if (rgba[i * 4 + 3] >= 128)
            {
                int be = int.MaxValue;
                for (uint k = 0; k < 3; k++)
                {
                    int dr = rgba[i * 4] - pr[(int)k], dg = rgba[i * 4 + 1] - pg[(int)k], db = rgba[i * 4 + 2] - pb[(int)k], e = dr * dr + dg * dg + db * db;
                    if (e < be) { be = e; best = k; }
                }
            }
            ix |= best << (2 * i);
        }
        dst[0] = (byte)a; dst[1] = (byte)(a >> 8); dst[2] = (byte)b; dst[3] = (byte)(b >> 8);
        dst[4] = (byte)ix; dst[5] = (byte)(ix >> 8); dst[6] = (byte)(ix >> 16); dst[7] = (byte)(ix >> 24);
    }

    private static void Bc2Alpha(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        for (int i = 0; i < 16; i += 2)
        {
            int a0 = (rgba[i * 4 + 3] * 15 + 127) / 255, a1 = (rgba[(i + 1) * 4 + 3] * 15 + 127) / 255;
            dst[i / 2] = (byte)(a0 | a1 << 4);
        }
    }

    /// <summary>Blocco BC4 di un canale (0 = R, 1 = G, 3 = A), modo a 8 valori.</summary>
    private static void Bc4(ReadOnlySpan<byte> rgba, int ch, Span<byte> dst)
    {
        int mn = 255, mx = 0;
        for (int i = 0; i < 16; i++) { int v = rgba[i * 4 + ch]; mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
        dst[0] = (byte)mx; dst[1] = (byte)mn;
        Span<int> pal = stackalloc int[8];
        pal[0] = mx; pal[1] = mn;
        for (int k = 1; k <= 6; k++) pal[k + 1] = ((7 - k) * mx + k * mn) / 7;
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = rgba[i * 4 + ch], best = 0, be = int.MaxValue;
            if (mx != mn)
                for (int k = 0; k < 8; k++) { int e = Math.Abs(v - pal[k]); if (e < be) { be = e; best = k; } }
            bits |= (ulong)best << (3 * i);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    // ───────────────────────── BC7 modo 6 (RGBA, un solo sottoinsieme, indici a 4 bit, estremi 7 bit + p-bit)

    private static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    private static void Bc7Mode6(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        // asse principale RGBA
        Span<double> mean = stackalloc double[4];
        for (int i = 0; i < 16; i++) for (int c = 0; c < 4; c++) mean[c] += px[i * 4 + c];
        for (int c = 0; c < 4; c++) mean[c] /= 16;
        Span<double> cov = stackalloc double[16];
        for (int i = 0; i < 16; i++)
            for (int a = 0; a < 4; a++)
                for (int b = 0; b < 4; b++) cov[a * 4 + b] += (px[i * 4 + a] - mean[a]) * (px[i * 4 + b] - mean[b]);
        Span<double> axis = stackalloc double[] { 1, 1, 1, 1 };
        Span<double> nx = stackalloc double[4];
        for (int it = 0; it < 8; it++)
        {
            for (int a = 0; a < 4; a++) { nx[a] = 0; for (int b = 0; b < 4; b++) nx[a] += cov[a * 4 + b] * axis[b]; }
            double len = Math.Sqrt(nx[0] * nx[0] + nx[1] * nx[1] + nx[2] * nx[2] + nx[3] * nx[3]);
            if (len < 1e-9) break;
            for (int a = 0; a < 4; a++) axis[a] = nx[a] / len;
        }
        double tmin = double.MaxValue, tmax = double.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double t = 0;
            for (int c = 0; c < 4; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
            tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
        }
        Span<int> e0 = stackalloc int[4], e1 = stackalloc int[4];
        Span<int> q0 = stackalloc int[4], q1 = stackalloc int[4];
        int p0 = Quant(mean, axis, tmin, e0, q0), p1 = Quant(mean, axis, tmax, e1, q1);

        Span<int> idx = stackalloc int[16];
        Span<int> pal = stackalloc int[64];
        for (int k = 0; k < 16; k++) for (int c = 0; c < 4; c++) pal[k * 4 + c] = ((64 - W4[k]) * e0[c] + W4[k] * e1[c] + 32) >> 6;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, be = int.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                int e = 0;
                for (int c = 0; c < 4; c++) { int d = px[i * 4 + c] - pal[k * 4 + c]; e += d * d; }
                if (e < be) { be = e; best = k; }
            }
            idx[i] = best;
        }
        // l'indice del pixel 0 deve avere il bit alto a zero: altrimenti si scambiano gli estremi
        if (idx[0] >= 8)
        {
            for (int c = 0; c < 4; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            (p0, p1) = (p1, p0);
            for (int i = 0; i < 16; i++) idx[i] = 15 - idx[i];
        }
        dst.Clear();
        int pos = 0;
        Put(dst, ref pos, 1 << 6, 7);
        for (int c = 0; c < 4; c++) { Put(dst, ref pos, q0[c], 7); Put(dst, ref pos, q1[c], 7); }
        Put(dst, ref pos, p0, 1); Put(dst, ref pos, p1, 1);
        Put(dst, ref pos, idx[0], 3);
        for (int i = 1; i < 16; i++) Put(dst, ref pos, idx[i], 4);
    }

    private static void Put(Span<byte> dst, ref int pos, int v, int n)
    {
        for (int b = 0; b < n; b++, pos++) if (((v >> b) & 1) != 0) dst[pos >> 3] |= (byte)(1 << (pos & 7));
    }

    /// <summary>Estremo sull'asse in t: valori 7 bit (q) e p-bit scelti per il minimo errore; e = valore a 8 bit risultante.</summary>
    private static int Quant(ReadOnlySpan<double> mean, ReadOnlySpan<double> axis, double t, Span<int> e, Span<int> q)
    {
        Span<double> v = stackalloc double[4];
        for (int c = 0; c < 4; c++) v[c] = Math.Clamp(mean[c] + axis[c] * t, 0, 255);
        int bestP = 0; double bestErr = double.MaxValue;
        Span<int> tq = stackalloc int[4];
        for (int p = 0; p < 2; p++)
        {
            double err = 0;
            for (int c = 0; c < 4; c++)
            {
                tq[c] = Math.Clamp((int)Math.Round((v[c] - p) / 2), 0, 127);
                double d = v[c] - (tq[c] * 2 + p); err += d * d;
            }
            if (err < bestErr) { bestErr = err; bestP = p; for (int c = 0; c < 4; c++) q[c] = tq[c]; }
        }
        for (int c = 0; c < 4; c++) e[c] = q[c] * 2 + bestP;
        return bestP;
    }
}

/// <summary>Lettura di immagini TGA (truecolor 24/32 bit, anche RLE) in RGBA, riga 0 in alto.</summary>
public static class TgaImage
{
    public static (byte[] Rgba, int W, int H) Read(byte[] b)
    {
        int idLen = b[0], cmapType = b[1], type = b[2];
        if (cmapType != 0 || (type != 2 && type != 10)) throw new InvalidDataException("only true-colour TGA (24/32 bit) is supported");
        int w = BitConverter.ToUInt16(b, 12), h = BitConverter.ToUInt16(b, 14), bpp = b[16], desc = b[17];
        if (bpp != 24 && bpp != 32) throw new InvalidDataException($"TGA with {bpp} bit per pixel is not supported");
        int bytes = bpp / 8, p = 18 + idLen;
        var raw = new byte[w * h * 4];
        int n = 0;
        void Pix(int at) { raw[n * 4] = b[at + 2]; raw[n * 4 + 1] = b[at + 1]; raw[n * 4 + 2] = b[at]; raw[n * 4 + 3] = bytes == 4 ? b[at + 3] : (byte)255; n++; }
        if (type == 2) { while (n < w * h) { Pix(p); p += bytes; } }
        else
            while (n < w * h)
            {
                int c = b[p++], cnt = (c & 0x7F) + 1;
                if ((c & 0x80) != 0) { for (int i = 0; i < cnt && n < w * h; i++) Pix(p); p += bytes; }
                else for (int i = 0; i < cnt && n < w * h; i++) { Pix(p); p += bytes; }
            }
        if ((desc & 0x20) == 0)   // origine in basso: si capovolge
        {
            var f = new byte[raw.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * w * 4, f, y * w * 4, w * 4);
            raw = f;
        }
        return (raw, w, h);
    }
}
