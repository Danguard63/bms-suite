using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace ObjectiveStudio;

/// <summary>
/// Editor degli obiettivi (finestra separata): tabella filtrabile per tipo e nome, mappa dall'alto sulla foto Photoreal
/// con la sagoma vera di ogni pezzo (Parent.dat dei modelli BMS), spostamento e rotazione in blocco dell'obiettivo,
/// conversione in incrocio. Le modifiche vanno in objective_edits.csv; le applica il lavoro Objects.
/// </summary>
public sealed class ObjectiveEditorWindow : Window
{
    private readonly EditorData _d;
    private readonly EditorMap _map;
    private readonly DataGrid _grid = new();
    private readonly ComboBox _typeFilter = new() { MinWidth = 140 };
    private readonly TextBox _search = new() { MinWidth = 140, ToolTip = "Part of the name, or the CampId. 'blocked' = the objectives with a ground link blocked by water (tick Links first)." };
    private readonly CheckBox _onlyChanged = new() { Content = "Only edited", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2), FontFamily = new FontFamily("Consolas") };
    private readonly Button _junction = new() { Content = "Convert to junction", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _reset = new() { Content = "Reset", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _forget = new() { Content = "Forget saved edit", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Button _preview = new() { Content = "3D preview", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Real BMS models on the real terrain and photo, as placed here (also before saving). Key 3." };
    private Preview3DWindow? _preview3d;
    private readonly Button _fly = new() { Content = "Fly over…", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Free camera over all the objectives, starting from the selected one: BMS models, textures and runway numbers on the real terrain, with the edits made here (also before saving)" };
    private FlyOverWindow? _flyOver;
    private readonly Button _korea = new() { Content = "Replace base…", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Replace this airbase with a complete base, placed on its runway: a BMS 4.38 Korean base, or a base of this theater (also one imported with Database Interchange)" };
    private readonly Button _replace = new() { Content = "Replace layout…", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3),
        ToolTip = "Put another layout (OCD) of the add-on or of BMS 4.38 on this objective: a longer bridge for a wide river, a port inland that becomes a town… (only this objective changes)" };
    private ReplaceLayoutWindow? _replaceWin;
    private readonly Button _turn = new() { Content = "Turn 180°", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3), ToolTip = "Turn the base around its main runway: same runway, buildings on the other side" };
    private readonly Button _unkorea = new() { Content = "Back to its own base", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
    private readonly TextBox _rwyHeading = new() { Width = 70, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "Runway direction you want (degrees, map grid; 0 = up). Either end works: 130 and 310 are the same runway." };
    private readonly Button _alignReal = new() { Content = "Align to real runway", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0),
        ToolTip = "Turns and moves the base so its main runway lies on the real one (airbase_targets.csv: OurAirports or OpenStreetMap)" };
    private readonly Button _setHeading = new() { Content = "Set runway heading", Padding = new Thickness(8, 3, 8, 3),
        ToolTip = "Turns the base around the centre of its main runway until the runway has this direction" };
    private readonly Button _save = new() { Content = "Save edits", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
    private readonly ICollectionView _view;
    private bool _syncing;

    public ObjectiveEditorWindow(TheaterProject p)
    {
        Title = "Objective editor – " + p.Theater.Name;
        Width = 1400; Height = 900;
        _d = new EditorData(p);
        _map = new EditorMap(_d, p.Theater.CreateGrid());

        // ── colonna sinistra: filtri, tabella, scheda dell'obiettivo
        _typeFilter.Items.Add(new TypeItem(-1, $"All types ({_d.Objectives.Count})"));
        foreach (int t in _d.TypesPresent)
            _typeFilter.Items.Add(new TypeItem(t, $"{EditorData.TypeName(t)} ({_d.Objectives.Count(o => o.Type == t)})"));
        _typeFilter.SelectedIndex = 0;

        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.CanUserAddRows = false;
        _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;   // nomi lunghi: la tabella scorre di lato invece di schiacciarli
        _grid.CanUserResizeColumns = true;
        _grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding(nameof(EdObjective.Id)), Width = new DataGridLength(55) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(EdObjective.Name)), Width = new DataGridLength(230) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(EdObjective.TypeName)), Width = new DataGridLength(85) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(EdObjective.State)), Width = new DataGridLength(85) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Real rwy (true)", Binding = new Binding(nameof(EdObjective.RealText)), Width = new DataGridLength(95) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Δ°", Binding = new Binding(nameof(EdObjective.DeltaText)), Width = new DataGridLength(48) });
        _view = CollectionViewSource.GetDefaultView(_d.Objectives);
        _view.Filter = Filter;
        _grid.ItemsSource = _view;

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        filters.Children.Add(_typeFilter);
        filters.Children.Add(_onlyChanged);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var sl = new TextBlock { Text = "Search ", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sl, Dock.Left);
        searchRow.Children.Add(sl);
        searchRow.Children.Add(_search);

        var buttons = new WrapPanel();
        buttons.Children.Add(_junction);
        buttons.Children.Add(_reset);
        buttons.Children.Add(_forget);
        buttons.Children.Add(_korea);
        buttons.Children.Add(_replace);
        buttons.Children.Add(_turn);
        buttons.Children.Add(_unkorea);
        buttons.Children.Add(_preview);
        var pieces = new Button { Content = "Pieces…", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3),
            ToolTip = "Piece inspector: the buildings of the selected objective with their 3D model, textures and data (hit points, damage, repair time, how many objectives use them)." };
        pieces.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective so) { _status.Text = "Select an objective first."; return; }
            PieceInspector.Open(this, _d, so);
        };
        buttons.Children.Add(pieces);
        var lists = new Button { Content = "Runways, points & areas…", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 8, 3),
            ToolTip = "The point lists of the selected objective as BMS uses them: runway taxi routes (one per landing direction), runway outlines, SAM/AAA batteries, parking… with all their data, and on the drawing of the base the leveling, land cover, special polygons and trees." };
        lists.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective so) { _status.Text = "Select an objective first."; return; }
            new PointListsWindow(_d, so) { Owner = this }.Show();
        };
        buttons.Children.Add(lists);
        buttons.Children.Add(_fly);
        buttons.Children.Add(_save);
        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0),
            Text = "Map: wheel = zoom, right or middle drag = pan, click = select, drag = move the whole objective, " +
                   "Ctrl+drag = rotate it (airbases turn around the centre of their main runway; Shift = runway at whole degrees; the protractor and the extended runway axis help to match the photo). " +
                   "'Runway heading' + Enter = exact direction. Keys: arrows = move 10 ft (Shift 100), Q/E = rotate 1° (Shift 10°, Ctrl 0.1°), " +
                   "Ctrl+Z = undo, F = centre on the selection, L = labels, 3 = 3D preview. 'Replace base…' puts a complete base (BMS 4.38 Korean or of this theater) on this airbase's runway " +
                   "(Turn 180° = buildings on the other side). 'Save edits' writes objective_edits.csv (and base_swaps.csv) in the work folder; then Build objects and Install (main window).",
        };

        var left = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(filters, Dock.Top); left.Children.Add(filters);
        DockPanel.SetDock(searchRow, Dock.Top); left.Children.Add(searchRow);
        var bottom = new StackPanel();
        bottom.Children.Add(_info);
        var hdgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        hdgRow.Children.Add(new TextBlock { Text = "Runway heading ", VerticalAlignment = VerticalAlignment.Center });
        hdgRow.Children.Add(_rwyHeading);
        hdgRow.Children.Add(_setHeading);
        hdgRow.Children.Add(_alignReal);
        var report = new Button { Content = "Alignment report…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Every airbase with the distance of its main runway from the real one, worst first (red = off, yellow = to check). Click a row to see the base here." };
        report.Click += (_, _) =>
        {
            if (_alignWin is { IsLoaded: true }) { _alignWin.Activate(); return; }
            _alignWin = new AlignmentWindow(_d, this) { Owner = this };
            _alignWin.Show();
        };
        hdgRow.Children.Add(report);
        bottom.Children.Add(hdgRow);
        bottom.Children.Add(buttons);
        bottom.Children.Add(help);
        DockPanel.SetDock(bottom, Dock.Bottom); left.Children.Add(bottom);
        left.Children.Add(_grid);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470), MinWidth = 260 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(left, 0); root.Children.Add(left);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
        Grid.SetColumn(split, 1); root.Children.Add(split);
        var mapBorder = new Border { Child = _map, Background = Brushes.Black };
        var bar = new WrapPanel { Margin = new Thickness(6, 3, 6, 3) };
        CheckBox Toggle(string text, bool on, Action<bool> set, string tip)
        {
            var cb = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            cb.Checked += (_, _) => { set(true); _map.InvalidateVisual(); };
            cb.Unchecked += (_, _) => { set(false); _map.InvalidateVisual(); };
            return cb;
        }
        bar.Children.Add(Toggle("Buildings", true, v => _map.ShowBuildings = v, "Shapes of the 3D pieces (real size from the BMS models). The selected objective always shows them."));
        bar.Children.Add(Toggle("Taxiways", true, v => _map.ShowTaxi = v, "Taxi routes of the airbases (light blue)"));
        bar.Children.Add(Toggle("Parking / defence points", true, v => _map.ShowPoints = v, "Parking spots (green), SAM launcher points (red), AAA gun points (orange), radar (pink), support vehicles and others (grey)"));
        bar.Children.Add(Toggle("Names", true, v => _map.ShowLabels = v, "Objective names (key L)"));
        bar.Children.Add(Toggle("Links", false, v => _map.ShowLinks = v, "Links between objectives (the road network of the ground units, from the campaign Te_New_Nt.tac): grey = ground, blue dashed = not for ground units, orange = one way, RED dashed = blocked by water on the BMS campaign map (the ground units cannot use it). The selected objective's links in yellow; MAGENTA = longer than 50 km (with its length); green = changed here. Right click (without dragging) on a link: Add intersection here / Remove link; on an objective: Link from it… then click another objective (adds the link, or removes it). Saved at once in network_edits.json; written into the campaign by Apply object changes in the Campaign Editor."));
        bar.Children.Add(Toggle("Hierarchy", false, v => _map.ShowTree = v, "Parent tree of the campaign (as in TacEdit): red ring = primary objective (PO), orange ring = secondary (SO); lines go from each objective to its parent (red SO→PO, orange third level→SO, green fourth level). The selected objective's parents and children in yellow."));
        bar.Children.Add(Toggle("Water (BMS)", false, v => _map.ShowWater = v, "Where BMS 4.38 has water: water surface (WaterHeightMap) above the ground (HeightMap), the same rule the tool uses for bridges, ports and vehicles. Blue = water, cyan line = shore. Useful on marshes and shallow water where the photo shows the bottom."));
        // pennello dell'acqua
        var brushMode = new ComboBox { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120,
            ToolTip = "Water brush: paint inland water where BMS has none (rivers missing or cut), or remove wrong water. Left drag = paint, Ctrl+Z = undo the last stroke. Saved at once in water_paint.csv; it goes into the terrain at the next 'Run Water DEM' (then the texture jobs and Assemble)." };
        foreach (var t in new[] { "Water brush: off", "Add water", "Remove water" }) brushMode.Items.Add(t);
        brushMode.SelectedIndex = 0;
        var brushSize = new ComboBox { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, ToolTip = "Brush diameter (the Watermap has about 62 m per pixel)" };
        foreach (var m in new[] { 60, 120, 250, 500, 1000, 2000, 5000 }) brushSize.Items.Add(m >= 1000 ? $"{m / 1000} km" : $"{m} m");
        brushSize.SelectedIndex = 2;
        brushMode.SelectionChanged += (_, _) => { _map.Brush = (BrushMode)brushMode.SelectedIndex; _map.Focus(); };
        brushSize.SelectionChanged += (_, _) => { _map.BrushM = new[] { 60, 120, 250, 500, 1000, 2000, 5000 }[Math.Max(0, brushSize.SelectedIndex)]; _map.InvalidateVisual(); };
        // the water brush changes the terrain: only for a TerrainTool project (theater conversion)
        if (!p.IsInstalledTheater) { bar.Children.Add(brushMode); bar.Children.Add(brushSize); }
        bar.Children.Add(Toggle("Protractor", true, v => _map.ShowProtractor = v, "Protractor around the main runway of the selected airbase, with the runway axis extended over the photo (always shown while rotating with Ctrl)"));
        bar.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray,
            Text = "White = runway   light blue = taxiways   green = parking   red = SAM   orange = AAA   pink = radar   grey = support   yellow = selected" });
        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        right.Children.Add(mapBorder);
        Grid.SetColumn(right, 2); root.Children.Add(right);
        var statusBar = new Border { Child = _status, Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)) };
        Grid.SetRow(statusBar, 1); Grid.SetColumnSpan(statusBar, 3); root.Children.Add(statusBar);
        Content = root;

        // ── eventi
        _typeFilter.SelectionChanged += (_, _) => _view.Refresh();
        _search.TextChanged += (_, _) => _view.Refresh();
        _onlyChanged.Checked += (_, _) => _view.Refresh();
        _onlyChanged.Unchecked += (_, _) => _view.Refresh();
        _grid.SelectionChanged += (_, _) =>
        {
            if (_syncing || _grid.SelectedItem is not EdObjective o) return;
            _map.Select(o, center: true);
            ShowInfo();
        };
        _map.SelectionChanged += o =>
        {
            _syncing = true;
            try
            {
                if (o != null && !_view.Contains(o)) { _typeFilter.SelectedIndex = 0; _search.Text = ""; _onlyChanged.IsChecked = false; }
                _grid.SelectedItem = o;
                if (o != null) _grid.ScrollIntoView(o);
            }
            finally { _syncing = false; }
            ShowInfo();
        };
        _map.Edited += _ => ShowInfo();
        _map.TreeLoaded += () => { ShowInfo(); if (_search.Text.Trim().Equals("blocked", StringComparison.OrdinalIgnoreCase)) _view.Refresh(); };
        _map.HoverText += t => _status.Text = t;
        _junction.Click += (_, _) => OnJunction();
        _reset.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o) return;
            _map.PushUndo(o);
            _d.Reset(o);
            AfterEdit();
        };
        _forget.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o || !o.HadEdit) return;
            if (MessageBox.Show(this, $"Remove the saved edit of '{o.Name}' from objective_edits.csv?\nAt the next Objects run it goes back where the tool puts it.",
                    "Objective editor", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _d.ForgetEdit(o);
            AfterEdit();
        };
        _korea.Click += (_, _) => OnKorea();
        _replace.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o) return;
            _replaceWin?.Close();
            _replaceWin = new ReplaceLayoutWindow(_d, o, () =>
            {
                AfterEdit();
                if (_preview3d != null && _preview3d.IsLoaded) _preview3d.ShowObjective(o);
            }) { Owner = this };
            _replaceWin.Closed += (_, _) => _replaceWin = null;
            _replaceWin.Show();
        };
        _turn.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o) return;
            _map.PushUndo(o);
            _d.Turn180(o);
            AfterEdit();
        };
        _unkorea.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o || o.SwapOcd < 0) return;
            _map.PushUndo(o);
            _d.RemoveSwap(o);
            AfterEdit();
            if (o.DropEdit) _status.Text = $"{o.Name}: the replacing base and its saved position go away when you save; run Objects to see the old base again.";
        };
        _setHeading.Click += (_, _) => SetHeading();
        _alignReal.Click += (_, _) =>
        {
            if (_map.Selected is not EdObjective o) return;
            _map.PushUndo(o);
            if (_d.AlignToReal(o)) AfterEdit(); else _status.Text = "No real runway for this base in airbase_targets.csv";
        };
        _rwyHeading.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SetHeading(); e.Handled = true; } };
        _save.Click += (_, _) => Save();
        _preview.Click += (_, _) => Open3D();
        _fly.Click += (_, _) => OpenFlyOver();
        _map.Preview3DRequested += Open3D;
        Closing += OnClosing;

        var warn = string.Join("  |  ", _d.Warnings);
        _status.Text = $"{_d.Objectives.Count} objectives from {_d.CampObjSource}" + (warn.Length > 0 ? "  |  " + warn : "");
        ShowInfo();
    }

    private sealed record TypeItem(int Type, string Text) { public override string ToString() => Text; }

    private bool Filter(object item)
    {
        if (item is not EdObjective o) return false;
        if (_typeFilter.SelectedItem is TypeItem ti && ti.Type >= 0 && o.Type != ti.Type && o.Type0 != ti.Type) return false;
        if (_onlyChanged.IsChecked == true && !o.Changed && !o.HadEdit) return false;
        string s = _search.Text.Trim();
        // "blocked": gli obiettivi con un collegamento interrotto dall'acqua (serve Links o Hierarchy acceso almeno una volta)
        if (s.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            return _map.Tree is { } tr && tr.Blocked.Any(b => b.Item1 == o.Id || b.Item2 == o.Id);
        if (s.Length > 0 && o.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0 && o.Id.ToString(CultureInfo.InvariantCulture) != s) return false;
        return true;
    }

    private void Open3D()
    {
        if (_map.Selected is not EdObjective o) { _status.Text = "Select an objective first."; return; }
        if (_preview3d == null || !_preview3d.IsLoaded)
        {
            _preview3d = new Preview3DWindow(_d, o) { Owner = this };
            _preview3d.Closed += (_, _) => _preview3d = null;
            _preview3d.PiecesChanged += AfterEdit;
            _preview3d.SaveRequested += Save;
            _preview3d.Show();
        }
        else _preview3d.ShowObjective(o);
    }

    private void OpenFlyOver()
    {
        var o = _map.Selected as EdObjective;
        if (_flyOver == null || !_flyOver.IsLoaded)
        {
            _flyOver = new FlyOverWindow(_d, o) { Owner = this };
            _flyOver.Closed += (_, _) => _flyOver = null;
            _flyOver.Show();
        }
        else { if (o != null) _flyOver.GoTo(o); _flyOver.Activate(); }
    }

    private void OnJunction()
    {
        if (_map.Selected is not EdObjective o) return;
        if (o.OrigType == 15) { MessageBox.Show(this, "It is already a junction.", "Objective editor"); return; }
        _map.PushUndo(o);
        _d.SetJunction(o, !o.Junction);
        AfterEdit();
    }

    private AlignmentWindow? _alignWin;

    /// <summary>Selects an objective in the table and centres the map on it (from the alignment report).</summary>
    public void ShowObjective(EdObjective o)
    {
        if (!_view.Contains(o)) { _typeFilter.SelectedIndex = 0; _onlyChanged.IsChecked = false; _search.Text = ""; _view.Refresh(); }
        _map.Select(o, center: true);
        _syncing = true;
        try { _grid.SelectedItem = o; _grid.ScrollIntoView(o); }
        finally { _syncing = false; }
        ShowInfo();
    }

    /// <summary>"Align to real runway" on an objective (undo works as for the button).</summary>
    public bool AlignToReal(EdObjective o)
    {
        _map.Select(o, center: false);
        _map.PushUndo(o);
        if (!_d.AlignToReal(o)) return false;
        AfterEdit();
        return true;
    }

    private void AfterEdit()
    {
        if (_map.Selected is EdObjective so) _d.UpdateRunway(so);
        _map.InvalidateVisual();
        var sel = _map.Selected;
        _syncing = true;
        try { _view.Refresh(); if (sel != null && _view.Contains(sel)) _grid.SelectedItem = sel; }
        finally { _syncing = false; }
        ShowInfo();
    }

    private void ShowInfo()
    {
        var o = _map.Selected;
        if (o == null) { _info.Text = "No objective selected."; _junction.IsEnabled = _reset.IsEnabled = _forget.IsEnabled = _korea.IsEnabled = _replace.IsEnabled = _turn.IsEnabled = _unkorea.IsEnabled = _setHeading.IsEnabled = _alignReal.IsEnabled = false; return; }
        _d.UpdateRunway(o);
        _alignReal.IsEnabled = o.Real != null && _d.MainRunway(o) != null;
        string rwy = _d.RunwayText(o);
        _setHeading.IsEnabled = rwy.Length > 0;
        if (rwy.Length > 0 && !_rwyHeading.IsKeyboardFocused && _d.MainRunway(o) is Runway mr) _rwyHeading.Text = (mr.Hdg % 180).ToString("0.0", CultureInfo.InvariantCulture);
        var (la, lo) = _map.LatLon(o.X, o.Y);
        string orig = o.OrigType != o.Type ? $" (was {EditorData.TypeName(o.OrigType)})" : "";
        _info.Text =
            $"{o.Name}  [#{o.Id}]\n" +
            $"Type     {o.TypeName}{orig}, layout OCD {o.Ocd}\n" +
            $"Position {la:0.000000}, {lo:0.000000}   X {TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(o.Y)}, Y {TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(o.X)} km\n" +
            (rwy.Length > 0 ? $"Runway   {rwy}\n" : "") +
            (o.Real is { } re ? $"Real     {re.TrueHdg % 180:0.0}°/{re.TrueHdg % 180 + 180:0.0}° true = {re.MapHdg % 180:0.0}° on the map, {re.LenFt * 0.3048:0} m ({re.Source}); turn {o.DeltaText}°\n" : "") +
            $"Heading  {o.Heading:0.0}°   moved {o.ShiftM:0} m" + (Math.Abs(o.Heading - o.H0) > 0.01 ? $", turned {Wrap(o.Heading - o.H0):+0.0;-0.0}°" : "") + "\n" +
            $"Pieces   {_d.LayoutOf(o).Features.Count}" + (o.HadEdit ? "   (has a saved edit)" : "") +
            (_map.Tree is { } tr ? $"\nCampaign {CampaignTree.LevelName(tr.Level.GetValueOrDefault(o.Id, 0))}" +
                (tr.Parent.TryGetValue(o.Id, out int pid) ? $", parent {_d.Objectives.FirstOrDefault(q => q.Id == pid)?.Name ?? "#" + pid}" : "") +
                $", {tr.Children.GetValueOrDefault(o.Id)} children, {tr.LinkCount.GetValueOrDefault(o.Id)} links" +
                (tr.Blocked.Any(b => b.Item1 == o.Id || b.Item2 == o.Id)
                    ? "\nBlocked  " + string.Join(", ", tr.Blocked.Where(b => b.Item1 == o.Id || b.Item2 == o.Id)
                        .Select(b => b.Item1 == o.Id ? b.Item2 : b.Item1).Select(k => (_d.Objectives.FirstOrDefault(q => q.Id == k)?.Name ?? "#" + k) + $" ({k})")) + " (water on the BMS campaign map)" : "") : "") +
            (o.RepOcd >= 0 ? $"\nLayout   {o.RepName} [{(o.RepSource == LayoutSwaps.Bms ? "BMS 4.38" : "Mideast")} OCD {o.RepOcd}]" + (o.RepChanged ? " (not saved)" : "") : "") +
            (o.SwapOcd >= 0 ? $"\nBase     {o.SwapName} [{(BaseSwaps.FromTheater(o.SwapOcd) ? "theater" : "Korea 4.38")} OCD {BaseSwaps.RealOcd(o.SwapOcd)}]" + (o.SwapOcd != o.SwapOcd0 ? " (not saved)" : "") +
                (_d.NumbersOk(o) == false ? "\nRunway   numbers painted in this base's runway would be wrong here: Objects will use generic runway pieces with the right numbers" : "") : "");
        bool airbase = !o.Junction && (o.Type is 1 or 2 || o.SwapOcd >= 0);
        _korea.IsEnabled = airbase && _d.KoreaOrd != null;
        _turn.IsEnabled = airbase;
        _replace.IsEnabled = !o.Junction && !airbase && o.Type != 15;
        _unkorea.IsEnabled = o.SwapOcd >= 0;
        _junction.IsEnabled = o.OrigType != 15;
        _junction.Content = o.Junction ? $"Back to {EditorData.TypeName(o.OrigType == 15 ? o.Type0 : o.OrigType).ToLowerInvariant()}" : "Convert to junction";
        _reset.IsEnabled = o.Changed;
        _forget.IsEnabled = o.HadEdit;
    }

    private void SetHeading()
    {
        if (_map.Selected is not EdObjective o) return;
        if (!double.TryParse(_rwyHeading.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) { _status.Text = "Runway heading: type a number of degrees, e.g. 309.5"; return; }
        _map.PushUndo(o);
        if (_d.SetRunwayHeading(o, h)) AfterEdit();
    }

    private void OnKorea()
    {
        if (_map.Selected is not EdObjective o) return;
        var all = _d.Suggest(o, true, true);
        if (all.Count == 0) { MessageBox.Show(this, "No runway found in this layout, or no base with runways found (BMS folder or this theater).", "Replace base"); return; }
        var (x, y, h, sOcd, sName, drop) = (o.X, o.Y, o.Heading, o.SwapOcd, o.SwapName, o.DropEdit);
        _map.PushUndo(o);
        var dlg = new KoreaBaseDialog(o, all, _d.WorldRunways(o).Count, pick =>
        {
            if (pick.HasValue) _d.ApplySwap(o, pick.Value.Item1, pick.Value.Item2);
            else { o.X = x; o.Y = y; o.Heading = h; o.SwapOcd = sOcd; o.SwapName = sName; o.DropEdit = drop; }
            AfterEdit();
        }) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            o.X = x; o.Y = y; o.Heading = h; o.SwapOcd = sOcd; o.SwapName = sName; o.DropEdit = drop;
            AfterEdit();
            return;
        }
        _status.Text = $"{o.Name}: base '{o.SwapName}' placed on the runway. Check it on the photo and in 3D, fine-tune (drag, Ctrl+drag, Turn 180°), then Save edits and run Objects.";
    }

    private static double Wrap(double a) => ((a % 360) + 540) % 360 - 180;

    /// <summary>Chiamato dopo il salvataggio se si vuole il lavoro Objects (true = anche Install in BMS). Lo imposta la finestra principale.</summary>
    public Func<bool, Task>? RunObjectsRequested { get; set; }

    private void Save()
    {
        int n = _d.SaveEdits();
        _view.Refresh();
        ShowInfo();
        _status.Text = _d.Project.IsInstalledTheater
            ? $"Saved {n} edits in the theater's work folder ({ObjectiveEdits.PathFor(_d.Project.ProjectFolder)}). BMS is not touched: next, Build objects and Install (main window)."
            : $"Saved {n} edits to {ObjectiveEdits.PathFor(_d.Project.ProjectFolder)}. Run Objects and then Install into BMS (main window): objects and campaign files.";
        AskRunObjects();
    }

    /// <summary>Promemoria: le modifiche arrivano in BMS solo dopo Objects e Install; li si puo' lanciare subito.</summary>
    private async void AskRunObjects()
    {
        if (RunObjectsRequested == null) return;
        var r = MessageBox.Show(this,
            "Edits saved. They reach BMS only after 'Run Objects' and 'Install'.\n\n" +
            "Yes = Run Objects now, then Install into BMS (objects and campaign files)\nNo = Run Objects only\nCancel = later",
            "Objective editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (r == MessageBoxResult.Cancel) return;
        _status.Text = "Objects job running in the main window…";
        try { await RunObjectsRequested(r == MessageBoxResult.Yes); }
        catch (Exception ex) { _status.Text = "Objects: " + ex.Message; return; }
        _status.Text = "Objects job finished (see the main window). Reopen the editor to see its result.";
    }

    /// <summary>Edits made in the window and not saved yet.</summary>
    public bool HasUnsaved => _d.Objectives.Any(o => o.Changed && !SameAsSaved(o));

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_d.Objectives.Any(o => o.Changed && !SameAsSaved(o))) return;
        var r = MessageBox.Show(this, "Save the edits before closing?", "Objective editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) e.Cancel = true;
        else if (r == MessageBoxResult.Yes) { _d.SaveEdits(); AskRunObjects(); }
    }

    private bool SameAsSaved(EdObjective o)
    {
        if (!o.HadEdit || o.PiecesDirty || o.RepChanged) return false;
        var saved = ObjectiveEdits.Load(_d.Project.ProjectFolder);
        return o.SwapOcd == o.SwapOcd0 && !o.DropEdit && saved.TryGetValue(o.Id, out var s) && Math.Abs(s.X - o.X) < 0.01 && Math.Abs(s.Y - o.Y) < 0.01 && Math.Abs(s.Heading - o.Heading) < 0.001 && s.Junction == o.Junction;
    }
}

/// <summary>
/// Mappa dell'editor: foto Photoreal sotto, obiettivi sopra (sagome dei pezzi, piste e vie di rullaggio).
/// Coordinate del teatro in piedi: X nord, Y est.
/// </summary>
public enum BrushMode { Off, Add, Remove }

public sealed partial class EditorMap : FrameworkElement
{
    private const double FtPerM = 3.280839895;
    private readonly EditorData _d;
    private readonly TheaterGrid _grid;
    private double _cx, _cy, _scale;              // centro (piedi, nord/est) e pixel per piede
    private Point? _panStart; private double _panCx, _panCy;
    private Point? _dragStart; private bool _rotating; private double _dragX, _dragY, _dragH, _dragAngle; private bool _dragMoved;
    private readonly Stack<(EdObjective O, double X, double Y, double H, bool J, int S, string SN, bool D)> _undo = new();
    private readonly Dictionary<(int, int, int, int, int), BitmapSource?> _bmp = new();
    private readonly LinkedList<(int, int, int, int, int)> _bmpLru = new();
    private bool _labels = true;
    public bool ShowBuildings { get; set; } = true;
    public bool ShowTaxi { get; set; } = true;
    public bool ShowPoints { get; set; } = true;
    public bool ShowLabels { get => _labels; set { _labels = value; InvalidateVisual(); } }
    public bool ShowProtractor { get; set; } = true;
    // collegamenti e gerarchia della campagna (sola lettura)
    private bool _links, _tree;
    public bool ShowLinks { get => _links; set { _links = value; if (value) LoadTree(); InvalidateVisual(); } }
    public bool ShowTree { get => _tree; set { _tree = value; if (value) LoadTree(); InvalidateVisual(); } }
    public CampaignTree? Tree { get; private set; }
    private bool _treeLoading;
    public event Action? TreeLoaded;
    // pennello dell'acqua (water_paint.csv)
    private BrushMode _brush;
    public BrushMode Brush { get => _brush; set { _brush = value; Cursor = value == BrushMode.Off ? null : Cursors.None; InvalidateVisual(); } }
    public double BrushM { get; set; } = 250;
    private List<WaterStroke>? _strokes;
    private WaterStroke? _stroke;
    private Point? _mouse;
    private List<WaterStroke> Strokes => _strokes ??= WaterPaint.Load(_d.Project.ProjectFolder);
    private bool _water;
    public bool ShowWater { get => _water; set { _water = value; _waterWant = null; if (!value) { _waterCts?.Cancel(); _waterBmp = null; } InvalidateVisual(); } }
    // acqua di BMS sopra la foto: calcolata in background per la vista, un po' dopo l'ultimo spostamento
    private BitmapSource? _waterBmp; private (double X, double Y, double Half) _waterAt; private (double X, double Y, double Half)? _waterWant;
    private CancellationTokenSource? _waterCts;
    private System.Windows.Threading.DispatcherTimer? _waterTimer;
    private double _pivotX, _pivotY;

    // tipi dei punti (PDX): 11/12 parcheggi sulle vie di rullaggio; 4 lanciatori SAM, 6 cannoni AAA, 5 artiglieria, 7 radar,
    // 9 mezzi di supporto, 16/20 altri mezzi delle batterie (liste PHD 4 = SAM, 6 = AAA, 14/18 = altri gruppi)
    private static readonly Brush ParkBrush = Fz(new SolidColorBrush(Color.FromRgb(90, 255, 90)));
    private static readonly Brush SamBrush = Fz(new SolidColorBrush(Color.FromRgb(255, 60, 60)));
    private static readonly Brush AaaBrush = Fz(new SolidColorBrush(Color.FromRgb(255, 170, 0)));
    private static readonly Brush RadarBrush = Fz(new SolidColorBrush(Color.FromRgb(255, 80, 220)));
    private static readonly Brush PtOtherBrush = Fz(new SolidColorBrush(Color.FromRgb(200, 200, 200)));
    private static Brush PointBrush(int t) => t switch { 10 or 11 or 12 or 13 => ParkBrush, 4 => SamBrush, 5 or 6 => AaaBrush, 7 => RadarBrush, _ => PtOtherBrush };
    /// <summary>Point types as the BMS Editor names them (PDX Type).</summary>
    public static string PointName(int t) => t switch
    {
        0 => "no point", 1 => "runway", 2 => "takeoff", 3 => "taxi", 4 => "SAM", 5 => "artillery", 6 => "AAA", 7 => "radar",
        8 => "runway dimension", 9 => "support", 10 => "static radar", 11 => "small parking", 12 => "large parking",
        13 => "small dock", 14 => "large dock", 15 => "take runway", 16 => "helicopter", 17 => "follow-me", 18 => "track",
        19 => "critical taxi", 20 => "MANPAD", 21 => "vacate runway", 22 => "tree", _ => $"point type {t}",
    };

    public EdObjective? Selected { get; private set; }
    public event Action<EdObjective?>? SelectionChanged;
    public event Action<EdObjective>? Edited;
    public event Action<string>? HoverText;
    public event Action? Preview3DRequested;

    public EditorMap(EditorData d, TheaterGrid grid)
    {
        _d = d; _grid = grid;
        ClipToBounds = true;
        Focusable = true;
        double s = d.Project.Theater.SizeMeters * FtPerM;
        _cx = _cy = s / 2;
    }

    public (double Lat, double Lon) LatLon(double xFt, double yFt) => _grid.ToLatLon(yFt / FtPerM, xFt / FtPerM);

    /// <summary>Zoom minimo: tutto il teatro riempie la vista (non un quadratino in mezzo).</summary>
    private double MinScale => ActualWidth > 2 && ActualHeight > 2 ? Math.Min(ActualWidth, ActualHeight) / (_d.Project.Theater.SizeMeters * FtPerM) : 1e-5;

    private Point ToScreen(double x, double y) => new(ActualWidth / 2 + (y - _cy) * _scale, ActualHeight / 2 - (x - _cx) * _scale);
    private (double X, double Y) ToWorld(Point p) => (_cx - (p.Y - ActualHeight / 2) / _scale, _cy + (p.X - ActualWidth / 2) / _scale);

    public void Select(EdObjective? o, bool center)
    {
        Selected = o;
        if (o != null && center)
        {
            _cx = o.X; _cy = o.Y;
            double r = Math.Max(1500, _d.LayoutOf(o).Radius * 1.6);
            if (ActualWidth > 0) _scale = Math.Min(ActualWidth, ActualHeight) / (2 * r);
        }
        InvalidateVisual();
    }

    public void PushUndo(EdObjective o) => _undo.Push((o, o.X, o.Y, o.Heading, o.Junction, o.SwapOcd, o.SwapName, o.DropEdit));

    // ───────────────────────── disegno ─────────────────────────

    private static readonly Pen RunwayPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.2));
    private static readonly Brush RunwayFill = Fz(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
    private static readonly Pen SpurPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 220, 255)), 0.8));
    private static bool Near((double N, double E, int T) a, (double N, double E, int T) b, double d) => (a.N - b.N) * (a.N - b.N) + (a.E - b.E) * (a.E - b.E) <= d * d;
    private static readonly Pen TaxiPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(220, 80, 220, 255)), 1.5));
    private static readonly Pen SelPen = Fz(new Pen(Brushes.Yellow, 2));
    private static readonly Pen CentrePen = Fz(new Pen(Brushes.Yellow, 1.5));
    private static readonly Brush TextBg = Fz(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static readonly Dictionary<int, (Brush Fill, Pen Line)> TypeStyle = new();
    private static readonly Pen IconRim = Fz(new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1));
    private readonly Dictionary<int, FormattedText?> _icons = new();

    /// <summary>Simbolo del tipo di obiettivo (null = resta un puntino: incroci, confini e altri molto numerosi).</summary>
    private FormattedText? Icon(int type, double dpi)
    {
        if (_icons.TryGetValue(type, out var f)) return f;
        string? g = type switch
        {
            1 or 2 => "\u2708",                 // aereo
            8 or 28 or 29 => "\u2302",          // casa
            3 or 13 or 30 => "\u2605",          // stella
            31 => "\u25B2",                     // triangolo
            21 => "\u25C9",                     // radar
            19 => "\u2693",                     // ancora
            6 => "\u2261",                      // ponte
            7 or 11 or 17 or 20 or 25 => "\u2699", // ingranaggio
            10 => "\u25A3",                     // deposito
            9 or 22 or 16 => "\u03A8",          // antenna
            23 or 24 => "\u2550",               // ferrovia
            4 or 12 or 18 or 27 => "\u2248",    // spiaggia, guado, passo, mare
            _ => null,
        };
        f = g == null ? null : new FormattedText(g, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Symbol"), 10, Brushes.Black, dpi);
        _icons[type] = f;
        return f;
    }
    private static (Brush, Pen) StyleOf(int type)
    {
        if (TypeStyle.TryGetValue(type, out var s)) return s;
        Color c = type switch
        {
            1 or 2 => Color.FromRgb(255, 90, 90), 6 => Color.FromRgb(255, 170, 0), 15 => Color.FromRgb(170, 170, 170),
            8 or 28 or 29 => Color.FromRgb(120, 200, 255), 31 or 21 => Color.FromRgb(255, 80, 220), 3 or 13 or 30 => Color.FromRgb(140, 230, 90),
            19 => Color.FromRgb(80, 140, 255), 20 or 25 or 11 or 7 or 17 => Color.FromRgb(255, 230, 90), _ => Color.FromRgb(230, 230, 230),
        };
        s = (Fz(new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B))), Fz(new Pen(new SolidColorBrush(c), 1)));
        TypeStyle[type] = s;
        return s;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1E)), null, new Rect(0, 0, W, H));
        if (W < 2 || H < 2) return;
        double sizeFt = _d.Project.Theater.SizeMeters * FtPerM;
        if (_scale <= 0 || _scale < MinScale) _scale = MinScale;

        DrawPhoto(dc, W, H);
        if (_water) DrawWater(dc, W, H);
        if (_water || _brush != BrushMode.Off) DrawStrokes(dc);
        var tl = ToWorld(new Point(0, 0)); var br = ToWorld(new Point(W, H));
        double xMin = br.X, xMax = tl.X, yMin = tl.Y, yMax = br.Y;
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(ToScreen(sizeFt, 0), ToScreen(0, sizeFt)));
        if ((_links || _tree) && Tree != null) DrawTree(dc, W, H);

        bool detail = _scale > 0.004;   // da circa 250 piedi per pixel in su si disegnano le sagome
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var o in _d.Objectives)
        {
            if (o == Selected) continue;
            DrawObjective(dc, o, detail, xMin, xMax, yMin, yMax, false, dpi);
        }
        if (Selected != null) DrawObjective(dc, Selected, true, xMin, xMax, yMin, yMax, true, dpi);
        if (Selected != null && (ShowProtractor || _rotating && _dragStart != null || (Keyboard.Modifiers & ModifierKeys.Control) != 0)) DrawProtractor(dc, Selected, dpi);
    }

    private static readonly Pen ProtPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 0)), 1.2));
    private static readonly Pen ProtThin = Fz(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 0)), 0.8));
    private static readonly Pen AxisPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(220, 0, 255, 255)), 1.2) { DashStyle = DashStyles.Dash });

    /// <summary>
    /// Goniometro attorno al centro della pista principale: tacche ogni grado (lunghe ogni 5 e 10, numeri ogni 10; 0 = alto =
    /// nord della griglia del teatro), asse della pista prolungato sulla foto e riquadro con direzione, magnetica e numeri.
    /// </summary>
    private void DrawProtractor(DrawingContext dc, EdObjective o, double dpi)
    {
        if (_d.MainRunway(o) is not Runway r) return;
        var c = ToScreen(r.X, r.Y);
        double rad = Math.Clamp(r.Len / 2 * _scale + 40, 120, Math.Max(120, Math.Min(ActualWidth, ActualHeight) * 0.45));
        dc.DrawEllipse(null, ProtPen, c, rad, rad);
        var tf = new Typeface("Segoe UI");
        for (int a = 0; a < 360; a++)
        {
            double t = a * Math.PI / 180, sx = Math.Sin(t), sy = -Math.Cos(t);
            double len = a % 10 == 0 ? 14 : a % 5 == 0 ? 9 : 4;
            dc.DrawLine(a % 5 == 0 ? ProtPen : ProtThin, new Point(c.X + sx * rad, c.Y + sy * rad), new Point(c.X + sx * (rad - len), c.Y + sy * (rad - len)));
            if (a % 10 == 0)
            {
                var ft = new FormattedText(a.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10, Brushes.Yellow, dpi);
                var at = new Point(c.X + sx * (rad + 11) - ft.Width / 2, c.Y + sy * (rad + 11) - ft.Height / 2);
                dc.DrawRectangle(TextBg, null, new Rect(at.X - 1, at.Y, ft.Width + 2, ft.Height));
                dc.DrawText(ft, at);
            }
        }
        // asse della pista prolungato oltre il cerchio (per confrontarlo con la pista della foto)
        double h = r.Hdg * Math.PI / 180, ex = Math.Sin(h), ey = -Math.Cos(h), far = Math.Max(ActualWidth, ActualHeight);
        dc.DrawLine(AxisPen, new Point(c.X - ex * far, c.Y - ey * far), new Point(c.X + ex * far, c.Y + ey * far));
        // riquadro con la direzione
        var txt = new FormattedText("Runway " + _d.RunwayText(o), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 14, Brushes.Yellow, dpi);
        var box = new Point(10, 10);
        dc.DrawRectangle(TextBg, null, new Rect(box.X - 4, box.Y - 2, txt.Width + 8, txt.Height + 4));
        dc.DrawText(txt, box);
    }

    private void DrawObjective(DrawingContext dc, EdObjective o, bool detail, double xMin, double xMax, double yMin, double yMax, bool sel, double dpi)
    {
        var lay = detail ? _d.LayoutOf(o) : null;
        double r = lay?.Radius ?? 0;
        if (o.X + r < xMin || o.X - r > xMax || o.Y + r < yMin || o.Y - r > yMax) return;
        var (fill, line) = StyleOf(o.Type);
        var c = ToScreen(o.X, o.Y);
        if (lay == null || r * _scale < 3)
        {
            // da circa 700 m per pixel in giu': icona del tipo (cerchio colorato con un simbolo), prima solo un puntino
            if (_scale * FtPerM > 1.0 / 700)
            {
                // icona del tipo con le forme della mappa di BMS, nel colore del tipo (come nel Campaign Editor)
                var (_, ln) = StyleOf(o.Type);
                double size = (sel ? 22 : 18) * Math.Max(0.65, ObjectiveIcons.Weight(o.Type));
                ObjectiveIcons.Draw(dc, o.Type, c, size, ln.Brush);
                if (sel) dc.DrawEllipse(null, SelPen, c, size / 2 + 3, size / 2 + 3);
                if (_labels && sel)
                {
                    var ft = new FormattedText(o.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Yellow, dpi);
                    var at = new Point(c.X + 10, c.Y + 6);
                    dc.DrawRectangle(TextBg, null, new Rect(at.X - 2, at.Y - 1, ft.Width + 4, ft.Height + 2));
                    dc.DrawText(ft, at);
                }
                return;
            }
            dc.DrawEllipse(fill, sel ? SelPen : line, c, sel ? 5 : 2.5, sel ? 5 : 2.5);
            return;
        }
        foreach (var p in lay.Paths)
        {
            if (p.Points.Count < 1) continue;
            var pts = p.Points.Select(q => { var w = EditorData.ToWorld(o, q.N, q.E); return ToScreen(w.X, w.Y); }).ToList();
            if (p.Type == 8)
            {
                // asfalto della pista: poligono
                if (pts.Count < 3) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], true, true); ctx.PolyLineTo(pts.Skip(1).ToList(), true, false); }
                g.Freeze();
                dc.DrawGeometry(RunwayFill, RunwayPen, g);
                continue;
            }
            // vie di rullaggio: la linea principale unisce i punti di rullaggio (3/15) uno dopo l'altro; i punti 11/12 sono
            // le deviazioni verso ricoveri e piazzole (3 → 11 → 11 → di nuovo 3): si disegna l'andata come un ramo corto,
            // non il ritorno, cosi' non viene la linea a zigzag
            if (ShowTaxi)
            {
                int last = -1;
                for (int i = 0; i < p.Points.Count; i++)
                {
                    var b = p.Points[i];
                    if (b.T is 3 or 15)
                    {
                        if (last >= 0 && Near(p.Points[last], b, 1500)) dc.DrawLine(TaxiPen, pts[last], pts[i]);
                        last = i;
                    }
                    else if (b.T is 11 or 12 && i > 0 && p.Points[i - 1].T is 3 or 15 or 11 or 12 && Near(p.Points[i - 1], b, 600))
                        dc.DrawLine(SpurPen, pts[i - 1], pts[i]);
                }
            }
            // parcheggi (11/12, lungo le vie di rullaggio) e punti delle batterie SAM/AAA (liste 4/6...): pallini
            if (ShowPoints && _scale > 0.03)
                for (int i = 0; i < p.Points.Count; i++)
                {
                    int t = p.Points[i].T;
                    if (t is 1 or 2 or 3 or 8 or 15) continue;
                    double rad = t is 10 or 11 or 12 or 13 ? 3 : 2.5;
                    dc.DrawEllipse(PointBrush(t), t is 4 or 6 ? Fz(new Pen(Brushes.Black, 0.6)) : null, pts[i], rad, rad);
                }
        }
        foreach (var f in ShowBuildings || sel ? lay.Features : Enumerable.Empty<EdFeature>())
        {
            var corners = EditorData.FeatureCorners(o, f).Select(q => ToScreen(q.X, q.Y)).ToList();
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(corners[0], true, true);
                ctx.PolyLineTo(corners.Skip(1).ToList(), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, sel ? SelPen : line, g);
        }
        if (sel)
        {
            dc.DrawLine(CentrePen, new Point(c.X - 8, c.Y), new Point(c.X + 8, c.Y));
            dc.DrawLine(CentrePen, new Point(c.X, c.Y - 8), new Point(c.X, c.Y + 8));
            // direzione dell'obiettivo (asse nord della disposizione)
            double h = o.Heading * Math.PI / 180;
            dc.DrawLine(CentrePen, c, new Point(c.X + 30 * Math.Sin(h), c.Y - 30 * Math.Cos(h)));
        }
        if (_labels && (sel || r * _scale > 25))
        {
            var ft = new FormattedText(o.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), sel ? 13 : 11,
                sel ? Brushes.Yellow : Brushes.White, dpi);
            var at = new Point(c.X + 6, c.Y + 6);
            dc.DrawRectangle(TextBg, null, new Rect(at.X - 2, at.Y - 1, ft.Width + 4, ft.Height + 2));
            dc.DrawText(ft, at);
        }
    }

    private void DrawPhoto(DrawingContext dc, double W, double H)
    {
        var ph = _d.Photo;
        if (ph == null) return;
        double pft0 = ph.TileM * FtPerM / PhotoTiles.TilePx;          // piedi per pixel al livello 0
        int mip = (int)Math.Ceiling(Math.Log2(0.75 / (pft0 * _scale)));
        mip = Math.Clamp(mip, 0, 12);
        int w = PhotoTiles.TilePx >> mip, side = Math.Min(PhotoTiles.Chunk, w), per = w / side;
        double chunkM = ph.TileM / per;
        var tl = ToWorld(new Point(0, 0)); var br = ToWorld(new Point(W, H));
        // metri del teatro: est = Y, nord = X; le righe delle tessere partono da nord
        double e0 = tl.Y / FtPerM, e1 = br.Y / FtPerM, nTop = tl.X / FtPerM, nBot = br.X / FtPerM;
        int c0 = (int)Math.Floor(e0 / chunkM), c1 = (int)Math.Floor(e1 / chunkM);
        int r0 = (int)Math.Floor((ph.SizeM - nTop) / chunkM), r1 = (int)Math.Floor((ph.SizeM - nBot) / chunkM);
        int total = per * ph.N;
        c0 = Math.Max(0, c0); r0 = Math.Max(0, r0); c1 = Math.Min(total - 1, c1); r1 = Math.Min(total - 1, r1);
        // vista d'insieme sotto (sempre: copre anche le tessere solo mare, che non esistono); da vicino sopra i pezzi veri
        if (Overview() is BitmapSource ov) dc.DrawImage(ov, new Rect(ToScreen(ph.SizeM * FtPerM, 0), ToScreen(0, ph.SizeM * FtPerM)));
        if (mip >= 6 || (long)(c1 - c0 + 1) * (r1 - r0 + 1) > 400) return;   // lontano: basta la vista d'insieme
        for (int rr = r0; rr <= r1; rr++)
            for (int cc = c0; cc <= c1; cc++)
            {
                int tx = cc / per, ty = rr / per, cx = cc % per, cy = rr % per;
                var bmp = Bitmap(ph, tx, ty, mip, cx, cy);
                if (bmp == null) continue;
                double eL = cc * chunkM, nT = ph.SizeM - rr * chunkM;
                var a = ToScreen(nT * FtPerM, eL * FtPerM);
                var b = ToScreen((nT - chunkM) * FtPerM, (eL + chunkM) * FtPerM);
                dc.DrawImage(bmp, new Rect(a, b));
            }
    }

    private void LoadTree()
    {
        if (Tree != null || _treeLoading) return;
        _treeLoading = true;
        HoverText?.Invoke("Reading links and hierarchy from the campaign...");
        Task.Run(() =>
        {
            var t = CampaignTree.Load(_d.Project, out var err);
            Dispatcher.BeginInvoke(() =>
            {
                _treeLoading = false; Tree = t;
                HoverText?.Invoke(t == null ? "Links and hierarchy not read: " + err
                    : $"Links and hierarchy from {t.Source}: {t.Links.Count} links, {t.Level.Count(k => k.Value == 0)} primary and {t.Level.Count(k => k.Value == 1)} secondary objectives; {t.BlockedNote}");
                TreeLoaded?.Invoke();
                InvalidateVisual();
            });
        });
    }

    private static readonly Pen LinkPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(150, 225, 225, 225)), 1));
    private static readonly Pen LinkAirPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(170, 90, 160, 255)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen LinkOneWayPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 150, 30)), 1.4));
    private static readonly Pen LinkSelPen = Fz(new Pen(Brushes.Yellow, 2.2));
    private static readonly Pen LinkBlockedPen = Fz(new Pen(new SolidColorBrush(Color.FromRgb(255, 30, 30)), 3) { DashStyle = DashStyles.Dash });
    private static readonly Pen[] TreePens =
    {
        Fz(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 60, 60)), 2.2)),     // SO → PO
        Fz(new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 160, 40)), 1.4)),    // 3° → SO
        Fz(new Pen(new SolidColorBrush(Color.FromArgb(190, 150, 230, 90)), 1)),      // 4° → 3°
    };
    private static readonly Pen TreeSelPen = Fz(new Pen(Brushes.Yellow, 2.5));
    private static readonly Pen PoRing = Fz(new Pen(new SolidColorBrush(Color.FromRgb(255, 60, 60)), 3));
    private static readonly Pen SoRing = Fz(new Pen(new SolidColorBrush(Color.FromRgb(255, 160, 40)), 2));

    /// <summary>Collegamenti e gerarchia della campagna sulle posizioni dell'editor (anche quelle non salvate).</summary>
    private void DrawTree(DrawingContext dc, double W, double H)
    {
        var t = Tree!;
        var byId = _byId ??= _d.Objectives.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var view = new Rect(-50, -50, W + 100, H + 100);
        bool Visible(Point a, Point b) => view.IntersectsWith(new Rect(a, b));
        int sel = Selected?.Id ?? int.MinValue;
        if (_links) DrawNetwork(dc, W, H, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (_tree)
        {
            // catena dei padri e figli dell'obiettivo scelto
            var hot = new HashSet<int>();
            if (Selected != null)
            {
                int c = sel; hot.Add(c);
                while (t.Parent.TryGetValue(c, out int up) && hot.Add(up)) c = up;
                foreach (var (k, v) in t.Parent) if (v == sel) hot.Add(k);
            }
            foreach (var (child, parent) in t.Parent)
            {
                if (!byId.TryGetValue(child, out var oc) || !byId.TryGetValue(parent, out var op)) continue;
                var pc = ToScreen(oc.X, oc.Y); var pp = ToScreen(op.X, op.Y);
                if (!Visible(pc, pp)) continue;
                int lv = t.Level.GetValueOrDefault(child, 1);
                bool h = hot.Contains(child) && hot.Contains(parent);
                // da lontano solo i rami alti
                if (!h && lv >= 3 && _scale < 0.0015) continue;
                if (!h && lv >= 2 && _scale < 0.0004) continue;
                dc.DrawLine(h ? TreeSelPen : TreePens[Math.Clamp(lv - 1, 0, 2)], pc, pp);
            }
            foreach (var (id, lv) in t.Level)
            {
                if (lv > 1 || !byId.TryGetValue(id, out var o)) continue;
                var c = ToScreen(o.X, o.Y);
                if (!view.Contains(c)) continue;
                double r = lv == 0 ? 11 : 7;
                dc.DrawEllipse(null, lv == 0 ? PoRing : SoRing, c, r, r);
            }
        }
    }
    private Dictionary<int, EdObjective>? _byId;

    private static readonly Brush AddFill = Fz(new SolidColorBrush(Color.FromArgb(150, 40, 200, 255)));
    private static readonly Brush RemoveFill = Fz(new SolidColorBrush(Color.FromArgb(150, 230, 120, 40)));
    private static readonly Pen BrushRim = Fz(new Pen(Brushes.White, 1.2));

    /// <summary>Pennellate (anche quelle non ancora nel terreno): azzurro = acqua aggiunta, arancio = acqua tolta.</summary>
    private void DrawStrokes(DrawingContext dc)
    {
        foreach (var s in Strokes.Append(_stroke).OfType<WaterStroke>())
        {
            double wpx = s.DiameterM * FtPerM * _scale;
            var fill = s.Add ? AddFill : RemoveFill;
            if (s.Points.Count == 1 || wpx < 1.5)
            {
                foreach (var q in s.Points) dc.DrawEllipse(fill, null, ToScreen(q.X, q.Y), Math.Max(0.75, wpx / 2), Math.Max(0.75, wpx / 2));
                if (s.Points.Count == 1) continue;
            }
            var pen = new Pen(fill, Math.Max(1.5, wpx)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(ToScreen(s.Points[0].X, s.Points[0].Y), false, false);
                c.PolyLineTo(s.Points.Skip(1).Select(q => ToScreen(q.X, q.Y)).ToList(), true, true);
            }
            g.Freeze();
            dc.PushOpacity(1);
            dc.DrawGeometry(null, pen, g);
            dc.Pop();
        }
        // cerchio del pennello sotto il mouse
        if (_brush != BrushMode.Off && _mouse is Point m)
        {
            double r = Math.Max(2, BrushM * FtPerM * _scale / 2);
            dc.DrawEllipse(null, BrushRim, m, r, r);
            dc.DrawEllipse(null, new Pen(_brush == BrushMode.Add ? AddFill : RemoveFill, 1), m, r + 1.5, r + 1.5);
            dc.DrawLine(BrushRim, new Point(m.X - 4, m.Y), new Point(m.X + 4, m.Y));
            dc.DrawLine(BrushRim, new Point(m.X, m.Y - 4), new Point(m.X, m.Y + 4));
        }
    }

    private void SaveStrokes(string what)
    {
        try { WaterPaint.Save(_d.Project.ProjectFolder, Strokes); HoverText?.Invoke($"{what}  |  {Strokes.Count} water strokes saved in {WaterPaint.FileName}: they go into the terrain at the next 'Run Water DEM'"); }
        catch (Exception ex) { HoverText?.Invoke("Water strokes NOT saved: " + ex.Message); }
    }

    private void DrawWater(DrawingContext dc, double W, double H)
    {
        double half = Math.Max(W, H) / 2 / _scale;
        var want = (_cx, _cy, half);
        if (_waterBmp != null)
        {
            var (x, y, h) = _waterAt;
            dc.DrawImage(_waterBmp, new Rect(ToScreen(x + h, y - h), ToScreen(x - h, y + h)));
        }
        if (_waterBmp != null && _waterAt == want) return;
        if (_waterWant == want) return;
        _waterWant = want;
        _waterTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background, (_, _) => StartWater(), Dispatcher);
        _waterTimer.Stop(); _waterTimer.Start();
    }

    private void StartWater()
    {
        _waterTimer?.Stop();
        if (!_water || _waterWant is not { } want) return;
        _waterCts?.Cancel();
        var cts = _waterCts = new CancellationTokenSource();
        int px = (int)Math.Clamp(Math.Max(ActualWidth, ActualHeight), 64, 1400);
        Task.Run(() =>
        {
            try
            {
                var buf = _d.WaterOverlay(want.X, want.Y, want.Half, px, cts.Token);
                if (buf == null || cts.IsCancellationRequested) return;
                var bmp = BitmapSource.Create(px, px, 96, 96, PixelFormats.Bgra32, null, buf, px * 4);
                bmp.Freeze();
                Dispatcher.BeginInvoke(() => { if (cts.IsCancellationRequested || !_water) return; _waterBmp = bmp; _waterAt = want; InvalidateVisual(); });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => HoverText?.Invoke("Water overlay failed: " + ex.Message)); }
        });
    }

    private BitmapSource? _overview;
    private bool _overviewLoading;
    /// <summary>Foto di tutto il teatro a bassa risoluzione (64 pixel per tessera), letta una volta in background.</summary>
    private BitmapSource? Overview()
    {
        if (_overview != null || _overviewLoading || _d.Photo is not PhotoTiles ph) return _overview;
        _overviewLoading = true;
        Task.Run(() =>
        {
            try
            {
                var (px, side) = ph.Overview(6);
                var bmp = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4);
                bmp.Freeze();
                Dispatcher.BeginInvoke(() => { _overview = bmp; InvalidateVisual(); });
            }
            catch (Exception) { }
        });
        return null;
    }

    private BitmapSource? Bitmap(PhotoTiles ph, int tx, int ty, int mip, int cx, int cy)
    {
        var key = (tx, ty, mip, cx, cy);
        if (_bmp.TryGetValue(key, out var hit)) { _bmpLru.Remove(key); _bmpLru.AddFirst(key); return hit; }
        BitmapSource? res = null;
        var px = ph.GetChunk(tx, ty, mip, cx, cy, out int side);
        if (px != null)
        {
            res = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4);
            res.Freeze();
        }
        _bmp[key] = res; _bmpLru.AddFirst(key);
        while (_bmpLru.Count > 300) { _bmp.Remove(_bmpLru.Last!.Value); _bmpLru.RemoveLast(); }
        return res;
    }

    // ───────────────────────── mouse e tastiera ─────────────────────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var before = ToWorld(p);
        _scale *= e.Delta > 0 ? 1.25 : 1 / 1.25;
        _scale = Math.Clamp(_scale, MinScale, 20);
        var after = ToWorld(p);
        _cx += before.X - after.X; _cy += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            if (e.ChangedButton == MouseButton.Right) _rightDown = p;
            _panStart = p; _panCx = _cx; _panCy = _cy;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_brush != BrushMode.Off)
        {
            var w0 = ToWorld(p);
            _stroke = new WaterStroke(_brush == BrushMode.Add, BrushM, new List<(double, double)> { (w0.X, w0.Y) });
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (LinkClick(p)) { e.Handled = true; return; }
        var hit = PickAt(p);
        if (hit != Selected) { Selected = hit; SelectionChanged?.Invoke(hit); InvalidateVisual(); }
        if (Selected != null)
        {
            _dragStart = p; _dragMoved = false;
            _rotating = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            _dragX = Selected.X; _dragY = Selected.Y; _dragH = Selected.Heading;
            (_pivotX, _pivotY) = _d.Pivot(Selected);   // le basi ruotano attorno al centro della pista principale
            var c = ToScreen(_pivotX, _pivotY);
            _dragAngle = Math.Atan2(p.X - c.X, -(p.Y - c.Y)) * 180 / Math.PI;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var w = ToWorld(p);
        _mouseNet = p;
        if (_linkFrom != 0) InvalidateVisual();
        var (la, lo) = LatLon(w.X, w.Y);
        string sel = Selected == null ? "" : $"   |   {Selected.Name}: heading {Selected.Heading:0.0}°, moved {Selected.ShiftM:0} m";
        HoverText?.Invoke($"{la:0.00000}, {lo:0.00000}   X {TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(w.Y)}, Y {TerrainTool.Core.Campaign.CampaignDoc.SimToGrid(w.X)} km   ({1 / _scale / FtPerM:0.#} m/px){sel}");
        if (_brush != BrushMode.Off)
        {
            _mouse = p;
            if (_stroke != null && _panStart == null && e.LeftButton == MouseButtonState.Pressed)
            {
                var last = _stroke.Points[^1];
                double step = Math.Max(BrushM * FtPerM / 4, 2 / _scale);
                if (Math.Sqrt((w.X - last.X) * (w.X - last.X) + (w.Y - last.Y) * (w.Y - last.Y)) >= step) _stroke.Points.Add((w.X, w.Y));
            }
            if (_panStart == null) { InvalidateVisual(); return; }
        }
        if (_panStart is Point ps)
        {
            _cx = _panCx + (p.Y - ps.Y) / _scale;
            _cy = _panCy - (p.X - ps.X) / _scale;
            InvalidateVisual();
            return;
        }
        if (_dragStart is Point ds && Selected != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragMoved && (p - ds).Length < 3) return;
            if (!_dragMoved) { _undo.Push((Selected, _dragX, _dragY, _dragH, Selected.Junction, Selected.SwapOcd, Selected.SwapName, Selected.DropEdit)); _dragMoved = true; }
            if (_rotating)
            {
                var c = ToScreen(_pivotX, _pivotY);
                double a = Math.Atan2(p.X - c.X, -(p.Y - c.Y)) * 180 / Math.PI;
                double delta = a - _dragAngle;
                EditorData.RotateAbout(Selected, _dragX, _dragY, _dragH, delta, _pivotX, _pivotY);
                // Shift: la pista principale a gradi interi
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _d.MainRunway(Selected) is Runway rr)
                    EditorData.RotateAbout(Selected, Selected.X, Selected.Y, Selected.Heading, Math.Round(rr.Hdg) - rr.Hdg, _pivotX, _pivotY);
            }
            else
            {
                Selected.X = _dragX - (p.Y - ds.Y) / _scale;
                Selected.Y = _dragY + (p.X - ds.X) / _scale;
            }
            InvalidateVisual();
            Edited?.Invoke(Selected);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        // tasto destro senza trascinare: menu della rete degli obiettivi
        if (e.ChangedButton == MouseButton.Right && _rightDown is Point rd && (e.GetPosition(this) - rd).Length < 4)
        {
            _rightDown = null; _panStart = null; ReleaseMouseCapture();
            ShowNetworkMenu(rd);
            return;
        }
        _rightDown = null;
        _panStart = null;
        if (_stroke != null)
        {
            Strokes.Add(_stroke);
            _stroke = null;
            SaveStrokes(_brush == BrushMode.Remove ? "Water removed" : "Water added");
            InvalidateVisual();
        }
        if (_dragStart != null && _dragMoved && Selected != null) Edited?.Invoke(Selected);
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_mouse != null) { _mouse = null; InvalidateVisual(); }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) InvalidateVisual();   // goniometro solo con Ctrl
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _linkFrom != 0) { _linkFrom = 0; HoverText?.Invoke("Link cancelled"); InvalidateVisual(); e.Handled = true; return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) { InvalidateVisual(); return; }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0, ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Z && ctrl && _brush != BrushMode.Off)
        {
            if (Strokes.Count > 0) { Strokes.RemoveAt(Strokes.Count - 1); SaveStrokes("Last water stroke undone"); InvalidateVisual(); }
            e.Handled = true; return;
        }
        if (e.Key == Key.Z && ctrl)
        {
            if (_undo.Count > 0)
            {
                var (o, x, y, h, j, sw, swn, dr) = _undo.Pop();
                o.X = x; o.Y = y; o.Heading = h; o.SwapOcd = sw; o.SwapName = swn; o.DropEdit = dr;
                if (o.Junction != j) _d.SetJunction(o, j);
                Selected = o; SelectionChanged?.Invoke(o); Edited?.Invoke(o);
                InvalidateVisual();
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.L) { _labels = !_labels; InvalidateVisual(); e.Handled = true; return; }
        if (e.Key is Key.D3 or Key.NumPad3) { Preview3DRequested?.Invoke(); e.Handled = true; return; }
        if (Selected == null) return;
        var s = Selected;
        double step = shift ? 100 : 10, rot = ctrl ? 0.1 : shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.F: Select(s, true); e.Handled = true; return;
            case Key.Up: PushUndo(s); s.X += step; break;
            case Key.Down: PushUndo(s); s.X -= step; break;
            case Key.Left: PushUndo(s); s.Y -= step; break;
            case Key.Right: PushUndo(s); s.Y += step; break;
            case Key.Q: case Key.E:
            {
                PushUndo(s);
                var (px, py) = _d.Pivot(s);
                EditorData.RotateAbout(s, s.X, s.Y, s.Heading, e.Key == Key.Q ? -rot : rot, px, py);
                break;
            }
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
        Edited?.Invoke(s);
    }

    /// <summary>L'obiettivo sotto il punto: dentro la sagoma di un pezzo, altrimenti il centro piu' vicino entro 12 pixel.</summary>
    private EdObjective? PickAt(Point p)
    {
        var w = ToWorld(p);
        double tol = 12 / _scale;
        EdObjective? best = null; double bestD = double.MaxValue;
        bool detail = _scale > 0.004;
        foreach (var o in _d.Objectives)
        {
            double r = detail ? _d.LayoutOf(o).Radius : 0;
            double dx = w.X - o.X, dy = w.Y - o.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d > r + tol) continue;
            if (d < tol && d < bestD) { best = o; bestD = d; continue; }
            if (!detail) continue;
            foreach (var f in _d.LayoutOf(o).Features)
                if (Inside(EditorData.FeatureCorners(o, f), w.X, w.Y))
                {
                    // il selezionato vince a parita'
                    double score = o == Selected ? -1 : d;
                    if (score < bestD) { best = o; bestD = score; }
                    break;
                }
        }
        return best;
    }

    private static bool Inside((double X, double Y)[] poly, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > y) != (poly[j].Y > y) && x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
        return c;
    }
}

