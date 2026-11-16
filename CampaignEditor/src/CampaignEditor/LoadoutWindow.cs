using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// Carico di un volo, con le regole di BMS (LoadoutRules): piloni dell'aereo in fila come sotto l'ala, per il pilone scelto
/// le armi della sua lista WLD con il massimo, la quantita', il pilone simmetrico; peso e magazzino dello squadrone.
/// Il cannone (pilone 0) non si tocca. OK scrive il carico nel file (si annulla con Ctrl+Z nell'editor).
/// </summary>
public sealed class LoadoutWindow : Window
{
    private readonly CampaignDoc _d;
    private readonly CampaignDb _db;
    private readonly CampUnit _f;
    private readonly LoadoutRules _rules;
    private readonly List<LoadoutRules.Pylon> _pylons;
    private readonly short[][] _ids;
    private readonly byte[][] _cnt;
    private readonly byte[]? _stores;
    private readonly int _aircraft;
    private int _k;                       // carico che si sta guardando (aereo del volo)
    private int _sel;                     // pilone scelto
    private bool _filling;

    private readonly WrapPanel _strip = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ListBox _weapons = new() { Height = 260 };
    private readonly ComboBox _count = new() { Width = 60, Margin = new Thickness(6, 0, 12, 0) };
    private readonly CheckBox _mirror = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _which = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _all = new() { Content = "Same loadout for every aircraft of the flight", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pylonHead = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
    private readonly TextBlock _weight = new() { Margin = new Thickness(0, 6, 0, 2) };
    private readonly StackPanel _problems = new();
    private readonly Button _ok = new() { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };

    public Action? Undo { get; private set; }

    public LoadoutWindow(CampaignDoc d, CampaignDb db, CampUnit flight)
    {
        _d = d; _db = db; _f = flight;
        _rules = new LoadoutRules(db, flight.Type - 100);
        _pylons = _rules.Pylons();
        var uf = d.Units!;
        int n = Math.Max(1, flight.LoadoutCount);
        _ids = new short[n][]; _cnt = new byte[n][];
        for (int k = 0; k < n; k++) { var (i, c) = uf.ReadLoadout(flight, k); _ids[k] = i; _cnt[k] = c; }
        _aircraft = Math.Max(1, UnitText.Vehicles(flight.Roster));
        var sq = d.Unit(flight.Squadron);
        _stores = sq is { Kind: UnitKind.Squadron } ? uf.Stores(sq) : null;
        _sel = _pylons.FirstOrDefault()?.Index ?? 0;

        string pkg = d.Unit(flight.PackageId) is { Kind: UnitKind.Package } p ? $"Package {p.CampId} · " : "";
        Title = $"Loadout – {_rules.Aircraft}";
        Width = 1000; Height = 720; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(10) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = $"{_rules.Aircraft} × {_aircraft}", FontSize = 16, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock
        {
            Text = $"{pkg}{flight.MissionName}" + (sq != null ? $" · squadron {sq.NameId} at {d.Obj(sq.Airbase)?.Name ?? "?"}" : "") +
                   $" · gun: {(_ids[0][0] > 0 ? $"{_rules.WeaponName(_ids[0][0])} ({_cnt[0][0]})" : "none")}",
            Foreground = Brushes.DimGray,
        });
        if (n > 1)
        {
            var wr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            for (int k = 0; k < n; k++) _which.Items.Add($"Aircraft {k + 1}");
            _which.SelectedIndex = 0;
            _which.SelectionChanged += (_, _) => { _k = Math.Max(0, _which.SelectedIndex); Refresh(); };
            wr.Children.Add(new TextBlock { Text = "Loadout of ", VerticalAlignment = VerticalAlignment.Center });
            wr.Children.Add(_which); wr.Children.Add(_all);
            head.Children.Add(wr);
        }
        if (_pylons.Count == 0)
            head.Children.Add(new TextBlock { Text = "The database has no pylons for this aircraft (VCD WpnOrHpIdx / WLD).", Foreground = Brushes.Red, Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);

        // ala: i piloni in fila
        var wing = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        wing.Children.Add(new Border { Height = 6, Background = new SolidColorBrush(Color.FromRgb(120, 130, 140)), CornerRadius = new CornerRadius(3) });
        wing.Children.Add(_strip);
        DockPanel.SetDock(wing, Dock.Top); root.Children.Add(wing);

        // in basso: peso, problemi, pulsanti
        var bottom = new StackPanel();
        bottom.Children.Add(_weight);
        bottom.Children.Add(_problems);
        var clear = new Button { Content = "Clear all pylons", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        clear.Click += (_, _) => { for (int i = 1; i < 16; i++) Set(i, 0, 0); Refresh(); };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        _ok.Click += (_, _) => Apply();
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(clear);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _ok, cancel } };
        DockPanel.SetDock(right, Dock.Right); buttons.Children.Insert(0, right);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);

