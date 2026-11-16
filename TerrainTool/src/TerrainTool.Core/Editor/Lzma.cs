namespace TerrainTool.Core.Editor;

/// <summary>
/// Decompressore LZMA (flusso "raw" LZMA1 con 5 byte di proprieta': lc/lp/pb e dimensione del dizionario), come nei
/// modelli .bml di BMS. Scritto secondo l'algoritmo dell'LZMA SDK di Igor Pavlov (pubblico dominio).
/// </summary>
public static class Lzma
{
    private const int NumStates = 12, PosBitsMax = 4, LenToPosStates = 4, AlignBits = 4;
    private const int StartPosModel = 4, EndPosModel = 14, FullDistances = 128, MatchMinLen = 2;

    private sealed class Range
    {
        private readonly byte[] _s; private int _p; public uint Rng = 0xFFFFFFFF, Code;
        public Range(byte[] s, int p) { _s = s; _p = p; for (int i = 0; i < 5; i++) Code = Code << 8 | Next(); }
        private byte Next() => _p < _s.Length ? _s[_p++] : (byte)0;
        public int Bit(ushort[] probs, int i)
        {
            uint bound = (Rng >> 11) * probs[i];
            int bit;
            if (Code < bound) { Rng = bound; probs[i] += (ushort)((2048 - probs[i]) >> 5); bit = 0; }
            else { Rng -= bound; Code -= bound; probs[i] -= (ushort)(probs[i] >> 5); bit = 1; }
            if (Rng < 1u << 24) { Rng <<= 8; Code = Code << 8 | Next(); }
            return bit;
        }
        public uint Direct(int n)
        {
            uint r = 0;
            for (int i = 0; i < n; i++)
            {
                Rng >>= 1;
                uint t = (Code - Rng) >> 31;   // 1 se Code < Rng
                Code -= Rng & (t - 1);
                r = r << 1 | (1 - t);
                if (Rng < 1u << 24) { Rng <<= 8; Code = Code << 8 | Next(); }
            }
            return r;
        }
        public int Tree(ushort[] p, int off, int bits)
        {
            int m = 1;
            for (int i = 0; i < bits; i++) m = (m << 1) + Bit(p, off + m);
            return m - (1 << bits);
        }
        public int Reverse(ushort[] p, int off, int bits)
        {
            int m = 1, s = 0;
            for (int i = 0; i < bits; i++) { int b = Bit(p, off + m); m = (m << 1) + b; s |= b << i; }
            return s;
        }
    }

    private sealed class LenDec
    {
        private readonly ushort[] _c = Probs(2), _low = Probs(PosBitsMax << 4 << 3), _mid = Probs(PosBitsMax << 4 << 3), _high = Probs(256);
        public int Decode(Range r, int posState)
        {
            if (r.Bit(_c, 0) == 0) return r.Tree(_low, posState << 3, 3);
            if (r.Bit(_c, 1) == 0) return 8 + r.Tree(_mid, posState << 3, 3);
            return 16 + r.Tree(_high, 0, 8);
        }
    }

    private static ushort[] Probs(int n) { var a = new ushort[n]; Array.Fill(a, (ushort)1024); return a; }

    /// <param name="props">5 byte: (pb*5+lp)*9+lc, dimensione del dizionario.</param>
    public static byte[] Decode(byte[] src, int propsAt, int dataAt, long outSize)
    {
        int d = src[propsAt];
        if (d >= 9 * 5 * 5) throw new InvalidDataException("bad LZMA properties");
        int lc = d % 9, lp = d / 9 % 5, pb = d / 45;
        var outp = new byte[outSize];
        var r = new Range(src, dataAt);
        var isMatch = Probs(NumStates << PosBitsMax); var isRep = Probs(NumStates); var isRepG0 = Probs(NumStates);
        var isRepG1 = Probs(NumStates); var isRepG2 = Probs(NumStates); var isRep0Long = Probs(NumStates << PosBitsMax);
        var posSlot = Probs(LenToPosStates << 6); var posDec = Probs(FullDistances - EndPosModel + 1); var align = Probs(1 << AlignBits);
        var lit = Probs(0x300 << (lc + lp));
        var len = new LenDec(); var repLen = new LenDec();
        int state = 0; uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        long pos = 0; byte prev = 0;
        int pbMask = (1 << pb) - 1, lpMask = (1 << lp) - 1;
        while (pos < outSize)
        {
            int posState = (int)(pos & pbMask);
            if (r.Bit(isMatch, (state << PosBitsMax) + posState) == 0)
            {
                int bse = 0x300 * ((int)((pos & lpMask) << lc) + (prev >> (8 - lc)));
                int sym = 1;
                if (state >= 7)
                {
                    int mb = outp[pos - rep0 - 1];
                    while (sym < 0x100)
                    {
                        int matchBit = (mb >> 7) & 1; mb <<= 1;
                        int bit = r.Bit(lit, bse + ((1 + matchBit) << 8) + sym);
                        sym = sym << 1 | bit;
                        if (matchBit != bit) { while (sym < 0x100) sym = sym << 1 | r.Bit(lit, bse + sym); break; }
                    }
                }
                else while (sym < 0x100) sym = sym << 1 | r.Bit(lit, bse + sym);
                prev = (byte)sym; outp[pos++] = prev;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }
            int l;
            if (r.Bit(isRep, state) == 1)
            {
                if (pos == 0) throw new InvalidDataException("LZMA: rep at start");
                if (r.Bit(isRepG0, state) == 0)
                {
                    if (r.Bit(isRep0Long, (state << PosBitsMax) + posState) == 0)
                    {
                        state = state < 7 ? 9 : 11;
                        prev = outp[pos - rep0 - 1]; outp[pos++] = prev;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (r.Bit(isRepG1, state) == 0) dist = rep1;
                    else
                    {
                        if (r.Bit(isRepG2, state) == 0) dist = rep2;
                        else { dist = rep3; rep3 = rep2; }
                        rep2 = rep1;
                    }
                    rep1 = rep0; rep0 = dist;
                }
                l = repLen.Decode(r, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2; rep2 = rep1; rep1 = rep0;
                l = len.Decode(r, posState);
                state = state < 7 ? 7 : 10;
                int slot = r.Tree(posSlot, Math.Min(l, LenToPosStates - 1) << 6, 6);
                if (slot >= StartPosModel)
                {
                    int nd = (slot >> 1) - 1;
                    rep0 = (uint)((2 | (slot & 1)) << nd);
                    if (slot < EndPosModel) rep0 += (uint)r.Reverse(posDec, (int)rep0 - slot - 1, nd);
                    else { rep0 += r.Direct(nd - AlignBits) << AlignBits; rep0 += (uint)r.Reverse(align, 0, AlignBits); }
                }
                else rep0 = (uint)slot;
                if (rep0 == 0xFFFFFFFF) break;   // marcatore di fine
                if (rep0 >= pos) throw new InvalidDataException("LZMA: distance beyond output");
            }
            l += MatchMinLen;
            for (int i = 0; i < l && pos < outSize; i++) { prev = outp[pos - rep0 - 1]; outp[pos++] = prev; }
        }
        return outp;
    }
}
