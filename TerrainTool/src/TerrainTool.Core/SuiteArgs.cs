using TerrainTool.Core.Campaign;

namespace TerrainTool.Core;

/// <summary>
/// Arguments the BMS Suite main menu passes to every program it starts:
/// <c>--theater &lt;name in BMS's theater list&gt;</c> and <c>--bms-data &lt;BMS Data folder&gt;</c>.
/// A program started by hand gets none of them and behaves as before.
/// </summary>
public static class SuiteArgs
{
    /// <summary>Value after <c>--name</c> (or <c>/name</c>), or null.</summary>
    public static string? Value(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("/" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && args[i + 1].Trim().Length > 0 ? args[i + 1].Trim() : null;
    }

    /// <summary>The theater chosen in the main menu (its name), or null.</summary>
    public static string? TheaterName(string[] args) => Value(args, "theater");

    /// <summary>The BMS Data folder chosen in the main menu, or null when missing or not on disk.</summary>
    public static string? BmsData(string[] args) => Value(args, "bms-data") is { } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// The theater chosen in the main menu, read from BMS's theater list; null when there is no <c>--theater</c>,
    /// no BMS Data folder (argument or <paramref name="fallbackDataDir"/>) or the theater is not in the list.
    /// </summary>
    public static BmsTheater? Theater(string[] args, string? fallbackDataDir = null)
    {
        string? name = TheaterName(args);
        if (name == null) return null;
        string? data = BmsData(args) ?? (fallbackDataDir != null && Directory.Exists(fallbackDataDir) ? fallbackDataDir : null);
        if (data == null) return null;
        try { return BmsTheater.List(data).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
