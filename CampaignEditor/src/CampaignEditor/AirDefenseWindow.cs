using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Editor;

namespace CampaignEditor;

/// <summary>
/// Piano delle difese aeree di una squadra (regole delle campagne Korea): ridistribuire i battaglioni antiaerei esistenti
/// verso gli obiettivi piu' importanti e/o crearne di nuovi sugli obiettivi scoperti; tabella da rivedere, poi Apply.
/// </summary>
public sealed class AirDefenseWindow : Window
{
    private readonly CampaignDoc _doc;
    private readonly CampaignDb _db;
    private readonly AirDefense _ad;
    private readonly ComboBox _team = new() { Width = 180, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _redistribute = new() { Content = "Move the existing air defense battalions to the most important objectives", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly CheckBox _fill = new() { Content = "Add new battalions on the objectives that have none", IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _minPrio = new() { Text = "30", Width = 50, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _maxNew = new() { Text = "20", Width = 50, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _donorKm = new() { Text = "50", Width = 50, Margin = new Thickness(4, 0, 12, 0) };
    private readonly StackPanel _types = new();
    private readonly Grid _table = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private List<AdPlanRow> _rows = new();
    public event Action<Action>? Applied;      // azione di annullamento

    public AirDefenseWindow(CampaignDoc doc, CampaignDb db)
    {
        _doc = doc; _db = db; _ad = new AirDefense(doc, db);
        Title = "Air defense plan – " + System.IO.Path.GetFileName(doc.Path);
        Width = 1150; Height = 850;
        foreach (var t in doc.Teams?.Teams ?? new List<CampTeam>())
            if (t.Index > 0) _team.Items.Add(new ComboBoxItem { Content = $"{t.Index} {t}  ({_ad.AdUnits(t.Index).Count} AD battalions)", Tag = t.Index });
        _team.SelectedIndex = 0;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "Rules from the BMS Korea campaigns: air defense battalions are independent (no brigade), with orders 'Air defense', one per objective " +
                   "(on airbases up to the number of SAM/AAA point lists of the layout), placed on the objective. Long range SAMs (SA-10, SA-5, Patriot) go only " +
                   "to SAM sites; medium SAMs (SA-2, SA-3, SA-6, Hawk) to SAM sites, ports and airbases; short range and AAA to airbases, radars and the rest. " +
                   "'Most important' = the objective priority stored in the campaign (Korea: airbases 50, factories 40, SAM sites 30). " +
                   "Existing battalions stay where they are if the place suits them; SAM sites and radars are never emptied.",
        };
        var opts = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        opts.Children.Add(new TextBlock { Text = "Team ", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_team);
        opts.Children.Add(new TextBlock { Text = "Take battalions from at most (km)", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_donorKm);
        opts.Children.Add(new TextBlock { Text = "New ones only on priority ≥", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_minPrio);
        opts.Children.Add(new TextBlock { Text = "max new", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_maxNew);
        var typesBox = new GroupBox { Header = "Types for the new battalions (range in km)", Content = new ScrollViewer { Content = _types, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 4, 0, 4) };
        var gen = new Button { Content = "Make the plan", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply the ticked rows", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(gen); bar.Children.Add(apply);

        var top = new StackPanel();
        foreach (var e in new UIElement[] { intro, opts, _redistribute, _fill, typesBox, bar, _info }) top.Children.Add(e);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _team.SelectionChanged += (_, _) => { FillTypes(); _table.Children.Clear(); _info.Text = ""; };
        gen.Click += (_, _) => MakePlan();
        apply.Click += (_, _) => ApplyPlan();
        FillTypes();
    }

    private int Team => _team.SelectedItem is ComboBoxItem { Tag: int t } ? t : -1;

    private void FillTypes()
    {
        _types.Children.Clear();
        var used = _ad.AdUnits(Team).Select(u => u.Type - 100).ToHashSet();
        foreach (var t in _db.AirDefenseTypes().OrderByDescending(t => _db.UcdOf(t)?.RngAir ?? 0).ThenBy(t => _db.UnitTypeName(t + 100)))
        {
            var u = _db.UcdOf(t);
            var cb = new CheckBox
            {
                Content = $"{_db.UnitTypeName(t + 100)}   ·   {AirDefense.ClassName(_ad.ClassOf(t))}, {u?.RngAir ?? 0} km" + (used.Contains(t) ? "   ·   used by this team" : ""),
                IsChecked = used.Contains(t), Tag = t, Margin = new Thickness(1),
            };
            _types.Children.Add(cb);
        }
    }

    private static int Num(TextBox b, int def) => int.TryParse(b.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

    private void MakePlan()
    {
        if (Team < 0) return;
        _ad.DonorKm = Num(_donorKm, 50);
        var types = _fill.IsChecked == true ? _types.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (int)c.Tag).ToList() : new List<int>();
        _rows = _ad.Plan(Team, _redistribute.IsChecked == true, types, Num(_minPrio, 30), Num(_maxNew, 20));
        FillTable();
        int moves = _rows.Count(r => r.Unit != null), news = _rows.Count(r => r.NewType >= 0);
        _info.Text = _rows.Count == 0 ? "Nothing to do: every air defense battalion is on a suitable objective and nothing important is left without one."
                   : $"{moves} battalions to move, {news} new battalions. Untick the rows you don't want, then Apply (Ctrl+Z in the editor undoes it all).";
    }

    private void FillTable()
    {
        _table.Children.Clear(); _table.RowDefinitions.Clear(); _table.ColumnDefinitions.Clear();
        foreach (var w in new[] { 30.0, 0, 100, 60, 0 })
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Cell(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); _table.Children.Add(e); }
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] h = { "", "Objective", "Type", "Priority", "Air defense" };
        for (int i = 0; i < h.Length; i++) Cell(new TextBlock { Text = h[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 2, 2, 6) }, 0, i);
        int row = 1;
        foreach (var r in _rows)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cb = new CheckBox { IsChecked = r.Apply, VerticalAlignment = VerticalAlignment.Center };
            var rr = r; cb.Click += (_, _) => rr.Apply = cb.IsChecked == true;
            Cell(cb, row, 0);
            Cell(new TextBlock { Text = r.Target.Name, Margin = new Thickness(2) }, row, 1);
            Cell(new TextBlock { Text = EditorData.TypeName(_ad.ObjType(r.Target)), Margin = new Thickness(2) }, row, 2);
            Cell(new TextBlock { Text = r.Target.Priority.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(2) }, row, 3);
            Cell(new TextBlock { Text = r.What, Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap, Foreground = r.NewType >= 0 ? Brushes.DarkGreen : Brushes.Black }, row, 4);
            row++;
        }
    }

    private void ApplyPlan()
    {
        var sel = _rows.Where(r => r.Apply).ToList();
        if (sel.Count == 0) return;
        try
        {
            var undo = _ad.Apply(sel);
            Applied?.Invoke(undo);
            _info.Text = $"Applied {sel.Count} rows. Make the plan again to see what is left.";
            _rows.Clear(); FillTable(); FillTypes();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
