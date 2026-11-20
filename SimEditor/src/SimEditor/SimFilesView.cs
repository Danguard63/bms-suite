using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TerrainTool.Core.Sim;

namespace SimEditor;

/// <summary>
/// "Sim files" tab: the BMS Sim files (missiles, radars, vehicles, bombs) as tables of values with their names and
/// explanations, next to BMS's value. BMS's files are only read: the first change is saved as the project's copy in
/// SimOverrides (Build theater puts it into the theater's NT\Sim); "Reset to BMS" moves the copy away.
/// </summary>
public sealed class SimFilesView : DockPanel
{
    public sealed class Row : INotifyPropertyChanged
    {
        private readonly SimFilesView _v;
        public SimTextFile.Item Item { get; }
        public string Section => Item.Section;
        public string Parameter => Item.Label;
        public string Bms { get; }
        public string Explain { get; }
        public string When { get; }
        public Row(SimFilesView v, SimTextFile.Item item, string bms, string explain, string when) { _v = v; Item = item; Bms = bms; Explain = explain; When = when; }
        public string Value
        {
            get => Item.Value;
            set
            {
                if (value == Item.Value) return;
                string? err = _v._file!.SetValue(Item, value.Trim());
                if (err != null) { _v.Status(err, true); }
                else _v.MarkDirty();
                Changed();
            }
        }
        /// <summary>ok | changed (differs from BMS) | moved (BMS changed it after the copy) | new (not in BMS).</summary>
        public string State => When.Length > 0 ? "moved" : Bms == "—" ? "new" : Bms != Item.Value && !_v.SameAsBms(Item) ? "changed" : "ok";
        public void Changed() { PropertyChanged?.Invoke(this, new(nameof(Value))); PropertyChanged?.Invoke(this, new(nameof(State))); }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly SimLibrary _lib;
    private readonly Action<string> _log;
    private readonly ComboBox _folder = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Search by file name" };
    private readonly ListBox _files = new();
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _info = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _save = new() { Content = "Save in SimOverrides", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _reset = new() { Content = "Reset to BMS…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _onlyChanged = new() { Content = "Only values different from BMS", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _rowFilter = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Show only the values whose section or name contains this text (e.g. gear, cl_table, Rmax, Rangeto)" };
    private readonly CheckBox _textView = new() { Content = "Text view", VerticalAlignment = VerticalAlignment.Center, ToolTip = "The file as text, as Notepad shows it (also for the .lst lists)" };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HorizontalGridLinesBrush = Brushes.Gainsboro, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBox _text = new()
    {
        FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed,
    };
    private List<SimLibrary.Entry> _entries = new();
    private SimLibrary.Entry? _cur;
    internal SimTextFile? _file;
    private SimTextFile? _bms, _base;
    private List<Row> _rows = new();
    private bool _dirty, _switching;

    public bool Dirty => _dirty;

    public SimFilesView(SimLibrary lib, Action<string> log)
    {
        _lib = lib; _log = log;
        Margin = new Thickness(10);
        foreach (var f in SimLibrary.Folders) _folder.Items.Add(f);
        _folder.Items.Add("Changed in this theater");
        _folder.SelectedIndex = 0;

        var left = new DockPanel { Width = 270, Margin = new Thickness(0, 0, 10, 0) };
        var lt = new TextBlock { Text = "BMS Data\\Sim", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var legend = new TextBlock { Text = "● = this theater has its own copy (SimOverrides)", Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(lt, Dock.Top); left.Children.Add(lt);
        DockPanel.SetDock(_folder, Dock.Top); left.Children.Add(_folder);
        DockPanel.SetDock(_search, Dock.Top); left.Children.Add(_search);
        DockPanel.SetDock(legend, Dock.Bottom); left.Children.Add(legend);
        left.Children.Add(_files);

        void Col(string h, string path, double w, bool edit = false, bool star = false)
        {
            var c = new DataGridTextColumn { Header = h, Binding = new Binding(path) { Mode = edit ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                                             Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w), IsReadOnly = !edit, MinWidth = 40 };
            var es = new Style(typeof(TextBlock));
            es.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            if (path is nameof(Row.Value) or nameof(Row.Bms)) es.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Consolas")));
            c.ElementStyle = es;
            _grid.Columns.Add(c);
        }
        Col("Section", nameof(Row.Section), 220);
        Col("Parameter", nameof(Row.Parameter), 230);
        Col("Value (edit here)", nameof(Row.Value), 260, edit: true);
        Col("BMS", nameof(Row.Bms), 200);
        Col("Explanation", nameof(Row.Explain), 300, star: true);
        var rs = new Style(typeof(DataGridRow));
        void Tint(string st, Color bg, Color fg, string? tipPath = null)
        {
            var t = new DataTrigger { Binding = new Binding(nameof(Row.State)), Value = st };
            t.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(bg)));
            t.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(fg)));
            if (tipPath != null) t.Setters.Add(new Setter(ToolTipProperty, new Binding(tipPath)));
            rs.Triggers.Add(t);
        }
        Tint("changed", Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0x5A, 0x40, 0x00));
        Tint("moved", Color.FromRgb(0xFD, 0xE2, 0xDF), Color.FromRgb(0xB0, 0x1E, 0x14), nameof(Row.When));
        Tint("new", Color.FromRgb(0xE6, 0xF0, 0xFB), Color.FromRgb(0x1A, 0x4C, 0x80));
        _grid.RowStyle = rs;

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_save); bar.Children.Add(_reset); bar.Children.Add(_onlyChanged);
        bar.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(_rowFilter); bar.Children.Add(_textView);
        var legend2 = new TextBlock
        {
            Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Yellow = different from BMS (your change). Red = BMS changed this value after it was copied into this theater (BMS update): point at the row to see both. " +
                   "Blue = not in BMS's file. A table row keeps its number of values. BMS's files are never written: the first save makes the theater's copy in SimOverrides; " +
                   "Build theater (TerrainTool) puts it into the theater, then Install theater Sim.",
        };
        var right = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top); right.Children.Add(_title);
        DockPanel.SetDock(_info, Dock.Top); right.Children.Add(_info);
        DockPanel.SetDock(bar, Dock.Top); right.Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom); right.Children.Add(_status);
        DockPanel.SetDock(legend2, Dock.Bottom); right.Children.Add(legend2);
        var stack = new Grid();
        stack.Children.Add(_grid); stack.Children.Add(_text);
        right.Children.Add(stack);

        DockPanel.SetDock(left, Dock.Left); Children.Add(left);
        Children.Add(right);

        _folder.SelectionChanged += (_, _) => FillList();
        _search.TextChanged += (_, _) => FillList();
        _files.SelectionChanged += (_, _) => { if (!_switching) OpenSelected(); };
        _onlyChanged.Click += (_, _) => FillGrid();
        _rowFilter.TextChanged += (_, _) => FillGrid();
        _textView.Click += (_, _) => ToggleText();
        _text.TextChanged += (_, _) => { if (!_switching && _textView.IsChecked == true) MarkDirty(); };
        _save.Click += (_, _) => Save();
        _reset.Click += (_, _) => Reset();
        _grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { _grid.CommitEdit(); Save(); e.Handled = true; } };
        _save.IsEnabled = _reset.IsEnabled = false;
        FillList();
        Status("Choose a file on the left: missiles (Misdata), radars (Radar), aircraft (Acdata, flight models), vehicles (Vehdef), bombs (Bombdata).", false);
    }

    internal void Status(string s, bool warn) { _status.Text = s; _status.Foreground = warn ? Brushes.Firebrick : Brushes.DimGray; }

    internal void MarkDirty()
    {
        _dirty = true; _save.IsEnabled = true;
        if (_cur != null) _title.Text = _cur.Name + "  *";
    }

    internal bool SameAsBms(SimTextFile.Item it) => _bms?.Find(it) is { } b && SimTextFile.SameValue(it, b);

    private void FillList()
    {
        string? keep = _cur?.Rel;
        string q = _search.Text.Trim();
        if (_folder.SelectedIndex < SimLibrary.Folders.Length) _entries = _lib.List(SimLibrary.Folders[_folder.SelectedIndex]);
        else _entries = SimLibrary.Folders.SelectMany(f => _lib.List(f)).Where(e => e.Changed).ToList();
        _switching = true;
        try
        {
            _files.Items.Clear();
            foreach (var e in _entries.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var it = new ListBoxItem
                {
                    Content = (e.Changed ? "● " : "    ") + (_folder.SelectedIndex >= SimLibrary.Folders.Length ? e.Rel : e.Name),
                    Tag = e, FontWeight = e.Changed ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = e.Changed ? (e.InBms ? "This theater has its own copy (SimOverrides)" : "Only in this theater (not in BMS)") : "BMS's file (read only until you change it)",
                };
                _files.Items.Add(it);
                if (keep != null && string.Equals(e.Rel, keep, StringComparison.OrdinalIgnoreCase)) _files.SelectedItem = it;
            }
        }
        finally { _switching = false; }
    }

    /// <summary>True when the current file can be left (saved, or the user does not mind losing the changes).</summary>
    public bool AskLeave()
    {
        if (!_dirty || _cur == null) return true;
        var r = MessageBox.Show(Window.GetWindow(this), $"{_cur.Name} has changes that are not saved. Save them in SimOverrides?", "Sim files", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save();
        _dirty = false;
        return true;
    }

    private void OpenSelected()
    {
        if (_files.SelectedItem is not ListBoxItem { Tag: SimLibrary.Entry e }) return;
        if (_cur != null && e.Rel == _cur.Rel) return;
        if (!AskLeave())
        {
            _switching = true;
            _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as SimLibrary.Entry)?.Rel == _cur?.Rel);
            _switching = false;
            return;
        }
        Open(e);
    }

    private void Open(SimLibrary.Entry e)
    {
        _cur = e; _dirty = false; _save.IsEnabled = false;
        string path = _lib.Current(e.Rel);
        try
        {
            _file = SimTextFile.Load(path);
            _bms = File.Exists(_lib.BmsPath(e.Rel)) ? SimTextFile.Load(_lib.BmsPath(e.Rel)) : null;
            _base = File.Exists(_lib.BaselinePath(e.Rel)) ? SimTextFile.Load(_lib.BaselinePath(e.Rel)) : null;
        }
        catch (Exception ex) { Status($"{e.Name}: {ex.Message}", true); return; }
        _title.Text = e.Name;
        bool own = File.Exists(_lib.OverridePath(e.Rel));
        _info.Text = (own ? $"This theater's copy: {_lib.OverridePath(e.Rel)}" : $"BMS's file (read only): {_lib.BmsPath(e.Rel)} – your first change is saved as this theater's copy.")
                     + (_file.Header.Length > 0 ? "\n" + _file.Header.Replace("\n", " · ") : "");
        _reset.IsEnabled = own;
        bool list = Path.GetExtension(e.Name).Equals(".lst", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(e.Name).Equals(".txt", StringComparison.OrdinalIgnoreCase) || _file.Items.Count == 0;
        _switching = true;
        _textView.IsChecked = list;
        _textView.IsEnabled = !list;
        _text.Text = _file.Text();
        _switching = false;
        ShowView();
        FillGrid();
        int moved = _rows.Count(r => r.State == "moved"), changed = _rows.Count(r => r.State == "changed");
        Status($"{_file.Items.Count} values" + (own ? $"; {changed} different from BMS" : "") + (moved > 0 ? $"; {moved} changed by a BMS update after the copy (red): check them" : ""), moved > 0);
    }

    private void FillGrid()
    {
        if (_file == null) return;
        string folder = _cur?.Folder ?? "";
        _rows = _file.Items.Select(it =>
        {
            var b = _bms?.Find(it);
            var o = _base?.Find(it);
            string when = b != null && o != null && !SimTextFile.SameValue(b, o)
                ? $"BMS now: {b.Value}\nBMS when this theater's copy was made: {o.Value}" : "";
            string exp = SimDocs.Describe(folder, it);
            if (it.Note.Length > 0 && !exp.Contains(it.Note, StringComparison.OrdinalIgnoreCase)) exp = exp.Length > 0 ? exp + " — " + it.Note : it.Note;
            return new Row(this, it, b?.Value ?? "—", exp, when);
        }).ToList();
        string q = _rowFilter.Text.Trim();
        _grid.ItemsSource = _rows.Where(r => (_onlyChanged.IsChecked != true || r.State != "ok")
                                             && (q.Length == 0 || r.Section.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Parameter.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                 .ToList();
    }

    private void ShowView()
    {
        bool text = _textView.IsChecked == true;
        _text.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        _grid.Visibility = text ? Visibility.Collapsed : Visibility.Visible;
        _onlyChanged.IsEnabled = !text;
    }

    private void ToggleText()
    {
        if (_file == null) return;
        if (_textView.IsChecked == true) { _grid.CommitEdit(); _switching = true; _text.Text = _file.Text(); _switching = false; }
        else
        {
            // back to the table: the text is read again
            _file = SimTextFile.FromText(_text.Text);
            FillGrid();
        }
        ShowView();
    }

    private bool Save()
    {
        if (_cur == null || _file == null) return false;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_textView.IsChecked == true) _file = SimTextFile.FromText(_text.Text);
        try
        {
            bool first = !File.Exists(_lib.OverridePath(_cur.Rel));
            _lib.SaveOverride(_cur.Rel, _file);
            _dirty = false; _save.IsEnabled = false; _reset.IsEnabled = true;
            _title.Text = _cur.Name;
            string msg = $"{_cur.Rel} saved in SimOverrides{(first ? " (this theater's copy is made now)" : "")}. Build theater (TerrainTool) puts it into the theater, then Install theater Sim into BMS.";
            Status(msg, false); _log(msg);
            var keep = _cur;
            FillList();
            _cur = keep;
            _info.Text = $"This theater's copy: {_lib.OverridePath(_cur.Rel)}" + (_file.Header.Length > 0 ? "\n" + _file.Header.Replace("\n", " · ") : "");
            return true;
        }
        catch (Exception ex) { Status("Not saved: " + ex.Message, true); return false; }
    }

    private void Reset()
    {
        if (_cur == null || !File.Exists(_lib.OverridePath(_cur.Rel))) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Go back to BMS's {_cur.Rel}?\n\nThis theater's copy is moved to SimOverrides_Removed (not deleted).", "Reset to BMS",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            string? moved = _lib.Reset(_cur.Rel);
            _log($"{_cur.Rel}: back to BMS's file; the theater's copy is in {moved}");
            var e = _cur; _cur = null; _dirty = false;
            FillList();
            var fresh = _entries.FirstOrDefault(x => x.Rel == e.Rel);
            if (fresh != null) Open(fresh); else { _file = null; _grid.ItemsSource = null; _title.Text = ""; _info.Text = ""; }
        }
        catch (Exception ex) { Status("Not reset: " + ex.Message, true); }
    }
}
