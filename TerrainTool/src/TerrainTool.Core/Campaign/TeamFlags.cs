using TerrainTool.Core.Dds;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Bandiere delle squadre come le disegna BMS: immagini OOB_FLAGS_*_HRZ (53x31) in Art\resource\Main.rsc del teatro.
/// Il numero "Flag" della squadra (file .tea) sceglie il posto; i nomi dei posti sono quelli della Korea, ma ogni teatro
/// mette le sue bandiere al loro posto (Mideast: CHINA = Iran, CIS = Arabia Saudita, JAPAN = Kuwait, NATO = EAU, NKOREA = Iraq, SKOREA = ONU).
/// Corrispondenza numero → posto verificata sulle squadre di Korea (save0.cam) e Mideast; 0 = nessuna bandiera.
/// </summary>
public static class TeamFlags
{
    public static readonly string?[] Slots = { null, "USA", "SKOREA", "JAPAN", "CIS", "CHINA", "NKOREA", "NATO" };

    public sealed record FlagImage(int Width, int Height, byte[] Bgra);

    /// <summary>Main.idx da provare, nell'ordine: Art del teatro, poi quella di BMS.</summary>
    public static IEnumerable<string> CandidateIndexes(IEnumerable<string> artDirs)
    {
        foreach (var a in artDirs)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            foreach (var sub in new[] { "resource", Path.Combine("Art", "resource") })
            {
                string dir = Path.Combine(a, sub);
                if (!Directory.Exists(dir)) continue;
                var f = Directory.EnumerateFiles(dir, "*.idx").FirstOrDefault(x => Path.GetFileName(x).Equals("Main.idx", StringComparison.OrdinalIgnoreCase));
                if (f != null) yield return f;
            }
        }
    }

    /// <summary>Le bandiere per numero (1-7), dal primo Main.rsc che le contiene. Vuoto se non trovate.</summary>
    public static Dictionary<int, FlagImage> Load(IEnumerable<string> artDirs)
    {
        var res = new Dictionary<int, FlagImage>();
        foreach (var idx in CandidateIndexes(artDirs))
        {
            try
            {
                var rb = ResourceBundle.Load(idx);
                for (int i = 1; i < Slots.Length; i++)
                {
                    if (res.ContainsKey(i)) continue;
                    var e = rb.Find($"OOB_FLAGS_{Slots[i]}_HRZ");
                    if (e != null && e.IsImage) res[i] = new FlagImage(e.Width, e.Height, rb.ToBgra(e));
                }
            }
            catch (Exception) { }
            if (res.Count == Slots.Length - 1) break;
        }
        return res;
    }
}
