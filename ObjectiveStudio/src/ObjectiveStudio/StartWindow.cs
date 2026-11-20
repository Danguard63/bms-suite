using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace ObjectiveStudio;

/// <summary>
/// First window: which theater to work on. A theater installed in BMS (any 4.38 theater: Korea, Israel, add-ons…) or a
/// TerrainTool project (a theater being converted). For an installed theater a work folder is made in
/// &lt;Objective Studio&gt;\Theaters\&lt;theater&gt;: BMS is only read.
/// </summary>
public sealed class StartWindow : Window
{
    /// <summary>The .ttproj chosen (the work folder's one for an installed theater).</summary>
    public string? ProjectFile { get; private set; }

    private readonly TextBox _data = new() { MinWidth = 420, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _theaters = new() { Height = 230 };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11.5 };
    private readonly Button _open = new() { Content = "Open theater", IsDefault = true, FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ListBox _recent = new() { Height = 110 };
    private readonly Button _openRecent = new() { Content = "Open", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };

    public StartWindow()
    {
        Title = "Objective Studio – choose a theater";
        Width = 760; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _data.Text = SavedDataDir() ?? @"C:\Falcon BMS 4.38\Data";

        var dataRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var lbl = new TextBlock { Text = "BMS Data folder  ", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(lbl, Dock.Left); dataRow.Children.Add(lbl);
        var browse = Btn("Browse…", Browse); DockPanel.SetDock(browse, Dock.Right); dataRow.Children.Add(browse);
        dataRow.Children.Add(_data);

        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(Head("Theater installed in BMS"));
        sp.Children.Add(Note("Any BMS 4.38 theater (Korea, Israel, add-ons…). Objective Studio only reads BMS: your edits are kept in its Theaters folder " +
                             "until you install them, with a backup of every file replaced."));
        sp.Children.Add(dataRow);
        sp.Children.Add(_theaters);
        sp.Children.Add(_info);
        sp.Children.Add(_open);

        sp.Children.Add(Head("Recent"));
        sp.Children.Add(Note("Theaters and TerrainTool projects opened before: select one and press Open (or double-click it)."));
        sp.Children.Add(_recent);
        sp.Children.Add(_openRecent);

        sp.Children.Add(Head("Theater conversion (TerrainTool project)"));
        sp.Children.Add(Note("For a theater being converted from an older BMS version with TerrainTool: open its project (.ttproj)."));
        sp.Children.Add(Btn("Open TerrainTool project…", BrowseProject, HorizontalAlignment.Left));
        Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 900 };

        _theaters.SelectionChanged += (_, _) => ShowTheater();
        _theaters.MouseDoubleClick += (_, _) => OpenTheater();
        _open.Click += (_, _) => OpenTheater();
        _recent.MouseDoubleClick += (_, _) => OpenRecent();
        _openRecent.Click += (_, _) => OpenRecent();
        _recent.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { OpenRecent(); e.Handled = true; } };
        // one list at a time: a recent entry selected makes its Open the default button, a theater selected the other one
        _recent.SelectionChanged += (_, _) =>
        {
            bool on = _recent.SelectedItem is ListBoxItem { Tag: string };
            _openRecent.IsEnabled = on; _openRecent.IsDefault = on; _open.IsDefault = !on;
            if (on) _theaters.SelectedItem = null;
        };
        _theaters.SelectionChanged += (_, _) => { if (_theaters.SelectedItem != null) { _recent.SelectedItem = null; _open.IsDefault = true; } };
        _data.LostFocus += (_, _) => LoadTheaters();

        foreach (var f in MainWindow.Recent().Where(File.Exists))
            _recent.Items.Add(new ListBoxItem { Content = MainWindow.Describe(f), Tag = f, ToolTip = f + "\nSelect and press Open, or double-click" });
        if (_recent.Items.Count == 0) _recent.Items.Add(new ListBoxItem { Content = "(nothing yet)", IsEnabled = false });
        LoadTheaters();
    }

