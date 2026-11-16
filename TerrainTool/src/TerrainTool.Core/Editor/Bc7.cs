namespace TerrainTool.Core.Editor;

/// <summary>
/// Decodifica BC7 (texture dei modelli BML v2 PBR, per esempio *_Albedo.dds). Porting in C# del decodificatore di
/// texture2ddecoder (K0lb3, licenza MIT; il codice BC7 viene da bimg di Branimir Karadzic, licenza BSD-2).
/// </summary>
public static class Bc7
{
    private static readonly ushort[] P2 = { 0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000, 0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c, 0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a, 0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660, 0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22 };
    private static readonly byte[] A2 = { 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2, 15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15 };
    private static readonly byte[][] Factors =
    {
        new byte[] { 0, 21, 43, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 9, 18, 27, 37, 46, 55, 64, 0, 0, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 },
    };
    private static readonly uint[] P3 = { 0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050, 0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250, 0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500, 0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200, 0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50, 0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600, 0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000, 0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254 };
    private static readonly byte[][] A3 =
    {
        new byte[] { 3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15, 8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3 },
        new byte[] { 15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8, 15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8 },
    };
    //                                       subsets, partition, rotation, indexSel, color, alpha, endpointP, sharedP, index0, index1
    private static readonly byte[,] Modes =
    {
        { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 }, { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 }, { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 }, { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
        { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 }, { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 }, { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 }, { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
    };

    private static int Bits(ReadOnlySpan<byte> b, int pos, int n)
    {
        if (n == 0) return 0;
        int v = 0;
        for (int i = 0; i < n; i++, pos++) if (pos < 128 && (b[pos >> 3] >> (pos & 7) & 1) != 0) v |= 1 << i;
        return v;
    }

    private static byte Expand(int v, int bits) { v = (v << (8 - bits)) & 0xFF; return (byte)(v | v >> bits); }

    /// <summary>Un blocco di 16 byte → 16 pixel BGRA (64 byte) in dst, riga per riga.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pos = 0, mode = 0;
        while (mode < 8 && Bits(src, pos++, 1) == 0) mode++;
        if (mode == 8) { dst[..64].Clear(); return; }
        int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3], cb = Modes[mode, 4], ab = Modes[mode, 5];
        int epb = Modes[mode, 6], spb = Modes[mode, 7], ib0 = Modes[mode, 8], ib1 = Modes[mode, 9];
        int modeP = epb != 0 ? epb : spb;
        int part = Bits(src, pos, pb); pos += pb;
        int rot = Bits(src, pos, rb); pos += rb;
        int isel = Bits(src, pos, isb); pos += isb;
        Span<int> R = stackalloc int[6], G = stackalloc int[6], B = stackalloc int[6], A = stackalloc int[6];
        for (int i = 0; i < ns * 2; i++) { R[i] = Bits(src, pos, cb) << modeP; pos += cb; }
        for (int i = 0; i < ns * 2; i++) { G[i] = Bits(src, pos, cb) << modeP; pos += cb; }
        for (int i = 0; i < ns * 2; i++) { B[i] = Bits(src, pos, cb) << modeP; pos += cb; }
        if (ab != 0) for (int i = 0; i < ns * 2; i++) { A[i] = Bits(src, pos, ab) << modeP; pos += ab; }
        else A.Fill(0xFF);
        if (modeP != 0)
            for (int s = 0; s < ns; s++)
            {
                int pa = Bits(src, pos, modeP); pos += modeP;
                int pbb = spb == 0 ? Bits(src, pos, modeP) : pa;
                if (spb == 0) pos += modeP;
                R[s * 2] |= pa; R[s * 2 + 1] |= pbb; G[s * 2] |= pa; G[s * 2 + 1] |= pbb;
                B[s * 2] |= pa; B[s * 2 + 1] |= pbb; A[s * 2] |= pa; A[s * 2 + 1] |= pbb;
            }
        int colorBits = cb + modeP;
        for (int i = 0; i < ns * 2; i++) { R[i] = Expand(R[i], colorBits); G[i] = Expand(G[i], colorBits); B[i] = Expand(B[i], colorBits); }
        if (ab != 0) for (int i = 0; i < ns * 2; i++) A[i] = Expand(A[i], ab + modeP);
        bool has1 = ib1 != 0;
        byte[] f0 = Factors[ib0 - 2], f1 = has1 ? Factors[ib1 - 2] : f0;
        int off0 = 0, off1 = ns * (16 * ib0 - 1);
        for (int idx = 0; idx < 16; idx++)
        {
            int sub = 0, anchorIdx = 0;
            if (ns == 2) { sub = (P2[part] >> idx) & 1; anchorIdx = sub != 0 ? A2[part] : 0; }
            else if (ns == 3) { sub = (int)((P3[part] >> (2 * idx)) & 3); anchorIdx = sub != 0 ? A3[sub - 1][part] : 0; }
            int anchor = idx == anchorIdx ? 1 : 0;
            int n0 = ib0 - anchor, n1 = has1 ? ib1 - anchor : 0;
            int i0 = Bits(src, pos + off0, n0), i1 = has1 ? Bits(src, pos + off1, n1) : i0;
            off0 += n0; off1 += n1;
            int fc = isel == 0 ? f0[i0] : f1[i1];
            int fa = isel == 0 ? f1[i1] : f0[i0];
            if (!has1) { fc = f0[i0]; fa = f0[i0]; }
            int s2 = sub * 2;
            int r = (R[s2] * (64 - fc) + R[s2 + 1] * fc + 32) >> 6, g = (G[s2] * (64 - fc) + G[s2 + 1] * fc + 32) >> 6;
            int bl = (B[s2] * (64 - fc) + B[s2 + 1] * fc + 32) >> 6, a = (A[s2] * (64 - fa) + A[s2 + 1] * fa + 32) >> 6;
            switch (rot) { case 1: (a, r) = (r, a); break; case 2: (a, g) = (g, a); break; case 3: (a, bl) = (bl, a); break; }
            int o = idx * 4;
            dst[o] = (byte)bl; dst[o + 1] = (byte)g; dst[o + 2] = (byte)r; dst[o + 3] = (byte)a;
        }
    }
}
