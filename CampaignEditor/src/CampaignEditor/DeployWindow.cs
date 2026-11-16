using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Editor;

namespace CampaignEditor;

/// <summary>
/// "Schiera le divisioni": brigate di una squadra in tre scaglioni rispetto al fronte, come le campagne Korea all'inizio
/// (Save1/Save4): tabella da rivedere, poi Apply (brigata e battaglioni sull'obiettivo, ordini Reserve).
/// </summary>
public sealed class DeployWindow : Window
{
    private readonly CampaignDoc _doc;
    private readonly CampaignDb _db;
    private readonly DivisionDeploy _dd;
    private readonly ComboBox _team = new() { Width = 200, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBox _frontShare = new() { Text = "55", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _secondShare = new() { Text = "20", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _frontKm = new() { Text = "35", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _secondKm = new() { Text = "100", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly CheckBox _reserve = new() { Content = "Set all orders to Reserve (as in Korea: BMS gives the real orders from the front)", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _onlyChanges = new() { Content = "Show only the brigades that change", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Grid _table = new();
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private List<DeployRow> _rows = new();
    public event Action<Action>? Applied;

    public DeployWindow(CampaignDoc doc, CampaignDb db)
    {
        _doc = doc; _db = db; _dd = new DivisionDeploy(doc, db);
        Title = "Deploy divisions – " + System.IO.Path.GetFileName(doc.Path);
        Width = 1250; Height = 850;
        foreach (var t in doc.Teams?.Teams ?? new List<CampTeam>())
        {
            int nb = doc.Units?.Units.Count(u => u.Kind == UnitKind.Brigade && u.Owner == t.Index) ?? 0;
            if (t.Index > 0 && nb > 0) _team.Items.Add(new ComboBoxItem { Content = $"{t.Index} {t}  ({nb} brigades)", Tag = t.Index });
        }
        _team.SelectedIndex = 0;
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "How the Korea campaigns start: about 55% of the brigades within 35 km of the front (mostly light: infantry, airmobile, marines), " +
                   "about 20% at 35-100 km (mostly armor, mechanized, artillery), the rest in reserve beyond 100 km. Never on a 1st line objective; " +
                   "one brigade per objective, almost always an army base (then cities, towns, depots); the brigade on the objective and its battalions with it. " +
                   "The front is where the team touches a hostile / at war team (set owners and relations first). Brigades already in a good place stay there.",
        };
        var opts = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        opts.Children.Add(new TextBlock { Text = "Team ", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_team);
        opts.Children.Add(new TextBlock { Text = "Front %", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_frontShare);
        opts.Children.Add(new TextBlock { Text = "within km", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_frontKm);
        opts.Children.Add(new TextBlock { Text = "Second %", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_secondShare);
        opts.Children.Add(new TextBlock { Text = "within km", VerticalAlignment = VerticalAlignment.Center }); opts.Children.Add(_secondKm);
        var gen = new Button { Content = "Make the plan", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply the ticked rows", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(gen); bar.Children.Add(apply);
        var top = new StackPanel();
        foreach (var e in new UIElement[] { intro, opts, _reserve, _onlyChanges, bar, _info }) top.Children.Add(e);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(new ScrollViewer { Content = _table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _team.SelectionChanged += (_, _) => { _rows.Clear(); FillTable(); _info.Text = Team >= 0 ? _dd.Describe(Team) : ""; };
        _onlyChanges.Click += (_, _) => FillTable();
        gen.Click += (_, _) => MakePlan();
        apply.Click += (_, _) => ApplyPlan();
        Loaded += (_, _) => _info.Text = Team >= 0 ? _dd.Describe(Team) : "No team with brigades in this file.";
    }

    private int Team => _team.SelectedItem is ComboBoxItem { Tag: int t } ? t : -1;
    private static double Num(TextBox b, double def) => double.TryParse(b.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    private void MakePlan()
    {
        if (Team < 0) return;
        _dd.FrontShare = Math.Clamp(Num(_frontShare, 55) / 100, 0, 1);
        _dd.SecondShare = Math.Clamp(Num(_secondShare, 20) / 100, 0, 1 - _dd.FrontShare);
        _dd.FrontKm = Num(_frontKm, 35); _dd.SecondKm = Math.Max(_dd.FrontKm + 1, Num(_secondKm, 100));
        _rows = _dd.Plan(Team, out var msg);
        _info.Text = _dd.Describe(Team) + "\n" + msg + (_rows.Count > 0 ? " Untick what you don't want, then Apply (Ctrl+Z in the editor undoes it all)." : "");
        FillTable();
    }

    private void FillTable()
    {
        _table.Children.Clear(); _table.RowDefinitions.Clear(); _table.ColumnDefinitions.Clear();
        foreach (var w in new[] { 30.0, 0, 70, 0, 0, 70, 70 })
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Cell(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); _table.Children.Add(e); }
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] h = { "", "Brigade", "Echelon", "From", "To", "Move km", "Front km" };
        for (int i = 0; i < h.Length; i++) Cell(new TextBlock { Text = h[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 2, 2, 6) }, 0, i);
        int row = 1;
        foreach (var r in _rows.OrderBy(r => r.Echelon).ThenBy(r => r.FrontKm))
        {
            bool changes = r.Target != r.From;
            if (_onlyChanges.IsChecked == true && !changes) continue;
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cb = new CheckBox { IsChecked = r.Apply, VerticalAlignment = VerticalAlignment.Center };
            var rr = r; cb.Click += (_, _) => rr.Apply = cb.IsChecked == true;
            Cell(cb, row, 0);
            Cell(new TextBlock { Text = $"{_db.UnitTypeName(r.Brigade.Type)} (Bde {r.Brigade.NameId}, div {r.Brigade.Division}, {r.Brigade.Elements.Count} bn)" + (r.Light ? " · light" : ""), Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap }, row, 1);
            Cell(new TextBlock { Text = r.Echelon.ToString(), Margin = new Thickness(2), Foreground = r.Echelon == Echelon.Front ? Brushes.Red : r.Echelon == Echelon.Second ? Brushes.DarkOrange : Brushes.Gray }, row, 2);
            Cell(new TextBlock { Text = r.From?.Name ?? "?", Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap }, row, 3);
            Cell(new TextBlock { Text = changes ? $"{r.Target.Name} ({EditorData.TypeName(_db.ObjectiveType(r.Target.Type))})" : "(stays)", Margin = new Thickness(2), TextWrapping = TextWrapping.Wrap, Foreground = changes ? Brushes.Black : Brushes.Gray }, row, 4);
            Cell(new TextBlock { Text = r.MoveKm.ToString("0", CultureInfo.InvariantCulture), Margin = new Thickness(2) }, row, 5);
            Cell(new TextBlock { Text = r.FrontKm.ToString("0", CultureInfo.InvariantCulture), Margin = new Thickness(2) }, row, 6);
            row++;
        }
    }

    private void ApplyPlan()
    {
        var sel = _rows.Where(r => r.Apply).ToList();
        if (sel.Count == 0) return;
        var undo = _dd.Apply(sel, _reserve.IsChecked == true);
        Applied?.Invoke(undo);
        _rows.Clear(); FillTable();
        _info.Text = $"Applied {sel.Count} brigades. " + _dd.Describe(Team);
    }
}
