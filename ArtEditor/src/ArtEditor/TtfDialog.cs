using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>
/// Converte un font TrueType/OpenType (installato in Windows o da file) in un font dell'interfaccia di BMS (.bft), con l'anteprima
/// esatta di come lo disegnera' BMS (pixel a 1 bit). Per sostituire un font esistente la misura si sceglie da sola: la stessa altezza
/// di riga, cosi' i testi restano nei loro spazi.
/// </summary>
public sealed class TtfDialog : Window
{
    private readonly ComboBox _family = new() { Width = 300, IsEditable = false };
    private readonly RadioButton _srcSys = new() { Content = "Font installed in Windows", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
    private readonly RadioButton _srcFile = new() { Content = "Font file (.ttf / .otf)", Margin = new Thickness(0, 6, 0, 4) };
    private readonly TextBlock _fileName = new() { Foreground = Brushes.DimGray, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _bold = new() { Content = "Bold", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _italic = new() { Content = "Italic" };
    private readonly RadioButton _sizeSame = new() { IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
    private readonly RadioButton _sizeEm = new() { Content = "Size (pixels):" , VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _em = new() { Width = 50, Text = "14", Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _name = new() { Width = 220 };
    private readonly TextBox _sample = new() { Width = 520, Text = "Tactical Engagement  0123456789  ÀÉÈÌÒÙ àéèìòù" };
    private readonly Image _oldImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Image _newImg = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly BftFont? _old;
    private readonly int _target;
    private readonly bool _adding;
    private string? _file, _fileFace;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public byte[]? Result { get; private set; }
    public int ResultHeight { get; private set; }
    public string ResultFace { get; private set; } = "";
    public string NewName => _name.Text.Trim().ToUpperInvariant();

    /// <param name="old">font da sostituire (null = font nuovo)</param>
    public TtfDialog(string title, BftFont? old, string? oldName, IEnumerable<string> existingNames)
    {
        Title = title;
        Width = 900; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _old = old; _adding = old == null; _target = old?.Height ?? 0;
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n)) _family.Items.Add(f);
        if (old != null && _family.Items.Contains(old.Face)) _family.SelectedItem = old.Face;
        else _family.SelectedItem = _family.Items.Contains("Arial") ? "Arial" : _family.Items.Cast<object>().FirstOrDefault();

        var browse = new Button { Content = "Choose file…", Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) => Browse();

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = _adding
                ? "A new interface font for this theater. BMS draws its interface with pixel fonts at a fixed height (1 bit, no smoothing): the TrueType font is converted " +
                  "at the size you choose. After saving, the new font appears in 'Change font…' of the visual editor."
                : $"Replaces {oldName} for this theater with a TrueType font. The KTO font is not touched: the new file goes into the theater's Art\\Fonts. " +
                  "Keeping the same line height the texts stay in their places on every screen.",
        });

        var src = new StackPanel();
        src.Children.Add(_srcSys);
        var r1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) }; r1.Children.Add(_family); src.Children.Add(r1);
        src.Children.Add(_srcFile);
        var r2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) }; r2.Children.Add(browse); r2.Children.Add(_fileName); src.Children.Add(r2);
        var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 8, 0, 0) }; st.Children.Add(_bold); st.Children.Add(_italic); src.Children.Add(st);
        root.Children.Add(Group("1. Font", src));

        var size = new StackPanel();
        if (!_adding) { _sizeSame.Content = $"Same line height as {oldName} ({_target} px) - recommended"; size.Children.Add(_sizeSame); }
        else _sizeEm.IsChecked = true;
        var r3 = new StackPanel { Orientation = Orientation.Horizontal }; r3.Children.Add(_sizeEm); r3.Children.Add(_em);
        r3.Children.Add(new TextBlock { Text = "  (like the '14' of OCR_EXT_14: the BMS fonts go from 10 to 26, titles 24-56)", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center });
        size.Children.Add(r3);
        root.Children.Add(Group("2. Size", size));

        if (_adding)
        {
            var nm = new StackPanel { Orientation = Orientation.Horizontal };
            nm.Children.Add(_name);
            nm.Children.Add(new TextBlock { Text = "  capital letters, numbers and _ (e.g. ME_TITLE_24)", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(Group("3. Name of the new font", nm));
        }

        var pv = new StackPanel();
        var sr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sr.Children.Add(new TextBlock { Text = "Text: ", VerticalAlignment = VerticalAlignment.Center }); sr.Children.Add(_sample);
        pv.Children.Add(sr);
        var dark = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)) };
        if (!_adding)
        {
            dark.Children.Add(new TextBlock { Text = "Now (" + oldName + ")", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(6, 4, 0, 2) });
            dark.Children.Add(new ScrollViewer { Content = _oldImg, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(6, 0, 6, 4) });
        }
        dark.Children.Add(new TextBlock { Text = "New (as BMS will draw it, shown 2× larger)", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(6, 4, 0, 2) });
        dark.Children.Add(new ScrollViewer { Content = _newImg, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(6, 0, 6, 6) });
        pv.Children.Add(dark);
        pv.Children.Add(_info);
        root.Children.Add(Group(_adding ? "4. Preview" : "3. Preview", pv));

        var ok = new Button { Content = _adding ? "Add the font" : "Use this font", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Ok(existing);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        root.Children.Add(bar);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        if (old != null) _em.Text = Math.Max(4, old.Height - 2).ToString();
        foreach (var c in new Control[] { _srcSys, _srcFile, _bold, _italic, _sizeSame, _sizeEm })
            if (c is System.Windows.Controls.Primitives.ToggleButton t) { t.Checked += (_, _) => Later(); t.Unchecked += (_, _) => Later(); }
        _family.SelectionChanged += (_, _) => { _srcSys.IsChecked = true; Later(); };
        _em.TextChanged += (_, _) => { _sizeEm.IsChecked = true; Later(); };
        _sample.TextChanged += (_, _) => Later();
        Loaded += (_, _) => Update();
    }

    private static GroupBox Group(string h, UIElement c) => new() { Header = h, Content = c, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };

    private void Browse()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "TrueType / OpenType font", Filter = "Fonts (*.ttf;*.otf)|*.ttf;*.otf|All files|*.*" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            _fileFace = BftMaker.RegisterFile(d.FileName);
            _file = d.FileName;
            _fileName.Text = $"{Path.GetFileName(_file)}  ({_fileFace})";
            _srcFile.IsChecked = true;
            Update();
        }
        catch (Exception ex) { MessageBox.Show(this, "Cannot read the font: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Later()
    {
        _timer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background, (_, _) => { _timer!.Stop(); Update(); }, Dispatcher);
        _timer.Stop(); _timer.Start();
    }

    private string? Face => _srcFile.IsChecked == true ? _fileFace : _family.SelectedItem as string;

    private BftMaker.Options? Opts()
    {
        if (Face is not { } face) return null;
        var o = new BftMaker.Options(face, 14, _bold.IsChecked == true, _italic.IsChecked == true, _srcFile.IsChecked == true ? _file : null);
        if (!_adding && _sizeSame.IsChecked == true) return o with { EmPx = BftMaker.FitEm(o, _target) };
        return int.TryParse(_em.Text, out int em) && em is >= 4 and <= 200 ? o with { EmPx = em } : null;
    }

    private void Update()
    {
        string sample = string.IsNullOrEmpty(_sample.Text) ? "Sample" : _sample.Text;
        if (_old != null) _oldImg.Source = Draw(_old, sample, 1, out _);
        var o = Opts();
        if (o == null) { _newImg.Source = null; Result = null; _info.Text = _srcFile.IsChecked == true ? "Choose a font file." : "Write a size between 4 and 200."; return; }
        try
        {
            var (bytes, h, missing) = BftMaker.Make(o);
            var f = BftFont.Load(bytes, "new");
            _newImg.Source = Draw(f, sample, 2, out int w);
            if (_old != null) _oldImg.Source = Draw(_old, sample, 2, out _);
            Result = bytes; ResultHeight = h; ResultFace = o.Face;
            string msg = $"{o.Face}{(o.Bold ? " Bold" : "")}{(o.Italic ? " Italic" : "")}, size {o.EmPx}: line height {h} px, this text {w} px wide";
            if (_old != null)
            {
                int ow = _old.Width(sample);
                msg += $" (now {_old.Height} px high, {ow} px wide).";
                if (h > _old.Height) msg += "\n⚠ Taller than the font it replaces: texts may be cut or overlap.";
                if (w > ow * 1.15) msg += "\n⚠ Wider than the font it replaces: long texts may not fit their buttons. A narrower font, or a smaller size, is safer.";
            }
            else msg += ".";
            if (missing > 0) msg += $"\n⚠ {missing} characters of the BMS set (accented letters, symbols) are not in this font: they will be drawn empty or replaced.";
            _info.Text = msg;
            _info.Foreground = msg.Contains('⚠') ? Brushes.DarkOrange : Brushes.Black;
        }
        catch (Exception ex) { _newImg.Source = null; Result = null; _info.Text = "Cannot convert this font: " + ex.Message; }
    }

    private static ImageSource Draw(BftFont f, string s, int scale, out int width)
    {
        width = f.Width(s);
        int w = Math.Clamp(width + 4, 8, 4000), h = f.Height + 2;
        var c = new byte[w * h * 4];
        f.Draw(c, w, h, s, 2, 1, (230, 230, 230));
        if (scale > 1)
        {
            var big = new byte[w * scale * h * scale * 4];
            for (int y = 0; y < h * scale; y++)
                for (int x = 0; x < w * scale; x++)
                    Array.Copy(c, 4 * ((y / scale) * w + x / scale), big, 4 * (y * w * scale + x), 4);
            return ImageOps.ToBitmap(new Pic(w * scale, h * scale, big));
        }
        return ImageOps.ToBitmap(new Pic(w, h, c));
    }

    private void Ok(HashSet<string> existing)
    {
        Update();
        if (Result == null) { MessageBox.Show(this, _info.Text, Title); return; }
        if (_adding)
        {
            string n = NewName;
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Z][A-Z0-9_]{1,30}$"))
            { MessageBox.Show(this, "Name: capital letters, numbers and _, starting with a letter (e.g. ME_TITLE_24).", Title); return; }
            if (existing.Contains(n)) { MessageBox.Show(this, $"A font called {n} already exists.", Title); return; }
        }
        DialogResult = true;
    }
}
