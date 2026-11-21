using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// Editor degli elenchi del Tactical Engagement: TePlanes.lst (aerei), TeUnits.lst (battaglioni per produttore),
/// TeNavalUnits.lst (navi per produttore). Confronto con la Korea di BMS e con il database del teatro. Salva fuori da BMS.
/// </summary>
public sealed class TeListsWindow : Window
{
    private readonly CampaignWorkspace _ws;
    private readonly CampaignDb _db;
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };

    // aerei
    private TePlanesFile _planes = new(), _planesRef = new();
    private readonly List<PlaneRow> _planeRows = new();
    private readonly DataGrid _planeGrid = Grid();
    private readonly TextBox _planeFind = new() { Width = 160, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _planeOnly = new() { Content = "Only aircraft in the list", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _planesDirty;
    private readonly Dictionary<(int, int, int), int> _vehicleCt = new();
    private readonly Dictionary<int, string> _roleName = new();

    // battaglioni e navi
    private readonly UnitList _ground, _naval;

    public TeListsWindow(CampaignWorkspace ws, CampaignDb db)
    {
        _ws = ws; _db = db;
        Title = "Tactical Engagement lists – " + ws.Name;
        Width = 1350; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var (ct, c) in db.Ct)
        {
            if (c.EntityType == 5 && c.Domain == 2) _vehicleCt.TryAdd((c.Type, c.SubType, c.Specific), ct);
        }
        foreach (var g in db.Ct.Where(kv => kv.Value.EntityType == 4 && kv.Value.Domain == 2 && kv.Value.Type == 3).GroupBy(kv => kv.Value.SubType))
            _roleName[g.Key] = g.Select(kv => db.UcdName(kv.Key)).Where(n => n.Length > 0).GroupBy(n => n).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "";

        _ground = new UnitList(this, "TeUnits.lst", 3, "Battalions");
        _naval = new UnitList(this, "TeNavalUnits.lst", 4, "Naval units");

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Aircraft (TePlanes.lst)", Content = PlanesTab() });
        tabs.Items.Add(new TabItem { Header = "Battalions by producer (TeUnits.lst)", Content = _ground.Build() });
        tabs.Items.Add(new TabItem { Header = "Naval units by producer (TeNavalUnits.lst)", Content = _naval.Build() });

        var save = UnitText.Btn("Save", SaveAndClose, "Writes the changed lists into " + ws.EditsFolder + (ws.InstallCopyDir != null ? " and Output\\Install" : "") + " (never into BMS)");
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

        LoadPlanes();
        _ground.Load();
        _naval.Load();
        Closing += (_, e) =>
        {
            if (!_planesDirty && !_ground.Dirty && !_naval.Dirty) return;
            var r = MessageBox.Show(this, "Save the changes?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    internal static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
        CanUserAddRows = false, CanUserDeleteRows = false, GridLinesVisibility = DataGridGridLinesVisibility.All,
        VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)), HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
        RowHeight = 22, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    internal static void Text(DataGrid g, string h, string p, double w, bool ro = true) =>
        g.Columns.Add(new DataGridTextColumn
        {
            Header = h, IsReadOnly = ro, Binding = new Binding(p) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
            Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w),
        });

    /// <summary>Casella che si spunta con un solo clic.</summary>
    internal static void Check(DataGrid g, string h, string p, double w, string? tip = null)
    {
        var f = new FrameworkElementFactory(typeof(CheckBox));
        f.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(p) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        f.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        f.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        g.Columns.Add(new DataGridTemplateColumn
        {
            Header = tip == null ? h : new TextBlock { Text = h, ToolTip = tip }, Width = new DataGridLength(w), SortMemberPath = p,
            CellTemplate = new DataTemplate { VisualTree = f },
        });
    }

    internal static Style BackStyle()
    {
        var rs = new Style(typeof(DataGridRow));
        rs.Setters.Add(new Setter(BackgroundProperty, new Binding("Back")));
        return rs;
    }

    internal static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 215, 215));
    internal static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(255, 243, 200));
    internal static readonly Brush Green = new SolidColorBrush(Color.FromRgb(220, 242, 220));

    // ───────────────────────── aerei

    public sealed class PlaneRow : INotifyPropertyChanged
    {
        public TePlanesFile.Entry E { get; init; } = new();
        public string Name => E.Name;
        public string DbName { get; init; } = "";
        public string Producer { get; init; } = "";
        public string Role { get; init; } = "";
        public bool InDb { get; init; }
        public bool InKorea { get; init; }
        public string Korea => InKorea ? "yes" : "";
        public string Db => InDb ? "OK" : "MISSING";
        public int Type => E.Type; public int SubType => E.SubType; public int Specific => E.Specific; public int UnitSubType => E.UnitSubType;
        // picture, name and icon of the TE list: can be changed in the table (e.g. the icon of another aircraft of the same shape)
        public int Id { get => E.Id; set { if (E.Id == value) return; E.Id = value; Changed?.Invoke(); On(nameof(Id)); } }
        public int TextId { get => E.Text; set { if (E.Text == value) return; E.Text = value; Changed?.Invoke(); On(nameof(TextId)); } }
        public int Icon { get => E.Icon; set { if (E.Icon == value) return; E.Icon = value; Changed?.Invoke(); On(nameof(Icon)); } }
        private bool _in;
        public Action? Changed;
        public bool InList { get => _in; set { if (_in == value) return; _in = value; Changed?.Invoke(); On(nameof(InList)); On(nameof(Back)); } }
        public void Init(bool v) => _in = v;
        public Brush Back => !InDb ? Red : _in != InKorea ? Yellow : _in ? Green : Brushes.White;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private UIElement PlanesTab()
    {
        Check(_planeGrid, "In list", nameof(PlaneRow.InList), 55, "Selectable in the Tactical Engagement");
        Text(_planeGrid, "Aircraft (list)", nameof(PlaneRow.Name), 220);
        Text(_planeGrid, "In the database", nameof(PlaneRow.DbName), 170);
        Text(_planeGrid, "Producer", nameof(PlaneRow.Producer), 70);
        Text(_planeGrid, "Squadron role", nameof(PlaneRow.Role), 120);
        Text(_planeGrid, "Korea list", nameof(PlaneRow.Korea), 70);
        Text(_planeGrid, "DB", nameof(PlaneRow.Db), 65);
        Text(_planeGrid, "Type", nameof(PlaneRow.Type), 45); Text(_planeGrid, "SubType", nameof(PlaneRow.SubType), 60);
        Text(_planeGrid, "Specific", nameof(PlaneRow.Specific), 60); Text(_planeGrid, "UnitSubType", nameof(PlaneRow.UnitSubType), 80);
        Text(_planeGrid, "ID", nameof(PlaneRow.Id), 60, false); Text(_planeGrid, "text", nameof(PlaneRow.TextId), 50, false); Text(_planeGrid, "Icon", nameof(PlaneRow.Icon), 0, false);
        _planeGrid.RowStyle = BackStyle();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(new TextBlock { Text = "Find ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_planeFind); bar.Children.Add(_planeOnly);
        _planeFind.TextChanged += (_, _) => FillPlanes();
        _planeOnly.Click += (_, _) => FillPlanes();
        bar.Children.Add(UnitText.Btn("Tick / untick the selected", () =>
        {
            var sel = _planeGrid.SelectedItems.OfType<PlaneRow>().ToList();
            bool on = sel.Any(r => !r.InList);
            foreach (var r in sel) r.InList = on;
        }));
        bar.Children.Add(UnitText.Btn("Same as the Korea list", () =>
        {
            if (MessageBox.Show(this, "Make the list the same as the Korea one (aircraft missing from the database are left out)?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var r in _planeRows) r.InList = r.InKorea && r.InDb;
        }));
        bar.Children.Add(UnitText.Btn("Remove aircraft missing from the database", () => { foreach (var r in _planeRows.Where(r => !r.InDb)) r.InList = false; },
            "Red lines: the Type/SubType/Specific is not an aircraft of this theater's database (old add-on entries)"));
        bar.Children.Add(UnitText.Btn("Add from the database…", AddPlane, "An aircraft of the database that is in no list: the picture/text/icon numbers are copied from an aircraft you choose"));

        var legend = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
            Text = "Green = in the list (as in the Korea), yellow = different from the Korea list, red = not in this theater's database. " +
                   "Type/SubType/Specific identify the aircraft in the database, UnitSubType the kind of squadron; ID, text and Icon are interface resources (picture, name, icon): double-click to change them (e.g. the icon of an aircraft of the same shape).",
        };
        var top = new StackPanel();
        top.Children.Add(bar); top.Children.Add(legend);
        var root = new DockPanel { Margin = new Thickness(6) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(_planeGrid);
        return root;
    }

    private void LoadPlanes()
    {
        string? cur = _ws.FindCampaignFile("TePlanes.lst"), rf = _ws.FindReferenceFile("TePlanes.lst");
        try
        {
            _planesRef = rf != null ? TePlanesFile.Load(rf) : new TePlanesFile();
            _planes = cur != null ? TePlanesFile.Load(cur) : rf != null ? TePlanesFile.Load(rf) : new TePlanesFile();
        }
        catch (Exception e) { _status.Text = "TePlanes.lst: " + e.Message; }
        _planeRows.Clear();
        var refKeys = _planesRef.Entries.Select(e => e.Key).ToHashSet();
        var seen = new HashSet<(int, int, int, int)>();
        void Add(TePlanesFile.Entry e, bool inList)
        {
            if (e.Type == 0 || !seen.Add(e.Key)) return;
            int vct = _vehicleCt.GetValueOrDefault((e.Type, e.SubType, e.Specific), -1);
            string dbName = vct >= 0 ? _db.VehicleName(vct) : "";
            var r = new PlaneRow
            {
                E = e, DbName = dbName, InDb = vct >= 0, InKorea = refKeys.Contains(e.Key),
                Producer = TerrainTool.Core.Campaign.Producer.Short(TerrainTool.Core.Campaign.Producer.OfName(dbName.Length > 0 ? dbName : e.Name)),
                Role = _roleName.GetValueOrDefault(e.UnitSubType, ""),
            };
            r.Init(inList);
            r.Changed = () => _planesDirty = true;
            _planeRows.Add(r);
        }
        foreach (var e in _planes.Entries) Add(e, true);
        foreach (var e in _planesRef.Entries) Add(e, false);
        FillPlanes();
        int missing = _planeRows.Count(r => r.InList && !r.InDb), notKorea = _planeRows.Count(r => r.InList != r.InKorea);
        _status.Text = $"TePlanes.lst: {cur ?? rf ?? "(none)"} · {_planeRows.Count(r => r.InList)} aircraft in the list, {missing} not in the database, {notKorea} different from the Korea list";
    }

    private void FillPlanes()
    {
        string q = _planeFind.Text.Trim();
        _planeGrid.ItemsSource = _planeRows
            .Where(r => _planeOnly.IsChecked != true || r.InList)
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.DbName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name.Length > 0 ? r.Name : r.DbName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddPlane()
    {
        var have = _planeRows.Select(r => (r.Type, r.SubType, r.Specific)).ToHashSet();
        // aerei del database usati da almeno uno squadrone e assenti dagli elenchi
        var squadrons = _db.Ct.Where(kv => kv.Value.EntityType == 4 && kv.Value.Domain == 2 && kv.Value.Type == 3).ToList();
        var candidates = new List<(int Vct, string Name, int UnitSub)>();
        foreach (var (vk, vct) in _vehicleCt)
        {
            if (have.Contains(vk)) continue;
            var sq = squadrons.FirstOrDefault(s => _db.UcdOf(s.Key)?.Vehicles.FirstOrDefault() == vct);
            if (sq.Value == null) continue;
            candidates.Add((vct, _db.VehicleName(vct), sq.Value.SubType));
        }
        if (candidates.Count == 0) { MessageBox.Show(this, "Every aircraft of the database that has a squadron is already in a list.", Title); return; }

        var w = new Window { Title = "Add an aircraft to the TE list", Width = 560, Height = 520, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Height = 330 };
        foreach (var c in candidates.OrderBy(c => c.Name)) list.Items.Add(new ListBoxItem { Content = $"{c.Name}   ({_roleName.GetValueOrDefault(c.UnitSub, "squadron")})", Tag = c });
        var tpl = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var r in _planeRows.Where(r => r.InDb).OrderBy(r => r.Name)) tpl.Items.Add(new ComboBoxItem { Content = r.Name, Tag = r });
        tpl.SelectedIndex = 0;
        list.SelectionChanged += (_, _) =>
        {
            // modello proposto: un aereo dello stesso produttore e dello stesso tipo di squadrone
            if (list.SelectedItem is not ListBoxItem { Tag: ValueTuple<int, string, int> c }) return;
            var b = TerrainTool.Core.Campaign.Producer.OfName(c.Item2);
            var best = tpl.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is PlaneRow r && r.UnitSubType == c.Item3 && TerrainTool.Core.Campaign.Producer.OfName(r.DbName) == b)
                       ?? tpl.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is PlaneRow r && r.UnitSubType == c.Item3);
            if (best != null) tpl.SelectedItem = best;
        };
        var ok = UnitText.Btn("Add", () =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: ValueTuple<int, string, int> c } || tpl.SelectedItem is not ComboBoxItem { Tag: PlaneRow t }) return;
            var ct = _db.Ct[c.Item1];
            var e = new TePlanesFile.Entry { Name = c.Item2, Type = ct.Type, SubType = ct.SubType, Specific = ct.Specific, UnitSubType = c.Item3, Id = t.Id, Text = t.TextId, Icon = t.Icon };
            var r = new PlaneRow { E = e, DbName = c.Item2, InDb = true, InKorea = false, Producer = TerrainTool.Core.Campaign.Producer.Short(TerrainTool.Core.Campaign.Producer.OfName(c.Item2)), Role = _roleName.GetValueOrDefault(c.Item3, "") };
            r.Init(true);
            r.Changed = () => _planesDirty = true;
            _planeRows.Add(r);
            _planesDirty = true;
            FillPlanes();
            _planeGrid.SelectedItem = r;
            _planeGrid.ScrollIntoView(r);
            w.Close();
            MessageBox.Show(this, $"'{c.Item2}' added to the aircraft list (selected in the table).\n\nPress Save to write TePlanes.lst.", "Add aircraft",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock { Text = "Aircraft of the database (with a squadron) that are in no list:", Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(list);
        sp.Children.Add(new TextBlock { Text = "Picture / name / icon numbers copied from:", Margin = new Thickness(0, 8, 0, 0) });
        sp.Children.Add(tpl);
        sp.Children.Add(new TextBlock { Text = "(the TE shows that aircraft's picture; the name in the list is the database one)", Foreground = Brushes.Gray, FontSize = 11 });
        sp.Children.Add(ok);
        w.Content = sp;
        w.ShowDialog();
    }

    // ───────────────────────── battaglioni / navi per produttore

    public sealed class UnitRow : INotifyPropertyChanged
    {
        public int Ct { get; init; }
        public string Name { get; init; } = "";
        public string Vehicles { get; init; } = "";
        public int SubType { get; init; }
        public int Specific { get; init; }
        public bool InDb { get; init; }
        public string KoreaSides { get; init; } = "";
        internal TeUnitsFile File = new();
        internal Action? Changed;
        private bool G(int s) => File.Has(s, SubType, Specific);
        private void S(int s, bool v) { if (G(s) == v) return; File.Set(s, SubType, Specific, v); Changed?.Invoke(); On("S" + s); On(nameof(Back)); On(nameof(Sides)); }
        public bool S1 { get => G(1); set => S(1, value); }
        public bool S2 { get => G(2); set => S(2, value); }
        public bool S3 { get => G(3); set => S(3, value); }
        public bool S4 { get => G(4); set => S(4, value); }
        public bool S5 { get => G(5); set => S(5, value); }
        public string Sides => string.Join(",", Enumerable.Range(1, 5).Where(G));
        public Brush Back => !InDb ? Red : Sides != KoreaSides ? Yellow : Sides.Length > 0 ? Green : Brushes.White;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private sealed class UnitList
    {
        private readonly TeListsWindow _w;
        private readonly string _file;
        private readonly int _domain;
        private readonly string _what;
        private TeUnitsFile _cur = new(), _ref = new();
        private readonly List<UnitRow> _rows = new();
        private readonly DataGrid _grid = Grid();
        private readonly TextBox _find = new() { Width = 160, Margin = new Thickness(0, 0, 10, 0) };
        private readonly CheckBox _only = new() { Content = "Only units in the list", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _info = new() { Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
        public bool Dirty;

        public UnitList(TeListsWindow w, string file, int domain, string what) { _w = w; _file = file; _domain = domain; _what = what; }

        public UIElement Build()
        {
            Text(_grid, _what == "Battalions" ? "Battalion type" : "Naval unit type", nameof(UnitRow.Name), 170);
            Text(_grid, "Vehicles", nameof(UnitRow.Vehicles), 260);
            Text(_grid, "CT", nameof(UnitRow.Ct), 50);
            Text(_grid, "SubType", nameof(UnitRow.SubType), 60);
            Text(_grid, "Specific", nameof(UnitRow.Specific), 60);
            string[] names = { "", "1 Chinese", "2 DPRK", "3 ROK", "4 Soviet", "5 US" };
            for (int s = 1; s <= 5; s++) Check(_grid, names[s], "S" + s, 72, $"Offered in the TE to teams whose equipment is {Producer.EquipmentNames[s]}");
            Text(_grid, "Korea", nameof(UnitRow.KoreaSides), 70);
            Text(_grid, "", nameof(UnitRow.Sides), 0);
            _grid.RowStyle = BackStyle();

            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            bar.Children.Add(new TextBlock { Text = "Find ", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_find); bar.Children.Add(_only);
            _find.TextChanged += (_, _) => Fill();
            _only.Click += (_, _) => Fill();
            bar.Children.Add(UnitText.Btn("Same as the Korea list", () =>
            {
                if (MessageBox.Show(_w, "Make this list the same as the Korea one?", _w.Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                _cur.Entries.Clear();
                _cur.Entries.AddRange(_ref.Entries);
                Dirty = true;
                Load(keepFiles: true);
            }));
            bar.Children.Add(UnitText.Btn("Remove units missing from the database", () =>
            {
                foreach (var r in _rows.Where(r => !r.InDb)) for (int s = 1; s <= 5; s++) _cur.Set(s, r.SubType, r.Specific, false);
                Dirty = true;
                Load(keepFiles: true);
            }));
            var legend = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6),
                Text = "A tick = the unit is offered in the Tactical Engagement to the teams with that equipment (Teams tab of the editor: Equipment). " +
                       "Green = as in the Korea, yellow = different from the Korea, red = not in this theater's database.",
            };
            var top = new StackPanel();
            top.Children.Add(bar); top.Children.Add(legend); top.Children.Add(_info);
            var root = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            root.Children.Add(_grid);
            return root;
        }

        public void Load(bool keepFiles = false)
        {
            string? cur = _w._ws.FindCampaignFile(_file), rf = _w._ws.FindReferenceFile(_file);
            if (!keepFiles)
            {
                try
                {
                    _ref = rf != null ? TeUnitsFile.Load(rf) : new TeUnitsFile();
                    _cur = cur != null ? TeUnitsFile.Load(cur) : rf != null ? TeUnitsFile.Load(rf) : new TeUnitsFile();
                }
                catch (Exception e) { _info.Text = _file + ": " + e.Message; return; }
            }
            _rows.Clear();
            var db = _w._db;
            var types = db.Ct.Where(kv => kv.Value.EntityType == 4 && kv.Value.Domain == _domain && kv.Value.Type == 1).ToList();
            var known = new HashSet<(int, int)>();
            foreach (var (ct, c) in types.OrderBy(kv => db.UcdName(kv.Key)).ThenBy(kv => kv.Value.SubType).ThenBy(kv => kv.Value.Specific))
            {
                if (!known.Add((c.SubType, c.Specific))) continue;
                Add(ct, db.UcdName(ct), string.Join(", ", db.UnitVehicles(ct).Take(4)), c.SubType, c.Specific, true);
            }
            foreach (var (s, sub, spec) in _cur.Entries.Concat(_ref.Entries))
                if (known.Add((sub, spec))) Add(-1, "(not in the database)", "", sub, spec, false);
            Fill();
            _info.Text = $"{_file}: {cur ?? rf ?? "(none: empty list)"} · {_cur.Entries.Count} lines · {_rows.Count(r => r.Sides != r.KoreaSides)} types different from the Korea · {_rows.Count(r => !r.InDb && r.Sides.Length > 0)} missing from the database";
        }

        private void Add(int ct, string name, string veh, int sub, int spec, bool inDb)
        {
            var r = new UnitRow
            {
                Ct = ct, Name = name, Vehicles = veh, SubType = sub, Specific = spec, InDb = inDb, File = _cur,
                KoreaSides = string.Join(",", Enumerable.Range(1, 5).Where(s => _ref.Has(s, sub, spec))),
            };
            r.Changed = () => Dirty = true;
            _rows.Add(r);
        }

        private void Fill()
        {
            string q = _find.Text.Trim();
            _grid.ItemsSource = _rows
                .Where(r => _only.IsChecked != true || r.Sides.Length > 0)
                .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Vehicles.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public string? SaveIfDirty()
        {
            if (!Dirty) return null;
            var p = _w._ws.SaveCampaignFile(_file, path => _cur.Save(path));
            Dirty = false;
            return p;
        }
    }

    // ───────────────────────── salvataggio

    /// <summary>The Save button: saves, says what was written and what comes next, then closes the window.</summary>
    private void SaveAndClose()
    {
        if (!_planesDirty && !_ground.Dirty && !_naval.Dirty)
        {
            MessageBox.Show(this, "Nothing changed: there is nothing to save.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Save()) return;
        string next = _ws.InstallCopyDir != null
            ? "Now press 'Install campaign files into BMS…' in the Campaign Editor's start window, then start BMS."
            : "BMS is not touched: copy them into the theater's Campaign folder to use them.";
        MessageBox.Show(this, _status.Text + "\n\n" + next, "TE lists saved", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private bool Save()
    {
        try
        {
            var saved = new List<string>();
            if (_planesDirty)
            {
                var f = new TePlanesFile { Header = _planes.Header };
                f.Entries.AddRange(_planes.Entries.Where(e => e.Type == 0).Take(1));
                if (f.Entries.Count == 0) f.Entries.AddRange(_planesRef.Entries.Where(e => e.Type == 0).Take(1));
                // ordine del file del teatro; le voci aggiunte in fondo
                var order = _planes.Entries.Select((e, i) => (e.Key, i)).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().i);
                foreach (var r in _planeRows.Where(r => r.InList).OrderBy(r => order.GetValueOrDefault(r.E.Key, int.MaxValue)))
                    f.Entries.Add(new TePlanesFile.Entry
                    {
                        Name = r.Name.Length > 0 ? r.Name : r.DbName, Type = r.Type, SubType = r.SubType, Specific = r.Specific,
                        UnitSubType = r.UnitSubType, Id = r.Id, Text = r.TextId, Icon = r.Icon,
                    });
                saved.Add(_ws.SaveCampaignFile("TePlanes.lst", p => f.Save(p)));
                _planesDirty = false;
            }
            if (_ground.SaveIfDirty() is { } g) saved.Add(g);
            if (_naval.SaveIfDirty() is { } n) saved.Add(n);
            _status.Text = saved.Count == 0 ? "Nothing changed." :
                "Saved: " + string.Join(", ", saved.Select(Path.GetFileName)) + " → " + _ws.EditsFolder +
                (_ws.InstallCopyDir != null ? " (and Output\\Install)." : ". BMS is not touched: copy them into the theater's Campaign folder to use them.");
            return true;
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
}
