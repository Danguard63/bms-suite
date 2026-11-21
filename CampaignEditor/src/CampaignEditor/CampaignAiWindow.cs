using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// Editor visuale dell'AI della campagna: Falcon4.aii (distanze dal fronte di AWACS, JSTARS, tanker, ECM, BAI, SAR, BARCAP…, regole)
/// e MissionData_*.xml (quote, loiter, numero di aerei di ogni tipo di missione). Confronto con la Korea di BMS (riferimento).
/// Legge dal teatro (sola lettura) e salva nella cartella di salvataggio (progetto: CampaignEdits + Output\Install).
/// </summary>
public sealed class CampaignAiWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private readonly CampaignWorkspace _ws;

    // ── Falcon4.aii
    private AiiFile? _aii, _aiiRef;
    private bool _aiiDirty;
    private readonly List<AiiRow> _aiiRows = new();
    private readonly DataGrid _aiiGrid = Grid();
    private readonly CheckBox _aiiOnlyDiff = new() { Content = "Only values different from Korea", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly FlotDiagram _flot = new() { Height = 300, Margin = new Thickness(0, 0, 0, 6) };

    // ── MissionData
    private readonly Dictionary<string, MissionDataFile> _md = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionDataFile> _mdRef = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mdDirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _mdFile = new() { MinWidth = 180, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _mdAll = new() { Content = "Apply each change to all the MissionData files", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "MissionData_XX.xml is the default, _00 … _05 are the team versions (in the Korea _00…_03 differ from the others for CAS, BAI, SAD, SEAD strike). " +
                  "A value you type goes into every file; the Korea buttons give every file its own Korea values." };
    private readonly ListBox _mdList = new() { Width = 210, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DataGrid _mdGrid = Grid();
    private readonly AltitudeChart _alt = new() { Height = 300, Margin = new Thickness(0, 0, 0, 6) };
    private string? _mission;

    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private bool _filling;

    public CampaignAiWindow(CampaignWorkspace ws)
    {
        _ws = ws;
        Title = "Campaign AI – " + ws.Name;
        Width = 1350; Height = 950;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "AI distances & rules (Falcon4.aii)", Content = AiiTab() });
        tabs.Items.Add(new TabItem { Header = "Mission profiles: altitudes, loiter, flights (MissionData)", Content = MissionTab() });

        var save = UnitText.Btn("Save", () => Save(), "Writes the changed files into " + ws.EditsFolder + (ws.InstallCopyDir != null ? " and Output\\Install" : "") + " (never into BMS)");
        save.FontWeight = FontWeights.SemiBold;
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(UnitText.Btn("Close", Close)); right.Children.Add(save);
        DockPanel.SetDock(right, Dock.Right); bottom.Children.Add(right);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;

        LoadAii();
        LoadMissionData();
        Closing += (_, e) =>
        {
            if (!_aiiDirty && _mdDirty.Count == 0) return;
            var r = MessageBox.Show(this, "Save the changes?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    private static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
        CanUserAddRows = false, CanUserDeleteRows = false, GridLinesVisibility = DataGridGridLinesVisibility.All,
        VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)), HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
        RowHeight = 22,
    };

    private static void Col(DataGrid g, string h, string p, double w, bool ro)
    {
        g.Columns.Add(new DataGridTextColumn
        {
            Header = h, IsReadOnly = ro, Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w),
            Binding = new Binding(p) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
        });
    }

    private static Style RowStyle()
    {
        var rs = new Style(typeof(DataGridRow));
        rs.Setters.Add(new Setter(BackgroundProperty, new Binding("Back")));
        return rs;
    }

    // ───────────────────────── Falcon4.aii

    public sealed class AiiRow : INotifyPropertyChanged
    {
        public string Section { get; init; } = "";
        public string Key { get; init; } = "";
        public string Ref { get; init; } = "";
        public string Help { get; init; } = "";
        private string _value = "";
        public Action<AiiRow>? Changed;
        public string Value
        {
            get => _value;
            set { value = value.Trim(); if (value == _value) return; _value = value; Changed?.Invoke(this); OnChanged(nameof(Value)); OnChanged(nameof(Back)); }
        }
        public void SetSilently(string v) { _value = v; OnChanged(nameof(Value)); OnChanged(nameof(Back)); }
        public bool Differs => Ref.Length > 0 && !NumEq(_value, Ref);
        public Brush Back => Differs ? new SolidColorBrush(Color.FromRgb(255, 243, 200)) : Brushes.White;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private static bool NumEq(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, Ci, out double x) && double.TryParse(b, NumberStyles.Float, Ci, out double y) ? Math.Abs(x - y) < 1e-9 : a.Trim() == b.Trim();

    private UIElement AiiTab()
    {
        Col(_aiiGrid, "Section", nameof(AiiRow.Section), 80, true);
        Col(_aiiGrid, "Parameter", nameof(AiiRow.Key), 220, true);
        Col(_aiiGrid, "Value", nameof(AiiRow.Value), 80, false);
        Col(_aiiGrid, "Korea (BMS)", nameof(AiiRow.Ref), 85, true);
        Col(_aiiGrid, "What it does", nameof(AiiRow.Help), 0, true);
        _aiiGrid.RowStyle = RowStyle();
        _aiiOnlyDiff.Click += (_, _) => FillAiiGrid();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(_aiiOnlyDiff);
        bar.Children.Add(UnitText.Btn("Korea value for the selected", () =>
        {
            foreach (var r in _aiiGrid.SelectedItems.OfType<AiiRow>().ToList()) if (r.Ref.Length > 0) r.Value = r.Ref;
        }, "Select one or more lines (Ctrl/Shift+click)"));
        bar.Children.Add(UnitText.Btn("Korea values for everything", () =>
        {
            if (MessageBox.Show(this, "Put every value back to the Korea one?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var r in _aiiRows) if (r.Ref.Length > 0) r.Value = r.Ref;
        }));
        bar.Children.Add(UnitText.Btn("Korea values for the distances only", () =>
        {
            foreach (var r in _aiiRows.Where(r => FlotDiagram.Keys.Contains(r.Key, StringComparer.OrdinalIgnoreCase))) if (r.Ref.Length > 0) r.Value = r.Ref;
        }, "Only the values drawn above (AWACS, JSTARS, tanker, ECM, BAI, SAR, BARCAP, CAP, sweep)"));

        _flot.ValueChanged += (key, v) =>
        {
            var r = _aiiRows.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (r != null) r.Value = v.ToString(Ci);
        };

        var legend = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            Text = "Distances in km from the front line (FLOT) into friendly territory. Coloured bar = this theater (drag its ends to change it, 5 km steps), " +
                   "grey bar = Korea of BMS. The Korea is 1024 km wide, the Mideast 2048: support aircraft can stay farther back. Lines in yellow below differ from the Korea.",
        };
        var top = new StackPanel();
        top.Children.Add(_flot); top.Children.Add(legend); top.Children.Add(bar);
        var root = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(_aiiGrid);
        return root;
    }

    private void LoadAii()
    {
        string? cur = _ws.FindCampaignFile("Falcon4.aii"), refF = _ws.FindReferenceFile("Falcon4.aii");
        try
        {
            _aiiRef = refF != null ? AiiFile.Load(refF) : null;
            _aii = cur != null ? AiiFile.Load(cur) : refF != null ? AiiFile.Load(refF) : null;
        }
        catch (Exception e) { _status.Text = "Falcon4.aii: " + e.Message; }
        _aiiRows.Clear();
        if (_aii == null) { _status.Text = "Falcon4.aii not found in the theater's Campaign folder nor in the Korea one."; return; }
        foreach (var p in _aii.Params)
        {
            var r = new AiiRow { Section = p.Section, Key = p.Key, Ref = _aiiRef?.Value(p.Key) ?? "", Help = AiiFile.Help.GetValueOrDefault(p.Key, "") };
            r.SetSilently(p.Value);
            r.Changed = row =>
            {
                if (_filling || _aii == null) return;
                _aii.Set(row.Key, row.Value);
                _aiiDirty = true;
                UpdateFlot();
            };
            _aiiRows.Add(r);
        }
        FillAiiGrid();
        UpdateFlot();
        _status.Text = $"Falcon4.aii: {(cur ?? refF)}" + (cur == null ? " (the theater has none: starting from the Korea one)" : "") + (refF != null ? $" · compared with {refF}" : " · Korea file not found");
    }

    private void FillAiiGrid() =>
        _aiiGrid.ItemsSource = _aiiOnlyDiff.IsChecked == true ? _aiiRows.Where(r => r.Differs).ToList() : _aiiRows.ToList();

    private void UpdateFlot()
    {
        double V(AiiFile? f, string k) => f?.Value(k) is { } s && double.TryParse(s, NumberStyles.Float, Ci, out double v) ? v : double.NaN;
        _flot.Set(k => V(_aii, k), k => V(_aiiRef, k));
    }

    // ───────────────────────── MissionData

    public sealed class FieldRow : INotifyPropertyChanged
    {
        public string Field { get; init; } = "";
        public string Ref { get; init; } = "";
        public string Help { get; init; } = "";
        public string Feet => (Field.Contains("Alt") && int.TryParse(_value, NumberStyles.Integer, Ci, out int h)) ? $"{h * 100:N0} ft" : "";
        private string _value = "";
        public Action<FieldRow>? Changed;
        public string Value
        {
            get => _value;
            set { value = value.Trim(); if (value == _value) return; _value = value; Changed?.Invoke(this); On(nameof(Value)); On(nameof(Back)); On(nameof(Feet)); }
        }
        public void SetSilently(string v) { _value = v; On(nameof(Value)); On(nameof(Back)); On(nameof(Feet)); }
        public Brush Back => Ref.Length > 0 && !NumEq(_value, Ref) ? new SolidColorBrush(Color.FromRgb(255, 243, 200)) : Brushes.White;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private UIElement MissionTab()
    {
        Col(_mdGrid, "Field", nameof(FieldRow.Field), 190, true);
        Col(_mdGrid, "Value", nameof(FieldRow.Value), 80, false);
        Col(_mdGrid, "", nameof(FieldRow.Feet), 80, true);
        Col(_mdGrid, "Korea (BMS)", nameof(FieldRow.Ref), 85, true);
        Col(_mdGrid, "What it does", nameof(FieldRow.Help), 0, true);
        _mdGrid.RowStyle = RowStyle();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(new TextBlock { Text = "File ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_mdFile);
        bar.Children.Add(_mdAll);
        bar.Children.Add(UnitText.Btn("Korea values for this mission", () => { if (_mission != null) KoreaMission(_mission); }));
        bar.Children.Add(UnitText.Btn("Korea values for every mission", () =>
        {
            if (MessageBox.Show(this, "Put every mission back to the Korea values?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var m in CurMd?.Missions.Where(MissionDataFile.IsReal).ToList() ?? new()) KoreaMission(m);
        }));
        bar.Children.Add(UnitText.Btn("Korea altitudes only (all missions)", () =>
        {
            foreach (var m in CurMd?.Missions.Where(MissionDataFile.IsReal).ToList() ?? new())
                foreach (var f in new[] { "TgtMinAlt", "TgtMaxAlt", "MisAltLowProfile", "MisAltHighProfile" }) KoreaField(m, f, false);
            RefreshChart(); FillMission();
        }));

        _mdFile.SelectionChanged += (_, _) => { if (!_filling) { RefreshChart(); FillMission(); } };
        _mdList.SelectionChanged += (_, _) =>
        {
            if (_filling || _mdList.SelectedItem is not ListBoxItem { Tag: string m }) return;
            _mission = m; _alt.Selected = m; FillMission();
        };
        _alt.Picked += m => { _mission = m; SelectInList(m); FillMission(); };
        _alt.AltChanged += (m, field, v) => SetField(m, field, v.ToString(Ci));

        var legend = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            Text = "Altitude over the target of every mission type: coloured bar = min-max of this theater (click a mission, then drag the top or bottom of its bar, 500 ft steps), " +
                   "grey = Korea; blue dash = route altitude high profile, green dash = low profile. Values in the files are in hundreds of feet (150 = 15,000 ft). " +
                   "Missions in yellow in the list differ from the Korea.",
        };
        var top = new StackPanel();
        top.Children.Add(bar); top.Children.Add(_alt); top.Children.Add(legend);
        var body = new DockPanel();
        DockPanel.SetDock(_mdList, Dock.Left); body.Children.Add(_mdList);
        body.Children.Add(_mdGrid);
        var root = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(body);
        return root;
    }

    private MissionDataFile? CurMd => (_mdFile.SelectedItem as ComboBoxItem)?.Tag is string n && _md.TryGetValue(n, out var f) ? f : null;
    private MissionDataFile? RefFor(string name) => _mdRef.TryGetValue(name, out var r) ? r : _mdRef.GetValueOrDefault("MissionData_XX.xml");

    private void LoadMissionData()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _ws.SearchDirs().Append(_ws.ReferenceCampaignDir ?? ""))
            if (d.Length > 0 && Directory.Exists(d))
                foreach (var f in Directory.EnumerateFiles(d, "MissionData_*.xml")) names.Add(Path.GetFileName(f));
        foreach (var n in names)
        {
            try
            {
                var cur = _ws.FindCampaignFile(n);
                var rf = _ws.FindReferenceFile(n);
                if (rf != null) _mdRef[n] = MissionDataFile.Load(rf);
                if (cur != null) _md[n] = MissionDataFile.Load(cur);
                else if (rf != null) _md[n] = MissionDataFile.Load(rf);
            }
            catch (Exception e) { _status.Text = n + ": " + e.Message; }
        }
        _filling = true;
        _mdFile.Items.Clear();
        foreach (var n in _md.Keys.OrderBy(n => n.Contains("_XX", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n))
            _mdFile.Items.Add(new ComboBoxItem { Content = n + (n.Contains("_XX", StringComparison.OrdinalIgnoreCase) ? "  (default)" : ""), Tag = n });
        _mdFile.SelectedIndex = _mdFile.Items.Count > 0 ? 0 : -1;
        _filling = false;
        if (_md.Count == 0) { _status.Text += " · no MissionData_*.xml found"; return; }
        _mission = CurMd?.Missions.FirstOrDefault(MissionDataFile.IsReal);
        RefreshChart();
        FillMission();
    }

    private void RefreshChart()
    {
        var f = CurMd;
        if (f == null) return;
        string name = Path.GetFileName(((ComboBoxItem)_mdFile.SelectedItem).Tag as string ?? "");
        var rf = RefFor(name);
        var cols = new List<AltitudeChart.Column>();
        foreach (var m in f.Missions.Where(MissionDataFile.IsReal))
        {
            var a = f.Fields(m); var b = rf?.Fields(m);
            cols.Add(new AltitudeChart.Column(m, MissionDataFile.Title(m),
                MissionDataFile.ParseInt(a.GetValueOrDefault("TgtMinAlt")), MissionDataFile.ParseInt(a.GetValueOrDefault("TgtMaxAlt")),
                MissionDataFile.ParseInt(a.GetValueOrDefault("MisAltLowProfile")), MissionDataFile.ParseInt(a.GetValueOrDefault("MisAltHighProfile")),
                b == null ? -1 : MissionDataFile.ParseInt(b.GetValueOrDefault("TgtMinAlt")), b == null ? -1 : MissionDataFile.ParseInt(b.GetValueOrDefault("TgtMaxAlt"))));
        }
        _alt.Selected = _mission;
        _alt.SetColumns(cols);

        _filling = true;
        _mdList.Items.Clear();
        foreach (var m in f.Missions.Where(MissionDataFile.IsReal))
        {
            var a = f.Fields(m); var b = rf?.Fields(m);
            bool diff = b != null && a.Any(kv => b.TryGetValue(kv.Key, out var v) && !NumEq(v, kv.Value));
            _mdList.Items.Add(new ListBoxItem
            {
                Content = MissionDataFile.Title(m), Tag = m, ToolTip = m,
                Background = diff ? new SolidColorBrush(Color.FromRgb(255, 243, 200)) : Brushes.Transparent,
            });
        }
        SelectInList(_mission);
        _filling = false;
    }

    private void SelectInList(string? m)
    {
        bool was = _filling; _filling = true;
        _mdList.SelectedItem = _mdList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == m);
        _mdList.SelectedItem?.Let(i => _mdList.ScrollIntoView(i));
        _filling = was;
    }

    private void FillMission()
    {
        var f = CurMd;
        if (f == null || _mission == null) { _mdGrid.ItemsSource = null; return; }
        string name = (string)((ComboBoxItem)_mdFile.SelectedItem).Tag!;
        var rf = RefFor(name)?.Fields(_mission);
        var vals = f.Fields(_mission);
        var rows = new List<FieldRow>();
        foreach (var k in f.FieldOrder(_mission))
        {
            var r = new FieldRow { Field = k, Ref = rf?.GetValueOrDefault(k) ?? "", Help = MissionDataFile.Help.GetValueOrDefault(k, "") };
            r.SetSilently(vals.GetValueOrDefault(k, ""));
            string mission = _mission;
            r.Changed = row => { if (!_filling) SetField(mission, row.Field, row.Value, refreshGrid: false); };
            rows.Add(r);
        }
        _mdGrid.ItemsSource = rows;
    }

    private void SetField(string mission, string field, string value, bool refreshGrid = true)
    {
        foreach (var (n, f) in Targets())
            if (f.Get(mission, field) != value && f.Set(mission, field, value)) _mdDirty.Add(n);
        RefreshChart();
        if (refreshGrid) FillMission();
    }

    /// <summary>File toccati da un comando: tutti (spunta) o quello mostrato.</summary>
    private List<KeyValuePair<string, MissionDataFile>> Targets() =>
        _mdAll.IsChecked == true ? _md.ToList() : _md.Where(kv => ReferenceEquals(kv.Value, CurMd)).ToList();

    /// <summary>Valori della Korea: ogni file prende quelli della sua copia coreana (in Korea _00…_03 differiscono da _04, _05, _XX per CAS/BAI/SAD).</summary>
    private void KoreaField(string mission, string field, bool refresh = true)
    {
        foreach (var (n, f) in Targets())
            if (RefFor(n)?.Get(mission, field) is { } v && f.Get(mission, field) != v && f.Set(mission, field, v)) _mdDirty.Add(n);
        if (refresh) { RefreshChart(); FillMission(); }
    }

    private void KoreaMission(string mission)
    {
        foreach (var (n, f) in Targets())
        {
            var rf = RefFor(n)?.Fields(mission);
            if (rf == null) continue;
            foreach (var (k, v) in rf) if (k != "MisType" && f.Get(mission, k) != v && f.Set(mission, k, v)) _mdDirty.Add(n);
        }
        RefreshChart(); FillMission();
    }

    // ───────────────────────── salvataggio

    private bool Save()
    {
        try
        {
            var saved = new List<string>();
            if (_aiiDirty && _aii != null)
            {
                saved.Add(_ws.SaveCampaignFile("Falcon4.aii", p => _aii.Save(p)));
                _aiiDirty = false;
            }
            foreach (var n in _mdDirty.ToList())
            {
                var f = _md[n];
                saved.Add(_ws.SaveCampaignFile(n, p => f.Save(p)));
                _mdDirty.Remove(n);
            }
            _status.Text = saved.Count == 0 ? "Nothing changed." :
                "Saved: " + string.Join(", ", saved.Select(Path.GetFileName)) + " → " + _ws.EditsFolder +
                ". BMS is not touched yet: 'Install campaign files into BMS…' (start window) copies them into the theater.";
            return true;
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    // ───────────────────────── disegno: distanze dal fronte

    private sealed class FlotDiagram : FrameworkElement
    {
        private sealed record Bar(string Label, string? MinKey, string MaxKey, Color Color, string Note);
        private static readonly Bar[] Bars =
        {
            new("AWACS", "MinimumAWACSDistance", "MaximumAWACSDistance", Color.FromRgb(70, 120, 200), "orbit behind the front"),
            new("JSTARS", "MinimumJSTARDistance", "MaximumJSTARDistance", Color.FromRgb(120, 90, 190), "orbit behind the front"),
            new("Tanker", "MinimumTankerDistance", "MaximumTankerDistance", Color.FromRgb(40, 150, 130), "refuelling track"),
            new("ECM", "MinimumECMDistance", "MaximumECMDistance", Color.FromRgb(200, 120, 40), "jammer orbit"),
            new("BARCAP", "BARCAP_CoastlineMinDist", "BARCAP_CoastlineMaxDist", Color.FromRgb(200, 70, 70), "from the coastline"),
            new("BAI", null, "MaxBAIDistance", Color.FromRgb(150, 110, 60), "farthest behind the front"),
            new("SAR", null, "MaxSARDistance", Color.FromRgb(90, 140, 60), "farthest behind the front"),
            new("CAP racetrack", null, "LoiterTurnDistance", Color.FromRgb(110, 110, 110), "length"),
            new("Sweep", null, "SweepRadius", Color.FromRgb(110, 110, 110), "distance covered"),
        };
        public static IEnumerable<string> Keys => Bars.SelectMany(b => new[] { b.MinKey, b.MaxKey }).OfType<string>();

        private Func<string, double> _cur = _ => double.NaN, _ref = _ => double.NaN;
        public event Action<string, double>? ValueChanged;
        private string? _dragKey;
        private const double Left = 120, RowH = 28, TopPad = 30;

        public FlotDiagram() { ClipToBounds = true; Cursor = Cursors.Arrow; }

        public void Set(Func<string, double> cur, Func<string, double> rf) { _cur = cur; _ref = rf; InvalidateVisual(); }

        private double Scale()
        {
            double max = Keys.SelectMany(k => new[] { _cur(k), _ref(k) }).Where(v => !double.IsNaN(v)).DefaultIfEmpty(100).Max();
            return Math.Max(100, Math.Ceiling(max * 1.15 / 50) * 50);
        }

        private double X(double km, double scale) => Left + (ActualWidth - Left - 20) * km / scale;
        private double Km(double x, double scale) => (x - Left) / (ActualWidth - Left - 20) * scale;

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(0, 0, ActualWidth, ActualHeight));
            if (ActualWidth < Left + 60) return;
            double scale = Scale();
            var tf = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText T(string s, double size, Brush b, bool bold = false) =>
                new(s, Ci, FlowDirection.LeftToRight, bold ? new Typeface(tf.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : tf, size, b, dpi);

            // assi
            double bottom = TopPad + Bars.Length * RowH + 4;
            int step = scale > 400 ? 100 : scale > 200 ? 50 : 25;
            for (int km = 0; km <= scale; km += step)
            {
                double x = X(km, scale);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(232, 232, 232)), 1), new Point(x, TopPad - 6), new Point(x, bottom));
                var t = T(km + " km", 10, Brushes.Gray);
                dc.DrawText(t, new Point(x - t.Width / 2, bottom + 2));
            }
            dc.DrawLine(new Pen(Brushes.DarkRed, 2), new Point(X(0, scale), TopPad - 10), new Point(X(0, scale), bottom));
            dc.DrawText(T("FLOT (front line)  →  friendly territory", 11, Brushes.DarkRed, true), new Point(X(0, scale) + 4, 4));

            for (int i = 0; i < Bars.Length; i++)
            {
                var b = Bars[i];
                double y = TopPad + i * RowH;
                dc.DrawText(T(b.Label, 12, Brushes.Black, true), new Point(8, y + 3));
                double rmin = b.MinKey == null ? 0 : _ref(b.MinKey), rmax = _ref(b.MaxKey);
                if (!double.IsNaN(rmax) && !double.IsNaN(rmin))
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(205, 205, 205)), null, new Rect(X(rmin, scale), y + 17, Math.Max(2, X(rmax, scale) - X(rmin, scale)), 6));
                double cmin = b.MinKey == null ? 0 : _cur(b.MinKey), cmax = _cur(b.MaxKey);
                if (double.IsNaN(cmax) || double.IsNaN(cmin)) { dc.DrawText(T("(not in the file)", 10, Brushes.Gray), new Point(Left, y + 3)); continue; }
                var br = new SolidColorBrush(b.Color);
                dc.DrawRectangle(br, null, new Rect(X(cmin, scale), y + 3, Math.Max(2, X(cmax, scale) - X(cmin, scale)), 13));
                foreach (var (k, v) in new[] { (b.MinKey, cmin), (b.MaxKey, cmax) })
                {
                    if (k == null) continue;
                    dc.DrawRectangle(Brushes.White, new Pen(br, 2), new Rect(X(v, scale) - 4, y + 1, 8, 17));
                }
                string txt = (b.MinKey == null ? $"{cmax:0} km" : $"{cmin:0}–{cmax:0} km") +
                             (!double.IsNaN(rmax) ? $"   (Korea {(b.MinKey == null ? $"{rmax:0}" : $"{rmin:0}–{rmax:0}")})" : "") + "   " + b.Note;
                dc.DrawText(T(txt, 10.5, Brushes.DimGray), new Point(Math.Min(X(cmax, scale) + 8, ActualWidth - 300), y + 3));
            }
        }

        private string? HitKey(Point p, out double scale)
        {
            scale = Scale();
            int i = (int)((p.Y - TopPad) / RowH);
            if (i < 0 || i >= Bars.Length) return null;
            var b = Bars[i];
            string? best = null; double bd = 10;
            foreach (var k in new[] { b.MinKey, b.MaxKey })
            {
                if (k == null) continue;
                double v = _cur(k);
                if (double.IsNaN(v)) continue;
                double d = Math.Abs(X(v, scale) - p.X);
                if (d < bd) { bd = d; best = k; }
            }
            return best;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (_dragKey != null && e.LeftButton == MouseButtonState.Pressed)
            {
                double scale = Scale();
                double v = Math.Max(0, Math.Round(Km(p.X, scale) / 5) * 5);
                var bar = Bars.First(b => b.MinKey == _dragKey || b.MaxKey == _dragKey);
                if (bar.MinKey != null)
                {
                    double other = _cur(_dragKey == bar.MinKey ? bar.MaxKey : bar.MinKey);
                    if (_dragKey == bar.MinKey) v = Math.Min(v, other); else v = Math.Max(v, other);
                }
                if (Math.Abs(v - _cur(_dragKey)) > 0.01) ValueChanged?.Invoke(_dragKey, v);
                return;
            }
            Cursor = HitKey(p, out _) != null ? Cursors.SizeWE : Cursors.Arrow;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _dragKey = HitKey(e.GetPosition(this), out _);
            if (_dragKey != null) CaptureMouse();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _dragKey = null; ReleaseMouseCapture();
        }
    }

    // ───────────────────────── disegno: quote delle missioni

    private sealed class AltitudeChart : FrameworkElement
    {
        public sealed record Column(string Mission, string Title, int Min, int Max, int Low, int High, int RefMin, int RefMax);
        private List<Column> _cols = new();
        public string? Selected { get; set; }
        public event Action<string>? Picked;
        public event Action<string, string, int>? AltChanged;
        private string? _dragField;
        private const double LeftPad = 60, BottomPad = 70, TopPad = 10;

        public AltitudeChart() { ClipToBounds = true; }

        public void SetColumns(List<Column> cols) { _cols = cols; InvalidateVisual(); }

        private double MaxAlt => Math.Max(300, Math.Ceiling(_cols.SelectMany(c => new[] { c.Max, c.High, c.RefMax }).DefaultIfEmpty(300).Max() * 1.1 / 50) * 50);
        private double ColW => _cols.Count == 0 ? 1 : (ActualWidth - LeftPad - 10) / _cols.Count;
        private double Y(double h) => TopPad + (ActualHeight - TopPad - BottomPad) * (1 - h / MaxAlt);
        private double H(double y) => (1 - (y - TopPad) / (ActualHeight - TopPad - BottomPad)) * MaxAlt;

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(0, 0, ActualWidth, ActualHeight));
            if (_cols.Count == 0 || ActualHeight < BottomPad + 40) return;
            var tf = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText T(string s, double size, Brush b) => new(s, Ci, FlowDirection.LeftToRight, tf, size, b, dpi);
            double max = MaxAlt;
            for (int h = 0; h <= max; h += 50)
            {
                double y = Y(h);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(235, 235, 235)), 1), new Point(LeftPad, y), new Point(ActualWidth - 10, y));
                var t = T($"{h * 100 / 1000.0:0}k ft", 10, Brushes.Gray);
                dc.DrawText(t, new Point(LeftPad - t.Width - 4, y - t.Height / 2));
            }
            double w = ColW;
            for (int i = 0; i < _cols.Count; i++)
            {
                var c = _cols[i];
                double x = LeftPad + i * w;
                bool sel = c.Mission == Selected;
                if (sel) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 240, 255)), null, new Rect(x, TopPad, w, ActualHeight - TopPad - BottomPad));
                if (c.RefMax >= 0)
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 170)), 1.5), new Rect(x + w * 0.15, Y(c.RefMax), w * 0.7, Math.Max(1, Y(c.RefMin) - Y(c.RefMax))));
                bool diff = c.RefMax >= 0 && (c.RefMin != c.Min || c.RefMax != c.Max);
                var col = diff ? Color.FromRgb(230, 140, 40) : Color.FromRgb(90, 140, 200);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(sel ? (byte)255 : (byte)200, col.R, col.G, col.B)), null,
                    new Rect(x + w * 0.3, Y(c.Max), w * 0.4, Math.Max(2, Y(c.Min) - Y(c.Max))));
                if (sel)
                    foreach (var v in new[] { c.Min, c.Max })
                        dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1.5), new Rect(x + w * 0.2, Y(v) - 3, w * 0.6, 6));
                if (c.High > 0) dc.DrawLine(new Pen(Brushes.RoyalBlue, 2) { DashStyle = DashStyles.Dash }, new Point(x + 2, Y(c.High)), new Point(x + w - 2, Y(c.High)));
                if (c.Low > 0) dc.DrawLine(new Pen(Brushes.SeaGreen, 2) { DashStyle = DashStyles.Dash }, new Point(x + 2, Y(c.Low)), new Point(x + w - 2, Y(c.Low)));
                var lbl = T(c.Title, 10, sel ? Brushes.Black : Brushes.DimGray);
                dc.PushTransform(new RotateTransform(-60, x + w / 2, ActualHeight - BottomPad + 4));
                dc.DrawText(lbl, new Point(x + w / 2 - lbl.Width, ActualHeight - BottomPad + 4));
                dc.Pop();
            }
        }

        private Column? At(Point p)
        {
            int i = (int)((p.X - LeftPad) / ColW);
            return i >= 0 && i < _cols.Count ? _cols[i] : null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);
            var c = At(p);
            if (c == null) return;
            if (c.Mission != Selected) { Selected = c.Mission; Picked?.Invoke(c.Mission); InvalidateVisual(); return; }
            double dMin = Math.Abs(Y(c.Min) - p.Y), dMax = Math.Abs(Y(c.Max) - p.Y);
            if (Math.Min(dMin, dMax) < 10) { _dragField = dMax <= dMin ? "TgtMaxAlt" : "TgtMinAlt"; CaptureMouse(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            var sel = _cols.FirstOrDefault(c => c.Mission == Selected);
            if (_dragField != null && sel != null && e.LeftButton == MouseButtonState.Pressed)
            {
                int v = (int)Math.Max(0, Math.Round(H(p.Y) / 5) * 5);
                if (_dragField == "TgtMinAlt") v = Math.Min(v, sel.Max); else v = Math.Max(v, sel.Min);
                if (v != (_dragField == "TgtMinAlt" ? sel.Min : sel.Max)) AltChanged?.Invoke(sel.Mission, _dragField, v);
                return;
            }
            var c = At(p);
            Cursor = c != null && c.Mission == Selected && Math.Min(Math.Abs(Y(c.Min) - p.Y), Math.Abs(Y(c.Max) - p.Y)) < 10 ? Cursors.SizeNS : Cursors.Arrow;
            ToolTip = c == null ? null : $"{c.Title}: target {c.Min * 100:N0}–{c.Max * 100:N0} ft" + (c.High > 0 ? $", route high {c.High * 100:N0} ft" : "") +
                                         (c.Low > 0 ? $", low {c.Low * 100:N0} ft" : "") + (c.RefMax >= 0 ? $"  ·  Korea {c.RefMin * 100:N0}–{c.RefMax * 100:N0} ft" : "");
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _dragField = null; ReleaseMouseCapture();
        }
    }
}

internal static class LetExt
{
    public static void Let<T>(this T o, Action<T> a) where T : class => a(o);
}
