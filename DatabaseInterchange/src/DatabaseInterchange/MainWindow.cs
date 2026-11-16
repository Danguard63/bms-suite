using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Database;
using TerrainTool.Core.Editor;

namespace DatabaseInterchange;

/// <summary>
/// Database Interchange: choose a source and a destination theater, the layouts (bases, ground objectives) to copy, make the
/// plan (nothing written, every number shown), install with backup, restore; the same for aircraft, helicopters, ground vehicles,
/// ships, weapons and unit types (database only: the Sim files are BMS's).
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private readonly TextBox _data = new() { Width = 280, Margin = new Thickness(0, 0, 4, 0) };
    private readonly ComboBox _from = new() { MinWidth = 200, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _to = new() { MinWidth = 200, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, FontSize = 12,
    };
    private readonly TextBox _report = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 2, 8, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button _plan = Btn("Make plan (nothing written)");
    private readonly Button _install = Btn("Install into the destination…");
    private List<LayoutRow> _rows = new();
    private Interchange.Plan? _current;

    // aircraft & vehicles tab
    private readonly ComboBox _kind = new() { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _eSearch = new() { Margin = new Thickness(0, 0, 0, 0) };
    private readonly DataGrid _eGrid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, FontSize = 12,
    };
    private readonly TextBox _eReport = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Button _ePlan = Btn("Make plan (nothing written)");
    private readonly Button _eInstall = Btn("Install into the destination…");
    private List<EntityRow> _eRows = new();
    private Interchange.Plan? _eCurrent;
    private List<int> _eCurrentCts = new();
    private HashSet<string> _dstNames = new(StringComparer.Ordinal);
    private string? _dstNamesOf;

    public sealed record LayoutRow(int Ocd, string Name, string Type, int Pieces);
    public sealed record EntityRow(int Ct, string Kind, string Name, string Detail, string InDest, Interchange.EntityKind K);

    public static string Root()
    {
        if (global::TerrainTool.Core.SuiteLayout.ProgramFolder() is { } pf) return pf;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS Database Interchange");
    }

    private static string Settings(string name) => Path.Combine(Root(), "Settings", name);
    private static string BackupRoot => Path.Combine(Root(), "Transfers", "Backups");
    private BmsTheater? From => (_from.SelectedItem as ComboBoxItem)?.Tag as BmsTheater;
    private BmsTheater? To => (_to.SelectedItem as ComboBoxItem)?.Tag as BmsTheater;

    private static Button Btn(string t) => new() { Content = t, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>Theater chosen in the BMS Suite main menu: it becomes the destination (or the source, when it cannot be written).</summary>
    private string? _startTheater;

    public MainWindow(string? bmsData = null, string? theater = null)
    {
        Title = "BMS Database Interchange";
        _startTheater = theater;
        Width = 1400; Height = 880; MinWidth = 1000; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { _data.Text = File.Exists(Settings("bms_data_folder.txt")) ? File.ReadAllText(Settings("bms_data_folder.txt")).Trim() : @"C:\Falcon BMS 4.38\Data"; }
        catch { _data.Text = @"C:\Falcon BMS 4.38\Data"; }
        if (bmsData != null) _data.Text = bmsData;

        var top = new WrapPanel { Margin = new Thickness(6) };
        void L(string s) => top.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        L("BMS Data folder"); top.Children.Add(_data);
        var browse = Btn("…"); browse.Click += (_, _) => Browse(); top.Children.Add(browse);
        L("   From"); top.Children.Add(_from);
        L("To"); top.Children.Add(_to);
        var reload = Btn("Reload"); reload.Click += (_, _) => LoadTheaters(); top.Children.Add(reload);

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 8, 6), FontSize = 11.5,
            Text = "Bases and ground objectives: choose layouts of the source theater (Ctrl / Shift + click for more), Make plan, then Install. " +
                   "Pieces and 3D models the destination already has the same are reused; the rest get free numbers at the end of the tables. " +
                   "Then use the new layout with Objective Studio (Replace layout).",
        };

        _grid.Columns.Add(new DataGridTextColumn { Header = "OCD", Binding = new System.Windows.Data.Binding(nameof(LayoutRow.Ocd)), Width = new DataGridLength(55) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(LayoutRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(LayoutRow.Type)), Width = new DataGridLength(110) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Pieces", Binding = new System.Windows.Data.Binding(nameof(LayoutRow.Pieces)), Width = new DataGridLength(55) });
        var left = new DockPanel { Width = 520, Margin = new Thickness(6, 0, 6, 6) };
        var lh = new TextBlock { Text = "Layouts of the source theater – search (name or OCD number)", Foreground = Brushes.DimGray };
        DockPanel.SetDock(lh, Dock.Top); left.Children.Add(lh);
        DockPanel.SetDock(_search, Dock.Top); left.Children.Add(_search);
        left.Children.Add(_grid);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_plan); bar.Children.Add(_install);
        var restore = Btn("Restore…"); restore.Click += (_, _) => Restore(); bar.Children.Add(restore);
        var right = new DockPanel { Margin = new Thickness(0, 0, 6, 6) };
        DockPanel.SetDock(bar, Dock.Top); right.Children.Add(bar);
        right.Children.Add(_report);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); body.Children.Add(left);
        body.Children.Add(right);
        var tabs = new TabControl { Margin = new Thickness(4, 0, 4, 0) };
        tabs.Items.Add(new TabItem { Header = "Bases & ground objectives", Content = body });
        tabs.Items.Add(new TabItem { Header = "Aircraft, vehicles & weapons", Content = EntitiesTab() });
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(tabs);
        Content = root;

        _install.IsEnabled = false;
        _eInstall.IsEnabled = false;
        _from.SelectionChanged += (_, _) => { Save("from.txt", From?.Name); LoadLayouts(); Invalidate(); };
        _to.SelectionChanged += (_, _) => { Save("to.txt", To?.Name); Invalidate(); MarkInDest(); };
        _search.TextChanged += (_, _) => FillGrid();
        _grid.SelectionChanged += (_, _) => Invalidate();
        _plan.Click += (_, _) => MakePlan();
        _install.Click += (_, _) => Install();
        Loaded += (_, _) => LoadTheaters();
    }

    private static void Save(string name, string? value)
    {
        if (value == null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(Settings(name))!); File.WriteAllText(Settings(name), value); } catch { }
    }

    private static string? Read(string name)
    {
        try { return File.Exists(Settings(name)) ? File.ReadAllText(Settings(name)).Trim() : null; } catch { return null; }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Falcon BMS Data folder (the one with Terrdata, Campaign, Art…)" };
        if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; LoadTheaters(); }
    }

    private void LoadTheaters()
    {
        string data = _data.Text.Trim();
        _from.Items.Clear(); _to.Items.Clear();
        if (!Directory.Exists(Path.Combine(data, "Terrdata"))) { _status.Text = $"{data}: not a BMS Data folder (no Terrdata)."; return; }
        Save("bms_data_folder.txt", data);
        foreach (var t in BmsTheater.List(data).Where(t => Directory.Exists(t.ObjectDir)))
        {
            bool shared = DbInstaller.IsShared(data, t.ObjectDir);
            _from.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t, ToolTip = t.ObjectDir });
            _to.Items.Add(new ComboBoxItem { Content = t.Name + (shared ? "  (shared: not writable)" : ""), Tag = t, IsEnabled = !shared, ToolTip = t.ObjectDir });
        }
        int Find(ComboBox c, string? n) => c.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Tag is BmsTheater t && string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase) && i.IsEnabled);
        string? from = Read("from.txt"), to = Read("to.txt");
        if (_startTheater != null)
        {
            // the working theater of the main menu is where things are brought; a shared (read only) theater can only be the source
            if (Find(_to, _startTheater) >= 0) { to = _startTheater; if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) from = null; }
            else if (Find(_from, _startTheater) >= 0) from = _startTheater;
            _startTheater = null;
        }
        _from.SelectedIndex = Math.Max(0, Find(_from, from));
        int ti = Find(_to, to);
        if (ti < 0) ti = _to.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.IsEnabled && i.Tag != From);
        _to.SelectedIndex = ti;
        if (From != null && To != null && From.Name == To.Name)
            _from.SelectedIndex = Math.Max(0, _from.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Tag is BmsTheater t && t.Name != To.Name));
        _status.Text = $"{_from.Items.Count} theaters in theater.lst";
    }

    private void LoadLayouts()
    {
        _rows.Clear();
        if (From is not { } s) { FillGrid(); return; }
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var db = BmsDatabase.Load(s.ObjectDir);
            _rows = Interchange.Layouts(s, db).Select(l => new LayoutRow(l.Ocd, l.Name, l.Type >= 0 ? EditorData.TypeName(l.Type) : "", l.Pieces)).ToList();
            _eRows = Interchange.Entities(db).Select(e => new EntityRow(e.Ct, Interchange.KindName(e.Kind), e.Name, e.Detail, "", e.Kind)).ToList();
            _dstNamesOf = null;
            _status.Text = $"{s.Name}: {_rows.Count} layouts, {_eRows.Count} aircraft, vehicles, weapons and unit types ({s.ObjectDir})";
        }
        catch (Exception ex) { _status.Text = "Source not read: " + ex.Message; _eRows.Clear(); }
        finally { Cursor = null; }
        FillGrid();
        MarkInDest();
    }

    private void FillGrid()
    {
        string q = _search.Text.Trim();
        _grid.ItemsSource = _rows.Where(r => q.Length == 0 || r.Ocd.ToString(Ci) == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Type.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Invalidate()
    {
        _current = null;
        _install.IsEnabled = false;
        _eCurrent = null;
        _eInstall.IsEnabled = false;
    }

    private void MakePlan()
    {
        var sel = _grid.SelectedItems.OfType<LayoutRow>().ToList();
        if (From is not { } s || To is not { } d) { _status.Text = "Choose the two theaters."; return; }
        if (sel.Count == 0) { _status.Text = "Choose one or more layouts on the left."; return; }
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var p = Interchange.Make(s, d, _data.Text.Trim(), sel.Select(r => r.Ocd).ToList());
            _current = p.Problems.Any(x => x.StartsWith("Source and destination", StringComparison.Ordinal) || x.StartsWith("The destination uses", StringComparison.Ordinal)) ? null : p;
            var lines = new List<string> { $"PLAN – nothing written yet ({DateTime.Now:HH:mm:ss})", "" };
            lines.AddRange(p.Report);
            if (p.Problems.Count > 0) { lines.Add(""); lines.Add($"To check ({p.Problems.Count}):"); lines.AddRange(p.Problems.Select(x => "  ! " + x)); }
            lines.Add("");
            lines.Add($"New records in {d.Name}: " + string.Join(", ", p.Records.GroupBy(r => r.Table).Select(g => $"{g.Count()} {g.Key} ({g.Min(r => r.Num)}–{g.Max(r => r.Num)})")));
            lines.Add($"Files: {p.Files.Count} (layout folders, models, textures)");
            foreach (var f in p.Files.Keys.OrderBy(x => x).Take(400)) lines.Add("  " + f);
            if (p.Files.Count > 400) lines.Add($"  … {p.Files.Count - 400} more");
            if (p.FeatureCtMap.Count > 0)
            {
                lines.Add(""); lines.Add("Pieces (CT in the source → CT in the destination):");
                foreach (var kv in p.FeatureCtMap.OrderBy(k => k.Key)) lines.Add($"  {kv.Key,6} → {kv.Value,6}{(kv.Key != kv.Value ? "" : "  (same number)")}");
            }
            _report.Text = string.Join("\n", lines);
            _install.IsEnabled = _current != null && p.OcdMap.Count > 0;
            _status.Text = _install.IsEnabled ? "Plan ready: read it, then Install." : "Nothing to install.";
        }
        catch (Exception ex) { _report.Text = "Plan not made: " + ex; }
        finally { Cursor = null; }
    }

    private static bool BmsRunning() => DbInstaller.BmsRunning();

    private void Install()
    {
        if (_current is not { } p) return;
        if (BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"Copy {p.OcdMap.Count} layout(s) from {p.Src.Name} into {p.Dst.Name}?\n\n{p.Records.Count} new records, {p.Files.Count} files.\n" +
                                  $"Everything replaced is copied first to\n{BackupRoot}\nand Restore… removes what is added.", Title,
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            string work = Path.Combine(Root(), "Transfers", $"{Safe(p.Src.Name)}_to_{Safe(p.Dst.Name)}");
            // il piano si rifa' adesso: numeri liberi calcolati sui file attuali della destinazione
            var fresh = Interchange.Make(p.Src, p.Dst, _data.Text.Trim(), p.OcdMap.Keys.ToList());
            Interchange.Write(fresh, work, Path.Combine(work, "out"));
            var log = Interchange.Install(fresh, Path.Combine(work, "out"), BackupRoot);
            var names = fresh.OcdMap.Select(kv => $"  OCD {kv.Key} → OCD {kv.Value}").ToList();
            _report.Text = "INSTALLED\n\n" + string.Join("\n", log) + "\n\nLayouts:\n" + string.Join("\n", names) + "\n\n" + string.Join("\n", fresh.Report) +
                           $"\n\nNext: Objective Studio → open {fresh.Dst.Name} → choose the objective → Replace layout → choose the new layout → Save, Build objects, Install.";
            _status.Text = "Installed. Start BMS once (it builds FALCON4.* again) or use the layout in Objective Studio.";
            Invalidate();
        }
        catch (Exception ex) { MessageBox.Show(this, "Not installed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Cursor = null; }
    }

    // ─────────────────── aircraft, vehicles, weapons

    private UIElement EntitiesTab()
    {
        _kind.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (var k in Enum.GetValues<Interchange.EntityKind>()) _kind.Items.Add(new ComboBoxItem { Content = Interchange.KindName(k), Tag = k });
        _kind.SelectedIndex = 1;
        _eGrid.Columns.Add(new DataGridTextColumn { Header = "CT", Binding = new System.Windows.Data.Binding(nameof(EntityRow.Ct)), Width = new DataGridLength(55) });
        _eGrid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(EntityRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _eGrid.Columns.Add(new DataGridTextColumn { Header = "Kind", Binding = new System.Windows.Data.Binding(nameof(EntityRow.Kind)), Width = new DataGridLength(105) });
        _eGrid.Columns.Add(new DataGridTextColumn { Header = "In destination", Binding = new System.Windows.Data.Binding(nameof(EntityRow.InDest)), Width = new DataGridLength(90) });
        _eGrid.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new System.Windows.Data.Binding(nameof(EntityRow.Detail)), Width = new DataGridLength(220) });

        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_kind, Dock.Left); filter.Children.Add(_kind);
        filter.Children.Add(_eSearch);
        var left = new DockPanel { Width = 640, Margin = new Thickness(6, 0, 6, 6) };
        var lh = new TextBlock
        {
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Text = "Aircraft, helicopters, ground vehicles, ships, weapons and unit types of the source theater – kind, search (name or CT number). " +
                   "Ctrl / Shift + click for more. \"In destination\": a record with the same name is already there.",
        };
        DockPanel.SetDock(lh, Dock.Top); left.Children.Add(lh);
        DockPanel.SetDock(filter, Dock.Top); left.Children.Add(filter);
        left.Children.Add(_eGrid);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_ePlan); bar.Children.Add(_eInstall);
        var restore = Btn("Restore…"); restore.Click += (_, _) => Restore(); bar.Children.Add(restore);
        var help = new TextBlock
        {
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 11.5,
            Text = "Copies the database records and everything they use: aircraft data and sensors, radars, weapons, hardpoint lists, " +
                   "rocket pods, the vehicles of a unit type, squadron stores, 3D models. What the destination already has the same is reused; " +
                   "the rest is added at the end of the tables, and new weapon numbers are added to the same racks in BmsRack.dat. " +
                   "Flight models and the other Sim files are not copied (both theaters use BMS's). Back up your BMS folder first.",
        };
        var right = new DockPanel { Margin = new Thickness(0, 0, 6, 6) };
        DockPanel.SetDock(bar, Dock.Top); right.Children.Add(bar);
        DockPanel.SetDock(help, Dock.Top); right.Children.Add(help);
        right.Children.Add(_eReport);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); body.Children.Add(left);
        body.Children.Add(right);

        _kind.SelectionChanged += (_, _) => FillEntities();
        _eSearch.TextChanged += (_, _) => FillEntities();
        _eGrid.SelectionChanged += (_, _) => { _eCurrent = null; _eInstall.IsEnabled = false; };
        _ePlan.Click += (_, _) => MakeEntityPlan();
        _eInstall.Click += (_, _) => InstallEntities();
        return body;
    }

    /// <summary>"In destination" column: names of the destination's aircraft, vehicles, weapons and unit types.</summary>
    private void MarkInDest()
    {
        if (To is not { } d || _eRows.Count == 0) { FillEntities(); return; }
        if (_dstNamesOf != d.ObjectDir)
        {
            try { _dstNames = Interchange.Entities(BmsDatabase.Load(d.ObjectDir)).Select(e => e.Kind + "|" + e.Name).ToHashSet(StringComparer.Ordinal); }
            catch (Exception) { _dstNames.Clear(); }
            _dstNamesOf = d.ObjectDir;
        }
        _eRows = _eRows.Select(r => r with { InDest = _dstNames.Contains(r.K + "|" + r.Name) ? "same name" : "" }).ToList();
        FillEntities();
    }

    private void FillEntities()
    {
        string q = _eSearch.Text.Trim();
        var k = (_kind.SelectedItem as ComboBoxItem)?.Tag as Interchange.EntityKind?;
        _eGrid.ItemsSource = _eRows.Where(r => (k == null || r.K == k) &&
            (q.Length == 0 || r.Ct.ToString(Ci) == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void MakeEntityPlan()
    {
        var sel = _eGrid.SelectedItems.OfType<EntityRow>().ToList();
        if (From is not { } s || To is not { } d) { _status.Text = "Choose the two theaters."; return; }
        if (sel.Count == 0) { _status.Text = "Choose one or more items on the left."; return; }
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var cts = sel.Select(r => r.Ct).ToList();
            var p = Interchange.MakeEntities(s, d, _data.Text.Trim(), cts);
            bool blocked = p.Problems.Any(x => x.StartsWith("Source and destination", StringComparison.Ordinal) || x.StartsWith("The destination uses", StringComparison.Ordinal));
            _eCurrent = blocked ? null : p;
            _eCurrentCts = cts;
            var lines = new List<string> { $"PLAN – nothing written yet ({DateTime.Now:HH:mm:ss})", "" };
            lines.AddRange(p.Report);
            if (p.Problems.Count > 0) { lines.Add(""); lines.Add($"To check ({p.Problems.Count}):"); lines.AddRange(p.Problems.Select(x => "  ! " + x)); }
            lines.Add("");
            lines.Add($"New records in {d.Name}: " + (p.Records.Count == 0 ? "none" :
                string.Join(", ", p.Records.GroupBy(r => r.Table).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key} ({g.Min(r => r.Num)}–{g.Max(r => r.Num)})"))));
            lines.Add($"Files: {p.Files.Count} (models, textures, BmsRack.dat)");
            foreach (var f in p.Files.Keys.OrderBy(x => x).Take(400)) lines.Add("  " + f);
            if (p.Files.Count > 400) lines.Add($"  … {p.Files.Count - 400} more");
            _eReport.Text = string.Join("\n", lines);
            _eInstall.IsEnabled = _eCurrent != null && (p.Records.Count > 0 || p.Files.Count > 0);
            _status.Text = _eInstall.IsEnabled ? "Plan ready: read it, then Install." : "Nothing to install.";
        }
        catch (Exception ex) { _eReport.Text = "Plan not made: " + ex; }
        finally { Cursor = null; }
    }

    private void InstallEntities()
    {
        if (_eCurrent is not { } p) return;
        if (BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"Copy {_eCurrentCts.Count} item(s) from {p.Src.Name} into {p.Dst.Name}?\n\n{p.Records.Count} new records, {p.Files.Count} files.\n" +
                                  $"Everything replaced is copied first to\n{BackupRoot}\nand Restore… removes what is added.\n\n" +
                                  "Make a backup copy of your BMS folder too before changing the database.", Title,
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            string work = Path.Combine(Root(), "Transfers", $"{Safe(p.Src.Name)}_to_{Safe(p.Dst.Name)}_items");
            // il piano si rifa' adesso: numeri liberi calcolati sui file attuali della destinazione
            var fresh = Interchange.MakeEntities(p.Src, p.Dst, _data.Text.Trim(), _eCurrentCts);
            Interchange.Write(fresh, work, Path.Combine(work, "out"));
            var log = Interchange.Install(fresh, Path.Combine(work, "out"), BackupRoot);
            _eReport.Text = "INSTALLED\n\n" + string.Join("\n", log) + "\n\n" + string.Join("\n", fresh.Report) +
                            (fresh.Problems.Count > 0 ? "\n\nTo check:\n" + string.Join("\n", fresh.Problems.Select(x => "  ! " + x)) : "") +
                            $"\n\nNext: start BMS once (it builds FALCON4.* again). Use the new units in {fresh.Dst.Name} with the Campaign Editor (squadrons, battalions) or the Mission Editor.";
            _status.Text = "Installed. Start BMS once: it builds FALCON4.* again.";
            Invalidate();
            _dstNamesOf = null;
            MarkInDest();
        }
        catch (Exception ex) { MessageBox.Show(this, "Not installed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Cursor = null; }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    private void Restore()
    {
        var b = Interchange.Backups(BackupRoot);
        if (b.Count == 0) { MessageBox.Show(this, "No installations yet.", Title); return; }
        var w = new Window { Title = "Restore an installation", Owner = this, Width = 900, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var lb = new ListBox { FontFamily = new FontFamily("Consolas") };
        foreach (var x in b) lb.Items.Add($"{x.Time:yyyy-MM-dd HH:mm:ss}   {x.Summary}");
        var ok = new Button { Content = "Restore", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        int pick = -1;
        ok.Click += (_, _) => { pick = lb.SelectedIndex; w.DialogResult = true; };
        var dp = new DockPanel { Margin = new Thickness(10) };
        var t = new TextBlock { Text = "Removes the files that installation added and puts back the ones it replaced (database tables included). " +
                                       "If you installed other changes into the same theater after it, restore those first (newest first).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(t, Dock.Top); dp.Children.Add(t);
        DockPanel.SetDock(ok, Dock.Bottom); dp.Children.Add(ok);
        dp.Children.Add(lb);
        w.Content = dp;
        if (w.ShowDialog() != true || pick < 0) return;
        if (BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            var log = Interchange.Restore(b[pick].Dir);
            _report.Text = "RESTORED\n\n" + string.Join("\n", log);
        }
        catch (Exception ex) { MessageBox.Show(this, "Not restored: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
