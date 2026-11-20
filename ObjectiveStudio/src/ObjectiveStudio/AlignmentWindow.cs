using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TerrainTool.Core.Editor;

namespace ObjectiveStudio;

/// <summary>
/// Airbase alignment report (from the objective editor): every airbase and airstrip with the distance of its main runway
/// from the real one, worst first, in red / yellow / green. Click a row = show it in the editor; the editor's edits are
/// read live (Refresh). Read only, except "Align to real runway" on the selected row, which is an ordinary editor edit.
/// </summary>
public sealed class AlignmentWindow : Window
{
    private readonly EditorData _d;
    private readonly ObjectiveEditorWindow _editor;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly CheckBox _onlyProblems = new() { Content = "Only bases to check", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button _align = new() { Content = "Align to real runway", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "Turns and moves the selected base so its main runway lies on the real one (as the button of the editor). Then Save edits in the editor." };
    private List<AirbaseAlignment.Row> _rows = new();

    public AlignmentWindow(EditorData d, ObjectiveEditorWindow editor)
    {
        _d = d; _editor = editor;
        Title = "Airbase alignment – main runway against the real one";
        Width = 1250; Height = 720; MinWidth = 700; MinHeight = 400;

        static Style Right() { var s = new Style(typeof(TextBlock)); s.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)); return s; }
        void Col(string head, string path, double w, string? fmt = null, bool right = false, string? tip = null)
        {
            var b = new Binding(path);
            if (fmt != null) b.StringFormat = fmt;
            var c = new DataGridTextColumn { Header = tip == null ? head : new TextBlock { Text = head, ToolTip = tip }, Binding = b, Width = new DataGridLength(w) };
            if (right) c.ElementStyle = Right();
            _grid.Columns.Add(c);
        }
        Col("Status", nameof(AirbaseAlignment.Row.StatusText), 62);
        Col("Id", nameof(AirbaseAlignment.Row.Id), 50, right: true);
        Col("Name", nameof(AirbaseAlignment.Row.Name), 250);
        Col("Type", nameof(AirbaseAlignment.Row.Type), 70);
        Col("Centre m", nameof(AirbaseAlignment.Row.CentreM), 70, "0", true, "Distance between the centre of the layout's main runway and the centre of the real runway");
        Col("Along m", nameof(AirbaseAlignment.Row.AlongM), 64, "+0;-0;0", true, "Part of the offset along the real runway");
        Col("Across m", nameof(AirbaseAlignment.Row.AcrossM), 66, "+0;-0;0", true, "Part of the offset across the real runway (sideways): the one that shows most on the photo");
        Col("Angle °", nameof(AirbaseAlignment.Row.AngleDeg), 60, "+0.00;-0.00;0.00", true, "How much the layout should turn to match the real runway (positive = clockwise)");
        Col("Layout m", nameof(AirbaseAlignment.Row.LayoutLenM), 66, "0", true, "Length of the layout's main runway");
        Col("Real m", nameof(AirbaseAlignment.Row.RealLenM), 60, "0", true, "Length of the real runway");
        Col("Ends m", nameof(AirbaseAlignment.Row.EndsM), 60, "0", true, "Largest distance between a runway end of the layout and the real one (includes the difference in length)");
        Col("Source", nameof(AirbaseAlignment.Row.Source), 150);
        Col("Note", nameof(AirbaseAlignment.Row.Note), 420);

