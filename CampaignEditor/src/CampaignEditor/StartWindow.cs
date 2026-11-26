using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;

namespace CampaignEditor;

/// <summary>
/// Finestra iniziale del Campaign Editor: a sinistra i progetti di TerrainTool (.ttproj) e i teatri installati in BMS (theater.lst),
/// a destra le campagne e le missioni TE del teatro scelto.
///   - teatro di BMS: Save scrive sul file aperto, anche nella cartella Campaign del teatro (avviso e backup in Backup\);
///   - progetto: i salvataggi vanno nella CampaignEdits del progetto (+ copia in Output\Install); in BMS solo con "Install" dell'editor.
/// Argomenti: --project &lt;file.ttproj&gt; sceglie il progetto, --open &lt;file .cam/.tac&gt; apre subito il file.
/// </summary>
public sealed class StartWindow : Window
{
    public sealed class FileRow
    {
        public string Path { get; init; } = "";
        public string File => System.IO.Path.GetFileName(Path);
        public string Kind { get; init; } = "";
        public string Units { get; init; } = "";
        public string Status { get; init; } = "";
        public string Source { get; init; } = "";
        public string Modified { get; init; } = "";
        public string Size { get; init; } = "";
        public string Notes { get; init; } = "";
        public Brush Back { get; init; } = Brushes.White;
    }

    private sealed record ProjectItem(string File);

