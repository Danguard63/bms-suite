using System.Text;

namespace TerrainTool.Core.Sound;

/// <summary>
/// Makes a synthesized (or recorded) name sound like a radio call next to BMS's ATC clips: silence trimmed, band-pass
/// 300-3400 Hz, soft compression, the level of BMS's ATC voices, a little hiss. Input and output are 8000 Hz mono 16 bit.
/// </summary>
public static class RadioEffect
{
    /// <summary>Level (RMS of the voiced part) of BMS's ATC voice 12 clips "Osan", "Osan Approach", "Osan Tower" (measured: about 8000).</summary>
    public const double BmsAtcLevel = 8000;

    public static double Level(short[] s)
    {
        double e = 0; int n = 0;
        foreach (var x in s) if (Math.Abs((int)x) > 300) { e += (double)x * x; n++; }
        return n == 0 ? 0 : Math.Sqrt(e / n);
    }

    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;
        public static Biquad HighPass(double f) { var w = 2 * Math.PI * f / 8000; var al = Math.Sin(w) / (2 * 0.707); var c = Math.Cos(w); var a0 = 1 + al; return new Biquad { _b0 = (1 + c) / 2 / a0, _b1 = -(1 + c) / a0, _b2 = (1 + c) / 2 / a0, _a1 = -2 * c / a0, _a2 = (1 - al) / a0 }; }
        public static Biquad LowPass(double f) { var w = 2 * Math.PI * f / 8000; var al = Math.Sin(w) / (2 * 0.707); var c = Math.Cos(w); var a0 = 1 + al; return new Biquad { _b0 = (1 - c) / 2 / a0, _b1 = (1 - c) / a0, _b2 = (1 - c) / 2 / a0, _a1 = -2 * c / a0, _a2 = (1 - al) / a0 }; }
        public double Run(double x) { var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2; _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y; return y; }
    }

    public static short[] Apply(short[] input, double level = BmsAtcLevel, double hiss = 0.04)
    {
        int first = Array.FindIndex(input, x => Math.Abs((int)x) > 500), last = Array.FindLastIndex(input, x => Math.Abs((int)x) > 500);
        if (first < 0) return input;
        first = Math.Max(0, first - 320); last = Math.Min(input.Length - 1, last + 320);   // 40 ms before and after the voice
        var x = new double[last - first + 1];
        for (int i = 0; i < x.Length; i++) x[i] = input[first + i];
        var f = new[] { Biquad.HighPass(300), Biquad.HighPass(300), Biquad.LowPass(3400), Biquad.LowPass(3400) };
        for (int i = 0; i < x.Length; i++) { double v = x[i]; foreach (var q in f) v = q.Run(v); x[i] = v; }
        double peak = x.Max(Math.Abs);
        if (peak <= 0) return input;
        for (int i = 0; i < x.Length; i++) x[i] = Math.Tanh(2.0 * x[i] / peak);
        double e = 0; int n = 0; foreach (var v in x) if (Math.Abs(v) > 0.03) { e += v * v; n++; }
        double g = level / Math.Sqrt(e / Math.Max(1, n));
        var rnd = new Random(1);
        var o = new short[x.Length];
        for (int i = 0; i < x.Length; i++)
            o[i] = (short)Math.Clamp(Math.Round(x[i] * g + (rnd.NextDouble() - 0.5) * level * hiss), short.MinValue, short.MaxValue);
        return o;
    }

    /// <summary>The samples of a PCM 16-bit mono WAV (any rate is accepted, resampled linearly to 8000 Hz).</summary>
    public static short[] ReadWav(byte[] b)
    {
        int rate = 8000, ch = 1, bits = 16, p = 12;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4); int len = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22); }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException("only 16-bit PCM WAV files are supported");
                int frames = Math.Min(len, b.Length - p - 8) / (2 * ch);
                var s = new double[frames];
                for (int i = 0; i < frames; i++) { double sum = 0; for (int c = 0; c < ch; c++) sum += BitConverter.ToInt16(b, p + 8 + (i * ch + c) * 2); s[i] = sum / ch; }
                if (rate == 8000) return s.Select(v => (short)v).ToArray();
                int outN = (int)((long)frames * 8000 / rate);
                var o = new short[outN];
                for (int i = 0; i < outN; i++) { double t = (double)i * rate / 8000; int k = (int)t; double fr = t - k; double a = s[Math.Min(k, frames - 1)], c2 = s[Math.Min(k + 1, frames - 1)]; o[i] = (short)Math.Round(a + (c2 - a) * fr); }
                return o;
            }
            p += 8 + len + (len & 1);
        }
        throw new InvalidDataException("not a WAV file (no data chunk)");
    }

    public static byte[] WriteWav(short[] s, int rate = 8000)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + s.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(s.Length * 2);
        foreach (var x in s) w.Write(x);
        w.Flush();
        return ms.ToArray();
    }
}
