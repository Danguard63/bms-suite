namespace TerrainTool.Core.Dds;

/// <summary>
/// Codificatore DXT1/BC1 (RGB, senza alfa) in C#, qualità "range fit" con raffinamento ai minimi quadrati.
/// Basta per mappe dati (profondità, maschere, blend) come quelle di NewTerrain.
/// </summary>
public static class Bc1Encoder
{
    /// <summary>Codifica un blocco 4×4: rgb = 16 pixel × 3 byte (riga per riga). Scrive 8 byte in dst.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> rgb, Span<byte> dst)
    {
        // Blocco uniforme: caso frequente nelle mappe dati, risultato esatto quando possibile
        bool uniform = true;
        for (int i = 3; i < 48 && uniform; i += 3)
            uniform = rgb[i] == rgb[0] && rgb[i + 1] == rgb[1] && rgb[i + 2] == rgb[2];
        if (uniform) { EncodeSolid(rgb[0], rgb[1], rgb[2], dst); return; }

        // Asse principale (covarianza) per scegliere gli estremi
        double mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++) { mr += rgb[3 * i]; mg += rgb[3 * i + 1]; mb += rgb[3 * i + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        double crr = 0, cgg = 0, cbb = 0, crg = 0, crb = 0, cgb = 0;
        for (int i = 0; i < 16; i++)
        {
            double r = rgb[3 * i] - mr, g = rgb[3 * i + 1] - mg, b = rgb[3 * i + 2] - mb;
            crr += r * r; cgg += g * g; cbb += b * b; crg += r * g; crb += r * b; cgb += g * b;
        }
        // power iteration
        double vr = 1, vg = 1, vb = 1;
        for (int it = 0; it < 6; it++)
        {
            double nr = crr * vr + crg * vg + crb * vb;
            double ng = crg * vr + cgg * vg + cgb * vb;
            double nb = crb * vr + cgb * vg + cbb * vb;
            double len = Math.Max(Math.Abs(nr), Math.Max(Math.Abs(ng), Math.Abs(nb)));
            if (len < 1e-9) { vr = 1; vg = 1; vb = 1; break; }
            vr = nr / len; vg = ng / len; vb = nb / len;
        }
        double minT = double.MaxValue, maxT = double.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double t = (rgb[3 * i] - mr) * vr + (rgb[3 * i + 1] - mg) * vg + (rgb[3 * i + 2] - mb) * vb;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        double vv = vr * vr + vg * vg + vb * vb;
        if (vv < 1e-12) vv = 1;
        double s0 = maxT / vv, s1 = minT / vv;
        double e0r = mr + vr * s0, e0g = mg + vg * s0, e0b = mb + vb * s0;
        double e1r = mr + vr * s1, e1g = mg + vg * s1, e1b = mb + vb * s1;

        Span<byte> idx = stackalloc byte[16];
        ushort c0 = Pack(e0r, e0g, e0b), c1 = Pack(e1r, e1g, e1b);
        // due passate: indici → estremi ottimi (minimi quadrati) → indici
        for (int pass = 0; pass < 2; pass++)
        {
            if (c0 == c1) break;
            if (c0 < c1) (c0, c1) = (c1, c0);
            AssignIndices(rgb, c0, c1, idx);
            if (pass == 1 || !LeastSquares(rgb, idx, out var a, out var b)) break;
            c0 = Pack(a.r, a.g, a.b); c1 = Pack(b.r, b.g, b.b);
        }
        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1)
        {
            // un solo colore a 16 bit: indice 0 ovunque
            WriteBlock(dst, c0, c1, stackalloc byte[16]);
            return;
        }
        AssignIndices(rgb, c0, c1, idx);
        WriteBlock(dst, c0, c1, idx);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ulong> SolidCache = new();

    private static void EncodeSolid(byte r, byte g, byte b, Span<byte> dst)
    {
        int key = (r << 16) | (g << 8) | b;
        if (!SolidCache.TryGetValue(key, out ulong packed))
        {
            Span<byte> tmp = stackalloc byte[8];
            EncodeSolidSlow(r, g, b, tmp);
            packed = BitConverter.ToUInt64(tmp);
            SolidCache[key] = packed;
        }
        BitConverter.TryWriteBytes(dst, packed);
    }

    private static void EncodeSolidSlow(byte r, byte g, byte b, Span<byte> dst)
    {
        // Cerca la coppia di estremi a 16 bit il cui colore 0, o il terzo/quarto della palette, riproduce meglio il colore
        ushort best0 = Pack(r, g, b), best1 = best0;
        int bestErr = Err(Unpack(best0), r, g, b);
        byte bestIdx = 0;
        if (bestErr > 0)
        {
            // prova i vicini per ogni canale con interpolazione 2/3–1/3
            int r5 = r >> 3, g6 = g >> 2, b5 = b >> 3;
            for (int dr = -1; dr <= 1; dr++)
            for (int dg = -1; dg <= 1; dg++)
            for (int db = -1; db <= 1; db++)
            for (int er = -1; er <= 1; er++)
            for (int eg = -1; eg <= 1; eg++)
            for (int eb = -1; eb <= 1; eb++)
            {
                int ar = Math.Clamp(r5 + dr, 0, 31), ag = Math.Clamp(g6 + dg, 0, 63), ab = Math.Clamp(b5 + db, 0, 31);
                int br = Math.Clamp(r5 + er, 0, 31), bg = Math.Clamp(g6 + eg, 0, 63), bb = Math.Clamp(b5 + eb, 0, 31);
                ushort ca = (ushort)((ar << 11) | (ag << 5) | ab), cb = (ushort)((br << 11) | (bg << 5) | bb);
                if (ca == cb) continue;
                if (ca < cb) (ca, cb) = (cb, ca);
                var pa = Unpack(ca); var pb = Unpack(cb);
                var p2 = ((2 * pa.r + pb.r) / 3, (2 * pa.g + pb.g) / 3, (2 * pa.b + pb.b) / 3);
                int e = Err(p2, r, g, b);
                if (e < bestErr) { bestErr = e; best0 = ca; best1 = cb; bestIdx = 2; }
                if (bestErr == 0) goto done;
            }
        }
    done:
        Span<byte> idx = stackalloc byte[16];
        idx.Fill(bestIdx);
        if (best0 == best1) idx.Clear();
        WriteBlock(dst, best0, best1, idx);
    }

    private static int Err((int r, int g, int b) p, int r, int g, int b) =>
        (p.r - r) * (p.r - r) + (p.g - g) * (p.g - g) + (p.b - b) * (p.b - b);

    private static void AssignIndices(ReadOnlySpan<byte> rgb, ushort c0, ushort c1, Span<byte> idx)
    {
        var p0 = Unpack(c0); var p1 = Unpack(c1);
        Span<int> pal = stackalloc int[12];
        pal[0] = p0.r; pal[1] = p0.g; pal[2] = p0.b;
        pal[3] = p1.r; pal[4] = p1.g; pal[5] = p1.b;
        pal[6] = (2 * p0.r + p1.r) / 3; pal[7] = (2 * p0.g + p1.g) / 3; pal[8] = (2 * p0.b + p1.b) / 3;
        pal[9] = (p0.r + 2 * p1.r) / 3; pal[10] = (p0.g + 2 * p1.g) / 3; pal[11] = (p0.b + 2 * p1.b) / 3;
        for (int i = 0; i < 16; i++)
        {
            int r = rgb[3 * i], g = rgb[3 * i + 1], b = rgb[3 * i + 2];
            int best = 0, bestE = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = r - pal[3 * k], dg = g - pal[3 * k + 1], db = b - pal[3 * k + 2];
                int e = dr * dr + dg * dg + db * db;
                if (e < bestE) { bestE = e; best = k; }
            }
            idx[i] = (byte)best;
        }
    }

