using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Sim;
using TerrainTool.Core.Project;

namespace SimEditor;

/// <summary>
/// Sim Editor (was TerrainTool, tab 7 · Sim &amp; SAM): the theater's own Sim (full copy of BMS Data\Sim + the project's SimOverrides,
/// made by Build theater into NT\Sim), the database changes (DbEdits.txt, applied by Run Objects on top of BMS's database)
/// and the check of the air defence systems: launchers, missiles, rate of fire (Sim radar data) and their positions on the
/// objectives of the project's campaigns.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _projectFile;
    private readonly TheaterProject _p;
    private readonly TextBlock _simStatus = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _dbEdits = new()
    {
        FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly DataGrid _samGrid = Grid0(DataGridSelectionMode.Single);
    private readonly DataGrid _placeGrid = Grid0(DataGridSelectionMode.Extended);
    private readonly CheckBox _allTypes = new() { Content = "All SAM types", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Also the battalion types not used in the project's campaigns" };
    private readonly CheckBox _problemsOnly = new() { Content = "Only problems", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _check = Btn("Check SAM systems & positions", bold: true);
    private readonly Button _install = Btn("Install theater Sim into BMS…");
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Height = 120, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private List<SamDb.SamSystem> _systems = new();
    private List<SamPlacement> _places = new();
    private bool _busy;
    private SimFilesView? _files;

    private static DataGrid Grid0(DataGridSelectionMode m) => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = m,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static Button Btn(string t, bool bold = false) => new() { Content = t, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    private string AddOnName => new DirectoryInfo(_p.OldTheaterFolder).Name;
    private string SimInstallDir => Path.Combine(_p.ProjectFolder, "Output", "Install", AddOnName, TheaterSimData.SimRel);
    private string? BmsSimDir => AssembleJob.FindDataDir(_p.KoreaNewTerrain) is { } d ? Path.Combine(d, "Sim") : null;

    public MainWindow(string projectFile)
    {
        _projectFile = projectFile;
        _p = TheaterProject.Load(projectFile);
        Title = $"Sim Editor – {_p.Theater.Name}";
        Width = 1500; Height = 900; MinWidth = 1000; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── left: theater Sim and the SAM check
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _check.ToolTip = "Reads the theater database (with DbEdits.txt applied, also the changes not saved yet), the Sim radar data and every campaign / TE of the project, and checks that each air defence battalion fits in the point list BMS gives it on its objective.";
        _install.ToolTip = @"Copies Output\Install\<add-on>\NT\Sim (made by 'Build theater' = full copy of BMS Data\Sim + the SimOverrides folder) into BMS\Data\<add-on>\NT\Sim and sets 'simdatadir' in the _NT.tdf (asks first). Replaced files and the .tdf are saved in Output\BmsBackup\<date>. BMS Data\Sim is never touched.";
        var copy = Btn("Copy a BMS Sim file to SimOverrides…");
        copy.ToolTip = @"Pick a file of BMS Data\Sim (e.g. Misdata\SA-2F.dat, Radar\Fan_Song_F.dat): it is copied into the project's SimOverrides folder with the same path, ready to be edited.";
        var openOv = Btn("Open SimOverrides");
        bar.Children.Add(_check); bar.Children.Add(_install); bar.Children.Add(copy); bar.Children.Add(openOv);
        bar.Children.Add(_allTypes); bar.Children.Add(_problemsOnly);

        void Col(DataGrid g, string h, string path, double w, bool star = false)
            => g.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(path), Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), MinWidth = star ? w : 20 });
        Col(_samGrid, "UCD", "Ucd", 45);
        Col(_samGrid, "Battalion type", "Name", 200);
        Col(_samGrid, "Launchers", "Launchers", 65);
        Col(_samGrid, "Missiles each", "MissilesEach", 80);
        Col(_samGrid, "Missile", "Missile", 110);
        Col(_samGrid, "Radars", "Radars", 160);
        Col(_samGrid, "Rate of fire (Sim radar)", "Fire", 260);
        Col(_samGrid, "Vehicles → points needed", "Needs", 230);
        Col(_samGrid, "Battalions", "Battalions", 65);
        Col(_samGrid, "Positions", "Worst", 160, star: true);
        Col(_placeGrid, "Campaign", "File", 130);
        Col(_placeGrid, "Team", "Team", 45);
        Col(_placeGrid, "Battalion", "Unit", 150);
        Col(_placeGrid, "Objective", "Objective", 230);
        Col(_placeGrid, "OCD", "Ocd", 50);
        Col(_placeGrid, "Point list", "List", 300);
        Col(_placeGrid, "Result", "Result", 160, star: true);

        var grids = new Grid();
        grids.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grids.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        grids.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_samGrid, 0); grids.Children.Add(_samGrid);
        var split = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetRow(split, 1); grids.Children.Add(split);
        Grid.SetRow(_placeGrid, 2); grids.Children.Add(_placeGrid);
        var how = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Text = "How BMS places an air defence battalion on an objective: it takes ONE free point list of the objective's layout (SAM list, or AAA list for gun units; one battalion per list, preferring a list with at least as many points as vehicles), then puts each vehicle on a point of its kind (SAM launcher, radar, AAA, support, infantry) inside that list; when those run out, on any other point left in the list; when the list is full, the vehicle is not on the base points. More launchers than SAM points still fit while the list has free points of other kinds.",
        };
        var left = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); left.Children.Add(bar);
        DockPanel.SetDock(_simStatus, Dock.Top); left.Children.Add(_simStatus);
        DockPanel.SetDock(how, Dock.Bottom); left.Children.Add(how);
        left.Children.Add(grids);

        // ── right: DbEdits.txt
        var right = new DockPanel();
        var rh = new TextBlock { Text = "Database changes (DbEdits.txt)", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        var rn = new TextBlock
        {
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "One change per line: TABLE Num Field Value  # comment   (e.g. UCD 161 ElementCount_1 3), or COPY TABLE From To for a new record (To = the next free number). " +
                   "Applied by 'Run Objects' (Objective Studio) on top of BMS's database, so they survive BMS updates; the SAM check on the left uses them right away. " +
                   "To get them into BMS: Run Objects, Build theater (TerrainTool), then Install objects.",
        };
        var save = Btn("Save", bold: true); var reload = Btn("Reload");
        var rb = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        rb.Children.Add(save); rb.Children.Add(reload);
        DockPanel.SetDock(rh, Dock.Top); right.Children.Add(rh);
        DockPanel.SetDock(rn, Dock.Top); right.Children.Add(rn);
        DockPanel.SetDock(rb, Dock.Bottom); right.Children.Add(rb);
        right.Children.Add(_dbEdits);

        var body = new Grid { Margin = new Thickness(10) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 500 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440), MinWidth = 260 });
        Grid.SetColumn(left, 0); body.Children.Add(left);
        var vs = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(vs, 1); body.Children.Add(vs);
        Grid.SetColumn(right, 2); body.Children.Add(right);

        var top = new DockPanel { Margin = new Thickness(10, 8, 10, 0) };
        var proj = new TextBlock { Text = $"Project: {_projectFile}", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var other = Btn("Open another project…");
        other.Click += (_, _) => OpenOther();
        DockPanel.SetDock(other, Dock.Right); top.Children.Add(other);
        top.Children.Add(proj);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_log, Dock.Bottom); _log.Margin = new Thickness(10, 0, 10, 10); root.Children.Add(_log);
        var tabs = new TabControl { Margin = new Thickness(6, 4, 6, 4) };
        if (BmsSimDir is { } bmsSim && Directory.Exists(bmsSim))
        {
            _files = new SimFilesView(new TerrainTool.Core.Sim.SimLibrary(bmsSim, TheaterSimData.SimOverridesFolder(_p)), s => { Log(s); RefreshSim(); });
            tabs.Items.Add(new TabItem { Header = "Sim files", Content = _files });
        }
        else tabs.Items.Add(new TabItem { Header = "Sim files", Content = new TextBlock { Margin = new Thickness(12), Text = "BMS Data\\Sim not found: set 'Korea NewTerrain' in TerrainTool (tab 1) to the Korea of your BMS 4.38." } });
        tabs.Items.Add(new TabItem { Header = "SAM check & database changes", Content = body });
        root.Children.Add(tabs);
        Content = root;

        _check.Click += async (_, _) => await SamCheckAsync();
        _install.Click += async (_, _) => await InstallSimAsync();
        copy.Click += (_, _) => CopySimFile();
        openOv.Click += (_, _) => { Directory.CreateDirectory(TheaterSimData.SimOverridesFolder(_p)); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TheaterSimData.SimOverridesFolder(_p)}\"") { UseShellExecute = true }); RefreshSim(); };
        save.Click += (_, _) => SaveDbEdits();
        reload.Click += (_, _) => LoadDbEdits();
        _allTypes.Click += (_, _) => ApplyFilter();
        _problemsOnly.Click += (_, _) => ShowPlaces();
        _samGrid.SelectionChanged += (_, _) => ShowPlaces();
        Closing += (_, e) =>
        {
            if (_files != null && !_files.AskLeave()) { e.Cancel = true; return; }
            if (DbEditsChanged() && MessageBox.Show(this, "DbEdits.txt has changes that are not saved. Close anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        RefreshSim();
        LoadDbEdits();
        Log($"Project {_p.Theater.Name}: {_p.ProjectFolder}");
    }

    private void Log(string s) { _log.AppendText($"{DateTime.Now:HH:mm:ss} {s}\n"); _log.ScrollToEnd(); }

    private void OpenOther()
    {
        var start = new StartWindow { Owner = this };
        if (start.ShowDialog() != true || start.ProjectFile == null) return;
        StartWindow.AddRecent(start.ProjectFile);
        var w = new MainWindow(start.ProjectFile);
        Application.Current.MainWindow = w;
        w.Show();
        Close();
    }

    private void RefreshSim()
    {
        string ov = TheaterSimData.SimOverridesFolder(_p);
        var sb = new StringBuilder();
        if (!Directory.Exists(ov)) sb.Append("Theater Sim: OFF (no SimOverrides folder: the theater uses the shared BMS Data\\Sim). 'Copy a BMS Sim file…' turns it on.");
        else
        {
            var files = Directory.EnumerateFiles(ov, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(ov, f))
                .Where(f => !f.StartsWith("README", StringComparison.OrdinalIgnoreCase)).ToList();
            sb.Append($"Theater Sim: ON → <add-on>\\{TheaterSimData.SimRel} = BMS Data\\Sim + {files.Count} SimOverrides file(s): {string.Join(", ", files.DefaultIfEmpty("none"))}");
            sb.Append(Directory.Exists(SimInstallDir) ? $"\nOutput copy: {SimInstallDir} (run 'Build theater' in TerrainTool after changing SimOverrides)" : "\nNot assembled yet: run 'Build theater' (TerrainTool, tab 8), then 'Install theater Sim into BMS…'");
        }
        _simStatus.Text = sb.ToString();
    }

    // ───────────────────────── DbEdits.txt

    private string _dbEditsLoaded = "";
    private bool DbEditsChanged() => _dbEdits.Text != _dbEditsLoaded;

    private void LoadDbEdits()
    {
        string f = TheaterSimData.DbEditsFile(_p);
        _dbEdits.Text = _dbEditsLoaded = File.Exists(f) ? File.ReadAllText(f) : "# DbEdits.txt - TABLE Num Field Value   # comment\r\n";
    }

    private void SaveDbEdits()
    {
        string f = TheaterSimData.DbEditsFile(_p);
        string tmp = Path.Combine(Path.GetTempPath(), "DbEdits_check.txt");
        try
        {
            File.WriteAllText(tmp, _dbEdits.Text);
            int n = TheaterSimData.ParseDbEdits(tmp).Count;     // a format error: not saved
            File.WriteAllText(f, _dbEdits.Text, new UTF8Encoding(false));
            _dbEditsLoaded = _dbEdits.Text;
            Log($"DbEdits.txt saved ({n} changes). Run Objects (Objective Studio) + Build theater (TerrainTool) and install the objects to get them into BMS.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "DbEdits.txt", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ───────────────────────── Sim files

    private void CopySimFile()
    {
        string? sim = BmsSimDir;
        if (sim == null || !Directory.Exists(sim)) { Log("BMS Data\\Sim not found (set 'Korea NewTerrain' in TerrainTool, tab 1)"); return; }
        var dlg = new OpenFileDialog { InitialDirectory = sim, Filter = "Sim data (*.dat;*.lst;*.txt)|*.dat;*.lst;*.txt|All files|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        string ov = TheaterSimData.SimOverridesFolder(_p);
        foreach (var f in dlg.FileNames)
        {
            string full = Path.GetFullPath(f), root = Path.GetFullPath(sim).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Log($"{f} is not inside {sim}"); continue; }
            if (SimLibrary.IsProtected(full[root.Length..])) { Log($"{full[root.Length..]}: flight models of flyable aircraft (and AcTypes.lst) are not copied nor edited: they stay BMS's"); continue; }
            string to = Path.Combine(ov, full[root.Length..]);
            if (File.Exists(to) && MessageBox.Show(this, $"{to}\nalready exists (it may contain your changes). Replace it with the BMS file?", "SimOverrides",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(full, to, true);
            Log($"{full[root.Length..]} copied to SimOverrides (edit it there, then 'Build theater' and 'Install theater Sim into BMS…')");
        }
        RefreshSim();
    }

    private async Task InstallSimAsync()
    {
        var inst = new BmsInstaller(_p, objects: false, photoreal: false, water: false, art: false, sim: true);
        if (inst.Problems.Count > 0)
        {
            foreach (var pr in inst.Problems) Log("Install into BMS: " + pr);
            MessageBox.Show(this, string.Join("\n", inst.Problems), "Install into BMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, inst.Plan() + "\n\nProceed?", "Install into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _install.IsEnabled = false;
        Log("Install into BMS: started → " + inst.Target);
        try { await Task.Run(() => inst.Run(l => Dispatcher.Invoke(() => Log(l)))); }
        catch (Exception ex) { Log("Install into BMS: ERROR " + ex.Message); }
        finally { _install.IsEnabled = true; }
    }

    // ───────────────────────── SAM check

    private async Task SamCheckAsync()
    {
        if (_busy) return;
        _busy = true; _check.IsEnabled = false;
        // the changes of DbEdits.txt not saved yet count for the check
        string edits = Path.Combine(Path.GetTempPath(), "DbEdits_check.txt");
        File.WriteAllText(edits, _dbEdits.Text);
        try
        {
            var p = _p;
            string simDir = Directory.Exists(SimInstallDir) ? SimInstallDir : BmsSimDir ?? "";
            var (systems, places, notes) = await Task.Run(() =>
            {
                var ws = CampaignWorkspace.FromProject(p);
                var db = CampaignDb.Load(ws.DbDirs);
                var s = SamDb.Load(ws.DbDirs, simDir, edits);
                var nts = new List<string>(s.Notes);
                // each campaign / TE once, from the first folder that has it (edits, Install, originals)
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in ws.SearchDirs().Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(d, "*.cam").Concat(Directory.EnumerateFiles(d, "*.tac")))
                        files.TryAdd(Path.GetFileName(f), f);
                var pl = new List<SamPlacement>();
                foreach (var f in files.Values.OrderBy(x => Path.GetFileName(x)))
                {
                    try { pl.AddRange(SamCheck.Check(f, db, s)); }
                    catch (Exception ex) { nts.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                var sys = s.Nums("UCD").Select(s.SystemOf).Where(x => x != null).Select(x => x!).ToList();
                foreach (var x in sys)
                {
                    var mine = pl.Where(q => q.Ucd == x.Ucd).ToList();
                    x.Battalions = mine.Count;
                    int bad = mine.Count(q => q.Problem), fb = mine.Count(q => q.Fallback > 0 && !q.Problem);
                    x.Worst = mine.Count == 0 ? "" : bad > 0 ? $"{bad} of {mine.Count} with vehicles off the points" : fb > 0 ? $"all fit ({fb} use points of another type)" : "all fit";
                }
                return (sys, pl, nts);
            });
            _systems = systems; _places = places;
            foreach (var n in notes) Log(n);
            int bad = places.Count(q => q.Problem && systems.Any(x => x.Ucd == q.Ucd));
            Log($"{systems.Count(x => x.Battalions > 0)} SAM battalion types in the campaigns, {places.Count(q => systems.Any(x => x.Ucd == q.Ucd))} battalions, {bad} with vehicles off the base points");
            ApplyFilter();
        }
        catch (Exception ex) { Log("SAM check: ERROR " + ex.Message); }
        finally { _busy = false; _check.IsEnabled = true; }
    }

    private void ApplyFilter()
    {
        bool all = _allTypes.IsChecked == true;
        _samGrid.ItemsSource = _systems.Where(x => all || x.Battalions > 0).OrderByDescending(x => x.Battalions).ThenBy(x => x.Missile).ToList();
        ShowPlaces();
    }

    private void ShowPlaces()
    {
        bool only = _problemsOnly.IsChecked == true;
        var sel = _samGrid.SelectedItem as SamDb.SamSystem;
        var ucds = sel != null ? new HashSet<int> { sel.Ucd } : _systems.Select(x => x.Ucd).ToHashSet();
        _placeGrid.ItemsSource = _places.Where(q => ucds.Contains(q.Ucd) && (!only || q.Problem)).ToList();
    }
}
