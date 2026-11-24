namespace TerrainTool.Core.Sound;

/// <summary>
/// Falcon.tlk, the radio voice clips of BMS (Sounds folder of the theater, sounddir of its .tdf). Read in BMS 4.38 by
/// VoiceManager::VMBegin and VoiceCodecSpeex::ReadFile:
/// three u32 (2664, 6580, 0 in Korea and Israel), then one u32 file offset per clip (the clip number is the tlkId of
/// fragfile.xml), then the clips. A clip is u32 bytes of PCM, u32 bytes of compressed data, then frames
/// [u8 length][Speex narrowband frame] (8000 Hz mono 16 bit; BMS's own clips are all 38-byte frames).
/// Checked: rebuilding Korea's Falcon.tlk from its clips gives the same bytes.
/// </summary>
public sealed class TlkFile
{
    public uint[] Header = { 2664, 6580, 0 };
    public List<byte[]> Clips { get; } = new();

    public static TlkFile Load(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 16) throw new InvalidDataException($"{path}: not a Falcon.tlk");
        var t = new TlkFile { Header = new[] { BitConverter.ToUInt32(d, 0), BitConverter.ToUInt32(d, 4), BitConverter.ToUInt32(d, 8) } };
        uint first = BitConverter.ToUInt32(d, 12);
        if (first < 16 || first > d.Length || (first - 12) % 4 != 0) throw new InvalidDataException($"{path}: offset table not valid");
        int n = (int)((first - 12) / 4);
        var offs = new uint[n + 1];
        for (int i = 0; i < n; i++) offs[i] = BitConverter.ToUInt32(d, 12 + 4 * i);
        offs[n] = (uint)d.Length;
        for (int i = 0; i < n; i++)
        {
            if (offs[i + 1] < offs[i] || offs[i + 1] > d.Length) throw new InvalidDataException($"{path}: clip {i} out of the file");
            t.Clips.Add(d.AsSpan((int)offs[i], (int)(offs[i + 1] - offs[i])).ToArray());
        }
        return t;
    }

    /// <summary>Adds a clip at the end; returns its number (tlkId).</summary>
    public int Add(byte[] clip) { Clips.Add(clip); return Clips.Count - 1; }

    public void Save(string path)
    {
        long tableEnd = 12 + 4L * Clips.Count;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        foreach (var h in Header) bw.Write(h);
        long pos = tableEnd;
        foreach (var c in Clips)
        {
            if (pos > uint.MaxValue) throw new InvalidOperationException("Falcon.tlk would be larger than 4 GB");
            bw.Write((uint)pos); pos += c.Length;
        }
        foreach (var c in Clips) bw.Write(c);
    }
}
