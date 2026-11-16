using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>
/// Sezione Fonts: i font dell'interfaccia (Fonts\FontRc.irc → .bft) come li vede il teatro, con l'anteprima. Si puo' sostituire un
/// font con un TrueType (stessa altezza), aggiungerne uno nuovo, rimettere quello della KTO. Tutto va nella cartella Art\Fonts del
/// teatro: la KTO (Data\Art\Fonts) non si tocca.
/// </summary>
public sealed class FontsPanel : DockPanel
{
    public sealed record FontRow(string Name, string Rel, BftFont? Font, ArtSet.FontState State, bool Added, string[] SameFile, int Uses, string[] Files);

    private ArtSet? _art;
    private UiArt? _ui;
    private readonly ListBox _list = new() { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _sample = new() { Width = 360, Text = "Tactical Engagement 0123456789", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _replace, _add, _reset, _remove;
    private List<FontRow> _rows = new();

    public FontRow? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as FontRow;
    public event Action<FontRow?>? SelectionChanged;
    /// <summary>Dopo una modifica (per aggiornare titolo, stato e schermate).</summary>
    public event Action<string>? Changed;

    public FontsPanel()
    {
        Button B(string t, string tip, Action a) { var b = new Button { Content = t, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => a(); return b; }
        _replace = B("Replace with a TrueType font…", "Converts a Windows font (or a .ttf/.otf file) into this BMS font, for this theater only", Replace);
        _add = B("Add a new font…", "A new interface font for this theater, then usable with 'Change font…' in the visual editor", Add);
        _reset = B("Put back the KTO font", "Removes the theater's version of this font: BMS uses the KTO one again", Reset);
        _remove = B("Remove this font", "Only for fonts added to this theater", Remove);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        bar.Children.Add(_replace); bar.Children.Add(_add); bar.Children.Add(_reset); bar.Children.Add(_remove);
        bar.Children.Add(new TextBlock { Text = "   Sample ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_sample);
        SetDock(bar, Dock.Top); Children.Add(bar);
        Children.Add(_list);
        _list.SelectionChanged += (_, _) => { UpdateButtons(); SelectionChanged?.Invoke(Selected); };
        _sample.TextChanged += (_, _) => Fill(Selected?.Name);
        UpdateButtons();
    }

    public void SetArt(ArtSet? art, UiArt? ui) { _art = art; _ui = ui; Reload(); }

    public void Reload(string? select = null)
    {
        if (_art == null || _ui == null) { _rows.Clear(); _list.Items.Clear(); return; }
        _ui.Forget();
        var files = _ui.FontFiles();
        var kto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _art.FontRcBaseLines())
        {
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*\[LOADFONT\]\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) kto.Add(m.Groups[1].Value);
        }
        var use = _ui.FontUsage();
        _rows = files.OrderBy(kv => kv.Key == "DEFAULT" ? "" : kv.Key).Select(kv => new FontRow(
            kv.Key, kv.Value, _ui.Font(kv.Key), _art.StateOfFont(kv.Value), !kto.Contains(kv.Key),
            files.Where(o => o.Key != kv.Key && o.Value.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)).Select(o => o.Key).ToArray(),
            use.TryGetValue(kv.Key, out var u) ? u.Count : 0, use.TryGetValue(kv.Key, out var u2) ? u2.Files.ToArray() : Array.Empty<string>())).ToList();
        Fill(select ?? Selected?.Name);
    }

    private void Fill(string? select)
    {
        _list.Items.Clear();
        string sample = string.IsNullOrEmpty(_sample.Text) ? "Sample" : _sample.Text;
        foreach (var r in _rows)
        {
            var sp = new StackPanel { Margin = new Thickness(4) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = r.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Width = 190 });
            head.Children.Add(new TextBlock { Text = StateText(r), Foreground = StateBrush(r), Width = 210 });
            head.Children.Add(new TextBlock { Text = (r.Font != null ? $"{r.Font.Height} px · {r.Font.Face}" : "file missing") + $" · {Path.GetFileName(r.Rel)}", Foreground = Brushes.Gray, Width = 330 });
            head.Children.Add(new TextBlock { Text = r.Uses > 0 ? $"used {r.Uses}× in {r.Files.Length} window files" : r.Name == "DEFAULT" ? "used where no font is set" : "not used by the windows", Foreground = Brushes.Gray });
            sp.Children.Add(head);
            if (r.Font != null)
            {
                int w = Math.Clamp(r.Font.Width(sample) + 4, 8, 3000), h = r.Font.Height + 2;
                var c = new byte[w * h * 4];
                r.Font.Draw(c, w, h, sample, 2, 1, (230, 230, 230));
                var img = new Image { Source = ImageOps.ToBitmap(new Pic(w, h, c)), Width = w, Height = h, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                sp.Children.Add(img);
            }
            var item = new ListBoxItem { Content = sp, Tag = r };
            _list.Items.Add(item);
            if (r.Name.Equals(select, StringComparison.OrdinalIgnoreCase)) _list.SelectedItem = item;
        }
        if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
        UpdateButtons();
    }

    public static string StateText(FontRow r) => r.State switch
    {
        ArtSet.FontState.Changed => "changed (not saved)",
        ArtSet.FontState.Theater => r.Added ? "added by the theater" : "theater's version",
        ArtSet.FontState.Kto => "KTO (Data\\Art)",
        _ => "missing",
    };

    private static Brush StateBrush(FontRow r) => r.State switch
    {
        ArtSet.FontState.Changed => Brushes.Gold,
        ArtSet.FontState.Theater => VisualPanel.TheaterBrush,
        ArtSet.FontState.Kto => VisualPanel.KtoBrush,
        _ => Brushes.IndianRed,
    };

    private void UpdateButtons()
    {
        var r = Selected;
        bool open = _art != null;
        _replace.IsEnabled = open && r != null;
        _add.IsEnabled = open;
        _reset.IsEnabled = open && r != null && !r.Added && _art!.FontInKto(r.Rel) && r.State is ArtSet.FontState.Theater or ArtSet.FontState.Changed;
        _remove.IsEnabled = open && r != null && r.Added;
    }

    private Window? OwnerWin => Window.GetWindow(this);

    // ───────────────────────── azioni ─────────────────────────

    private void Replace()
    {
        if (_art == null || _ui == null || Selected is not { } r) return;
        if (r.SameFile.Length > 0 && MessageBox.Show(OwnerWin!,
                $"{Path.GetFileName(r.Rel)} is also used by {string.Join(", ", r.SameFile)}: they change too (same file).\n\nContinue?",
                "Replace font", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        if (r.State == ArtSet.FontState.Kto && MessageBox.Show(OwnerWin!,
                $"{r.Name} comes from the KTO (Data\\Art\\Fonts), the master used by every theater.\n\nThe KTO is NOT changed: the new font is written into this theater's " +
                $"Art\\Fonts\\{Path.GetFileName(r.Rel)}, which BMS uses instead of the KTO one for this theater only. You can put the KTO font back at any time.\n\nContinue?",
                "Replace font", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var d = new TtfDialog($"Replace {r.Name} with a TrueType font", r.Font, r.Name, _rows.Select(x => x.Name)) { Owner = OwnerWin };
        if (d.ShowDialog() != true || d.Result == null) return;
        _art.SetFontFile(r.Rel, d.Result);
        Done($"{r.Name}: replaced with {d.ResultFace} ({d.ResultHeight} px). Save to write it into the theater's Art\\Fonts.", r.Name);
    }

    private void Add()
    {
        if (_art == null || _ui == null) return;
        var d = new TtfDialog("Add a new interface font", null, null, _rows.Select(x => x.Name)) { Owner = OwnerWin };
        if (d.ShowDialog() != true || d.Result == null) return;
        string name = d.NewName;
        string rel = @"Fonts\" + name.ToLowerInvariant() + ".bft";
        int id = _art.AddFont(name, rel, d.Result);
        Done($"Font {name} added ({d.ResultFace}, {d.ResultHeight} px, number {id}). Save to write it; then use it with 'Change font…' in the visual editor.", name);
    }

    private void Reset()
    {
        if (_art == null || Selected is not { } r) return;
        _art.ResetFontFile(r.Rel);
        Done($"{r.Name}: the KTO font will be used again (the theater's file is removed when you save).", r.Name);
    }

    private void Remove()
    {
        if (_art == null || _ui == null || Selected is not { } r) return;
        string warn = r.Uses > 0 ? $"\n\n⚠ It is used {r.Uses} times in: {string.Join(", ", r.Files.Take(6))}{(r.Files.Length > 6 ? "…" : "")}. Those texts will fall back to the DEFAULT font: change them first in the visual editor." : "";
        if (MessageBox.Show(OwnerWin!, $"Remove the font {r.Name} from this theater?{warn}", "Remove font", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _art.RemoveFont(r.Name, r.Rel);
        Done($"Font {r.Name} removed. Save to write it.", null);
    }

    private void Done(string msg, string? select)
    {
        Reload(select);
        Changed?.Invoke(msg);
        SelectionChanged?.Invoke(Selected);
    }
}
