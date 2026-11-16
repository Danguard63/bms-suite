using System.Text.Json;

namespace TerrainTool.Core.Database;

/// <summary>
/// The changes made to a theater's database, kept in the work folder until they are installed: the full new content of every
/// record changed or added (table, number, fields in order) and a log. Every change is saved at once and also copied to
/// History\&lt;time&gt;.json with its description, so any earlier version can be brought back.
/// </summary>
public sealed class DbEdits
{
    public sealed class Rec
    {
        public string Table { get; set; } = "";
        public int Num { get; set; }
        public List<string[]> Fields { get; set; } = new();
    }

    public sealed class FileData
    {
        public string Theater { get; set; } = "";
        public string ObjectDir { get; set; } = "";
        public List<string> Log { get; set; } = new();
        public List<Rec> Records { get; set; } = new();
    }

    public string Folder { get; }
    public FileData Data { get; private set; } = new();
    public string FilePath => Path.Combine(Folder, "db_edits.json");
    public string HistoryDir => Path.Combine(Folder, "History");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private DbEdits(string folder) { Folder = folder; }

    public static DbEdits Open(string folder, string theater, string objectDir)
    {
        Directory.CreateDirectory(folder);
        var e = new DbEdits(folder);
        if (File.Exists(e.FilePath))
            e.Data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(e.FilePath)) ?? new FileData();
        e.Data.Theater = theater; e.Data.ObjectDir = objectDir;
        return e;
    }

    public int Count => Data.Records.Count;

    /// <summary>Records changed, by table.</summary>
    public IEnumerable<string> Tables => Data.Records.Select(r => r.Table).Distinct(StringComparer.OrdinalIgnoreCase);

    public void Set(string table, int num, List<(string Field, string Value)> fields)
    {
        var r = Data.Records.FirstOrDefault(x => string.Equals(x.Table, table, StringComparison.OrdinalIgnoreCase) && x.Num == num);
        if (r == null) Data.Records.Add(r = new Rec { Table = table.ToUpperInvariant(), Num = num });
        r.Fields = fields.Select(f => new[] { f.Field, f.Value }).ToList();
    }

    /// <summary>Saves the changes and a copy in History with the description of the last change.</summary>
    public void Save(string description)
    {
        Data.Log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm}  {description}");
        string json = JsonSerializer.Serialize(Data, Json);
        File.WriteAllText(FilePath, json);
        Directory.CreateDirectory(HistoryDir);
        File.WriteAllText(Path.Combine(HistoryDir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.json"), json);
    }

    /// <summary>Saved versions, newest first: file, time, the change that made it, how many records it changes.</summary>
    public List<(string File, DateTime Time, string Description, int Records)> History()
    {
        var res = new List<(string, DateTime, string, int)>();
        if (!Directory.Exists(HistoryDir)) return res;
        foreach (var f in Directory.EnumerateFiles(HistoryDir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                var d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(f));
                res.Add((f, File.GetLastWriteTime(f), d?.Log.LastOrDefault() ?? "", d?.Records.Count ?? 0));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return res;
    }

    /// <summary>Brings back a saved version (the current one is saved in History first, so this can be undone too).</summary>
    public void RestoreVersion(string historyFile)
    {
        var d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(historyFile)) ?? new FileData();
        d.Theater = Data.Theater; d.ObjectDir = Data.ObjectDir;
        Data = d;
        Save($"back to the version of {File.GetLastWriteTime(historyFile):yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>No changes any more (saved in History, so it can be undone).</summary>
    public void Clear()
    {
        Data.Records.Clear();
        Save("all changes removed");
    }
}
