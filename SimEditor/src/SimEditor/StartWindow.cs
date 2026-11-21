using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace SimEditor;

/// <summary>
/// Which theater to work on: a theater installed in BMS (any add-on: a work folder is made in &lt;Sim Editor&gt;\Theaters, BMS is
/// only read until Install), a recent one, or a TerrainTool project (theater conversion).
/// </summary>
public sealed class StartWindow : Window
{
    public string? ProjectFile { get; private set; }
    private readonly ListBox _recent = new() { MinHeight = 110, MaxHeight = 160 };
    private readonly TextBox _data = new() { MinWidth = 400, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _theaters = new() { Height = 200 };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11.5 };
    private readonly Button _open = new() { Content = "Open theater", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };

    public static string Root() => TerrainTool.Core.SuiteLayout.ProgramFolder()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS Sim Editor");
    /// <summary>Work folders of the installed theaters.</summary>
    public static string TheatersRoot() => Path.Combine(Root(), "Theaters");
    private static string RecentFile => Path.Combine(Root(), "Settings", "projects.txt");
    private static string DataFile => Path.Combine(Root(), "Settings", "bms_data_folder.txt");

    /// <summary>Recent: this program's (theaters and projects) first, then Objective Studio's TerrainTool projects.</summary>
    public static List<string> Recent()
    {
        var res = new List<string>();
        void Add(string file, string? skipUnder)
        {
            try
            {
                if (!File.Exists(file)) return;
                foreach (var l in File.ReadAllLines(file).Select(x => x.Trim()))
                    if (l.EndsWith(".ttproj", StringComparison.OrdinalIgnoreCase) && File.Exists(l) && !res.Contains(l, StringComparer.OrdinalIgnoreCase)
                        && (skipUnder == null || !Path.GetFullPath(l).StartsWith(skipUnder, StringComparison.OrdinalIgnoreCase)))
                        res.Add(l);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Add(RecentFile, null);
        if (TerrainTool.Core.SuiteLayout.FolderOf("ObjectiveStudio") is { } os)
            Add(Path.Combine(os, "Settings", "projects.txt"), Path.GetFullPath(Path.Combine(os, "Theaters")));
        return res;
    }

    public static void AddRecent(string file)
    {
        try
        {
            var l = Recent().Where(x => !x.Equals(file, StringComparison.OrdinalIgnoreCase)).Prepend(file).Take(12).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllLines(RecentFile, l);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The work folder (.ttproj) of an installed theater, made when needed.</summary>
    public static string OpenTheater(BmsTheater t)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!); File.WriteAllText(DataFile, t.DataDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var p = TheaterWorkspace.Open(t, TheatersRoot());
        return Path.Combine(p.ProjectFolder, TheaterWorkspace.FolderName(t) + ".ttproj");
    }

    private static string Describe(string f)
    {
        try
        {
            var p = TerrainTool.Core.Project.TheaterProject.Load(f);
            return p.IsInstalledTheater ? $"{(p.Theater.DisplayName.Trim() is { Length: > 0 } n ? n : p.Theater.Name)}   –   theater installed in BMS"
                                        : $"{Path.GetFileNameWithoutExtension(f)}   –   TerrainTool project (theater conversion)";
        }
        catch (Exception) { return f; }
    }

    public StartWindow()
    {
        Title = "Sim Editor – choose a theater";
        Width = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        try { _data.Text = File.Exists(DataFile) && File.ReadAllText(DataFile).Trim() is { Length: > 0 } d && Directory.Exists(d) ? d : @"C:\Falcon BMS 4.38\Data"; }
        catch (IOException) { _data.Text = @"C:\Falcon BMS 4.38\Data"; }

        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = "Sim Editor", FontSize = 18, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(Note("The theater's own Sim files (missiles, radars, vehicles, bombs, flight models of AI-only aircraft), the database changes (DbEdits.txt) and the check of the air defence systems. " +
                             "BMS is only read: your changes stay in the work folder until 'Install into BMS…', which saves every file it replaces (Restore)."));
        sp.Children.Add(Head("Theater installed in BMS"));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var lbl = new TextBlock { Text = "BMS Data folder  ", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(lbl, Dock.Left); row.Children.Add(lbl);
        var browseData = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browseData.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "BMS Data folder" };
            if (Directory.Exists(_data.Text)) dlg.InitialDirectory = _data.Text;
            if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; LoadTheaters(); }
        };
        DockPanel.SetDock(browseData, Dock.Right); row.Children.Add(browseData);
        row.Children.Add(_data);
        sp.Children.Add(row);
        sp.Children.Add(_theaters);
        sp.Children.Add(_info);
        sp.Children.Add(_open);

        sp.Children.Add(Head("Recent"));
        foreach (var f in Recent()) _recent.Items.Add(new ListBoxItem { Content = Describe(f), Tag = f, ToolTip = f });
        if (_recent.Items.Count == 0) _recent.Items.Add(new ListBoxItem { Content = "(nothing yet)", IsEnabled = false });
        sp.Children.Add(_recent);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openRecent = new Button { Content = "Open", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
        var browse = new Button { Content = "TerrainTool project…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "A theater being converted with TerrainTool (.ttproj)" };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        bar.Children.Add(openRecent); bar.Children.Add(browse); bar.Children.Add(cancel);
        sp.Children.Add(bar);
        Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 900 };

        openRecent.Click += (_, _) => OpenRecent();
        _recent.MouseDoubleClick += (_, _) => OpenRecent();
        _recent.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenRecent(); };
        _recent.SelectionChanged += (_, _) =>
        {
            bool on = _recent.SelectedItem is ListBoxItem { Tag: string };
            openRecent.IsEnabled = on; openRecent.IsDefault = on; _open.IsDefault = !on;
            if (on) _theaters.SelectedItem = null;
        };
        _theaters.SelectionChanged += (_, _) => { ShowTheater(); if (_theaters.SelectedItem != null) { _recent.SelectedItem = null; _open.IsDefault = true; } };
        _theaters.MouseDoubleClick += (_, _) => Open();
        _open.Click += (_, _) => Open();
        browse.Click += (_, _) => Browse();
        string loaded = _data.Text.Trim();
        _data.LostFocus += (_, _) => { if (_data.Text.Trim() != loaded) { loaded = _data.Text.Trim(); LoadTheaters(); } };
        LoadTheaters();
    }

    private static TextBlock Head(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Note(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 6) };

    private void LoadTheaters()
    {
        _theaters.Items.Clear();
        string dir = _data.Text.Trim();
        var list = Directory.Exists(dir) ? BmsTheater.List(dir) : new List<BmsTheater>();
        foreach (var t in list)
        {
            var r = new StackPanel { Margin = new Thickness(2) };
            r.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold });
            r.Children.Add(new TextBlock { Text = t.Subtitle, FontSize = 11, Foreground = Brushes.DimGray });
            _theaters.Items.Add(new ListBoxItem { Content = r, Tag = t });
        }
        _info.Text = list.Count == 0 ? (Directory.Exists(dir) ? "No theater in TerrData\\TheaterDefinition\\theater.lst of this folder." : "Choose the BMS Data folder (for example C:\\Falcon BMS 4.38\\Data).")
                                     : $"{list.Count} theaters installed. Choose one.";
        ShowTheater();
    }

    private void ShowTheater()
    {
        if ((_theaters.SelectedItem as ListBoxItem)?.Tag is not BmsTheater t) { _open.IsEnabled = false; return; }
        string sim = TerrainTool.Core.Sim.TheaterSim.SimDir(t);
        string? why = TerrainTool.Core.Sim.TheaterSim.Problem(t);
        _info.Text = $"Sim        {sim}{(TerrainTool.Core.Sim.TheaterSim.HasOwnSim(t) ? "" : "  (BMS's: the theater gets its own copy at the first install)")}\nDatabase   {t.ObjectDir}" +
                     (why != null ? $"\n⚠ Install is not possible: {why}. The files can still be read and checked." : "");
        _info.FontFamily = new FontFamily("Consolas");
        _open.IsEnabled = true;
    }

    private void Open()
    {
        if ((_theaters.SelectedItem as ListBoxItem)?.Tag is not BmsTheater t) return;
        try { ProjectFile = OpenTheater(t); DialogResult = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenRecent()
    {
        if (_recent.SelectedItem is ListBoxItem { Tag: string f }) { ProjectFile = f; DialogResult = true; }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "TerrainTool project", Filter = "TerrainTool project (*.ttproj)|*.ttproj" };
        string? projects = TerrainTool.Core.SuiteLayout.FolderOf("TerrainTool") is { } tt ? Path.Combine(tt, "Projects") : null;
        if (projects != null && Directory.Exists(projects)) dlg.InitialDirectory = projects;
        if (dlg.ShowDialog(this) == true) { ProjectFile = dlg.FileName; DialogResult = true; }
    }
}
