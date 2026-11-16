using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// Tabella di tutti i tipi di battaglione e brigata del database BMS 4.38 (come la tabella delle unita' di Mission Commander):
/// filtri per genere, ruolo, produttore e testo; da qui si aggiunge un'unita' alla campagna (sull'obiettivo selezionato
/// nell'editor, o dentro una brigata) o si cambia il tipo dell'unita' selezionata.
/// </summary>
public sealed class UnitTableWindow : Window
{
    private readonly CampaignDoc _doc;
    private readonly CampaignDb _db;
    private readonly UnitEditor _ed;
    private readonly Func<CampObjective?> _selObj;
    private readonly Func<CampUnit?> _selUnit;
    private readonly List<UnitTypeRow> _all;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.All, VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)), HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
    };
    private readonly ComboBox _kind = new() { Width = 110, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _role = new() { Width = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _prod = new() { Width = 90, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _search = new() { Width = 180, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Unit or vehicle name (e.g. Patriot, T-72, Armored)" };
    private readonly CheckBox _usedOnly = new() { Content = "Only types used in this file", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _team = new() { Width = 180, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _brigade = new() { Width = 300, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _orders = new() { Width = 110, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _division = new() { Text = "2", Width = 40, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Korea: division 2 = light brigades, 3 = heavy" };
    private readonly TextBlock _objText = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _add = new() { Content = "Add to the campaign", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _change = new() { Content = "Change the selected unit to this type", Padding = new Thickness(10, 3, 10, 3) };

    /// <summary>Operazione fatta: annullamento e unita' da selezionare nell'editor.</summary>
    public event Action<Action, CampUnit?>? Applied;

    public UnitTableWindow(CampaignDoc doc, CampaignDb db, Func<CampObjective?> selObj, Func<CampUnit?> selUnit)
    {
        _doc = doc; _db = db; _selObj = selObj; _selUnit = selUnit;
        _ed = new UnitEditor(doc, db);
        _all = UnitEditor.Types(db, doc);
        Title = "Battalions & brigades – " + System.IO.Path.GetFileName(doc.Path);
        Width = 1300; Height = 820;

        foreach (var k in new[] { "All", "Battalions", "Brigades" }) _kind.Items.Add(k);
        _kind.SelectedIndex = 1;
        _role.Items.Add("All roles");
        foreach (var r in _all.Where(r => r.Kind == UnitKind.Battalion).Select(r => r.Role).Distinct().OrderBy(r => r)) _role.Items.Add(new ComboBoxItem { Content = VehicleRoles.Name(r), Tag = r });
        _role.SelectedIndex = 0;
        foreach (var p in new[] { "All", "West", "Soviet", "Chinese" }) _prod.Items.Add(p);
        _prod.SelectedIndex = 0;
        for (int i = 0; i <= 11; i++) _orders.Items.Add(CampUnit.OrdersName(i));
        _orders.SelectedIndex = 0;
        foreach (var t in doc.Teams?.Teams ?? new List<CampTeam>())
            if (t.Index > 0) _team.Items.Add(new ComboBoxItem { Content = $"{t.Index}  {t}", Tag = (byte)t.Index });

        Col("#", nameof(UnitTypeRow.Ct), 50);
        Col("Unit type", nameof(UnitTypeRow.Name), 150);
        Col("Kind", nameof(UnitTypeRow.KindText), 75);
        Col("Role", nameof(UnitTypeRow.RoleText), 85);
        Col("Producer", nameof(UnitTypeRow.ProducerText), 65);
        Col("Main vehicle", nameof(UnitTypeRow.MainVehicle), 130);
        Col("Vehicles", nameof(UnitTypeRow.Vehicles), 0);
        Col("Veh.", nameof(UnitTypeRow.VehicleCount), 45);
        Col("AD range", nameof(UnitTypeRow.AirRange), 65);
        Col("In file", nameof(UnitTypeRow.InFile), 55);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void F(string label, UIElement e) { filters.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }); filters.Children.Add(e); }
        F("Show", _kind); F("Role", _role); F("Producer", _prod); F("Find", _search);
        filters.Children.Add(_usedOnly);

        var addBox = new StackPanel();
        var r1 = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        void A(WrapPanel p, string label, UIElement e) { p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }); p.Children.Add(e); }
        A(r1, "Team", _team); A(r1, "Into brigade", _brigade); A(r1, "Orders", _orders); A(r1, "Division (new brigade)", _division);
        var r2 = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        r2.Children.Add(new TextBlock { Text = "Objective: ", VerticalAlignment = VerticalAlignment.Center });
        r2.Children.Add(_objText);
        var r3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        r3.Children.Add(_add); r3.Children.Add(_change);
        r3.Children.Add(new Button { Content = "3D view…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), ToolTip = "The vehicles of the selected type in 3D with the BMS textures" });
        ((Button)r3.Children[^1]).Click += (_, _) =>
        {
            if (Row is { } row) ModelViewWindow.Open(this, row.Name, ModelViewWindow.VehiclesOfTypes(_db, new[] { row.Ct }));
            else MessageBox.Show(this, "Select a type in the table first.", Title);
        };
        addBox.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Text = "Select an objective (or a brigade) in the editor, pick a type in the table, then Add. A battalion put in a brigade goes on the brigade's objective " +
                   "with its division and orders, and is numbered like Korea (brigade 31 → 311, 312…). A new brigade starts empty: then add its battalions. " +
                   "Brigade types have no vehicles: their battalions do the fighting. Ctrl+Z in the editor undoes each step.",
        });
        foreach (var e in new UIElement[] { r1, r2, r3, _info }) addBox.Children.Add(e);
        var group = new GroupBox { Header = "Add to the campaign / change a unit", Content = addBox, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6) };

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters);
        DockPanel.SetDock(group, Dock.Bottom); root.Children.Add(group);
        root.Children.Add(_grid);
        Content = root;

        _kind.SelectionChanged += (_, _) => Filter();
        _role.SelectionChanged += (_, _) => Filter();
        _prod.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();
        _usedOnly.Click += (_, _) => Filter();
        _team.SelectionChanged += (_, _) => FillBrigades(null);
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _brigade.SelectionChanged += (_, _) => UpdateButtons();
        _add.Click += (_, _) => Add();
        _change.Click += (_, _) => Change();
        Activated += (_, _) => FromEditor();
        Loaded += (_, _) => { Filter(); FromEditor(); };
    }

    private void Col(string head, string path, double width)
    {
        var c = new DataGridTextColumn { Header = head, Binding = new Binding(path), SortMemberPath = path };
        c.Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width);
        if (path == nameof(UnitTypeRow.Vehicles))
            c.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } };
        _grid.Columns.Add(c);
    }

    private UnitTypeRow? Row => _grid.SelectedItem as UnitTypeRow;
    private byte Team => _team.SelectedItem is ComboBoxItem { Tag: byte t } ? t : (byte)0;
    private CampUnit? Brigade => _brigade.SelectedItem is ComboBoxItem { Tag: CampUnit b } ? b : null;

    private void Filter()
    {
        string q = _search.Text.Trim();
        var role = _role.SelectedItem is ComboBoxItem { Tag: VehicleRole r } ? r : (VehicleRole?)null;
        Bloc? bloc = _prod.SelectedIndex switch { 1 => Bloc.West, 2 => Bloc.East, 3 => Bloc.China, _ => null };
        var rows = _all.Where(x => _kind.SelectedIndex == 0 || (_kind.SelectedIndex == 1) == (x.Kind == UnitKind.Battalion))
            .Where(x => role == null || x.Kind == UnitKind.Battalion && x.Role == role)
            .Where(x => bloc == null || x.Bloc == bloc)
            .Where(x => _usedOnly.IsChecked != true || x.InFile > 0)
            .Where(x => q.Length == 0 || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Vehicles.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || x.Ct.ToString(CultureInfo.InvariantCulture) == q)
            .ToList();
        _grid.ItemsSource = rows;
        _info.Text = $"{rows.Count} unit types.";
        UpdateButtons();
    }

    /// <summary>Squadra, obiettivo e brigata presi da quello che e' selezionato nell'editor.</summary>
    public void Refresh() => FromEditor();

    private void FromEditor()
    {
        var o = _selObj();
        var u = _selUnit();
        byte team = (byte)(u?.Owner ?? o?.Owner ?? Team);
        if (team > 0) _team.SelectedItem = _team.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (byte)i.Tag! == team) ?? _team.SelectedItem;
        if (_team.SelectedIndex < 0 && _team.Items.Count > 0) _team.SelectedIndex = 0;
        var br = u?.Kind == UnitKind.Brigade ? u : u?.Kind == UnitKind.Battalion ? _doc.Unit(u.Parent) : null;
        FillBrigades(br);
        _objText.Text = o != null ? $"{o.Name}  ({CampaignValidator.TeamName(_doc, o.Owner)}) – selected in the editor"
                      : "none selected in the editor (needed for a new brigade or an independent battalion)";
        UpdateButtons();
    }

    private void FillBrigades(CampUnit? select)
    {
        _brigade.Items.Clear();
        _brigade.Items.Add(new ComboBoxItem { Content = "(none: independent battalion on the objective)" });
        foreach (var b in _doc.Units?.Units.Where(x => x.Kind == UnitKind.Brigade && x.Owner == Team).OrderBy(x => x.Division).ThenBy(x => x.NameId) ?? Enumerable.Empty<CampUnit>())
        {
            string at = _doc.Obj(b.AssignedObj)?.Name ?? "no objective";
            _brigade.Items.Add(new ComboBoxItem { Content = $"Bde {b.NameId} – {_db.UnitTypeName(b.Type)} (div {b.Division}, {b.Elements.Count} bn) at {at}", Tag = b });
        }
        _brigade.SelectedItem = _brigade.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == select) ?? _brigade.Items[0];
    }

    private void UpdateButtons()
    {
        var r = Row;
        var u = _selUnit();
        _add.IsEnabled = r != null && Team > 0;
        _add.Content = r == null ? "Add to the campaign"
            : r.Kind == UnitKind.Brigade ? "Add brigade on the objective"
            : Brigade != null ? $"Add battalion to Bde {Brigade.NameId}" : "Add independent battalion on the objective";
        _change.IsEnabled = r != null && u != null && u.Kind == r.Kind;
        _change.Content = u != null && u.IsGround ? $"Change {(u.Kind == UnitKind.Brigade ? "Bde" : "Bn")} {u.NameId} to this type" : "Change the selected unit to this type";
    }

    private void Add()
    {
        var r = Row;
        if (r == null || Team == 0) return;
        var o = _selObj();
        byte orders = (byte)Math.Max(0, _orders.SelectedIndex);
        (CampUnit? Unit, Action? Undo, string? Error) res;
        if (r.Kind == UnitKind.Brigade)
        {
            if (o == null) { _info.Text = "Select the objective of the new brigade in the editor first."; return; }
            short.TryParse(_division.Text, out short div);
            res = _ed.AddBrigade(r.Ct, Team, o, div, orders);
        }
        else
        {
            if (Brigade == null && o == null) { _info.Text = "Select an objective in the editor, or choose a brigade."; return; }
            res = _ed.AddBattalion(r.Ct, Team, o, Brigade, orders);
        }
        if (res.Error != null) { _info.Text = res.Error; return; }
        if (o != null && o.Owner != Team && r.Kind == UnitKind.Brigade) _info.Text = "Note: the objective belongs to another team.";
        else _info.Text = $"Added {(r.Kind == UnitKind.Brigade ? "brigade" : "battalion")} {res.Unit!.NameId} – {r.Name}.";
        r.InFile++;
        Applied?.Invoke(res.Undo!, res.Unit);
        if (r.Kind == UnitKind.Brigade) FillBrigades(res.Unit);
        _grid.Items.Refresh();
        UpdateButtons();
    }

    private void Change()
    {
        var r = Row; var u = _selUnit();
        if (r == null || u == null) return;
        string before = _db.UnitTypeName(u.Type);
        var (undo, err) = _ed.ChangeType(u, r.Ct);
        if (err != null) { _info.Text = err; return; }
        _info.Text = $"{u.Kind} {u.NameId}: {before} → {r.Name}" + (u.Kind == UnitKind.Battalion ? " (full roster of the new type)." : ".");
        Applied?.Invoke(undo!, u);
        UpdateButtons();
    }
}
