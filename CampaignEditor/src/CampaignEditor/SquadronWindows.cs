using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Models;

namespace CampaignEditor;

/// <summary>Scelta della base di uno squadrone: aeroporti, piste (anche autostradali), army base (elicotteri), portaerei (task force).</summary>
internal sealed class BasePicker : StackPanel
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly SquadronEditor _se;
    private readonly ComboBox _list = new() { Margin = new Thickness(0, 2, 0, 2), IsTextSearchEnabled = true, MinWidth = 380 };
    private readonly CheckBox _air = new() { Content = "Airbases / airstrips", IsChecked = true, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _army = new() { Content = "Army bases (helicopters)", IsChecked = false, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _ships = new() { Content = "Carriers / task forces", IsChecked = true, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _allTeams = new() { Content = "Show all teams", IsChecked = false };
    public Func<byte> Team = () => 0;
    public CampObjective? Objective => (_list.SelectedItem as ComboBoxItem)?.Tag as CampObjective;
    public CampUnit? Carrier => (_list.SelectedItem as ComboBoxItem)?.Tag as CampUnit;
    public event Action? Changed;

    public BasePicker(CampaignDoc d, CampaignDb db, object? select)
    {
        _d = d; _db = db; _se = new SquadronEditor(d, db);
        var f = new WrapPanel();
        foreach (var c in new[] { _air, _army, _ships, _allTeams }) { f.Children.Add(c); c.Click += (_, _) => Fill(Objective as object ?? Carrier); }
        Children.Add(f);
        Children.Add(_list);
        _list.SelectionChanged += (_, _) => Changed?.Invoke();
        Loaded += (_, _) => Fill(select);
    }

    public void Fill(object? select)
    {
        _list.Items.Clear();
        byte team = Team();
        bool all = _allTeams.IsChecked == true;
        var sq = _d.Units?.Units.Where(u => u.Kind == UnitKind.Squadron).ToList() ?? new();
        if (_air.IsChecked == true || _army.IsChecked == true)
            foreach (var o in _d.Objectives.Where(o => (_air.IsChecked == true && _se.IsAirfield(o)) || (_army.IsChecked == true && _se.IsArmyBase(o)))
                         .Where(o => all || o.Owner == team || o == select).OrderBy(o => o.Name))
            {
                int n = sq.Count(s => s.Airbase == o.Id);
                _list.Items.Add(new ComboBoxItem { Content = $"{o.Name}  ({TerrainTool.Core.Editor.EditorData.TypeName(_db.ObjectiveType(o.Type))}, {CampaignValidator.TeamName(_d, o.Owner)}{(n > 0 ? $", {n} squadrons" : "")})", Tag = o });
            }
        if (_ships.IsChecked == true)
            foreach (var t in _d.Units?.Units.Where(u => u.Kind == UnitKind.TaskForce && (all || u.Owner == team || u == select)) ?? Enumerable.Empty<CampUnit>())
            {
                int n = sq.Count(s => s.Airbase == t.Id);
                _list.Items.Add(new ComboBoxItem { Content = $"⚓ {_db.UnitVehicles(t.Type - 100).FirstOrDefault()} – TF {t.NameId} {_db.UcdName(t.Type - 100)} ({CampaignValidator.TeamName(_d, t.Owner)}{(n > 0 ? $", {n} squadrons" : "")})", Tag = t });
            }
        _list.SelectedItem = _list.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == select) ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
    }
}

/// <summary>"Add Squadron" di Mission Commander: aereo, squadra, base (o portaerei), numero, stemma, specialita'.</summary>
public sealed class AddSquadronWindow : Window
{
    public event Action<Action, CampUnit?>? Applied;

    public AddSquadronWindow(CampaignDoc d, CampaignDb db, CampObjective? editorSel)
    {
        var se = new SquadronEditor(d, db);
        Title = "Add squadron – " + System.IO.Path.GetFileName(d.Path);
        Width = 760; Height = 560;
        var types = se.SquadronTypes();
        var find = new TextBox { ToolTip = "Filter the aircraft list (F-16, MiG, Tanker…)" };
        var ac = new ComboBox { Margin = new Thickness(0, 2, 0, 2), IsTextSearchEnabled = true };
        void FillAc()
        {
            string q = find.Text.Trim();
            ac.Items.Clear();
            foreach (var (ct, text) in types.Where(t => q.Length == 0 || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var bloc = Producer.OfName(db.UnitVehicles(ct).FirstOrDefault());
                ac.Items.Add(new ComboBoxItem { Content = $"{text}{(bloc == Bloc.Unknown ? "" : "  · " + Producer.Short(bloc))}  #{ct}", Tag = ct });
            }
            if (ac.Items.Count > 0) ac.SelectedIndex = 0;
        }
        FillAc();
        find.TextChanged += (_, _) => FillAc();
        byte startTeam = editorSel?.Owner ?? 1;
        var team = UnitText.Teams(d, startTeam);
        var picker = new BasePicker(d, db, editorSel) { Team = () => team.SelectedItem is ComboBoxItem { Tag: byte t } ? t : startTeam };
        team.SelectionChanged += (_, _) => picker.Fill(null);
        var sqs = d.Units?.Units.Where(u => u.Kind == UnitKind.Squadron).ToList() ?? new();
        var name = new TextBox { Text = (sqs.Where(u => u.Owner == startTeam).Select(u => (int)u.NameId).DefaultIfEmpty(0).Max() + 1).ToString(CultureInfo.InvariantCulture) };
        var patch = new TextBox { Text = (sqs.FirstOrDefault(u => u.Owner == startTeam)?.Patch ?? 0).ToString(CultureInfo.InvariantCulture), ToolTip = "Squadron patch number (as the other squadrons of the team)" };
        var spec = new ComboBox();
        foreach (var s in SquadronEditor.Specialties) spec.Items.Add(s);
        spec.SelectedIndex = 0;
        var humanNew = new CheckBox { Content = "Players can fly it (Human Cont)", Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "Flag 0x80. F-16 squadrons are always flyable; other aircraft need this (Korea: the U.S. F-15C at Gunsan)" };
        var info = UnitText.T("", 12);
        void Info()
        {
            if (ac.SelectedItem is not ComboBoxItem { Tag: int ct }) { info.Text = ""; return; }
            var st = db.StoresOf(ct);
            info.Text = $"{UnitText.Vehicles(db.FullRoster(ct))} aircraft, stores: {(st == null ? "none in the database" : $"{st.Count} weapon types (SSD)")}, fuel {se.FuelFor(ct)}. " +
                        "Pilots copied from a squadron of the same team (all available). Flags as Korea: 0x22 on land, 0x32 (No Plan) on a carrier.";
        }
        ac.SelectionChanged += (_, _) => Info();
        Info();

        var form = UnitText.Form(("Find aircraft", find), ("Aircraft", ac), ("Team", team), ("Base", picker), ("Name (number)", name), ("Squad patch", patch), ("Specialty", spec), ("Players", humanNew));
        var apply = UnitText.Btn("Apply", () =>
        {
            if (ac.SelectedItem is not ComboBoxItem { Tag: int ct }) { MessageBox.Show(this, "Choose the aircraft.", Title); return; }
            if (!short.TryParse(name.Text, out short nm) || !ushort.TryParse(patch.Text, out ushort pa)) { MessageBox.Show(this, "Check name and patch.", Title); return; }
            byte t = team.SelectedItem is ComboBoxItem { Tag: byte tt } ? tt : startTeam;
            var (u, undo, err) = se.Add(ct, t, picker.Objective, picker.Carrier, nm, pa, (byte)Math.Max(0, spec.SelectedIndex), humanNew.IsChecked == true);
            if (err != null) { MessageBox.Show(this, err, Title); return; }
            Applied?.Invoke(undo!, u);
            MessageBox.Show(this, $"A new {CampaignValidator.TeamName(d, t)} {db.UnitVehicles(ct).FirstOrDefault()} squadron, {UnitText.Ordinal(nm)}, has been created at {picker.Objective?.Name ?? db.UnitVehicles(picker.Carrier!.Type - 100).FirstOrDefault()}.", Title);
            Close();
        });
        apply.FontWeight = FontWeights.SemiBold;
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(UnitText.Btn("Cancel", Close)); bottom.Children.Add(apply);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(form); root.Children.Add(info); root.Children.Add(bottom);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

/// <summary>Scheda dello squadrone (come "Squadron Unit" di Mission Commander).</summary>
public sealed class SquadronWindow : Window
{
    /// <summary>Legenda dei colori del magazzino.</summary>
    private static UIElement StoresLegend()
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        void L(Color c, string t)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            r.Children.Add(new Border { Width = 14, Height = 12, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) });
            r.Children.Add(new TextBlock { Text = t, FontSize = 11 });
            p.Children.Add(r);
        }
        L(Color.FromRgb(190, 235, 190), "green: more than the standard of the type");
        L(Color.FromRgb(255, 225, 170), "orange: less than the standard of the type");
        L(Colors.White, "white: as the standard");
        p.Children.Add(new TextBlock { FontSize = 11, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap,
            Text = "Standard (SSD) = the stores BMS gives this aircraft type (Falcon4_SSD.xml). Resupply puts every count back to it." });
        return p;
    }

    public event Action<Action, CampUnit?>? Applied;

    public sealed class RosterRow { public int Nr { get; init; } public string Type { get; init; } = ""; public int Act { get; set; } public int Max { get; init; } }
    public sealed class StoreRow { public int Nr { get; init; } public string Store { get; init; } = ""; public int Count { get; set; } public int Max { get; init; } public Brush Back { get; init; } = Brushes.Transparent; }
    public sealed class RoleRow { public int Nr { get; init; } public string Rating { get; init; } = ""; public int Value { get; set; } public int Db { get; init; } public string Total => Value == 255 ? Db.ToString(CultureInfo.InvariantCulture) : Value.ToString(CultureInfo.InvariantCulture); }

    public SquadronWindow(CampaignDoc d, CampaignDb db, CampUnit u)
    {
        var se = new SquadronEditor(d, db);
        var uf = d.Units!;
        int ct = u.Type - 100;
        string ac = db.UnitVehicles(ct).FirstOrDefault() ?? "";
        Title = $"Squadron – {UnitText.Ordinal(u.NameId)} ({u.Id})";
        Width = 1500; Height = 950;
        int roster = u.Roster;
        int flags = u.UnitFlags;

        var head = new StackPanel();
        head.Children.Add(UnitText.T($"{UnitText.Ordinal(u.NameId)} {db.UcdName(ct)} Squadron", 20, FontWeights.SemiBold));
        head.Children.Add(UnitText.T($"Type: {ac}  (CT {ct})   ·   VU {u.Id}   ·   CampId {u.CampId}", 13));
        var ab = d.Obj(u.Airbase); var tf = ab == null ? d.Unit(u.Airbase) : null;
        string baseText = ab != null ? ab.Name : tf != null ? $"{db.UnitVehicles(tf.Type - 100).FirstOrDefault()} (TF {tf.NameId})" : "off the map / none";
        head.Children.Add(UnitText.T($"Airbase: {baseText}", 15, FontWeights.SemiBold));

        // ── cambia
        var team = UnitText.Teams(d, u.Owner);
        var name = new TextBox { Text = u.NameId.ToString(CultureInfo.InvariantCulture) };
        var spec = new ComboBox();
        foreach (var s in SquadronEditor.Specialties) spec.Items.Add(s);
        spec.SelectedIndex = u.Specialty < spec.Items.Count ? u.Specialty : -1;
        var rosterBox = new TextBox { Text = u.Roster.ToString(CultureInfo.InvariantCulture) };
        int max = db.FullRoster(ct);
        var patch = new TextBox { Text = u.Patch.ToString(CultureInfo.InvariantCulture) };
        var fuel = new TextBox { Text = u.Fuel.ToString(CultureInfo.InvariantCulture), ToolTip = $"Standard for this type: {se.FuelFor(ct)}" };
        // skin (texture set) degli aerei dello squadrone: quante ce ne sono lo dice il Parent.dat del modello
        var skin = new ComboBox { IsEnabled = u.TexSetAt != 0,
            ToolTip = u.TexSetAt != 0 ? "Which skin (texture set) the aircraft of this squadron wear, as 'Skin set' in Mission Commander. '3D view…' shows the one chosen here."
                                      : "This file is too old to keep a skin per squadron." };
        int sets = 1;
        try { var md = ModelViewWindow.VehiclesOf(d, db, u).FirstOrDefault()?.ModelDir; if (md != null) sets = Math.Max(1, BmlModel.ReadParent(md)?.TextureSets ?? 1); } catch { }
        sets = Math.Max(sets, u.TexSet + 1);
        for (int i = 0; i < sets; i++) skin.Items.Add(new ComboBoxItem { Content = i == 0 ? "1 (default)" : (i + 1).ToString(CultureInfo.InvariantCulture), Tag = i });
        skin.SelectedIndex = Math.Clamp(u.TexSet, 0, sets - 1);
        var flagNum = UnitText.T($"Flags: {u.UnitFlags}", 12);
        flagNum.Foreground = Brushes.DimGray;
        var human = new CheckBox { Content = "Players can fly it (Human Cont)", IsChecked = SquadronEditor.IsHuman(u), Margin = new Thickness(0, 4, 0, 4), FontWeight = FontWeights.SemiBold,
            ToolTip = "Flag 0x80 'Human Cont' (Mission Commander: 'Control: Set by HQ' off). Korea: only the U.S. F-15C squadron at Gunsan has it; F-16 squadrons are always flyable." };
        human.Click += (_, _) => { int f = human.IsChecked == true ? flags | UnitEditor.FHumanCont : flags & ~UnitEditor.FHumanCont; flagNum.Text = $"Flags: {f} (Mission Commander: {(human.IsChecked == true ? "human flyable" : "set by HQ")})"; };
        var sameType = UnitText.Btn("Same for all squadrons with this aircraft", () =>
        {
            var all = uf.Units.Where(x => x.Kind == UnitKind.Squadron && x.Type == u.Type).ToList();
            var (undo, n) = se.SetHuman(all, human.IsChecked == true);
            if (n == 0) { MessageBox.Show(this, "Nothing to change.", Title); return; }
            Applied?.Invoke(undo!, u);
            MessageBox.Show(this, $"{n} {ac} squadrons {(human.IsChecked == true ? "can now be flown by players" : "are no longer flyable by players")} (all teams). Ctrl+Z in the editor undoes it.", Title);
            Close();
        }, "Applies the tick above to every squadron of this aircraft type, all teams (now)");

        void Done((Action? Undo, string? Err) r)
        {
            if (r.Err != null) { MessageBox.Show(this, r.Err, Title); return; }
            Applied?.Invoke(r.Undo!, d.Units!.Units.Contains(u) ? u : null);
            Close();
        }

        // cambio aereo
        var acBox = new ComboBox { Margin = new Thickness(0, 2, 0, 2), IsTextSearchEnabled = true };
        foreach (var (c, t) in se.SquadronTypes()) acBox.Items.Add(new ComboBoxItem { Content = $"{t}  #{c}", Tag = c });
        acBox.SelectedItem = acBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == ct);
        var changeAc = UnitText.Btn("Change aircraft type", () =>
        {
            if (acBox.SelectedItem is ComboBoxItem { Tag: int c } && c != ct) Done(se.ChangeAircraft(u, c));
        }, "New type now, with full roster, the standard stores (SSD) and fuel of the new type");
        // spostamento
        var picker = new BasePicker(d, db, (object?)ab ?? tf) { Team = () => u.Owner };
        var move = UnitText.Btn("Move to airbase", () =>
        {
            if (picker.Objective == ab && picker.Carrier == tf) return;
            var (undo, err, fl) = se.Move(u, picker.Objective, picker.Carrier);
            if (fl > 0 && err == null) MessageBox.Show(this, $"The squadron has {fl} planned flights: they keep their old route (BMS will plan new ones).", Title);
            Done((undo, err));
        });

        var change = new GroupBox { Header = "Change", Padding = new Thickness(6), Width = 470, Margin = new Thickness(0, 0, 10, 0),
            Content = new StackPanel { Children =
            {
                UnitText.Form(("Team", team), ("Name (number)", name), ("Specialty", spec), ("Roster", rosterBox), ("Squad patch", patch), ("Skin", skin), ("Fuel", fuel)),
                UnitText.Btn($"Max roster: {max} ({UnitText.Vehicles(max)} aircraft)", () => rosterBox.Text = max.ToString(CultureInfo.InvariantCulture)),
                human, flagNum, sameType,
                new TextBlock { Text = "Aircraft", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, acBox, changeAc,
                new TextBlock { Text = "Base", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, picker, move,
            } } };

        // ── statistiche e informazioni
        var st = uf.Stats(u);
        var stats = new GroupBox { Header = "Squadron statistics", Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 0),
            Content = new StackPanel { Children =
            {
                UnitText.T($"A-A kills {st.AA}   A-G kills {st.AG}   A-S kills {st.AS}   A-N kills {st.AN}"),
                UnitText.T($"Missions flown {st.Missions}   Mission score {st.Score}   Total losses {st.Losses}   Pilots lost {st.PilotLosses}"),
                UnitText.T($"X {u.X}   Y {u.Y}   ·   flights planned: {uf.Units.Count(f => f.Kind == UnitKind.Flight && f.Squadron == u.Id)}"),
            } } };

        // ── piloti
        var pilots = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 330, CanUserAddRows = false };
        foreach (var (h, p, w) in new[] { ("Nr", "Nr", 35.0), ("Id", "Id", 50.0), ("Skill", "Skill", 45.0), ("Status", "Status", 50.0), ("AA", "AA", 35.0), ("AG", "AG", 35.0), ("AS", "AS", 35.0), ("AN", "AN", 35.0), ("Missions", "Missions", 60.0) })
            pilots.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p), Width = h == "Missions" ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
        pilots.ItemsSource = uf.Pilots(u);
        bool resetPilots = false, resetStats = false, clearSched = false;
        var pilotBar = new WrapPanel();
        pilotBar.Children.Add(UnitText.Btn("Pilots available again", () => { resetPilots = true; MessageBox.Show(this, "Applied with Apply.", Title); }, "Status 0 (available) for every pilot (MC: Resurrect killed pilots)"));
        pilotBar.Children.Add(UnitText.Btn("Clear used pilots", () => { resetPilots = resetStats = true; MessageBox.Show(this, "Applied with Apply: kills and missions to zero too.", Title); }));
        var pilotBox = new GroupBox { Header = $"Pilots in squadron: {uf.Pilots(u).Count}", Padding = new Thickness(4), Width = 420, Margin = new Thickness(0, 0, 8, 0),
            Content = new StackPanel { Children = { pilots, pilotBar } } };

        // ── organico
        var rosterGrid = new DataGrid { AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 330, CanUserAddRows = false };
        rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Nr", Binding = new Binding("Nr"), IsReadOnly = true, Width = 35 });
        rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding("Type"), IsReadOnly = true, Width = 120 });
        rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Act", Binding = new Binding("Act"), Width = 40 });
        rosterGrid.Columns.Add(new DataGridTextColumn { Header = "Max", Binding = new Binding("Max"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        void FillRoster()
        {
            var ucd = db.UcdOf(ct);
            rosterGrid.ItemsSource = Enumerable.Range(0, 16).Select(i => new RosterRow
            {
                Nr = i, Type = ucd != null && ucd.Vehicles[i] > 0 ? db.VehicleName(ucd.Vehicles[i]) : "",
                Act = (roster >> (2 * i)) & 3, Max = Math.Min(3, ucd?.Elements?[i] ?? 0),
            }).ToList();
        }
        FillRoster();
        rosterGrid.CellEditEnding += (_, e) =>
        {
            if (e.Row.Item is not RosterRow r || e.EditingElement is not TextBox tb || !int.TryParse(tb.Text, out int v)) return;
            roster = (roster & ~(3 << (2 * r.Nr))) | (Math.Clamp(v, 0, 3) << (2 * r.Nr));
            rosterBox.Text = roster.ToString(CultureInfo.InvariantCulture);
        };
        rosterBox.LostFocus += (_, _) => { if (int.TryParse(rosterBox.Text, out int v)) { roster = v; FillRoster(); } };
        var rosterGb = new GroupBox { Header = $"Roster   ·   total AC: {UnitText.Vehicles(u.Roster)}", Padding = new Thickness(4), Width = 260, Margin = new Thickness(0, 0, 8, 0), Content = rosterGrid };

        // ── magazzino
        var stores = uf.Stores(u);
        var ssd = db.StoresOf(ct) ?? new Dictionary<int, int>();
        var storesGrid = new DataGrid { AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 330, CanUserAddRows = false };
        storesGrid.Columns.Add(new DataGridTextColumn { Header = "Nr", Binding = new Binding("Nr"), IsReadOnly = true, Width = 45 });
        storesGrid.Columns.Add(new DataGridTextColumn { Header = "Store", Binding = new Binding("Store"), IsReadOnly = true, Width = 150 });
        storesGrid.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = new Binding("Count"), Width = 50 });
        storesGrid.Columns.Add(new DataGridTextColumn { Header = "Standard (SSD)", Binding = new Binding("Max"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var rs = new Style(typeof(DataGridRow)); rs.Setters.Add(new Setter(BackgroundProperty, new Binding("Back"))); storesGrid.RowStyle = rs;
        var showAll = new CheckBox { Content = "Show all", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void FillStores()
        {
            var rows = new List<StoreRow>();
            for (int i = 0; i < stores.Length; i++)
            {
                int mx = ssd.GetValueOrDefault(i);
                if (showAll.IsChecked != true && stores[i] == 0 && mx == 0) continue;
                if (showAll.IsChecked == true && !db.Wcd.ContainsKey(i)) continue;
                rows.Add(new StoreRow { Nr = i, Store = db.Wcd.GetValueOrDefault(i, "?"), Count = stores[i], Max = mx,
                    Back = stores[i] > mx ? new SolidColorBrush(Color.FromRgb(190, 235, 190)) : stores[i] < mx ? new SolidColorBrush(Color.FromRgb(255, 225, 170)) : Brushes.Transparent });
            }
            storesGrid.ItemsSource = rows;
        }
        FillStores();
        showAll.Click += (_, _) => FillStores();
        storesGrid.CellEditEnding += (_, e) =>
        {
            if (e.Row.Item is StoreRow r && e.EditingElement is TextBox tb && int.TryParse(tb.Text, out int v)) stores[r.Nr] = (byte)Math.Clamp(v, 0, 255);
        };
        var storeBar = new WrapPanel();
        storeBar.Children.Add(UnitText.Btn("Resupply", () => { stores = se.StandardStores(ct, u.StoresLen); FillStores(); }, "Stores back to the standard of the type (Falcon4_SSD.xml), as Mission Commander"));
        storeBar.Children.Add(showAll);
        var storesGb = new GroupBox { Header = $"Stores ({ssd.Count} types in SSD)", Padding = new Thickness(4), Width = 360, Margin = new Thickness(0, 0, 8, 0),
            Content = new StackPanel { Children = { storesGrid, storeBar, StoresLegend() } } };

        // ── programma e ruoli
        var sched = uf.Schedule(u);
        var schedGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 150, CanUserAddRows = false,
            ItemsSource = sched.Select((v, i) => new { Aircraft = i, Usage = $"0x{v:X8}" }).ToList() };
        var csr = uf.CampSpecificRating(u);
        var ucdScores = db.UcdOf(ct)?.RoleScores ?? new int[16];
        var roleRows = Enumerable.Range(0, 16).Select(i => new RoleRow { Nr = i, Rating = SquadronEditor.RoleNames[i], Value = csr.Length == 16 ? csr[i] : 255, Db = ucdScores[i] }).ToList();
        var roleGrid = new DataGrid { AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, Height = 330, CanUserAddRows = false, ItemsSource = roleRows };
        roleGrid.Columns.Add(new DataGridTextColumn { Header = "Nr", Binding = new Binding("Nr"), IsReadOnly = true, Width = 30 });
        roleGrid.Columns.Add(new DataGridTextColumn { Header = "Rating", Binding = new Binding("Rating"), IsReadOnly = true, Width = 95 });
        roleGrid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding("Value"), Width = 50 });
        roleGrid.Columns.Add(new DataGridTextColumn { Header = "DB", Binding = new Binding("Db"), IsReadOnly = true, Width = 40 });
        roleGrid.Columns.Add(new DataGridTextColumn { Header = "Total", Binding = new Binding("Total"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var roleGb = new GroupBox { Header = "Camp specific role (0-100, 255 = from the database)", Padding = new Thickness(4), Width = 300, IsEnabled = csr.Length == 16,
            Content = new StackPanel { Children = { roleGrid, UnitText.Btn("All 255", () => { foreach (var r in roleRows) r.Value = 255; roleGrid.Items.Refresh(); }) } } };
        var schedGb = new GroupBox { Header = "Schedule", Padding = new Thickness(4), Width = 220, Margin = new Thickness(0, 0, 8, 0),
            Content = new StackPanel { Children = { schedGrid, UnitText.Btn("Clear", () => { clearSched = true; MessageBox.Show(this, "Applied with Apply.", Title); }) } } };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(change);
        row1.Children.Add(new StackPanel { Children = { stats, schedGb } });
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row2.Children.Add(pilotBox); row2.Children.Add(rosterGb); row2.Children.Add(storesGb); row2.Children.Add(roleGb);
        var body = new StackPanel();
        body.Children.Add(row1); body.Children.Add(row2);
        body.Children.Add(UnitText.FlagsBox(u.UnitFlags, f => flags = f));

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var del = UnitText.Btn("Delete squadron", () =>
        {
            if (MessageBox.Show(this, "Delete this squadron?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Done(se.Delete(u));
        });
        del.Foreground = Brushes.DarkRed;
        var apply = UnitText.Btn("Apply", () =>
        {
            if (!short.TryParse(name.Text, out short nm) || !int.TryParse(rosterBox.Text, out int ro) || !ushort.TryParse(patch.Text, out ushort pa) || !int.TryParse(fuel.Text, out int fu))
            { MessageBox.Show(this, "Check the numbers.", Title); return; }
            int fl = human.IsChecked == true ? flags | UnitEditor.FHumanCont : flags & ~UnitEditor.FHumanCont;
            Done(se.Apply(u, new SquadronEditor.Edit
            {
                Owner = team.SelectedItem is ComboBoxItem { Tag: byte t } ? t : u.Owner, Specialty = (byte)Math.Max(0, spec.SelectedIndex),
                NameId = nm, Patch = pa, Roster = ro, Flags = fl, Fuel = fu, Stores = stores,
                Skin = u.TexSetAt != 0 && skin.SelectedItem is ComboBoxItem { Tag: int sk } ? sk : null,
                Csr = csr.Length == 16 ? roleRows.Select(r => (byte)Math.Clamp(r.Value, 0, 255)).ToArray() : Array.Empty<byte>(),
                ResetPilots = resetPilots, ResetPilotStats = resetStats, ClearSchedule = clearSched,
            }));
        });
        apply.FontWeight = FontWeights.SemiBold;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(UnitText.Btn("3D view…", () => ModelViewWindow.Open(this, UnitText.Title(db, u), ModelViewWindow.VehiclesOf(d, db, u), skin.SelectedIndex), "The aircraft of this squadron in 3D with the BMS textures, in the skin chosen above"));
        right.Children.Add(UnitText.Btn("Cancel", Close)); right.Children.Add(apply);
        DockPanel.SetDock(del, Dock.Left); bottom.Children.Add(del); bottom.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
