using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>Testi comuni delle finestre delle unita' (come in Mission Commander).</summary>
internal static class UnitText
{
    public static string Ordinal(int n)
    {
        int a = Math.Abs(n) % 100, b = Math.Abs(n) % 10;
        string suf = a is 11 or 12 or 13 ? "th" : b == 1 ? "st" : b == 2 ? "nd" : b == 3 ? "rd" : "th";
        return n + suf;
    }

    /// <summary>Mezzi (o aerei) presenti: 2 bit per elemento nel roster.</summary>
    public static int Vehicles(int roster)
    {
        int n = 0;
        for (int i = 0; i < 16; i++) n += (roster >> (2 * i)) & 3;
        return n;
    }

    public static string Kind(UnitKind k) => k switch
    {
        UnitKind.Battalion => "Battalion", UnitKind.Brigade => "Brigade", UnitKind.Squadron => "Squadron", UnitKind.Flight => "Flight",
        UnitKind.Package => "Package", UnitKind.TaskForce => "Task force", _ => k.ToString(),
    };

    /// <summary>Nome come in Mission Commander: "SA-3 (S-125) - Air Defense", "F-16CM-40 - Fighter", "Armored - M1A1".</summary>
    public static string Name(CampaignDoc d, CampaignDb db, CampUnit u)
    {
        string ucd = db.UcdName(u.Type - 100);
        if (u.Kind == UnitKind.Brigade)
        {
            var first = u.Elements.Select(e => d.Unit(e)).OfType<CampUnit>().FirstOrDefault();
            string v = first != null ? db.UnitVehicles(first.Type - 100).FirstOrDefault() ?? "" : "";
            return v.Length > 0 ? $"{ucd} - {v}" : ucd;
        }
        string main = db.UnitVehicles(u.Type - 100).FirstOrDefault() ?? "";
        return main.Length > 0 ? $"{main} - {ucd}" : ucd;
    }

    public static string Title(CampaignDb db, CampUnit u) =>
        $"{Ordinal(u.NameId)} {db.UcdName(u.Type - 100)} {Kind(u.Kind)}";

    public static Brush LevelBrush(IssueLevel? l) => l switch
    {
        IssueLevel.Error => new SolidColorBrush(Color.FromRgb(255, 190, 190)),
        IssueLevel.Warning => new SolidColorBrush(Color.FromRgb(255, 205, 120)),
        _ => Brushes.Transparent,
    };

    public static TextBlock T(string s, double size = 12, FontWeight? w = null) =>
        new() { Text = s, FontSize = size, FontWeight = w ?? FontWeights.Normal, Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public static Grid Form(params (string Label, UIElement Field)[] rows)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].Label, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(l, i); g.Children.Add(l);
            Grid.SetRow(rows[i].Field, i); Grid.SetColumn(rows[i].Field, 1); g.Children.Add(rows[i].Field);
        }
        return g;
    }

    public static Button Btn(string text, Action click, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 6, 2), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    public static ComboBox Orders(byte selected)
    {
        var c = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        for (int i = 0; i <= 11; i++) c.Items.Add(CampUnit.OrdersName(i));
        c.SelectedIndex = selected <= 11 ? selected : -1;
        return c;
    }

    public static ComboBox Teams(CampaignDoc d, byte selected)
    {
        var c = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var t in d.Teams?.Teams ?? new List<CampTeam>())
            c.Items.Add(new ComboBoxItem { Content = $"{t.Index}  {t}", Tag = (byte)t.Index });
        c.SelectedIndex = selected < c.Items.Count ? selected : -1;
        return c;
    }

    /// <summary>Obiettivi da scegliere: quello selezionato nell'editor, poi i piu' vicini all'unita'.</summary>
    public static ComboBox Objectives(CampaignDoc d, CampaignDb db, CampUnit u, CampObjective? editorSel)
    {
        var c = new ComboBox { Margin = new Thickness(0, 2, 0, 2), IsTextSearchEnabled = true };
        var cur = d.Obj(u.AssignedObj);
        var list = d.Objectives.OrderBy(o => Math.Pow(o.X - u.X, 2) + Math.Pow(o.Y - u.Y, 2)).Take(250).ToList();
        if (editorSel != null) { list.Remove(editorSel); list.Insert(0, editorSel); }
        if (cur != null && !list.Contains(cur)) list.Insert(0, cur);
        foreach (var o in list)
        {
            double km = Math.Sqrt(Math.Pow(o.X - u.X, 2) + Math.Pow(o.Y - u.Y, 2));
            string tag = o == editorSel ? "  ← selected in the editor" : "";
            c.Items.Add(new ComboBoxItem { Content = $"{o.Name} ({TerrainTool.Core.Editor.EditorData.TypeName(db.ObjectiveType(o.Type))}, {CampaignValidator.TeamName(d, o.Owner)}, {km:0} km){tag}", Tag = o });
        }
        c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == cur);
        return c;
    }

    public static Expander FlagsBox(int flags, Action<int> changed)
    {
        var wp = new WrapPanel { Margin = new Thickness(4) };
        int cur = flags;
        for (int i = 0; i < 32; i++)
        {
            int bit = 1 << i;
            var cb = new CheckBox { Content = UnitEditor.FlagNames[i], IsChecked = (flags & bit) != 0, Width = 150, Margin = new Thickness(0, 2, 0, 2) };
            cb.Click += (_, _) => { cur = cb.IsChecked == true ? cur | bit : cur & ~bit; changed(cur); };
            wp.Children.Add(cb);
        }
        var ex = new Expander { Header = $"Unit flags: {flags} (0x{flags:X})", Content = wp, Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "The number Mission Commander shows in grey in Advanced Options (e.g. 160 = Parent 32 + Human Cont 128)" };
        foreach (CheckBox cb in wp.Children) cb.Click += (_, _) => ex.Header = $"Unit flags: {cur} (0x{cur:X})";
        return ex;
    }
}

