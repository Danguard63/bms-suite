using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>
/// Aspetto e suono di un pulsante come nei vecchi BMS: immagine normale, premuta (es. in rilievo e poi schiacciata) e disattivata,
/// spostamento e colore del testo quando si preme, suono al clic e al passaggio del mouse. Tutto e' scritto nei file .scf
/// ([IMAGES]/[BUTTONIMAGE], [TEXTOFFSET], [UPCOLOR]/[DOWNCOLOR], [SOUNDBITE]); le immagini nuove vanno nell'archivio del teatro.
/// </summary>
public sealed class ButtonDialog : Window
{
    public const string Keep = "Keep as it is", Same = "Same as the normal image (flat button)", Make = "Make it from the normal image", File = "Image file…";

    private readonly UiArt _ui;
    private readonly UiElement _e;
    private readonly Pic? _normal;
    private readonly ComboBox _pressMode = new() { Width = 330 }, _disMode = new() { Width = 330 };
    private readonly Image _imgN = Img(), _imgP = Img(), _imgD = Img(), _try = Img();
    private readonly TextBox _dx = Num(), _dy = Num(), _up = new() { Width = 110 }, _down = new() { Width = 110 };
    private readonly ComboBox _sndPress = new() { Width = 260 }, _sndOver = new() { Width = 260 };
    private readonly RadioButton _one = new() { IsChecked = true, Margin = new Thickness(0, 0, 0, 4) }, _all = new();
    private Pic? _pressedPic, _disabledPic;
    private string? _pressedFile, _disabledFile;
    private readonly string? _curPressed, _curDisabled;
    private readonly Func<string, string?> _soundFile;

    public ButtonStyle Result { get; } = new();

