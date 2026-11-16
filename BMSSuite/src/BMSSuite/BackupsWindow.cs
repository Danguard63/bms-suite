using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BMSSuite;

/// <summary>
/// Tutti i backup fatti dai programmi della suite (cartelle dei backup sotto la cartella della suite), dal piu' recente.
/// Solo consultazione: il ripristino si fa dal programma che li ha fatti, che sa cosa rimettere e dove.
/// </summary>
public sealed class BackupsWindow : Window
{
    public sealed record Row(string Program, string When, string Size, string Folder, DateTime Time);

    /// <summary>Nomi delle cartelle in cui i programmi mettono i backup (una sottocartella per backup).</summary>
    private static readonly string[] BackupDirNames = { "Backups", "BmsBackup", "Backup" };

    public BackupsWindow(string suiteRoot)
    {
        Title = "BMS Suite – backups";
        Width = 980; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = Find(suiteRoot);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, ItemsSource = rows,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Program", Binding = new System.Windows.Data.Binding(nameof(Row.Program)), Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTextColumn { Header = "When", Binding = new System.Windows.Data.Binding(nameof(Row.When)), Width = new DataGridLength(140), SortMemberPath = nameof(Row.Time) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new System.Windows.Data.Binding(nameof(Row.Size)), Width = new DataGridLength(80) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Folder", Binding = new System.Windows.Data.Binding(nameof(Row.Folder)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var open = new Button { Content = "Open folder", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 8, 8, 0) };
        open.Click += (_, _) =>
        {
            if (grid.SelectedItem is Row r && Directory.Exists(r.Folder))
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + r.Folder + "\"") { UseShellExecute = true }); } catch { }
        };
        grid.MouseDoubleClick += (_, _) => open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 8, 0, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(open); bar.Children.Add(close);
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = rows.Count == 0 ? "No backups yet: each program makes one before it writes into BMS."
                 : $"{rows.Count} backups, newest first. To put one back, use Restore… in the program that made it (it knows what to put back and where).",
        };
        var dp = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(note, Dock.Top); dp.Children.Add(note);
        DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
        dp.Children.Add(grid);
        Content = dp;
    }

    public static List<Row> Find(string suiteRoot)
    {
        var res = new List<Row>();
        foreach (var tool in MainWindow.Tools)
        {
            // development tree (BMS Suite\<program>) or release package (BMS Suite\Programs\<program>)
            string dir = Path.Combine(suiteRoot, tool.Folder);
            if (!Directory.Exists(dir)) dir = Path.Combine(suiteRoot, "Programs", tool.Folder);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> backupDirs;
            try
            {
                backupDirs = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                    .Where(d => BackupDirNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    .Where(d => !d.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !d.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                    .ToList();
            }
            catch { continue; }
            foreach (var bd in backupDirs)
                foreach (var one in SafeDirs(bd))
                {
                    var info = new DirectoryInfo(one);
                    long size = 0;
                    try { size = info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
                    var time = info.CreationTime;
                    res.Add(new Row(tool.Title, time.ToString("dd/MM/yyyy HH:mm"), size >= 1 << 20 ? $"{size / 1048576.0:0.0} MB" : $"{size / 1024.0:0} KB", one, time));
                }
        }
        return res.OrderByDescending(r => r.Time).ToList();
    }

    private static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.EnumerateDirectories(d).ToList(); } catch { return Array.Empty<string>(); }
    }
}
