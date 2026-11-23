using System.Globalization;
using System.Text;
using TerrainTool.Core.Dds;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Squadron patches of a theater, as the campaign uses them. Art\Patches\ImageIds.id lists "name TAB id" (1ST_BMS 70001,
/// ROUNDEL_BAC 70002, ... KTO_CAMP_ROKAF_102ND_FS 70030); the images are in Art\resource\Patches.rsc under the same names.
/// The patch number kept by a squadron in the .cam / .tac is id - 70001, i.e. the place in the list counting from 0 (0 = 1ST_BMS,
/// 29 = KTO_CAMP_ROKAF_102ND_FS 70030). Checked on the Korea save0.cam: with id - 70001 the PRC squadrons (11) wear ROUNDEL_PLANAF,
/// the DPRK ones (10) ROUNDEL_NKAF, the CIS ones (24) ROUNDEL_VVS, ROK and U.S. squadrons ROKAF / USAF patches; with id - 70000
/// every one would be off by one. The comment at the top of ImageIds.id asks to add new patches after the last KTO_CAMP_ "to keep
/// the same patches IDs already declared in CAM files". Read only.
/// </summary>
public static class SquadronPatches
{
    /// <summary>The id of patch 0 in ImageIds.id (1ST_BMS): a squadron's patch number is id - IdBase.</summary>
    public const int IdBase = 70001;

    public sealed record Patch(int Number, int Id, string Name, int Width, int Height, byte[]? Bgra)
    {
        public override string ToString() => $"{Number}  {Name}";
    }

    /// <summary>
    /// The patches by number. artDirs: the theater's Art first, then BMS's. The first ImageIds.id found is the list (a theater's
    /// list replaces BMS's); each image is taken from the first Patches.rsc that has it. A patch with no image has Bgra null.
    /// </summary>
    public static SortedDictionary<int, Patch> Load(IEnumerable<string> artDirs)
    {
        var res = new SortedDictionary<int, Patch>();
        var dirs = artDirs.Where(a => !string.IsNullOrWhiteSpace(a) && Directory.Exists(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? ids = dirs.Select(a => TheaterArtJob.FindCI(a, @"Patches\ImageIds.id")).FirstOrDefault(f => f != null);
        if (ids == null) return res;
        var bundles = new List<ResourceBundle>();
        foreach (var a in dirs)
            if (TheaterArtJob.FindCI(a, @"resource\Patches.idx") is { } idx)
                try { bundles.Add(ResourceBundle.Load(idx)); } catch (Exception) { }
        foreach (var raw in File.ReadAllLines(ids, Encoding.Latin1))
        {
            if (raw.StartsWith('#')) continue;
            var v = raw.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (v.Length < 2 || !int.TryParse(v[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) continue;
            int n = id - IdBase;
            if (n < 0 || n > ushort.MaxValue || res.ContainsKey(n)) continue;
            byte[]? bgra = null; int w = 0, h = 0;
            foreach (var b in bundles)
            {
                if (b.Find(v[0]) is not { IsImage: true } e) continue;
                try { bgra = b.ToBgra(e); w = e.Width; h = e.Height; break; } catch (Exception) { }
            }
            res[n] = new Patch(n, id, v[0], w, h, bgra);
        }
        return res;
    }
}
