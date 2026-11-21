using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Database;

namespace ModelEditor;

/// <summary>Small windows of the database editing: choose a record, a number, a slot, the scope of a list change, a text report.</summary>
public static class DbDialogs
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static Window Win(Window? owner, string title, double w, double h) => new()
    {
        Title = title, Owner = owner, Width = w, Height = h, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
    };

    private static Button Btn(string t, bool def = false) => new() { Content = t, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), IsDefault = def, MinWidth = 80 };

    public sealed record PickRow(int Num, string Name, string Info, DbRecord R);

    /// <summary>Choose one record from a table with column headers (click a header to sort; search by name or number).</summary>
    public static DbRecord? Pick(Window? owner, string title, BmsDatabase d, IEnumerable<DbRecord> items, Func<DbRecord, string>? extra = null)
    {
        var all = items.Select(r => new PickRow(r.Num, d.DisplayName(r), extra?.Invoke(r) ?? "", r))
                       .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Num).ToList();
        var w = Win(owner, title, 640, 640);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brushes.Gainsboro, CanUserSortColumns = true,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding(nameof(PickRow.Num)), Width = new DataGridLength(60) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(PickRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        if (extra != null) grid.Columns.Add(new DataGridTextColumn { Header = "Info", Binding = new System.Windows.Data.Binding(nameof(PickRow.Info)), Width = new DataGridLength(150) });
        var ok = Btn("OK", true); var cancel = Btn("Cancel"); cancel.IsCancel = true;
        DbRecord? res = null;
        void Fill()
        {
            string q = search.Text.Trim();
            grid.ItemsSource = all.Where(r => q.Length == 0 || r.Num.ToString(Ci) == q || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        search.TextChanged += (_, _) => Fill();
        void Done() { if (grid.SelectedItem is PickRow r) { res = r.R; w.DialogResult = true; } }
        ok.Click += (_, _) => Done();
        grid.MouseDoubleClick += (_, _) => Done();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(10) };
        var lab = new TextBlock { Text = "Search (name or number) – click a column header to sort", Foreground = Brushes.DimGray };
        DockPanel.SetDock(lab, Dock.Top); root.Children.Add(lab);
        DockPanel.SetDock(search, Dock.Top); root.Children.Add(search);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(grid);
        w.Content = root;
        Fill();
        w.Loaded += (_, _) => search.Focus();
        return w.ShowDialog() == true ? res : null;
    }

    /// <summary>Ask a whole number.</summary>
    public static int? Number(Window? owner, string title, string prompt, int value, int min, int max)
    {
        var w = Win(owner, title, 420, 170);
        var tb = new TextBox { Text = value.ToString(Ci), Margin = new Thickness(0, 6, 0, 0) };
        var ok = Btn("OK", true); var cancel = Btn("Cancel"); cancel.IsCancel = true;
        int? res = null;
        ok.Click += (_, _) =>
        {
            if (int.TryParse(tb.Text.Trim(), NumberStyles.Integer, Ci, out int n) && n >= min && n <= max) { res = n; w.DialogResult = true; }
            else MessageBox.Show(w, $"A whole number from {min} to {max}.", title);
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(tb); sp.Children.Add(bar);
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? res : null;
    }

    public sealed record SlotChoice(bool Hardpoint, int Idx, int Rounds, bool Visible, bool Rack);

    /// <summary>What goes in a vehicle slot: a hardpoint list, a weapon with its rounds, or nothing.</summary>
    public static SlotChoice? Slot(Window? owner, BmsDatabase d, DbRecord vcd, int slot)
    {
        var cur = d.Slots(vcd).FirstOrDefault(s => s.Slot == slot);
        bool hasCur = cur.Idx > 0;
        var w = Win(owner, $"{vcd.Name} – slot {slot}", 520, 330);
        var hp = new RadioButton { Content = "Hardpoint list (the weapons it can carry are in the list)", IsChecked = !hasCur || cur.Hardpoint, Margin = new Thickness(0, 2, 0, 2) };
        var wp = new RadioButton { Content = "Weapon (fixed: gun, pod…)", IsChecked = hasCur && !cur.Hardpoint, Margin = new Thickness(0, 2, 0, 2) };
        var none = new RadioButton { Content = "Empty", Margin = new Thickness(0, 2, 0, 2) };
        int idx = hasCur ? cur.Idx : 0;
        var what = new TextBlock { Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        var choose = new Button { Content = "Choose…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 6, 0, 0) };
        var rounds = new TextBox { Width = 60, Text = hasCur && !cur.Hardpoint ? cur.Count.ToString(Ci) : "1" };
        var vis = new CheckBox { Content = "Visible", IsChecked = hasCur ? cur.Visible : true, Margin = new Thickness(0, 0, 14, 0) };
        var rack = new CheckBox { Content = "Rack", IsChecked = hasCur ? cur.Rack : true };
        void Show()
        {
            var r = idx > 0 ? d.Get(hp.IsChecked == true ? "WLD" : "WCD", idx) : null;
            what.Text = none.IsChecked == true ? "(nothing)" : r != null ? $"{r.Num}  {d.DisplayName(r)}" : "(choose one)";
            choose.IsEnabled = rounds.IsEnabled = none.IsChecked != true;
            rounds.IsEnabled = wp.IsChecked == true;
        }
        hp.Checked += (_, _) => { if (!(hasCur && cur.Hardpoint)) idx = 0; Show(); };
        wp.Checked += (_, _) => { if (!(hasCur && !cur.Hardpoint)) idx = 0; Show(); };
        none.Checked += (_, _) => Show();
        choose.Click += (_, _) =>
        {
            var r = hp.IsChecked == true
                ? Pick(w, "Hardpoint list", d, d.All("WLD").Where(x => x.Num > 0), x => $"({d.ListWeapons(x).Count} weapons)")
                : Pick(w, "Weapon", d, d.All("WCD").Where(x => x.Num > 0));
            if (r != null) { idx = r.Num; Show(); }
        };
        var ok = Btn("OK", true); var cancel = Btn("Cancel"); cancel.IsCancel = true;
        SlotChoice? res = null;
        ok.Click += (_, _) =>
        {
            if (none.IsChecked == true) { res = new SlotChoice(false, 0, 0, false, false); w.DialogResult = true; return; }
            if (idx <= 0) { MessageBox.Show(w, "Choose the list or the weapon first.", w.Title); return; }
            if (!int.TryParse(rounds.Text.Trim(), NumberStyles.Integer, Ci, out int n) || n < 1 || n > 254) { MessageBox.Show(w, "Rounds: from 1 to 254.", w.Title); return; }
            res = new SlotChoice(hp.IsChecked == true, idx, n, vis.IsChecked == true, rack.IsChecked == true);
            w.DialogResult = true;
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(hp); sp.Children.Add(wp); sp.Children.Add(none);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(what); row.Children.Add(choose);
        sp.Children.Add(row);
        var r2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        r2.Children.Add(new TextBlock { Text = "Rounds (weapon only) ", VerticalAlignment = VerticalAlignment.Center });
        r2.Children.Add(rounds);
        r2.Children.Add(new TextBlock { Text = "      " });
        r2.Children.Add(vis); r2.Children.Add(rack);
        sp.Children.Add(r2);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        sp.Children.Add(bar);
        w.Content = sp;
        Show();
        return w.ShowDialog() == true ? res : null;
    }

    public enum Scope { Cancel, All, Copy }

    /// <summary>A hardpoint list used by more vehicles: change it for all of them, or make a copy for one vehicle only.</summary>
    public static Scope ListScope(Window? owner, BmsDatabase d, DbRecord wld, List<DbRecord> users, DbRecord? ctx)
    {
        var w = Win(owner, $"Hardpoint list \"{wld.Name}\"", 600, 440);
        var res = Scope.Cancel;
        var sp = new DockPanel { Margin = new Thickness(12) };
        var head = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = $"This list is used by {users.Count} vehicle(s). A change of the list changes the weapons of all of them:",
        };
        DockPanel.SetDock(head, Dock.Top); sp.Children.Add(head);
        var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var all = Btn($"Change it for all {users.Count}");
        all.Click += (_, _) => { res = Scope.All; w.DialogResult = true; };
        bar.Children.Add(all);
        if (ctx != null)
        {
            var copy = Btn($"Make a copy for {ctx.Name} only", true);
            copy.Click += (_, _) => { res = Scope.Copy; w.DialogResult = true; };
            bar.Children.Add(copy);
        }
        var cancel = Btn("Cancel"); cancel.IsCancel = true;
        bar.Children.Add(cancel);
        DockPanel.SetDock(bar, Dock.Bottom); sp.Children.Add(bar);
        if (ctx == null)
        {
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0),
                Text = "To change it for one vehicle only, open the list from that vehicle (its slots table): then 'Make a copy' appears." };
            DockPanel.SetDock(hint, Dock.Bottom); sp.Children.Add(hint);
        }
        var lb = new ListBox();
        foreach (var v in users)
        {
            var units = DbOps.UnitsWith(d, v);
            lb.Items.Add($"{v.Name}   –   in {units.Count} unit type(s)" + (units.Count > 0 ? ": " + string.Join(", ", units.Take(6).Select(d.DisplayName)) + (units.Count > 6 ? " …" : "") : ""));
        }
        sp.Children.Add(lb);
        w.Content = sp;
        w.ShowDialog();
        return res;
    }

    /// <summary>A long text to read (report, log), with OK.</summary>
    public static void Report(Window? owner, string title, string text)
    {
        var w = Win(owner, title, 760, 520);
        var tb = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        var ok = Btn("OK", true); ok.IsCancel = true; ok.Click += (_, _) => w.Close();
        var dp = new DockPanel { Margin = new Thickness(10) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(ok);
        DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
        dp.Children.Add(tb);
        w.Content = dp;
        w.ShowDialog();
    }

    /// <summary>Ask a text (a name); null = cancelled.</summary>
    public static string? Text(Window? owner, string title, string prompt, string value, int maxLength)
    {
        var w = Win(owner, title, 480, 200);
        var tb = new TextBox { Text = value, MaxLength = maxLength, Margin = new Thickness(0, 6, 0, 0) };
        var ok = Btn("OK", true); var cancel = Btn("Cancel"); cancel.IsCancel = true;
        string? res = null;
        ok.Click += (_, _) => { if (tb.Text.Trim().Length > 0) { res = tb.Text.Trim(); w.DialogResult = true; } };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(tb); sp.Children.Add(bar);
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? res : null;
    }

    /// <summary>Choose an item of a list of texts (history versions, backups); returns its index or -1.</summary>
    public static int Choose(Window? owner, string title, string note, IList<string> items, string okText)
    {
        var w = Win(owner, title, 760, 480);
        var lb = new ListBox { FontFamily = new FontFamily("Consolas") };
        foreach (var i in items) lb.Items.Add(i);
        var ok = Btn(okText, true); var cancel = Btn("Close"); cancel.IsCancel = true;
        int res = -1;
        ok.Click += (_, _) => { if (lb.SelectedIndex >= 0) { res = lb.SelectedIndex; w.DialogResult = true; } };
        var dp = new DockPanel { Margin = new Thickness(10) };
        var t = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(t, Dock.Top); dp.Children.Add(t);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        DockPanel.SetDock(bar, Dock.Bottom); dp.Children.Add(bar);
        dp.Children.Add(lb);
        w.Content = dp;
        return w.ShowDialog() == true ? res : -1;
    }
}
