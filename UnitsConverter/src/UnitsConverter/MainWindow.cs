using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using TerrainTool.Core.Database;

namespace UnitsConverter;

/// <summary>
/// 4.37 Units Converter: choose an old database (a BMS 4.37 installation, an add-on or just an objects folder) and a theater
/// of BMS 4.38, tick what to convert, check the conversion step by step, convert and install (backup first), restore.
/// Everything happens here: nothing else of the suite is needed.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBox _srcPath = new() { Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _src = new() { MinWidth = 260 };
    private readonly TextBlock _srcInfo = Info();
    private readonly TextBox _bmsPath = new() { Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _dst = new() { MinWidth = 260 };
    private readonly TextBlock _dstInfo = Info();
    private readonly ComboBox _kind = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _search = new() { MinWidth = 180, Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _onlyTicked = new() { Content = "Only ticked", VerticalAlignment = VerticalAlignment.Center };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HorizontalGridLinesBrush = Brushes.Gainsboro, FontSize = 12,
    };
    private readonly TextBox _report = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _reportTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
    private const string TitleIdle = "4 · What the conversion will do, step by step ('Check the conversion' shows it here; nothing is written yet)";
    private const string TitlePlan = "4 · What 'Convert and install…' will do, step by step, by itself (nothing written yet)";
    private const string TitleDone = "Done: what the conversion did (Restore… undoes it)";
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button _find = Btn("Find what can be converted");
    private readonly Button _check = Btn("Check the conversion (nothing written)");
    private readonly Button _install = Btn("Convert and install…");
    private readonly Button _restore = Btn("Restore…");

    private List<Row> _rows = new();
    private List<Tpl> _templates = new();
    private UnitsConversion.Conversion? _plan;
    private string? _planKey;
    private readonly string? _startTheater;

    public sealed class Row
    {
        public bool Convert { get; set; }
        public bool CanConvert { get; init; }
        public int Ct { get; init; }
        public string Kind { get; init; } = "";
        public string Name { get; init; } = "";
        public string Model { get; init; } = "";
        public string State { get; init; } = "";
        public bool SourceModel { get; set; }
        public bool SourceModelPossible { get; init; }
        public bool IsAircraft { get; init; }
        public string TeName { get; set; } = "";
        public int TeTemplate { get; set; } = -1;
        /// <summary>Aircraft: the BMS 4.38 flight model it will fly with (name), and the choices.</summary>
        public string? FlightModel { get; set; }
        public string? SameNameModel { get; init; }
        public List<FmItem> FlightModels { get; init; } = new();
    }

    public sealed record FmItem(string Name, string Label);

    public sealed record Tpl(int Id, string Label);

    private static TextBlock Info() => new() { Foreground = Brushes.DimGray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private static Button Btn(string t) => new() { Content = t, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };

    public static string Root()
    {
        if (TerrainTool.Core.SuiteLayout.ProgramFolder() is { } pf) return pf;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS 4.37 Units Converter");
    }

    private static string SettingsFile => Path.Combine(Root(), "Settings", "paths.txt");
    private static string WorkDir => Path.Combine(Root(), "Work");
    private string BackupRoot => Path.Combine(Root(), "Backups", Safe(Dst?.Theater.Name ?? "theater"));
    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private UnitsConversion.SourceDb? Src => _src.SelectedItem as UnitsConversion.SourceDb;
    private UnitsConversion.Destination? Dst => _dst.SelectedItem as UnitsConversion.Destination;

    public MainWindow(string? bmsData = null, string? theater = null)
    {
        Title = "BMS 4.37 Units Converter";
        Width = 1450; Height = 900; MinWidth = 1050; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;   // the list has long texts: the whole screen from the start
        _startTheater = theater;
        string[] saved = { "", "" };
        try { if (File.Exists(SettingsFile)) saved = File.ReadAllLines(SettingsFile).Concat(new[] { "", "" }).Take(2).ToArray(); } catch (IOException) { }
        _srcPath.Text = saved[0];
        _bmsPath.Text = bmsData != null ? (Path.GetFileName(bmsData.TrimEnd('\\')).Equals("Data", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(bmsData.TrimEnd('\\'))! : bmsData)
                       : saved[1].Length > 0 ? saved[1] : @"C:\Falcon BMS 4.38";

        // ── top: the two databases
        var top = new Grid { Margin = new Thickness(10, 8, 10, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        for (int i = 0; i < 4; i++) top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int r, int c, int span = 1) { Grid.SetRow(e, r); Grid.SetColumn(e, c); Grid.SetColumnSpan(e, span); top.Children.Add(e); }
        TextBlock H(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var srcBrowse = Btn("Browse…"); srcBrowse.Click += (_, _) => BrowseSource();
        var bmsBrowse = Btn("Browse…"); bmsBrowse.Click += (_, _) => BrowseBms();
        Put(H("1 · Old database"), 0, 0); Put(_srcPath, 0, 1); Put(srcBrowse, 0, 2); Put(_src, 0, 3);
        Put(_srcInfo, 1, 1, 3);
        Put(H("2 · BMS 4.38 theater"), 2, 0); Put(_bmsPath, 2, 1); Put(bmsBrowse, 2, 2); Put(_dst, 2, 3);
        Put(_dstInfo, 3, 1, 3);
        _srcPath.ToolTip = "A BMS 4.37 installation (e.g. C:\\Falcon BMS 4.37), an add-on folder (e.g. E:\\Add-On Mideast128) or just an objects folder with Falcon4_CT.xml";
        _bmsPath.ToolTip = "The BMS 4.38 installation (e.g. C:\\Falcon BMS 4.38)";

        // ── middle: the list and the plan
        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var k in new[] { "All kinds", "Aircraft", "Helicopter", "Ground vehicle", "Ship", "Weapon", "Squadron type", "Battalion / brigade type", "Task force type" }) _kind.Items.Add(k);
        _kind.SelectedIndex = 0;
        filter.Children.Add(_kind);
        filter.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        filter.Children.Add(_search);
        filter.Children.Add(_onlyTicked);
        _kind.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _onlyTicked.Click += (_, _) => Fill();
        var left = new DockPanel();
        var lt = new TextBlock { Text = "3 · Tick what to convert (only what the theater has not, by name)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(lt, Dock.Top); left.Children.Add(lt);
        DockPanel.SetDock(filter, Dock.Top); left.Children.Add(filter);
        left.Children.Add(_grid);
        var right = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(_reportTitle, Dock.Top); right.Children.Add(_reportTitle);
        right.Children.Add(_report);
        var mid = new Grid { Margin = new Thickness(10, 4, 10, 0) };
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        mid.Children.Add(left); mid.Children.Add(right);

        // ── bottom
        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 0) };
        bar.Children.Add(_find); bar.Children.Add(_check); bar.Children.Add(_install); bar.Children.Add(_restore);
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(10, 6, 10, 0),
            Text = "A backup of everything that changes is made before every installation: Restore… puts the theater back as it was. " +
                   "Flight models stay BMS 4.38's (never copied nor changed): an aircraft flies with the BMS flight model of the same name, and its weapons " +
                   "hang where that flight model puts its hardpoints. 3D models and textures of an add-on belong to their authors: ask them before you share a theater with them.",
        };
        var bottom = new StackPanel();
        bottom.Children.Add(bar); bottom.Children.Add(note); bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(mid);
        Content = root;

        _find.Click += (_, _) => Analyze();
        _check.Click += (_, _) => Check();
        _install.Click += (_, _) => Install();
        _restore.Click += (_, _) => Restore();
        _srcPath.LostFocus += (_, _) => LoadSources();
        _bmsPath.LostFocus += (_, _) => LoadDestinations();
        _src.SelectionChanged += (_, _) => { ShowSource(); Clear(); };
        _dst.SelectionChanged += (_, _) => { ShowDestination(); Clear(); };
        SetupGrid();
        _reportTitle.Text = TitleIdle;
        _check.IsEnabled = _install.IsEnabled = false;
        Loaded += (_, _) => { LoadSources(); LoadDestinations(); };
        _status.Text = "Choose the old database and the BMS 4.38 theater, then 'Find what can be converted'.";
    }

    // ───────────────────────── the two databases

    private void BrowseSource()
    {
        var d = new OpenFolderDialog { Title = "Old database: a BMS 4.37 installation, an add-on folder or an objects folder" };
        if (Directory.Exists(_srcPath.Text)) d.InitialDirectory = _srcPath.Text;
        if (d.ShowDialog(this) != true) return;
        _srcPath.Text = d.FolderName;
        LoadSources();
    }

    private void BrowseBms()
    {
        var d = new OpenFolderDialog { Title = "The BMS 4.38 installation (e.g. C:\\Falcon BMS 4.38)" };
        if (Directory.Exists(_bmsPath.Text)) d.InitialDirectory = _bmsPath.Text;
        if (d.ShowDialog(this) != true) return;
        _bmsPath.Text = d.FolderName;
        LoadDestinations();
    }

    private string? _srcLoaded, _bmsLoaded;

    private void LoadSources()
    {
        string p = _srcPath.Text.Trim();
        if (p == _srcLoaded) return;
        _srcLoaded = p;
        _src.Items.Clear();
        if (p.Length == 0) { _srcInfo.Text = ""; return; }
        var list = UnitsConversion.FindSources(p);
        foreach (var s in list) _src.Items.Add(s);
        if (list.Count == 0) _srcInfo.Text = "No database here: choose a BMS 4.37 installation, an add-on folder (with Terrdata\\Objects) or a folder with Falcon4_CT.xml.";
        else _src.SelectedIndex = 0;
        SaveSettings();
    }

    private void ShowSource()
    {
        if (Src is not { } s) { _srcInfo.Text = ""; return; }
        _srcInfo.Text = $"{s.Theater.ObjectDir} · {s.Version}" + (s.AcTypes != null ? $" · flight model list {s.AcTypes}" : " · no Sim\\Acdata\\AcTypes.lst near it: flight model numbers kept as they are") +
                        (s.Is438 ? "   This database is already 4.38: Database Interchange is the program for 4.38 → 4.38." : "");
        _srcInfo.Foreground = s.Is438 ? Brushes.DarkOrange : Brushes.DimGray;
    }

    private void LoadDestinations()
    {
        string p = _bmsPath.Text.Trim();
        if (p == _bmsLoaded) return;
        _bmsLoaded = p;
        _dst.Items.Clear();
        var list = UnitsConversion.FindDestinations(p);
        foreach (var d in list) _dst.Items.Add(d);
        if (list.Count == 0) { _dstInfo.Text = "No BMS installation here (Data\\TerrData\\TheaterDefinition\\theater.lst)."; return; }
        var pick = list.FirstOrDefault(d => d.Problem == null && _startTheater != null && d.Theater.Name.Equals(_startTheater, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(d => d.Problem == null) ?? list[0];
        _dst.SelectedItem = pick;
        SaveSettings();
    }

    private void ShowDestination()
    {
        if (Dst is not { } d) { _dstInfo.Text = ""; return; }
        _dstInfo.Foreground = d.Problem != null ? Brushes.Firebrick : Brushes.DimGray;
        _dstInfo.Text = d.Problem != null ? "Cannot be written: " + d.Problem
            : $"database {d.Theater.ObjectDir} · 3D models {d.Theater.ModelsDir} · campaign {d.Theater.CampaignDir}";
        _templates = new List<Tpl> { new(-1, "automatic (BMS aircraft with the same flight model)") };
        if (d.Problem == null)
            _templates.AddRange(UnitsConversion.TePlanes(d).Entries.Where(e => e.Type != 0).Select(e => new Tpl(e.Id, $"{e.Name}  (icon {e.Icon})")).OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase));
        SetupGrid();
    }

    private void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllLines(SettingsFile, new[] { _srcPath.Text.Trim(), _bmsPath.Text.Trim() }); }
        catch (IOException) { }
    }

    private void Clear()
    {
        _rows = new(); _grid.ItemsSource = null; _plan = null; _report.Text = ""; _reportTitle.Text = TitleIdle;
        _check.IsEnabled = _install.IsEnabled = false;
    }

    // ───────────────────────── the list

    private void SetupGrid()
    {
        _grid.Columns.Clear();
        DataGridTemplateColumn Check(string header, string path, string enabled, string tip)
        {
            var f = new FrameworkElementFactory(typeof(CheckBox));
            f.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            f.SetBinding(UIElement.IsEnabledProperty, new Binding(enabled));
            f.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            f.AddHandler(ToggleButton.ClickEvent, new RoutedEventHandler((_, _) => { _install.IsEnabled = false; _plan = null; }));
            return new DataGridTemplateColumn { Header = new TextBlock { Text = header, ToolTip = tip }, CellTemplate = new DataTemplate { VisualTree = f }, Width = 60 };
        }
        DataGridTextColumn Text(string h, string path, double w, bool star = false) => new()
        {
            Header = h, Binding = new Binding(path), IsReadOnly = true, MinWidth = 50,
            Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w),
            ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } },
        };
        _grid.Columns.Add(Check("Convert", nameof(Row.Convert), nameof(Row.CanConvert), "Convert this into the theater"));
        _grid.Columns.Add(Text("Kind", nameof(Row.Kind), 95));
        _grid.Columns.Add(Text("Name", nameof(Row.Name), 150));
        var state = Text("3D model and state", nameof(Row.State), 0, star: true);
        state.MinWidth = 260;
        _grid.Columns.Add(state);
        var ff = new FrameworkElementFactory(typeof(ComboBox));
        ff.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(Row.FlightModels)));
        ff.SetValue(ItemsControl.DisplayMemberPathProperty, nameof(FmItem.Label));
        ff.SetValue(Selector.SelectedValuePathProperty, nameof(FmItem.Name));
        ff.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(Row.FlightModel)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        ff.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(Row.IsAircraft)));
        ff.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => { _install.IsEnabled = false; _plan = null; }));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = new TextBlock
            {
                Text = "Flies like",
                ToolTip = "Aircraft: the flight model it flies with. It also gives where it stands on its wheels and which pylons and racks it carries.\n" +
                          "'Its own' (AI-only aircraft): the old database's flight model, copied into the theater's Sim folder and added to its AcTypes.lst: as it was.\n" +
                          "Otherwise a BMS 4.38 flight model (never changed; those of flyable aircraft are never copied).\n" +
                          "✓ racks on every pylon it uses, left and right alike · ⚠ pylons without racks or different left and right.",
            },
            CellTemplate = new DataTemplate { VisualTree = ff }, Width = new DataGridLength(330),
        });
        _grid.Columns.Add(Check("Source model", nameof(Row.SourceModel), nameof(Row.SourceModelPossible),
            "BMS 4.38 has a different model with the same number: tick to bring the old database's model (with a free number) instead of BMS's"));
        var name = new DataGridTextColumn { Header = new TextBlock { Text = "Name in the TE", ToolTip = "Aircraft and helicopters: the name the Tactical Engagement shows" },
                                            Binding = new Binding(nameof(Row.TeName)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(130) };
        name.ElementStyle = new Style(typeof(TextBlock));
        _grid.Columns.Add(name);
        var cf = new FrameworkElementFactory(typeof(ComboBox));
        cf.SetValue(ItemsControl.ItemsSourceProperty, _templates);
        cf.SetValue(ItemsControl.DisplayMemberPathProperty, nameof(Tpl.Label));
        cf.SetValue(Selector.SelectedValuePathProperty, nameof(Tpl.Id));
        cf.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(Row.TeTemplate)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        cf.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(Row.IsAircraft)));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = new TextBlock { Text = "TE picture and icon like", ToolTip = "Aircraft and helicopters: the TE list shows the picture and icon of this aircraft" },
            CellTemplate = new DataTemplate { VisualTree = cf }, Width = new DataGridLength(210),
        });
    }

    private void Analyze()
    {
        if (Src is not { } s) { _status.Text = "Choose the old database first."; return; }
        if (Dst is not { } d || d.Problem != null) { _status.Text = "Choose a BMS 4.38 theater that can be written."; return; }
        try
        {
            Mouse(true);
            var an = UnitsConversion.Analyze(s, d);
            var S = BmsDatabase.Load(s.Theater.ObjectDir);
            _rows = an.Candidates.Select(c =>
            {
                bool air = c.Entity.Kind is Interchange.EntityKind.Aircraft or Interchange.EntityKind.Helicopter;
                var fits = air ? UnitsConversion.FlightModelsFor(s, d, S, c.Ct) : new();
                var proposed = UnitsConversion.Proposed(fits);
                string Label(UnitsConversion.FlightModelFit f) =>
                    (f.NoRack.Count == 0 && f.Asymmetric.Count == 0 ? "✓ " : "⚠ ") + (f.Own ? $"Its own (the old database's {f.Model.Name}, copied into the theater)" : f.Model.Name) + (f.SameName ? " (same name)" : "") + (f == proposed ? " — suggested" : "") +
                    (f.NoRack.Count > 0 ? $": pylons {string.Join(", ", f.NoRack)} without racks" : ": racks on all pylons") +
                    (f.Asymmetric.Count > 0 ? ", left ≠ right" : ", left = right") +
                    (f.Own ? ", wheels as before" : double.IsNaN(f.GearDiff) ? "" : ", " + f.Wheels) +
                    (f.OtherPylons.Count == 0 ? ", same pylons as before" : "");
                var items = fits.Take(12).ToList();
                foreach (var f in fits.Where(f => f.SameName || f == proposed)) if (!items.Contains(f)) items.Insert(0, f);
                return (c, air, items, proposed, Label: (Func<UnitsConversion.FlightModelFit, string>)Label);
            }).Select(x => new Row
            {
                Ct = x.c.Ct, Kind = x.c.Kind, Name = x.c.Name, CanConvert = x.c.CanImport,
                State = x.c.What + (x.c.Note.Length > 0 ? " — " + x.c.Note : ""),
                SourceModelPossible = x.c.OwnModelPossible && !x.c.OwnModelDefault, SourceModel = false,
                IsAircraft = x.air, TeName = x.air ? x.c.Name : "",
                FlightModels = x.items.Select(f => new FmItem(f.Key, x.Label(f))).ToList(),
                FlightModel = x.proposed?.Key,
                SameNameModel = x.items.FirstOrDefault(f => f.SameName)?.Model.Name,
            }).ToList();
            Fill();
            int ok = _rows.Count(r => r.CanConvert);
            _status.Text = $"{_rows.Count} item(s) of {s.Theater.Name} that {d.Theater.Name} has not; {ok} can be converted. Tick them, then 'Check the conversion'." +
                           (an.Problems.Count > 0 ? "  " + string.Join("; ", an.Problems) : "");
            _report.Text = ""; _reportTitle.Text = TitleIdle;
            _check.IsEnabled = ok > 0; _install.IsEnabled = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        { _status.Text = "Could not read the databases: " + ex.Message; }
        finally { Mouse(false); }
    }

    private void Fill()
    {
        string k = _kind.SelectedIndex <= 0 ? "" : (string)_kind.SelectedItem;
        string q = _search.Text.Trim();
        _grid.ItemsSource = _rows.Where(r => (k.Length == 0 || r.Kind == k) && (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                             && (_onlyTicked.IsChecked != true || r.Convert)).ToList();
    }

    private static void Mouse(bool busy) => System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;

    // ───────────────────────── check, install, restore

    private List<UnitsConversion.Pick> Picks()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        return _rows.Where(r => r.Convert && r.CanConvert)
            .Select(r => new UnitsConversion.Pick(r.Ct, r.Name, r.SourceModel, r.IsAircraft && r.TeName.Trim().Length > 0 ? r.TeName.Trim() : null, r.TeTemplate,
                                                  r.IsAircraft && r.FlightModel != null && r.FlightModel != r.SameNameModel ? r.FlightModel : null)).ToList();
    }

    private string Key(List<UnitsConversion.Pick> p) => (Src?.Theater.ObjectDir ?? "") + "|" + (Dst?.Theater.ObjectDir ?? "") + "|" + string.Join("|", p);

    private bool Check()
    {
        if (Src is not { } s || Dst is not { } d || d.Problem != null) return false;
        var picks = Picks();
        if (picks.Count == 0) { _status.Text = "Tick at least one item to convert."; return false; }
        try
        {
            Mouse(true);
            _plan = UnitsConversion.MakePlan(s, d, picks, Path.Combine(WorkDir, "plan"));
            _planKey = Key(picks);
            _report.Text = _plan.Text();
            _reportTitle.Text = TitlePlan;
            _install.IsEnabled = _plan.HasWork;
            _status.Text = !_plan.HasWork ? "Nothing to write: the theater has all of it already."
                : _plan.Problems.Count > 0 ? $"Read the plan: {_plan.Problems.Count} problem(s) at the end. 'Convert and install…' writes it anyway (what has a problem may not show in BMS)."
                : "Plan ready, nothing written yet. Read it, then 'Convert and install…'.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException or InvalidOperationException)
        { _status.Text = "The plan could not be made: " + ex.Message; return false; }
        finally { Mouse(false); }
    }

    private void Install()
    {
        if (Dst is not { } d) return;
        var picks = Picks();
        if (_plan == null || _planKey != Key(picks)) { if (!Check()) return; }   // the choice changed: plan again first
        if (_plan == null || !_plan.HasWork) return;
        if (DbInstaller.BmsRunning()) { MessageBox.Show(this, "BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        int records = _plan.Plan.Records.Count, files = _plan.Plan.Files.Count;
        if (MessageBox.Show(this,
                $"Convert {picks.Count} item(s) into {d.Theater.Name}?\n\n{records} database record(s), {files} file(s) (3D models, textures, TE list, names).\n\n" +
                $"A backup of everything that changes is made first in\n{BackupRoot}\nRestore… puts the theater back as it was.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Mouse(true);
            var log = UnitsConversion.Install(_plan, Path.Combine(WorkDir, "plan"), BackupRoot);
            string done = "INSTALLED\r\n" + string.Join("\r\n", log) + "\r\n\r\n" + _report.Text;
            _plan = null;
            Mouse(false);
            Analyze();   // the list again: what was converted is no longer in it
            _report.Text = done;
            _reportTitle.Text = TitleDone;
            _status.Text = $"Converted into {d.Theater.Name}. Start BMS: Tactical Engagement → New, the aircraft are in the squadron list of the teams. " +
                           "Restore… undoes it.";
            MessageBox.Show(this, $"Done: {picks.Count} item(s) converted into {d.Theater.Name}.\n\nStart BMS and try them in a new Tactical Engagement.\nRestore… undoes the conversion.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _status.Text = "Not installed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Not installed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Mouse(false); }
    }

    private void Restore()
    {
        var b = UnitsConversion.Backups(BackupRoot);
        if (b.Count == 0) { MessageBox.Show(this, $"No backup for {Dst?.Theater.Name ?? "this theater"} yet ({BackupRoot}).", Title); return; }
        var w = new Window { Title = "Restore " + (Dst?.Theater.Name ?? ""), Owner = this, Width = 860, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Margin = new Thickness(10) };
        foreach (var (dir, time, summary) in b) list.Items.Add($"{time:yyyy-MM-dd HH:mm}   {summary}");
        list.SelectedIndex = 0;
        var ok = new Button { Content = "Restore the theater as it was before this conversion", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
        var dp = new DockPanel();
        var hint = new TextBlock { Text = "Newest first. Restoring an older one also removes what came after it only if you restore those first (newest first).", Margin = new Thickness(10, 8, 10, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        DockPanel.SetDock(hint, Dock.Top); dp.Children.Add(hint);
        DockPanel.SetDock(ok, Dock.Bottom); dp.Children.Add(ok);
        dp.Children.Add(list);
        w.Content = dp;
        ok.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            if (DbInstaller.BmsRunning()) { MessageBox.Show(w, "BMS is running: close it first.", Title); return; }
            try
            {
                var log = UnitsConversion.Restore(b[i].Dir);
                Directory.Move(b[i].Dir, b[i].Dir + "_restored");
                w.Close();
                Analyze();
                _report.Text = "RESTORED\r\n" + string.Join("\r\n", log);
                _reportTitle.Text = "Restored: the theater is as it was before that conversion";
                _status.Text = "Theater restored as it was before that conversion.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { MessageBox.Show(w, ex.Message, "Not restored", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        w.ShowDialog();
    }
}
