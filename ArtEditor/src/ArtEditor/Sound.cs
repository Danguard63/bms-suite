using System.IO;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>
/// Ascolto dei suoni dell'interfaccia di BMS (.ogg Vorbis): il file si decodifica (NVorbis) in un .wav temporaneo che Windows sa
/// suonare. Serve solo per ascoltare: BMS usa gli .ogg cosi' come sono.
/// </summary>
public static class Sound
{
    private static MediaPlayer? _player;

    /// <summary>Durata in secondi e canali; eccezione se il file non e' un Ogg Vorbis.</summary>
    public static (double Seconds, int Channels, int Rate) Info(string ogg)
    {
        using var r = new NVorbis.VorbisReader(ogg);
        return (r.TotalTime.TotalSeconds, r.Channels, r.SampleRate);
    }

    public static void Play(string file)
    {
        string wav = file;
        if (Path.GetExtension(file).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            wav = Path.Combine(Path.GetTempPath(), "BmsArtEditor_" + Path.GetFileNameWithoutExtension(file) + "_" + Math.Abs(file.GetHashCode()) + ".wav");
            if (!File.Exists(wav) || File.GetLastWriteTimeUtc(wav) < File.GetLastWriteTimeUtc(file)) ToWav(file, wav);
        }
        _player?.Stop();
        _player ??= new MediaPlayer();
        _player.Open(new Uri(wav));
        _player.Play();
    }

    private static void ToWav(string ogg, string wav)
    {
        using var r = new NVorbis.VorbisReader(ogg);
        int ch = r.Channels, rate = r.SampleRate;
        var pcm = new MemoryStream();
        var buf = new float[rate * ch];
        int n;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp((int)(buf[i] * 32767f), short.MinValue, short.MaxValue);
                pcm.WriteByte((byte)v); pcm.WriteByte((byte)(v >> 8));
            }
        using var f = new BinaryWriter(File.Create(wav));
        int len = (int)pcm.Length;
        f.Write("RIFF"u8.ToArray()); f.Write(36 + len); f.Write("WAVE"u8.ToArray());
        f.Write("fmt "u8.ToArray()); f.Write(16); f.Write((short)1); f.Write((short)ch); f.Write(rate); f.Write(rate * ch * 2); f.Write((short)(ch * 2)); f.Write((short)16);
        f.Write("data"u8.ToArray()); f.Write(len); f.Write(pcm.ToArray());
    }
}
