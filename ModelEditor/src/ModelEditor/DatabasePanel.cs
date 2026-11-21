using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Database;

namespace ModelEditor;

/// <summary>
/// Database (read only): the Falcon4_*.xml tables of a theater as the BMS Editor shows them — vehicles (aircraft, ground, ships),
/// weapons, hardpoint lists, units, sensors, features, Class Table — with the links between them (vehicle → slots → weapons,
/// weapon → who carries it, unit → vehicles, aircraft → sensors) and "View 3D" to the model. Nothing is written.
/// </summary>
public sealed class DatabasePanel : DockPanel
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private readonly Func<string> _dataDir;
    private readonly Action<string> _open3D;
    private readonly ListBox _cats = new() { Width = 190, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, FontSize = 12,
    };
    private readonly StackPanel _detail = new() { Margin = new Thickness(10, 4, 10, 10) };
    private readonly TextBlock _info = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _back = new() { Content = "◀ Back", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Stack<DbRecord> _history = new();
    private BmsDatabase? _db;
    private BmsTheater? _th;
    private DbRecord? _shown;
    private bool _navigating;
    private DbEdits? _edits;
    private readonly WrapPanel _editBar = new() { Margin = new Thickness(6, 0, 6, 4) };
    private readonly TextBlock _editInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.SemiBold };
    private bool Editable => _edits != null;
    private Window? Owner => Window.GetWindow(this);

    public sealed record Cat(string Label, string Table, Func<BmsDatabase, DbRecord, bool>? Filter = null)
    {
        public override string ToString() => Label;
    }

    public sealed record Row(int Index, string Name, string Ct, string Model, string Summary, DbRecord R);

    private static readonly Cat[] Cats =
    {
        new("Aircraft & helicopters", "VCD", (d, r) => d.DomainOf(r) == 2),
        new("Ground vehicles", "VCD", (d, r) => d.DomainOf(r) == 3),
        new("Ships", "VCD", (d, r) => d.DomainOf(r) == 4),
        new("Submarines", "VCD", (d, r) => d.DomainOf(r) == 7),
        new("Weapons", "WCD"),
        new("Hardpoint lists", "WLD"),
        new("Weapon sim data", "SWD"),
        new("Rocket pods", "RKT"),
        new("Units", "UCD"),
        new("Squadron stores", "SSD"),
        new("Aircraft data (sensors)", "ACD"),
        new("Radars", "RCD"),
        new("IR sensors", "ICD"),
        new("RWR", "RWD"),
        new("Visual sensors", "VSD"),
        new("Features (buildings)", "FCD"),
        new("Objective types", "OCD"),
        new("Class Table (all)", "CT"),
        new("Other: DDP", "DDP"),
    };

    public DatabasePanel(Func<string> dataDir, Action<string> open3D)
    {
        _dataDir = dataDir; _open3D = open3D;
        var top = new WrapPanel { Margin = new Thickness(6, 6, 6, 4) };
        var reload = new Button { Content = "Reload", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Reads the theater's database files again" };
        reload.Click += (_, _) => LoadDb();
        top.Children.Add(reload);
        top.Children.Add(_back);
        top.Children.Add(_info);
        SetDock(top, Dock.Top); Children.Add(top);
        var note = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 4), FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Text = "Read only: the Falcon4_*.xml files of the theater's objects folder, as the BMS Editor shows them. Blue names are links. " +
                   "Flags with a name were checked against the Korea database; the others are shown as 'bit N'.",
        };
        SetDock(note, Dock.Top); Children.Add(note);
        _editBar.Children.Add(_editInfo);
        foreach (var (t, a, tip) in new (string, Action, string)[]
        {
            ("Changes…", ShowChanges, "Every record changed or added, field by field, with Undo"),
            ("History…", ShowHistory, "Every saved version of the changes: go back to any of them"),
            ("Check database", CheckDb, "Checks that every link (slots, lists, weapons, units, sensors, CT) points to something that exists"),
            ("Install into BMS…", Install, "Writes the changed tables into the theater (backup of every file replaced, FALCON4.* removed so BMS builds them again)"),
            ("Restore…", RestoreBackup, "Puts back the tables of an earlier installation"),
            ("Remove a copy…", RestoreDuplicate, "Removes a copy made with Duplicate… (database records, TE line and name)"),
        })
        {
            var b = new Button { Content = t, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            b.Click += (_, _) => a();
            _editBar.Children.Add(b);
        }
        SetDock(_editBar, Dock.Top); Children.Add(_editBar);

        foreach (var c in Cats) _cats.Items.Add(c);
        SetDock(_cats, Dock.Left); Children.Add(_cats);

        _grid.Columns.Add(Col("#", nameof(Row.Index), 50));
        _grid.Columns.Add(Col("Name", nameof(Row.Name), 190));
        _grid.Columns.Add(Col("CT", nameof(Row.Ct), 50));
        _grid.Columns.Add(Col("Model", nameof(Row.Model), 50));
        _grid.Columns.Add(Col("Summary", nameof(Row.Summary), 260));
        var mid = new DockPanel { Width = 620, Margin = new Thickness(0, 0, 6, 0) };
        var sp = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var sl = new TextBlock { Text = "Search ", VerticalAlignment = VerticalAlignment.Center };
        SetDock(sl, Dock.Left); sp.Children.Add(sl); sp.Children.Add(_search);
        SetDock(sp, Dock.Top); mid.Children.Add(sp);
        mid.Children.Add(_grid);
        SetDock(mid, Dock.Left); Children.Add(mid);
        Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

        _cats.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _grid.SelectionChanged += (_, _) => { if (!_navigating && _grid.SelectedItem is Row r) Show(r.R, true); };
        _back.Click += (_, _) =>
        {
            if (_history.Count == 0) return;
            var r = _history.Pop();
            Show(r, false);
            _back.IsEnabled = _history.Count > 0;
        };
        _detail.Children.Add(new TextBlock { Text = "Choose a theater, a category on the left and a record.", Foreground = Brushes.Gray });
    }

    private static DataGridTextColumn Col(string h, string path, double w) =>
        new() { Header = h, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(w) };

    private bool _loaded;
    /// <summary>Called when the tab is opened: reads the database of the theater chosen at the top of the window (once).</summary>
    public void EnsureLoaded() { if (!_loaded && _th != null) { _loaded = true; LoadDb(); } }

    /// <summary>Theater chosen at the top of the window (the same for the Models tab).</summary>
    public void SetTheater(BmsTheater? t, bool visible)
    {
        _th = t; _loaded = false; _db = null; _edits = null;
        _grid.ItemsSource = null; _detail.Children.Clear();
        _info.Text = t == null ? "Choose the BMS Data folder and the theater at the top." : "";
        UpdateEditInfo();
        if (visible) EnsureLoaded();
    }

    private void LoadDb()
    {
        if (_th is not BmsTheater t) return;
        _loaded = true;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _db = BmsDatabase.Load(t.ObjectDir);
            _edits = null;
            if (!DbInstaller.IsShared(_dataDir(), t.ObjectDir))
            {
                _edits = DbEdits.Open(Path.Combine(MainWindow.Root(), "Theaters", TheaterWorkspace.FolderName(t)), t.Name, t.ObjectDir);
                _db.Apply(_edits);
            }
            UpdateEditInfo();
            _history.Clear(); _back.IsEnabled = false; _shown = null;
            _info.Text = $"{t.ObjectDir}: " + string.Join(", ", _db.Tables.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.Count}")) +
                         (_db.Problems.Count > 0 ? "  ⚠ " + string.Join("; ", _db.Problems) : "");
            if (_cats.SelectedIndex < 0) _cats.SelectedIndex = 0; else Fill();
        }
        catch (Exception ex) { _info.Text = "Database not read: " + ex.Message; }
        finally { Cursor = null; }
    }

    private void UpdateEditInfo()
    {
        foreach (var b in _editBar.Children.OfType<Button>()) b.IsEnabled = Editable;
        if (_th == null) { _editInfo.Text = ""; return; }
        if (!Editable)
        {
            _editInfo.Text = "Read only: this is BMS's shared Terrdata\\objects (Korea and the theaters without their own database).";
            _editInfo.Foreground = Brushes.DimGray;
            foreach (var b in _editBar.Children.OfType<Button>().Where(b => (string)b.Content == "Check database")) b.IsEnabled = true;
            return;
        }
        int n = _edits!.Count;
        _editInfo.Text = n == 0 ? "Editing: no changes yet" : $"Editing: {n} record(s) changed in the work folder – not in BMS until 'Install into BMS'";
        _editInfo.Foreground = n == 0 ? Brushes.DimGray : Brushes.DarkOrange;
    }

    /// <summary>One change: saved at once in the work folder (with a History version), in memory, and shown again.</summary>
    private void Commit(string description, params (string Table, int Num, List<(string, string)> Fields)[] recs)
    {
        if (_edits == null || _db == null) return;
        foreach (var (t, n, f) in recs) { _edits.Set(t, n, f); _db.SetRecord(t, n, f); }
        _edits.Save(description);
        UpdateEditInfo();
        var cur = _shown;
        Fill();
        if (cur != null) { var nr = _db.Get(cur.Table, cur.Num); if (nr != null) Show(nr, false); }
    }

    private Row RowOf(DbRecord r)
    {
        var d = _db!;
        int ct = d.CtOf(r), model = d.ModelOf(r);
        return new Row(r.Num, d.DisplayName(r), ct >= 0 ? ct.ToString(Ci) : "", model > 0 ? model.ToString(Ci) : "", Summary(r), r);
    }

    private string Summary(DbRecord r)
    {
        var d = _db!;
        switch (r.Table)
        {
            case "VCD": return $"{r.Int("MaxSpeed", 0)} km/h, {r.Int("HitPoints", 0)} hit points, {d.Slots(r).Count} slots";
            case "WCD": return $"{BmsDatabase.DamageTypeName(r.Int("DamageType", 0))} {r.Int("Strength", 0)}, range {r.Int("Range", 0)} km, {BmsDatabase.GuidanceName(r.Int("Guidance", 0))}";
            case "WLD": return string.Join(", ", d.ListWeapons(r).Take(4).Select(w => d.Get("WCD", w.Wcd)?.Name ?? $"#{w.Wcd}")) + (d.ListWeapons(r).Count > 4 ? " …" : "");
            case "UCD": return string.Join(", ", d.Elements(r).Where(e => e.Count > 0).GroupBy(e => e.Ct).Select(g => $"{g.Sum(e => e.Count)}× {d.CtName(g.Key)}").Take(4));
            case "CT":  return $"{BmsDatabase.EntityTable(r.Int("EntityType"))} {r.Int("EntityIdx")}, {BmsDatabase.DomainName(r.Int("Domain"))}, model {r.Int("GraphicsNormal")}";
            case "RCD": case "ICD": case "RWD": case "VSD": return r.Get("DetectionRange") is { } dr ? $"detection {dr}" : "";
            case "FCD": return $"{r.Int("HitPoints", 0)} hit points, repair {r.Int("RepairTime", 0)} h";
            case "SWD": return $"{r.Get("WpnClass")}/{r.Get("Domain")}/{r.Get("WpnType")}, weight {r.Get("Weight")}";
            case "ACD": return $"combat class {r.Get("CombatClass")}";
            case "RKT": return $"{r.Int("RocketCount", 0)} rockets";
            case "SSD": return $"{r.Fields.Count(f => f.Field.StartsWith("WpnStores_", StringComparison.Ordinal))} weapon types";
            default: return string.Join(", ", r.Fields.Take(3).Select(f => $"{f.Field} {f.Value}"));
        }
    }

    private void Fill()
    {
        if (_db == null || _cats.SelectedItem is not Cat c) return;
        string q = _search.Text.Trim();
        var rows = _db.Search(c.Table, q).Where(r => c.Filter == null || c.Filter(_db, r)).Select(RowOf).ToList();
        _navigating = true;
        _grid.ItemsSource = rows;
        _navigating = false;
    }

    // ───────────────────────── details

    /// <summary>Show a record (from a link: also selects its category and row).</summary>
    public void Navigate(DbRecord r)
    {
        if (_db == null) return;
        var cat = Cats.FirstOrDefault(c => c.Table == r.Table && (c.Filter == null || c.Filter(_db, r)));
        if (cat != null && !Equals(_cats.SelectedItem, cat))
        {
            _search.Text = "";
            _cats.SelectedItem = cat;
        }
        if (_grid.ItemsSource is List<Row> rows && rows.FirstOrDefault(x => x.R == r) is { } row)
        {
            _navigating = true;
            _grid.SelectedItem = row; _grid.ScrollIntoView(row);
            _navigating = false;
        }
        Show(r, true);
    }

    private void Show(DbRecord r, bool remember)
    {
        if (remember && _shown != null && _shown != r) { _history.Push(_shown); _back.IsEnabled = true; }
        _shown = r;
        var d = _db!;
        _detail.Children.Clear();
        int ct = d.CtOf(r), model = d.ModelOf(r);
        _detail.Children.Add(new TextBlock { Text = d.DisplayName(r), FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var sub = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        sub.Inlines.Add($"{TableName(r.Table)} {r.Num}");
        if (ct >= 0 && r.Table != "CT") { sub.Inlines.Add("  ·  "); sub.Inlines.Add(Link($"Class Table {ct}", d.Get("CT", ct))); }
        if (model > 0) sub.Inlines.Add($"  ·  3D model {model}");
        if (r.Table is "VCD" or "UCD" && ct >= 0 && d.Get("CT", ct) is { } ctr) sub.Inlines.Add($"  ·  {BmsDatabase.DomainName(ctr.Int("Domain"))}");
        _detail.Children.Add(sub);
        if (model > 0)
        {
            string dir = ModelDir(model);
            var b = new Button { Content = "View 3D", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
                                 IsEnabled = Directory.Exists(dir), ToolTip = Directory.Exists(dir) ? dir : $"{dir} not found" };
            b.Click += (_, _) => _open3D(dir);
            _detail.Children.Add(b);
        }
        if (r.Table == "VCD" && Editable && ct >= 0)
        {
            var dup = new Button { Content = "Duplicate…", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "A copy of this vehicle under another name (e.g. 'F-16CM-50 Red' for the other side of a PvP theater): same 3D model, flight model, sensors and weapons, " +
                          "its own class number, squadron and flight types, TE list line and name. Installed at once with a backup (Restore… removes it)." };
            dup.Click += (_, _) => Duplicate(r, ct);
            _detail.Children.Add(dup);
        }

        switch (r.Table)
        {
            case "VCD": Vehicle(r); break;
            case "WCD": Weapon(r); break;
            case "WLD": ListWeapons(r); break;
            case "UCD": Unit(r); break;
            case "ACD": Acd(r); break;
            case "SSD": Stores(r); break;
            case "RKT":
                Head("Rocket pod");
                Pair("Pod", Link(d.Get("WCD", r.Int("RocketPodIdx"))));
                Pair("Rocket", Link(d.Get("WCD", r.Int("RocketWpnIdx"))));
                Pair("Rockets", r.Get("RocketCount") ?? "");
                break;
            case "CT":
                Head("Class Table");
                Pair("Points to", Link(d.EntityOf(r.Num)));
                Pair("Kind", $"{BmsDatabase.EntityTable(r.Int("EntityType"))} (entity type {r.Get("EntityType")}), {BmsDatabase.DomainName(r.Int("Domain"))}, class {r.Get("Class")}, type {r.Get("Type")}, subtype {r.Get("SubType")}, specific {r.Get("Specific")}");
                Pair("3D models", $"normal {r.Get("GraphicsNormal")}, repaired {r.Get("GraphicsRepaired")}, damaged {r.Get("GraphicsDamaged")}, destroyed {r.Get("GraphicsDestroyed")} (left {r.Get("GraphicsLeftDestroyed")}, right {r.Get("GraphicsRightDestroyed")}, both {r.Get("GraphicsBothDestroyed")})");
                if (r.Int("EntityType") == BmsDatabase.Vehicle && r.Int("Domain") == 2) Pair("Aircraft data", Link(d.Get("ACD", r.Int("MoverDefinitionData"))));
                break;
            case "FCD":
                Head("Feature");
                Pair("Flags", BmsDatabase.FlagText(r.Long("Flags"), BmsDatabase.FeatureFlags));
                Pair("Radar", RadarLink(r));
                Damage(r);
                break;
            case "SWD":
                Head("Sim weapon data");
                Pair("Used by", string.Join(", ", d.All("WCD").Where(w => w.Int("SimWeaponDataIdx") == r.Num).Select(w => w.Name)));
                break;
        }

        var users = d.UsedBy(r);
        if (users.Count > 0)
        {
            Head($"Used by ({users.Count})");
            foreach (var (who, how) in users.Take(300))
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                tb.Inlines.Add(Link(who)); tb.Inlines.Add(new Run($"  {TableName(who.Table)} {who.Num} · {how}") { Foreground = Brushes.DimGray });
                _detail.Children.Add(tb);
            }
            if (users.Count > 300) _detail.Children.Add(new TextBlock { Text = $"… {users.Count - 300} more", Foreground = Brushes.Gray });
        }

        Head("All fields (as in the file)");
        var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        foreach (var (k, v) in r.Fields)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = new TextBlock { Text = k, Foreground = Brushes.DimGray, FontFamily = new FontFamily("Consolas"), FontSize = 11.5 };
            var b = new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11.5 };
            Grid.SetRow(a, row); Grid.SetRow(b, row); Grid.SetColumn(b, 1);
            g.Children.Add(a); g.Children.Add(b);
            row++;
        }
        _detail.Children.Add(g);
    }

    private string ModelDir(int model)
    {
        string m = model.ToString(Ci);
        if (_th != null && Path.Combine(_th.ObjectDir, "Models", m) is var own && Directory.Exists(own)) return own;
        return Path.Combine(_dataDir(), "Terrdata", "objects", "Models", m);
    }

    private static string TableName(string t) => t switch
    {
        "VCD" => "Vehicle", "WCD" => "Weapon", "WLD" => "Hardpoint list", "UCD" => "Unit", "CT" => "Class Table", "FCD" => "Feature", "OCD" => "Objective type",
        "SWD" => "Sim weapon data", "ACD" => "Aircraft data", "RCD" => "Radar", "ICD" => "IR sensor", "RWD" => "RWR", "VSD" => "Visual sensor",
        "SSD" => "Squadron stores", "RKT" => "Rocket pod", "DDP" => "DDP", _ => t,
    };

    private Inline Link(DbRecord? r) => r == null ? new Run("—") { Foreground = Brushes.Gray } : Link(_db!.DisplayName(r), r);

    private Inline Link(string text, DbRecord? r)
    {
        if (r == null) return new Run(text) { Foreground = Brushes.Gray };
        var h = new Hyperlink(new Run(text)) { ToolTip = $"{TableName(r.Table)} {r.Num}" };
        h.Click += (_, _) => Navigate(r);
        return h;
    }

    private Inline RadarLink(DbRecord r) => r.Int("RadarIdx", 0) > 0 ? Link(_db!.Get("RCD", r.Int("RadarIdx"))) : new Run("none");

    private void Head(string s) => _detail.Children.Add(new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 12, 0, 4) });

    private void Pair(string label, object value)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, Foreground = Brushes.DimGray });
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (value is Inline il) tb.Inlines.Add(il); else tb.Text = value?.ToString() ?? "";
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        _detail.Children.Add(g);
    }

    private void Fields(DbRecord r, params (string Field, string Label)[] f)
    {
        foreach (var (k, l) in f) if (r.Get(k) is { } v) Pair(l, v);
    }

    /// <summary>Hit chance / strength / range / detection per kind of target (8 columns), as in the BMS Editor.</summary>
    private void Combat(DbRecord r)
    {
        var rows = new[] { ("Hit_", "Hit chance %"), ("Str_", "Strength"), ("Rng_", "Range (km)"), ("Det_", "Detection (km)") }.Where(x => r.Get(x.Item1 + "NoMove") != null).ToList();
        if (rows.Count == 0) return;
        Head("Against each kind of target");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in BmsDatabase.TargetKinds) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        g.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < BmsDatabase.TargetKinds.Length; i++)
        {
            var h = new TextBlock { Text = BmsDatabase.TargetKinds[i], Foreground = Brushes.DimGray, FontSize = 11 };
            Grid.SetColumn(h, i + 1); g.Children.Add(h);
        }
        int y = 1;
        foreach (var (p, label) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = label, Foreground = Brushes.DimGray };
            Grid.SetRow(l, y); g.Children.Add(l);
            for (int i = 0; i < BmsDatabase.TargetKinds.Length; i++)
            {
                var v = new TextBlock { Text = r.Get(p + BmsDatabase.TargetKinds[i]) ?? "" };
                Grid.SetRow(v, y); Grid.SetColumn(v, i + 1); g.Children.Add(v);
            }
            y++;
        }
        _detail.Children.Add(g);
    }

    /// <summary>Damage taken per kind of warhead (Dam_*), bars 0..100.</summary>
    private void Damage(DbRecord r)
    {
        var dam = r.Fields.Where(f => f.Field.StartsWith("Dam_", StringComparison.Ordinal)).ToList();
        if (dam.Count == 0) return;
        Head("Damage taken per warhead kind (%)");
        foreach (var (k, v) in dam)
        {
            double.TryParse(v, NumberStyles.Float, Ci, out double pct);
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = k[4..].Replace("Incendairy", "Incendiary"), Foreground = Brushes.DimGray });
            var cell = new Grid();
            cell.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(230, 230, 226)), Height = 10 });
            cell.Children.Add(new Border { Background = pct >= 66 ? Brushes.IndianRed : pct >= 25 ? Brushes.Orange : Brushes.SeaGreen, Height = 10,
                                           HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(pct, 0, 100) * 1.6 });
            Grid.SetColumn(cell, 1); g.Children.Add(cell);
            var t = new TextBlock { Text = "  " + v };
            Grid.SetColumn(t, 2); g.Children.Add(t);
            _detail.Children.Add(g);
        }
    }

    private void Vehicle(DbRecord r)
    {
        var d = _db!;
        Head("Basic data");
        Fields(r, ("HitPoints", "Hit points"), ("EmptyWeight", "Empty weight (lbs)"), ("FuelWeight", "Fuel weight (lbs)"), ("MaxWeight", "Max weight (lbs)"),
               ("FuelRate", "Fuel rate (lbs/min)"), ("MaxSpeed", "Max speed (km/h)"), ("MinAlt", "Min alt (ft/100)"), ("CruiseAlt", "Cruise alt (ft/100)"),
               ("MaxAlt", "Max alt (ft/100)"), ("EngineSound", "Engine noise"), ("NumberOfCrew", "Crew"), ("RadarCs", "Radar CS"), ("NCTR", "NCTR / cockpit"),
               ("CallsignIdx", "Callsign"), ("CallsignSlots", "Call slots"), ("InServiceStart", "Service start (year)"), ("InServiceEnd", "Service end (year)"),
               ("Description", "Description"));
        Pair("Radar", RadarLink(r));
        Pair("Flags", BmsDatabase.FlagText(r.Long("Flags"), BmsDatabase.VehicleFlags));
        int ct = d.CtOf(r);
        if (d.DomainOf(r) == 2 && d.Get("CT", ct) is { } c) Pair("Aircraft data (sensors)", Link(d.Get("ACD", c.Int("MoverDefinitionData"))));

        var slots = d.Slots(r);
        if (Editable)
        {
            // anche gli slot vuoti, per poterli riempire
            int n = DbOps.SlotCount(r);
            var full = new List<(int Slot, bool Hardpoint, int Idx, int Count, bool Visible, bool Rack)>();
            for (int i = 0; i < n; i++) full.Add(slots.FirstOrDefault(s => s.Slot == i) is { Slot: var ss } x && ss == i && (x.Idx > 0 || x.Count > 0) ? x : (i, false, 0, 0, false, false));
            slots = full;
        }
        if (slots.Count > 0)
        {
            Head("Weapons (slots)");
            if (Editable)
            {
                var cp = new Button { Content = "Copy all slots from another vehicle…", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
                                      ToolTip = "Gives this vehicle the hardpoints and weapons of another one (e.g. another F-16 version), with a preview" };
                cp.Click += (_, _) => CopySlots(r);
                _detail.Children.Add(cp);
            }
            var g = new Grid();
            foreach (var w in new[] { 50, 80, 240, 60, 55, 45, 80 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            void Cell(int y, int x, object v, bool head = false)
            {
                var tb = new TextBlock { Margin = new Thickness(0, 1, 6, 1), Foreground = head ? Brushes.DimGray : Brushes.Black, TextWrapping = TextWrapping.Wrap };
                if (v is Inline il) tb.Inlines.Add(il); else tb.Text = v.ToString();
                Grid.SetRow(tb, y); Grid.SetColumn(tb, x); g.Children.Add(tb);
            }
            g.RowDefinitions.Add(new RowDefinition());
            string[] hs = { "Slot", "Type", "Name", "Rounds", "Visible", "Rack" };
            for (int i = 0; i < hs.Length; i++) Cell(0, i, hs[i], true);
            int y = 1;
            foreach (var s in slots)
            {
                g.RowDefinitions.Add(new RowDefinition());
                bool empty = s.Idx <= 0 && s.Count <= 0;
                Cell(y, 0, s.Slot); Cell(y, 1, empty ? "" : s.Hardpoint ? "Hardpoint" : "Weapon");
                Cell(y, 2, empty ? new Run("(empty)") { Foreground = Brushes.Gray } : Link(s.Hardpoint ? d.Get("WLD", s.Idx) : d.Get("WCD", s.Idx)));
                Cell(y, 3, s.Hardpoint || empty ? "-" : s.Count.ToString(Ci)); Cell(y, 4, s.Visible ? "✓" : ""); Cell(y, 5, s.Rack ? "✓" : "");
                if (Editable)
                {
                    int slot = s.Slot;
                    var h = new Hyperlink(new Run("Change…")) { ToolTip = "Hardpoint list, weapon or empty for this slot" };
                    h.Click += (_, _) => ChangeSlot(r, slot);
                    Cell(y, 6, h);
                }
                y++;
            }
            _detail.Children.Add(g);
        }
        Combat(r);
        Damage(r);
    }

    private void Weapon(DbRecord r)
    {
        var d = _db!;
        Head("Weapon data");
        Pair("Damage type", BmsDatabase.DamageTypeName(r.Int("DamageType", 0)));
        Fields(r, ("Strength", "Damage"), ("BlastRadius", "Blast radius (ft)"), ("Range", "Range (km)"), ("Weight", "Weight (lbs)"), ("Drag", "Drag"),
               ("MinAlt", "Min alt (ft)"), ("MaxAlt", "Max alt (ft)"), ("FireRate", "Fire rate"), ("Rariety", "Rarity"), ("Rackgroup", "Rack group"),
               ("Collective", "Collective"), ("BulletTTL", "Bullet TTL (s)"), ("BulletVelocity", "Bullet speed (ft/s)"), ("BulletDispersion", "Bullet dispersion"),
               ("BulletRoundsPerSec", "Rounds/sec"));
        Pair("Guidance", BmsDatabase.GuidanceName(r.Int("Guidance", 0)));
        Pair("Flags", BmsDatabase.FlagText(r.Long("Flags"), BmsDatabase.WeaponFlags));
        Pair("Sim weapon data", Link(d.Get("SWD", r.Int("SimWeaponDataIdx"))));
        Pair("Seeker radar", RadarLink(r));
        Combat(r);
    }

    private void ListWeapons(DbRecord r)
    {
        var d = _db!;
        Head($"Weapons this hardpoint can carry ({d.ListWeapons(r).Count})");
        if (Editable)
        {
            var add = new Button { Content = "Add a weapon…", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            add.Click += (_, _) => EditList(r, "add", -1);
            _detail.Children.Add(add);
        }
        int pos = 0;
        foreach (var (w, n) in d.ListWeapons(r))
        {
            var tb = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new Run($"{n} × ") { Foreground = Brushes.DimGray });
            tb.Inlines.Add(Link(d.Get("WCD", w)));
            if (Editable)
            {
                int at = pos;
                var cnt = new Hyperlink(new Run("count…")) { ToolTip = "How many of this weapon the hardpoint carries" };
                cnt.Click += (_, _) => EditList(r, "count", at);
                var rem = new Hyperlink(new Run("remove")) { Foreground = Brushes.Firebrick };
                rem.Click += (_, _) => EditList(r, "remove", at);
                tb.Inlines.Add(new Run("     ")); tb.Inlines.Add(cnt); tb.Inlines.Add(new Run("   ")); tb.Inlines.Add(rem);
            }
            _detail.Children.Add(tb);
            pos++;
        }
    }

    private void Unit(DbRecord r)
    {
        var d = _db!;
        Head("Unit");
        Fields(r, ("MoveType", "Move type"), ("MoveSpeed", "Move speed (km/h)"), ("MaxRange", "Max range (km)"), ("Fuel", "Fuel (lbs)"), ("FuelRate", "Fuel rate"),
               ("MainRole", "Main role"), ("UnitIcon", "Unit icon"), ("Description", "Description"));
        if (r.Int("RadarVehicle", 0) > 0) Pair("Radar vehicle", Link(d.CtName(r.Int("RadarVehicle")), d.EntityOf(r.Int("RadarVehicle"))));
        if (r.Int("SquadronStoresIdx", 0) > 0) Pair("Squadron stores", Link(d.Get("SSD", r.Int("SquadronStoresIdx"))));
        var el = d.Elements(r).Where(e => e.Count > 0).ToList();
        if (el.Count > 0)
        {
            Head("Elements (vehicles)");
            foreach (var (ct, n, name) in el)
            {
                var tb = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
                tb.Inlines.Add(new Run($"{n} × ") { Foreground = Brushes.DimGray });
                tb.Inlines.Add(Link(d.CtName(ct), d.EntityOf(ct)));
                if (name.Length > 0) tb.Inlines.Add(new Run($"   \"{name}\"") { Foreground = Brushes.DimGray });
                _detail.Children.Add(tb);
            }
        }
        Combat(r);
        Damage(r);
    }

    private void Acd(DbRecord r)
    {
        var d = _db!;
        Head("Aircraft data");
        Fields(r, ("CombatClass", "Combat class"), ("AirframeDatIdx", "Airframe data"), ("IrSignatureIdx", "IR signature"));
        Head("Sensors");
        for (int i = 0; i < 5; i++)
        {
            int t = r.Int($"SensorType_{i}"), idx = r.Int($"SensorIdx_{i}");
            if (t < 0) continue;
            string table = t switch { 0 => "VSD", 1 => "RCD", 2 => "ICD", 3 => "RWD", _ => "" };
            Pair($"Sensor {i + 1}", table.Length > 0 ? Link($"{TableName(table)}: {d.Get(table, idx)?.Name ?? $"#{idx}"}", d.Get(table, idx)) : new Run($"type {t} #{idx}"));
        }
    }

    private void Stores(DbRecord r)
    {
        var d = _db!;
        Head("Squadron stores");
        Fields(r, ("InfiniteAG", "Infinite A-G"), ("InfiniteAA", "Infinite A-A"), ("InfiniteGun", "Infinite gun"));
        foreach (var (k, v) in r.Fields.Where(f => f.Field.StartsWith("WpnStores_", StringComparison.Ordinal)))
        {
            int w = int.TryParse(k["WpnStores_".Length..], NumberStyles.Integer, Ci, out int x) ? x : -1;
            var tb = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
            tb.Inlines.Add(new Run($"{v} × ") { Foreground = Brushes.DimGray });
            tb.Inlines.Add(Link(d.Get("WCD", w)));
            _detail.Children.Add(tb);
        }
    }

    // ───────────────────────── editing

    private string UnitsText(DbRecord vcd)
    {
        var u = DbOps.UnitsWith(_db!, vcd);
        return u.Count == 0 ? "No unit type of this theater has it." :
            $"It changes for every unit that has it ({u.Count} unit types): " + string.Join(", ", u.Take(12).Select(_db!.DisplayName)) + (u.Count > 12 ? " …" : "") +
            ".\nThat is every squadron or battalion of those types, of every side, in every campaign of this theater.";
    }

    private void ChangeSlot(DbRecord vcd, int slot)
    {
        var d = _db!;
        var c = DbDialogs.Slot(Owner, d, vcd, slot);
        if (c == null) return;
        string what = c.Idx <= 0 ? "empty" : c.Hardpoint ? $"hardpoint list {d.Get("WLD", c.Idx)?.Name}" : $"{c.Rounds} × {d.Get("WCD", c.Idx)?.Name}";
        if (MessageBox.Show(Owner, $"{vcd.Name}, slot {slot} → {what}.\n\n{UnitsText(vcd)}\n\nSave this change in the work folder?", "Change slot",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Commit($"{vcd.Name}: slot {slot} → {what}", ("VCD", vcd.Num, DbOps.SetSlot(vcd, slot, c.Hardpoint, c.Idx, c.Rounds, c.Visible, c.Rack)));
    }

    private void CopySlots(DbRecord vcd)
    {
        var d = _db!;
        int dom = d.DomainOf(vcd);
        var src = DbDialogs.Pick(Owner, $"Copy the slots into {vcd.Name} from…", d, d.All("VCD").Where(v => v.Num != vcd.Num && v.Num > 0 && d.DomainOf(v) == dom), v => $"({d.Slots(v).Count} slots)");
        if (src == null) return;
        var a = d.Slots(vcd).ToDictionary(s => s.Slot); var b = d.Slots(src).ToDictionary(s => s.Slot);
        string Desc((int Slot, bool Hardpoint, int Idx, int Count, bool Visible, bool Rack) s) => s.Idx <= 0 ? "empty" : s.Hardpoint ? $"list {d.Get("WLD", s.Idx)?.Name}" : $"{s.Count} × {d.Get("WCD", s.Idx)?.Name}";
        var lines = new List<string>();
        int n = Math.Min(DbOps.SlotCount(vcd), DbOps.SlotCount(src));
        for (int i = 0; i < n; i++)
        {
            string x = a.TryGetValue(i, out var sa) ? Desc(sa) : "empty", y = b.TryGetValue(i, out var sb) ? Desc(sb) : "empty";
            if (x != y) lines.Add($"slot {i}:  {x}  →  {y}");
        }
        if (lines.Count == 0) { MessageBox.Show(Owner, $"{src.Name} has the same slots as {vcd.Name}.", "Copy slots"); return; }
        if (MessageBox.Show(Owner, $"{vcd.Name} gets the slots of {src.Name}:\n\n" + string.Join("\n", lines) + $"\n\n{UnitsText(vcd)}\n\nSave this change in the work folder?",
                            "Copy slots", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Commit($"{vcd.Name}: slots copied from {src.Name}", ("VCD", vcd.Num, DbOps.CopySlots(vcd, src)));
    }

    /// <summary>Add / remove / count of a weapon in a hardpoint list; a list used by more vehicles asks: all, or a copy for one.</summary>
    private void EditList(DbRecord wld, string op, int at)
    {
        var d = _db!;
        var weapons = d.ListWeapons(wld);
        string desc;
        if (op == "add")
        {
            if (weapons.Count >= 64) { MessageBox.Show(Owner, "A hardpoint list keeps at most 64 weapons.", "Add a weapon"); return; }
            var w = DbDialogs.Pick(Owner, $"Add a weapon to \"{wld.Name}\"", d, d.All("WCD").Where(x => x.Num > 0 && !weapons.Any(y => y.Wcd == x.Num)));
            if (w == null) return;
            var n = DbDialogs.Number(Owner, "Add a weapon", $"How many {w.Name} on this hardpoint?", 1, 1, 255);
            if (n == null) return;
            weapons.Add((w.Num, n.Value));
            desc = $"+ {n} × {w.Name}";
        }
        else if (op == "remove")
        {
            desc = $"- {d.Get("WCD", weapons[at].Wcd)?.Name}";
            weapons.RemoveAt(at);
        }
        else
        {
            var n = DbDialogs.Number(Owner, "Count", $"How many {d.Get("WCD", weapons[at].Wcd)?.Name} on this hardpoint?", weapons[at].Count, 1, 255);
            if (n == null) return;
            desc = $"{d.Get("WCD", weapons[at].Wcd)?.Name}: {weapons[at].Count} → {n}";
            weapons[at] = (weapons[at].Wcd, n.Value);
        }
        var users = DbOps.VehiclesWithList(d, wld.Num);
        DbRecord? ctx = _history.Count > 0 && _history.Peek() is { Table: "VCD" } pv && users.Any(u => u.Num == pv.Num) ? d.Get("VCD", pv.Num) : null;
        var scope = DbDialogs.Scope.All;
        if (users.Count > 1)   // usata da un mezzo solo (o da nessuno): si cambia e basta
        {
            scope = DbDialogs.ListScope(Owner, d, wld, users, ctx);
            if (scope == DbDialogs.Scope.Cancel) return;
        }
        if (scope == DbDialogs.Scope.All)
        {
            Commit($"list {wld.Name}: {desc}", ("WLD", wld.Num, DbOps.ListWith(wld, wld.Name, weapons)));
            return;
        }
        // copia della lista per un solo mezzo: numero nuovo in fondo, gli slot di quel mezzo che la usavano passano alla copia
        int nn = d.NextFree("WLD");
        string name = DbOps.CopyName(wld.Name, BmsDatabase.MaxName("WLD"));
        var list = DbOps.ListWith(wld, name, weapons);
        var v = ctx!;
        var vf = v.Fields.ToList();
        foreach (var s in d.Slots(v).Where(s => s.Hardpoint && s.Idx == wld.Num))
            vf = DbOps.With(vf, ($"WpnOrHpIdx_{s.Slot}", nn.ToString(Ci)));
        Commit($"{v.Name}: own copy of list {wld.Name} (new list {nn} \"{name}\"): {desc}", ("WLD", nn, list), ("VCD", v.Num, vf));
        if (d.Get("WLD", nn) is { } nr) Navigate(nr);
    }

    private void ShowChanges()
    {
        if (_edits == null || _db == null) return;
        var d = _db;
        var items = _edits.Data.Records.OrderBy(r => r.Table).ThenBy(r => r.Num).ToList();
        var texts = new List<string>();
        foreach (var rec in items)
        {
            var now = d.Get(rec.Table, rec.Num);
            var old = d.Originals.GetValueOrDefault((rec.Table, rec.Num));
            string head = $"{TableName(rec.Table)} {rec.Num}  {(now != null ? d.DisplayName(now) : "")}  {(old == null ? "(new)" : "")}";
            var diffs = new List<string>();
            if (old != null && now != null)
            {
                foreach (var (k, v) in now.Fields) if (old.Get(k) != v) diffs.Add($"{k}: {old.Get(k) ?? "–"} → {v}");
                foreach (var (k, v) in old.Fields) if (now.Get(k) == null) diffs.Add($"{k}: {v} → (removed)");
            }
            texts.Add(head + (diffs.Count > 0 ? "\n      " + string.Join("\n      ", diffs.Take(30)) + (diffs.Count > 30 ? "\n      …" : "") : ""));
        }
        if (texts.Count == 0) { MessageBox.Show(Owner, "No changes in the work folder.", "Changes"); return; }
        int i = DbDialogs.Choose(Owner, "Changes not installed", $"{items.Count} record(s) changed in {_edits.Folder}. Choose one and 'Undo this record' to drop its change " +
                                 "(the other changes stay; History keeps every version).\n\n" + string.Join("\n", _edits.Data.Log.TakeLast(8)), texts, "Undo this record");
        if (i < 0) return;
        var rr = items[i];
        if (MessageBox.Show(Owner, $"Drop the change of {TableName(rr.Table)} {rr.Num}? (A new list made as a copy is dropped too; slots that point to it should be changed back.)",
                            "Undo", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _edits.Data.Records.Remove(rr);
        _edits.Save($"undo {TableName(rr.Table)} {rr.Num}");
        LoadDb();
    }

    private void ShowHistory()
    {
        if (_edits == null) return;
        var h = _edits.History();
        if (h.Count == 0) { MessageBox.Show(Owner, "No saved versions yet.", "History"); return; }
        int i = DbDialogs.Choose(Owner, "History of the changes", "Every change saved a version. Choose one and 'Bring back' to return to it (the current state is saved too, so this can be undone).",
                                 h.Select(x => $"{x.Time:yyyy-MM-dd HH:mm:ss}   {x.Records,3} records   {x.Description}").ToList(), "Bring back this version");
        if (i < 0) return;
        _edits.RestoreVersion(h[i].File);
        LoadDb();
    }

    private void CheckDb()
    {
        if (_db == null) return;
        var p = DbCheck.Run(_db);
        string text = p.Count == 0 ? "No problems: every link points to something that exists." :
            $"{p.Count(x => x.Error)} error(s), {p.Count(x => !x.Error)} warning(s){(Editable ? " (database with the changes of the work folder)" : "")}:\n\n" +
            string.Join("\n", p.Select(x => $"{(x.Error ? "ERROR  " : "warning")}  {TableName(x.Record.Table)} {x.Record.Num} {_db.DisplayName(x.Record)}: {x.Message}"));
        DbDialogs.Report(Owner, "Check database", text);
    }

    private string WorkOut => Path.Combine(_edits!.Folder, "Output", "Objects");
    private string BackupRoot => Path.Combine(_edits!.Folder, "Output", "BmsBackup");

    private void Install()
    {
        if (_edits == null || _db == null || _th == null) return;
        if (_edits.Count == 0) { MessageBox.Show(Owner, "No changes to install.", "Install"); return; }
        if (DbInstaller.BmsRunning()) { MessageBox.Show(Owner, "Falcon BMS is running: close it first.", "Install", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var errors = DbCheck.Run(_db).Where(x => x.Error).ToList();
        if (errors.Count > 0 && MessageBox.Show(Owner, $"Check database finds {errors.Count} error(s), for example:\n{_db.DisplayName(errors[0].Record)}: {errors[0].Message}\n\nInstall anyway?",
                                                "Install", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(Owner, $"Install {_edits.Count} changed record(s) into\n{_th.ObjectDir}?\n\nEvery file replaced is copied first to\n{BackupRoot}\n" +
                                   "and FALCON4.* are removed (BMS builds them again at the next start). Restore… puts everything back.",
                            "Install into BMS", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var files = DbInstaller.Write(_edits, _th.ObjectDir, WorkOut);
            var log = DbInstaller.Install(_th.ObjectDir, WorkOut, BackupRoot);
            File.WriteAllLines(Path.Combine(_edits.Folder, "Output", "Install_log.txt"), new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {_th.ObjectDir}" }.Concat(log).Concat(_edits.Data.Log));
            DbDialogs.Report(Owner, "Installed", string.Join("\n", log) + "\n\nStart BMS: it builds FALCON4.* again from the XML. The changes stay in the work folder (History, Changes).");
        }
        catch (Exception ex) { MessageBox.Show(Owner, "Not installed: " + ex.Message, "Install", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private string DuplicateBackups => Path.Combine(_edits!.Folder, "Output", "BmsBackup_duplicates");

    /// <summary>A copy of a vehicle under another name, planned, shown and installed (backup first).</summary>
    private void Duplicate(DbRecord vcd, int ct)
    {
        if (_edits == null || _db == null || _th == null) return;
        string? name = DbDialogs.Text(Owner, "Duplicate " + vcd.Name, $"Name of the copy of {vcd.Name} (at most {BmsDatabase.MaxName("VCD")} characters, BMS's limit). " +
                                       "It flies with the same flight model and carries the same weapons; it gets its own squadrons and its own line in the TE.",
                                       (vcd.Name + " Red").Length <= BmsDatabase.MaxName("VCD") ? vcd.Name + " Red" : vcd.Name, BmsDatabase.MaxName("VCD"));
        if (name == null) return;
        string work = Path.Combine(_edits.Folder, "Output", "Duplicate");
        Interchange.Plan p;
        try { p = AircraftDuplicate.Make(_th, ct, name, work, _db); }
        catch (Exception ex) { MessageBox.Show(Owner, "Not made: " + ex.Message, "Duplicate", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (p.Problems.Count > 0) { MessageBox.Show(Owner, string.Join("\n", p.Problems), "Duplicate", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (DbInstaller.BmsRunning()) { MessageBox.Show(Owner, "Falcon BMS is running: close it first.", "Duplicate", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(Owner, string.Join("\n", p.Report) + $"\n\nInstall it into {_th.Name} now? Every file replaced is saved first; Restore… (copies) removes it again.",
                "Duplicate", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var log = AircraftDuplicate.Install(p, work, DuplicateBackups);
            DbDialogs.Report(Owner, "Duplicated", string.Join("\n", p.Report) + "\n\n" + string.Join("\n", log) +
                             "\n\nStart BMS: it builds FALCON4.* again. In the TE, create a NEW scenario to choose the copy. Campaigns use it through its squadrons (Campaign Editor).");
            LoadDb();
        }
        catch (Exception ex) { MessageBox.Show(Owner, "Not installed: " + ex.Message, "Duplicate", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreDuplicate()
    {
        if (_edits == null || _th == null) return;
        var b = Interchange.Backups(DuplicateBackups).Where(x => !x.Dir.EndsWith("_restored", StringComparison.OrdinalIgnoreCase)).ToList();
        if (b.Count == 0) { MessageBox.Show(Owner, "No copy made yet.", "Restore copies"); return; }
        int i = DbDialogs.Choose(Owner, "Remove a copy", "Removes what that copy added (database records, TE line, name) and puts back the files it changed. Newest first if there are more.",
                                 b.Select(x => $"{x.Time:yyyy-MM-dd HH:mm:ss}   {x.Summary}").ToList(), "Remove");
        if (i < 0) return;
        if (DbInstaller.BmsRunning()) { MessageBox.Show(Owner, "Falcon BMS is running: close it first.", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            var log = Interchange.Restore(b[i].Dir);
            Directory.Move(b[i].Dir, b[i].Dir + "_restored");
            DbDialogs.Report(Owner, "Removed", string.Join("\n", log));
            LoadDb();
        }
        catch (Exception ex) { MessageBox.Show(Owner, "Not removed: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RestoreBackup()
    {
        if (_edits == null || _th == null) return;
        var b = DbInstaller.Backups(BackupRoot);
        if (b.Count == 0) { MessageBox.Show(Owner, "No backups yet (they are made by 'Install into BMS').", "Restore"); return; }
        int i = DbDialogs.Choose(Owner, "Restore a backup", "The files as they were before that installation. The changes in the work folder are not touched (use History or Changes for those).",
                                 b.Select(x => $"{x.Time:yyyy-MM-dd HH:mm:ss}   {string.Join(", ", x.Files)}").ToList(), "Restore");
        if (i < 0) return;
        if (DbInstaller.BmsRunning()) { MessageBox.Show(Owner, "Falcon BMS is running: close it first.", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            var log = DbInstaller.Restore(_th.ObjectDir, b[i].Dir);
            DbDialogs.Report(Owner, "Restored", string.Join("\n", log));
            LoadDb();
        }
        catch (Exception ex) { MessageBox.Show(Owner, "Not restored: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
