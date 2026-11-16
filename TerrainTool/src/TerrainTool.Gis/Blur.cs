namespace TerrainTool.Gis;

/// <summary>Sfocatura gaussiana approssimata con 3 passate di box blur separabile (immagine quadrata 8 bit).</summary>
public static class Blur
{
    public static void Gaussian(byte[] img, int size, int radius, CancellationToken ct)
    {
        if (radius <= 0) return;
        var tmp = new byte[img.LongLength];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxH(img, tmp, size, radius, ct);
            BoxV(tmp, img, size, radius, ct);
        }
    }

    private static void BoxH(byte[] src, byte[] dst, int size, int r, CancellationToken ct)
    {
        Parallel.For(0, size, new ParallelOptions { CancellationToken = ct }, y =>
        {
            long row = (long)y * size;
            int sum = 0, w = 2 * r + 1;
            for (int x = -r; x <= r; x++) sum += src[row + Math.Clamp(x, 0, size - 1)];
            for (int x = 0; x < size; x++)
            {
                dst[row + x] = (byte)((sum + w / 2) / w);
                sum += src[row + Math.Min(x + r + 1, size - 1)] - src[row + Math.Max(x - r, 0)];
            }
        });
    }

    private static void BoxV(byte[] src, byte[] dst, int size, int r, CancellationToken ct)
    {
        const int strip = 64; // colonne per blocco, per usare bene la cache
        int strips = (size + strip - 1) / strip;
        Parallel.For(0, strips, new ParallelOptions { CancellationToken = ct }, s =>
        {
            int x0 = s * strip, x1 = Math.Min(size, x0 + strip), n = x1 - x0, w = 2 * r + 1;
            var sum = new int[n];
            for (int y = -r; y <= r; y++)
            {
                long row = (long)Math.Clamp(y, 0, size - 1) * size;
                for (int i = 0; i < n; i++) sum[i] += src[row + x0 + i];
            }
            for (int y = 0; y < size; y++)
            {
                long row = (long)y * size;
                long add = (long)Math.Min(y + r + 1, size - 1) * size, sub = (long)Math.Max(y - r, 0) * size;
                for (int i = 0; i < n; i++)
                {
                    dst[row + x0 + i] = (byte)((sum[i] + w / 2) / w);
                    sum[i] += src[add + x0 + i] - src[sub + x0 + i];
                }
            }
        });
    }
}
