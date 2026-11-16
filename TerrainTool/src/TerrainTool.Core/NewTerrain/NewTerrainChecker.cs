using TerrainTool.Core.Theater;

namespace TerrainTool.Core.NewTerrain;

public enum CheckStatus { Ok, Missing, WrongSize, Present, MissingOptional }

public sealed class CheckResult
{
    public required SpecEntry Entry { get; init; }
    public CheckStatus Status { get; init; }
    public long? ActualSize { get; init; }
    public long? ExpectedSize { get; init; }
    public long? KoreaSize { get; init; }
    public int? FileCount { get; init; }
    public string Note { get; init; } = "";

    public string StatusLabel => Status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Missing => "Missing",
        CheckStatus.WrongSize => "Wrong size",
        CheckStatus.Present => "Present",
        CheckStatus.MissingOptional => "Missing (optional)",
        _ => "",
    };
}

/// <summary>
/// Confronta una cartella NewTerrain con la struttura attesa e, se indicata, con la Korea.
/// Solo lettura: non scrive mai né nella cartella controllata né in quella di riferimento.
/// </summary>
public static class NewTerrainChecker
{
    public static List<CheckResult> Check(string folder, TheaterConfig cfg, string? koreaFolder = null)
    {
        var results = new List<CheckResult>();
        foreach (var e in NewTerrainSpec.Entries(cfg))
        {
            string path = Path.Combine(folder, e.RelPath);
            long? korea = null;
            if (koreaFolder != null)
            {
                // Il file .aef porta il nome del teatro: nella Korea è Korea.aef
                string kRel = e.RelPath.EndsWith(".aef", StringComparison.OrdinalIgnoreCase) ? @"HeightMaps\Korea.aef" : e.RelPath;
                string kPath = Path.Combine(koreaFolder, kRel);
                if (!e.IsDirectory && File.Exists(kPath)) korea = new FileInfo(kPath).Length;
            }

            if (e.IsDirectory)
            {
                if (!Directory.Exists(path))
                {
                    results.Add(new CheckResult { Entry = e, Status = e.Optional ? CheckStatus.MissingOptional : CheckStatus.Missing });
                    continue;
                }
                int n = SafeCount(path);
                results.Add(new CheckResult { Entry = e, Status = n > 0 ? CheckStatus.Present : CheckStatus.Missing, FileCount = n, Note = n == 0 ? "empty folder" : "" });
                continue;
            }

            long? expected = e.ExpectedSize(cfg) ?? (e.SameSizeAsKorea ? korea : null);
            if (!File.Exists(path))
            {
                results.Add(new CheckResult { Entry = e, Status = e.Optional ? CheckStatus.MissingOptional : CheckStatus.Missing, ExpectedSize = expected, KoreaSize = korea });
                continue;
            }
            long size = new FileInfo(path).Length;
            CheckStatus st = expected is null ? CheckStatus.Present : (size == expected ? CheckStatus.Ok : CheckStatus.WrongSize);
            string note = "";
            if (e.RelPath == "Theater.txt") note = CheckTheaterTxt(path, cfg);
            if (note.Length > 0 && st == CheckStatus.Present) st = CheckStatus.WrongSize;
            results.Add(new CheckResult { Entry = e, Status = st, ActualSize = size, ExpectedSize = expected, KoreaSize = korea, Note = note });
        }
        return results;
    }

    private static string CheckTheaterTxt(string path, TheaterConfig cfg)
    {
        try
        {
            var t = TheaterConfig.ParseTheaterTxt(File.ReadAllText(path));
            var diffs = new List<string>();
            if (t.SizeKm != cfg.SizeKm) diffs.Add($"size {t.SizeKm}≠{cfg.SizeKm}");
            if (t.MapSizePixels != cfg.MapSizePixels) diffs.Add($"pixel {t.MapSizePixels}≠{cfg.MapSizePixels}");
            if (Math.Abs(t.CenterLat - cfg.CenterLat) > 1e-6) diffs.Add("center lat differs");
            if (Math.Abs(t.CenterLon - cfg.CenterLon) > 1e-6) diffs.Add("center lon differs");
            return diffs.Count == 0 ? "" : "Differs from project: " + string.Join(", ", diffs);
        }
        catch (Exception ex) { return "Unreadable: " + ex.Message; }
    }

    private static int SafeCount(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "";
        double b = bytes.Value;
        string[] u = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{b:0.##} {u[i]}";
    }
}
