using System.Buffers.Binary;
using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Lettura del database compilato (FALCON4.CT/UCD/VCD/WCD/WLD/SSD) per i teatri che non hanno le tabelle XML (es. Korea TvT).
/// Ogni file: i16 numero di record, poi record di lunghezza fissa. Gli offset dei campi sono ricavati confrontando i FALCON4.*
/// di BMS 4.38 con le Falcon4_*.xml da cui BMS li compila (stessi valori su tutti i record); CoreTest lo ricontrolla.
/// </summary>
public sealed partial class CampaignDb
{
    public const int CtSize = 81, UcdSize = 336, VcdSize = 160, WcdSize = 64, WldSize = 208, SsdSize = 1003;

    /// <summary>True se il database e' stato letto dai FALCON4.* compilati.</summary>
    public bool Compiled { get; private set; }

    private static readonly EnumerationOptions NoCase = new() { MatchCasing = MatchCasing.CaseInsensitive };
    private static string? FileIn(string dir, string name) => Directory.EnumerateFiles(dir, name, NoCase).FirstOrDefault();
    private static bool HasXmlCt(string dir) => FileIn(dir, "Falcon4_CT.xml") != null;
    private static string? CompiledCt(string dir) => FileIn(dir, "FALCON4.CT");
    private static bool HasDatabase(string dir) => HasXmlCt(dir) || CompiledCt(dir) != null;

    /// <summary>La cartella ha il database solo compilato.</summary>
    public static bool IsCompiledOnly(string dir) => Directory.Exists(dir) && !HasXmlCt(dir) && CompiledCt(dir) != null;

    private static IEnumerable<(int Num, byte[] Rec)> Table(string? path, int size)
    {
        if (path == null) yield break;
        var b = File.ReadAllBytes(path);
        if (b.Length < 2) yield break;
        int n = BinaryPrimitives.ReadInt16LittleEndian(b);
        if (n < 0 || 2 + (long)n * size != b.Length)
            throw new InvalidDataException($"{Path.GetFileName(path)}: {n} records of {size} bytes expected, file is {b.Length} bytes");
        for (int i = 0; i < n; i++) yield return (i, b.AsSpan(2 + i * size, size).ToArray());
    }

    /// <summary>Le Falcon4_*.xml di BMS non scrivono i campi a 0 di queste liste e la lettura XML li vede come -1: qui lo stesso.</summary>
    private static int Absent(int v) => v == 0 ? -1 : v;

    private static short I16(byte[] r, int o) => BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(o));
    private static int I32(byte[] r, int o) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(o));
    private static string Name(byte[] r, int o, int len)
    {
        int end = Array.IndexOf(r, (byte)0, o, len);
        return Encoding.Latin1.GetString(r, o, (end < 0 ? o + len : end) - o).Trim();
    }

    private void LoadCompiled(string dir, string ctPath)
    {
        Compiled = true;
        CtPath = ctPath;
        foreach (var (n, r) in Table(ctPath, CtSize))
            Ct[n] = new CtEntry((sbyte)r[8], (sbyte)r[9], r[10], r[11], r[12], (sbyte)r[76], I16(r, 77), I16(r, 60));

        foreach (var (n, r) in Table(FileIn(dir, "FALCON4.UCD"), UcdSize))
        {
            string name = Name(r, 230, 20);
            var veh = Enumerable.Range(0, 16).Select(i => Absent(I16(r, 68 + 2 * i))).ToArray();
            _ucd[n] = (name, veh[0]);
            Ucd[n] = new UcdEntry(name, veh, r[284], r[306], r[298],
                Enumerable.Range(0, 16).Select(i => Math.Max(0, I32(r, 4 + 4 * i))).ToArray(),
                I32(r, 260), I16(r, 330), I16(r, 256), Enumerable.Range(0, 16).Select(i => Math.Max(0, (int)(sbyte)r[268 + i])).ToArray(),
                I16(r, 332), (sbyte)r[305]);
        }

        foreach (var (n, r) in Table(FileIn(dir, "FALCON4.VCD"), VcdSize))
        {
            _vcd[n] = Name(r, 8, 15);   // char Name[15]: BMS compila tagliando i nomi piu' lunghi (es. "SA-20A (S-300 P")
            bool armed = Enumerable.Range(0, 16).Any(i => I16(r, 98 + 2 * i) > 0 && r[130 + i] > 0);
            VcdGun[n] = (Absent(I16(r, 98)), Absent(r[130]));
            VcdHardpoints[n] = Enumerable.Range(0, 16).Select(i => i == 0 ? 0 : Math.Max(0, (int)I16(r, 98 + 2 * i))).ToArray();
            VcdWeights[n] = (I32(r, 32), I32(r, 36), I32(r, 40));
            VcdInfo[n] = new VcdStats(r[75], r[77], r[79], r[87], I16(r, 56), r[95], armed, I16(r, 54));
        }

        foreach (var (n, r) in Table(FileIn(dir, "FALCON4.SSD"), SsdSize))
        {
            var st = new Dictionary<int, int>();
            for (int i = 0; i < 1000; i++) if (r[i] != 0) st[i] = r[i];
            Ssd[n] = st;
        }

        foreach (var (n, r) in Table(FileIn(dir, "FALCON4.WCD"), WcdSize))
        {
            Wcd[n] = Name(r, 14, 20);
            WcdWeight[n] = I16(r, 50);
        }

        foreach (var (n, r) in Table(FileIn(dir, "FALCON4.WLD"), WldSize))
        {
            var w = new List<(int, int)>();
            for (int i = 0; i < 64; i++)
            {
                int wi = I16(r, 16 + 2 * i);
                if (wi > 0) w.Add((wi, Math.Max(1, (int)(sbyte)r[144 + i])));
            }
            Wld[n] = (Name(r, 0, 16), w);
        }
    }
}
