using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerrainTool.Core.Dds;

namespace ModelEditor;

/// <summary>
/// Copie dei file di BMS prima di ogni modifica: Backup\&lt;data_ora&gt;\&lt;percorso completo senza ':'&gt; con un file
/// "original.txt" che dice da dove viene. Si possono rimettere a posto dalla finestra Backups.
/// </summary>
public static class BackupStore
{
    public sealed record Entry(string Folder, string Date, string Original, string Copy)
    {
        public string Name => Path.GetFileName(Original);
    }

    public static string Root => Path.Combine(MainWindow.Root(), "Backup");

    /// <summary>Copia il file (se esiste) prima di cambiarlo. Ritorna la copia o null.</summary>
    public static string? Save(string file)
    {
        if (!File.Exists(file)) return null;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string dir = Path.Combine(Root, stamp);
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(file));
        File.Copy(file, copy, true);
        File.WriteAllText(Path.Combine(dir, "original.txt"), Path.GetFullPath(file));
        return copy;
    }

    public static List<Entry> List()
    {
        var res = new List<Entry>();
        if (!Directory.Exists(Root)) return res;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            try
            {
                string o = Path.Combine(d, "original.txt");
                if (!File.Exists(o)) continue;
                string orig = File.ReadAllText(o).Trim();
                string copy = Path.Combine(d, Path.GetFileName(orig));
                if (!File.Exists(copy)) continue;
                string n = Path.GetFileName(d);
                string date = DateTime.TryParseExact(n[..Math.Min(15, n.Length)], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss") : n;
                res.Add(new Entry(d, date, orig, copy));
            }
            catch { }
        }
        return res.OrderByDescending(e => e.Folder, StringComparer.Ordinal).ToList();
    }

    /// <summary>Rimette la copia al suo posto (salvando prima la versione attuale).</summary>
    public static void Restore(Entry e)
    {
        Save(e.Original);
        Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
        File.Copy(e.Copy, e.Original, true);
    }
}

/// <summary>Elenco delle copie, con "Restore".</summary>
public sealed class BackupWindow : Window
{
    private readonly ListView _list = new();
    public event Action<string>? Restored;

    public BackupWindow()
    {
        Title = "Backups — files changed by the Model Editor";
        Width = 900; Height = 500; MinWidth = 600; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Date", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Date") });
        gv.Columns.Add(new GridViewColumn { Header = "File", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Original place", Width = 580, DisplayMemberBinding = new System.Windows.Data.Binding("Original") });
        _list.View = gv;
        _list.SelectionMode = SelectionMode.Extended;
        var bar = new WrapPanel { Margin = new Thickness(6) };
        var restore = new Button { Content = "Restore selected", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        var open = new Button { Content = "Open backup folder", Padding = new Thickness(8, 2, 8, 2) };
        restore.Click += (_, _) => RestoreSelected();
        open.Click += (_, _) => { Directory.CreateDirectory(BackupStore.Root); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{BackupStore.Root}\"") { UseShellExecute = true }); };
        bar.Children.Add(restore); bar.Children.Add(open);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(_list);
        Content = root;
        Fill();
    }

    private void Fill() => _list.ItemsSource = BackupStore.List();

    private void RestoreSelected()
    {
        var sel = _list.SelectedItems.OfType<BackupStore.Entry>().ToList();
        if (sel.Count == 0) return;
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (sel.GroupBy(e => e.Original, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) { MessageBox.Show(this, "Choose only one version of each file.", Title); return; }
        if (MessageBox.Show(this, "Restore\n\n" + string.Join("\n", sel.Select(e => $"{e.Name} ({e.Date}) → {e.Original}")) + "\n\nThe current versions are backed up first.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var e in sel)
        {
            try { BackupStore.Restore(e); Restored?.Invoke(e.Original); }
            catch (Exception ex) { MessageBox.Show(this, $"{e.Original}: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        Fill();
    }
}