        // centro: armi del pilone scelto
        var mid = new DockPanel();
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        ctl.Children.Add(new TextBlock { Text = "Quantity", VerticalAlignment = VerticalAlignment.Center });
        ctl.Children.Add(_count); ctl.Children.Add(_mirror);
        DockPanel.SetDock(_pylonHead, Dock.Top); mid.Children.Add(_pylonHead);
        DockPanel.SetDock(ctl, Dock.Bottom); mid.Children.Add(ctl);
        mid.Children.Add(_weapons);
        root.Children.Add(mid);
        Content = root;

        _weapons.SelectionChanged += (_, _) => { if (!_filling) PickWeapon(); };
        _count.SelectionChanged += (_, _) => { if (!_filling && _count.SelectedItem is int c && _ids[_k][_sel] > 0) { Set(_sel, _ids[_k][_sel], (byte)c, true); Refresh(); } };
        _mirror.Click += (_, _) => { if (_mirror.IsChecked == true && _rules.Mirror(_sel) is int m && m > 0) { Set(m, _ids[_k][_sel], _cnt[_k][_sel]); Refresh(); } };
        Refresh();
    }

    /// <summary>Mette l'arma sul pilone (e sul simmetrico se richiesto), nel carico scelto o in tutti.</summary>
    private void Set(int pylon, short wcd, byte count, bool mirror = false)
    {
        var ks = _all.IsChecked == true ? Enumerable.Range(0, _ids.Length) : new[] { _k };
        int m = mirror && _mirror.IsChecked == true ? _rules.Mirror(pylon) : -1;
        foreach (int k in ks)
            foreach (int p in m > 0 ? new[] { pylon, m } : new[] { pylon })
            {
                _ids[k][p] = count == 0 ? (short)0 : wcd;
                _cnt[k][p] = wcd == 0 ? (byte)0 : count;
            }
    }

    private void PickWeapon()
    {
        if (_weapons.SelectedItem is not ListBoxItem { Tag: ValueTuple<int, int> t }) return;
        var (wcd, max) = t;
        if (wcd == 0) Set(_sel, 0, 0, true);
        else
        {
            int keep = _ids[_k][_sel] == wcd ? _cnt[_k][_sel] : max;          // un'arma nuova: pieno; la stessa: la quantita' che c'era
            Set(_sel, (short)wcd, (byte)Math.Clamp(keep, 1, max), true);
        }
        Refresh();
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void Refresh()
    {
        _filling = true;
        var ids = _ids[_k]; var cnt = _cnt[_k];
        // piloni
        _strip.Children.Clear();
        foreach (var p in _pylons)
        {
            bool loaded = ids[p.Index] > 0 && cnt[p.Index] > 0;
            var b = new Button
            {
                Width = 96, Height = 78, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(3), Tag = p.Index,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Background = p.Index == _sel ? new SolidColorBrush(Color.FromRgb(255, 236, 160)) : loaded ? new SolidColorBrush(Color.FromRgb(214, 232, 214)) : Brushes.WhiteSmoke,
                BorderBrush = p.Index == _sel ? Brushes.DarkOrange : Brushes.Gray, BorderThickness = new Thickness(p.Index == _sel ? 2 : 1),
                ToolTip = $"Pylon {p.Index} – {p.Name}",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{p.Index}", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = loaded ? Short(_rules.WeaponName(ids[p.Index]), 26) : "empty", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 11,
                                        Foreground = loaded ? Brushes.Black : Brushes.Gray },
                        new TextBlock { Text = loaded ? $"× {cnt[p.Index]}" : "", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 },
                    },
                },
            };
            b.Click += (_, _) => { _sel = (int)b.Tag; Refresh(); };
            _strip.Children.Add(b);
        }
        // armi del pilone scelto
        _weapons.Items.Clear(); _count.Items.Clear();
        var sp = _pylons.FirstOrDefault(p => p.Index == _sel);
        if (sp != null)
        {
            int mir = _rules.Mirror(_sel);
            _pylonHead.Text = $"Pylon {_sel} – {sp.Name}: weapons allowed by BMS (WLD list)";
            _mirror.Content = mir > 0 ? $"same on the symmetric pylon {mir}" : "(no symmetric pylon)";
            _mirror.IsEnabled = mir > 0;
            var empty = new ListBoxItem { Content = "— empty —", Tag = (0, 0) };
            _weapons.Items.Add(empty);
            foreach (var (wcd, max) in sp.Weapons.GroupBy(w => w.Wcd).Select(g => (g.Key, g.Max(x => x.Max))).OrderBy(w => _rules.WeaponName(w.Key)))
            {
                int st = _stores != null && wcd < _stores.Length ? _stores[wcd] : -1;
                var it = new ListBoxItem
                {
                    Tag = (wcd, max),
                    Content = $"{_rules.WeaponName(wcd)}    max {max} · {_rules.WeaponWeight(wcd):N0} lb each" + (st >= 0 ? $" · stores {st}" : ""),
                    Foreground = st == 0 ? Brushes.Gray : Brushes.Black,
                };
                _weapons.Items.Add(it);
                if (ids[_sel] == wcd) _weapons.SelectedItem = it;
            }
            if (ids[_sel] <= 0) _weapons.SelectedItem = empty;
            int cur = ids[_sel] > 0 ? _rules.MaxOn(_sel, ids[_sel]) : 0;
            for (int c = 1; c <= cur; c++) _count.Items.Add(c);
            _count.SelectedItem = ids[_sel] > 0 ? (object)(int)cnt[_sel] : null;
            _count.IsEnabled = cur > 1;
        }
        else { _pylonHead.Text = ""; _mirror.IsEnabled = false; }

        // peso e problemi (di tutti i carichi del volo)
        int wt = _rules.Weight(ids, cnt), maxPay = _rules.MaxPayload;
        _weight.Text = maxPay > 0
            ? $"Stores weight {wt:N0} lb of {maxPay:N0} lb  (max take-off {_rules.Weights.Max:N0} − empty {_rules.Weights.Empty:N0} − internal fuel {_rules.Weights.Fuel:N0})"
            : $"Stores weight {wt:N0} lb (the database gives no maximum)";
        _weight.Foreground = maxPay > 0 && wt > maxPay ? Brushes.Red : Brushes.Black;
        _problems.Children.Clear();
        bool errors = false;
        for (int k = 0; k < _ids.Length; k++)
            foreach (var (err, text) in _rules.Check(_ids[k], _cnt[k], _all.IsChecked == true || _ids.Length == 1 ? _aircraft : 1, _stores))
            {
                errors |= err;
                _problems.Children.Add(new TextBlock { Text = (_ids.Length > 1 ? $"Aircraft {k + 1}: " : "") + text, Foreground = err ? Brushes.Red : Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap });
            }
        if (_problems.Children.Count == 0) _problems.Children.Add(new TextBlock { Text = "Loadout valid for BMS.", Foreground = Brushes.DarkGreen });
        _ok.IsEnabled = !errors;
        _filling = false;
    }

    private void Apply()
    {
        var uf = _d.Units!;
        var sn = uf.TakeSnapshot();
        for (int k = 0; k < _ids.Length; k++) uf.WriteLoadout(_f, k, _ids[k], _cnt[k]);
        _d.Dirty = true;
        Undo = () => { uf.RestoreSnapshot(sn); _d.Dirty = true; };
        DialogResult = true;
    }
}