    /// <summary>Estremi ottimi dati gli indici (pesi 1, 0, 2/3, 1/3).</summary>
    private static bool LeastSquares(ReadOnlySpan<byte> rgb, ReadOnlySpan<byte> idx,
        out (double r, double g, double b) a, out (double r, double g, double b) b)
    {
        double aa = 0, bb = 0, ab = 0;
        double axr = 0, axg = 0, axb = 0, bxr = 0, bxg = 0, bxb = 0;
        for (int i = 0; i < 16; i++)
        {
            double w = idx[i] switch { 0 => 1.0, 1 => 0.0, 2 => 2.0 / 3, _ => 1.0 / 3 };
            double u = 1 - w;
            aa += w * w; bb += u * u; ab += w * u;
            axr += w * rgb[3 * i]; axg += w * rgb[3 * i + 1]; axb += w * rgb[3 * i + 2];
            bxr += u * rgb[3 * i]; bxg += u * rgb[3 * i + 1]; bxb += u * rgb[3 * i + 2];
        }
        double det = aa * bb - ab * ab;
        if (Math.Abs(det) < 1e-9) { a = b = default; return false; }
        double f = 1 / det;
        a = ((axr * bb - bxr * ab) * f, (axg * bb - bxg * ab) * f, (axb * bb - bxb * ab) * f);
        b = ((bxr * aa - axr * ab) * f, (bxg * aa - axg * ab) * f, (bxb * aa - axb * ab) * f);
        return true;
    }

