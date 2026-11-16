using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;

namespace ObjectiveStudio;

/// <summary>
/// Review coastal moves: gli obiettivi che Run Objects ha trovato con gli edifici in acqua sul terreno nuovo e ha spostato verso
/// terra (coastal_moves.csv del progetto). Per ognuno: quanto e' stato spostato, edifici in acqua prima/dopo, moli e navi dei porti;
/// Fly over nella posizione originale o in quella nuova; scelta MOVE (tieni lo spostamento) o KEEP (resta dov'era nell'add-on).
/// Save scrive le scelte; Run Objects le rispetta. Funziona con qualunque teatro del Terrain Tool.
/// </summary>
public sealed class CoastalWindow : Window
{
    private readonly TheaterProject _p;
    private readonly EditorData _d;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _show = new() { Width = 190, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _save = new() { Content = "Save coastal_moves.csv", FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _saveRun = new() { Content = "Save and Run Objects", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
    private List<Row> _rows = new();
    private Dictionary<int, RealPlace> _real = new();
    private readonly Button _find = new() { Content = "Find real places (OSM)", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "Looks in OpenStreetMap for a city, town, village or island with the same name within 15 km of each objective (internet needed). " +
                  "Run Objects then moves the objective onto its real place instead of only towards the nearest land: the add-on put many objectives kilometres away from the real place." };
    private FlyOverWindow? _fly;

    public Func<Task>? RunObjects { get; init; }

    public sealed class Row
    {
        public CoastalRow R { get; set; } = null!;
        public string Name => R.Name;
        public int Id => R.CampId;
        public string Type => R.Type;
        public int Pieces => R.Pieces;
        public string Water => R.Moved ? $"{R.WaterBefore} → {R.WaterAfter}" : $"{R.WaterBefore}";
        public string Marine => R.Marine > 0 ? $"{R.MarineInWater}/{R.Marine}" : "";
        public string Move => R.Moved ? R.MoveM.ToString("0", CultureInfo.InvariantCulture) : "";
        public string Decision { get; set; } = "";
        public string Result => !R.Moved ? "not moved" : Decision == "KEEP" ? "stays" : "moved";
        public string Note => R.Note;
        public RealPlace? Real { get; set; }
        public string RealText => Real == null ? "" : $"{Real.OsmName} ({Real.OsmKind}) {Real.DistKm:0.0} km";
        public bool UseReal { get; set; } = true;
        public CoastalRow Final => R with { Decision = CoastalPlan.Parse(Decision) };
    }

    public CoastalWindow(TheaterProject p)
    {
        _p = p;
        _d = new EditorData(p);
        Title = "Review coastal moves";
        Width = 1250; Height = 800;
        foreach (var s in new[] { "All", "Moved", "Ports", "Moved more than 2 km", "Moved more than 1 km", "Moved less than 500 m", "Not moved (no land near)", "KEEP" }) _show.Items.Add(s);
        _show.SelectedIndex = 0;
        _show.SelectionChanged += (_, _) => Refill();

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(_save); top.Children.Add(_saveRun); top.Children.Add(_find);
        top.Children.Add(new TextBlock { Text = "Show ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_show);
        top.Children.Add(Btn("Fly over: original place", () => Fly(false)));
        top.Children.Add(Btn("Fly over: moved place", () => Fly(true)));
        top.Children.Add(Btn("Satellite map (original place)", MapSelected));
        top.Children.Add(Btn("All shown → MOVE", () => SetShown("MOVE")));
        top.Children.Add(Btn("All shown → KEEP", () => SetShown("KEEP")));

        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = 230, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(Row.Id)), Width = 55, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(Row.Type)), Width = 90, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Pieces", Binding = new Binding(nameof(Row.Pieces)), Width = 55, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "In water (before → after)", Binding = new Binding(nameof(Row.Water)), Width = 150, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Piers/ships in water", Binding = new Binding(nameof(Row.Marine)), Width = 120, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Move (m)", Binding = new Binding(nameof(Row.Move)), Width = 70, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Real place (OSM)", Binding = new Binding(nameof(Row.RealText)), Width = 190, IsReadOnly = true });
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Use", Binding = new Binding(nameof(Row.UseReal)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 40 });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Decision", Width = 80, ItemsSource = new[] { "", "MOVE", "KEEP" },
            SelectedItemBinding = new Binding(nameof(Row.Decision)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Note", Binding = new Binding(nameof(Row.Note)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _grid.MouseDoubleClick += (_, _) => Fly(true);

        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0),
            Text = "Run Objects checks every objective (not airbases, bridges, junctions, dams) against the water of the finished terrain, as BMS draws it. " +
                   "If more than a quarter of its buildings are in the water, it moves the objective by the shortest distance (up to 4 km) that leaves 10% or less " +
                   "of them in the water. In ports, piers, quays and ships do not count as buildings and at least half of them should still touch the water, so the " +
                   "port stays on the coast. Decision: empty or MOVE = keep the move, KEEP = leave the objective where the add-on put it (fix it by hand in the " +
                   "objective editor if needed: a hand edit always wins). Save, then Run Objects and, in the Campaign Editor, Apply object changes.",
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        foreach (var e in new UIElement[] { top, _status }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        DockPanel.SetDock(help, Dock.Bottom); root.Children.Add(help);
        root.Children.Add(_grid);
        Content = root;

        _save.Click += (_, _) => Save();
        _find.Click += async (_, _) => await FindRealAsync();
        _saveRun.Click += async (_, _) => { if (Save() && RunObjects != null) { Close(); await RunObjects(); } };

        var plan = CoastalPlan.Load(p.ProjectFolder);
        _real = RealPlaces.Load(p.ProjectFolder);
        _rows = plan.Values.Select(r => new Row { R = r, Decision = CoastalPlan.Name(r.Decision), Real = _real.GetValueOrDefault(r.CampId), UseReal = _real.GetValueOrDefault(r.CampId)?.Use ?? true }).ToList();
        Refill();
        int moved = _rows.Count(r => r.R.Moved), ports = _rows.Count(r => r.R.Marine > 0 || r.Type == "Port");
        _status.Text = _rows.Count == 0
            ? $"{CoastalPlan.FileName} not found or empty: run Run Objects first (it writes the list)."
            : $"{_rows.Count} objectives with buildings in the water: {moved} moved ({_rows.Count(r => r.R.MoveM > 2000)} by more than 2 km), {ports} ports, " +
              $"{_rows.Count - moved} not moved. Look at the long moves and the ports first; the rest can stay MOVE.";
    }

    private static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private void Refill()
    {
        IEnumerable<Row> r = _rows;
        r = _show.SelectedIndex switch
        {
            1 => r.Where(x => x.R.Moved),
            2 => r.Where(x => x.Type == "Port" || x.R.Marine > 0),
            3 => r.Where(x => x.R.MoveM > 2000),
            4 => r.Where(x => x.R.MoveM > 1000),
            5 => r.Where(x => x.R.Moved && x.R.MoveM < 500),
            6 => r.Where(x => !x.R.Moved),
            7 => r.Where(x => x.Decision == "KEEP"),
            _ => r,
        };
        _grid.ItemsSource = r.OrderByDescending(x => x.R.MoveM).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SetShown(string d)
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_grid.ItemsSource is IEnumerable<Row> shown) foreach (var r in shown) if (r.R.Moved) r.Decision = d;
        _grid.Items.Refresh();
    }

    private bool Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            CoastalPlan.Save(_p.ProjectFolder, _rows.Select(r => r.Final));
            if (_real.Count > 0)
            {
                foreach (var r in _rows) if (r.Real != null) _real[r.Real.CampId] = r.Real with { Use = r.UseReal };
                SpreadReal();
                RealPlaces.Save(_p.ProjectFolder, _real.Values);
            }
            int keep = _rows.Count(r => r.R.Moved && r.Decision == "KEEP");
            _status.Text = $"Saved {CoastalPlan.PathFor(_p.ProjectFolder)}: {keep} objectives stay where the add-on put them, {_rows.Count(r => r.R.Moved) - keep} are moved. " +
                           "Next: Run Objects, Install objects, then Campaign Editor → Apply object changes and Install campaign files into BMS.";
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, "Not saved: " + ex.Message, Title); return false; }
    }

    /// <summary>Obiettivi finiti sullo stesso luogo in giri diversi: non uno sopra l'altro (RealPlaces.SpreadShared).</summary>
    private void SpreadReal()
    {
        var byId = _rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        RealPlaces.SpreadShared(_real, id => byId.TryGetValue(id, out var r) ? (r.R.OrigX, r.R.OrigY) : null);
        foreach (var r in _rows) if (_real.TryGetValue(r.Id, out var rp)) r.Real = rp with { Use = r.UseReal };
    }

    private async Task FindRealAsync()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_rows.Count == 0) { _status.Text = "No list yet: run Run Objects first."; return; }
        _find.IsEnabled = false;
        _status.Text = "Asking OpenStreetMap (Overpass) for the places around the objectives… (up to a few minutes)";
        try
        {
            // solo centri abitati, porti (di solito col nome del paese) e obiettivi su un'isola: un impianto o una torre radio col nome
            // della citta' finirebbe nel centro citta'
            var objs = _rows.Where(r => r.Type is "City" or "Town" or "Village" or "Port" || r.Name.Contains("Island", StringComparison.OrdinalIgnoreCase))
                            .Where(r => !_real.ContainsKey(r.Id))   // solo quelli che non hanno ancora un luogo: ogni giro aggiunge
                            .Select(r => (r.Id, r.Name, r.R.OrigX, r.R.OrigY)).ToList();
            if (objs.Count == 0) { _status.Text = "Every objective that can have a real place already has one (or none was found by name). Delete real_places.csv to search again from scratch."; return; }
            var prog = new Progress<string>(m => _status.Text = m + " (each server up to 75 s, then the next one)");
            var (found, log, unanswered) = await RealPlaces.FindAsync(_p, objs, 15, 0.8, CancellationToken.None, prog);
            // si aggiungono ai luoghi gia' trovati (e alle spunte Use gia' tolte), che restano come sono
            foreach (var f in found) _real[f.CampId] = f;
            SpreadReal();
            log.Insert(0, $"{objs.Count} objectives without a real place asked, {found.Count} found");
            if (unanswered.Count > 0) log.Add($"{unanswered.Count} objectives not answered by the server: press the button again later");
            RealPlaces.Save(_p.ProjectFolder, _real.Values);
            foreach (var r in _rows) { r.Real = _real.GetValueOrDefault(r.Id); if (r.Real != null) r.UseReal = r.Real.Use; }
            _grid.Items.Refresh();
            _status.Text = string.Join("  |  ", log) + $"  →  {RealPlaces.PathFor(_p.ProjectFolder)}. Check the 'Real place' column (untick Use where the name match is wrong), then Save and Run Objects.";
        }
        catch (Exception ex) { _status.Text = "Real places not found: " + ex.Message; }
        finally { _find.IsEnabled = true; }
    }

    private void Fly(bool moved)
    {
        if (_grid.SelectedItem is not Row row) { _status.Text = "Select an objective first."; return; }
        var o = _d.Objectives.FirstOrDefault(x => x.Id == row.Id);
        if (o == null) { _status.Text = "Objective not found in the editor data."; return; }
        o.X = moved ? row.R.NewX : row.R.OrigX;
        o.Y = moved ? row.R.NewY : row.R.OrigY;
        if (_fly == null || !_fly.IsLoaded)
        {
            _fly = new FlyOverWindow(_d, o) { Owner = this };
            _fly.Closed += (_, _) => _fly = null;
            _fly.Show();
        }
        else { _fly.GoTo(o); _fly.Activate(); }
        _status.Text = $"{row.Name}: {(moved ? "moved place" : "original place")} in the Fly over (water shown as BMS draws it).";
    }

    private void MapSelected()
    {
        if (_grid.SelectedItem is not Row row) { _status.Text = "Select an objective first."; return; }
        var (lat, lon) = _p.Theater.CreateGrid().ToLatLon(row.R.OrigY * 0.3048, row.R.OrigX * 0.3048);
        string url = string.Format(CultureInfo.InvariantCulture, "https://www.google.com/maps/@{0:0.00000},{1:0.00000},2500m/data=!3m1!1e3", lat, lon);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = "Browser not opened: " + ex.Message; }
    }
}
