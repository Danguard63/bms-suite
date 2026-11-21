using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace ObjectiveStudio;

/// <summary>
/// Optimize bridges: un clic rileva tutti i ponti del teatro sul terreno finito (acqua della Watermap / WaterHeightMap,
/// quote della HeightMap) e propone, ponte per ponte: KEEP (acqua sotto: serve alle unita' ed e' un bersaglio),
/// JUNCTION (niente acqua: diventa un incrocio, stessi collegamenti), CHECK (acqua vicina ma non sotto: da guardare).
/// Per i ponti a piu' pezzi calcola le quote perche' l'impalcato sia continuo. Salva bridges.csv nel progetto; Run Objects
/// lo applica. Funziona con qualunque teatro del Terrain Tool: usa solo i dati del teatro, niente file esterni.
/// </summary>
public sealed class BridgesWindow : Window
{
    private readonly TheaterProject _p;
    private readonly EditorData _d;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 12, Minimum = 0, Maximum = 1 };
    private readonly ComboBox _show = new() { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _survey = new() { Content = "Survey all bridges", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _save = new() { Content = "Save bridges.csv", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
    private readonly Button _saveRun = new() { Content = "Save and Run Objects", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = false };
    private List<Row> _rows = new();
    private FlyOverWindow? _fly;

    /// <summary>Run Objects della finestra principale (dopo il salvataggio).</summary>
    public Func<Task>? RunObjects { get; init; }

    /// <summary>Riga modificabile della tabella (Decision).</summary>
    public sealed class Row
    {
        /// <summary>Theater installed in BMS: nothing changes unless chosen (JUNCTION, Move? YES, Level? YES); empty = as the theater has it.</summary>
        public static bool Installed;
        public BridgeRow R { get; set; } = null!;
        public string Name => R.Name;
        public int Id => R.CampId;
        public int Pieces => R.Pieces;
        public string Water => R.WaterUnderM > 0 ? R.WaterUnderM.ToString("0", CultureInfo.InvariantCulture) : "";
        public string Near => R.NearestWaterM > 0 ? R.NearestWaterM.ToString("0", CultureInfo.InvariantCulture) : R.NearestWaterM == 0 ? "under" : "";
        public string Level => R.LevelFt > 0 ? R.LevelFt.ToString("0", CultureInfo.InvariantCulture) : "";
        public string Proposal => BridgePlan.Name(R.Proposal);
        public string Decision { get; set; } = "";
        public string Result => BridgePlan.Name((BridgePlan.Parse(Decision) ?? null) is { } d ? (d == BridgeAction.Check ? BridgeAction.Keep : d) : Installed ? BridgeAction.Keep : R.Final);
        public string Note => R.Note;
        public string Move => R.HasMove ? R.MoveM.ToString("0", CultureInfo.InvariantCulture) : "";
        public string Turn => R.HasMove ? R.TurnDeg.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "";
        public string MoveDecision { get; set; } = "";
        public bool? MoveDec => MoveDecision switch { "YES" => true, "NO" => false, _ => null };
        public string LevelDecision { get; set; } = "";
        public BridgeRow Final => R with
        {
            Decision = BridgePlan.Parse(Decision) is { } x && x != BridgeAction.Check ? x : Installed ? BridgeAction.Keep : null,
            MoveDecision = Installed ? MoveDec == true : MoveDec,
            LevelDecision = LevelDecision == "NO" || (Installed && LevelDecision != "YES") ? false : null,
        };
    }

    /// <summary>Posizioni degli obiettivi all'apertura (il rilievo parte sempre da qui, il Fly over mostra la posa decisa).</summary>
    private readonly Dictionary<int, (double X, double Y, double H)> _start = new();

    public BridgesWindow(TheaterProject p)
    {
        _p = p;
        Row.Installed = p.IsInstalledTheater;
        _d = new EditorData(p);
        if (p.IsInstalledTheater) _saveRun.Content = "Save and Build objects";
        foreach (var o in _d.Objectives.Where(BridgeOptimizer.IsBridge)) _start[o.Id] = (o.X, o.Y, o.Heading);
        Title = "Optimize bridges";
        Width = 1250; Height = 800;

        foreach (var s in new[] { "All bridges", "To junction", "To check", "Kept", "Kept and leveled", "To move onto the river" }) _show.Items.Add(s);
        _show.SelectedIndex = 0;
        _show.SelectionChanged += (_, _) => Refill();

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(_survey);
        top.Children.Add(_save);
        top.Children.Add(_saveRun);
        top.Children.Add(new TextBlock { Text = "Show ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_show);
        top.Children.Add(Btn("Fly over selected", FlySelected));
        top.Children.Add(Btn("Satellite map of selected", MapSelected));

        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = 210, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(Row.Id)), Width = 55, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Pieces", Binding = new Binding(nameof(Row.Pieces)), Width = 55, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Water under (m)", Binding = new Binding(nameof(Row.Water)), Width = 105, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Nearest water (m)", Binding = new Binding(nameof(Row.Near)), Width = 115, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Level (ft)", Binding = new Binding(nameof(Row.Level)), Width = 70, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Proposal", Binding = new Binding(nameof(Row.Proposal)), Width = 80, IsReadOnly = true });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Decision", Width = 95, ItemsSource = new[] { "", "KEEP", "JUNCTION" },
            SelectedItemBinding = new Binding(nameof(Row.Decision)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Level?", Width = 60, ItemsSource = p.IsInstalledTheater ? new[] { "", "YES", "NO" } : new[] { "", "NO" },
            SelectedItemBinding = new Binding(nameof(Row.LevelDecision)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Move (m)", Binding = new Binding(nameof(Row.Move)), Width = 65, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Turn (°)", Binding = new Binding(nameof(Row.Turn)), Width = 60, IsReadOnly = true });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Move?", Width = 65, ItemsSource = new[] { "", "YES", "NO" },
            SelectedItemBinding = new Binding(nameof(Row.MoveDecision)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new Binding(nameof(Row.Note)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _grid.MouseDoubleClick += (_, _) => FlySelected();

        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0),
            Text = "Survey reads the finished terrain of this project (no outside data, works for any theater): water under the pieces of every bridge " +
                   "(Output\\TB_Input\\Watermap.raw, or the NewTerrain WaterHeightMap), water nearby, and the ground under each piece. " +
                   "KEEP = water under the bridge: units need it to cross and it is a target. JUNCTION = no water within 300 m: Run Objects turns it into a junction " +
                   "in the same place with the same links, so units still pass and no link changes. CHECK = water nearby but not under it (bridge off the river, " +
                   "or a river too narrow for the water map): stays a bridge unless you choose JUNCTION. Level = multi-piece bridges whose pieces BMS would put at " +
                   "different heights (each piece sits on the ground under its own centre): Run Objects raises/lowers the middle pieces (OffsetZ) so the deck meets, " +
                   "with its own copy of the layout. Move/Turn = bridges with water near or under them that do not cross the river properly (an end in the water, water under " +
                   "the ramps, slanting across the current, river off-centre): the best crossing within 500 m, ends on land, river under the spans and as square to the " +
                   "current as possible; Save writes it into objective_edits.csv like a move made by hand (Move? = NO to refuse). Fly over shows the leveled pieces before Run Objects. Decision: empty = proposal. Nothing is written into BMS here.",
        };

        if (p.IsInstalledTheater)
            help.Text = "THEATER INSTALLED IN BMS: its bridges were placed by the theater makers, so nothing changes unless you choose it: Decision JUNCTION " +
                        "(bridge with no water under it becomes a junction), Move? YES (moved onto the river), Level? YES (deck heights and ground under the ends). " +
                        "Empty = left as the theater has it; the Proposal column is only advice. Bridges the theater already levels itself are never re-leveled. " +
                        "Then Build objects and Install, as for the other edits (backup and Restore as usual).\n\n" + help.Text;
        var root = new DockPanel { Margin = new Thickness(10) };
        foreach (var e in new UIElement[] { top, _bar, _status }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        DockPanel.SetDock(help, Dock.Bottom); root.Children.Add(help);
        root.Children.Add(_grid);
        Content = root;

        _survey.Click += async (_, _) => await SurveyAsync();
        _save.Click += (_, _) => Save();
        _saveRun.Click += async (_, _) => { if (Save() && RunObjects != null) { Close(); await RunObjects(); } };

        var old = BridgePlan.Load(p.ProjectFolder);
        if (old != null)
        {
            _rows = old.Values.Select(r => new Row { R = r, Decision = r.Decision is { } dd ? BridgePlan.Name(dd) : "", MoveDecision = r.MoveDecision switch { true => "YES", false => "NO", _ => "" }, LevelDecision = r.LevelDecision == false ? "NO" : "" }).ToList();
            Refill();
            _save.IsEnabled = _saveRun.IsEnabled = true;
            _status.Text = $"bridges.csv loaded ({_rows.Count} bridges). Survey again after changing the terrain or the objectives.";
        }
        else _status.Text = "Click 'Survey all bridges'.";
    }

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private async Task SurveyAsync()
    {
        _survey.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var decisions = _rows.ToDictionary(r => r.Id, r => r.Final);
            foreach (var o in _d.Objectives) if (_start.TryGetValue(o.Id, out var st)) { o.X = st.X; o.Y = st.Y; o.Heading = st.H; }
            var prog = new Progress<double>(v => _bar.Value = v);
            string src = "";
            var rows = await Task.Run(() =>
            {
                lock (_d)
                {
                    using var w = new WaterProbe(_p);
                    src = w.Source;
                    if (!w.HasWater) throw new InvalidOperationException("no water data: neither Output\\TB_Input\\Watermap.raw nor the NewTerrain WaterHeightMap.raw");
                    return BridgeOptimizer.Survey(_d, w, decisions, prog, CancellationToken.None);
                }
            });
            _rows = rows.Select(r => new Row { R = r, Decision = r.Decision is { } dd ? BridgePlan.Name(dd) : "", MoveDecision = r.MoveDecision switch { true => "YES", false => "NO", _ => "" }, LevelDecision = r.LevelDecision == false ? "NO" : "" }).ToList();
            Refill();
            _save.IsEnabled = _saveRun.IsEnabled = true;
            int keep = rows.Count(r => r.Proposal == BridgeAction.Keep), chk = rows.Count(r => r.Proposal == BridgeAction.Check), jct = rows.Count(r => r.Proposal == BridgeAction.Junction);
            int lev = rows.Count(r => r.Proposal == BridgeAction.Keep && r.LevelFt > 0), mv = rows.Count(r => r.HasMove);
            _status.Text = $"{rows.Count} bridges: {keep} KEEP (water under them, {lev} of them to level, {mv} to move onto the river), {chk} CHECK, {jct} JUNCTION.  Water from {src}. " +
                           (_p.IsInstalledTheater ? "Nothing changes unless you choose it (JUNCTION, Move? YES, Level? YES). Then 'Save bridges.csv' and Build objects." : "Not saved yet: 'Save bridges.csv', then Run Objects.");
        }
        catch (Exception ex) { _status.Text = "Survey failed: " + ex.Message; }
        finally { _survey.IsEnabled = true; Cursor = null; }
    }

    private void Refill()
    {
        IEnumerable<Row> r = _rows;
        r = _show.SelectedIndex switch
        {
            1 => r.Where(x => x.Result == "JUNCTION"),
            2 => r.Where(x => x.R.Proposal == BridgeAction.Check),
            3 => r.Where(x => x.Result == "KEEP"),
            4 => r.Where(x => x.Result == "KEEP" && x.R.LevelFt > 0),
            5 => r.Where(x => x.R.HasMove),
            _ => r,
        };
        _grid.ItemsSource = r.OrderBy(x => x.R.Proposal).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private bool Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            var fin = _rows.Select(r => r.Final).ToList();
            BridgePlan.Save(_p.ProjectFolder, fin);
            // spostamenti sul fiume: come le modifiche a mano dell'editor (objective_edits.csv), che Run Objects applica
            var edits = ObjectiveEdits.Load(_p.ProjectFolder);
            int moved = 0;
            foreach (var r in fin.Where(r => r.MoveFinal))
            {
                bool jn = edits.TryGetValue(r.CampId, out var e0) && e0.Junction;
                edits[r.CampId] = new ObjectiveEdit(r.CampId, r.Name, r.NewX, r.NewY, r.NewHeading, jn);
                _start[r.CampId] = (r.NewX, r.NewY, r.NewHeading);   // il prossimo rilievo parte dalla posizione nuova
                moved++;
            }
            if (moved > 0) ObjectiveEdits.Save(_p.ProjectFolder, edits.Values);
            int j = fin.Count(r => r.Final == BridgeAction.Junction);
            _status.Text = $"Saved {BridgePlan.PathFor(_p.ProjectFolder)}: {j} bridges become junctions, {fin.Count - j} stay bridges" +
                           (moved > 0 ? $", {moved} moved onto their river (objective_edits.csv, previous version in .bak)" : "") +
                           (_p.IsInstalledTheater ? ". Next: Build objects, then Install into BMS." : ". Next: Run Objects, then Install into BMS (objects and campaign files).") + " Survey again after saving: moved bridges then show as 'crosses the river correctly'.";
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, "Not saved: " + ex.Message, Title); return false; }
    }

    private EdObjective? Selected() => _grid.SelectedItem is Row r ? _d.Objectives.FirstOrDefault(o => o.Id == r.Id) : null;

    private void FlySelected()
    {
        if (Selected() is not { } o) { _status.Text = "Select a bridge first."; return; }
        // posa decisa: spostata sul fiume se lo spostamento e' accettato, altrimenti quella di partenza
        if (_grid.SelectedItem is Row row)
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var f = row.Final;
            if (f.MoveFinal) { o.X = f.NewX; o.Y = f.NewY; o.Heading = f.NewHeading; }
            else if (_start.TryGetValue(o.Id, out var st)) { o.X = st.X; o.Y = st.Y; o.Heading = st.H; }
        }
        if (_fly == null || !_fly.IsLoaded)
        {
            _fly = new FlyOverWindow(_d, o) { Owner = this };
            _fly.Closed += (_, _) => _fly = null;
            _fly.Show();
        }
        else { _fly.GoTo(o); _fly.Activate(); }
    }

    private void MapSelected()
    {
        if (Selected() is not { } o) { _status.Text = "Select a bridge first."; return; }
        var (lat, lon) = _p.Theater.CreateGrid().ToLatLon(o.Y * 0.3048, o.X * 0.3048);
        string url = string.Format(CultureInfo.InvariantCulture, "https://www.google.com/maps/@{0:0.00000},{1:0.00000},1500m/data=!3m1!1e3", lat, lon);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = "Browser not opened: " + ex.Message; }
    }
}