    private static void WriteBlock(Span<byte> dst, ushort c0, ushort c1, ReadOnlySpan<byte> idx)
    {
        dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
        uint bits = 0;
        for (int i = 0; i < 16; i++) bits |= (uint)(idx[i] & 3) << (2 * i);
        dst[4] = (byte)bits; dst[5] = (byte)(bits >> 8); dst[6] = (byte)(bits >> 16); dst[7] = (byte)(bits >> 24);
    }

    public static ushort Pack(double r, double g, double b)
    {
        int r5 = (int)Math.Round(Math.Clamp(r, 0, 255) * 31 / 255.0);
        int g6 = (int)Math.Round(Math.Clamp(g, 0, 255) * 63 / 255.0);
        int b5 = (int)Math.Round(Math.Clamp(b, 0, 255) * 31 / 255.0);
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    public static (int r, int g, int b) Unpack(ushort c)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    /// <summary>Decodifica un blocco (per test e anteprime): 16 pixel × 3 byte.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> rgb)
    {
        ushort c0 = (ushort)(src[0] | src[1] << 8), c1 = (ushort)(src[2] | src[3] << 8);
        var p0 = Unpack(c0); var p1 = Unpack(c1);
        Span<int> pal = stackalloc int[12];
        pal[0] = p0.r; pal[1] = p0.g; pal[2] = p0.b; pal[3] = p1.r; pal[4] = p1.g; pal[5] = p1.b;
        if (c0 > c1)
        {
            pal[6] = (2 * p0.r + p1.r) / 3; pal[7] = (2 * p0.g + p1.g) / 3; pal[8] = (2 * p0.b + p1.b) / 3;
            pal[9] = (p0.r + 2 * p1.r) / 3; pal[10] = (p0.g + 2 * p1.g) / 3; pal[11] = (p0.b + 2 * p1.b) / 3;
        }
        else
        {
            pal[6] = (p0.r + p1.r) / 2; pal[7] = (p0.g + p1.g) / 2; pal[8] = (p0.b + p1.b) / 2;
        }
        uint bits = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)((bits >> (2 * i)) & 3);
            rgb[3 * i] = (byte)pal[3 * k]; rgb[3 * i + 1] = (byte)pal[3 * k + 1]; rgb[3 * i + 2] = (byte)pal[3 * k + 2];
        }
    }
}