/// <summary>
/// Elenco delle unita' come la scheda Units di Mission Commander: tutte, aeree (squadroni, pacchetti, voli), di terra (brigate,
/// battaglioni), task force; filtri per squadra, tipo, "solo brigate"; righe colorate dai problemi (rosso errore, arancione avviso).
/// Doppio clic: scheda dell'unita'.
/// </summary>
public sealed class UnitListWindow : Window
{
    private readonly CampaignDoc _doc;
    private readonly CampaignDb _db;
    private readonly UnitEditor _ed;
    private readonly SquadronEditor _se;
    private readonly Func<CampObjective?> _selObj;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.All, VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)), HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FrozenColumnCount = 2,
    };
    private readonly ComboBox _team = new() { Width = 170, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _type = new() { Width = 220, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _onlyProblems = new() { Content = "Only units with problems", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _search = new() { Width = 160, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Name, number, objective or ID" };
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private string _filter = "Land";
    public event Action<Action, CampUnit?>? Applied;
    public event Action<CampUnit>? ShowOnMap;
    public event Action<UnitKind>? AddUnit;

    public sealed class Row
    {
        public CampUnit U { get; init; } = null!;
        public int Nr { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Number { get; init; } = "";
        public string Team { get; init; } = "";
        public string Home { get; init; } = "";
        public string Elements { get; init; } = "";
        public string Orders { get; init; } = "";
        public int Division { get; init; }
        public string Id { get; init; } = "";
        public int CampId { get; init; }
        public string Inactive { get; init; } = "";
        public string NoPlan { get; init; } = "";
        public string Human { get; init; } = "";
        public string Problems { get; init; } = "";
        public Brush Back { get; init; } = Brushes.Transparent;
    }

    public UnitListWindow(CampaignDoc doc, CampaignDb db, Func<CampObjective?> selObj)
    {
        _doc = doc; _db = db; _selObj = selObj; _ed = new UnitEditor(doc, db); _se = new SquadronEditor(doc, db);
        Title = "Units – " + System.IO.Path.GetFileName(doc.Path);
        Width = 1450; Height = 900;

        var left = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
        RadioButton F(string text, string key, double indent = 0)
        {
            var r = new RadioButton { Content = text, GroupName = "unitfilter", Margin = new Thickness(indent, 3, 0, 3), IsChecked = key == _filter };
            r.Checked += (_, _) => { _filter = key; FillTypes(); Fill(); };
            left.Children.Add(r);
            return r;
        }
        F("All units", "All"); F("All air", "Air"); F("Squadrons", "Squadron", 14); F("Packages", "Package", 14); F("Flights", "Flight", 14);
        F("All land", "Land"); F("Brigades", "Brigade", 14); F("Brigades + their battalions", "Parents", 14); F("Battalions", "Battalion", 14); F("Task forces", "TaskForce");
        left.Children.Add(new TextBlock { Text = "Add unit", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
        left.Children.Add(UnitText.Btn("Add squadron…", AddSquadron, "Aircraft, team and base (airbase, airstrip, army base or carrier), as Mission Commander"));
        left.Children.Add(UnitText.Btn("Add battalion…", () => AddUnit?.Invoke(UnitKind.Battalion), "Opens the battalions & brigades table"));
        left.Children.Add(UnitText.Btn("Add brigade…", () => AddUnit?.Invoke(UnitKind.Brigade), "Opens the battalions & brigades table"));
        left.Children.Add(UnitText.Btn("Flyable aircraft…", FlyableAircraft, "Which aircraft the players can fly: 'Human Cont' on all the squadrons of the ticked types (F-16 squadrons are always flyable)"));
        left.Children.Add(new TextBlock { Text = "Repairs", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
        left.Children.Add(UnitText.Btn("Fix support elements", FixSupport,
            "Air defense and supply battalions listed among the battalions of a brigade (old campaigns): as BMS 4.37+ and Korea want, they leave the brigade " +
            "and stay attached to it as support (cargo), with No Plan"));
        left.Children.Add(UnitText.Btn("Battalions to brigade position…", BattalionsToBrigades,
            "As in the Korea campaigns at the start (and Mission Commander 'Move all Battalions to there Brigade location'): every battalion of a brigade " +
            "goes to the brigade's place, with its destination and assigned objective"));
        left.Children.Add(_count);
        left.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11,
            Text = "Red = error (missing battalion, more than 5 battalions, no objective, airbase that does not exist). Orange = warning (air defense or supply battalion inside a brigade, " +
                   "battalion of another team, no No Plan on air defense). Double-click = unit window.",
        });

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void L(string s) => top.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        L("Team"); top.Children.Add(_team); L("Type"); top.Children.Add(_type); L("Find"); top.Children.Add(_search); top.Children.Add(_onlyProblems);
        top.Children.Add(UnitText.Btn("Show on map", () => { if (_grid.SelectedItem is Row r) ShowOnMap?.Invoke(r.U); }));
        top.Children.Add(UnitText.Btn("Open…", OpenSelected));
        top.Children.Add(UnitText.Btn("3D view…", () =>
        {
            if (_grid.SelectedItem is Row r) ModelViewWindow.Open(this, UnitText.Title(_db, r.U), ModelViewWindow.VehiclesOf(_doc, _db, r.U));
            else MessageBox.Show(this, "Select a unit in the list first.", Title);
        }, "The vehicles of the selected unit in 3D with the BMS textures"));

        _team.Items.Add(new ComboBoxItem { Content = "All teams", Tag = -1 });
        foreach (var t in doc.Teams?.Teams ?? new List<CampTeam>())
            if (doc.Units?.Units.Any(u => u.Owner == t.Index) == true) _team.Items.Add(new ComboBoxItem { Content = $"{t.Index}  {t}", Tag = t.Index });
        _team.SelectedIndex = 0;

        void C(string h, string p, double w)
        {
            var c = new DataGridTextColumn { Header = h, Binding = new Binding(p), SortMemberPath = p, Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) };
            _grid.Columns.Add(c);
        }
        C("Nr", nameof(Row.Nr), 45); C("Name", nameof(Row.Name), 230); C("Type", nameof(Row.Kind), 80); C("Number", nameof(Row.Number), 65);
        C("Team", nameof(Row.Team), 110); C("Home / objective", nameof(Row.Home), 200); C("Elements", nameof(Row.Elements), 90);
        C("Orders", nameof(Row.Orders), 85); C("Div", nameof(Row.Division), 40); C("ID", nameof(Row.Id), 70); C("CampId", nameof(Row.CampId), 60);
        C("Inactive", nameof(Row.Inactive), 55); C("No Plan", nameof(Row.NoPlan), 55); C("Players", nameof(Row.Human), 55); C("Problems", nameof(Row.Problems), 600);
        var rs = new Style(typeof(DataGridRow));
        rs.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(Row.Back))));
        _grid.RowStyle = rs;

        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); right.Children.Add(top);
        DockPanel.SetDock(_summary, Dock.Top); right.Children.Add(_summary);
        right.Children.Add(_grid);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(left, Dock.Left); root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        _team.SelectionChanged += (_, _) => Fill();
        _type.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _onlyProblems.Click += (_, _) => Fill();
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        Loaded += (_, _) => { FillTypes(); Fill(); };
    }

    private bool Match(CampUnit u) => _filter switch
    {
        "All" => true, "Air" => u.IsAir, "Land" => u.IsGround, "Parents" => u.IsGround,
        "Squadron" => u.Kind == UnitKind.Squadron, "Package" => u.Kind == UnitKind.Package, "Flight" => u.Kind == UnitKind.Flight,
        "Brigade" => u.Kind == UnitKind.Brigade, "Battalion" => u.Kind == UnitKind.Battalion, "TaskForce" => u.Kind == UnitKind.TaskForce, _ => true,
    };

    private void FillTypes()
    {
        var keep = (_type.SelectedItem as ComboBoxItem)?.Tag as string;
        _type.Items.Clear();
        _type.Items.Add(new ComboBoxItem { Content = "All types", Tag = "" });
        foreach (var n in _doc.Units?.Units.Where(Match).Select(u => _db.UcdName(u.Type - 100)).Where(n => n.Length > 0).Distinct().OrderBy(n => n) ?? Enumerable.Empty<string>())
            _type.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        _type.SelectedItem = _type.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == keep) ?? _type.Items[0];
    }

    public void Fill()
    {
        if (_doc.Units == null) return;
        int team = _team.SelectedItem is ComboBoxItem { Tag: int t } ? t : -1;
        string type = (_type.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        string q = _search.Text.Trim();
        var units = _doc.Units.Units;
        var rows = new List<Row>();
        IEnumerable<CampUnit> seq = units.Where(Match);
        if (_filter == "Parents")
        {
            // brigata seguita dai suoi battaglioni e dagli appoggi, poi i battaglioni indipendenti
            var ordered = new List<CampUnit>();
            foreach (var b in units.Where(u => u.Kind == UnitKind.Brigade))
            {
                ordered.Add(b);
                ordered.AddRange(b.Elements.Select(e => _doc.Unit(e)).OfType<CampUnit>());
                ordered.AddRange(units.Where(x => x.Kind == UnitKind.Battalion && x.Cargo == b.Id && !b.Elements.Contains(x.Id)));
            }
            ordered.AddRange(units.Where(x => x.Kind == UnitKind.Battalion && !ordered.Contains(x)));
            seq = ordered;
        }
        int nr = 0, nErr = 0, nWarn = 0;
        foreach (var u in seq)
        {
            nr = units.IndexOf(u);
            if (team >= 0 && u.Owner != team) continue;
            if (type.Length > 0 && _db.UcdName(u.Type - 100) != type) continue;
            var probs = u.IsGround ? _ed.Problems(u) : u.Kind == UnitKind.Squadron ? _se.Problems(u) : new List<(IssueLevel Level, string Text)>();
            IssueLevel? lev = probs.Count == 0 ? null : probs.Any(p => p.Level == IssueLevel.Error) ? IssueLevel.Error : IssueLevel.Warning;
            if (lev == IssueLevel.Error) nErr++; else if (lev == IssueLevel.Warning) nWarn++;
            if (_onlyProblems.IsChecked == true && lev == null) continue;
            string name = UnitText.Name(_doc, _db, u);
            string home = u.Kind == UnitKind.Squadron ? _doc.Obj(u.Airbase)?.Name ?? (_doc.Unit(u.Airbase) is { } tf ? $"TF {tf.NameId}" : "")
                        : u.IsGround || u.Kind == UnitKind.TaskForce ? _doc.Obj(u.AssignedObj)?.Name ?? "" : "";
            if (_filter == "Parents" && u.Kind == UnitKind.Battalion)
                name = (_doc.Unit(u.Parent) is { Kind: UnitKind.Brigade } ? "   └ " : _doc.Unit(u.Cargo) is { Kind: UnitKind.Brigade } ? "   ┄ support: " : "") + name;
            string elements = u.Kind switch
            {
                UnitKind.Brigade => $"{u.Elements.Count} Battalions",
                UnitKind.Battalion => $"{UnitText.Vehicles(u.Roster)} Vehicles",
                UnitKind.Squadron => $"{UnitText.Vehicles(u.Roster)} AC",
                UnitKind.Flight => $"{UnitText.Vehicles(u.Roster)} AC",
                UnitKind.Package => $"{u.Elements.Count} Flights",
                _ => "",
            };
            if (q.Length > 0 && !(name.Contains(q, StringComparison.OrdinalIgnoreCase) || home.Contains(q, StringComparison.OrdinalIgnoreCase)
                                  || u.NameId.ToString(CultureInfo.InvariantCulture) == q || u.Id.Num.ToString(CultureInfo.InvariantCulture) == q)) continue;
            rows.Add(new Row
            {
                U = u, Nr = nr, Name = name, Kind = UnitText.Kind(u.Kind), Number = UnitText.Ordinal(u.NameId), Team = CampaignValidator.TeamName(_doc, u.Owner),
                Home = home, Elements = elements, Orders = u.IsGround || u.Kind == UnitKind.TaskForce ? CampUnit.OrdersName(u.Orders) : "",
                Division = u.IsGround ? u.Division : 0, Id = u.Id.ToString(), CampId = u.CampId,
                Inactive = (u.UnitFlags & UnitEditor.FInactive) != 0 ? "Yes" : "", NoPlan = (u.UnitFlags & UnitEditor.FNoPlan) != 0 ? "Yes" : "",
                Human = u.Kind == UnitKind.Squadron && SquadronEditor.IsHuman(u) ? "Yes" : "",
                Problems = string.Join("; ", probs.Select(p => p.Text)), Back = UnitText.LevelBrush(lev),
            });
        }
        var sel = (_grid.SelectedItem as Row)?.U;
        _grid.ItemsSource = rows;
        if (sel != null) _grid.SelectedItem = rows.FirstOrDefault(r => r.U == sel);
        _count.Text = $"Units in list: {rows.Count}";
        _summary.Text = $"{nErr} units with errors (red), {nWarn} with warnings (orange) in this view.";
    }

    private void OpenSelected()
    {
        if (_grid.SelectedItem is not Row r) return;
        Open(r.U);
    }

    public void Open(CampUnit u)
    {
        Window? w = u.Kind switch
        {
            UnitKind.Battalion => new BattalionWindow(_doc, _db, u, _selObj()),
            UnitKind.Brigade => new BrigadeWindow(_doc, _db, u, _selObj()),
            UnitKind.Squadron => new SquadronWindow(_doc, _db, u),
            _ => null,
        };
        if (w == null) { ShowOnMap?.Invoke(u); return; }
        w.Owner = this;
        switch (w)
        {
            case BattalionWindow bw: bw.Applied += (undo, unit) => { Applied?.Invoke(undo, unit); Fill(); }; bw.OpenUnit += Open; break;
            case BrigadeWindow brw: brw.Applied += (undo, unit) => { Applied?.Invoke(undo, unit); Fill(); }; brw.OpenUnit += Open; break;
            case SquadronWindow sw: sw.Applied += (undo, unit) => { Applied?.Invoke(undo, unit); Fill(); }; break;
        }
        w.Show();
    }

    /// <summary>Tipi di aereo degli squadroni del file, con la spunta "i giocatori lo possono volare" (Human Cont su tutti i loro squadroni).</summary>
    private void FlyableAircraft()
    {
        var sqs = _doc.Units!.Units.Where(u => u.Kind == UnitKind.Squadron).ToList();
        var groups = sqs.GroupBy(u => _db.UnitVehicles(u.Type - 100).FirstOrDefault() ?? "?").OrderBy(g => g.Key).ToList();
        var w = new Window { Title = "Flyable aircraft – " + System.IO.Path.GetFileName(_doc.Path), Width = 560, Height = 700, Owner = this };
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, List<CampUnit> Units)>();
        foreach (var g in groups)
        {
            var units = g.ToList();
            int on = units.Count(SquadronEditor.IsHuman);
            bool f16 = g.Key.StartsWith("F-16", StringComparison.OrdinalIgnoreCase);
            var cb = new CheckBox
            {
                Content = $"{g.Key}   ·   {units.Count} squadrons ({string.Join(", ", units.Select(u => CampaignValidator.TeamName(_doc, u.Owner)).Distinct())})" +
                          (on > 0 && on < units.Count ? $"   ·   now {on} of {units.Count}" : "") + (f16 ? "   ·   F-16: always flyable" : ""),
                IsChecked = on == units.Count ? true : on == 0 ? false : null, IsThreeState = on > 0 && on < units.Count, Margin = new Thickness(2),
            };
            boxes.Add((cb, units));
            list.Children.Add(cb);
        }
        var apply = UnitText.Btn("Apply", () =>
        {
            var undos = new List<Action>(); int n = 0;
            foreach (var (cb, units) in boxes)
            {
                if (cb.IsChecked == null) continue;
                var (u, c) = _se.SetHuman(units, cb.IsChecked == true);
                if (u != null) { undos.Add(u); n += c; }
            }
            if (n > 0) Applied?.Invoke(() => { for (int i = undos.Count - 1; i >= 0; i--) undos[i](); }, null);
            Fill();
            MessageBox.Show(w, $"{n} squadrons changed. Ctrl+Z in the editor undoes it.", w.Title);
            w.Close();
        });
        apply.FontWeight = FontWeights.SemiBold;
        var root = new DockPanel { Margin = new Thickness(10) };
        var top = UnitText.T("Ticked = 'Human Cont' on all the squadrons of that aircraft, so the players can choose and fly them. " +
                             "Korea has it only on the U.S. F-15C at Gunsan; the F-16 squadrons are always flyable (BMS's own aircraft). Grey = mixed, left as it is.");
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(apply, Dock.Bottom); apply.HorizontalAlignment = HorizontalAlignment.Right; root.Children.Add(apply);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.Content = root;
        w.ShowDialog();
    }

    private void AddSquadron()
    {
        var w = new AddSquadronWindow(_doc, _db, _selObj()) { Owner = this };
        w.Applied += (undo, unit) => { Applied?.Invoke(undo, unit); Fill(); };
        w.ShowDialog();
    }

    private void BattalionsToBrigades()
    {
        var all = _doc.Units?.Units ?? new List<CampUnit>();
        int off = all.Where(b => b.Kind == UnitKind.Brigade).Sum(br => br.Elements.Select(e => _doc.Unit(e)).OfType<CampUnit>().Count(x => x.X != br.X || x.Y != br.Y));
        var w = new Window { Title = "Battalions to brigade position", Width = 520, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var team = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        team.Items.Add(new ComboBoxItem { Content = "All teams", Tag = null });
        foreach (var t in _doc.Teams?.Teams ?? new List<CampTeam>())
            if (all.Any(u => u.Kind == UnitKind.Brigade && u.Owner == t.Index)) team.Items.Add(new ComboBoxItem { Content = $"{t.Index}  {t}", Tag = t.Index });
        team.SelectedIndex = 0;
        var support = new CheckBox { Content = "Also the support battalions of the brigade (air defense, supply attached with cargo)", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
        var aobj = new CheckBox { Content = "Also the brigade's assigned objective", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
        bool ok = false;
        var go = UnitText.Btn("Move", () => { ok = true; w.Close(); });
        go.FontWeight = FontWeights.SemiBold;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(UnitText.Btn("Cancel", w.Close)); buttons.Children.Add(go);
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            $"In the Korea campaigns the battalions start on their brigade (Save0: 194 of 198). In this file {off} battalions are away from their brigade.\n" +
            "Each battalion gets the brigade's place and destination (its planned route is cleared). Ctrl+Z in the editor undoes it." });
        sp.Children.Add(new TextBlock { Text = "Team", Margin = new Thickness(0, 10, 0, 0) });
        sp.Children.Add(team); sp.Children.Add(support); sp.Children.Add(aobj); sp.Children.Add(buttons);
        w.Content = sp;
        w.ShowDialog();
        if (!ok) return;
        byte? tm = (team.SelectedItem as ComboBoxItem)?.Tag is byte b ? b : null;
        var (undo, moved, brigs) = _ed.AllBattalionsToBrigades(tm, support.IsChecked == true, aobj.IsChecked == true);
        if (undo == null) { MessageBox.Show(this, "Every battalion is already on its brigade.", Title); return; }
        Applied?.Invoke(undo, null);
        Fill();
        MessageBox.Show(this, $"{moved} battalions of {brigs} brigades moved to their brigade.\nCtrl+Z in the editor undoes it.", Title);
    }

    private void FixSupport()
    {
        var (n, undo) = _ed.FixSupportElements();
        if (n == 0) { MessageBox.Show(this, "No air defense or supply battalion is listed inside a brigade.", Title); return; }
        Applied?.Invoke(undo!, null);
        Fill();
        MessageBox.Show(this, $"{n} air defense / supply battalions are no longer battalions of their brigade: they stay attached to it as support (cargo) with No Plan, as in BMS 4.37+ and Korea.\nCtrl+Z in the editor undoes it.", Title);
    }
}

/// <summary>Scheda del battaglione (come "Battalion Unit" di Mission Commander). Apply scrive tutto insieme (un solo Ctrl+Z).</summary>
public sealed class BattalionWindow : Window
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly UnitEditor _ed;
    private readonly CampUnit _u;
    private int _flags, _roster;
    private readonly DataGrid _rosterGrid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 380 };
    public event Action<Action, CampUnit?>? Applied;
    public event Action<CampUnit>? OpenUnit;

    public sealed class RosterRow
    {
        public int Nr { get; init; }
        public string Rank { get; init; } = "";
        public string Vehicle { get; init; } = "";
        public int Act { get; set; }
        public int Max { get; init; }
    }

    public BattalionWindow(CampaignDoc d, CampaignDb db, CampUnit u, CampObjective? editorSel)
    {
        _d = d; _db = db; _u = u; _ed = new UnitEditor(d, db);
        _flags = u.UnitFlags; _roster = u.Roster;
        Title = $"Battalion – Bn {u.NameId} ({u.Id})";
        Width = 1100; Height = 860;

        var name = new TextBox { Text = u.NameId.ToString(CultureInfo.InvariantCulture) };
        var div = new TextBox { Text = u.Division.ToString(CultureInfo.InvariantCulture) };
        var morale = new TextBox { Text = u.Morale.ToString(CultureInfo.InvariantCulture) };
        var supply = new TextBox { Text = u.Supply.ToString(CultureInfo.InvariantCulture) };
        var fatigue = new TextBox { Text = u.Fatigue.ToString(CultureInfo.InvariantCulture) };
        var roster = new TextBox { Text = u.Roster.ToString(CultureInfo.InvariantCulture) };
        int max = db.FullRoster(u.Type - 100);
        var team = UnitText.Teams(d, u.Owner);
        var orders = UnitText.Orders(u.Orders);
        var hq = new CheckBox { Content = "Set by HQ", IsChecked = (u.UnitFlags & UnitEditor.FNotByHq) == 0, ToolTip = "Orders given by the campaign AI (flag 0x80 'Human Cont' off). Off = the orders stay as you set them." };
        var aobj = UnitText.Objectives(d, db, u, editorSel);
        var x = new TextBox { Text = u.X.ToString(CultureInfo.InvariantCulture) };
        var y = new TextBox { Text = u.Y.ToString(CultureInfo.InvariantCulture) };

        var head = new StackPanel();
        head.Children.Add(UnitText.T(UnitText.Title(db, u), 20, FontWeights.SemiBold));
        head.Children.Add(UnitText.T($"Type: {db.UnitVehicles(u.Type - 100).FirstOrDefault()} – {db.UcdName(u.Type - 100)} (CT: {u.Type - 100})" +
                                     (_ed.IsAirDefense(u) ? "  ·  air defense" : _ed.IsSupply(u) ? "  ·  supply" : ""), 13));

        // brigata / appoggio
        var brPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var br = d.Unit(u.Parent);
        var sup = d.Unit(u.Cargo);
        brPanel.Children.Add(UnitText.T("Brigade:", 13, FontWeights.SemiBold));
        if (br is { Kind: UnitKind.Brigade }) brPanel.Children.Add(UnitText.Btn($"{UnitText.Title(db, br)} (div {br.Division})", () => OpenUnit?.Invoke(br)));
        else brPanel.Children.Add(UnitText.T("No brigade"));
        if (sup is { Kind: UnitKind.Brigade }) { brPanel.Children.Add(UnitText.T("Support element of:")); brPanel.Children.Add(UnitText.Btn($"{UnitText.Title(db, sup)}", () => OpenUnit?.Invoke(sup))); }
        var move = new ComboBox { Margin = new Thickness(0, 2, 0, 2), ToolTip = "Put the battalion in another brigade of its team, or make it independent (applied now)" };
        move.Items.Add(new ComboBoxItem { Content = "(independent)" });
        foreach (var b in d.Units!.Units.Where(z => z.Kind == UnitKind.Brigade && z.Owner == u.Owner).OrderBy(z => z.Division).ThenBy(z => z.NameId))
            move.Items.Add(new ComboBoxItem { Content = $"{UnitText.Title(db, b)} – div {b.Division}, {b.Elements.Count} bn, {d.Obj(b.AssignedObj)?.Name ?? "no objective"}", Tag = b });
        move.SelectedItem = move.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == br) ?? move.Items[0];
        move.SelectionChanged += (_, _) =>
        {
            var target = (move.SelectedItem as ComboBoxItem)?.Tag as CampUnit;
            if (target == br) return;
            var (undo, err) = _ed.SetBrigade(u, target);
            if (err != null) { MessageBox.Show(this, err, Title); move.SelectedItem = move.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == br) ?? move.Items[0]; return; }
            Applied?.Invoke(undo!, u); Close();
        };
        brPanel.Children.Add(UnitText.Form(("Move to", move)));

        // cambio tipo (prima i tipi con lo stesso ruolo)
        var types = UnitEditor.Types(db, d).Where(t => t.Kind == UnitKind.Battalion).ToList();
        var myRole = db.UnitVehicleCts(u.Type - 100).Select(db.RoleOf).FirstOrDefault();
        var typeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 2), IsTextSearchEnabled = true, ToolTip = "Types with the same role first (SAM for a SAM battalion, tanks for tanks…)" };
        foreach (var t in types.OrderBy(t => t.Role == myRole ? 0 : 1).ThenBy(t => t.Name).ThenBy(t => t.MainVehicle))
            typeBox.Items.Add(new ComboBoxItem { Content = $"{t.Name} – {t.MainVehicle} [{t.RoleText}{(t.ProducerText.Length > 0 ? ", " + t.ProducerText : "")}] #{t.Ct}", Tag = t.Ct });
        typeBox.SelectedItem = typeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == u.Type - 100);
        var changeType = UnitText.Btn("Change battalion type", () =>
        {
            if (typeBox.SelectedItem is not ComboBoxItem { Tag: int ct } || ct == u.Type - 100) return;
            var (undo, err) = _ed.ChangeType(u, ct);
            if (err != null) { MessageBox.Show(this, err, Title); return; }
            Applied?.Invoke(undo!, u); Close();
        }, "Changes the type now, with the full roster of the new type");

        var left = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(UnitText.Form(("Team", team)));
        left.Children.Add(brPanel);
        left.Children.Add(new TextBlock { Text = "Type", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        left.Children.Add(typeBox); left.Children.Add(changeType);

        var maxB = UnitText.Btn($"Max: {max}", () => { roster.Text = max.ToString(CultureInfo.InvariantCulture); }, "Full roster of this unit type");
        var change = new GroupBox { Header = "Change", Padding = new Thickness(6),
            Content = new StackPanel { Children = { UnitText.Form(("Name (number)", name), ("Division", div), ("Morale", morale), ("Supply", supply), ("Fatigue", fatigue), ("Roster", roster)), maxB } } };
        var place = UnitText.Btn("Place battalion at obj", () =>
        {
            if ((aobj.SelectedItem as ComboBoxItem)?.Tag is not CampObjective o) return;
            var (undo, err) = _ed.PlaceAt(u, o);
            if (err != null) { MessageBox.Show(this, err, Title); return; }
            Applied?.Invoke(undo!, u); Close();
        }, "Battalion on the chosen objective (also as assigned objective), route removed; air defense: Assigned + Ordered");
        var ordBox = new GroupBox { Header = "Orders", Padding = new Thickness(6),
            Content = new StackPanel { Children = { hq, UnitText.Form(("Orders", orders), ("Assigned obj", aobj)), place,
                UnitText.T(u.NumWaypoints > 0 ? $"Route: {u.NumWaypoints} waypoints (removed by Place)" : "No route", 11) } } };
        var (lat, lon) = (0.0, 0.0);
        var info = new GroupBox { Header = "Information", Padding = new Thickness(6),
            Content = new StackPanel { Children = { UnitText.Form(("X (km)", x), ("Y (km)", y)), UnitText.T($"VU {u.Id}   ·   CampId {u.CampId}   ·   spotted: see Advanced", 11) } } };
        _ = lat; _ = lon;
        var mid = new StackPanel();
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition()); row1.ColumnDefinitions.Add(new ColumnDefinition()); row1.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(ordBox, 1); Grid.SetColumn(info, 2);
        row1.Children.Add(change); row1.Children.Add(ordBox); row1.Children.Add(info);
        mid.Children.Add(row1);

        // organico per elemento
        _rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Nr", Binding = new Binding(nameof(RosterRow.Nr)), IsReadOnly = true, Width = 40 });
        _rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Rank", Binding = new Binding(nameof(RosterRow.Rank)), IsReadOnly = true, Width = 120 });
        _rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(RosterRow.Vehicle)), IsReadOnly = true, Width = 200 });
        _rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Act", Binding = new Binding(nameof(RosterRow.Act)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 50 });
        _rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Max", Binding = new Binding(nameof(RosterRow.Max)), IsReadOnly = true, Width = 50 });
        void FillRoster()
        {
            var ucd = db.UcdOf(u.Type - 100);
            var rows = new List<RosterRow>();
            for (int i = 0; i < 16; i++)
            {
                int v = ucd?.Vehicles[i] ?? 0;
                string vn = v > 0 ? db.VehicleName(v) : "";
                rows.Add(new RosterRow { Nr = i, Rank = vn.Length == 0 ? "" : i == 0 ? "Battalion Cmdr" : i == 4 ? "Company Cmdr" : "Platoon Cmdr", Vehicle = vn,
                                         Act = (_roster >> (2 * i)) & 3, Max = Math.Min(3, ucd?.Elements?[i] ?? 0) });
            }
            _rosterGrid.ItemsSource = rows;
        }
        FillRoster();
        _rosterGrid.CellEditEnding += (_, e) =>
        {
            if (e.Row.Item is not RosterRow r || e.EditingElement is not TextBox tb || !int.TryParse(tb.Text, out int v)) return;
            v = Math.Clamp(v, 0, 3);
            _roster = (_roster & ~(3 << (2 * r.Nr))) | (v << (2 * r.Nr));
            roster.Text = _roster.ToString(CultureInfo.InvariantCulture);
        };
        roster.LostFocus += (_, _) => { if (int.TryParse(roster.Text, out int v)) { _roster = v; FillRoster(); } };
        var rosterHead = UnitText.T($"Roster   ·   Total vehicles: {UnitText.Vehicles(u.Roster)} of {UnitText.Vehicles(max)}", 13, FontWeights.SemiBold);
        mid.Children.Add(rosterHead);
        mid.Children.Add(_rosterGrid);
        mid.Children.Add(UnitText.FlagsBox(u.UnitFlags, f => _flags = f));

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var del = UnitText.Btn("Delete battalion", () =>
        {
            if (MessageBox.Show(this, "Delete this battalion?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var (undo, err) = _ed.Delete(u);
            if (err != null) { MessageBox.Show(this, err, Title); return; }
            Applied?.Invoke(undo!, null); Close();
        });
        del.Foreground = Brushes.DarkRed;
        var apply = UnitText.Btn("Apply", () =>
        {
            if (!short.TryParse(name.Text, out short nm) || !short.TryParse(div.Text, out short dv) || !byte.TryParse(morale.Text, out byte mo) ||
                !byte.TryParse(supply.Text, out byte su) || !byte.TryParse(fatigue.Text, out byte fa) || !int.TryParse(roster.Text, out int ro) ||
                !int.TryParse(x.Text, out int xx) || !int.TryParse(y.Text, out int yy))
            { MessageBox.Show(this, "Check the numbers (morale, supply, fatigue 0-255).", Title); return; }
            int flags = hq.IsChecked == true ? _flags & ~UnitEditor.FNotByHq : _flags | UnitEditor.FNotByHq;
            var e = new UnitEditor.Edit
            {
                Owner = team.SelectedItem is ComboBoxItem { Tag: byte t } ? t : u.Owner, Orders = (byte)Math.Max(0, orders.SelectedIndex),
                NameId = nm, Division = dv, Morale = mo, Supply = su, Fatigue = fa, Roster = ro, Flags = flags, X = xx, Y = yy,
                AssignedObj = (aobj.SelectedItem as ComboBoxItem)?.Tag is CampObjective o ? o.Id : u.AssignedObj,
            };
            var (undo, err) = _ed.Apply(u, e);
            if (err != null) { MessageBox.Show(this, err, Title); return; }
            Applied?.Invoke(undo!, u); Close();
        });
        apply.FontWeight = FontWeights.SemiBold;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(UnitText.Btn("3D view…", () => ModelViewWindow.Open(this, UnitText.Title(db, u), ModelViewWindow.VehiclesOf(d, db, u)), "The vehicles of this unit in 3D with the BMS textures"));
        right.Children.Add(UnitText.Btn("Cancel", Close)); right.Children.Add(apply);
        DockPanel.SetDock(del, Dock.Left); bottom.Children.Add(del); bottom.Children.Add(right);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); body.Children.Add(left);
        body.Children.Add(new ScrollViewer { Content = mid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;
    }
}

/// <summary>Scheda della brigata (come "Brigade Unit" di Mission Commander): battaglioni, appoggi, comandi sui battaglioni.</summary>
public sealed class BrigadeWindow : Window
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly UnitEditor _ed;
    private readonly CampUnit _u;
    private int _flags;
    public event Action<Action, CampUnit?>? Applied;
    public event Action<CampUnit>? OpenUnit;

    public sealed class ElemRow
    {
        public CampUnit U { get; init; } = null!;
        public int Nr { get; init; }
        public string Name { get; init; } = "";
        public int Ct { get; init; }
        public int Vehicles { get; init; }
        public string Id { get; init; } = "";
        public int Division { get; init; }
        public string Team { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public string Obj { get; init; } = "";
        public string Note { get; init; } = "";
        public Brush Back { get; init; } = Brushes.Transparent;
    }

    public BrigadeWindow(CampaignDoc d, CampaignDb db, CampUnit u, CampObjective? editorSel)
    {
        _d = d; _db = db; _u = u; _ed = new UnitEditor(d, db); _flags = u.UnitFlags;
        Title = $"Brigade – Bde {u.NameId} ({u.Id})";
        Width = 1250; Height = 900;

        var head = new StackPanel();
        head.Children.Add(UnitText.T(UnitText.Title(db, u), 20, FontWeights.SemiBold));
        head.Children.Add(UnitText.T($"Division: {UnitText.Ordinal(u.Division)}   ·   type {db.UcdName(u.Type - 100)} (CT {u.Type - 100})   ·   VU {u.Id}   ·   CampId {u.CampId}", 13));

        var team = UnitText.Teams(d, u.Owner);
        var name = new TextBox { Text = u.NameId.ToString(CultureInfo.InvariantCulture) };
        var div = new TextBox { Text = u.Division.ToString(CultureInfo.InvariantCulture), ToolTip = "Applied to its battalions too" };
        var orders = UnitText.Orders(u.Orders);
        var aobj = UnitText.Objectives(d, db, u, editorSel);
        var x = new TextBox { Text = u.X.ToString(CultureInfo.InvariantCulture) };
        var y = new TextBox { Text = u.Y.ToString(CultureInfo.InvariantCulture) };
        var types = UnitEditor.Types(db, d).Where(t => t.Kind == UnitKind.Brigade).ToList();
        var typeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var t in types.OrderBy(t => t.Name)) typeBox.Items.Add(new ComboBoxItem { Content = $"{t.Name} #{t.Ct}", Tag = t.Ct });
        typeBox.SelectedItem = typeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == u.Type - 100);

        void Do((Action? Undo, string? Err) r, bool close = true)
        {
            if (r.Err != null) { MessageBox.Show(this, r.Err, Title); return; }
            Applied?.Invoke(r.Undo!, _d.Units!.Units.Contains(u) ? u : null);
            if (close) Close();
        }

        var props = new GroupBox { Header = "Brigade", Padding = new Thickness(6), Width = 420, Margin = new Thickness(0, 0, 10, 0),
            Content = new StackPanel { Children =
            {
                UnitText.Form(("Team", team), ("Name (number)", name), ("Division", div), ("Orders", orders), ("Assigned obj", aobj), ("X (km)", x), ("Y (km)", y), ("Type", typeBox)),
                UnitText.Btn("Change brigade type", () => { if (typeBox.SelectedItem is ComboBoxItem { Tag: int ct } && ct != u.Type - 100) Do(_ed.ChangeType(u, ct)); }),
            } } };

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        actions.Children.Add(UnitText.Btn("Place brigade at obj", () =>
        {
            if ((aobj.SelectedItem as ComboBoxItem)?.Tag is CampObjective o) Do(_ed.PlaceAt(u, o, true, true));
        }, "Brigade and its battalions on the chosen objective (also as assigned objective), routes removed"));
        actions.Children.Add(UnitText.Btn("Batt > Bri location", () => Do(_ed.BattalionsFollow(u, true, false, false)), "All battalions to the location of the brigade"));
        actions.Children.Add(UnitText.Btn("Assign batts to same Aobj", () => Do(_ed.BattalionsFollow(u, false, true, false)), "All battalions get the brigade's assigned objective"));
        actions.Children.Add(UnitText.Btn("Batt > Bri division", () => Do(_ed.BattalionsFollow(u, false, false, true)), "All battalions get the brigade's division"));

        // battaglioni
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 160 };
        var supGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 90 };
        foreach (var g in new[] { grid, supGrid })
        {
            void C(string h, string p, double w) => g.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p), Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
            C("Nr", nameof(ElemRow.Nr), 35); C("Name", nameof(ElemRow.Name), 260); C("CT", nameof(ElemRow.Ct), 50); C("Vehicles", nameof(ElemRow.Vehicles), 60);
            C("Id", nameof(ElemRow.Id), 70); C("Division", nameof(ElemRow.Division), 60); C("Team", nameof(ElemRow.Team), 100); C("X", nameof(ElemRow.X), 45); C("Y", nameof(ElemRow.Y), 45);
            C("Assigned obj", nameof(ElemRow.Obj), 200); C("Note", nameof(ElemRow.Note), 0);
            var rs = new Style(typeof(DataGridRow)); rs.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(ElemRow.Back)))); g.RowStyle = rs;
            g.MouseDoubleClick += (_, _) => { if (g.SelectedItem is ElemRow r) OpenUnit?.Invoke(r.U); };
        }
        ElemRow Row(CampUnit b, int i)
        {
            string note = b.Owner != u.Owner ? "another team" : _ed.IsAirDefense(b) || _ed.IsSupply(b) ? (b.Parent == u.Id ? "should be a support element" : "") : b.Parent != u.Id ? "points to another brigade" : "";
            double km = Math.Sqrt(Math.Pow(b.X - u.X, 2) + Math.Pow(b.Y - u.Y, 2));
            if (note.Length == 0 && km > 0.5) note = $"{km:0} km from the brigade";
            bool warn = b.Owner != u.Owner || ((_ed.IsAirDefense(b) || _ed.IsSupply(b)) && b.Parent == u.Id) || b.Parent != u.Id && u.Elements.Contains(b.Id);
            return new ElemRow
            {
                U = b, Nr = i, Name = $"{UnitText.Title(db, b)} ({db.UnitVehicles(b.Type - 100).FirstOrDefault()})", Ct = b.Type - 100, Vehicles = UnitText.Vehicles(b.Roster),
                Id = b.Id.ToString(), Division = b.Division, Team = CampaignValidator.TeamName(d, b.Owner), X = b.X, Y = b.Y,
                Obj = d.Obj(b.AssignedObj)?.Name ?? "", Note = note, Back = UnitText.LevelBrush(warn ? IssueLevel.Warning : null),
            };
        }
        var elems = u.Elements.Select(e => d.Unit(e)).OfType<CampUnit>().ToList();
        grid.ItemsSource = elems.Select((b, i) => Row(b, i)).ToList();
        var sups = d.Units!.Units.Where(b => b.Kind == UnitKind.Battalion && b.Cargo == u.Id).ToList();
        supGrid.ItemsSource = sups.Select((b, i) => Row(b, i)).ToList();
        int missing = u.Elements.Count - elems.Count;

        // aggiungi / togli battaglione
        var addBox = new ComboBox { Width = 420, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Independent battalions of the same team (nearest first). New battalions: Battalions & brigades table." };
        foreach (var b in d.Units.Units.Where(b => b.Kind == UnitKind.Battalion && b.Owner == u.Owner && d.Unit(b.Parent) == null && !_ed.IsAirDefense(b) && !_ed.IsSupply(b))
                     .OrderBy(b => Math.Pow(b.X - u.X, 2) + Math.Pow(b.Y - u.Y, 2)).Take(200))
            addBox.Items.Add(new ComboBoxItem { Content = $"{UnitText.Title(db, b)} ({db.UnitVehicles(b.Type - 100).FirstOrDefault()}) – {Math.Sqrt(Math.Pow(b.X - u.X, 2) + Math.Pow(b.Y - u.Y, 2)):0} km", Tag = b });
        var elemBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        elemBar.Children.Add(addBox);
        var addB = UnitText.Btn("Add battalion", () => { if ((addBox.SelectedItem as ComboBoxItem)?.Tag is CampUnit b) Do(_ed.SetBrigade(b, u)); },
            $"The battalion joins the brigade: same objective, place and division (max {UnitEditor.MaxElements} battalions)");
        addB.IsEnabled = u.Elements.Count < UnitEditor.MaxElements;
        elemBar.Children.Add(addB);
        elemBar.Children.Add(UnitText.Btn("Remove battalion", () => { if (grid.SelectedItem is ElemRow r) Do(_ed.SetBrigade(r.U, null)); }, "The selected battalion becomes independent (it stays where it is)"));

        var mid = new StackPanel();
        mid.Children.Add(actions);
        mid.Children.Add(UnitText.T($"Elements  ·  {u.Elements.Count} battalions (max {UnitEditor.MaxElements}; Korea 2-4)" + (missing > 0 ? $"  ·  {missing} MISSING" : "") +
                                    $"  ·  total vehicles {elems.Sum(b => UnitText.Vehicles(b.Roster))}", 13, FontWeights.SemiBold));
        mid.Children.Add(grid);
        mid.Children.Add(elemBar);
        mid.Children.Add(UnitText.T($"Support elements (air defense / supply attached with cargo, BMS 4.37+)  ·  {sups.Count}", 13, FontWeights.SemiBold));
        mid.Children.Add(supGrid);
        mid.Children.Add(UnitText.FlagsBox(u.UnitFlags, f => _flags = f));

        var top = new DockPanel();
        DockPanel.SetDock(props, Dock.Left); top.Children.Add(props);
        top.Children.Add(new ScrollViewer { Content = mid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var dels = new StackPanel { Orientation = Orientation.Horizontal };
        var del1 = UnitText.Btn("Delete brigade", () =>
        {
            if (MessageBox.Show(this, "Delete the brigade? Its battalions stay, as independent battalions.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Do(DeleteKeepBattalions());
        });
        var del2 = UnitText.Btn("Del brigade (incl bat)", () =>
        {
            if (MessageBox.Show(this, $"Delete the brigade and its {u.Elements.Count} battalions?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Do(_ed.Delete(u));
        });
        del1.Foreground = del2.Foreground = Brushes.DarkRed;
        dels.Children.Add(del1); dels.Children.Add(del2);
        var apply = UnitText.Btn("Apply", () =>
        {
            if (!short.TryParse(name.Text, out short nm) || !short.TryParse(div.Text, out short dv) || !int.TryParse(x.Text, out int xx) || !int.TryParse(y.Text, out int yy))
            { MessageBox.Show(this, "Check the numbers.", Title); return; }
            Do(_ed.Apply(u, new UnitEditor.Edit
            {
                Owner = team.SelectedItem is ComboBoxItem { Tag: byte t } ? t : u.Owner, Orders = (byte)Math.Max(0, orders.SelectedIndex),
                NameId = nm, Division = dv, Flags = _flags, X = xx, Y = yy, Roster = u.Roster,
                AssignedObj = (aobj.SelectedItem as ComboBoxItem)?.Tag is CampObjective o ? o.Id : u.AssignedObj,
            }));
        });
        apply.FontWeight = FontWeights.SemiBold;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(UnitText.Btn("3D view…", () => ModelViewWindow.Open(this, UnitText.Title(db, u), ModelViewWindow.VehiclesOf(d, db, u)), "The vehicles of this unit in 3D with the BMS textures"));
        right.Children.Add(UnitText.Btn("Cancel", Close)); right.Children.Add(apply);
        DockPanel.SetDock(dels, Dock.Left); bottom.Children.Add(dels); bottom.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(top);
        Content = root;
    }

    /// <summary>Toglie la brigata lasciando i battaglioni (indipendenti, dove sono).</summary>
    private (Action? Undo, string? Err) DeleteKeepBattalions()
    {
        var uf = _d.Units!;
        var sn = uf.TakeSnapshot();
        foreach (var b in _u.Elements.Select(e => _d.Unit(e)).OfType<CampUnit>().ToList()) uf.SetParent(b, default);
        uf.SetElements(_u, new List<VuId>());
        _d.Reindex();
        var (undo, err) = _ed.Delete(_u);
        if (err != null) { uf.RestoreSnapshot(sn); _d.Reindex(); return (null, err); }
        return (() => { uf.RestoreSnapshot(sn); _d.Reindex(); }, null);
    }
}
