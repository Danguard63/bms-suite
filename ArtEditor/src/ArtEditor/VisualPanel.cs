using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ArtEditor;

/// <summary>
/// Editor visuale: la schermata di BMS ridisegnata a 1920×1080 (finestre .scf, immagini, testi, font), con il menu a sinistra che
/// porta alle altre schermate come in BMS. In visualizzazione si naviga; con "Edit" si seleziona un elemento (clic) e con il tasto
/// destro si cambia. I colori dei riquadri dicono da dove viene ogni elemento: del teatro, della KTO (Data\Art) o disegnato da BMS.
/// </summary>
public sealed class VisualPanel : DockPanel
{
    private UiArt? _ui;
    private readonly ComboBox _screens = new() { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ToggleButton _edit = new() { Content = "Edit", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private readonly CheckBox _sources = new() { Content = "Show where each element comes from", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Image _img = new() { Width = UiScreens.ScreenW, Height = UiScreens.ScreenH, Stretch = Stretch.Fill };
    private readonly Canvas _over = new() { Width = UiScreens.ScreenW, Height = UiScreens.ScreenH, Background = Brushes.Transparent };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6), Foreground = Brushes.DimGray };
    private readonly Button _back = new() { Content = "\u25C0 Back", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Back to the previous screen (in BMS: the close / cancel button of the screen)" };
    private readonly Stack<UiScreen> _history = new();
    private UiScreen? _current;
    private bool _goingBack;
    private List<UiElement> _els = new();
    private int _gen;

    public UiElement? Selected { get; private set; }
    /// <summary>Elemento scelto (null = nessuno): la finestra principale mostra i dettagli.</summary>
    public event Action<UiElement?>? SelectionChanged;
    /// <summary>Menu del tasto destro per l'elemento (in modalita' Edit).</summary>
    public Func<UiElement, ContextMenu?>? MenuFor;
    public bool Editing => _edit.IsChecked == true;
    public UiScreen Screen => _screens.SelectedItem as UiScreen ?? UiScreens.All[0];

    public static readonly Brush TheaterBrush = Fz(new SolidColorBrush(Color.FromRgb(80, 220, 120)));
    public static readonly Brush KtoBrush = Fz(new SolidColorBrush(Color.FromRgb(255, 170, 40)));
    public static readonly Brush LockedBrush = Fz(new SolidColorBrush(Color.FromRgb(230, 70, 70)));
    public static readonly Brush DynamicBrush = Fz(new SolidColorBrush(Color.FromRgb(110, 170, 230)));
    public static readonly Brush HiddenBrush = Fz(new SolidColorBrush(Color.FromRgb(220, 80, 220)));
    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public VisualPanel()
    {
        foreach (var s in UiScreens.All) _screens.Items.Add(s);
        _screens.SelectedIndex = 0;
        _current = UiScreens.All[0];
        _screens.SelectionChanged += (_, _) =>
        {
            if (!_goingBack && _current != null && _current != Screen) _history.Push(_current);
            _current = Screen;
            _back.IsEnabled = Screen != UiScreens.All[0];
            Select(null); _ = Refresh();
        };
        _back.IsEnabled = false;
        _back.Click += (_, _) => GoBack();
        _edit.Checked += (_, _) => { UpdateHint(); DrawOverlay(); };
        _edit.Unchecked += (_, _) => { Select(null); UpdateHint(); DrawOverlay(); _over.ToolTip = null; _over.Cursor = Cursors.Arrow; Mouse.Capture(null); };
        _sources.Checked += (_, _) => DrawOverlay();
        _sources.Unchecked += (_, _) => DrawOverlay();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        bar.Children.Add(new TextBlock { Text = "Screen ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_screens); bar.Children.Add(_back); bar.Children.Add(_edit); bar.Children.Add(_sources);
        foreach (var (b, t) in new[] { (TheaterBrush, "theater"), (KtoBrush, "KTO image (copied to the theater if changed)"), (LockedBrush, "KTO text/layout (not editable)"), (DynamicBrush, "filled by BMS"), (HiddenBrush, "hidden in this theater") })
        {
            bar.Children.Add(new Border { Width = 14, Height = 10, BorderBrush = b, BorderThickness = new Thickness(2), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
        }
        SetDock(bar, Dock.Top); Children.Add(bar);
        SetDock(_hint, Dock.Top); Children.Add(_hint);

        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        var g = new Grid { Width = UiScreens.ScreenW, Height = UiScreens.ScreenH };
        g.Children.Add(_img); g.Children.Add(_over);
        var vb = new Viewbox { Child = g, Stretch = Stretch.Uniform };
        Children.Add(new Border { Child = vb, Background = Brushes.Black });

        _over.MouseLeftButtonDown += OnClick;
        _over.MouseRightButtonUp += OnRightClick;
        _over.MouseMove += OnMove;
        UpdateHint();
    }

    public void SetArt(UiArt? ui) { _ui = ui; Select(null); _ = Refresh(); }

    private void UpdateHint() => _hint.Text = Editing
        ? "Edit: click an element to select it, right-click to change it. Images of the KTO (Data\\Art, the master of all theaters) are never changed: " +
          "the editor copies the archive into the theater's art and changes the copy. KTO texts and window layouts cannot be changed here. Areas filled by BMS while it runs (maps, lists) are only outlined."
        : ViewHint;

    private const string ViewHint = "View: the screen as BMS draws it for this theater (Full HD). Click the menu on the left to go to another screen; the close / cancel buttons of a screen (or \u25C0 Back) bring you back. Press Edit to change images and texts.";

    /// <summary>Torna alla schermata precedente (o al menu principale).</summary>
    public void GoBack()
    {
        var to = UiScreens.All[0];
        while (_history.Count > 0) { var h = _history.Pop(); if (h != Screen) { to = h; break; } }
        _goingBack = true;
        try { _screens.SelectedItem = to; } finally { _goingBack = false; }
    }

    /// <summary>Pulsanti che in BMS chiudono la schermata (X in alto, Cancel, Close, Back): in visualizzazione riportano indietro.</summary>
    private bool IsCloseButton(UiElement? e)
    {
        if (e == null || e.Kind != "BUTTON" || Screen == UiScreens.All[0]) return false;
        string id = (e.Item.Id ?? "").ToUpperInvariant();
        string img = (e.Image ?? "").ToUpperInvariant();
        string txt = (e.TextKey ?? "").ToUpperInvariant();
        return img.StartsWith("CLOSE") || id is "CANCEL" or "CLOSE_WINDOW" or "CLOSE" or "BACK" or "EXIT" or "DONE"
            || id.EndsWith("_CANCEL") || id.EndsWith("_CLOSE") || id.EndsWith("_BACK") || id.EndsWith("_EXIT")
            || txt is "TXT_CANCEL" or "TXT_CLOSE" or "TXT_BACK" or "TXT_EXIT";
    }

    /// <summary>Ridisegna la schermata (anche dopo una modifica: le modifiche non salvate si vedono subito).</summary>
    public async Task Refresh()
    {
        if (_ui == null) { _img.Source = null; _over.Children.Clear(); return; }
        int gen = ++_gen;
        var ui = _ui; var scr = Screen;
        try
        {
            var (els, px) = await Task.Run(() =>
            {
                var e = UiScreens.Build(ui, UiScreens.Windows(ui, scr), includeHidden: true);
                return (e, UiScreens.Render(ui, e));
            });
            if (gen != _gen) return;
            _els = els;
            _img.Source = ImageOps.ToBitmap(new Pic(UiScreens.ScreenW, UiScreens.ScreenH, px));
            if (Selected != null) Selected = _els.FirstOrDefault(x => x.Item == Selected.Item);
            DrawOverlay();
            SelectionChanged?.Invoke(Selected);
        }
        catch (Exception ex) { _hint.Text = "Could not draw the screen: " + ex.Message; }
    }

    // ───────────────────────── provenienza degli elementi ─────────────────────────

    public enum Source { Theater, KtoImage, Locked, Dynamic, None }

    public Source SourceOf(UiElement e)
    {
        if (_ui == null) return Source.None;
        if (e.Dynamic) return Source.Dynamic;
        if (e.Image != null && _ui.FindImage(e.Image) is { } r) return r.Theater ? Source.Theater : Source.KtoImage;
        // immagini da file .tga (ImageRc.irc): per ora si vedono, non si cambiano
        if (e.Image != null && _ui.FindTga(e.Image) is { } tg) return tg.Theater ? Source.Theater : Source.Locked;
        if (e.TextKey != null && _ui.Text(e.TextKey) is { } t) return t.TheaterText ? Source.Theater : Source.Locked;
        return e.Kind is "TEXT" or "BUTTON" ? Source.Locked : Source.None;
    }

    private Brush? BrushOf(Source s) => s switch
    {
        Source.Theater => TheaterBrush, Source.KtoImage => KtoBrush, Source.Locked => LockedBrush, Source.Dynamic => DynamicBrush, _ => null,
    };

    private void DrawOverlay()
    {
        _over.Children.Clear();
        if (Editing)
            foreach (var e in _els.Where(x => x.Hidden)) Rect(e.Rect, HiddenBrush, 2, true);
        if (Editing && _sources.IsChecked == true)
            foreach (var e in _els)
            {
                if (e.Hidden) continue;
                if (e.Kind is "FILL" or "LINE" or "BOX") continue;
                if (e.Rect.W >= UiScreens.ScreenW - 10 && e.Rect.H >= UiScreens.ScreenH - 10) continue;   // sfondo intero
                if (BrushOf(SourceOf(e)) is not { } b) continue;
                Rect(e.Rect, b, 1.5, SourceOf(e) == Source.Dynamic);
            }
        if (Selected != null) Rect(Selected.Rect, Brushes.Gold, 3, false);
    }

    private void Rect(Rect32 r, Brush b, double t, bool dash)
    {
        var rc = new Rectangle { Width = Math.Max(2, r.W), Height = Math.Max(2, r.H), Stroke = b, StrokeThickness = t, IsHitTestVisible = false };
        if (dash) rc.StrokeDashArray = new DoubleCollection { 4, 3 };
        Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y);
        _over.Children.Add(rc);
    }

    // ───────────────────────── mouse ─────────────────────────

    /// <summary>L'elemento piu' in alto sotto il punto (immagini, testi e pulsanti prima dei riempimenti; lo sfondo intero per ultimo).</summary>
    private UiElement? Hit(Point p)
    {
        int x = (int)p.X, y = (int)p.Y;
        UiElement? best = null;
        for (int i = _els.Count - 1; i >= 0; i--)
        {
            var e = _els[i];
            if (!e.Rect.Contains(x, y) || e.Kind is "FILL" or "LINE" or "BOX") continue;
            if (e.Hidden && !Editing) continue;
            if (best == null) best = e;
            if (e.Kind is "BUTTON" or "TEXT" || e.Rect.W * e.Rect.H < best.Rect.W * best.Rect.H) { best = e; break; }
        }
        return best;
    }

    private void Select(UiElement? e)
    {
        Selected = e;
        DrawOverlay();
        SelectionChanged?.Invoke(e);
    }

    private void OnClick(object sender, MouseButtonEventArgs ev)
    {
        var p = ev.GetPosition(_over);
        if (Editing) { Select(Hit(p)); return; }
        // visualizzazione: un pulsante del menu porta alla sua schermata, un pulsante di chiusura torna indietro
        var (link, target) = Link(p);
        if (link == null) { _hint.Text = ViewHint + (Hit(p) is { } h ? $"   (clicked: {h.Label}, not a link to another screen)" : ""); return; }
        _hint.Text = ViewHint;
        if (target != null) _screens.SelectedItem = target; else GoBack();
    }

    /// <summary>
    /// In visualizzazione: il pulsante sotto il punto che porta a un'altra schermata (voce del menu) o indietro (chiusura). Si cercano tutti gli
    /// elementi sotto il punto, dall'alto, passando sopra a immagini e riempimenti; un altro pulsante o testo sopra il link lo copre (come in BMS).
    /// </summary>
    private (UiElement? El, UiScreen? Target) Link(Point pt)
    {
        int x = (int)pt.X, y = (int)pt.Y;
        for (int i = _els.Count - 1; i >= 0; i--)
        {
            var e = _els[i];
            if (e.Hidden || !e.Rect.Contains(x, y) || e.Kind is "FILL" or "LINE" or "BOX") continue;
            if (e.Image is { } img && UiScreens.All.FirstOrDefault(s => s.MenuImage != null && s.MenuImage.Equals(img, StringComparison.OrdinalIgnoreCase)) is { } scr)
                return (e, scr);
            // pulsanti che aprono un'altra schermata (TE BUILDER, EDIT, COMMIT, INTEL...)
            if (e.Kind == "BUTTON" && e.Item.Id is { } id && UiScreens.Links.FirstOrDefault(l => l.From == Screen.Name && l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { To: { } to }
                && UiScreens.All.FirstOrDefault(s => s.Name == to) is { } target)
                return (e, target);
            if (IsCloseButton(e)) return (e, null);
            if (e.Kind == "BUTTON") return (null, null);
        }
        // aree che aprono un'altra schermata (la lista delle missioni del TE)
        for (int i = _els.Count - 1; i >= 0; i--)
        {
            var e = _els[i];
            if (!(x >= e.Win.X && y >= e.Win.Y && x < e.Win.X + e.Win.W && y < e.Win.Y + e.Win.H)) continue;
            if (UiScreens.AreaLinks.FirstOrDefault(l => l.From == Screen.Name && l.WindowId.Equals(e.Win.Id, StringComparison.OrdinalIgnoreCase)) is { To: { } to }
                && UiScreens.All.FirstOrDefault(s => s.Name == to) is { } target)
                return (e, target);
        }
        return (null, null);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs ev)
    {
        if (!Editing) return;
        var e = Hit(ev.GetPosition(_over));
        if (e == null) return;
        Select(e);
        if (MenuFor?.Invoke(e) is { } m) { m.PlacementTarget = _over; m.IsOpen = true; }
    }

    private void OnMove(object sender, MouseEventArgs ev)
    {
        var p = ev.GetPosition(_over);
        var e = Hit(p);
        var (link, target) = Editing ? default((UiElement?, UiScreen?)) : Link(p);
        bool nav = target != null, close = link != null && target == null;
        if (link != null) e = link;
        _over.Cursor = Editing && e != null || nav || close ? Cursors.Hand : Cursors.Arrow;
        _over.ToolTip = e == null ? null : Editing ? $"{e.Label}{(e.Image != null ? " · " + e.Image : "")}{(e.TextKey != null ? " · " + e.TextKey : "")}" : nav ? "Go to " + target!.Name : close ? "Back" : null;
    }
}
