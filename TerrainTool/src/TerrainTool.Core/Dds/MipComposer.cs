namespace TerrainTool.Core.Dds;

/// <summary>Formati delle mappe colore, ricalcati sui file della Korea.</summary>
public enum ColorDdsFormat
{
    /// <summary>DXT1 con intestazione classica (come le tessere Photoreal\4K di Korea e Israel).</summary>
    Dxt1,
    /// <summary>DXT5 con intestazione classica (come Photoreal\GlobalColorMap.dds della Korea).</summary>
    Dxt5,
    /// <summary>BC3 sRGB con intestazione DX10: la Korea usa BC7 sRGB per WaterMaps\ColorMap.dds; BC3 tiene lo stesso spazio colore.</summary>
    Bc3SrgbDx10,
}

/// <summary>
/// Compone una DDS a colori con tutte le mipmap da un'immagine RGB in memoria (8 bit, pixel interlacciati).
/// Alfa sempre 255, come nella GlobalColorMap della Korea.
/// </summary>
public static class MipComposer
{
    public static int LevelCount(int size) { int n = 1; while (size > 1) { size /= 2; n++; } return n; }

    public static byte[] Header(int size, ColorDdsFormat fmt)
    {
        bool dx10 = fmt == ColorDdsFormat.Bc3SrgbDx10;
        var h = new byte[dx10 ? 148 : 128];
        void W(int off, uint v) => BitConverter.TryWriteBytes(h.AsSpan(off, 4), v);
        h[0] = (byte)'D'; h[1] = (byte)'D'; h[2] = (byte)'S'; h[3] = (byte)' ';
        W(4, 124);
        W(8, 0x000A1007);                 // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
        W(12, (uint)size);
        W(16, (uint)size);
        W(20, (uint)(fmt == ColorDdsFormat.Dxt1 ? (long)size * size / 2 : (long)size * size));  // BC1: ½ byte, BC3: 1 byte per pixel
        W(24, 1);
        W(28, (uint)LevelCount(size));
        W(76, 32);
        W(80, 0x4);                       // DDPF_FOURCC
        var four = dx10 ? "DX10" : fmt == ColorDdsFormat.Dxt1 ? "DXT1" : "DXT5";
        for (int i = 0; i < 4; i++) h[84 + i] = (byte)four[i];
        W(108, 0x401008);                 // COMPLEX|TEXTURE|MIPMAP
        if (dx10)
        {
            W(128, 78);                   // DXGI_FORMAT_BC3_UNORM_SRGB
            W(132, 3);                    // TEXTURE2D
            W(136, 0);
            W(140, 1);                    // array size
            W(144, 0);
        }
        return h;
    }