        var rowStyle = new Style(typeof(DataGridRow));
        void Tint(string status, Color bg, Color fg)
        {
            var t = new DataTrigger { Binding = new Binding(nameof(AirbaseAlignment.Row.StatusText)), Value = status };
            t.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(bg)));
            t.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(fg)));
            rowStyle.Triggers.Add(t);
        }
        Tint("OFF", Color.FromRgb(0xFD, 0xE2, 0xDF), Color.FromRgb(0xB0, 0x1E, 0x14));
        Tint("check", Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0x7A, 0x52, 0x00));
        Tint("OK", Color.FromRgb(0xE8, 0xF6, 0xEC), Color.FromRgb(0x1D, 0x6B, 0x36));
        Tint("no data", Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0x70, 0x70, 0x70));
        _grid.RowStyle = rowStyle;

        var refresh = new Button { Content = "Refresh", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Reads the bases again as they are now in the editor (also edits not saved yet)" };
        var export = new Button { Content = "Export CSV…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 14, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_onlyProblems);
        bar.Children.Add(refresh);
        bar.Children.Add(_align);
        bar.Children.Add(export);
        bar.Children.Add(_summary);

        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0),
            Text = $"Red OFF = main runway more than {AirbaseAlignment.BadM:0} m or {AirbaseAlignment.BadDeg:0.#}° from the real one; yellow check = more than {AirbaseAlignment.WarnM:0} m or {AirbaseAlignment.WarnDeg:0.#}°; green OK = closer. " +
                   "Grey = no real runway (airbase_targets.csv) or no runway in the layout. The real runways come from OurAirports and OpenStreetMap: the photo can still differ by 10–20 m. " +
                   "A long layout on a short real runway (or the opposite) gives large 'Ends' even when the base is centred: that is the layout, not the position. " +
                   "Click a row to see the base in the editor; fix it there (drag, Ctrl+drag, 'Align to real runway'), then Refresh here and Save edits in the editor.",
        };

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        DockPanel.SetDock(help, Dock.Bottom); root.Children.Add(help);
        root.Children.Add(_grid);
        Content = root;

        _onlyProblems.Checked += (_, _) => Fill();
        _onlyProblems.Unchecked += (_, _) => Fill();
        refresh.Click += (_, _) => Fill();
        _grid.SelectionChanged += (_, _) =>
        {
            _align.IsEnabled = _grid.SelectedItem is AirbaseAlignment.Row r && r.Objective.Real != null && r.LayoutLenM != null;
            if (_grid.SelectedItem is AirbaseAlignment.Row row) _editor.ShowObjective(row.Objective);
        };
        _grid.MouseDoubleClick += (_, _) => { if (_grid.SelectedItem is AirbaseAlignment.Row row) { _editor.ShowObjective(row.Objective); _editor.Activate(); } };
        _align.Click += (_, _) =>
        {
            if (_grid.SelectedItem is not AirbaseAlignment.Row row) return;
            if (_editor.AlignToReal(row.Objective)) { _status.Text = $"{row.Name}: aligned to the real runway in the editor (not saved yet: Save edits in the editor)."; Fill(row.Objective); }
            else _status.Text = $"{row.Name}: no real runway or no runway in the layout.";
        };
        export.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "airbase_alignment.csv",
                InitialDirectory = Directory.Exists(_d.Project.ProjectFolder) ? _d.Project.ProjectFolder : null };
            if (dlg.ShowDialog(this) != true) return;
            try { File.WriteAllText(dlg.FileName, AirbaseAlignment.ToCsv(AirbaseAlignment.Build(_d)), new System.Text.UTF8Encoding(true)); _status.Text = "Written " + dlg.FileName; }
            catch (Exception ex) { _status.Text = "Not written: " + ex.Message; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.F5) { Fill(); e.Handled = true; } };
        Fill();
    }

    private void Fill(EdObjective? keep = null)
    {
        keep ??= (_grid.SelectedItem as AirbaseAlignment.Row)?.Objective;
        _rows = AirbaseAlignment.Build(_d);
        int bad = _rows.Count(r => r.Status == AirbaseAlignment.Level.Bad), warn = _rows.Count(r => r.Status == AirbaseAlignment.Level.Warn),
            ok = _rows.Count(r => r.Status == AirbaseAlignment.Level.Ok), none = _rows.Count(r => r.Status == AirbaseAlignment.Level.NoData);
        _summary.Text = $"{_rows.Count} airbases:  {bad} off,  {warn} to check,  {ok} OK,  {none} without data";
        var shown = _onlyProblems.IsChecked == true ? _rows.Where(r => r.Status is AirbaseAlignment.Level.Bad or AirbaseAlignment.Level.Warn).ToList() : _rows;
        _grid.ItemsSource = shown;
        if (keep != null && shown.FirstOrDefault(r => r.Objective == keep) is { } k) { _grid.SelectedItem = k; _grid.ScrollIntoView(k); }
        _align.IsEnabled = _grid.SelectedItem is AirbaseAlignment.Row s && s.Objective.Real != null && s.LayoutLenM != null;
        if (_rows.Count > 0 && none == _rows.Count)
            _status.Text = "No base has a real runway: run 'Real runways from OpenStreetMap' in the main window (TerrainTool projects), or fill airbase_targets.csv.";
    }
}
