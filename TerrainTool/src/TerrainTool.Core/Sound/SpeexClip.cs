using SpeexSharp;

namespace TerrainTool.Core.Sound;

/// <summary>
/// A Falcon.tlk clip to and from 8000 Hz mono 16-bit PCM, with Speex narrowband as BMS reads it (VoiceCodecSpeex::ReadFile).
/// Quality 8 gives the 38-byte frames of BMS's own clips. Uses SpeexSharp (MIT) and libspeex (BSD).
/// </summary>
public static class SpeexClip
{
    public const int SampleRate = 8000;
    public const int Quality = 8;

    public static short[] Decode(byte[] clip)
    {
        if (clip.Length < 8) return Array.Empty<short>();
        int pcmBytes = BitConverter.ToInt32(clip, 0), comp = BitConverter.ToInt32(clip, 4);
        using var dec = new SpeexDecoder(SpeexMode.Narrowband);
        var frame = new short[dec.FrameSize];
        var res = new List<short>(Math.Max(0, pcmBytes / 2));
        int p = 8, e = Math.Min(clip.Length, 8 + comp);
        while (p < e)
        {
            int len = clip[p++];
            if (len == 0 || p + len > e) break;
            dec.DecodeInt(clip.AsSpan(p, len), frame);
            res.AddRange(frame);
            p += len;
        }
        return res.Take(Math.Min(res.Count, pcmBytes / 2)).ToArray();
    }

    public static byte[] Encode(short[] pcm)
    {
        using var enc = new SpeexEncoder(SpeexMode.Narrowband) { Quality = Quality };
        int fs = enc.FrameSize;
        using var ms = new MemoryStream();
        var buf = new byte[256];
        var fr = new short[fs];
        for (int i = 0; i < pcm.Length; i += fs)
        {
            Array.Clear(fr);
            Array.Copy(pcm, i, fr, 0, Math.Min(fs, pcm.Length - i));
            enc.EncodeInt(fr);
            int n = enc.Write(buf);
            if (n <= 0 || n > 255) throw new InvalidOperationException($"Speex frame of {n} bytes");
            ms.WriteByte((byte)n); ms.Write(buf, 0, n);
        }
        var body = ms.ToArray();
        var clip = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(clip.AsSpan(0, 4), pcm.Length * 2);
        BitConverter.TryWriteBytes(clip.AsSpan(4, 4), body.Length);
        body.CopyTo(clip, 8);
        return clip;
    }
}