    /// <summary>Scrive la DDS; restituisce un'anteprima RGB PreviewSize².</summary>
    public static byte[] Compose(string outPath, byte[] rgb, int size, ColorDdsFormat fmt, IProgress<double>? p, CancellationToken ct, bool makePreview = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        string tmp = outPath + ".tmp";
        int levels = LevelCount(size);
        long totalBlocks = 0;
        for (int l = 0, s = size; l < levels; l++, s = Math.Max(1, s / 2)) totalBlocks += (long)((s + 3) / 4) * ((s + 3) / 4);
        long doneBlocks = 0;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            fs.Write(Header(size, fmt));
            byte[] cur = rgb;
            int s = size;
            for (int l = 0; l < levels; l++)
            {
                ct.ThrowIfCancellationRequested();
                int bw = (s + 3) / 4;
                int bs = fmt == ColorDdsFormat.Dxt1 ? 8 : 16;
                var outBuf = new byte[(long)bw * bw * bs];
                var level = cur; int ls = s;
                Parallel.For(0, bw, new ParallelOptions { CancellationToken = ct }, by =>
                {
                    Span<byte> px = stackalloc byte[48];
                    for (int bx = 0; bx < bw; bx++)
                    {
                        for (int k = 0; k < 4; k++)
                            for (int i = 0; i < 4; i++)
                            {
                                int y = Math.Min(by * 4 + k, ls - 1), x = Math.Min(bx * 4 + i, ls - 1);
                                long o = ((long)y * ls + x) * 3;
                                int d = (k * 4 + i) * 3;
                                px[d] = level[o]; px[d + 1] = level[o + 1]; px[d + 2] = level[o + 2];
                            }
                        var blk = outBuf.AsSpan((int)(((long)by * bw + bx) * bs), bs);
                        if (bs == 8) { Bc1Encoder.EncodeBlock(px, blk); continue; }
                        blk[0] = 255; blk[1] = 255; // alfa: a0 = a1 = 255, indici 0 → opaco
                        blk.Slice(2, 6).Clear();
                        Bc1Encoder.EncodeBlock(px, blk.Slice(8, 8));
                    }
                });
                fs.Write(outBuf);
                doneBlocks += (long)bw * bw;
                p?.Report((double)doneBlocks / totalBlocks);
                if (l < levels - 1) { cur = Downsample(cur, s); s = Math.Max(1, s / 2); }
            }
        }
        File.Move(tmp, outPath, true);
        return makePreview ? Preview(rgb, size) : Array.Empty<byte>();
    }

    /// <summary>Riduzione 2:1 con media 2×2.</summary>
    public static byte[] Downsample(byte[] src, int size)
    {
        int n = Math.Max(1, size / 2);
        var dst = new byte[(long)n * n * 3];
        Parallel.For(0, n, y =>
        {
            int y0 = Math.Min(2 * y, size - 1), y1 = Math.Min(2 * y + 1, size - 1);
            for (int x = 0; x < n; x++)
            {
                int x0 = Math.Min(2 * x, size - 1), x1 = Math.Min(2 * x + 1, size - 1);
                for (int c = 0; c < 3; c++)
                {
                    int v = src[((long)y0 * size + x0) * 3 + c] + src[((long)y0 * size + x1) * 3 + c]
                          + src[((long)y1 * size + x0) * 3 + c] + src[((long)y1 * size + x1) * 3 + c];
                    dst[((long)y * n + x) * 3 + c] = (byte)((v + 2) / 4);
                }
            }
        });
        return dst;
    }

    /// <summary>Sfocatura approssimata gaussiana (tre passate di media mobile) con raggio r.</summary>
    public static byte[] Blur(byte[] src, int size, int r)
    {
        if (r <= 0) return (byte[])src.Clone();
        var a = (byte[])src.Clone();
        var b = new byte[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(a, b, size, r);
            BoxV(b, a, size, r);
        }
        return a;
    }

    private static void BoxH(byte[] src, byte[] dst, int size, int r)
    {
        Parallel.For(0, size, y =>
        {
            long row = (long)y * size * 3;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0, win = 2 * r + 1;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Clamp(i, 0, size - 1) * 3 + c];
                for (int x = 0; x < size; x++)
                {
                    dst[row + x * 3 + c] = (byte)((sum + win / 2) / win);
                    sum += src[row + Math.Min(x + r + 1, size - 1) * 3 + c] - src[row + Math.Max(x - r, 0) * 3 + c];
                }
            }
        });
    }

    private static void BoxV(byte[] src, byte[] dst, int size, int r)
    {
        Parallel.For(0, size, x =>
        {
            for (int c = 0; c < 3; c++)
            {
                int sum = 0, win = 2 * r + 1;
                for (int i = -r; i <= r; i++) sum += src[((long)Math.Clamp(i, 0, size - 1) * size + x) * 3 + c];
                for (int y = 0; y < size; y++)
                {
                    dst[((long)y * size + x) * 3 + c] = (byte)((sum + win / 2) / win);
                    sum += src[((long)Math.Min(y + r + 1, size - 1) * size + x) * 3 + c] - src[((long)Math.Max(y - r, 0) * size + x) * 3 + c];
                }
            }
        });
    }

    public static byte[] Preview(byte[] rgb, int size)
    {
        int ps = DdsComposer.PreviewSize, step = Math.Max(1, size / ps);
        var prev = new byte[ps * ps * 3];
        for (int y = 0; y < ps && y * step < size; y++)
            for (int x = 0; x < ps && x * step < size; x++)
            {
                long o = ((long)y * step * size + (long)x * step) * 3;
                int d = (y * ps + x) * 3;
                prev[d] = rgb[o]; prev[d + 1] = rgb[o + 1]; prev[d + 2] = rgb[o + 2];
            }
        return prev;
    }

    /// <summary>Legge un RAW RGB (pixel interlacciati, quadrato) scritto dal tool.</summary>
    public static (byte[] Rgb, int Size) ReadRgbRaw(string path)
    {
        long len = new FileInfo(path).Length;
        int size = (int)Math.Round(Math.Sqrt(len / 3.0));
        if ((long)size * size * 3 != len) throw new InvalidDataException($"{Path.GetFileName(path)} is not a square RGB RAW");
        return (File.ReadAllBytes(path), size);
    }
}
