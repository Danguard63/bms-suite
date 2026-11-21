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
/// "Station ILS" di Mission Commander: stations+ils.dat del teatro (TACAN, torre UHF/VHF, terra, avvicinamento, OPS, LSO, ATIS, ILS
/// delle piste 1-4, frammenti audio), con i controlli delle frequenze, i doppioni entro 50 NM e gli ILS rispetto alle piste della
/// disposizione nuova delle basi. Si salva in CampaignEdits (mai in BMS): Assemble / Install lo portano nell'add-on.
/// </summary>
public sealed class StationWindow : Window
{
    private readonly CampaignDoc _doc;
    private readonly CampaignDb _db;
    private readonly StationsCheck _chk;
    private readonly string _src, _dst;
    private StationsFile _f;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, FrozenColumnCount = 2 };
    private readonly ListBox _issues = new() { Height = 170 };
    private readonly TextBox _header = new() { AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _onlyProblems = new() { Content = "Only lines with problems", Margin = new Thickness(0, 6, 0, 0) };
    private bool _dirty;

    public sealed class Row
    {
        private readonly StationWindow _w;
        public Station S { get; }
        public Row(StationWindow w, Station s) { _w = w; S = s; }
        public int Nr { get; set; }
        public string Objective => _w._chk.Objective(S.CampId)?.Name ?? $"? (campaign ID {S.CampId})";
        public string Runways => _w._chk.Objective(S.CampId) is { } o && _w._chk.HasLayout(o) ? StationsCheck.Labels(_w._chk.Runways(o)) : "";
        public int CampId { get => S.CampId; set { S.CampId = value; _w.Changed(); } }
        public int Channel { get => S.TcnChannel; set { S.TcnChannel = value; _w.Changed(); } }
        public string Band { get => S.TcnBand; set { S.TcnBand = value.Trim().ToUpperInvariant(); _w.Changed(); } }
        public int Callsign { get => S.TcnCallsign; set { S.TcnCallsign = value; _w.Changed(); } }
        public int Range { get => S.TcnRange; set { S.TcnRange = value; _w.Changed(); } }
        public int TcnType { get => S.TcnType; set { S.TcnType = value; _w.Changed(); } }
        private static int Khz(string v) => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 ? (int)Math.Round(m * 1000) : 0;
        private static int Ils10(string v) => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 ? (int)Math.Round(m * 100) : 0;
        public string Uhf { get => StationsFile.Mhz(S.Uhf); set { S.Uhf = Khz(value); _w.Changed(); } }
        public string Vhf { get => StationsFile.Mhz(S.Vhf); set { S.Vhf = Khz(value); _w.Changed(); } }
        public string Gnd { get => StationsFile.Mhz(S.GroundUhf); set { S.GroundUhf = Khz(value); _w.Changed(); } }
        public string App { get => StationsFile.Mhz(S.ApproachUhf); set { S.ApproachUhf = Khz(value); _w.Changed(); } }
        public string Lso { get => StationsFile.Mhz(S.LsoUhf); set { S.LsoUhf = Khz(value); _w.Changed(); } }
        public string Ops { get => StationsFile.Mhz(S.OpsUhf); set { S.OpsUhf = Khz(value); _w.Changed(); } }
        public string Atis { get => StationsFile.Mhz(S.AtisVhf); set { S.AtisVhf = Khz(value); _w.Changed(); } }
        public string Ils1 { get => StationsFile.IlsMhz(S.Ils1); set { S.Ils1 = Ils10(value); _w.Changed(); } }
        public string Ils2 { get => StationsFile.IlsMhz(S.Ils2); set { S.Ils2 = Ils10(value); _w.Changed(); } }
        public string Ils3 { get => StationsFile.IlsMhz(S.Ils3); set { S.Ils3 = Ils10(value); _w.Changed(); } }
        public string Ils4 { get => StationsFile.IlsMhz(S.Ils4); set { S.Ils4 = Ils10(value); _w.Changed(); } }
        public int TowerFrag { get => S.TowerFrag; set { S.TowerFrag = value; _w.Changed(); } }
        public int GroundFrag { get => S.GroundFrag; set { S.GroundFrag = value; _w.Changed(); } }
        public int ApproachFrag { get => S.ApproachFrag; set { S.ApproachFrag = value; _w.Changed(); } }
        public int AtisFrag { get => S.AtisFrag; set { S.AtisFrag = value; _w.Changed(); } }
        public string Problems { get; set; } = "";
        public Brush Back { get; set; } = Brushes.Transparent;
    }

    public StationWindow(CampaignWorkspace ws, CampaignDoc doc, CampaignDb db, CampaignDoc? oldDoc, CampaignDb? oldDb)
    {
        _doc = doc; _db = db;
        _chk = new StationsCheck(doc, db);
        if (oldDoc != null && oldDb != null)
        {
            var oc = new StationsCheck(oldDoc, oldDb);
            _chk.OldRunways = id => oc.Objective(id) is { } o && oc.HasLayout(o) ? oc.Runways(o) : null;
        }
        string edits = ws.EditsFolder;
        string? Find(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("stations+ils.dat", StringComparison.OrdinalIgnoreCase)) : null;
        // salvato qui, poi la copia Install del progetto, poi l'originale (vecchio add-on o cartella Campaign del teatro, sola lettura)
        _src = ws.SearchDirs().Select(Find).FirstOrDefault(f => f != null) ?? "";
        _dst = Path.Combine(edits, _src.Length > 0 ? Path.GetFileName(_src) : "stations+ils.dat");
        _f = _src.Length > 0 && File.Exists(_src) ? StationsFile.Load(_src) : new StationsFile();
        Title = "Station ILS – " + (_src.Length > 0 ? _src : "(no stations+ils.dat found)");
        Width = 1600; Height = 950;

        // colonne (frequenze in MHz come in Mission Commander)
        void C(string h, string path, double w, bool ro = false)
            => _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), IsReadOnly = ro });
        C("Nr", nameof(Row.Nr), 38, true); C("Objective", nameof(Row.Objective), 190, true); C("CampID", nameof(Row.CampId), 55);
        C("TACAN", nameof(Row.Channel), 50); C("Band", nameof(Row.Band), 40); C("Callsign", nameof(Row.Callsign), 60); C("Range", nameof(Row.Range), 48); C("Type", nameof(Row.TcnType), 38);
        C("UHF", nameof(Row.Uhf), 62); C("VHF", nameof(Row.Vhf), 62); C("GND", nameof(Row.Gnd), 62); C("APP", nameof(Row.App), 62); C("LSO", nameof(Row.Lso), 62);
        C("OPS", nameof(Row.Ops), 62); C("ATIS", nameof(Row.Atis), 62);
        C("ILS 1", nameof(Row.Ils1), 52); C("ILS 2", nameof(Row.Ils2), 52); C("ILS 3", nameof(Row.Ils3), 52); C("ILS 4", nameof(Row.Ils4), 52);
        C("Runways (new layout)", nameof(Row.Runways), 120, true);
        C("TWR frag", nameof(Row.TowerFrag), 58); C("GND frag", nameof(Row.GroundFrag), 58); C("APP frag", nameof(Row.ApproachFrag), 58); C("ATIS frag", nameof(Row.AtisFrag), 58);
        C("Problems", nameof(Row.Problems), 0, true);
        var rs = new Style(typeof(DataGridRow)); rs.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(Row.Back)))); _grid.RowStyle = rs;

        var left = new StackPanel { Width = 175, Margin = new Thickness(0, 0, 8, 0) };
        Button B(string t, Action a, string tip) { var b = UnitText.Btn(t, a, tip); b.HorizontalAlignment = HorizontalAlignment.Stretch; left.Children.Add(b); return b; }
        var objCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 2), ToolTip = "Airbases and airstrips of the campaign without a line" };
        left.Children.Add(new TextBlock { Text = "Add a line for", FontWeight = FontWeights.SemiBold });
        left.Children.Add(objCombo);
        void FillMissing()
        {
            objCombo.Items.Clear();
            var have = _f.Stations.Select(s => s.CampId).ToHashSet();
            foreach (var o in _doc.Objectives.Where(o => _chk.IsAirfield(o) && !have.Contains(o.CampId)).OrderBy(o => o.Name))
                objCombo.Items.Add(new ComboBoxItem { Content = $"{o.Name} ({o.CampId})", Tag = o });
            if (objCombo.Items.Count > 0) objCombo.SelectedIndex = 0;
        }
        B("Add", () =>
        {
            if ((objCombo.SelectedItem as ComboBoxItem)?.Tag is not CampObjective o) return;
            var s = NewStation(o);
            _f.Stations.Add(s);
            Changed(); Fill(); FillMissing();
            _grid.SelectedItem = (_grid.ItemsSource as List<Row>)?.FirstOrDefault(r => r.S == s);
            _grid.ScrollIntoView(_grid.SelectedItem);
        }, "New line with a free TACAN channel and free UHF / VHF / ILS frequencies (not used within 50 NM); frags 0 (BMS then says no base name)");
        B("Delete", () => { if (_grid.SelectedItem is Row r) { _f.Stations.Remove(r.S); Changed(); Fill(); FillMissing(); } }, "Delete the selected line");
        B("Move up", () => Move(-1), "");
        B("Move down", () => Move(1), "");
        left.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
        B("Remap ILS", () =>
        {
            var log = _chk.RemapIls(_f);
            if (log.Count == 0) { MessageBox.Show(this, "No base has a different runway structure in the new layout (rotated runways keep their ILS).", Title); return; }
            Changed(); Fill();
            MessageBox.Show(this, string.Join("\n", log), "Remap ILS – " + log.Count + " bases");
        }, "Bases whose new layout has other runways (Korean base, two strips become one…): each ILS goes to the runway end in the same position (1st strip / 1st end…)");
        B("Clear ILS beyond runways", () => { int n = _chk.TrimIls(_f); Changed(); Fill(); MessageBox.Show(this, $"{n} ILS slots cleared (bases with fewer runway ends than ILS slots).", Title); },
          "ILS slots after the last runway end of the new layout (BMS ignores them)");
        B("Remove junction lines", () =>
        {
            var l = _chk.RemoveJunctionLines(_f);
            Changed(); Fill(); FillMissing();
            MessageBox.Show(this, l.Count == 0 ? "No line belongs to a removed base." : $"{l.Count} lines of removed bases (now junctions) deleted:\n" + string.Join("\n", l), Title);
        }, "Lines of the bases you removed (turned into junctions): their TACAN and frequencies become free");
        B("Fill empty GND / APP", FillEmpty, "Airbases with tower but no ground or approach frequency: free UHF frequencies (as Mission Commander)");
        left.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
        var save = B("Save Station ILS", Save, "Writes " + _dst + " (never into BMS)");
        save.FontWeight = FontWeights.SemiBold; save.Padding = new Thickness(8, 6, 8, 6);
        left.Children.Add(_onlyProblems);
        _onlyProblems.Click += (_, _) => Fill();
        left.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11,
            Text = "Frequencies in MHz. Red = error (out of range, ILS without a runway is orange), orange = warning (same frequency or TACAN within 50 NM, " +
                   "different runways in the new layout), yellow = info. Runways: the ends of the new layout in the ILS slot order.",
        });

        var right = new DockPanel { Width = 330, Margin = new Thickness(8, 0, 0, 0) };
        var hdr = new TextBlock { Text = "File header (comments)", FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(hdr, Dock.Top); right.Children.Add(hdr);
        right.Children.Add(_header);
        _header.Text = _f.Header;
        _header.TextChanged += (_, _) => { _f.Header = _header.Text; _dirty = true; };

        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(_issues);
        _issues.MouseDoubleClick += (_, _) =>
        {
            if (_issues.SelectedItem is ListBoxItem { Tag: Station s } && (_grid.ItemsSource as List<Row>)?.FirstOrDefault(r => r.S == s) is { } r)
            { _grid.SelectedItem = r; _grid.ScrollIntoView(r); }
        };

        var center = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); center.Children.Add(bottom);
        center.Children.Add(_grid);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(left, Dock.Left); root.Children.Add(left);
        DockPanel.SetDock(right, Dock.Right); root.Children.Add(right);
        root.Children.Add(center);
        Content = root;
        Loaded += (_, _) => { Fill(); FillMissing(); };
        Closing += (_, e) =>
        {
            if (!_dirty) return;
            var r = MessageBox.Show(this, "Save the changes to Station ILS?", Title, MessageBoxButton.YesNoCancel);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes) Save();
        };
    }

    private void Changed() { _dirty = true; if (IsLoaded) Dispatcher.BeginInvoke(new Action(Recheck)); }

    private void Fill()
    {
        var probs = _chk.Run(_f);
        var by = probs.Where(p => p.S != null).GroupBy(p => p.S!).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<Row>();
        for (int i = 0; i < _f.Stations.Count; i++)
        {
            var s = _f.Stations[i];
            var pl = by.GetValueOrDefault(s) ?? new();
            if (_onlyProblems.IsChecked == true && pl.All(p => p.Level == IssueLevel.Info)) continue;
            var lev = pl.Count == 0 ? (IssueLevel?)null : pl.Min(p => p.Level);
            rows.Add(new Row(this, s)
            {
                Nr = i, Problems = string.Join("; ", pl.Select(p => p.Text)),
                Back = lev == IssueLevel.Error ? new SolidColorBrush(Color.FromRgb(255, 190, 190)) : lev == IssueLevel.Warning ? new SolidColorBrush(Color.FromRgb(255, 215, 150))
                     : lev == IssueLevel.Info ? new SolidColorBrush(Color.FromRgb(255, 250, 205)) : Brushes.Transparent,
            });
        }
        _grid.ItemsSource = rows;
        ShowIssues(probs);
    }

    private void Recheck() => ShowIssues(_chk.Run(_f));

    private void ShowIssues(List<StationsCheck.Problem> probs)
    {
        _issues.Items.Clear();
        foreach (var p in probs.OrderBy(p => p.Level))
            _issues.Items.Add(new ListBoxItem
            {
                Content = $"{p.Level}: {p.Text}", Tag = p.S,
                Foreground = p.Level == IssueLevel.Error ? Brushes.DarkRed : p.Level == IssueLevel.Warning ? Brushes.DarkOrange : Brushes.Gray,
            });
        _status.Text = $"{_f.Stations.Count} lines · {probs.Count(p => p.Level == IssueLevel.Error)} errors, {probs.Count(p => p.Level == IssueLevel.Warning)} warnings, " +
                       $"{probs.Count(p => p.Level == IssueLevel.Info)} notes (double-click a line below to find it)" + (_dirty ? " · not saved" : "");
    }

    private void Move(int d)
    {
        if (_grid.SelectedItem is not Row r) return;
        int i = _f.Stations.IndexOf(r.S), j = i + d;
        if (j < 0 || j >= _f.Stations.Count) return;
        (_f.Stations[i], _f.Stations[j]) = (_f.Stations[j], _f.Stations[i]);
        Changed(); Fill();
        _grid.SelectedItem = (_grid.ItemsSource as List<Row>)?.FirstOrDefault(x => x.S == r.S);
    }

    // ───── frequenze libere (non usate entro 50 NM) ─────

    private IEnumerable<Station> Near(CampObjective at) => _f.Stations.Where(s => _chk.Objective(s.CampId) is { } o &&
        Math.Sqrt(Math.Pow(o.X - at.X, 2) + Math.Pow(o.Y - at.Y, 2)) <= StationsFile.ConflictKm);

    private int FreeUhf(CampObjective at, params int[] alsoUsed)
    {
        var used = Near(at).SelectMany(s => new[] { s.Uhf, s.GroundUhf, s.ApproachUhf, s.OpsUhf, s.LsoUhf }).Concat(alsoUsed).ToHashSet();
        for (int k = 250000; k < 399975; k += 25) if (!used.Contains(k) && k != 243000) return k;
        return 0;
    }

    private int FreeVhf(CampObjective at, params int[] alsoUsed)
    {
        var used = Near(at).SelectMany(s => new[] { s.Vhf, s.AtisVhf }).Concat(alsoUsed).ToHashSet();
        for (int k = 118025; k < 136975; k += 25) if (!used.Contains(k) && k is not (121500 or 121700 or 121900 or 123450)) return k;
        return 0;
    }

    private Station NewStation(CampObjective o)
    {
        var usedTcn = Near(o).Where(s => s.TcnRange > 0).Select(s => s.TcnChannel * 2 + (s.TcnBand == "Y" ? 1 : 0)).ToHashSet();
        int ch = Enumerable.Range(17, 110).FirstOrDefault(c => !usedTcn.Contains(c * 2));   // canali X 17-126 (sotto i 17 di solito liberi per altro)
        var s = new Station { CampId = o.CampId, TcnChannel = ch, TcnBand = "X", TcnCallsign = 32766, TcnRange = 50, TcnType = 1, Comment1 = "", Comment2 = o.Name };
        s.Uhf = FreeUhf(o);
        s.Vhf = FreeVhf(o);
        s.GroundUhf = FreeUhf(o, s.Uhf);
        s.ApproachUhf = FreeUhf(o, s.Uhf, s.GroundUhf);
        s.OpsUhf = FreeUhf(o, s.Uhf, s.GroundUhf, s.ApproachUhf);
        s.AtisVhf = FreeVhf(o, s.Vhf);
        if (_db.ObjectiveType(o.Type) == 1 && _chk.HasLayout(o))
        {
            // un ILS per striscia (stessa frequenza sulle due estremita', come il Mideast)
            var rw = _chk.Runways(o);
            var perStrip = new Dictionary<int, int>();
            for (int i = 0; i < Math.Min(4, rw.Count); i++)
            {
                if (!perStrip.TryGetValue(rw[i].Strip, out int f))
                {
                    _f.Stations.Add(s);                 // perche' FreeIls veda anche le frequenze appena date
                    f = _chk.FreeIls(_f, o);
                    _f.Stations.Remove(s);
                    perStrip[rw[i].Strip] = f;
                }
                s.SetIls(i, f);
            }
        }
        return s;
    }

    private void FillEmpty()
    {
        int n = 0;
        foreach (var s in _f.Stations)
        {
            if (_chk.Objective(s.CampId) is not { } o || _db.ObjectiveType(o.Type) != 1 || s.Uhf == 0) continue;
            if (s.GroundUhf == 0) { s.GroundUhf = FreeUhf(o, s.Uhf, s.ApproachUhf); n++; }
            if (s.ApproachUhf == 0) { s.ApproachUhf = FreeUhf(o, s.Uhf, s.GroundUhf); n++; }
        }
        Changed(); Fill();
        MessageBox.Show(this, $"{n} ground / approach frequencies filled.", Title);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dst)!);
            if (File.Exists(_dst)) File.Copy(_dst, _dst + ".bak", true);
            _f.Header = _header.Text;
            _f.Save(_dst);
            _dirty = false;
            Recheck();
            MessageBox.Show(this, $"Saved: {_dst}\n'Install campaign files into BMS…' (start window) copies it into the theater's Campaign folder.", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
