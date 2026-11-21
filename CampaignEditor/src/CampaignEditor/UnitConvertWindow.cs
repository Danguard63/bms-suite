using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Project;

namespace CampaignEditor;

/// <summary>
/// Conversione delle campagne e missioni del vecchio add-on al database BMS 4.38 (ObjectsNT), in tre passi:
/// 1) mezzi che nella 4.38 non esistono → con cosa sostituirli (alternative dello stesso genere);
/// 2) tipi di unita' → tipo 4.38 (scelto in base ai mezzi del passo 1, modificabile);
/// 3) armi di magazzini e carichi che nella 4.38 non esistono → sostituto o togliere.
/// I file convertiti vanno in CampaignEdits e nella copia Output\Install; BMS non si tocca.
/// </summary>
public sealed class UnitConvertWindow : Window
{
    private readonly TheaterProject _p;
    private readonly CampaignDb _oldDb, _newDb;
    private readonly string _edits, _installCamp, _oldCamp;
    private readonly StackPanel _files = new();
    private readonly Grid _vehTable = new(), _unitTable = new(), _wpnTable = new();
    private readonly TabControl _tabs = new();
    private readonly CheckBox _hideUnchanged = new() { Content = "Hide unit types that stay the same", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _showAllVeh = new() { Content = "Show also the vehicles that exist in BMS 4.38", IsChecked = false, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _sameBloc = new() { Content = "Suggest only equipment of the same producer (Western for Western, Soviet or Chinese for Soviet): a Roland does not become an SA-11",
        IsChecked = true, Margin = new Thickness(0, 0, 0, 6), ToolTip = "The producer comes from the name (SA-, ZSU, T-72, MiG, Su- = Soviet; J-, ZTZ, PL- = Chinese; M1A1, MIM, AIM, Roland, Mirage... = Western). Untick to see every alternative." };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _log = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Height = 120, TextWrapping = TextWrapping.Wrap };
    private readonly List<(CheckBox Box, string Name, string Src, int Units, bool Done)> _fileRows = new();
    private Dictionary<int, int> _types = new();          // tipi usati → quante unita'
    private List<UnitTypeConverter.VehicleMatch> _veh = new();
    private List<TypeMatch> _units = new();
    private List<UnitTypeConverter.WeaponMatch> _wpn = new();
    private Dictionary<int, int> _autoWeapons = new();
    public event Action<string>? FileConverted;

    public UnitConvertWindow(TheaterProject p, CampaignDb oldDb, CampaignDb newDb)
    {
        _p = p; _oldDb = oldDb; _newDb = newDb;
        Title = "Convert campaigns – old add-on database → BMS 4.38";
        Width = 1300; Height = 950;
        _edits = ObjectsJob.CampaignEditsFolder(p);
        string addOn = new DirectoryInfo(p.OldTheaterFolder).Name;
        _installCamp = Path.Combine(p.ProjectFolder, "Output", "Install", addOn, "Campaign");
        _oldCamp = Path.Combine(p.OldTheaterFolder, "Campaign");

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "The old add-on had its own vehicles and weapons (Rapier, Roland, Chieftain, Mirage F1EQ…) that BMS 4.38 does not have. " +
                   "1 · Vehicles: for each missing vehicle choose what replaces it (alternatives of the same kind: SAM for a SAM, tank for a tank, fighter for a fighter). " +
                   "2 · Unit types: each old unit type gets the BMS 4.38 unit type that best matches its vehicles after your choices (change it if you want). " +
                   "A unit's vehicles and their weapons then come from the BMS 4.38 database, so a SAM battery always fires its own missiles. " +
                   "3 · Weapons: aircraft weapons of squadron stores and flight loadouts that BMS 4.38 does not have: replace or remove. " +
                   "Only type numbers change: positions, brigades, orders and objectives stay the same. Your choices are saved in the project.",
        };
        var filesBox = new GroupBox { Header = "Files with units (source: campaign editor saves, else Output\\Install, else the old add-on)",
            Content = new ScrollViewer { Content = _files, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var analyze = new Button { Content = "Analyze selected files", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save choices", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var convert = new Button { Content = "Convert selected files", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        bar.Children.Add(analyze); bar.Children.Add(save); bar.Children.Add(convert);

        DockPanel Page(UIElement top, Grid table)
        {
            var dp = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(top, Dock.Top); dp.Children.Add(top);
            dp.Children.Add(new ScrollViewer { Content = table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            return dp;
        }
        _tabs.Items.Add(new TabItem { Header = "1 · Vehicles", Content = Page(new StackPanel { Children = { _showAllVeh, _sameBloc } }, _vehTable) });
        _tabs.Items.Add(new TabItem { Header = "2 · Unit types", Content = Page(_hideUnchanged, _unitTable) });
        _tabs.Items.Add(new TabItem { Header = "3 · Weapons", Content = Page(new TextBlock { Text = "Weapons of squadron stores and flight loadouts that BMS 4.38 does not have (weapons that only changed number are moved automatically).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }, _wpnTable) });

        var root = new DockPanel { Margin = new Thickness(8) };
        foreach (UIElement e in new UIElement[] { intro, filesBox, bar, _summary }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        DockPanel.SetDock(_log, Dock.Bottom); root.Children.Add(_log);
        root.Children.Add(_tabs);
        Content = root;

        analyze.Click += (_, _) => Analyze();
        save.Click += (_, _) => SaveChoices();
        convert.Click += (_, _) => Convert();
        _hideUnchanged.Click += (_, _) => FillUnits();
        _showAllVeh.Click += (_, _) => FillVehicles();
        _sameBloc.Click += (_, _) => { FillVehicles(); FillUnits(); FillWeapons(); };
        Loaded += (_, _) => { FillFiles(); Analyze(); };
    }

    private string MapPath => Path.Combine(_p.ProjectFolder, UnitTypeConverter.MapFile);
    private string VehPath => Path.Combine(_p.ProjectFolder, "vehicle_map.csv");
    private string WpnPath => Path.Combine(_p.ProjectFolder, "weapon_map.csv");

    private void Log(string s) { _log.AppendText(s + "\n"); _log.ScrollToEnd(); }

    private void FillFiles()
    {
        _files.Children.Clear(); _fileRows.Clear();
        var done = UnitTypeConverter.LoadDone(_edits);
        if (!Directory.Exists(_oldCamp)) { Log("Old add-on Campaign folder not found: " + _oldCamp); return; }
        var names = Directory.EnumerateFiles(_oldCamp).Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            string src = new[] { Path.Combine(_edits, n), Path.Combine(_installCamp, n), Path.Combine(_oldCamp, n) }.First(File.Exists);
            int units;
            try
            {
                var uni = CampaignArchive.Read(src).FirstOrDefault(a => a.Name.EndsWith(".uni", StringComparison.OrdinalIgnoreCase)).Data;
                units = uni != null && uni.Length >= 10 ? BitConverter.ToUInt16(uni, 4) : 0;
            }
            catch (Exception) { continue; }
            if (units == 0) continue;
            bool isDone = done.Contains(n);
            string where = src.StartsWith(_edits, StringComparison.OrdinalIgnoreCase) ? "editor save" : src.StartsWith(_installCamp, StringComparison.OrdinalIgnoreCase) ? "Output\\Install" : "old add-on";
            var cb = new CheckBox { IsChecked = !isDone, IsEnabled = !isDone, Margin = new Thickness(2),
                Content = $"{n}   ·   {units} units   ·   from {where}" + (isDone ? "   ·   ALREADY CONVERTED" : "") };
            _files.Children.Add(cb);
            _fileRows.Add((cb, n, src, units, isDone));
        }
        if (_fileRows.Count == 0) Log("No campaign or TE file with units found.");
    }

    // ───────────────────────── analisi ─────────────────────────

    private void Analyze()
    {
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            _types = new Dictionary<int, int>();
            var kinds = new Dictionary<int, UnitKind>();
            var wuses = new Dictionary<int, int>();
            foreach (var r in _fileRows.Where(r => r.Box.IsChecked == true))
            {
                var d = CampaignDoc.Load(r.Src, _oldDb.ClassOf);
                if (d.Units?.Error != null) { Log($"{r.Name}: {d.Units.Error}"); continue; }
                foreach (var u in d.Units?.Units ?? new()) { int t = u.Type - 100; _types[t] = _types.GetValueOrDefault(t) + 1; kinds[t] = u.Kind; }
                UnitTypeConverter.CountWeapons(d, wuses);
            }
            // 1) mezzi, con le scelte salvate
            _veh = UnitTypeConverter.MatchVehicles(_types, _oldDb, _newDb);
            var vsaved = UnitTypeConverter.LoadPairs(VehPath);
            foreach (var v in _veh.Where(v => !v.Exists))
                if (vsaved.TryGetValue(v.OldName, out var nn))
                {
                    v.NewName = nn.Length == 0 ? null : nn; v.Manual = true;
                    if (nn.Length > 0 && v.Candidates.All(c => c.Name != nn)) v.Candidates.Insert(0, (nn, 0));
                }
            // 2) unita'
            _unitKinds = kinds;
            MatchUnits();
            // 3) armi
            _autoWeapons = UnitTypeConverter.WeaponMap(_oldDb, _newDb, out _);
            _wpn = UnitTypeConverter.MatchWeapons(wuses, _oldDb, _newDb);
            var wsaved = UnitTypeConverter.LoadPairs(WpnPath);
            foreach (var w in _wpn)
                if (wsaved.TryGetValue(w.OldIdx.ToString(CultureInfo.InvariantCulture), out var ws) && int.TryParse(ws, out int wi)) { w.NewIdx = wi; w.Manual = true; }
            FillVehicles(); FillUnits(); FillWeapons();
            int miss = _veh.Count(v => !v.Exists);
            _summary.Text = $"{_types.Count} unit types, {_veh.Count} vehicles ({miss} not in BMS 4.38), {_wpn.Count} aircraft weapons not in BMS 4.38. " +
                            "Start from tab 1: every vehicle in red needs a replacement.";
            _tabs.SelectedIndex = 0;
        }
        catch (Exception ex) { Log("Analyze: " + ex.Message); }
        finally { Cursor = null; }
    }

    private Dictionary<int, UnitKind> _unitKinds = new();

    private Dictionary<string, string> VehicleSubs() =>
        _veh.Where(v => !v.Exists && v.NewName != null).ToDictionary(v => UnitTypeConverter.Norm(v.OldName), v => v.NewName!);

    /// <summary>Tipi di unita' rifatti con i mezzi scelti; le scelte fatte a mano nella scheda 2 (o salvate) restano.</summary>
    private void MatchUnits()
    {
        var manual = _units.Where(m => m.Manual).ToDictionary(m => m.OldIdx, m => m.NewIdx);
        foreach (var (k, v) in UnitTypeConverter.LoadManual(MapPath)) manual.TryAdd(k, v);
        var subs = VehicleSubs();
        _units = _types.Select(kv =>
        {
            var m = UnitTypeConverter.Match(kv.Key, _oldDb, _newDb, _unitKinds.GetValueOrDefault(kv.Key), subs);
            m.Count = kv.Value;
            if (manual.TryGetValue(kv.Key, out int mi))
            {
                m.NewIdx = mi; m.Manual = mi != m.AutoIdx;
                if (m.Candidates.All(c => c.Idx != mi)) m.Candidates.Insert(0, (mi, UnitTypeConverter.Describe(_newDb, mi), 0));
            }
            return m;
        }).OrderBy(m => Order(m.Quality)).ThenBy(m => m.Kind).ThenByDescending(m => m.Count).ToList();
    }

    private static int Order(string q) => q switch { "Substitute" or "No equivalent" => 0, "Close" => 1, "Your vehicle" => 2, "Same vehicle" => 3, "Same type, moved" => 4, _ => 5 };

    private static Brush QualityBrush(string q) => q switch
    {
        "Substitute" or "No equivalent" or "Unknown in old database" => Brushes.Red, "Close" => Brushes.DarkOrange,
        "Your vehicle" => Brushes.Blue, "Same vehicle" => Brushes.DarkGoldenrod, "Same type, moved" => Brushes.Green, _ => Brushes.Gray,
    };

    // ───────────────────────── tabelle ─────────────────────────

    private static void Columns(Grid g, params double[] w)
    {
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        foreach (var x in w) g.ColumnDefinitions.Add(new ColumnDefinition { Width = x == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(x) });
    }

    private static void Cell(Grid g, UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); g.Children.Add(e); }

    private static int Head(Grid g, params string[] h)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < h.Length; i++) Cell(g, new TextBlock { Text = h[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 2, 2, 6) }, 0, i);
        return 1;
    }

    private static TextBlock T(string s, Brush? b = null) => new() { Text = s, Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = b ?? Brushes.Black };

    private static string BlocTag(Bloc b) => b == Bloc.Unknown ? "" : "  · " + Producer.Short(b);

    /// <summary>Con il filtro attivo si tolgono le alternative del blocco opposto (resta sempre quella scelta).</summary>
    private bool Hide(Bloc from, string cand, bool chosen) =>
        !chosen && _sameBloc.IsChecked == true && from != Bloc.Unknown && Producer.OfName(cand) is var b && b != Bloc.Unknown && Producer.Affinity(from, b) == 0;

    private void FillVehicles()
    {
        Columns(_vehTable, 200, 110, 65, 60, 0, 320);
        int row = Head(_vehTable, "Vehicle of the old add-on", "Kind", "Producer", "Units", "Used in", "Replace with (BMS 4.38)");
        foreach (var v in _veh)
        {
            if (v.Exists && _showAllVeh.IsChecked != true) continue;
            _vehTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(_vehTable, T(v.OldName, v.Exists ? Brushes.Gray : Brushes.Red), row, 0);
            Cell(_vehTable, T(v.Category), row, 1);
            Cell(_vehTable, T(v.Bloc == Bloc.Unknown ? "?" : Producer.Short(v.Bloc), v.Bloc == Bloc.Unknown ? Brushes.Gray : null), row, 2);
            Cell(_vehTable, T(v.Units.ToString(CultureInfo.InvariantCulture)), row, 3);
            Cell(_vehTable, T(string.Join(", ", v.UsedIn.Take(6)) + (v.UsedIn.Count > 6 ? "…" : "")), row, 4);
            if (v.Exists) Cell(_vehTable, T("exists in BMS 4.38", Brushes.Green), row, 5);
            else
            {
                var cb = new ComboBox { Margin = new Thickness(2), ToolTip = "Alternatives of the same kind first (score = same sub-kind + similar name + same producer)" };
                foreach (var c in v.Candidates)
                    if (!Hide(v.Bloc, c.Name, c.Name == v.NewName))
                        cb.Items.Add(new ComboBoxItem { Content = $"{c.Name}{BlocTag(Producer.OfName(c.Name))}   ({c.Score})", Tag = c.Name });
                cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v.NewName);
                var vv = v;
                cb.SelectionChanged += (_, _) =>
                {
                    if (cb.SelectedItem is not ComboBoxItem it) return;
                    vv.NewName = (string)it.Tag; vv.Manual = vv.NewName != vv.AutoName;
                    MatchUnits(); FillUnits();
                };
                Cell(_vehTable, cb, row, 5);
            }
            row++;
        }
    }

    private void FillUnits()
    {
        Columns(_unitTable, 75, 50, 0, 110, 0);
        int row = Head(_unitTable, "Kind", "Units", "Old add-on unit type (vehicles)", "Match", "BMS 4.38 unit type (vehicles)");
        foreach (var m in _units)
        {
            if (_hideUnchanged.IsChecked == true && m.Quality == "Unchanged" && !m.Manual) continue;
            _unitTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(_unitTable, T(m.Kind.ToString()), row, 0);
            Cell(_unitTable, T(m.Count.ToString(CultureInfo.InvariantCulture)), row, 1);
            Cell(_unitTable, T($"{m.OldText}  [#{m.OldIdx}]{BlocTag(m.OldBloc)}"), row, 2);
            var q = T(m.Manual ? "Your choice" : m.Quality, m.Manual ? Brushes.Blue : QualityBrush(m.Quality));
            Cell(_unitTable, q, row, 3);
            var cb = new ComboBox { Margin = new Thickness(2) };
            foreach (var c in m.Candidates)
            {
                string mainVeh = _newDb.UnitVehicles(c.Idx).FirstOrDefault() ?? "";
                if (Hide(m.OldBloc, mainVeh, c.Idx == m.NewIdx)) continue;
                cb.Items.Add(new ComboBoxItem { Content = c.Text + BlocTag(Producer.OfName(mainVeh)) + (c.Score > 0 ? $"   ({c.Score})" : ""), Tag = c.Idx });
            }
            cb.Items.Add(new ComboBoxItem { Content = "(leave the number as it is)", Tag = m.OldIdx });
            cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == m.NewIdx);
            var mm = m;
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedItem is not ComboBoxItem it) return;
                mm.NewIdx = (int)it.Tag; mm.Manual = mm.NewIdx != mm.AutoIdx;
                q.Text = mm.Manual ? "Your choice" : mm.Quality; q.Foreground = mm.Manual ? Brushes.Blue : QualityBrush(mm.Quality);
            };
            Cell(_unitTable, cb, row, 4);
            row++;
        }
    }

    private void FillWeapons()
    {
        Columns(_wpnTable, 220, 90, 70, 0);
        int row = Head(_wpnTable, "Weapon of the old add-on", "Kind", "Uses", "Replace with (BMS 4.38)");
        foreach (var w in _wpn)
        {
            _wpnTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(_wpnTable, T(w.OldName + BlocTag(w.Bloc), Brushes.Red), row, 0);
            Cell(_wpnTable, T(w.Category), row, 1);
            Cell(_wpnTable, T(w.Uses.ToString(CultureInfo.InvariantCulture)), row, 2);
            var cb = new ComboBox { Margin = new Thickness(2) };
            cb.Items.Add(new ComboBoxItem { Content = "(remove it)", Tag = -1 });
            foreach (var c in w.Candidates)
                if (!Hide(w.Bloc, c.Name, c.Idx == w.NewIdx))
                    cb.Items.Add(new ComboBoxItem { Content = $"{c.Name}{BlocTag(Producer.OfName(c.Name))}   ({c.Score})", Tag = c.Idx });
            cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == w.NewIdx);
            var ww = w;
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem it) { ww.NewIdx = (int)it.Tag; ww.Manual = ww.NewIdx != ww.AutoIdx; } };
            Cell(_wpnTable, cb, row, 3);
            row++;
        }
        if (_wpn.Count == 0) { _wpnTable.RowDefinitions.Add(new RowDefinition()); Cell(_wpnTable, T("All weapons used exist in BMS 4.38.", Brushes.Green), row, 0); }
    }

    // ───────────────────────── salvataggio e conversione ─────────────────────────

    private void SaveChoices()
    {
        try
        {
            UnitTypeConverter.SaveMap(MapPath, _units, _newDb);
            UnitTypeConverter.SavePairs(VehPath, "OldVehicle;NewVehicle;Kind",
                _veh.Where(v => !v.Exists && v.Manual).Select(v => (v.OldName, v.NewName ?? "", v.Category)));
            UnitTypeConverter.SavePairs(WpnPath, "OldIdx;NewIdx;OldName",
                _wpn.Where(w => w.Manual).Select(w => (w.OldIdx.ToString(CultureInfo.InvariantCulture), w.NewIdx.ToString(CultureInfo.InvariantCulture), w.OldName)));
            Log("Choices saved in the project (unit_type_map.csv, vehicle_map.csv, weapon_map.csv).");
        }
        catch (Exception ex) { Log("Save: " + ex.Message); }
    }

    private void Convert()
    {
        var sel = _fileRows.Where(r => r.Box.IsChecked == true && !r.Done).ToList();
        if (sel.Count == 0) { Log("Nothing selected."); return; }
        int open = _veh.Count(v => !v.Exists && v.NewName == null);
        if (MessageBox.Show(this, $"Convert the units of {sel.Count} file(s)?" + (open > 0 ? $"\n\n{open} missing vehicles have no replacement chosen." : "") +
                $"\n\nThe converted files go to\n{_edits}\nand to the Output\\Install copy. Nothing is written into BMS (use Install as usual).",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SaveChoices();
        var map = _units.ToDictionary(m => m.OldIdx, m => m.NewIdx);
        var weapons = new Dictionary<int, int>(_autoWeapons);
        foreach (var w in _wpn) weapons[w.OldIdx] = w.NewIdx;
        Directory.CreateDirectory(_edits);
        foreach (var r in sel)
        {
            try
            {
                var d = CampaignDoc.Load(r.Src, _oldDb.ClassOf);
                if (d.Units?.Error != null) { Log($"{r.Name}: not converted ({d.Units.Error})"); continue; }
                if (d.Units!.Units.Any(u => !map.ContainsKey(u.Type - 100))) { Log($"{r.Name}: not analyzed yet (press Analyze with this file selected)"); continue; }
                var log = UnitTypeConverter.Convert(d, map, weapons);
                string dst = Path.Combine(_edits, r.Name);
                d.Save(dst);
                var chk = CampaignDoc.Load(dst, _newDb.ClassOf);
                if (chk.Units?.Error != null) { Log($"{r.Name}: CHECK FAILED ({chk.Units.Error}) — not marked as converted"); continue; }
                var (bu, bs, bl) = UnitTypeConverter.Verify(chk, _newDb);
                if (bu + bs + bl > 0) { Log($"{r.Name}: CHECK FAILED ({bu} units of the wrong kind, {bs} store and {bl} loadout weapons unknown) — not marked as converted"); continue; }
                Directory.CreateDirectory(_installCamp);
                File.Copy(dst, Path.Combine(_installCamp, r.Name), true);
                UnitTypeConverter.MarkDone(_edits, r.Name);
                Log($"{r.Name}: {string.Join("; ", log)}; check OK (every unit is a unit of the same kind, every weapon a BMS 4.38 weapon)");
                FileConverted?.Invoke(r.Name);
            }
            catch (Exception ex) { Log($"{r.Name}: ERROR {ex.Message}"); }
        }
        FillFiles();
        Log("Done. Next: 'Install campaign files into BMS…' (start window), then try the campaign in BMS.");
    }
}