/// <summary>
/// Scelta della base coreana: elenco delle basi complete della 4.38 dalla piu' simile (piste: numero, lunghezza, distanze),
/// la base selezionata si vede subito sulla mappa (e nel 3D) gia' messa sulla pista.
/// </summary>
public sealed class KoreaBaseDialog : Window
{
    private readonly List<(KoreaBase Base, Placement Where)> _all;
    private readonly int _runways;
    private readonly DataGrid _list = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        CanUserAddRows = false, CanUserSortColumns = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro,
    };
    private readonly CheckBox _military = new() { Content = "Military bases only", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _anyCount = new() { Content = "Also bases with a different number of runways", Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _noFixed = new() { Content = "Only bases that keep their original runway", IsChecked = false,
        ToolTip = "Many Korean bases have the runway numbers painted in their runway models. If they are turned, Objects replaces their runway with generic 4.38 runway pieces with the right numbers (taxiways, shelters and the rest stay). Tick this to see only the bases that keep their own runway here." };
    private readonly Action<(KoreaBase, Placement)?> _pick;
    private readonly TextBox _find = new() { Width = 160, Margin = new Thickness(4, 0, 14, 0), ToolTip = "Find a base by name, ICAO code or OCD number (e.g. Gimpo, RKSO, 1312)" };
    private readonly ComboBox _source = new() { Width = 170, Margin = new Thickness(4, 0, 14, 0) };

    /// <summary>Riga dell'elenco: colonne ordinabili (numeri come numeri).</summary>
    public sealed record Item(KoreaBase Base, Placement Where)
    {
        public double Distance => Math.Round(Where.Cost / 3.2808);
        public string RunwayKept => Base.FixedNumbers.Count == 0 || Where.NumbersOk ? "own" : "generic";
        public string Name => Base.Name;
        public string Source => Base.FromTheater ? "Theater" : "Korea";
        public int Ocd => Base.RealOcd;
        public int Runways => Base.Runways.Count;
        public double LongestM => Math.Round(Base.Runways.Max(r => r.Len) / 3.2808);
        public string Lengths => string.Join(" / ", Base.Runways.Select(r => (r.Len / 3.2808).ToString("0", CultureInfo.InvariantCulture)));
        public string Kind => Base.Military ? "military" : "civil";
        public int Pieces => Base.Features;
        public int Parking => Base.Parking;
        public double WingspanM => Base.MaxWingspanFt >= 9000 ? 999 : Math.Round(Base.MaxWingspanFt * 0.3048);
        public string WingspanText => Base.MaxWingspanFt >= 9000 ? "any" : Base.MaxWingspanFt > 0 ? (Base.MaxWingspanFt * 0.3048).ToString("0", CultureInfo.InvariantCulture) : "";
    }

    public KoreaBaseDialog(EdObjective o, List<(KoreaBase, Placement)> all, int runways, Action<(KoreaBase, Placement)?> pick)
    {
        _all = all; _runways = runways; _pick = pick;
        Title = $"Replace base – {o.Name}";
        Width = 1100; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var top = new StackPanel { Margin = new Thickness(8) };
        top.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Bases to put on this airbase, most similar first: complete BMS 4.38 Korean bases (with their taxiway asphalt model) and the bases of this theater " +
                   $"(also the ones imported with Database Interchange). {runways} runway(s) here. " +
                   "The number on the left is how far the runways are from these ones (centres, angle, length). " +
                   "The selected base is shown on the map right away: check it on the photo, then OK. Only the main Korean objective " +
                   "is used (extra parts of big Korean bases are other objectives and are not imported)."
        });
        var opts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        opts.Children.Add(new TextBlock { Text = "Source", VerticalAlignment = VerticalAlignment.Center });
        foreach (var x in new[] { "All", "Korea 4.38", "This theater" }) _source.Items.Add(x);
        _source.SelectedIndex = 0;
        opts.Children.Add(_source);
        opts.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center });
        opts.Children.Add(_find);
        opts.Children.Add(_military); opts.Children.Add(_anyCount); opts.Children.Add(_noFixed);
        top.Children.Add(opts);
        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90 };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        bottom.Children.Add(ok); bottom.Children.Add(cancel);
        var dp = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom); dp.Children.Add(bottom);
        void Col(string h, string path, double w, string? sortPath = null, bool right = false)
        {
            var c = new DataGridTextColumn { Header = h, Binding = new Binding(path), Width = new DataGridLength(w), SortMemberPath = sortPath ?? path };
            if (right) c.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
            _list.Columns.Add(c);
        }
        Col("Distance (m)", nameof(Item.Distance), 85, right: true);
        Col("Runway", nameof(Item.RunwayKept), 65);
        Col("Name", nameof(Item.Name), 200);
        Col("Source", nameof(Item.Source), 65);
        Col("OCD", nameof(Item.Ocd), 55, right: true);
        Col("Rwys", nameof(Item.Runways), 45, right: true);
        Col("Lengths (m)", nameof(Item.Lengths), 110, nameof(Item.LongestM));
        Col("Kind", nameof(Item.Kind), 65);
        Col("Pieces", nameof(Item.Pieces), 55, right: true);
        Col("Parking", nameof(Item.Parking), 60, right: true);
        Col("Max wingspan (m)", nameof(Item.WingspanText), 110, nameof(Item.WingspanM), right: true);
        dp.Children.Add(_list);
        Content = dp;
        _military.Checked += (_, _) => Fill(); _military.Unchecked += (_, _) => Fill();
        _anyCount.Checked += (_, _) => Fill(); _anyCount.Unchecked += (_, _) => Fill();
        _noFixed.Checked += (_, _) => Fill(); _noFixed.Unchecked += (_, _) => Fill();
        _find.TextChanged += (_, _) => Fill();
        _source.SelectionChanged += (_, _) =>
        {
            // le basi del teatro hanno spesso nomi da aeroporto civile ("Airport"): il filtro "militari" le nasconderebbe
            if (_source.SelectedIndex == 2) _military.IsChecked = false;
            Fill();
        };
        _list.SelectionChanged += (_, _) => _pick(_list.SelectedItem is Item it ? (it.Base, it.Where) : null);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) DialogResult = true; };
        ok.Click += (_, _) => { if (_list.SelectedItem != null) DialogResult = true; };
        Fill();
    }

    private void Fill()
    {
        var rows = new List<Item>();
        string find = _find.Text.Trim();
        foreach (var (b, w) in _all.OrderBy(t => t.Where.Cost))
        {
            if (find.Length > 0 && b.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0 && b.RealOcd.ToString(CultureInfo.InvariantCulture) != find) continue;
            if (_source.SelectedIndex == 1 && b.FromTheater || _source.SelectedIndex == 2 && !b.FromTheater) continue;
            // cercata col suo numero OCD: si mostra comunque, anche se i filtri la nasconderebbero
            bool byNumber = find.Length > 0 && b.RealOcd.ToString(CultureInfo.InvariantCulture) == find;
            if (!byNumber && _military.IsChecked == true && !b.Military) continue;
            if (!byNumber && _anyCount.IsChecked != true && b.Runways.Count != _runways) continue;
            if (!byNumber && _noFixed.IsChecked == true && !w.NumbersOk) continue;
            rows.Add(new Item(b, w));
        }
        // il riordino scelto cliccando sul capocolonna resta quando cambiano i filtri
        var sort = _list.Columns.Where(c => c.SortDirection != null).Select(c => (c, c.SortDirection!.Value)).FirstOrDefault();
        _list.ItemsSource = rows;
        if (sort.c != null)
        {
            var view = CollectionViewSource.GetDefaultView(_list.ItemsSource);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(sort.c.SortMemberPath, sort.Item2));
            sort.c.SortDirection = sort.Item2;
        }
    }
}