    private static TextBlock Head(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Note(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) };
    private static Button Btn(string text, Action a, HorizontalAlignment h = HorizontalAlignment.Stretch)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = h };
        b.Click += (_, _) => a();
        return b;
    }

    private static string SettingsFile => Path.Combine(MainWindow.Root(), "Settings", "bms_data_folder.txt");

    private static string? SavedDataDir()
    {
        try { return File.Exists(SettingsFile) && File.ReadAllText(SettingsFile).Trim() is { Length: > 0 } s && Directory.Exists(s) ? s : null; }
        catch (IOException) { return null; }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "BMS Data folder (the one with TerrData, Campaign, Art, Add-On …)" };
        if (Directory.Exists(_data.Text)) dlg.InitialDirectory = _data.Text;
        if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; LoadTheaters(); }
    }

    private void LoadTheaters()
    {
        _theaters.Items.Clear();
        string dir = _data.Text.Trim();
        var list = Directory.Exists(dir) ? BmsTheater.List(dir) : new List<BmsTheater>();
        foreach (var t in list)
        {
            var problems = TheaterWorkspace.Problems(t);
            var row = new StackPanel { Margin = new Thickness(2) };
            row.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, Foreground = problems.Count == 0 ? Brushes.Black : Brushes.Gray });
            row.Children.Add(new TextBlock { Text = t.Subtitle + (problems.Count > 0 ? "  –  cannot be opened" : ""), FontSize = 11, Foreground = Brushes.DimGray });
            _theaters.Items.Add(new ListBoxItem { Content = row, Tag = t });
        }
        if (list.Count == 0)
            _info.Text = Directory.Exists(dir) ? "No theater in TerrData\\TheaterDefinition\\theater.lst of this folder." : "Choose the BMS Data folder (for example C:\\Falcon BMS 4.38\\Data).";
        else { _info.Text = $"{list.Count} theaters installed. Choose one."; _theaters.SelectedIndex = 0; }
        _open.IsEnabled = false;
        ShowTheater();
    }

    private void ShowTheater()
    {
        if ((_theaters.SelectedItem as ListBoxItem)?.Tag is not BmsTheater t) { _open.IsEnabled = false; return; }
        var problems = TheaterWorkspace.Problems(t);
        string work = Path.Combine(MainWindow.TheatersRoot(), TheaterWorkspace.FolderName(t));
        _info.Text = problems.Count > 0
            ? "⚠ " + string.Join("\n⚠ ", problems)
            : $"Objects    {t.ObjectDir}\nCampaign   {t.CampaignDir}\nTerrain    {t.NewTerrainDir}\nYour edits {work}" + (Directory.Exists(work) ? "  (already there)" : "  (made when you open it)");
        _info.FontFamily = new FontFamily(problems.Count > 0 ? "Segoe UI" : "Consolas");
        _open.IsEnabled = problems.Count == 0;
    }

    private void OpenTheater()
    {
        if ((_theaters.SelectedItem as ListBoxItem)?.Tag is not BmsTheater t || TheaterWorkspace.Problems(t).Count > 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, _data.Text.Trim());
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        try
        {
            var p = TheaterWorkspace.Open(t, MainWindow.TheatersRoot());
            Done(Path.Combine(p.ProjectFolder, TheaterWorkspace.FolderName(t) + ".ttproj"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "TerrainTool project", Filter = "TerrainTool project (*.ttproj)|*.ttproj" };
        // start where the projects are: the folder of the last project opened, else TerrainTool\Projects of the Suite
        string? last = MainWindow.Recent().FirstOrDefault(f => f.EndsWith(".ttproj", StringComparison.OrdinalIgnoreCase) && File.Exists(f) && !IsWorkFolderProject(f));
        string? projects = global::TerrainTool.Core.SuiteLayout.FolderOf("TerrainTool") is { } tt ? Path.Combine(tt, "Projects") : null;
        string? start = last != null ? Path.GetDirectoryName(Path.GetDirectoryName(last)) : projects;
        if (start != null && Directory.Exists(start)) dlg.InitialDirectory = start;
        else if (projects != null && Directory.Exists(projects)) dlg.InitialDirectory = projects;
        if (dlg.ShowDialog(this) == true) Done(dlg.FileName);
    }

    private void OpenRecent()
    {
        if (_recent.SelectedItem is ListBoxItem { Tag: string f }) Done(f);
    }

    /// <summary>The .ttproj of an installed theater lives in Objective Studio's own Theaters folder, not among the TerrainTool projects.</summary>
    private static bool IsWorkFolderProject(string f) =>
        Path.GetFullPath(f).StartsWith(Path.GetFullPath(Path.Combine(MainWindow.Root(), "Theaters")), StringComparison.OrdinalIgnoreCase);

    private void Done(string file)
    {
        ProjectFile = file;
        DialogResult = true;
    }
}