    private readonly TextBox _data = new() { MinWidth = 420, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new() { Width = 280, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontFamily = new FontFamily("Consolas"), FontSize = 11.5 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DataGrid _files = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
        GridLinesVisibility = DataGridGridLinesVisibility.All, VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
        HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)), RowHeight = 22,
    };
    private readonly string _workRoot;
    private readonly Button _installAll = new()
    {
        Content = "Install campaign files into BMS…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 6, 2), Visibility = Visibility.Collapsed,
        ToolTip = "Copies into the theater's Campaign folder in BMS the saved campaign files that differ from BMS (TerrainTool project: Output\\Install\\<add-on>\\Campaign, " +
                  "with the object changes of Run Objects written in first). Each replaced file is kept in Backup.",
    };
    private readonly Button _applyCh = new()
    {
        Content = "Apply object changes", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 6, 2), Visibility = Visibility.Collapsed,
        ToolTip = "TerrainTool project: the objectives that the Object Editor (Run Objects) moved, turned, replaced or removed (Output\\ObjectChanges.json) " +
                  "are written into CampObjData.XML, the campaign saves (*.cam), the TE template (Te_New_Nt.tac) and stations+ils.dat (into Output\\Install; editor saves are updated too)",
    };
    private readonly Button _thr = new()
    {
        Content = "Campaign terrain (.thr)…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 6, 2), IsEnabled = false,
        ToolTip = "The theater's .thr (ground cover, relief, rail per km², still used by BMS 4.38 for move times, cover and 'near water'): view it, " +
                  "rebuild it from the New Terrain, compare it with the terrain BMS generates for the campaign",
    };
    private readonly string? _startProject;
    /// <summary>Theater chosen in the BMS Suite main menu (selected in the list when nothing else is asked).</summary>
    private readonly string? _startTheater;
    private CampaignWorkspace? _ws;

    public StartWindow(string? project = null, string? theater = null, string? bmsData = null)
    {
        Title = "Campaign Editor – choose a theater or a project";
        Width = 1200; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _workRoot = WorkRoot();
        _startProject = project;
        _startTheater = theater;
        if (project != null) AddRecent(project);
        _data.Text = bmsData ?? LastDataDir() ?? @"C:\Falcon BMS 4.38\Data";

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "BMS Data folder ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_data);
        top.Children.Add(UnitText.Btn("Browse…", Browse));
        top.Children.Add(UnitText.Btn("Reload", Reload, "Read TerrData\\TheaterDefinition\\theater.lst again"));
        top.Children.Add(UnitText.Btn("Add TerrainTool project…", BrowseProject, "A .ttproj file of TerrainTool: campaign files of its add-on, unit conversion and Install"));

        void C(string h, string p, double w) => _files.Columns.Add(new DataGridTextColumn
            { Header = h, Binding = new Binding(p), Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
        C("File", nameof(FileRow.File), 230); C("Kind", nameof(FileRow.Kind), 85); C("Units", nameof(FileRow.Units), 55);
        C("Unit types", nameof(FileRow.Status), 150); C("Source", nameof(FileRow.Source), 200);
        C("Modified", nameof(FileRow.Modified), 115); C("Size", nameof(FileRow.Size), 70); C("Notes", nameof(FileRow.Notes), 0);
        var rs = new Style(typeof(DataGridRow)); rs.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(FileRow.Back)))); _files.RowStyle = rs;
        _files.MouseDoubleClick += (_, _) => OpenSelected();

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var open = UnitText.Btn("Open in the campaign editor", OpenSelected, "Double-click a file does the same");
        open.FontWeight = FontWeights.SemiBold;
        buttons.Children.Add(open);
        buttons.Children.Add(UnitText.Btn("Empty editor", () => OpenEditor(null), "Then Open… a .cam / .tac"));
        buttons.Children.Add(UnitText.Btn("Saved files folder", () =>
        {
            if (_ws == null) return;
            Directory.CreateDirectory(_ws.EditsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + _ws.EditsFolder + "\"") { UseShellExecute = true });
        }));
        buttons.Children.Add(UnitText.Btn("Refresh", FillFiles));
        buttons.Children.Add(UnitText.Btn("Backups…", () => new BackupWindow { Owner = this }.Show(), "Files replaced by the editor, kept in " + BackupStore.Root + ": restore them"));
        _applyCh.Click += (_, _) => ApplyObjectChanges();
        _installAll.Click += (_, _) => InstallProjectCampaign();
        buttons.Children.Add(_applyCh);
        buttons.Children.Add(_installAll);
        _thr.Click += (_, _) => { if (_ws != null) new TheaterCellsWindow(_ws) { Owner = this }.Show(); };
        buttons.Children.Add(_thr);
        buttons.Children.Add(UnitText.Btn("TE lists…", () =>
        {
            var ws = _ws ?? MakeWorkspace();
            if (ws == null) { MessageBox.Show(this, "Choose a project or a theater on the left.", Title); return; }
            try { new TeListsWindow(ws, CampaignDb.Load(ws.DbDirs, compiledInFirstDir: ws.BmsTheater != null)) { Owner = this }.Show(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TE lists", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }, "TePlanes.lst, TeUnits.lst, TeNavalUnits.lst: the aircraft, battalions and naval units offered in the Tactical Engagement (also in the editor's toolbar)"));
        buttons.Children.Add(UnitText.Btn("Remove project from the list", RemoveProject, "Only from this list: no file is deleted"));

        var right = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top); right.Children.Add(_info);
        DockPanel.SetDock(_note, Dock.Bottom); right.Children.Add(_note);
        DockPanel.SetDock(buttons, Dock.Bottom); right.Children.Add(buttons);
        right.Children.Add(_files);
        var body = new DockPanel();
        DockPanel.SetDock(_list, Dock.Left); body.Children.Add(_list);
        body.Children.Add(right);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(body);
        Content = root;

        _list.SelectionChanged += (_, _) => FillFiles();
        Loaded += (_, _) => Reload();
    }

    /// <summary>&lt;cartella del Campaign Editor&gt;\Theaters (quella con build.bat), altrimenti Documenti\BMS Campaign Editor\Theaters.</summary>
    public static string WorkRoot()
    {
        if (global::TerrainTool.Core.SuiteLayout.ProgramFolder() is { } pf) return Path.Combine(pf, "Theaters");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS Campaign Editor", "Theaters");
    }

    private string SettingsFile => Path.Combine(_workRoot, "bms_data_folder.txt");
    private string RecentFile => Path.Combine(_workRoot, "projects.txt");

    private string? LastDataDir()
    {
        try { return File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile).Trim() is { Length: > 0 } s && Directory.Exists(s) ? s : null : null; }
        catch (IOException) { return null; }
    }

    private List<string> Recent()
    {
        try { return File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Select(l => TheaterProject.FindMoved(l.Trim())).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new(); }
        catch (IOException) { return new(); }
    }

    private void AddRecent(string file)
    {
        try
        {
            var l = Recent(); l.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase)); l.Insert(0, Path.GetFullPath(file));
            Directory.CreateDirectory(_workRoot); File.WriteAllLines(RecentFile, l.Take(10));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void RemoveProject()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not ProjectItem p) { MessageBox.Show(this, "Select a project in the list.", Title); return; }
        try { var l = Recent(); l.RemoveAll(x => x.Equals(p.File, StringComparison.OrdinalIgnoreCase)); File.WriteAllLines(RecentFile, l); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        Reload();
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "BMS Data folder (the one with TerrData, Campaign, Add-On …)" };
        if (Directory.Exists(_data.Text)) dlg.InitialDirectory = _data.Text;
        if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; Reload(); }
    }

    private void BrowseProject()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "TerrainTool project", Filter = "TerrainTool project (*.ttproj)|*.ttproj" };
        if (dlg.ShowDialog(this) != true) return;
        AddRecent(dlg.FileName);
        Reload(dlg.FileName);
    }

    private static TheaterProject LoadProject(string file)
    {
        var p = TheaterProject.Load(file);
        if (string.IsNullOrWhiteSpace(p.ProjectFolder)) p.ProjectFolder = Path.GetDirectoryName(file) ?? "";
        return p;
    }

    private static UIElement Header(string text) =>
        new TextBlock { Text = text, FontWeight = FontWeights.Bold, Foreground = Brushes.SteelBlue, Margin = new Thickness(2, 6, 2, 2) };

    private void Reload() => Reload(_startProject);

    private void Reload(string? select)
    {
        _list.Items.Clear();
        _files.ItemsSource = null;
        string dir = _data.Text.Trim();
        var projects = Recent().Where(File.Exists).ToList();
        if (projects.Count > 0)
        {
            _list.Items.Add(new ListBoxItem { Content = Header("TerrainTool projects"), IsEnabled = false });
            foreach (var f in projects)
            {
                var sp = new StackPanel { Margin = new Thickness(2) };
                sp.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(f), FontWeight = FontWeights.SemiBold });
                sp.Children.Add(new TextBlock { Text = "project · " + Path.GetDirectoryName(f), Foreground = Brushes.Gray, FontSize = 11 });
                _list.Items.Add(new ListBoxItem { Content = sp, Tag = new ProjectItem(f), ToolTip = f });
            }
        }
        var list = Directory.Exists(dir) ? BmsTheater.List(dir) : new List<BmsTheater>();
        if (list.Count > 0)
        {
            try { Directory.CreateDirectory(_workRoot); File.WriteAllText(SettingsFile, dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _list.Items.Add(new ListBoxItem { Content = Header("BMS theaters"), IsEnabled = false });
            foreach (var t in list)
            {
                var sp = new StackPanel { Margin = new Thickness(2) };
                sp.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold });
                sp.Children.Add(new TextBlock { Text = t.Subtitle, Foreground = Brushes.Gray, FontSize = 11 });
                _list.Items.Add(new ListBoxItem { Content = sp, Tag = t, ToolTip = t.Tdf });
            }
        }
        else _info.Text = $"No BMS theaters found: {Path.Combine(dir, "TerrData", "TheaterDefinition", "theater.lst")} is missing or empty. Choose the Data folder of BMS (e.g. C:\\Falcon BMS 4.38\\Data).";

        var first = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => select != null && i.Tag is ProjectItem p && p.File.Equals(Path.GetFullPath(select), StringComparison.OrdinalIgnoreCase))
                    ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => select == null && _startTheater != null && i.Tag is BmsTheater t && t.Name.Equals(_startTheater, StringComparison.OrdinalIgnoreCase))
                    ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag != null);
        if (first != null) _list.SelectedItem = first;
    }

    private CampaignWorkspace? MakeWorkspace()
    {
        var tag = (_list.SelectedItem as ListBoxItem)?.Tag;
        try
        {
            return tag switch
            {
                ProjectItem p => CampaignWorkspace.FromProject(LoadProject(p.File)),
                BmsTheater t => CampaignWorkspace.FromTheater(t, _workRoot),
                _ => null,
            };
        }
        catch (Exception ex) { _info.Text = "Cannot read it: " + ex.Message; return null; }
    }

    private void FillFiles()
    {
        _files.ItemsSource = null;
        _ws = MakeWorkspace();
        _applyCh.Visibility = _ws?.IsProject == true ? Visibility.Visible : Visibility.Collapsed;
        // installed theater: only when it has its own Campaign folder (BMS's own Data\Campaign is never written)
        _installAll.Visibility = _ws?.IsProject == true || (_ws?.BmsTheater is { } bt && !SameDir(bt.CampaignDir, Path.Combine(bt.DataDir, "Campaign"))) ? Visibility.Visible : Visibility.Collapsed;
        _thr.IsEnabled = _ws != null;
        if (_ws == null) return;
        var ws = _ws;
        string Exists(string d) => Directory.Exists(d) ? d : d + "   (MISSING)";
        string map = ws.TheaterKnown
            ? $"{ws.Theater.SizeKm} km, centre {ws.Theater.CenterLat.ToString("0.###", CultureInfo.InvariantCulture)} / {ws.Theater.CenterLon.ToString("0.###", CultureInfo.InvariantCulture)}" + (ws.PhotoDir != null ? ", satellite texture" : "")
            : "no Theater.txt (classic terrain): 1024 km assumed, latitude/longitude not reliable";
        if (ws.BmsTheater is { } t)
        {
            _info.Text =
                $"Campaign   {Exists(t.CampaignDir)}\n" +
                $"Database   {Exists(t.ObjectDir)}{(File.Exists(Path.Combine(t.ObjectDir, "Falcon4_CT.xml")) ? "" : CampaignDb.IsCompiledOnly(t.ObjectDir) ? "   (compiled FALCON4.* files, read only)" : "   (no Falcon4_CT.xml: BMS's own is used)")}\n" +
                $"3D models  {Exists(t.ModelsDir)}\n" +
                $"Map        {map}\n" +
                $"Saves to   {ws.EditsFolder}";
            _note.Text = "Each theater uses its own database (the objectdir of its .tdf: classes, units, vehicles, weapons, squadron stores) and its own 3D models. " +
                         "Save writes into the file you opened, also in the theater's Campaign folder (you are asked once, and the old file is kept in " + BackupStore.Root +
                         ": see 'Backups…'). Back up the Campaign folder before editing. Green = copies saved by earlier versions of the editor in " + _workRoot + ".";
        }
        else if (ws.Project is { } p)
        {
            _info.Text =
                $"Project    {p.ProjectFolder}\n" +
                $"Add-on     {Exists(p.OldTheaterFolder)}\n" +
                $"Database   {string.Join("  ·  ", ws.DbDirs.Where(Directory.Exists).Take(2))}\n" +
                $"Map        {map}\n" +
                $"Saves to   {ws.EditsFolder}  (+ copy in {ws.InstallCopyDir})";
            _note.Text = "Order: 1) Convert unit types (once per file: the old add-on's units use its own database), 2) edit, Validate, Save (into the project's CampaignEdits, never into BMS), " +
                         "3) 'Install into BMS…' in the editor copies the file into the add-on (with a backup). Orange = units still to convert, green = editor save.";
        }

        var done = ws.IsProject ? UnitTypeConverter.LoadDone(ws.EditsFolder) : new HashSet<string>();
        var rows = new List<FileRow>();
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            foreach (var dir in ws.SearchDirs().Where(Directory.Exists))
            {
                string src = dir == ws.EditsFolder ? (ws.IsProject ? "editor save" : "saved copy")
                    : dir == ws.InstallCopyDir ? "Output\\Install"
                    : ws.IsProject ? "old add-on" : "BMS Campaign folder";
                foreach (var path in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    string n = Path.GetFileName(path);
                    if (rows.Any(r => r.File.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;   // la prima copia vince (come quando si apre)
                    var fi = new FileInfo(path);
                    int units = -1, ver = 0; string notes = "";
                    try
                    {
                        var arc = CampaignArchive.Read(path);
                        var uni = arc.FirstOrDefault(a => a.Name.EndsWith(".uni", StringComparison.OrdinalIgnoreCase)).Data;
                        units = uni != null && uni.Length >= 10 ? BitConverter.ToUInt16(uni, 4) : 0;
                        var v = arc.FirstOrDefault(a => a.Name.EndsWith(".ver", StringComparison.OrdinalIgnoreCase)).Data;
                        if (v != null) ver = UniFile.ReadVersion(v);
                        if (!arc.Any(a => a.Name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))) notes = "objectives from the scenario";
                    }
                    catch (Exception ex) { notes = "unreadable: " + ex.Message; }
                    bool edited = dir == ws.EditsFolder;
                    bool conv = ws.IsProject && units > 0 && !done.Contains(n);
                    rows.Add(new FileRow
                    {
                        Path = path,
                        Kind = n.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) ? "Campaign" : "TE mission",
                        Units = units < 0 ? "?" : units.ToString(CultureInfo.InvariantCulture),
                        Status = !ws.IsProject || units < 0 ? "" : units == 0 ? "no units" : done.Contains(n) ? "converted (BMS 4.38)" : "old add-on: convert",
                        Source = src,
                        Modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        Size = (fi.Length / 1024.0).ToString("N0", CultureInfo.InvariantCulture) + " KB",
                        Notes = (ver > 0 ? $"version {ver}" : "") + (notes.Length > 0 ? (ver > 0 ? " · " : "") + notes : ""),
                        Back = edited ? new SolidColorBrush(Color.FromRgb(215, 240, 215)) : conv ? new SolidColorBrush(Color.FromRgb(255, 244, 224)) : Brushes.White,
                    });
                }
            }
        }
        finally { Cursor = null; }
        _files.ItemsSource = rows;
    }

    /// <summary>Progetto: applica Output\ObjectChanges.json (scritto da Run Objects nell'Object Editor) ai file della campagna.</summary>
    private void ApplyObjectChanges()
    {
        if (_ws?.Project is not { } p || _ws.InstallCopyDir is not { } campOut) return;
        string f = ObjectChangeSet.PathFor(p);
        var set = ObjectChangeSet.Load(f);
        var net = NetworkEdits.Load(NetworkEdits.PathFor(p));
        if (set == null && net.IsEmpty) { MessageBox.Show(this, $"No object changes found ({f}): they come from Run Objects (Objective Studio), which also installs them.", Title); return; }
        string what = (set != null ? $"Object changes of {set.Created:yyyy-MM-dd HH:mm}: {set.Objectives.Count} objectives, {set.RemovedBases.Count} removed bases.\n" : "") +
                      (!net.IsEmpty ? $"Objective network (Object Editor): {net.Added.Count} new objectives, {net.Links.Count} link changes.\n" : "");
        set ??= new ObjectChangeSet();
        if (MessageBox.Show(this, what + "\n" +
                $"Write them into CampObjData.XML, the campaign saves (*.cam), Te_New_Nt.tac and stations+ils.dat?\n" +
                $"Output: {campOut}\n(the editor's saves in CampaignEdits are updated too; their current versions go into Backup)\n\nBMS is not touched: then use 'Install campaign files into BMS…'.",
                "Apply object changes", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            string edits = ObjectsJob.CampaignEditsFolder(p);
            if (Directory.Exists(edits))
                foreach (var x in Directory.EnumerateFiles(edits).Where(x => x.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)))
                    BackupStore.Save(x);
            var notes = ObjectChangeApplier.Apply(set, p, campOut, net);
            TerrainTool.Core.Campaign.ProjectCampaign.MarkApplied(p);
            FillFiles();
            MessageBox.Show(this, string.Join("\n", notes), "Apply object changes");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Apply object changes", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = null; }
    }

    /// <summary>
    /// Progetto: i file di Output\Install\&lt;add-on&gt;\Campaign (salvataggi dell'editor e file riscritti da Objects) che sono diversi
    /// da quelli dell'add-on in BMS vengono copiati nella sua cartella Campaign, ognuno con il backup del file sostituito.
    /// </summary>
    private void InstallProjectCampaign()
    {
        if (_ws?.Project == null && _ws?.BmsTheater is { } th) { InstallTheaterCampaign(th); return; }
        if (_ws?.Project is not { } p || _ws.InstallCopyDir is not { } src) return;
        // the object changes of Run Objects not yet in the campaign files: written first
        if (TerrainTool.Core.Campaign.ProjectCampaign.HasPending(p))
        {
            if (MessageBox.Show(this, "Run Objects changed objectives (moved, turned, replaced or removed) after the campaign files were last updated.\n\n" +
                    "The campaign files of the project (CampObjData.XML, *.cam, Te_New_Nt.tac, stations+ils.dat) must have those changes before they are installed. " +
                    "Nothing is copied into BMS at this step: the next window lists the files and asks again.\n\n" +
                    "Write the changes into the project's campaign files now?",
                    "Install campaign files into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                string bak = Path.Combine(BackupStore.Root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_objectchanges");
                TerrainTool.Core.Campaign.ProjectCampaign.ApplyPending(p, bak);
                FillFiles();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Apply object changes", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            finally { Cursor = null; }
        }
        if (!Directory.Exists(src)) { MessageBox.Show(this, "No campaign file saved yet in this project.", Title); return; }
        string? target = ProjectInstall.TargetCampaign(p);
        if (target == null) { MessageBox.Show(this, "The add-on's Campaign folder was not found in BMS.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var todo = ProjectInstall.Changed(src, target);
        if (todo.Count == 0) { MessageBox.Show(this, "The add-on's Campaign folder in BMS already has the same files.", Title); return; }
        string list = string.Join("\n", todo.Take(25).Select(f => "  " + Path.GetFileName(f) + (File.Exists(Path.Combine(target, Path.GetFileName(f))) ? "" : "  (new)")))
                      + (todo.Count > 25 ? $"\n  … and {todo.Count - 25} more" : "");
        if (MessageBox.Show(this, $"Copy {todo.Count} file(s) from\n{src}\ninto\n{target}?\n\n{list}\n\nEach replaced file is kept in {BackupStore.Root} ('Backups…').",
                "Install campaign files into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int n = 0;
        try
        {
            foreach (var f in todo)
            {
                string dst = Path.Combine(target, Path.GetFileName(f));
                string from = f;
                // aircraft the 4.37 Units Converter added to the installed TE list stay in it
                if (Path.GetFileName(f).Equals("TePlanes.lst", StringComparison.OrdinalIgnoreCase) && Directory.GetParent(target) is { } addOn)
                {
                    string merged = Path.Combine(Path.GetTempPath(), "TePlanes_merged.lst");
                    int kept = TerrainTool.Core.Database.UnitsConversion.KeepConvertedTeLines(f, dst, Path.Combine(addOn.FullName, "Terrdata", ObjectsJob.FolderName), merged);
                    if (kept > 0) from = merged;
                }
                BackupStore.Save(dst);
                File.Copy(from, dst, true); n++;
            }
            MessageBox.Show(this, $"Installed {n} file(s) into {target}.\nCreate a NEW TE scenario if bases were moved.", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, $"After {n} file(s): {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installed theater: the campaign files saved by the editor (its work folder) that differ from the theater's Campaign
    /// folder are copied there, each replaced file kept in Backup. BMS's own Data\Campaign is never written.
    /// </summary>
    private void InstallTheaterCampaign(BmsTheater th)
    {
        string src = _ws!.EditsFolder, target = th.CampaignDir;
        if (SameDir(target, Path.Combine(th.DataDir, "Campaign"))) { MessageBox.Show(this, "This theater uses BMS's own Campaign folder, which is not changed.", Title); return; }
        if (!Directory.Exists(src) || !Directory.Exists(target)) { MessageBox.Show(this, "No campaign file saved yet for this theater.", Title); return; }
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var todo = ProjectInstall.Changed(src, target).Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)).ToList();
        if (todo.Count == 0) { MessageBox.Show(this, "The theater's Campaign folder in BMS already has the same files.", Title); return; }
        string list = string.Join("\n", todo.Take(25).Select(f => "  " + Path.GetFileName(f) + (File.Exists(Path.Combine(target, Path.GetFileName(f))) ? "" : "  (new)")))
                      + (todo.Count > 25 ? $"\n  … and {todo.Count - 25} more" : "");
        if (MessageBox.Show(this, $"Copy {todo.Count} file(s) from\n{src}\ninto\n{target}?\n\n{list}\n\nEach replaced file is kept in {BackupStore.Root} ('Backups…').",
                "Install campaign files into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int n = 0;
        try
        {
            foreach (var f in todo)
            {
                string dst = Path.Combine(target, Path.GetFileName(f)), from = f;
                if (Path.GetFileName(f).Equals("TePlanes.lst", StringComparison.OrdinalIgnoreCase))
                {
                    string merged = Path.Combine(Path.GetTempPath(), "TePlanes_merged.lst");
                    if (TerrainTool.Core.Database.UnitsConversion.KeepConvertedTeLines(f, dst, th.ObjectDir, merged) > 0) from = merged;
                }
                BackupStore.Save(dst);
                File.Copy(from, dst, true); n++;
            }
            MessageBox.Show(this, $"Installed {n} file(s) into {target}.", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, $"After {n} file(s): {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenSelected()
    {
        if (_files.SelectedItem is FileRow r) OpenEditor(r.Path);
        else MessageBox.Show(this, "Choose a campaign or TE file in the list.", Title);
    }

    /// <summary>Apre l'editor sul file (o vuoto). Usato anche da --open.</summary>
    public void OpenEditor(string? path)
    {
        var ws = MakeWorkspace();
        if (ws == null) { MessageBox.Show(this, "Choose a project or a theater on the left.", Title); return; }
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            var w = new CampaignEditorWindow(ws, path);
            w.Closed += (_, _) => FillFiles();
            w.Show();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Campaign editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = null; }
    }
}
