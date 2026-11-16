namespace TerrainTool.Core.Dds;

/// <summary>Sorgente di un canale: fornisce una riga alla risoluzione di destinazione.</summary>
public interface IChannelSource : IDisposable
{
    void ReadRow(int y, byte[] row);
}

/// <summary>Canale costante (es. R=255 della GlobalAlpha).</summary>
public sealed class ConstantChannel : IChannelSource
{
    private readonly byte _v;
    public ConstantChannel(byte v) => _v = v;
    public void ReadRow(int y, byte[] row) => Array.Fill(row, _v);
    public void Dispose() { }
}

public enum Resample { Average, Nearest }

/// <summary>
/// Canale da un RAW 8 bit quadrato, ridimensionato al lato di destinazione.
/// Riduzione di un fattore intero: media (Average) o pixel più vicino (Nearest);
/// ingrandimento di un fattore intero: ripetizione dei pixel.
/// </summary>
public sealed class RawChannel : IChannelSource
{
    private readonly FileStream _fs;
    private readonly int _src, _dst;
    private readonly Resample _mode;
    private readonly byte[] _buf;
    private int _cachedRow = -1;
    private readonly byte[] _cache;
    public int SourceSize => _src;

    public RawChannel(string path, int dstSize, Resample mode)
    {
        long len = new FileInfo(path).Length;
        int src = (int)Math.Round(Math.Sqrt(len));
        if ((long)src * src != len) throw new InvalidDataException($"{Path.GetFileName(path)}: {len} bytes is not a square 8-bit RAW");
        if (src % dstSize != 0 && dstSize % src != 0)
            throw new InvalidDataException($"{Path.GetFileName(path)}: size {src} cannot be resized to {dstSize} by an integer factor");
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 22);
        _src = src; _dst = dstSize; _mode = mode;
        int f = Math.Max(1, src / dstSize);
        _buf = new byte[src * f];
        _cache = new byte[dstSize];
    }

    public static int SizeOf(string path)
    {
        long len = new FileInfo(path).Length;
        int s = (int)Math.Round(Math.Sqrt(len));
        return (long)s * s == len ? s : -1;
    }

    public void ReadRow(int y, byte[] row)
    {
        if (_src == _dst)
        {
            _fs.Position = (long)y * _src;
            _fs.ReadExactly(row, 0, _dst);
            return;
        }
        if (_src > _dst)
        {
            int f = _src / _dst;
            if (_mode == Resample.Nearest)
            {
                // pixel centrale del blocco f×f
                _fs.Position = ((long)y * f + f / 2) * _src;
                _fs.ReadExactly(_buf, 0, _src);
                for (int x = 0; x < _dst; x++) row[x] = _buf[x * f + f / 2];
                return;
            }
            _fs.Position = (long)y * f * _src;
            _fs.ReadExactly(_buf, 0, _src * f);
            int div = f * f;
            for (int x = 0; x < _dst; x++)
            {
                int sum = 0, x0 = x * f;
                for (int r = 0; r < f; r++)
                {
                    int o = r * _src + x0;
                    for (int c = 0; c < f; c++) sum += _buf[o + c];
                }
                row[x] = (byte)((sum + div / 2) / div);
            }
            return;
        }
        // ingrandimento: ripete i pixel
        int u = _dst / _src, sy = y / u;
        if (sy != _cachedRow)
        {
            _fs.Position = (long)sy * _src;
            _fs.ReadExactly(_buf, 0, _src);
            for (int x = 0; x < _dst; x++) _cache[x] = _buf[x / u];
            _cachedRow = sy;
        }
        Array.Copy(_cache, row, _dst);
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>Compone tre canali in una DDS DXT1 senza mipmap, come quelle di NewTerrain.</summary>
public static class DdsComposer
{
    public const int PreviewSize = 512;

    /// <summary>
    /// Scrive la DDS e restituisce un'anteprima RGB (PreviewSize², 3 byte per pixel) letta dai canali sorgente.
    /// </summary>
    public static byte[] ComposeDxt1(string outPath, int size, IChannelSource r, IChannelSource g, IChannelSource b,
                                     IProgress<double>? progress, CancellationToken ct)
    {
        if (size % 4 != 0) throw new ArgumentException("Size must be a multiple of 4");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        int blocksX = size / 4;
        var preview = new byte[PreviewSize * PreviewSize * 3];
        int pstep = Math.Max(1, size / PreviewSize);
        string tmp = outPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22))
        {
            fs.Write(Header(size, size));
            var rows = new byte[3][][];
            for (int c = 0; c < 3; c++) { rows[c] = new byte[4][]; for (int k = 0; k < 4; k++) rows[c][k] = new byte[size]; }
            var outRow = new byte[blocksX * 8];
            var srcs = new[] { r, g, b };
            for (int by = 0; by < size / 4; by++)
            {
                ct.ThrowIfCancellationRequested();
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 4; k++) srcs[c].ReadRow(by * 4 + k, rows[c][k]);

                Parallel.For(0, blocksX, bx =>
                {
                    Span<byte> px = stackalloc byte[48];
                    for (int k = 0; k < 4; k++)
                        for (int i = 0; i < 4; i++)
                        {
                            int x = bx * 4 + i, o = (k * 4 + i) * 3;
                            px[o] = rows[0][k][x]; px[o + 1] = rows[1][k][x]; px[o + 2] = rows[2][k][x];
                        }
                    Bc1Encoder.EncodeBlock(px, outRow.AsSpan(bx * 8, 8));
                });
                fs.Write(outRow);

                // anteprima: una riga ogni pstep
                for (int k = 0; k < 4; k++)
                {
                    int y = by * 4 + k;
                    if (y % pstep != 0 || y / pstep >= PreviewSize) continue;
                    int py = y / pstep;
                    for (int pxi = 0; pxi < PreviewSize && pxi * pstep < size; pxi++)
                    {
                        int o = (py * PreviewSize + pxi) * 3, x = pxi * pstep;
                        preview[o] = rows[0][k][x]; preview[o + 1] = rows[1][k][x]; preview[o + 2] = rows[2][k][x];
                    }
                }
                if ((by & 63) == 0) progress?.Report((double)by / (size / 4));
            }
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
        progress?.Report(1);
        return preview;
    }

    /// <summary>Intestazione DDS (128 byte) identica a quelle della Korea: DXT1, 1 livello, linear size.</summary>
    public static byte[] Header(int width, int height)
    {
        var h = new byte[128];
        void W(int off, uint v) => BitConverter.TryWriteBytes(h.AsSpan(off, 4), v);
        h[0] = (byte)'D'; h[1] = (byte)'D'; h[2] = (byte)'S'; h[3] = (byte)' ';
        W(4, 124);                       // dwSize
        W(8, 0x000A1007);                // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
        W(12, (uint)height);
        W(16, (uint)width);
        W(20, (uint)(Math.Max(1, width / 4) * Math.Max(1, height / 4) * 8)); // linear size
        W(24, 1);                        // depth (1, come nei file della Korea)
        W(28, 1);                        // mipmap count
        W(76, 32);                       // ddspf.dwSize
        W(80, 0x4);                      // DDPF_FOURCC
        h[84] = (byte)'D'; h[85] = (byte)'X'; h[86] = (byte)'T'; h[87] = (byte)'1';
        W(108, 0x1000);                  // DDSCAPS_TEXTURE
        return h;
    }

    /// <summary>Legge una DDS DXT1 e restituisce i tre canali decodificati a passo 'step' (per verifiche).</summary>
    public static (int Size, byte[] Rgb) DecodeDxt1(string path, int step)
    {
        using var fs = File.OpenRead(path);
        var hdr = new byte[128];
        fs.ReadExactly(hdr);
        int h = BitConverter.ToInt32(hdr, 12), w = BitConverter.ToInt32(hdr, 16);
        int ow = w / step, oh = h / step;
        var rgb = new byte[ow * oh * 3];
        var row = new byte[w / 4 * 8];
        Span<byte> px = stackalloc byte[48];
        for (int by = 0; by < h / 4; by++)
        {
            fs.ReadExactly(row);
            for (int bx = 0; bx < w / 4; bx++)
            {
                Bc1Encoder.DecodeBlock(row.AsSpan(bx * 8, 8), px);
                for (int k = 0; k < 4; k++)
                    for (int i = 0; i < 4; i++)
                    {
                        int y = by * 4 + k, x = bx * 4 + i;
                        if (y % step != 0 || x % step != 0) continue;
                        int o = ((y / step) * ow + x / step) * 3, s = (k * 4 + i) * 3;
                        rgb[o] = px[s]; rgb[o + 1] = px[s + 1]; rgb[o + 2] = px[s + 2];
                    }
            }
        }
        return (w, rgb);
    }
}
