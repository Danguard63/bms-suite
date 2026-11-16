using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Sceglie un'altra disposizione (OCD) per l'obiettivo: dello stesso tipo (un ponte piu' lungo) o di un altro tipo (porto → citta').
/// Elenco dall'add-on Mideast e dalla 4.38; per i ponti la larghezza del fiume sotto e quanto la copre ogni ponte. Scegliere una riga
/// la mette subito sull'obiettivo (mappa e anteprima 3D si aggiornano); "Use this layout" la tiene, Cancel o la chiusura rimettono
/// com'era. Si salva con "Save edits" dell'editor (layout_swaps.csv); la applica Run Objects.
/// </summary>
public sealed class ReplaceLayoutWindow : Window
{
    private readonly EditorData _d;
    private readonly EdObjective _o;
    private readonly Action _changed;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        CanUserAddRows = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox _sameType = new() { Content = "Same type only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox _source = new() { MinWidth = 110, Margin = new Thickness(4, 0, 14, 0) };
    private readonly TextBox _find = new() { Width = 170, Margin = new Thickness(4, 0, 14, 0), ToolTip = "Part of the layout name or its OCD number" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private ICollectionView? _view;
    private readonly bool _bridge;
    private readonly double _riverM;

    // com'era l'obiettivo all'apertura (Cancel lo rimette)
    private readonly (string Src, int Ocd, string Name, int RepType, int Type, double X, double Y, double H, Dictionary<int, PieceEdit> Pieces, Dictionary<int, PointEdit> Points, bool Dirty, int Ver) _back;
    private bool _keep;

    public sealed class Row
    {
        public required EditorData.LayoutChoice C { get; init; }
        public string Source => C.SourceText;
        public int Ocd => C.Ocd;
        public string Name => C.Name;
        public string Type => C.TypeText;
        public int Pieces => C.Pieces;
        public string Size => C.SizeM.ToString("0", CultureInfo.InvariantCulture);
        public string Span => C.SpanM.ToString("0", CultureInfo.InvariantCulture);
        public double Margin { get; init; }
        public string MarginText => double.IsNaN(Margin) ? "" : Margin.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        public double Sort { get; init; }
    }

    public ReplaceLayoutWindow(EditorData d, EdObjective o, Action changed)
    {
        _d = d; _o = o; _changed = changed;
        Title = $"Replace layout – {o.Name}";
        Width = 980; Height = 680;
        _back = (o.RepSource, o.RepOcd, o.RepName, o.RepType, o.Type, o.X, o.Y, o.Heading,
                 new Dictionary<int, PieceEdit>(o.Pieces), new Dictionary<int, PointEdit>(o.PointMoves), o.PiecesDirty, o.PieceVersion);
        _bridge = o.Type == 6 || o.OrigType == 6;
        if (_bridge)
        {
            try
            {
                using var w = new WaterProbe(d.Project);
                var b = d.BridgeAxis(o);
                _riverM = w.HasWater ? EditorData.RiverWidthM(w, b.X, b.Y, b.AxisDeg) : double.NaN;
            }
            catch (Exception) { _riverM = double.NaN; }
        }

        var cur = d.LayoutOf(o);
        _info.Text = $"{o.Name}  [#{o.Id}]   type {EditorData.TypeName(o.OrigType)}, layout OCD {o.OrigOcd}, {cur.Features.Count} pieces now" +
                     (o.RepOcd >= 0 ? $"\nReplaced by: {o.RepName} ({(o.RepSource == LayoutSwaps.Bms ? "BMS 4.38" : "Mideast")} OCD {o.RepOcd})" : "") +
                     (_bridge ? double.IsNaN(_riverM) ? "\nRiver width under the bridge: unknown (no water map)" :
                         $"\nRiver under the bridge (along its axis): {_riverM:0} m wide; bridge now {d.BridgeAxis(o).SpanFt * 0.3048:0} m long" : "") +
                     (o.Pieces.Count + o.PointMoves.Count > 0 ? "\nNote: the pieces and points you moved by hand belong to the old layout and are dropped with a new one." : "");

        foreach (var s in new[] { "All", "Mideast", "BMS 4.38" }) _source.Items.Add(s);
        _source.SelectedIndex = 0;
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        filters.Children.Add(_sameType);
        filters.Children.Add(new TextBlock { Text = "Source", VerticalAlignment = VerticalAlignment.Center });
        filters.Children.Add(_source);
        filters.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center });
        filters.Children.Add(_find);

        void Col(string h, string path, double w) => _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(path), Width = new DataGridLength(w) });
        Col("Source", nameof(Row.Source), 75);
        Col("OCD", nameof(Row.Ocd), 55);
        Col("Name", nameof(Row.Name), 260);
        Col("Type", nameof(Row.Type), 90);
        Col("Pieces", nameof(Row.Pieces), 55);
        Col("Size (m)", nameof(Row.Size), 65);
        if (_bridge)
        {
            Col("Length (m)", nameof(Row.Span), 75);
            _grid.Columns.Add(new DataGridTextColumn { Header = "vs river (m)", Binding = new Binding(nameof(Row.MarginText)), Width = new DataGridLength(85) });
        }

        var use = new Button { Content = "Use this layout", IsDefault = true, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
        var back = new Button { Content = "Back to its own layout", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "No replacement: the objective keeps (or gets back) the layout of the add-on" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 3, 10, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(use); buttons.Children.Add(back); buttons.Children.Add(cancel);

        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0),
            Text = "Select a row: the layout goes on the objective at once (map and 3D preview). " +
                   (_bridge ? "Bridges are turned and moved onto the axis and the centre of the old one; 'vs river' = how much longer than the river (negative = too short). " : "") +
                   "A layout of another type changes the objective's type in the campaign (e.g. port → city: the AI then treats it as a city). " +
                   "'Use this layout', then 'Save edits' in the editor and Run Objects." };

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_info, Dock.Top); root.Children.Add(_info);
        DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters);
        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(help);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        _sameType.Checked += (_, _) => _view?.Refresh();
        _sameType.Unchecked += (_, _) => _view?.Refresh();
        _source.SelectionChanged += (_, _) => _view?.Refresh();
        _find.TextChanged += (_, _) => _view?.Refresh();
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is Row r) Preview(r.C); };
        use.Click += (_, _) =>
        {
            if (_grid.SelectedItem is not Row r) { _status.Text = "Select a layout first."; return; }
            Preview(r.C); _keep = true; Close();
        };
        back.Click += (_, _) => { Restore(); _d.SetReplacement(_o, null); _changed(); _keep = true; Close(); };
        Closing += (_, _) => { if (!_keep) { Restore(); _changed(); } };
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        Cursor = Cursors.Wait;
        _status.Text = "Reading the layouts of the add-on and of BMS 4.38…";
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var all = _d.LayoutCatalog().Where(c => c.Type is not (1 or 2 or 15)).ToList();   // basi: "Korean base…"; incroci: niente da scegliere
                var rows = all.Select(c =>
                {
                    double m = _bridge && !double.IsNaN(_riverM) && _riverM > 0 ? c.SpanM - _riverM : double.NaN;
                    // ponti: prima quelli che coprono il fiume, dal piu' giusto; poi i corti, dal piu' lungo
                    double sort = double.IsNaN(m) ? 0 : m >= 0 ? m : 100000 - m;
                    return new Row { C = c, Margin = m, Sort = sort };
                }).OrderBy(r => r.Sort).ThenBy(r => r.C.Name).ToList();
                _view = CollectionViewSource.GetDefaultView(rows);
                _view.Filter = Filter;
                _grid.ItemsSource = _view;
                _status.Text = $"{rows.Count} layouts (Mideast {all.Count(c => c.Source == LayoutSwaps.Mideast)}, BMS 4.38 {all.Count(c => c.Source == LayoutSwaps.Bms)}); airbases are replaced with 'Replace base…'.";
                if (_o.RepOcd >= 0 && rows.FirstOrDefault(r => r.C.Ocd == _o.RepOcd && r.C.Source == _o.RepSource) is Row cur) { _grid.SelectedItem = cur; _grid.ScrollIntoView(cur); }
            }
            catch (Exception ex) { _status.Text = "Layouts not read: " + ex.Message; }
            finally { Cursor = null; }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool Filter(object item)
    {
        if (item is not Row r) return false;
        if (_sameType.IsChecked == true && r.C.Type != _o.OrigType) return false;
        if (_source.SelectedIndex == 1 && r.C.Source != LayoutSwaps.Mideast) return false;
        if (_source.SelectedIndex == 2 && r.C.Source != LayoutSwaps.Bms) return false;
        string f = _find.Text.Trim();
        return f.Length == 0 || r.C.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.C.Ocd.ToString(CultureInfo.InvariantCulture) == f;
    }

    /// <summary>Rimette l'obiettivo com'era all'apertura e ci mette la disposizione scelta.</summary>
    private void Preview(EditorData.LayoutChoice c)
    {
        Restore();
        _d.SetReplacement(_o, c);
        _status.Text = $"{c.Name}: {c.Pieces} pieces, {c.SizeM:0} m across" + (_bridge ? $", {c.SpanM:0} m long" : "") +
                       (c.Type != _o.OrigType ? $"; the objective becomes a {EditorData.TypeName(c.Type).ToLowerInvariant()}" : "");
        _changed();
    }

    private void Restore()
    {
        _o.RepSource = _back.Src; _o.RepOcd = _back.Ocd; _o.RepName = _back.Name; _o.RepType = _back.RepType; _o.Type = _back.Type;
        _o.X = _back.X; _o.Y = _back.Y; _o.Heading = _back.H;
        _o.Pieces.Clear(); foreach (var kv in _back.Pieces) _o.Pieces[kv.Key] = kv.Value;
        _o.PointMoves.Clear(); foreach (var kv in _back.Points) _o.PointMoves[kv.Key] = kv.Value;
        _o.PiecesDirty = _back.Dirty; _o.PieceVersion = _back.Ver + 1000;
    }
}
