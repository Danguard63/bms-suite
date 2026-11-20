using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace ObjectiveStudio;

/// <summary>
/// Objective Studio: what is inside the objectives of a theater (layouts, pieces, airbases, bridges).
/// Two kinds of work folder, both opened as a .ttproj:
///   - a theater installed in BMS (any 4.38 theater): the work folder is &lt;Objective Studio&gt;\Theaters\&lt;theater&gt;, BMS is only read;
///   - a TerrainTool project (theater conversion): as the old Object Editor (Run Objects from the old add-on, conversion tools).
/// The window shows the steps in order and, in the "Next step" box, what to do now.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _sub = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _paths = new() { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _next = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };

    private readonly Button _editor = Big("Objective editor…", "Table and map of all objectives on the satellite photo: move and rotate objectives, edit layout pieces and parking points, " +
                                                              "replace a layout, put a complete Korean or theater airbase, turn an objective into a junction, 3D preview");
    private readonly Button _fly = Small("Fly over objectives…", "Look at the objectives one after the other in 3D over the terrain");
    private readonly Button _osm = Small("Real runways from OpenStreetMap", "Theater conversion: for the airbases without real runway data (not in OurAirports) asks OpenStreetMap for their runways (airbase_targets.csv). Close the objective editor first.");
    private readonly Button _bridges = Small("Optimize bridges…", "Theater conversion: bridges on the real rivers and roads");
    private readonly Button _coastal = Small("Review coastal moves…", "Theater conversion: objectives that Run Objects moves out of the water");

    private readonly Button _build = Big("Run Objects", "");
    private readonly Button _cancel = Small("Cancel", "");
    private readonly ProgressBar _bar = new() { Height = 12, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _buildInfo = new() { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private readonly Button _install = Big("Install objects into BMS…", "Copies the built objects into BMS (asks first) and deletes the caches BMS rebuilds when a campaign or TE starts");
    private readonly Button _restore = Small("Restore a backup…", "Puts back the BMS files replaced by an earlier install");
    private readonly Button _campaign = Small("Open Campaign Editor", "Objectives moved, turned, replaced or removed: the campaign and TE files must follow (Campaign Editor)");

    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _notes = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap, MinHeight = 120,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _convTools = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

    private readonly ObjectsJob _job = TextureJob.All.OfType<ObjectsJob>().First();
    private CancellationTokenSource? _cts;
    private TheaterProject? _p;
    private string _projectFile = "";
    private bool Installed => _p?.IsInstalledTheater == true;

    public MainWindow(string project)
    {
        Title = "Objective Studio";
        Width = 1180; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var change = Small("Change theater…", "Open another theater installed in BMS, or a TerrainTool project");
        change.Click += (_, _) => ChangeTheater();
        var work = Small("Open work folder", "The folder where Objective Studio keeps the edits of this theater");
        work.Click += (_, _) => { if (_p != null) Explore(_p.ProjectFolder); };
        var hb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { work, change } };
        DockPanel.SetDock(hb, Dock.Right);
        head.Children.Add(hb);
        head.Children.Add(new StackPanel { Children = { _title, _sub, _paths } });

        var nextBox = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(232, 243, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(120, 170, 230)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel { Children = { new TextBlock { Text = "Next step", FontWeight = FontWeights.SemiBold }, _next } },
        };

        foreach (var b in new[] { _osm, _bridges, _coastal }) _convTools.Children.Add(b);
        var step1 = Step("1 · Edit objectives", "Edits are saved in the work folder, BMS is not touched.",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { _editor, _fly } }, _convTools);
        var step2 = Step("2 · Build objects", "", new StackPanel { Orientation = Orientation.Horizontal, Children = { _build, _cancel } }, _bar, _buildInfo);
        var step3 = Step("3 · Install into BMS and update the campaign", "Make a backup copy of your BMS Data folder before installing for the first time.",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { _install, _restore, _campaign } });

        var other = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        other.Children.Add(new TextBlock { Text = "Other tools: ", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
        var tex = Small("Model textures…", "Textures of aircraft, helicopters, vehicles and ships (will move to the Model Editor)");
        tex.Click += (_, _) => new ModelTexturesWindow(_p) { Owner = this }.Show();
        other.Children.Add(tex);
        var outBtn = Small("Open output folder", "Where Build objects writes");
        outBtn.Click += (_, _) => { if (_p != null) Explore(Path.Combine(_p.ProjectFolder, "Output")); };
        other.Children.Add(outBtn);

        var top = new StackPanel { Children = { head, nextBox, step1, step2, step3, other, _status } };
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new GroupBox { Header = "Log", Content = _notes, Margin = new Thickness(0, 6, 0, 0) });
        Content = root;

        _editor.Click += (_, _) => OpenEditor();
        _fly.Click += (_, _) => OpenFlyOver();
        _osm.Click += async (_, _) => await OsmAsync();
        _bridges.Click += (_, _) => OpenBridges();
        _coastal.Click += (_, _) => OpenCoastal();
        _build.Click += async (_, _) => await BuildAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _install.Click += async (_, _) => await InstallAsync();
        _restore.Click += (_, _) => RestoreBackup();
        _campaign.Click += (_, _) => OpenCampaignEditor();
        _cancel.IsEnabled = false;
        Activated += (_, _) => { if (_p != null && _cts == null) Refresh(); };

        LoadProject(project);
    }

    // ───────────────────────── look ─────────────────────────

    private static Button Big(string text, string tip) => new()
    {
        Content = text, FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0),
        ToolTip = tip.Length > 0 ? tip : null,
    };
    private static Button Small(string text, string tip) => new()
    {
        Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip.Length > 0 ? tip : null,
    };
    private static GroupBox Step(string header, string note, params UIElement[] content)
    {
        var sp = new StackPanel { Margin = new Thickness(6) };
        if (note.Length > 0) sp.Children.Add(new TextBlock { Text = note, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var c in content) sp.Children.Add(c);
        return new GroupBox { Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, FontSize = 13.5 }, Content = sp, Margin = new Thickness(0, 0, 0, 8) };
    }

    private static void Explore(string dir)
    {
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }

    // ───────────────────────── theaters and projects ─────────────────────────

    /// <summary>&lt;Objective Studio folder&gt; (the one with build.bat), else Documents\BMS Objective Studio.</summary>
    public static string Root()
    {
        if (global::TerrainTool.Core.SuiteLayout.ProgramFolder() is { } pf) return pf;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS Objective Studio");
    }

    /// <summary>Where the work folders of the installed theaters go.</summary>
    public static string TheatersRoot() => Path.Combine(Root(), "Theaters");

    private static string RecentFile => Path.Combine(Root(), "Settings", "projects.txt");

    public static List<string> Recent()
    {
        try { return File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Select(l => TheaterProject.FindMoved(l.Trim())).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new(); }
        catch (IOException) { return new(); }
    }

    private static void AddRecent(string file)
    {
        try
        {
            var l = Recent(); l.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase)); l.Insert(0, Path.GetFullPath(file));
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!); File.WriteAllLines(RecentFile, l.Take(10));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>One line for the recent list.</summary>
    public static string Describe(string file)
    {
        try
        {
            var p = TheaterProject.Load(file);
            return p.IsInstalledTheater ? $"{p.Theater.DisplayName.Trim() switch { "" => p.Theater.Name, var n => n }}   –   theater installed in BMS"
                                        : $"{Path.GetFileNameWithoutExtension(file)}   –   TerrainTool project (theater conversion)";
        }
        catch (Exception) { return file; }
    }

    private void ChangeTheater()
    {
        if (_cts != null) return;
        if (OwnedWindows.OfType<ObjectiveEditorWindow>().Any()) { MessageBox.Show(this, "Close the objective editor first.", Title); return; }
        var w = new StartWindow { Owner = this };
        if (w.ShowDialog() == true && w.ProjectFile != null) LoadProject(w.ProjectFile);
    }

    private void LoadProject(string file)
    {
        try
        {
            var p = TheaterProject.Load(file);
            if (string.IsNullOrWhiteSpace(p.ProjectFolder)) p.ProjectFolder = Path.GetDirectoryName(file) ?? "";
            _p = p; _projectFile = file;
            AddRecent(file);
        }
        catch (Exception ex)
        {
            _p = null;
            _title.Text = "Cannot read " + file;
            _sub.Text = ex.Message;
            SetEnabled(false);
            return;
        }
        string name = Installed && _p!.Theater.DisplayName.Trim().Length > 0 ? _p.Theater.DisplayName.Trim() : _p!.Theater.Name;
        Title = "Objective Studio – " + name;
        _title.Text = name;
        if (Installed)
        {
            _sub.Text = $"Theater installed in BMS · {_p.Theater.SizeKm} km. BMS is only read; your edits stay in the work folder until you install them.";
            _paths.Text = $"Objects    {_p.SourceObjectsDir}\nCampaign   {_p.SourceCampaignDir}\nWork       {_p.ProjectFolder}";
            _build.Content = "Build objects";
            _install.Content = "Install into BMS…";
            _install.ToolTip = "Copies into the theater's folders in BMS only the built files that changed; every file replaced is saved first (Restore a backup)";
            _build.ToolTip = "Applies your saved edits to a copy of the theater's objects (in the work folder). Nothing else is changed.";
        }
        else
        {
            string addOn = string.IsNullOrWhiteSpace(_p.OldTheaterFolder) ? "?" : new DirectoryInfo(_p.OldTheaterFolder).Name;
            _sub.Text = "TerrainTool project · theater conversion from the old add-on.";
            _paths.Text = $"Project    {_p.ProjectFolder}\nOld add-on {_p.OldTheaterFolder} (read only)\nOutput     {Path.Combine(_p.ProjectFolder, "Output", "Install", addOn, "Terrdata", ObjectsJob.FolderName)}";
            _build.Content = "Run Objects (from the old add-on)";
            _install.Content = "Install objects into BMS…";
            _build.ToolTip = "Builds Output\\Install\\<add-on>\\Terrdata\\ObjectsNT: BMS 4.38 database, old add-on layouts, removed bases, objective editor edits, Korean bases, leveling. " +
                             "The changes for the campaign go into Output\\ObjectChanges.json (Campaign Editor: Apply object changes).";
        }
        _convTools.Visibility = Visibility.Visible;
        // teatro installato: dei lavori della conversione resta solo Optimize bridges (con le scelte fatte a mano)
        _osm.Visibility = _coastal.Visibility = Installed ? Visibility.Collapsed : Visibility.Visible;
        _bridges.ToolTip = Installed ? "Bridges of this theater on its water: which have no water under them, which are off the river, which have pieces at different heights. Nothing changes unless you choose it."
                                     : "Theater conversion: bridges on the real rivers and roads";
        SetEnabled(true);
        Refresh();
    }

    private void SetEnabled(bool on)
    {
        bool idle = on && _cts == null;
        foreach (var b in new[] { _editor, _fly, _osm, _bridges, _coastal, _campaign }) b.IsEnabled = idle;
        _build.IsEnabled = idle;
        _install.IsEnabled = idle;
        _restore.IsEnabled = idle && Installed;
        _restore.Visibility = Installed ? Visibility.Visible : Visibility.Collapsed;
        _campaign.Visibility = Installed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────────────────────── state and next step ─────────────────────────

    private static DateTime Time(string f) => File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;

    /// <summary>Saved edits of the work folder: file name, what it is, how many lines, last change.</summary>
    private List<(string File, string What, int Count, DateTime When)> EditFiles()
    {
        var res = new List<(string, string, int, DateTime)>();
        if (_p == null) return res;
        void Add(string f, string what)
        {
            string path = Path.Combine(_p.ProjectFolder, f);
            int n = 0;
            try { if (File.Exists(path)) n = Math.Max(0, File.ReadLines(path).Count(l => l.Trim().Length > 0) - 1); } catch (IOException) { }
            res.Add((f, what, n, Time(path)));
        }
        Add(ObjectiveEdits.FileName, "objectives moved, turned or made junctions");
        Add(BaseSwaps.FileName, "airbases replaced by another base (Korean or of this theater)");
        Add(LayoutSwaps.FileName, "layouts replaced");
        Add(Path.GetFileName(PieceEdits.PathFor(_p.ProjectFolder)), "layout pieces moved or removed");
        Add(Path.GetFileName(PointEdits.PathFor(_p.ProjectFolder)), "parking / vehicle points moved");
        if (!Installed)
        {
            Add("airbase_targets.csv", "real runways (conversion)");
            Add("basi_tieni_via.csv", "airbases removed (conversion)");
            Add("ponti_tieni_via.csv", "bridges removed (conversion)");
        }
        return res;
    }

    private void Refresh()
    {
        if (_p == null) return;
        var files = EditFiles();
        int edits = files.Where(f => f.File is not ("airbase_targets.csv" or "basi_tieni_via.csv" or "ponti_tieni_via.csv")).Sum(f => f.Count);
        DateTime lastEdit = files.Select(f => f.When).DefaultIfEmpty(DateTime.MinValue).Max();
        string buildLog = Path.Combine(_p.ProjectFolder, "Output", Installed ? "Build_log.txt" : "RunObjects_log.txt");
        DateTime built = Time(buildLog);
        _buildInfo.Text = string.Join("\n", files.Where(f => f.Count > 0 || f.When > DateTime.MinValue)
                              .Select(f => $"{(f.Count > 0 ? "✓" : "–")} {f.Count,4}  {f.What}"))
                          + (files.All(f => f.Count == 0) ? "No saved edits yet." : "")
                          + (built > DateTime.MinValue ? $"\nLast build: {built.ToLocalTime():dd/MM/yyyy HH:mm}" : "\nNot built yet.");

        string next;
        if (Installed)
        {
            DateTime installed = Time(Path.Combine(_p.ProjectFolder, "Output", "Install_log.txt"));
            next = edits == 0 && built == DateTime.MinValue
                ? "Open the Objective editor, change what you need and press 'Save edits'. BMS is not touched."
                : lastEdit > built ? $"{edits} edits saved: press 'Build objects' (it prepares the theater's objects and campaign files in the work folder, BMS is not touched)."
                : installed < built ? "Built: press 'Install into BMS' (only the changed files are copied, each replaced file is saved first)."
                : "Installed. Start BMS and check the theater; 'Restore a backup' puts the old files back.";
        }
        else
        {
            string ch = ObjectChangeSet.PathFor(_p);
            next = built == DateTime.MinValue ? "Run Objects once: it builds the theater's objects from the old add-on."
                 : lastEdit > built ? "Edits saved after the last Run Objects: run it again, then Install objects into BMS."
                 : File.Exists(ch) && Time(ch) >= built.AddMinutes(-1) ? "Install objects into BMS. If airbases were moved, turned, replaced or removed: Campaign Editor → Apply object changes → Install campaign files."
                 : "Everything built. Edit objectives or install.";
        }
        _next.Text = next;
    }

    // ───────────────────────── jobs (TerrainTool project) ─────────────────────────

    private TextureContext Context() => new(_p!, _p!.TbOutputOrDefault);

    private async Task BuildAsync()
    {
        if (!Installed) { await RunObjectsAsync(); return; }
        if (_p == null || _cts != null) return;
        if (OwnedWindows.OfType<ObjectiveEditorWindow>().Any(w => w.HasUnsaved))
        { MessageBox.Show(this, "The objective editor has edits not saved yet: press 'Save edits' there first.", Title); return; }
        _cts = new CancellationTokenSource();
        SetEnabled(false); _cancel.IsEnabled = true;
        _notes.Text = "";
        var t0 = DateTime.Now;
        var prog = new Progress<double>(f => { _bar.Value = Math.Clamp(f, 0, 1); _status.Text = $"Build objects {f * 100:0}%"; });
        try
        {
            var ctx = Context();
            var notes = await Task.Run(() => _job.BuildInstalled(ctx, prog, _cts.Token), _cts.Token);
            _notes.Text = string.Join(Environment.NewLine, notes);
            try { File.WriteAllText(Path.Combine(_p.ProjectFolder, "Output", "Build_log.txt"), $"Build objects {t0:yyyy-MM-dd HH:mm}\r\n" + string.Join("\r\n", notes)); } catch (IOException) { }
            _status.Text = $"✓ Built in {DateTime.Now - t0:mm\\:ss}. BMS not touched yet: Install into BMS.";
        }
        catch (OperationCanceledException) { _status.Text = "cancelled"; }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
        finally
        {
            _cts.Dispose(); _cts = null;
            _cancel.IsEnabled = false; _bar.Value = 0;
            SetEnabled(_p != null);
            Refresh();
        }
    }

    private async Task InstallInstalledAsync()
    {
        if (_p == null || _cts != null) return;
        var inst = new InstalledTheaterInstaller(_p);
        if (inst.Problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n\n", inst.Problems), "Install into BMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, inst.Plan() + "\n\nProceed?", "Install into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetEnabled(false);
        var lines = new List<string> { "Install into BMS" };
        try
        {
            // il Progress va creato qui, sul thread della finestra: creato dentro Task.Run richiama la barra da un altro thread
            // (errore che chiudeva il programma alla fine dell'Install)
            var progress = new Progress<double>(f => _bar.Value = f);
            await Task.Run(() => inst.Run(l => Dispatcher.Invoke(() => { lines.Add(l); _notes.Text = string.Join(Environment.NewLine, lines); }), progress));
            try { File.WriteAllText(Path.Combine(_p.ProjectFolder, "Output", "Install_log.txt"), string.Join("\r\n", lines)); } catch (IOException) { }
            _status.Text = "✓ Installed into BMS.";
        }
        catch (Exception ex) { _status.Text = "Install: ERROR " + ex.Message; }
        finally { _bar.Value = 0; SetEnabled(true); Refresh(); }
    }

    private void RestoreBackup()
    {
        if (_p == null || !Installed) return;
        var list = InstalledTheaterInstaller.Backups(_p);
        if (list.Count == 0) { MessageBox.Show(this, "No backup yet: backups are made by 'Install into BMS'.", Title); return; }
        var lb = new ListBox { Height = 220, Margin = new Thickness(10) };
        foreach (var b in list) lb.Items.Add(new ListBoxItem { Content = $"{b.When:dd/MM/yyyy HH:mm:ss}   ({b.Files} files)", Tag = b.Dir });
        lb.SelectedIndex = 0;
        var ok = new Button { Content = "Restore", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(10, 0, 6, 10) };
        var w = new Window { Title = "Restore a backup", Width = 460, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { new TextBlock { Text = "The files replaced by that install are put back into BMS.", Margin = new Thickness(10, 10, 10, 0), TextWrapping = TextWrapping.Wrap }, lb, ok } } };
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true || (lb.SelectedItem as ListBoxItem)?.Tag is not string dir) return;
        try
        {
            if (OwnedWindows.OfType<ObjectiveEditorWindow>().Any()) { MessageBox.Show(this, "Close the objective editor first.", "Restore a backup"); return; }
            var keep = MessageBox.Show(this, "BMS gets back the files of before that install.\n\n" +
                "Also bring back the edits of that install into the work folder, to change them and install again?\n\n" +
                "Yes = the edits come back (the editor shows them again)\nNo = the editor starts again from the theater as it is in BMS",
                "Restore a backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (keep == MessageBoxResult.Cancel) return;
            var log = InstalledTheaterInstaller.Restore(_p, dir, keep == MessageBoxResult.Yes);
            _notes.Text = string.Join(Environment.NewLine, log);
            _status.Text = "✓ Backup restored.";
            try { File.Delete(Path.Combine(_p.ProjectFolder, "Output", "Install_log.txt")); } catch (IOException) { }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore a backup", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Refresh();
    }

    /// <summary>Run Objects of a TerrainTool project; true if it ended well.</summary>
    private async Task<bool> RunObjectsAsync()
    {
        if (_p == null || _cts != null || Installed) return false;
        TextureContext ctx;
        try { ctx = Context(); }
        catch (Exception ex) { _status.Text = "Objects: " + ex.Message; return false; }
        string? miss = _job.Missing(ctx);
        if (miss != null) { _status.Text = "Missing input: " + miss; return false; }
        _cts = new CancellationTokenSource();
        SetEnabled(false); _cancel.IsEnabled = true;
        _notes.Text = "";
        var t0 = DateTime.Now;
        var prog = new Progress<double>(f => { _bar.Value = Math.Clamp(f, 0, 1); _status.Text = $"{_job.Name} {f * 100:0}%"; });
        bool ok = false;
        try
        {
            var (_, notes) = await Task.Run(() => _job.Run(ctx, prog, _cts.Token), _cts.Token);
            _notes.Text = string.Join(Environment.NewLine, notes);
            try { File.WriteAllText(Path.Combine(_p.ProjectFolder, "Output", "RunObjects_log.txt"), $"Run Objects {t0:yyyy-MM-dd HH:mm}\r\n" + string.Join("\r\n", notes)); } catch (IOException) { }
            _status.Text = $"✓ Objects done in {DateTime.Now - t0:mm\\:ss}.";
            ok = true;
        }
        catch (OperationCanceledException) { _status.Text = "cancelled"; }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
        finally
        {
            _cts.Dispose(); _cts = null;
            _cancel.IsEnabled = false; _bar.Value = 0;
            SetEnabled(_p != null);
            Refresh();
        }
        return ok;
    }

    private async Task OsmAsync()
    {
        if (_p == null || _cts != null) return;
        if (OwnedWindows.OfType<ObjectiveEditorWindow>().Any())
        {
            MessageBox.Show(this, "Close the objective editor first (it would overwrite the positions written now).", "Real runways from OpenStreetMap");
            return;
        }
        if (MessageBox.Show(this, "Ask OpenStreetMap (overpass-api.de) for the runways of the airbases that have no real runway data, and add them to airbase_targets.csv?\n" +
                "Korean bases already chosen for those airbases are put on the real runway too.\nThen run Objects.",
                "Real runways from OpenStreetMap", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _cts = new CancellationTokenSource();
        SetEnabled(false);
        _status.Text = "Asking OpenStreetMap…";
        try
        {
            var log = await OsmRunways.AddMissingTargetsAsync(new EditorData(_p), 8, _cts.Token);
            _notes.Text = string.Join(Environment.NewLine, log);
            _status.Text = log.Count > 0 ? log[0] : "done";
        }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
        finally { _cts.Dispose(); _cts = null; SetEnabled(true); Refresh(); }
    }

    private void OpenBridges()
    {
        if (_p == null) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { new BridgesWindow(_p) { Owner = this, RunObjects = async () => { await BuildAsync(); } }.Show(); }
        catch (Exception ex) { MessageBox.Show(this, "Optimize bridges not opened: " + ex.Message, Title); }
        finally { Cursor = null; }
    }

    private void OpenCoastal()
    {
        if (_p == null) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { new CoastalWindow(_p) { Owner = this, RunObjects = async () => { await RunObjectsAsync(); } }.Show(); }
        catch (Exception ex) { MessageBox.Show(this, "Review coastal moves not opened: " + ex.Message, Title); }
        finally { Cursor = null; }
    }

    private void OpenFlyOver()
    {
        if (_p == null) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { new FlyOverWindow(new EditorData(_p)) { Owner = this }.Show(); }
        catch (Exception ex) { MessageBox.Show(this, "Fly over not opened: " + ex.Message, Title); }
        finally { Cursor = null; }
    }

    private void OpenEditor()
    {
        if (_p == null) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var w = new ObjectiveEditorWindow(_p) { Owner = this };
            if (!Installed) w.RunObjectsRequested = async install => { if (await RunObjectsAsync() && install) await InstallAsync(); };
            w.Closed += (_, _) => Refresh();
            w.Show();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Objective editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = null; }
    }

    private async Task InstallAsync()
    {
        if (Installed) { await InstallInstalledAsync(); return; }
        if (_p == null || _cts != null) return;
        var inst = new BmsInstaller(_p, true, false);
        if (inst.Problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", inst.Problems), "Install objects into BMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, inst.Plan() + "\n\nProceed?", "Install objects into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        SetEnabled(false);
        var lines = new List<string> { "Install objects into BMS → " + inst.Target };
        try
        {
            await Task.Run(() => inst.Run(l => Dispatcher.Invoke(() => { lines.Add(l); _notes.Text = string.Join(Environment.NewLine, lines); })));
            try { File.WriteAllText(Path.Combine(_p.ProjectFolder, "Output", "Install_log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm}\r\n" + string.Join("\r\n", lines)); } catch (IOException) { }
            _status.Text = "✓ Objects installed. If bases changed: Campaign Editor → Apply object changes → Install campaign files into BMS.";
        }
        catch (Exception ex) { _status.Text = "Install: ERROR " + ex.Message; }
        finally { SetEnabled(true); Refresh(); }
    }

    /// <summary>The Campaign Editor (CampaignEditor folder next to Objective Studio's), opened on the same theater or project.</summary>
    private void OpenCampaignEditor()
    {
        if (_p == null) return;
        string? exe = global::TerrainTool.Core.SuiteLayout.ExeOf("CampaignEditor");
        if (exe == null) { MessageBox.Show(this, "The Campaign Editor was not found: build it with build.bat in the CampaignEditor folder (next to ObjectiveStudio).", Title); return; }
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        // a TerrainTool project opens directly; an installed theater is chosen in the Campaign Editor's own start window
        if (!Installed) { psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_projectFile); }
        System.Diagnostics.Process.Start(psi);
    }
}
