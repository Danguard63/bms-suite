namespace TerrainTool.Core;

/// <summary>
/// Folders of the BMS Suite, the same rules for the development tree and for the release package:
/// <list type="bullet">
/// <item>development: <c>C:\BMS Suite\&lt;Program&gt;</c> is the folder with build.bat; the exe is in
///   <c>&lt;Program&gt;\src\&lt;Project&gt;\bin\Release\net8.0-windows</c>;</item>
/// <item>release package: <c>&lt;Suite&gt;\Programs\&lt;Program&gt;</c> holds the exe.</item>
/// </list>
/// Each program keeps its own folders (Settings, Backup, Theaters, Projects...) in its program folder.
/// </summary>
public static class SuiteLayout
{
    /// <summary>The folder of the release package that holds one folder per program.</summary>
    public const string ProgramsFolderName = "Programs";

    /// <summary>True for a program folder: the one with build.bat, or a folder inside Programs.</summary>
    public static bool IsProgramFolder(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "build.bat"))) return true;
            return string.Equals(new DirectoryInfo(dir).Parent?.Name, ProgramsFolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Folder of the running program, or null when the exe runs from somewhere else.</summary>
    public static string? ProgramFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (IsProgramFolder(d.FullName)) return d.FullName;
        return null;
    }

    /// <summary>The folder that holds the program folders: C:\BMS Suite (development) or &lt;Suite&gt;\Programs (release).</summary>
    public static string? ProgramsRoot() => ProgramFolder() is { } f ? Path.GetDirectoryName(f) : null;

    /// <summary>The folder of a program of the suite (it may not exist).</summary>
    public static string? FolderOf(string program) => ProgramsRoot() is { } r ? Path.Combine(r, program) : null;

    /// <summary>The exe of a program of the suite (release package first, then the development build), or null.</summary>
    public static string? ExeOf(string program, string? project = null, string? exeName = null)
    {
        project ??= program;
        exeName ??= project + ".exe";
        if (FolderOf(program) is not { } f) return null;
        foreach (var p in new[]
        {
            Path.Combine(f, exeName),
            Path.Combine(f, "src", project, "bin", "Release", "net8.0-windows", exeName),
            Path.Combine(f, "src", project, "bin", "Debug", "net8.0-windows", exeName),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
