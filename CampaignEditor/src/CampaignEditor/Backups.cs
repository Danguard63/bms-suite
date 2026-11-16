using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Dds;

namespace CampaignEditor;

/// <summary>
/// Copie di sicurezza dei file che il Campaign Editor sovrascrive (dentro BMS o altrove):
/// &lt;cartella del Campaign Editor&gt;\Backup\&lt;aaaaMMgg_hhmmss&gt;\&lt;file&gt; piu' origin.txt con il percorso originale di ogni file.
/// Da qui la finestra "Backups…" li rimette al loro posto.
/// </summary>
public static class BackupStore
{
    public sealed record Entry(string Folder, string File, string Original, DateTime When, long Size)
    {
        public string Name => System.IO.Path.GetFileName(File);
        public string Date => When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string SizeKb => (Size / 1024.0).ToString("N0", CultureInfo.InvariantCulture) + " KB";
        public bool OriginalExists => System.IO.File.Exists(Original);
    }

    public static string Root => Path.Combine(Directory.GetParent(StartWindow.WorkRoot())!.FullName, "Backup");

    private const string Index = "origin.txt";

    /// <summary>Copia file (se esiste) in una cartella nuova del Backup e ne ricorda il percorso. Restituisce la cartella, o null.</summary>
    public static string? Save(string file)
    {
        if (!System.IO.File.Exists(file)) return null;
        string full = Path.GetFullPath(file);
        string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string dst = Path.Combine(dir, Path.GetFileName(full));
        System.IO.File.Copy(full, dst, true);
        System.IO.File.AppendAllText(Path.Combine(dir, Index), Path.GetFileName(full) + "\t" + full + "\r\n");
        return dir;
    }

    public static List<Entry> List()
    {
        var res = new List<Entry>();
        if (!Directory.Exists(Root)) return res;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            string idx = Path.Combine(dir, Index);
            if (!System.IO.File.Exists(idx)) continue;
            DateTime when = DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
            foreach (var line in System.IO.File.ReadAllLines(idx))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                string f = Path.Combine(dir, p[0]);
                if (System.IO.File.Exists(f)) res.Add(new Entry(dir, f, p[1], when, new FileInfo(f).Length));
            }
        }
        return res.OrderByDescending(e => e.When).ToList();
    }

    /// <summary>Rimette il file al suo posto; la versione che c'e' adesso finisce a sua volta nel Backup.</summary>
    public static string? Restore(Entry e)
    {
        string? kept = Save(e.Original);
        Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
        System.IO.File.Copy(e.File, e.Original, true);
        return kept;
    }
}

/// <summary>Elenco dei backup con "Restore" (rimette il file dov'era) e "Open folder".</summary>
public sealed class BackupWindow : Window
{
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
        GridLinesVisibility = DataGridGridLinesVisibility.All, VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
        HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)), RowHeight = 22,
    };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    public BackupWindow()
    {
        Title = "Campaign Editor – backups";
        Width = 1100; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        void C(string h, string p, double w) => _grid.Columns.Add(new DataGridTextColumn
            { Header = h, Binding = new Binding(p), Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
        C("Saved on", nameof(BackupStore.Entry.Date), 140); C("File", nameof(BackupStore.Entry.Name), 220); C("Size", nameof(BackupStore.Entry.SizeKb), 80);
        C("Goes back to", nameof(BackupStore.Entry.Original), 0);
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var restore = UnitText.Btn("Restore", Restore, "Put the selected file(s) back where they were (the current version is backed up first)");
        restore.FontWeight = FontWeights.SemiBold;
        bar.Children.Add(restore);
        bar.Children.Add(UnitText.Btn("Open folder", () =>
        {
            string d = _grid.SelectedItem is BackupStore.Entry e ? e.Folder : BackupStore.Root;
            Directory.CreateDirectory(d);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + d + "\"") { UseShellExecute = true });
        }));
        bar.Children.Add(UnitText.Btn("Refresh", Fill));
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
            Text = "Every time the editor replaces a file (in BMS or elsewhere) the old version is copied here first: " + BackupStore.Root +
                   ". Restore puts it back; nothing is deleted. Close BMS before restoring files of its Campaign folders.",
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(_grid);
        Content = root;
        _grid.MouseDoubleClick += (_, _) => Restore();
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        var l = BackupStore.List();
        _grid.ItemsSource = l;
        _status.Text = l.Count == 0 ? "No backups yet." : $"{l.Count} backed-up file(s).";
    }

    private void Restore()
    {
        var sel = _grid.SelectedItems.OfType<BackupStore.Entry>().ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select one or more files.", Title); return; }
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string list = string.Join("\n", sel.Select(e => $"{e.Name} ({e.Date})  →  {e.Original}"));
        if (MessageBox.Show(this, $"Restore\n\n{list}\n\nThe versions there now are backed up first.", Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int n = 0;
        foreach (var e in sel)
            try { BackupStore.Restore(e); n++; }
            catch (Exception ex) { MessageBox.Show(this, $"{e.Name}: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        Fill();
        _status.Text = $"Restored {n} file(s).";
    }
}
