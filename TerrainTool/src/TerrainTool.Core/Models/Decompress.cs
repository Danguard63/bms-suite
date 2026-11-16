namespace TerrainTool.Core.Models;

/// <summary>
/// Decompressori per i modelli BML di BMS: LZMA "grezzo" (5 byte di proprieta' senza la dimensione, come li scrive
/// l'esportatore ufficiale di BenchmarkSims) e LZ4 a frame. Solo lettura, nessuna dipendenza esterna.
/// </summary>
public static class Decompress
{
    // ───────────────────────── LZMA (porting del decodificatore di riferimento LzmaSpec di Igor Pavlov)

    public static byte[] Lzma(byte[] src, int offset, int length, int outSize)
    {
        int end = offset + length;
        int d = src[offset];
        if (d >= 9 * 5 * 5) throw new InvalidDataException("LZMA: bad properties");
        int lc = d % 9; d /= 9; int lp = d % 5; int pb = d / 5;
        var dec = new LzmaDecoder(src, offset + 5, end, outSize, lc, lp, pb);
        dec.Run();
        return dec.Out;
    }

    private sealed class LzmaDecoder
    {
        private const int kNumStates = 12, kNumPosBitsMax = 4, kEndPosModelIndex = 14, kNumFullDistances = 128, kNumAlignBits = 4, kNumLenToPosStates = 4;
        private readonly byte[] _in; private int _ip; private readonly int _iend;
        private uint _range, _code;
        public readonly byte[] Out; private int _op;
        private readonly int _lc, _lp, _pb;
        private readonly ushort[] _lit;
        private readonly ushort[] _posSlot = new ushort[kNumLenToPosStates << 6];
        private readonly ushort[] _posDec = new ushort[1 + kNumFullDistances - kEndPosModelIndex];
        private readonly ushort[] _align = new ushort[1 << kNumAlignBits];
        private readonly ushort[] _isMatch = new ushort[kNumStates << kNumPosBitsMax], _isRep = new ushort[kNumStates], _isRepG0 = new ushort[kNumStates],
            _isRepG1 = new ushort[kNumStates], _isRepG2 = new ushort[kNumStates], _isRep0Long = new ushort[kNumStates << kNumPosBitsMax];
        private readonly LenDecoder _len = new(), _repLen = new();

        public LzmaDecoder(byte[] src, int start, int end, int outSize, int lc, int lp, int pb)
        {
            _in = src; _ip = start; _iend = end; Out = new byte[outSize];
            _lc = lc; _lp = lp; _pb = pb;
            _lit = new ushort[0x300 << (lc + lp)];
            foreach (var a in new[] { _lit, _posSlot, _posDec, _align, _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long }) Array.Fill(a, (ushort)1024);
        }

        private byte Next() => _ip < _iend ? _in[_ip++] : (byte)0;

        private void Normalize()
        {
            if (_range < (1u << 24)) { _range <<= 8; _code = (_code << 8) | Next(); }
        }

        private int Bit(ushort[] p, int i)
        {
            uint v = p[i];
            uint bound = (_range >> 11) * v;
            int sym;
            if (_code < bound) { v += ((1u << 11) - v) >> 5; _range = bound; sym = 0; }
            else { v -= v >> 5; _code -= bound; _range -= bound; sym = 1; }
            p[i] = (ushort)v;
            Normalize();
            return sym;
        }

        private uint Direct(int numBits)
        {
            uint res = 0;
            do
            {
                _range >>= 1; _code -= _range;
                uint t = 0u - (_code >> 31);
                _code += _range & t;
                res = (res << 1) + (t + 1);
                Normalize();
            } while (--numBits > 0);
            return res;
        }

        private int Tree(ushort[] p, int off, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + Bit(p, off + m);
            return m - (1 << numBits);
        }

        private int Reverse(ushort[] p, int off, int numBits)
        {
            int m = 1, sym = 0;
            for (int i = 0; i < numBits; i++) { int b = Bit(p, off + m); m = (m << 1) + b; sym |= b << i; }
            return sym;
        }

        private sealed class LenDecoder
        {
            public readonly ushort[] Choice = { 1024, 1024 };
            public readonly ushort[] Low = Filled(16 << 3), Mid = Filled(16 << 3), High = Filled(256);
            private static ushort[] Filled(int n) { var a = new ushort[n]; Array.Fill(a, (ushort)1024); return a; }
        }

        private int DecodeLen(LenDecoder l, int posState)
        {
            if (Bit(l.Choice, 0) == 0) return Tree(l.Low, posState << 3, 3);
            if (Bit(l.Choice, 1) == 0) return 8 + Tree(l.Mid, posState << 3, 3);
            return 16 + Tree(l.High, 0, 8);
        }

