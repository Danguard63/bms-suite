using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Dds;

namespace ArtEditor;

/// <summary>
/// Finestra principale: a sinistra le categorie, al centro le caselle della categoria (anteprima e stato), a destra i dettagli della
/// casella scelta (dove appare in BMS, misure, anteprime del teatro / di BMS / del riferimento) e i pulsanti per cambiarla.
/// </summary>
public sealed class MainWindow : Window
{
    private ArtSet? _art;
    private string _dataDir = Settings.Get("data") ?? @"C:\Falcon BMS 4.38\Data";
    private bool _warnedBms;

    private readonly ListBox _cats = new() { Width = 230, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _catHelp = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6), Foreground = Brushes.DimGray };
    private readonly WrapPanel _cards = new() { Margin = new Thickness(4) };
    private readonly ScrollViewer _cardScroll;
    private readonly DataGrid _texts = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly StackPanel _centerTop = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
    private readonly StackPanel _details = new() { Margin = new Thickness(10) };
    private readonly TextBlock _where = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _save = new() { Content = "Save", FontWeight = FontWeights.SemiBold };
    private ArtSlot? _sel;
    private int _gen;
    private readonly VisualPanel _visual = new();
    private readonly FontsPanel _fontsPanel = new();
    private readonly SoundsPanel _soundsPanel = new();
    private UiArt? _ui;

    private static readonly Brush Dark = Fz(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)));
    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MainWindow()
    {
        Title = "BMS Art Editor";
        Width = 1500; Height = 920; MinWidth = 1000; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;

        Button B(string text, string tip, RoutedEventHandler h)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += h; return b;
        }
        _save.Padding = new Thickness(14, 3, 14, 3); _save.Margin = new Thickness(0, 0, 6, 0);
        _save.ToolTip = "Writes the changed files into the theater's art folder (each replaced file is first copied into ArtEditor\\Backup)";
        _save.Click += (_, _) => Save();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 4) };
        bar.Children.Add(B("Open…", "Open the art of a theater, of a TerrainTool project, of a folder, or start a new one", (_, _) => OpenArt()));
        bar.Children.Add(B("Reference…", "Another art to take images from: another theater, an old 4.36/4.37 add-on…", (_, _) => OpenReference()));
        bar.Children.Add(_save);
        bar.Children.Add(B("Save as a copy…", "Makes a separate copy of this art (e.g. a PvP version with some buttons hidden) with the changes not saved yet; the original is not changed",
            async (_, _) => await SaveAsCopy()));
        bar.Children.Add(B("Check", "Looks for problems: wrong sizes, missing images, patches without a number, missing texts", (_, _) => Check()));
        bar.Children.Add(B("Copy into a theater…", "Copies this art into the art folder of a theater installed in BMS (with a backup of what it replaces)", (_, _) => CopyInto()));
        bar.Children.Add(B("Open folder", "The art folder being edited", (_, _) => { if (_art != null) { Directory.CreateDirectory(_art.WorkRoot); Process.Start("explorer.exe", _art.WorkRoot); } }));
        bar.Children.Add(B("Backups", "Folder with the copies of the files replaced by Save and Copy", (_, _) => { Directory.CreateDirectory(Settings.BackupFolder); Process.Start("explorer.exe", Settings.BackupFolder); }));
        bar.Children.Add(_where);

        foreach (var c in ArtCatalog.Categories) _cats.Items.Add(c);
        _cats.SelectionChanged += (_, _) => ShowCategory();

        _cardScroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Dark };
        _texts.Columns.Add(new DataGridTextColumn { Header = "Text", Binding = new System.Windows.Data.Binding(nameof(TextRow.Label)), IsReadOnly = true, Width = 260 });
        _texts.Columns.Add(new DataGridTextColumn { Header = "Theater (edit here)", Binding = new System.Windows.Data.Binding(nameof(TextRow.Value)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(2, DataGridLengthUnitType.Star), ElementStyle = Wrap() });
        _texts.Columns.Add(new DataGridTextColumn { Header = "BMS 4.38 (Korea)", Binding = new System.Windows.Data.Binding(nameof(TextRow.Bms)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = Wrap(), Foreground = Brushes.Gray });
        _texts.Columns.Add(new DataGridTextColumn { Header = "Key", Binding = new System.Windows.Data.Binding(nameof(TextRow.Key)), IsReadOnly = true, Width = 170, Foreground = Brushes.Gray });
        _texts.CellEditEnding += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not TextRow r || e.EditingElement is not TextBox tb) return;
            _art?.SetText(r.Key, tb.Text);
            Dispatcher.BeginInvoke(new Action(UpdateTitle));
        };

        var center = new DockPanel();
        DockPanel.SetDock(_catHelp, Dock.Top); center.Children.Add(_catHelp);
        DockPanel.SetDock(_centerTop, Dock.Top); center.Children.Add(_centerTop);
        var stack = new Grid();
        stack.Children.Add(_cardScroll); stack.Children.Add(_texts); stack.Children.Add(_visual); stack.Children.Add(_fontsPanel); stack.Children.Add(_soundsPanel);
        _soundsPanel.Visibility = Visibility.Collapsed;
        _soundsPanel.SelectionChanged += ShowSoundDetails;
        _soundsPanel.Changed += msg => { _status.Text = msg; UpdateTitle(); };
        _fontsPanel.Visibility = Visibility.Collapsed;
        _fontsPanel.SelectionChanged += ShowFontDetails;
        _fontsPanel.Changed += msg => { _status.Text = msg; UpdateTitle(); };
        _visual.SelectionChanged += ShowVisualDetails;
        _visual.MenuFor = VisualMenu;
        center.Children.Add(stack);

        var detailScroll = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 400 };
        var main = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
        DockPanel.SetDock(_cats, Dock.Left); main.Children.Add(_cats);
        var detailBorder = new Border { Child = detailScroll, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1, 0, 0, 0) };
        DockPanel.SetDock(detailBorder, Dock.Right); main.Children.Add(detailBorder);
        main.Children.Add(center);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var sb = new Border { Child = _status, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(sb, Dock.Bottom); root.Children.Add(sb);
        root.Children.Add(main);
        Content = root;

        Loaded += (_, _) =>
        {
            string? last = Settings.Get("work");
            if (last != null && Directory.Exists(last)) Load(last, Settings.Get("label") ?? Path.GetFileName(last));
            else OpenArt();
        };
        Closing += (_, e) =>
        {
            if (_art?.IsDirty != true) return;
            var r = MessageBox.Show(this, "There are changes not saved. Save them now?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
        ShowEmpty();
    }

    private static Style Wrap()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        return s;
    }

    public sealed class TextRow
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Value { get; set; } = "";
        public string Bms { get; init; } = "";
    }

    // ───────────────────────── apertura ─────────────────────────

    private void OpenArt()
    {
        if (_art?.IsDirty == true && MessageBox.Show(this, "The changes not saved will be lost. Continue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var w = new OpenWindow(false) { Owner = this };
        if (w.ShowDialog() != true || w.Root == null) return;
        _dataDir = w.DataDir;
        Load(w.Root, w.Label);
    }

    private void Load(string root, string label)
    {
        string baseArt = Path.Combine(_dataDir, "Art");
        if (!Directory.Exists(baseArt))
        {
            MessageBox.Show(this, $"BMS art not found: {baseArt}\nChoose the BMS Data folder in Open….", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowEmpty(); return;
        }
        // la KTO (Data\Art) e' il master di tutti i teatri: non si apre mai per modificarla
        if (Path.GetFullPath(ArtSet.ArtRoot(root)).TrimEnd('\\').Equals(Path.GetFullPath(baseArt).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"{root} is the KTO art (Data\\Art), the master used by every theater: it is never changed by the editor.\n" +
                "Use 'New theater art…' to start a theater art from it, or open the art folder of a theater.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowEmpty(); return;
        }
        _art = new ArtSet(baseArt, root, label);
        _ui = new UiArt(_art);
        _visual.SetArt(Category == ArtCatalog.Visual ? _ui : null);
        _warnedBms = false;
        Settings.Set("work", root); Settings.Set("label", label);
        UpdateTitle();
        if (_cats.SelectedIndex < 0) _cats.SelectedIndex = 0; else ShowCategory();
        _status.Text = $"Opened {label}: {root}. Files the theater does not have come from BMS ({baseArt}).";
    }

    private void OpenReference()
    {
        if (_art == null) return;
        var w = new OpenWindow(true) { Owner = this };
        if (w.ShowDialog() != true || w.Root == null) return;
        _art.SetReference(w.Root);
        Settings.Set("reference", w.Root);
        UpdateTitle();
        _status.Text = $"Reference: {w.Root}. Choose an image and press 'Take from reference'.";
        ShowCategory();
    }

    private void UpdateTitle()
    {
        if (_art == null) { _where.Text = ""; Title = "BMS Art Editor"; return; }
        Title = $"BMS Art Editor · {_art.Label}{(_art.IsDirty ? " *" : "")}";
        _where.Text = $"Editing: {_art.WorkRoot}" + (_art.ReferenceRoot != null ? $"    ·    Reference: {_art.ReferenceRoot}" : "");
        _save.IsEnabled = _art.IsDirty;
    }

    private void ShowEmpty()
    {
        _cards.Children.Clear();
        _details.Children.Clear();
        _details.Children.Add(new TextBlock { Text = "Open the art of a theater with Open….", TextWrapping = TextWrapping.Wrap });
        _save.IsEnabled = false;
    }

    // ───────────────────────── categorie e caselle ─────────────────────────

    private string Category => _cats.SelectedItem as string ?? ArtCatalog.Flags;

    private async void ShowCategory()
    {
        if (_art == null) return;
        int gen = ++_gen;
        string cat = Category;
        _catHelp.Text = ArtCatalog.CategoryHelp(cat);
        _centerTop.Children.Clear();
        bool texts = cat == ArtCatalog.Texts, visual = cat == ArtCatalog.Visual, fonts = cat == ArtCatalog.FontsCat, sounds = cat == ArtCatalog.SoundsCat;
        _texts.Visibility = texts ? Visibility.Visible : Visibility.Collapsed;
        _visual.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        _fontsPanel.Visibility = fonts ? Visibility.Visible : Visibility.Collapsed;
        _soundsPanel.Visibility = sounds ? Visibility.Visible : Visibility.Collapsed;
        _cardScroll.Visibility = texts || visual || fonts || sounds ? Visibility.Collapsed : Visibility.Visible;
        if (texts) { ShowTexts(); return; }
        if (fonts) { _fontsPanel.SetArt(_art, _ui); ShowFontDetails(_fontsPanel.Selected); return; }
        if (sounds) { _soundsPanel.SetArt(_art, _ui); ShowSoundDetails(_soundsPanel.Selected); return; }
        if (visual)
        {
            _ui?.Forget();
            _visual.SetArt(_ui);
            return;
        }
        if (cat == ArtCatalog.Patches)
        {
            var add = new Button { Content = "Add a new patch…", Padding = new Thickness(10, 3, 10, 3) };
            add.Click += (_, _) => AddPatch();
            _centerTop.Children.Add(add);
        }
        _cards.Children.Clear();
        _cards.Children.Add(new TextBlock { Text = "Loading…", Foreground = Brushes.White, Margin = new Thickness(8) });
        var art = _art;
        List<(ArtSlot S, Pic? P, ArtSet.State St)> items;
        try
        {
            items = await Task.Run(() =>
            {
                var slots = cat == ArtCatalog.Patches ? art.PatchSlots() : cat == ArtCatalog.Campaigns ? art.CampaignSlots() : ArtCatalog.Slots.Where(s => s.Category == cat).ToList();
                return slots.Select(s => (s, art.Image(s), art.StateOf(s))).ToList();
            });
        }
        catch (Exception ex) { _cards.Children.Clear(); _status.Text = "Could not read the art: " + ex.Message; return; }
        if (gen != _gen) return;
        _cards.Children.Clear();
        foreach (var (s, p, st) in items) _cards.Children.Add(Card(s, p, st));
        if (_sel == null || _sel.Category != cat) _sel = items.FirstOrDefault().S;
        else _sel = items.Select(i => i.S).FirstOrDefault(s => s.Title == _sel.Title) ?? items.FirstOrDefault().S;
        ShowDetails();
    }

    private FrameworkElement Card(ArtSlot s, Pic? p, ArtSet.State st)
    {
        double w = s.Kind switch { SlotKind.Flag => 120, SlotKind.Patch => 110, _ => 210 };
        var sp = new StackPanel { Width = w };
        if (p != null)
        {
            var img = new Image { Source = ImageOps.ToBitmap(p), Stretch = Stretch.Uniform, MaxHeight = s.Kind == SlotKind.Patch ? 96 : 130, MaxWidth = w };
            RenderOptions.SetBitmapScalingMode(img, p.W < w ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            sp.Children.Add(img);
        }
        else sp.Children.Add(new TextBlock { Text = "(not in this art)", Foreground = Brushes.Gray, Height = 40 });
        sp.Children.Add(new TextBlock { Text = s.Title, Foreground = Brushes.White, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = s.Title });
        sp.Children.Add(new TextBlock { Text = StateText(st), FontSize = 10, Foreground = StateBrush(st) });
        var b = new Border
        {
            Child = sp, Margin = new Thickness(5), Padding = new Thickness(6), CornerRadius = new CornerRadius(3), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(2), BorderBrush = ReferenceEquals(s, _sel) || s.Title == _sel?.Title && s.Category == _sel?.Category ? Brushes.Gold : Brushes.Transparent,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x3B)), Tag = s,
        };
        b.MouseLeftButtonUp += (_, _) =>
        {
            _sel = s;
            foreach (var c in _cards.Children.OfType<Border>()) c.BorderBrush = ReferenceEquals(c.Tag, s) ? Brushes.Gold : Brushes.Transparent;
            ShowDetails();
        };
        return b;
    }

    private static string StateText(ArtSet.State s) => s switch
    {
        ArtSet.State.Changed => "● changed (not saved)", ArtSet.State.Theater => "theater image", ArtSet.State.Default => "BMS default", _ => "not in BMS",
    };

    private static Brush StateBrush(ArtSet.State s) => s switch
    {
        ArtSet.State.Changed => Brushes.Orange, ArtSet.State.Theater => Brushes.LightGreen, _ => Brushes.Gray,
    };

    // ───────────────────────── dettagli ─────────────────────────

    private void ShowDetails()
    {
        _details.Children.Clear();
        if (_art == null || _sel is not { } s) return;
        _details.Children.Add(new TextBlock { Text = s.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _details.Children.Add(new TextBlock { Text = StateText(_art.StateOf(s)), Foreground = StateBrush(_art.StateOf(s)) == Brushes.Gray ? Brushes.DimGray : Brushes.DarkGreen, Margin = new Thickness(0, 2, 0, 6) });
        Para("Where it shows in BMS", s.Where);
        if (s.Note != null) Para("Note", s.Note);
        var sizes = _art.Sizes(s);
        Para("Images in the BMS files (all are changed together)", sizes.Count > 0 ? string.Join("\n", sizes) : "none: this art and BMS do not have it");

        var rules = _art.Rules(s);
        if (rules.Count > 0)
        {
            var rb = new StackPanel();
            foreach (var r in rules) rb.Children.Add(new TextBlock { Text = "• " + r, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            _details.Children.Add(new Expander { Header = "Image rules (read before replacing)", Content = rb, IsExpanded = Settings.Get("rulesOpen") != "0", Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold });
            rb.SetValue(System.Windows.Documents.TextElement.FontWeightProperty, FontWeights.Normal);
            ((Expander)_details.Children[^1]).Expanded += (_, _) => Settings.Set("rulesOpen", "1");
            ((Expander)_details.Children[^1]).Collapsed += (_, _) => Settings.Set("rulesOpen", "0");
        }
        var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        Button B(string t, string tip, Action a, bool en = true)
        {
            var b = new Button { Content = t, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), IsEnabled = en };
            b.Click += (_, _) => a(); btns.Children.Add(b); return b;
        }
        bool has = sizes.Count > 0;
        B("Replace with an image…", "PNG, JPG, BMP, TGA, DDS… of any size: you choose the part to use, the editor resizes and converts it", Replace, has).FontWeight = FontWeights.SemiBold;
        B("Take from reference", _art.ReferenceRoot == null ? "Choose a reference art first (Reference… at the top)" : "The same image of the reference art (resized if its size is different)", FromReference, has && _art.Image(s, "ref") != null);
        B("Reset to BMS 4.38", "Puts back the BMS image", Reset, has && s.Kind != SlotKind.Patch);
        B("Export as PNG…", "Saves the image to edit it in another program (then use Replace)", Export, has);
        _details.Children.Add(btns);

        Preview("In the theater (what BMS shows)", _art.Image(s));
        if (s.Kind != SlotKind.Patch) Preview("BMS 4.38", _art.Image(s, "base"));
        if (_art.ReferenceRoot != null) Preview("Reference", _art.Image(s, "ref"));
    }

    private void Para(string head, string text)
    {
        _details.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 1) });
        _details.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    }

    private void Preview(string head, Pic? p)
    {
        _details.Children.Add(new TextBlock { Text = head + (p != null ? $"  ({p.W}×{p.H})" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
        if (p == null) { _details.Children.Add(new TextBlock { Text = "—", Foreground = Brushes.Gray }); return; }
        double scale = Math.Min(370.0 / p.W, Math.Max(1, 120.0 / Math.Max(p.W, p.H)));
        scale = Math.Min(scale, 370.0 / p.W);
        var img = new Image { Source = ImageOps.ToBitmap(p), Width = p.W * scale, Height = p.H * scale, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(img, scale > 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _details.Children.Add(new Border { Child = img, Background = Dark, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left });
    }

    // ───────────────────────── editor visuale ─────────────────────────

    private void ShowVisualDetails(UiElement? e)
    {
        _details.Children.Clear();
        if (_ui == null) return;
        if (e == null)
        {
            _details.Children.Add(new TextBlock { Text = _visual.Screen.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            Para("How it works", _visual.Editing
                ? "Click an element to see where it comes from; right-click it to change it."
                : "This is the screen as BMS draws it for this theater. Press Edit (above the screen) to select and change its images and texts.");
            Para("The KTO is the master", "Every theater uses the art of the KTO (Data\\Art) for everything its own art folder does not have. " +
                 "The editor never writes into the KTO: changing a KTO image copies its archive into the theater's art and changes the copy, so other theaters are not touched. " +
                 "KTO interface texts and window layouts (.scf) are shared by all theaters: they cannot be changed here.");
            if (!_visual.Screen.Checked) Para("Note", "The layout of this screen has not been checked against BMS yet: some windows may be missing or overlap.");
            return;
        }
        if (e.Hidden)
            _details.Children.Add(new Border { BorderBrush = VisualPanel.HiddenBrush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(6, 2, 0, 2), Margin = new Thickness(0, 4, 0, 4),
                Child = new TextBlock { Text = "Hidden in this theater: BMS does not draw it. Right-click → Show again to bring it back.", TextWrapping = TextWrapping.Wrap } });
        var src = _visual.SourceOf(e);
        _details.Children.Add(new TextBlock { Text = e.Label, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var (what, brush) = src switch
        {
            VisualPanel.Source.Theater => ("From the theater's art: can be changed.", VisualPanel.TheaterBrush),
            VisualPanel.Source.KtoImage => ("Image of the KTO (Data\\Art). Changing it copies its archive into the theater's art: the KTO is not touched.", VisualPanel.KtoBrush),
            VisualPanel.Source.Locked => ("KTO text or layout, shared by all theaters: it cannot be changed here.", VisualPanel.LockedBrush),
            VisualPanel.Source.Dynamic => ("Filled by BMS while it runs (map, list, name of the squadron…): nothing to change here.", VisualPanel.DynamicBrush),
            _ => ("Part of the window layout.", Brushes.Gray),
        };
        _details.Children.Add(new Border { BorderBrush = brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(6, 2, 0, 2), Margin = new Thickness(0, 6, 0, 4),
            Child = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap } });
        Para("Window file", $"{e.Item.File}, line {e.Item.Line + 1}" + (_ui.ScfFromTheater(e.Item.File) ? "  (the theater's own copy)" : "  (KTO)"));
        Para("Position and size", $"{e.Rect.X}, {e.Rect.Y}   {e.Rect.W}×{e.Rect.H}  (screen 1920×1080)");
        if (e.Image != null && _ui.FindImage(e.Image) is { } ir)
        {
            Para("Image", $"{e.Image}  {ir.Entry.Width}×{ir.Entry.Height}\nin {ir.Archive} of the {(ir.Theater ? "theater" : "KTO")}");
            if (_ui.Image(e.Image) is { } p) Preview("Preview", p);
        }
        if (e.TextKey != null)
        {
            var t = _ui.Text(e.TextKey);
            Para("Text", $"{e.TextKey} = \"{t?.Text}\"\nin {t?.File ?? "?"}{(t?.TheaterText == true ? " (theater text)" : " (KTO interface text)")}");
        }
        if (e.Font != null) Para("Font", e.Font + (_ui.FontFiles().TryGetValue(e.Font, out var ff) ? "  (" + ff + ")" : "  (not in FontRc.irc: BMS uses DEFAULT)"));
        if (_visual.Editing)
        {
            var btns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (VisualMenu(e) is { } m)
                foreach (var mi in m.Items.OfType<MenuItem>())
                {
                    var b = new Button { Content = mi.Header, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), IsEnabled = mi.IsEnabled, ToolTip = mi.ToolTip };
                    var item = mi;
                    b.Click += (_, _) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    btns.Children.Add(b);
                }
            _details.Children.Add(btns);
        }
    }

    /// <summary>Menu del tasto destro su un elemento della schermata.</summary>
    private ContextMenu? VisualMenu(UiElement e)
    {
        if (_ui == null || _art == null) return null;
        var m = new ContextMenu();
        MenuItem Item(string h, Action a, bool en = true, string? tip = null)
        {
            var mi = new MenuItem { Header = h, IsEnabled = en, ToolTip = tip };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
            return mi;
        }
        if (e.Image != null && _ui.FindImage(e.Image) is { } ir)
            Item("Change image…", () => VisualReplace(e, ir), true, ir.Theater ? "Image of the theater" : "Image of the KTO: it is copied into the theater's art, the KTO is not changed");
        if (e.TextKey != null)
        {
            var t = _ui.Text(e.TextKey);
            bool ok = t?.TheaterText == true;
            Item("Change text…", () => VisualText(e), ok, ok ? "Text of the theater (Theater.irc)" : "KTO interface text shared by all theaters: not editable here");
        }
        if (e.Hidden)
            Item("Show again", () => VisualHide(e, false), e.HiddenByEditor, e.HiddenByEditor ? "Removes the hiding made with the editor" : "BMS itself hides this element");
        else if (e.Kind is "BUTTON" or "TEXT" or "BITMAP" or "TILE")
            Item("Hide in this theater (e.g. a PvP version)…", () => VisualHide(e, true), true,
                "BMS does not draw it any more in this theater (a hidden button cannot be clicked): useful to remove buttons that are not needed, as in a PvP art");
        if (e.Kind == "BUTTON" && !e.Hidden)
            Item("Button look and sound…", () => VisualButton(e), true,
                "Images for normal / pressed / disabled (e.g. raised and pushed in), text moving when pressed, click sound. " +
                (_ui.ScfFromTheater(e.Item.File) ? "The window file is the theater's own copy." : "The window file is the KTO one: it is copied into the theater's art (the KTO is not touched)."));
        if (e.Kind is "TEXT" or "BUTTON" && !string.IsNullOrEmpty(e.Text))
            Item("Change font…", () => VisualFont(e), true, _ui.ScfFromTheater(e.Item.File) ? "The window file is the theater's own copy"
                : "The window file is the KTO one: it is copied into the theater's art and the copy is changed (the KTO is not touched)");
        return m.Items.Count > 0 ? m : null;
    }

    private void VisualReplace(UiElement e, UiArt.ImgRef ir)
    {
        if (_art == null || _ui == null) return;
        if (!ir.Theater && MessageBox.Show(this,
                $"{e.Image} is an image of the KTO ({ir.Archive} in Data\\Art), the master art used by every theater.\n\n" +
                $"The KTO is NOT changed: the editor copies {ir.Archive} into this theater's art and changes the copy. " +
                "Other theaters keep the KTO image.\n\nContinue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var arch = _ui.ImageArchives(e.Image!).Where(a => a.StartsWith("_FHD", StringComparison.OrdinalIgnoreCase) || a.StartsWith("resource", StringComparison.OrdinalIgnoreCase)).ToArray();
        var slot = new ArtSlot(ArtCatalog.Visual, e.Image!, $"{_visual.Screen.Name} screen, {e.Label} ({e.Item.File}).", SlotKind.Image, arch, new[] { e.Image! });
        if (Replace(slot)) { _ui.Forget(); _ = _visual.Refresh(); }
    }

    private void VisualFont(UiElement e)
    {
        if (_art == null || _ui == null) return;
        string rel = e.Item.File;
        if (!_ui.ScfFromTheater(rel) && MessageBox.Show(this,
                $"The font is set in the window file {rel}, which this theater takes from the KTO (Data\\Art), the master used by every theater.\n\n" +
                "The KTO is NOT changed: the editor copies the window file into this theater's art and changes the copy. " +
                "From then on the theater uses its own copy of this window (later BMS updates of it will not reach this theater).\n\nContinue?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var fp = new FontPicker(_ui, e.Text ?? "", e.Font, e.Color) { Owner = this };
        if (fp.ShowDialog() != true || fp.Result == null || fp.Result.Equals(e.Font, StringComparison.OrdinalIgnoreCase)) return;
        var lines = _art.ScfLines(rel);
        if (lines == null) return;
        _art.SetScf(rel, Scf.SetFont(lines, e.Item, fp.Result));
        _status.Text = $"{e.Label}: font {e.Font} → {fp.Result} in {rel}. Save to write it (into the theater's art).";
        UpdateTitle();
        _ui.Forget();
        _ = _visual.Refresh();
    }

    private void VisualHide(UiElement e, bool hide)
    {
        if (_art == null || _ui == null) return;
        string rel = e.Item.File;
        if (hide)
        {
            string msg = $"Hide {e.Label}{(e.Text is { Length: > 0 } t ? " (\"" + t + "\")" : "")} in this theater?\n\n" +
                         "BMS will not draw it any more, and a hidden button cannot be clicked. Typical use: a PvP version of the art without the buttons that are not needed (TE Edit, TE Builder…).\n\n";
            msg += _ui.ScfFromTheater(rel) ? $"The change goes into the theater's own copy of {rel}."
                : $"{rel} is the KTO window file (Data\\Art): the KTO is NOT changed. The editor copies the file into this theater's art and hides the element in the copy " +
                  "(later BMS updates of this window will not reach this theater).";
            msg += "\n\nYou can show it again from here (Edit: hidden elements have a purple dashed frame).";
            if (MessageBox.Show(this, msg, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        }
        var lines = _art.ScfLines(rel);
        if (lines == null) return;
        _art.SetScf(rel, Scf.SetHidden(lines, e.Item, hide));
        _status.Text = $"{e.Label} {(hide ? "hidden" : "shown again")} in {rel}. Save to write it (into the theater's art).";
        UpdateTitle();
        _ui.Forget();
        _ = _visual.Refresh();
    }

    /// <summary>Tutti i pulsanti (di tutte le finestre) che usano questa immagine come immagine normale.</summary>
    private List<ScfItem> ButtonsWithImage(string image)
    {
        if (_ui == null) return new();
        return _ui.AllWindows().SelectMany(w => w.Items).Where(it => it.Kind == "BUTTON" && ButtonDialog.States(it).N is { } n && n.Equals(image, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private string? SoundFileOf(string name)
    {
        if (_art == null) return null;
        var s = ArtSet.ParseSounds(_art.SoundLines()).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return s.Name == null ? null : _art.SoundFile(s.File);
    }

    private void VisualButton(UiElement e)
    {
        if (_art == null || _ui == null) return;
        var st = ButtonDialog.States(e.Item);
        var same = st.N != null ? ButtonsWithImage(st.N) : new List<ScfItem>();
        var sounds = ArtSet.ParseSounds(_art.SoundLines()).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var d = new ButtonDialog(_ui, e, sounds, same.Count, SoundFileOf) { Owner = this };
        if (d.ShowDialog() != true) return;
        var r = d.Result;
        var items = r.AllSame ? same : new List<ScfItem> { e.Item };
        var files = items.Select(i => i.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kto = files.Where(f => !_ui.ScfFromTheater(f)).ToList();
        if (kto.Count > 0 && MessageBox.Show(this,
                $"{kto.Count} window file(s) come from the KTO (Data\\Art): {string.Join(", ", kto.Take(6))}{(kto.Count > 6 ? "…" : "")}.\n\n" +
                "The KTO is NOT changed: the editor copies them into this theater's art and changes the copies (later BMS updates of those windows will not reach this theater)." +
                (r.PressedNew != null || r.DisabledNew != null ? $"\n\nThe new images go into the theater's copy of {r.Archive}." : "") + "\n\nContinue?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            if (r.PressedNew != null && r.PressedName != null) _art.AddImage(r.Archive, r.PressedName, r.PressedNew, r.Normal);
            if (r.DisabledNew != null && r.DisabledName != null) _art.AddImage(r.Archive, r.DisabledName, r.DisabledNew, r.Normal);
            foreach (var f in files)
            {
                var lines = _art.ScfLines(f);
                if (lines == null) continue;
                // dal basso verso l'alto: le righe degli elementi sopra non si spostano
                foreach (var it in items.Where(i => i.File.Equals(f, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.Line))
                    lines = ButtonEdit.Apply(lines, it, r);
                _art.SetScf(f, lines);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not change the button: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); return; }
        _status.Text = $"{items.Count} button(s) changed in {files.Count} window file(s). Save to write it (into the theater's art).";
        UpdateTitle();
        _ui.Forget();
        _ = _visual.Refresh();
    }

    private void VisualText(UiElement e)
    {
        if (_art == null || _ui == null || e.TextKey == null) return;
        var t = _ui.Text(e.TextKey);
        var v = Prompt.Ask(this, "Change text", $"{ArtCatalog.TextLabel(e.TextKey)} ({e.TextKey}):", t?.Text ?? "");
        if (v == null) return;
        _art.SetText(e.TextKey, v);
        _status.Text = $"{e.TextKey} changed. Save to write it.";
        UpdateTitle();
        _ui.Forget();
        _ = _visual.Refresh();
    }

    // ───────────────────────── suoni ─────────────────────────

    private void ShowSoundDetails(SoundsPanel.SoundRow? r)
    {
        _details.Children.Clear();
        TextBlock H(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        TextBlock P(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        if (r != null)
        {
            _details.Children.Add(new TextBlock { Text = r.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            _details.Children.Add(P($"File: {r.File}\nFrom: {r.State}" + (r.Options.Length > 0 ? $"\nOptions: {r.Options}" : "")));
            _details.Children.Add(P(r.Uses > 0 ? $"Chosen by {r.Uses} buttons ([SOUNDBITE] in the window files)." : "Not chosen by buttons: BMS plays it by itself (music, voices, events)."));
        }
        _details.Children.Add(H("How it works"));
        _details.Children.Add(P("Every button says which sound it makes and when ([SOUNDBITE] SND_PUSH C_TYPE_LMOUSEDOWN = when clicked; C_TYPE_MOUSEOVER = when the mouse goes over it). " +
                                "SoundRc.irc says which file each SND_ name plays. To change the sound of a button, use the visual editor: Edit, right-click on the button → Button look and sound."));
        _details.Children.Add(H("Rules"));
        _details.Children.Add(P("• BMS plays .ogg (Ogg Vorbis) files. A .wav or .mp3 can be converted with Audacity (free): File → Export → Export as OGG.\n" +
                                "• Button sounds should be short (well under a second) and not loud: they play at every click.\n" +
                                "• Changing SND_PUSH changes the click of hundreds of buttons; to change only some buttons, give them another sound in the visual editor."));
        _details.Children.Add(H("The KTO is the master"));
        _details.Children.Add(P("The KTO sounds (Data\\Sounds\\ui) are never changed. A new sound is copied into this theater's art (Art\\Sounds), the theater gets its own copy of " +
                                "SoundRc.irc (Art\\main\\SoundRc.irc) and Main_Snd.lst tells BMS to use it, as the Israel theater does. 'Put back the KTO sound' points the sound to the KTO file again."));
        _details.Children.Add(H("Check in BMS"));
        _details.Children.Add(P("After saving, start BMS with this theater and click a button that uses the sound. If you still hear the old sound, tell us: the editor then needs another way."));
    }

    // ───────────────────────── font ─────────────────────────

    private void ShowFontDetails(FontsPanel.FontRow? r)
    {
        _details.Children.Clear();
        TextBlock H(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        TextBlock P(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        if (r != null)
        {
            _details.Children.Add(new TextBlock { Text = r.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
            _details.Children.Add(P($"File: {r.Rel}  ·  {FontsPanel.StateText(r)}" + (r.Font != null ? $"\nFrom the TrueType font: {r.Font.Face}, line height {r.Font.Height} px" : "")));
            if (r.SameFile.Length > 0) _details.Children.Add(P("Same file as: " + string.Join(", ", r.SameFile) + " (changing one changes all)."));
            _details.Children.Add(H("Where it is used"));
            _details.Children.Add(P(r.Uses > 0 ? $"{r.Uses} times, in: " + string.Join(", ", r.Files.Select(f => Path.GetFileNameWithoutExtension(f))) + "."
                : r.Name == "DEFAULT" ? "Every text that has no font of its own." : "Not chosen by any window yet: use 'Change font…' in the visual editor (Edit, right-click on a text)."));
        }
        _details.Children.Add(H("How it works"));
        _details.Children.Add(P("BMS does not use TrueType fonts directly: each interface font is a .bft file, the TrueType font already drawn in pixels (black and white, no " +
                                "smoothing) at one height. The editor makes the .bft the same way the BMS tools did, so the preview is exactly what BMS will show."));
        _details.Children.Add(H("Rules"));
        _details.Children.Add(P("• Replace a font keeping the same line height (the default choice): the texts stay inside their buttons and boxes on every screen.\n" +
                                "• Prefer narrow, plain fonts: a wider font can make long texts (menus, buttons) not fit. The window warns when the new font is wider.\n" +
                                "• Small sizes (10-16 px) need fonts made for screens; thin or decorative fonts become unreadable in black and white pixels.\n" +
                                "• Only characters 31-255 (Western European, Windows-1252) exist in BMS fonts; letters missing in the TrueType font are drawn empty.\n" +
                                "• The license of the TrueType font must allow distributing it embedded in your theater."));
        _details.Children.Add(H("The KTO is the master"));
        _details.Children.Add(P("The fonts in Data\\Art\\Fonts are used by every theater and are never changed. A replaced or new font is written into this theater's Art\\Fonts " +
                                "(with FontRc.irc and FontIds.id when a font is added); whatever the theater does not have comes from the KTO. 'Put back the KTO font' removes the theater's file."));
        _details.Children.Add(H("Check in BMS"));
        _details.Children.Add(P("BMS takes the fonts from the theater's Art\\Fonts before the KTO ones (checked with BMS 4.38). After saving, start BMS with this " +
                                "theater and look at a screen that uses the changed font; 'Where it is used' above lists the window files."));
    }

    // ───────────────────────── azioni ─────────────────────────

    private void Replace() { if (_sel != null) Replace(_sel); }

    private bool Replace(ArtSlot s)
    {
        if (_art == null) return false;
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Image for " + s.Title, Filter = ImageOps.OpenFilter };
        if (Settings.Get("imgdir") is { } dir && Directory.Exists(dir)) d.InitialDirectory = dir;
        if (d.ShowDialog(this) != true) return false;
        Settings.Set("imgdir", Path.GetDirectoryName(d.FileName)!);
        Pic src;
        try { src = ImageOps.Load(d.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "Cannot read the image: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        // misura di riferimento per il riquadro: la voce piu' grande (per le bandiere quella orizzontale)
        var t = _art.Targets(s).Where(x => s.Kind != SlotKind.Flag || x.E.Name.EndsWith("_HRZ", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.E.Width * x.E.Height).FirstOrDefault();
        if (t.E == null) return false;
        if (src.W < t.E.Width || src.H < t.E.Height)
        {
            if (MessageBox.Show(this, $"The image is {src.W}×{src.H}, smaller than what BMS needs ({t.E.Width}×{t.E.Height}).\nIt will be enlarged and look blurred. Use it anyway?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        }
        var cw = new CropWindow(src, t.E.Width, t.E.Height, s.Title, _art.Rules(s)) { Owner = this };
        if (cw.ShowDialog() != true) return false;
        var done = _art.Replace(s, src, cw.Result);
        _status.Text = $"{s.Title}: {done.Count} images changed ({string.Join(", ", done)}). Save to write them.";
        Refresh();
        return true;
    }

    private void FromReference()
    {
        if (_art == null || _sel is not { } s) return;
        var done = _art.FromReference(s);
        _status.Text = done.Count > 0 ? $"{s.Title}: taken from the reference ({string.Join(", ", done)}). Save to write them." : $"{s.Title}: not in the reference art.";
        Refresh();
    }

    private void Reset()
    {
        if (_art == null || _sel is not { } s) return;
        int n = _art.Reset(s);
        _status.Text = n > 0 ? $"{s.Title}: BMS 4.38 image put back ({n}). Save to write it." : $"{s.Title}: already the BMS image.";
        Refresh();
    }

    private void Export()
    {
        if (_art == null || _sel is not { } s || _art.Image(s) is not { } p) return;
        var d = new Microsoft.Win32.SaveFileDialog { Title = "Export " + s.Title, Filter = "PNG|*.png", FileName = s.PreviewEntry + ".png" };
        if (d.ShowDialog(this) != true) return;
        ImageOps.SavePng(p, d.FileName);
        _status.Text = $"Exported {d.FileName} ({p.W}×{p.H}). Edit it and use 'Replace with an image…' to put it back.";
    }

    private void AddPatch()
    {
        if (_art == null) return;
        var name = Prompt.Ask(this, "New squadron patch", "Name of the patch (letters, numbers and _, no spaces, not starting with a number; e.g. ME_12TH_SQN):", "ME_NEW_SQN");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z][A-Z0-9_\-]{0,30}$"))
        { MessageBox.Show(this, "Use only letters, numbers, _ and -, no spaces, starting with a letter (max 31 characters).", Title); return; }
        if (_art.PatchIds().ContainsKey(name) || _art.Effective(ArtCatalog.PatchArchive)?.Find(name) != null)
        { MessageBox.Show(this, $"A patch called {name} already exists.", Title); return; }
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Image of the patch " + name, Filter = ImageOps.OpenFilter };
        if (d.ShowDialog(this) != true) return;
        Pic src;
        try { src = ImageOps.Load(d.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "Cannot read the image: " + ex.Message, Title); return; }
        var cw = new CropWindow(src, 96, 96, name, new[] { "Patch: 96×96, emblem on a transparent background (PNG with transparency); keep it bold and simple." }) { Owner = this };
        if (cw.ShowDialog() != true) return;
        int id = _art.AddPatch(name, src, cw.Result);
        _status.Text = $"Patch {name} added with number {id}. Save to write it; in the Campaign Editor the squadrons can then use patch {id}.";
        _sel = null;
        Refresh();
    }

    private void Refresh()
    {
        UpdateTitle();
        ShowCategory();
    }

    private void ShowTexts()
    {
        _details.Children.Clear();
        _details.Children.Add(new TextBlock { Text = "Texts of the theater", FontSize = 16, FontWeight = FontWeights.SemiBold });
        _details.Children.Add(new TextBlock
        {
            Text = "Click a cell of the 'Theater' column to change it (Enter to confirm). The country and team names follow the team numbers of the campaign " +
                   "(Team 1 = team 1 of the .tea / Teams tab). Campaign n is the n-th campaign of the selection screen; its picture is in 'Campaign pictures'. " +
                   "Quotes (\") are not allowed inside a text: they become apostrophes.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        var irc = _art?.Irc();
        if (irc == null) { _texts.ItemsSource = null; _status.Text = "Theater.irc not found (neither in the theater nor in BMS)."; return; }
        var bms = _art!.IrcBase()?.Entries().ToDictionary(e => e.Key, e => e.Text, StringComparer.OrdinalIgnoreCase) ?? new();
        var have = irc.Entries().Where(e => e.Key.StartsWith("TXT_", StringComparison.OrdinalIgnoreCase)).ToList();
        var keys = have.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // anche le voci che il teatro non ha (BMS le mostra vuote): scrivendole si aggiungono al file
        var rows = have.Select(e => new TextRow { Key = e.Key, Label = ArtCatalog.TextLabel(e.Key), Value = e.Text, Bms = bms.GetValueOrDefault(e.Key, "") }).ToList();
        rows.AddRange(bms.Where(kv => kv.Key.StartsWith("TXT_", StringComparison.OrdinalIgnoreCase) && !keys.Contains(kv.Key))
            .Select(kv => new TextRow { Key = kv.Key, Label = ArtCatalog.TextLabel(kv.Key) + "  (missing in the theater)", Value = "", Bms = kv.Value }));
        _texts.ItemsSource = rows;
    }

    // ───────────────────────── salvataggio, controllo, copia ─────────────────────────

    private bool InBms(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(_dataDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copia dell'Art aperto in ArtEditor\Theaters\&lt;nome&gt;\Art, con dentro le modifiche non salvate; l'originale resta com'era.
    /// Serve per le varianti (es. l'Art PvP distribuito per gli eventi, senza i pulsanti che non servono).
    /// </summary>
    private async Task SaveAsCopy()
    {
        if (_art == null) return;
        var name = Prompt.Ask(this, "Save as a copy", "Name of the copy (e.g. 'Mideast PvP'). It is made in ArtEditor\\Theaters\\<name>\\Art with the changes not saved yet; the art you opened is not changed:",
            _art.Label + " PvP");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = TerrainTool.Core.Campaign.CampaignWorkspace.SafeName(name.Trim());
        string dst = Path.Combine(Settings.ToolFolder, "Theaters", name, "Art");
        if (Path.GetFullPath(dst).TrimEnd('\\').Equals(Path.GetFullPath(_art.WorkRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "That is the art already open.", Title); return; }
        if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any() &&
            MessageBox.Show(this, $"{dst} already exists: its files will be replaced. Continue?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        string src = _art.WorkRoot;
        var files = Directory.Exists(src) ? Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList() : new List<string>();
        long total = files.Sum(f => new FileInfo(f).Length), done = 0;
        IsEnabled = false;
        try
        {
            var prog = new Progress<long>(b => _status.Text = $"Copying the art… {b / 1048576} / {Math.Max(1, total / 1048576)} MB");
            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    string t = Path.Combine(dst, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    ResourceBundle.BreakLink(t);
                    File.Copy(f, t, true);
                    done += new FileInfo(f).Length;
                    ((IProgress<long>)prog).Report(done);
                }
            });
            Directory.CreateDirectory(dst);
            // le modifiche non salvate vanno nella copia
            bool dirty = _art.IsDirty;
            _art.Retarget(dst);
            if (dirty) _art.Save(Path.Combine(Settings.BackupFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}"));
            Load(dst, name);
            MessageBox.Show(this, $"Copy made: {dst}\n\n{files.Count} files copied{(dirty ? " and the changes not saved yet written into the copy" : "")}. The art you had open ({src}) is unchanged.\n\n" +
                "Now you are editing the copy. To use it in BMS: 'Copy into a theater…', or give its Art folder to the players (it replaces the theater's art folder).",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Copy failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }

    private bool Save()
    {
        if (_art == null || !_art.IsDirty) return true;
        if (InBms(_art.WorkRoot))
        {
            if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (!_warnedBms && MessageBox.Show(this,
                    $"You are saving into BMS:\n{_art.WorkRoot}\n\nEvery file that is replaced is first copied into\n{Settings.BackupFolder}\nbut a backup of your own of the theater's art folder is a good idea the first time.\n\nSave now?",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return false;
            _warnedBms = true;
        }
        try
        {
            string backup = Path.Combine(Settings.BackupFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{CampaignWorkspaceName(_art.Label)}");
            var files = _art.Save(backup);
            _soundsPanel.Saved();
            _status.Text = $"Saved {files.Count} files in {_art.WorkRoot}: {string.Join(", ", files)}" + (Directory.Exists(backup) ? $"  ·  replaced files copied into {backup}" : "");
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Save failed: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static string CampaignWorkspaceName(string s) => TerrainTool.Core.Campaign.CampaignWorkspace.SafeName(s);

    private void Check()
    {
        if (_art == null) return;
        var res = _art.Check();
        MessageBox.Show(this, res.Count == 0 ? "No problems found." : string.Join("\n\n", res), "Check · " + _art.Label, MessageBoxButton.OK,
            res.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CopyInto()
    {
        if (_art == null) return;
        if (_art.IsDirty && !Save()) return;
        var w = new OpenWindow(true) { Owner = this, Title = "Copy this art into a theater (choose the theater)" };
        if (w.ShowDialog() != true || w.Root == null) return;
        string dst = w.Root;
        string bmsArt = Path.GetFullPath(Path.Combine(w.DataDir, "Art")).TrimEnd('\\');
        if (Path.GetFullPath(dst).TrimEnd('\\').Equals(bmsArt, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "That theater uses the BMS art itself (Data\\Art): it is not changed. Give the theater its own art folder (artdir) first.", Title); return; }
        if (Path.GetFullPath(dst).TrimEnd('\\').Equals(Path.GetFullPath(_art.WorkRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show(this, "That is the art being edited.", Title); return; }
        // solo i file che l'editor gestisce: mai CkptArt, finestre .scf o altro che la cartella del teatro contiene
        var files = Directory.Exists(_art.WorkRoot) ? Directory.EnumerateFiles(_art.WorkRoot, "*", SearchOption.AllDirectories)
            .Where(f => ArtSet.Managed(Path.GetRelativePath(_art.WorkRoot, f))).ToList() : new();
        if (files.Count == 0) { MessageBox.Show(this, "This art has no files of its own yet (everything is BMS default).", Title); return; }
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var changed = files.Where(f =>
        {
            string rel = Path.GetRelativePath(_art.WorkRoot, f);
            string? t = TheaterArtJob.FindCI(dst, rel);
            return t == null || new FileInfo(t).Length != new FileInfo(f).Length || File.GetLastWriteTimeUtc(t) != File.GetLastWriteTimeUtc(f);
        }).ToList();
        if (changed.Count == 0) { _status.Text = "The theater already has the same files."; return; }
        if (MessageBox.Show(this, $"Copy {changed.Count} files into\n{dst}\n\n{string.Join("\n", changed.Take(12).Select(f => Path.GetRelativePath(_art.WorkRoot, f)))}{(changed.Count > 12 ? "\n…" : "")}\n\n" +
                $"The files replaced are first copied into {Settings.BackupFolder}. A backup of your own of the theater's art folder is a good idea the first time.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        string backup = Path.Combine(Settings.BackupFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_copy_{CampaignWorkspaceName(w.Label)}");
        int n = 0;
        try
        {
            foreach (var f in changed)
            {
                string rel = Path.GetRelativePath(_art.WorkRoot, f);
                string target = TheaterArtJob.FindCI(dst, rel) ?? Path.Combine(dst, rel);
                if (File.Exists(target))
                {
                    string b = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                    File.Copy(target, b, true);
                    ResourceBundle.BreakLink(target);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(f));
                n++;
            }
            // i suoni del teatro: Main_Snd.lst e SoundRc.irc devono puntare all'Art di destinazione
            if (changed.Any(f => Path.GetFileName(f).Equals(ArtSet.SoundList, StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)))
                ArtSet.RelinkSounds(dst, w.DataDir, _art.WorkRelToData() ?? (Path.GetFileName(Path.GetDirectoryName(_art.WorkRoot.TrimEnd('\\', '/'))!) + @"\Art"));
            _status.Text = $"{n} files copied into {dst}" + (Directory.Exists(backup) ? $" (replaced files saved in {backup})" : "") + ". Restart BMS to see them.";
        }
        catch (Exception ex) { MessageBox.Show(this, $"Copy stopped after {n} files: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