    private static Image Img() => new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true };
    private static TextBox Num() => new() { Width = 40, Margin = new Thickness(4, 0, 10, 0) };

    public static (string? N, string? P, string? Third, string? D, bool ImagesLine) States(ScfItem it) => ButtonEdit.States(it);

    public ButtonDialog(UiArt ui, UiElement e, IEnumerable<string> sounds, int sameCount, Func<string, string?> soundFile)
    {
        _ui = ui; _e = e; _soundFile = soundFile;
        Title = "Button look and sound · " + e.Label;
        Width = 820; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var st = States(e.Item);
        Result.Normal = st.N ?? "";
        _curPressed = st.P; _curDisabled = st.D;
        var ir = st.N != null ? ui.FindImage(st.N) : null;
        if (ir != null) { Result.Archive = ir.Archive; _normal = ui.Image(st.N!); }

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "As in the old BMS versions, a button can have one image when it is up (for example raised) and another while it is pressed (flat, pushed in), " +
                   "move its text when pressed and make a sound. Most BMS 4.38 buttons use the same image for both, so they look flat. " +
                   "New images go into the theater's copy of the archive; the window files (.scf) changed are the theater's copies: the KTO is not touched.",
        });

        // immagini
        var imgs = new StackPanel();
        if (_normal == null)
            imgs.Children.Add(new TextBlock { Text = st.N == null ? "This button has no image (text only)." : $"{st.N} is not in the art archives (BMS draws nothing for it): give the button an existing image first with 'Change image…'.", Foreground = Brushes.DimGray });
        else
        {
            foreach (var cb in new[] { _pressMode, _disMode }) { cb.Items.Add(Keep); cb.Items.Add(Same); cb.Items.Add(Make); cb.Items.Add(File); cb.SelectedIndex = 0; }
            bool sticky = ButtonEdit.Sticky(e.Item);
            bool toggle = ButtonEdit.Toggle(e.Item);
            imgs.Children.Add(Row(toggle ? $"On (now: {st.P ?? "none"})" : sticky ? $"Selected (now: {st.P ?? "none"})" : $"Pressed (now: {st.P ?? "none"})", _pressMode));
            if (toggle)
                imgs.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 2, 0, 6),
                    Text = "This is an on/off button (like the options in Setup): every click switches it, and BMS shows this image while it is on.",
                });
            else if (sticky)
                imgs.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 2, 0, 6),
                    Text = "This is a radio button (like the main menu items): BMS shows this image while the button is the one chosen, and it stays like that until another one " +
                           "of the group is chosen. A darker image here means: the menu item of the open screen stays darker. For a highlight that is what you want; " +
                           "for a 'pushed in' effect only while clicking, use a normal (C_TYPE_NORMAL) button.",
                });
            imgs.Children.Add(Row($"Disabled (now: {st.D ?? "none"})", _disMode));
            var pv = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            pv.Children.Add(Box("Normal", _imgN)); pv.Children.Add(Box("Pressed", _imgP)); pv.Children.Add(Box("Disabled", _imgD));
            imgs.Children.Add(pv);
            _pressMode.SelectionChanged += (_, _) => Pick(_pressMode, true);
            _disMode.SelectionChanged += (_, _) => Pick(_disMode, false);
        }
        root.Children.Add(Group("Images", imgs));

        // testo
        var tx = new StackPanel();
        var off = e.Item.All("TEXTOFFSET").FirstOrDefault(a => a.Length >= 3 && a[0].Equals("C_STATE_DOWN", StringComparison.OrdinalIgnoreCase));
        _dx.Text = off?[1] ?? "0"; _dy.Text = off?[2] ?? "0";
        var upc = e.Item.All("TEXTCOLOR").FirstOrDefault(a => a.Length >= 4 && a[0] is "C_STATE_0" or "C_STATE_UP")?.Skip(1) ?? e.Item.Get("UPCOLOR");
        var dnc = e.Item.All("TEXTCOLOR").FirstOrDefault(a => a.Length >= 4 && a[0] is "C_STATE_1" or "C_STATE_DOWN")?.Skip(1) ?? e.Item.Get("DOWNCOLOR");
        _up.Text = upc != null ? string.Join(" ", upc.Take(3)) : ""; _down.Text = dnc != null ? string.Join(" ", dnc.Take(3)) : "";
        var r1 = new StackPanel { Orientation = Orientation.Horizontal };
        r1.Children.Add(new TextBlock { Text = "When pressed, move the text by  X", VerticalAlignment = VerticalAlignment.Center }); r1.Children.Add(_dx);
        r1.Children.Add(new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center }); r1.Children.Add(_dy);
        r1.Children.Add(new TextBlock { Text = "pixels (1 1 = pushed in)", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center });
        tx.Children.Add(r1);
        var r2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        r2.Children.Add(new TextBlock { Text = "Text colour (R G B): normal ", VerticalAlignment = VerticalAlignment.Center }); r2.Children.Add(_up);
        r2.Children.Add(new TextBlock { Text = "   pressed ", VerticalAlignment = VerticalAlignment.Center }); r2.Children.Add(_down);
        r2.Children.Add(new TextBlock { Text = "  (empty = as now)", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center });
        tx.Children.Add(r2);
        if (string.IsNullOrEmpty(e.Text)) tx.IsEnabled = false;
        root.Children.Add(Group("Text" + (string.IsNullOrEmpty(e.Text) ? " (this button has no text)" : ""), tx));

        // prova
        var tryBox = new StackPanel();
        tryBox.Children.Add(new TextBlock { Text = "Press the button below with the mouse to see it pressed (and hear its sound):", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) });
        var bd = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)), Padding = new Thickness(10), Child = _try, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        bd.MouseLeftButtonDown += (_, _) => { ShowTry(true); PlayChosen(_sndPress); };
        bd.MouseLeftButtonUp += (_, _) => ShowTry(false);
        bd.MouseLeave += (_, _) => ShowTry(false);
        bd.MouseEnter += (_, _) => PlayChosen(_sndOver);
        tryBox.Children.Add(bd);
        root.Children.Add(Group("Try it", tryBox));

        // suoni
        var sn = new StackPanel();
        string? curPress = e.Item.All("SOUNDBITE").FirstOrDefault(a => a.Length > 1 && ButtonEdit.PressEvents.Contains(a[1], StringComparer.OrdinalIgnoreCase))?[0];
        string? curOver = e.Item.All("SOUNDBITE").FirstOrDefault(a => a.Length > 1 && a[1] == "C_TYPE_MOUSEOVER")?[0];
        foreach (var (cb, cur) in new[] { (_sndPress, curPress), (_sndOver, curOver) })
        {
            cb.Items.Add("(none)");
            foreach (var s in sounds.OrderBy(x => x)) cb.Items.Add(s);
            cb.SelectedItem = cur != null && cb.Items.Contains(cur) ? cur : "(none)";
        }
        sn.Children.Add(Row("When clicked", _sndPress, PlayBtn(_sndPress)));
        sn.Children.Add(Row("When the mouse goes over it", _sndOver, PlayBtn(_sndOver)));
        sn.Children.Add(new TextBlock { Text = "The sound files themselves are changed in the Sounds section.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(Group("Sound", sn));

        // a chi
        var who = new StackPanel();
        _one.Content = "Only this button";
        _all.Content = sameCount > 1 && st.N != null ? $"All the {sameCount} buttons that use the image {st.N} (on every screen)" : "All the buttons with the same image";
        _all.IsEnabled = sameCount > 1 && st.N != null;
        who.Children.Add(_one); who.Children.Add(_all);
        root.Children.Add(Group("Apply to", who));

        var ok = new Button { Content = "Apply", Padding = new Thickness(16, 4, 16, 4), IsDefault = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Ok();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _pressedPic = _curPressed != null ? ui.Image(_curPressed) : null;
        _disabledPic = _curDisabled != null ? ui.Image(_curDisabled) : null;
        foreach (var t in new[] { _dx, _dy, _up, _down }) t.TextChanged += (_, _) => ShowTry(false);
        Loaded += (_, _) => { Previews(); ShowTry(false); };
    }

    private static GroupBox Group(string h, UIElement c) => new() { Header = h, Content = c, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };

    private static StackPanel Row(string label, params UIElement[] c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center });
        foreach (var x in c) p.Children.Add(x);
        return p;
    }

    private Button PlayBtn(ComboBox cb)
    {
        var b = new Button { Content = "▶", Width = 28, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Listen" };
        b.Click += (_, _) => PlayChosen(cb);
        return b;
    }

    private void PlayChosen(ComboBox cb)
    {
        if (cb.SelectedItem is not string s || s == "(none)") return;
        try { if (_soundFile(s) is { } f) Sound.Play(f); } catch (Exception) { }
    }

    private static Border Box(string label, Image img)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        sp.Children.Add(img);
        return new Border { Child = sp, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)), Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 0) };
    }

    private void Pick(ComboBox cb, bool pressed)
    {
        if (_normal == null) return;
        string mode = cb.SelectedItem as string ?? Keep;
        Pic? pic = null; string? file = null;
        if (mode == Keep) pic = pressed ? (_curPressed != null ? _ui.Image(_curPressed) : null) : (_curDisabled != null ? _ui.Image(_curDisabled) : null);
        else if (mode == Same) pic = _normal;
        else if (mode == Make) pic = pressed ? ImageOps.Pressed(_normal) : ImageOps.Disabled(_normal);
        else
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = pressed ? "Image of the pressed button" : "Image of the disabled button", Filter = ImageOps.OpenFilter };
            if (d.ShowDialog(this) != true) { cb.SelectedIndex = 0; return; }
            try
            {
                var src = ImageOps.Load(d.FileName);
                pic = ImageOps.Resize(ImageOps.Crop(src, ImageOps.CenterCrop(src.W, src.H, _normal.Aspect)), _normal.W, _normal.H);
                file = d.FileName;
            }
            catch (Exception ex) { MessageBox.Show(this, "Cannot read the image: " + ex.Message, Title); cb.SelectedIndex = 0; return; }
        }
        if (pressed) { _pressedPic = pic; _pressedFile = file; } else { _disabledPic = pic; _disabledFile = file; }
        Previews(); ShowTry(false);
    }

    private void Previews()
    {
        _imgN.Source = _normal != null ? ImageOps.ToBitmap(_normal) : null;
        _imgP.Source = _pressedPic != null ? ImageOps.ToBitmap(_pressedPic) : null;
        _imgD.Source = _disabledPic != null ? ImageOps.ToBitmap(_disabledPic) : null;
    }

    private static (byte, byte, byte)? Rgb(string s)
    {
        var p = s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 3 && byte.TryParse(p[0], out var r) && byte.TryParse(p[1], out var g) && byte.TryParse(p[2], out var b) ? (r, g, b) : null;
    }

    /// <summary>Il pulsante come lo disegna BMS, normale o premuto (immagine, testo spostato e colorato).</summary>
    private void ShowTry(bool down)
    {
        var bg = down ? (_pressedPic ?? _normal) : _normal;
        var f = _ui.Font(_e.Font);
        int w = bg?.W ?? Math.Max(60, (f?.Width(_e.Text ?? "") ?? 40) + 20), h = bg?.H ?? (f?.Height ?? 16) + 10;
        var c = new byte[w * h * 4];
        if (bg != null) Array.Copy(bg.Bgra, c, Math.Min(c.Length, bg.Bgra.Length));
        if (!string.IsNullOrEmpty(_e.Text) && f != null)
        {
            var col = (down ? Rgb(_down.Text) : Rgb(_up.Text)) ?? _e.Color;
            int tx = (w - f.Width(_e.Text)) / 2, ty = (h - f.Height) / 2;
            if (down && int.TryParse(_dx.Text, out int dx) && int.TryParse(_dy.Text, out int dy)) { tx += dx; ty += dy; }
            f.Draw(c, w, h, _e.Text, tx, ty, col);
        }
        _try.Source = ImageOps.ToBitmap(new Pic(w, h, c));
    }

    private void Ok()
    {
        var r = Result;
        r.AllSame = _all.IsChecked == true;
        if (_normal != null)
        {
            string P(string suffix) { string n = r.Normal + suffix; return n.Length > 31 ? n[..31] : n; }
            string pm = _pressMode.SelectedItem as string ?? Keep, dm = _disMode.SelectedItem as string ?? Keep;
            if (pm == Same) r.PressedName = r.Normal;
            else if (pm is Make or File) { r.PressedName = P("_PRESSED"); r.PressedNew = _pressedPic; }
            if (dm == Same) r.DisabledName = r.Normal;
            else if (dm is Make or File) { r.DisabledName = P("_DISABLED"); r.DisabledNew = _disabledPic; }
        }
        if (!string.IsNullOrEmpty(_e.Text))
        {
            if (!int.TryParse(_dx.Text, out int dx) || !int.TryParse(_dy.Text, out int dy)) { MessageBox.Show(this, "Text move: write two whole numbers (e.g. 1 and 1).", Title); return; }
            r.DownOffset = (dx, dy);
            if (_up.Text.Trim().Length > 0) { if (Rgb(_up.Text) is { } u) r.Up = u; else { MessageBox.Show(this, "Normal text colour: three numbers 0-255, e.g. 230 230 230.", Title); return; } }
            if (_down.Text.Trim().Length > 0) { if (Rgb(_down.Text) is { } d) r.Down = d; else { MessageBox.Show(this, "Pressed text colour: three numbers 0-255, e.g. 0 255 0.", Title); return; } }
        }
        r.PressSound = _sndPress.SelectedItem is string sp ? (sp == "(none)" ? "" : sp) : null;
        r.OverSound = _sndOver.SelectedItem is string so ? (so == "(none)" ? "" : so) : null;
        DialogResult = true;
    }
}
