using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Database;
using TerrainTool.Core.Project;

namespace DatabaseInterchange;

/// <summary>
/// Aircraft, vehicles &amp; weapons tab → "From an old add-on into a TerrainTool project…": what the project's old add-on has and BMS
/// has not, with what can be brought in (with BMS's 3D models or its own ones, added to BMS's models) and what not. The ticks are saved in the
/// project's imports.csv; Run Objects (Objective Studio) brings them in at every run.
/// </summary>
public sealed class AddOnImportWindow : Window
{
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _pick;
        public AddOnImport.Candidate C { get; init; } = null!;
        public bool Pick { get => _pick; set { if (_pick == value) return; _pick = value && C.CanImport; PropertyChanged?.Invoke(this, new(nameof(Pick))); } }
        public bool Enabled => C.CanImport;
        private bool _own;
        /// <summary>Use the add-on's 3D model (added to BMS's models): always for an own model, a choice when BMS has a different one.</summary>
        public bool Own { get => _own || C.Fit == AddOnImport.Fit.OwnModel; set { if (_own == value) return; _own = value && C.OwnModelPossible; PropertyChanged?.Invoke(this, new(nameof(Own))); PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public bool OwnEnabled => C.OwnModelPossible && C.Fit == AddOnImport.Fit.OtherModel;
        public string Kind => C.Kind;
        public string Name => C.Name;
        public int Ct => C.Ct;
        public string What => C.What;
        public string Note => C.Note;
        public string State => !C.CanImport ? "not possible" : C.Fit == AddOnImport.Fit.OtherModel && !Own ? "check" : C.Fit == AddOnImport.Fit.OwnModel || Own ? "own model" : "ok";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly TheaterProject _p;
    private readonly List<Row> _rows = new();
    private readonly ICollectionView _view;
    private readonly ComboBox _kind = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _search = new() { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Search by name" };
    private readonly CheckBox _all = new() { Content = "Show also what cannot be brought in", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public AddOnImportWindow(TheaterProject p, string bmsData)
    {
        _p = p;
        Title = "Aircraft & vehicles from the old add-on";
        Width = 1200; Height = 760; MinWidth = 760; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddOnImport.Analysis a;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { a = AddOnImport.Analyze(p.OldTheaterFolder, bmsData, AddOnModels.LoadAssigned(p.ProjectFolder), AddOnModels.LoadInstalled(p.ProjectFolder)); }
        finally { Cursor = null; }
        var savedList = AddOnImport.Load(p).Where(c => string.Equals(c.Source, p.OldTheaterFolder, StringComparison.OrdinalIgnoreCase)).ToList();
        var saved = savedList.Select(c => c.Name).ToHashSet();
        foreach (var c in a.Candidates) _rows.Add(new Row { C = c, Pick = saved.Contains(c.Name), Own = savedList.Any(x => x.Name == c.Name && x.OwnModel) });
        var lost = saved.Where(n => _rows.All(r => r.Name != n)).ToList();

        var head = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = $"{p.OldTheaterFolder}: {a.Version}. {a.Candidates.Count} entries the add-on has and BMS has not (by name), " +
                   $"{a.Candidates.Count(c => c.CanImport)} can be brought in now.\n" +
                   "Brought in: the chosen record with BMS's own versions of what it uses (weapons, radars, sensors, flight data with the same name stay BMS's), " +
                   "renumbered on the BMS version in use at every 'Run Objects'. Own 3D models (blue): added to BMS's models by 'Install objects', with their number or a free one, " +
                   "and their textures when BMS has none with that number; never replacing anything of BMS (a model whose texture number BMS uses for another texture cannot be brought: renumbering it would mean changing the model file). " +
                   "Yellow: BMS has a different model with that number; tick 'Add-on model' to use the add-on's one. " +
                   "Flyable aircraft stay BMS's: a variant flies like the BMS aircraft whose flight data it uses. Unit types (squadrons, battalions) bring their vehicles: tick the squadron to use the aircraft in a campaign.",
        };
        _kind.Items.Add("All kinds");
        foreach (var k in a.Candidates.Select(c => c.Kind).Distinct().OrderBy(k => k)) _kind.Items.Add(k);
        _kind.SelectedIndex = 0;

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var pickStyle = new Style(typeof(CheckBox));
        pickStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(Row.Enabled))));
        pickStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Bring", Binding = new Binding(nameof(Row.Pick)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            ElementStyle = pickStyle, EditingElementStyle = pickStyle, Width = 50,
        });
        var ownStyle = new Style(typeof(CheckBox));
        ownStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(Row.OwnEnabled))));
        ownStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = new TextBlock { Text = "Add-on model", ToolTip = "Use the add-on's 3D model instead of BMS's different model with the same number: it is added to BMS's models with a free number (nothing of BMS is replaced). Always on for an aircraft or vehicle with its own model." },
            Binding = new Binding(nameof(Row.Own)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            ElementStyle = ownStyle, EditingElementStyle = ownStyle, Width = 85,
        });
        void Col(string h, string path, double w) => grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(path), Width = new DataGridLength(w), IsReadOnly = true });
        Col("", nameof(Row.State), 80);
        Col("Kind", nameof(Row.Kind), 140);
        Col("Name", nameof(Row.Name), 190);
        Col("CT", nameof(Row.Ct), 55);
        Col("3D model / contents", nameof(Row.What), 360);
        Col("Note", nameof(Row.Note), 420);
        var rs = new Style(typeof(DataGridRow));
        void Tint(string state, Color bg, Color fg)
        {
            var t = new DataTrigger { Binding = new Binding(nameof(Row.State)), Value = state };
            t.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(bg)));
            t.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(fg)));
            rs.Triggers.Add(t);
        }
        Tint("check", Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0x7A, 0x52, 0x00));
        Tint("not possible", Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0x80, 0x80, 0x80));
        Tint("own model", Color.FromRgb(0xE6, 0xF0, 0xFB), Color.FromRgb(0x1A, 0x4C, 0x80));
        grid.RowStyle = rs;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is Row r && (_all.IsChecked == true || r.C.CanImport || r.Pick)
                            && (_kind.SelectedIndex <= 0 || r.Kind == (string)_kind.SelectedItem)
                            && (_search.Text.Trim().Length == 0 || r.Name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        grid.ItemsSource = _view;
        _kind.SelectionChanged += (_, _) => _view.Refresh();
        _search.TextChanged += (_, _) => _view.Refresh();
        _all.Checked += (_, _) => _view.Refresh();
        _all.Unchecked += (_, _) => _view.Refresh();

        var tick = new Button { Content = "Tick selected", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        var untick = new Button { Content = "Untick selected", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        tick.Click += (_, _) => { foreach (var r in grid.SelectedItems.OfType<Row>()) r.Pick = true; Count(); };
        untick.Click += (_, _) => { foreach (var r in grid.SelectedItems.OfType<Row>()) r.Pick = false; Count(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_kind); bar.Children.Add(_search); bar.Children.Add(_all); bar.Children.Add(tick); bar.Children.Add(untick);

        var save = new Button { Content = "Save choice", FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        save.Click += (_, _) =>
        {
            var keep = AddOnImport.Load(p).Where(c => !string.Equals(c.Source, p.OldTheaterFolder, StringComparison.OrdinalIgnoreCase)).ToList();
            keep.AddRange(_rows.Where(r => r.Pick).Select(r => new AddOnImport.Choice(p.OldTheaterFolder, r.Ct, r.Name, r.Own)));
            try
            {
                AddOnImport.Save(p, keep);
                _status.Text = $"Saved {keep.Count} entr{(keep.Count == 1 ? "y" : "ies")} in {AddOnImport.FilePath(p)}. Next: Objective Studio → Run Objects (the log lists what was brought in and with which numbers), " +
                               "Build theater, Install objects. To use them in a campaign: Campaign Editor → Convert unit types, choose the new unit types, Apply object changes.";
            }
            catch (Exception ex) { _status.Text = "Not saved: " + ex.Message; }
        };
        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var remove = new Button { Content = "Remove imported models from BMS…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 18, 0),
            ToolTip = @"Takes away from BMS's Terrdata\objects the model and texture files that 'Install objects' added for this project (Output\BmsBackup\imported_files.txt). BMS's own files are never touched." };
        remove.Click += (_, _) =>
        {
            int n = AddOnModels.LoadInstalled(p.ProjectFolder).Count;
            if (n == 0) { _status.Text = "No imported model files are installed in BMS for this project."; return; }
            if (MessageBox.Show(this, $"Remove the {n} model and texture files this project added to BMS's Terrdata\\objects?\n\nClose BMS first. The aircraft that use them will have no 3D model until you install them again.",
                    "Remove imported models", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { _status.Text = string.Join("\n", AddOnModels.Remove(p.ProjectFolder, System.IO.Path.Combine(bmsData, "Terrdata", "objects"))); }
            catch (Exception ex) { _status.Text = "Not removed: " + ex.Message; }
        };
        btns.Children.Add(remove); btns.Children.Add(save); btns.Children.Add(close);
        DockPanel.SetDock(btns, Dock.Right);
        bottom.Children.Add(btns);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(grid);
        Content = root;
        foreach (var r in _rows) r.PropertyChanged += (_, _) => Count();
        Count();
        if (a.Problems.Count > 0) _status.Text = string.Join("\n", a.Problems);
        if (lost.Count > 0) _status.Text += (lost.Count > 0 ? "\n" : "") + "In imports.csv but no longer in the add-on or now in BMS: " + string.Join(", ", lost) + " (removed when you save).";
    }

    private void Count()
    {
        int n = _rows.Count(r => r.Pick);
        Title = $"Aircraft & vehicles from the old add-on – {n} ticked";
    }
}