        private uint DecodeDistance(int len)
        {
            int lenState = Math.Min(len, kNumLenToPosStates - 1);
            int posSlot = Tree(_posSlot, lenState << 6, 6);
            if (posSlot < 4) return (uint)posSlot;
            int numDirectBits = (posSlot >> 1) - 1;
            uint dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
            if (posSlot < kEndPosModelIndex) dist += (uint)Reverse(_posDec, (int)dist - posSlot, numDirectBits);
            else { dist += Direct(numDirectBits - kNumAlignBits) << kNumAlignBits; dist += (uint)Reverse(_align, 0, kNumAlignBits); }
            return dist;
        }

        public void Run()
        {
            if (Next() != 0) throw new InvalidDataException("LZMA: corrupted stream");
            _range = 0xFFFFFFFF; _code = 0;
            for (int i = 0; i < 4; i++) _code = (_code << 8) | Next();
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int state = 0;
            int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
            while (_op < Out.Length)
            {
                int posState = _op & pbMask;
                if (Bit(_isMatch, (state << kNumPosBitsMax) + posState) == 0)
                {
                    int prev = _op > 0 ? Out[_op - 1] : 0;
                    int litState = ((_op & lpMask) << _lc) + (prev >> (8 - _lc));
                    int baseI = 0x300 * litState;
                    int sym = 1;
                    if (state >= 7)
                    {
                        int matchByte = Out[_op - (int)rep0 - 1];
                        do
                        {
                            int matchBit = (matchByte >> 7) & 1; matchByte <<= 1;
                            int b = Bit(_lit, baseI + ((1 + matchBit) << 8) + sym);
                            sym = (sym << 1) | b;
                            if (matchBit != b) break;
                        } while (sym < 0x100);
                    }
                    while (sym < 0x100) sym = (sym << 1) | Bit(_lit, baseI + sym);
                    Out[_op++] = (byte)(sym - 0x100);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }
                int len;
                if (Bit(_isRep, state) != 0)
                {
                    if (_op == 0) throw new InvalidDataException("LZMA: corrupted stream");
                    if (Bit(_isRepG0, state) == 0)
                    {
                        if (Bit(_isRep0Long, (state << kNumPosBitsMax) + posState) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            Out[_op] = Out[_op - (int)rep0 - 1]; _op++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (Bit(_isRepG1, state) == 0) dist = rep1;
                        else
                        {
                            if (Bit(_isRepG2, state) == 0) dist = rep2;
                            else { dist = rep3; rep3 = rep2; }
                            rep2 = rep1;
                        }
                        rep1 = rep0; rep0 = dist;
                    }
                    len = DecodeLen(_repLen, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = DecodeLen(_len, posState);
                    state = state < 7 ? 7 : 10;
                    rep0 = DecodeDistance(len);
                    if (rep0 == 0xFFFFFFFF) break;   // marcatore di fine
                    if (rep0 >= _op) throw new InvalidDataException("LZMA: corrupted stream");
                }
                len += 2;
                int from = _op - (int)rep0 - 1;
                for (int i = 0; i < len && _op < Out.Length; i++) Out[_op++] = Out[from + i];
            }
        }
    }

    // ───────────────────────── LZ4 (formato a frame, come lz4.frame.compress)

    public static byte[] Lz4Frame(byte[] src, int offset, int length, int outSize)
    {
        var o = new byte[outSize];
        int op = 0, p = offset, end = offset + length;
        if (BitConverter.ToUInt32(src, p) != 0x184D2204) return Lz4Block(src, offset, length, outSize);
        p += 4;
        byte flg = src[p++]; p++;                       // FLG, BD
        bool blockChecksum = (flg & 0x10) != 0, contentSize = (flg & 0x08) != 0, dictId = (flg & 0x01) != 0;
        if (contentSize) p += 8;
        if (dictId) p += 4;
        p++;                                             // HC
        while (p + 4 <= end)
        {
            uint bs = BitConverter.ToUInt32(src, p); p += 4;
            if (bs == 0) break;
            bool raw = (bs & 0x80000000) != 0; int n = (int)(bs & 0x7FFFFFFF);
            if (raw) { int c = Math.Min(n, o.Length - op); Buffer.BlockCopy(src, p, o, op, c); op += c; }
            else op = Lz4BlockInto(src, p, n, o, op);
            p += n;
            if (blockChecksum) p += 4;
        }
        return o;
    }

    public static byte[] Lz4Block(byte[] src, int offset, int length, int outSize)
    {
        var o = new byte[outSize];
        Lz4BlockInto(src, offset, length, o, 0);
        return o;
    }

    private static int Lz4BlockInto(byte[] s, int p, int n, byte[] o, int op)
    {
        int end = p + n;
        while (p < end)
        {
            int token = s[p++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = s[p++]; lit += b; } while (b == 255); }
            int c = Math.Min(lit, o.Length - op);
            Buffer.BlockCopy(s, p, o, op, c); op += c; p += lit;
            if (p >= end) break;
            int off = s[p] | (s[p + 1] << 8); p += 2;
            int ml = token & 15;
            if (ml == 15) { int b; do { b = s[p++]; ml += b; } while (b == 255); }
            ml += 4;
            int from = op - off;
            for (int i = 0; i < ml && op < o.Length; i++) o[op++] = o[from + i];
        }
        return op;
    }
}
