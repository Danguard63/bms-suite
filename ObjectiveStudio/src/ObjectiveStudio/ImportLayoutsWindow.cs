using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Database;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace ObjectiveStudio;

/// <summary>
/// Layouts (bases and ground objectives) of another theater installed in BMS, copied into this theater's database with
/// everything they need (the same engine as Database Interchange), so 'Replace layout' and 'Replace airbase' can use them
/// right away. Backup first; Restore removes what was added.
/// </summary>
public sealed class ImportLayoutsWindow : Window
{
    private sealed record Row(int Ocd, string Name, string Type, int Pieces);

    private readonly TheaterProject _p;
    private readonly BmsTheater _dst;
    private readonly ComboBox _from = new() { MinWidth = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro,
    };
    private readonly TextBox _report = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private List<Row> _rows = new();

    /// <summary>Set when layouts were installed: the theater's database changed (the editor reloads it).</summary>
    public bool Changed { get; private set; }

    private static string BackupRoot => Path.Combine(MainWindow.Root(), "Imports", "Backups");

    public ImportLayoutsWindow(TheaterProject p, BmsTheater dst)
    {
        _p = p; _dst = dst;
        Title = $"Bring layouts from another theater into {dst.Name}";
        Width = 1250; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "From ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_from);
        foreach (var t in BmsTheater.List(dst.DataDir).Where(t => Directory.Exists(t.ObjectDir) && !string.Equals(Path.GetFullPath(t.ObjectDir), Path.GetFullPath(dst.ObjectDir), StringComparison.OrdinalIgnoreCase)))
            _from.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t, ToolTip = t.ObjectDir });

        void Col(string h, string path, double w, bool star = false) => _grid.Columns.Add(new DataGridTextColumn
        { Header = h, Binding = new System.Windows.Data.Binding(path), Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
        Col("OCD", nameof(Row.Ocd), 55); Col("Name", nameof(Row.Name), 200, true); Col("Type", nameof(Row.Type), 110); Col("Pieces", nameof(Row.Pieces), 55);

        var left = new DockPanel { Width = 520, Margin = new Thickness(0, 0, 8, 0) };
        var lh = new TextBlock { Text = "Layouts of that theater – search (name, type or OCD); Ctrl / Shift + click for more", Foreground = Brushes.DimGray };
        DockPanel.SetDock(lh, Dock.Top); left.Children.Add(lh);
        DockPanel.SetDock(_search, Dock.Top); left.Children.Add(_search);
        left.Children.Add(_grid);

        var plan = new Button { Content = "Check (nothing written)", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var install = new Button { Content = "Bring them into this theater…", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var restore = new Button { Content = "Restore…", Padding = new Thickness(10, 3, 10, 3) };
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), Children = { plan, install, restore } };
        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); right.Children.Add(bar);
        right.Children.Add(_report);

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
            Text = $"The layouts chosen are copied into {dst.Name}'s database ({dst.ObjectDir}) with their pieces, 3D models and textures; what the theater already has the same is reused, " +
                   "the rest gets free numbers. Every file replaced is saved first (Restore). Then 'Replace layout' or 'Replace airbase' in the objective editor offers them.",
        };
        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); body.Children.Add(left);
        body.Children.Add(right);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _from.SelectionChanged += (_, _) => LoadLayouts();
        _search.TextChanged += (_, _) => Fill();
        plan.Click += (_, _) => Plan();
        install.Click += (_, _) => Install();
        restore.Click += (_, _) => Restore();
        if (_from.Items.Count > 0) _from.SelectedIndex = 0; else _status.Text = "No other theater with a database in BMS's theater list.";
    }

    private BmsTheater? From => (_from.SelectedItem as ComboBoxItem)?.Tag as BmsTheater;

    private void LoadLayouts()
    {
        _rows.Clear();
        if (From is { } s)
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var db = BmsDatabase.Load(s.ObjectDir);
                _rows = Interchange.Layouts(s, db).Select(l => new Row(l.Ocd, l.Name, l.Type >= 0 ? EditorData.TypeName(l.Type) : "", l.Pieces)).ToList();
                _status.Text = $"{s.Name}: {_rows.Count} layouts";
            }
            catch (Exception ex) { _status.Text = "Not read: " + ex.Message; }
            finally { Cursor = null; }
        }
        Fill();
    }

    private void Fill()
    {
        string q = _search.Text.Trim();
        _grid.ItemsSource = _rows.Where(r => q.Length == 0 || r.Ocd.ToString(CultureInfo.InvariantCulture) == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Type.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<int> Chosen() => _grid.SelectedItems.OfType<Row>().Select(r => r.Ocd).ToList();

    private Interchange.Plan? Make(bool show)
    {
        if (From is not { } s) return null;
        var ocds = Chosen();
        if (ocds.Count == 0) { _status.Text = "Choose one or more layouts on the left."; return null; }
        var p = Interchange.Make(s, _dst, _dst.DataDir, ocds);
        if (show)
        {
            var lines = new List<string> { $"CHECK – nothing written yet ({DateTime.Now:HH:mm:ss})", "" };
            lines.AddRange(p.Report);
            if (p.Problems.Count > 0) { lines.Add(""); lines.Add($"To check ({p.Problems.Count}):"); lines.AddRange(p.Problems.Select(x => "  ! " + x)); }
            lines.Add("");
            lines.Add($"New records: " + string.Join(", ", p.Records.GroupBy(r => r.Table).Select(g => $"{g.Count()} {g.Key}")) + $"; files: {p.Files.Count}");
            _report.Text = string.Join("\n", lines);
        }
        return p;
    }

    private void Plan()
    {
        Cursor = System.Windows.Input.Cursors.Wait;
        try { Make(true); }
        catch (Exception ex) { _report.Text = "Not checked: " + ex.Message; }
        finally { Cursor = null; }
    }

    private void Install()
    {
        if (From is not { } s || Chosen().Count == 0) { _status.Text = "Choose one or more layouts on the left."; return; }
        if (DbInstaller.BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"Copy {Chosen().Count} layout(s) of {s.Name} into {_dst.Name}?\n\nEvery file replaced is saved first; Restore… removes what is added.", Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var p = Make(false);
            if (p == null) return;
            if (p.Problems.Any(x => x.StartsWith("Source and destination", StringComparison.Ordinal) || x.StartsWith("The destination uses", StringComparison.Ordinal)))
            { _report.Text = "Not installed:\n" + string.Join("\n", p.Problems); return; }
            string work = Path.Combine(MainWindow.Root(), "Imports", $"{CampaignWorkspace.SafeName(s.Name)}_to_{CampaignWorkspace.SafeName(_dst.Name)}");
            Interchange.Write(p, work, Path.Combine(work, "out"));
            var log = Interchange.Install(p, Path.Combine(work, "out"), BackupRoot);
            _report.Text = "INSTALLED\n\n" + string.Join("\n", log) + "\n\nLayouts:\n" + string.Join("\n", p.OcdMap.Select(kv => $"  OCD {kv.Key} → OCD {kv.Value}")) +
                           "\n\nNow 'Replace layout' or 'Replace airbase' in the objective editor offers them; then Build objects and Install into BMS.";
            _status.Text = "✓ Brought into " + _dst.Name + ".";
            Changed = true;
        }
        catch (Exception ex) { MessageBox.Show(this, "Not installed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Cursor = null; }
    }

    private void Restore()
    {
        var b = Interchange.Backups(BackupRoot);
        if (b.Count == 0) { MessageBox.Show(this, "Nothing brought in yet.", Title); return; }
        var lb = new ListBox { FontFamily = new FontFamily("Consolas"), Height = 260 };
        foreach (var x in b) lb.Items.Add($"{x.Time:yyyy-MM-dd HH:mm:ss}   {x.Summary}");
        lb.SelectedIndex = 0;
        var ok = new Button { Content = "Restore", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var w = new Window { Title = "Restore", Owner = this, Width = 860, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(10), Children = { new TextBlock { Text = "Removes the files that installation added and puts back the ones it replaced. Newest first if there are more.", TextWrapping = TextWrapping.Wrap }, lb, ok } } };
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true || lb.SelectedIndex < 0) return;
        if (DbInstaller.BmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { _report.Text = "RESTORED\n\n" + string.Join("\n", Interchange.Restore(b[lb.SelectedIndex].Dir)); Changed = true; }
        catch (Exception ex) { MessageBox.Show(this, "Not restored: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
