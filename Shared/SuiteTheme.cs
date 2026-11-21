// One look for every program of the BMS Suite (this file is compiled into each of them: see the <Compile Include> of
// their .csproj). The main menu is dark; the programs keep a light work area (maps, tables, long texts) and take from the
// menu its graphite, its single amber accent, the buttons and a title strip, so opening a program does not feel like
// leaving the suite.
global using MessageBox = BmsSuiteUi.SuiteMessageBox;

using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BmsSuiteUi;

/// <summary>
/// The shared look: SuiteTheme.Apply(app) once at start, before the first window. Nothing in the programs has to change:
/// <list type="bullet">
/// <item>buttons: flat, rounded; a button written in semibold or bold is the main action of its window (amber); one
/// whose text starts with Install, Restore, Remove or Delete writes into BMS or undoes it (graphite);</item>
/// <item>windows: light grey ground; the main windows (no owner) get a graphite strip with icon and title;</item>
/// <item>tables: light headers, soft grid lines, amber selection.</item>
/// </list>
/// A value set on a control by the program itself always wins over the theme.
/// </summary>
internal static class SuiteTheme
{
    public static readonly Color Ink = Color.FromRgb(0x17, 0x19, 0x1C);
    public static readonly Color Graphite = Color.FromRgb(0x20, 0x23, 0x27);
    public static readonly Color Amber = Color.FromRgb(0xF2, 0xA9, 0x00);
    public static readonly Color Ground = Color.FromRgb(0xF4, 0xF5, 0xF7);
    public static readonly Color Edge = Color.FromRgb(0xB4, 0xBA, 0xC1);
    public static readonly Color Text = Color.FromRgb(0x1F, 0x23, 0x28);
    public static readonly Color Muted = Color.FromRgb(0x5C, 0x63, 0x6B);
    public static readonly Color Red = Color.FromRgb(0xC6, 0x3A, 0x2E);

    public static SolidColorBrush B(Color c, byte a = 255)
    {
        var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>True on a window that shows the graphite title strip (set for the windows without an owner).</summary>
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.RegisterAttached("Header", typeof(bool), typeof(SuiteTheme), new PropertyMetadata(false));
    public static void SetHeader(DependencyObject o, bool v) => o.SetValue(HeaderProperty, v);
    public static bool GetHeader(DependencyObject o) => (bool)o.GetValue(HeaderProperty);

    private static bool _applied;

    public static void Apply(Application app)
    {
        if (_applied) return;
        _applied = true;
        try
        {
            var r = app.Resources;
            r[typeof(Button)] = ButtonStyle(r.Contains(typeof(Button)) ? r[typeof(Button)] as Style : null);
            r[typeof(DataGrid)] = DataGridStyle();
            r[typeof(DataGridColumnHeader)] = HeaderStyle();
            r[typeof(ProgressBar)] = Simple(typeof(ProgressBar), (Control.ForegroundProperty, B(Amber)), (Control.BackgroundProperty, B(Color.FromRgb(0xE3, 0xE6, 0xEA))), (Control.BorderBrushProperty, B(Edge)));
            r[typeof(GroupBox)] = Simple(typeof(GroupBox), (Control.BorderBrushProperty, B(Color.FromRgb(0xD3, 0xD7, 0xDC))), (Control.PaddingProperty, new Thickness(6)));
            r[typeof(TextBox)] = Simple(typeof(TextBox), (Control.BorderBrushProperty, B(Edge)));

            // a style for Window does not reach the classes made from it: one for each window class of the program
            var template = WindowTemplate();
            var types = new List<Type> { typeof(Window) };
            try { if (Assembly.GetEntryAssembly() is { } asm) types.AddRange(asm.GetTypes().Where(t => t.IsSubclassOf(typeof(Window)) && !t.IsAbstract && !t.IsGenericTypeDefinition)); }
            catch (ReflectionTypeLoadException) { }
            foreach (var t in types)
            {
                var s = new Style(t);
                s.Setters.Add(new Setter(Control.BackgroundProperty, B(Ground)));
                s.Setters.Add(new Setter(Control.TemplateProperty, template));
                s.Seal();
                r[t] = s;
            }
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is not Window w || !ReferenceEquals(e.OriginalSource, w)) return;
                if (w.Owner == null && w is not SuiteMessageBox.Dialog)
                {
                    // the strip shows the window's icon: a window without one of its own takes the program's
                    if (w.Icon == null && ProgramIcon() is { } icon) w.Icon = icon;
                    SetHeader(w, true);
                }
                if (w is not SuiteMessageBox.Dialog && (w.SizeToContent & SizeToContent.Width) == 0) FoldHelp(w, 0);
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // the look is a comfort: without it the program still works, with the look of Windows
        }
    }

    /// <summary>
    /// A button that opens a menu of other buttons (kept out of the window): the toolbar shows one entry per group instead
    /// of one per tool. Each entry does what its button does, has its tooltip and is greyed when the button is disabled.
    /// </summary>
    public static Button MenuButton(string title, IEnumerable<Button> items, string? tip = null)
    {
        var list = items.ToList();
        var b = new Button { Content = title + "  ▾", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        foreach (var it in list)
        {
            var src = it;
            var mi = new MenuItem { Header = (it.Content as string ?? it.Content?.ToString() ?? "").Replace("_", "__") };
            if (it.ToolTip is string tt) mi.ToolTip = new TextBlock { Text = tt, MaxWidth = 460, TextWrapping = TextWrapping.Wrap };
            mi.Click += (_, _) => src.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            menu.Items.Add(mi);
        }
        b.Click += (_, _) =>
        {
            for (int i = 0; i < list.Count; i++) ((MenuItem)menu.Items[i]!).IsEnabled = list[i].IsEnabled;
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>
    /// The long grey explanations above the panels: one line with "…" until clicked (the whole text also shows when the
    /// mouse stays on it), so they do not take the room of the work area once read.
    /// </summary>
    private static void FoldHelp(DependencyObject o, int depth)
    {
        if (depth > 40) return;
        if (o is TextBlock t)
        {
            if (t.TextWrapping != TextWrapping.NoWrap && t.ToolTip == null && t.Inlines.Count <= 1 && t.Text.Length > 170 && t.FontWeight == FontWeights.Normal
                && t.Foreground is SolidColorBrush { Color: var c } && (c == Colors.DimGray || c == Colors.Gray))
            {
                var wrap = t.TextWrapping;
                t.TextWrapping = TextWrapping.NoWrap;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.Cursor = Cursors.Hand;
                t.ToolTip = new ToolTip { Content = new TextBlock { Text = t.Text + "\n\n(click the text to keep it open)", MaxWidth = 560, TextWrapping = TextWrapping.Wrap } };
                ToolTipService.SetShowDuration(t, 60000);
                t.MouseLeftButtonUp += (_, _) =>
                {
                    bool open = t.TextWrapping == TextWrapping.NoWrap;
                    t.TextWrapping = open ? wrap : TextWrapping.NoWrap;
                    t.TextTrimming = open ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                };
            }
            return;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) FoldHelp(d, depth + 1);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static ImageSource? _icon;
    private static bool _iconRead;

    /// <summary>The icon of the program's exe (the one Windows shows in the title bar), or null.</summary>
    private static ImageSource? ProgramIcon()
    {
        if (_iconRead) return _icon;
        _iconRead = true;
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                IntPtr h = ExtractIcon(IntPtr.Zero, exe, 0);
                if (h != IntPtr.Zero && h != new IntPtr(1))
                {
                    try
                    {
                        var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        _icon = bmp;
                    }
                    finally { DestroyIcon(h); }
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
        return _icon;
    }

    private static Style Simple(Type t, params (DependencyProperty P, object V)[] setters)
    {
        var s = new Style(t);
        foreach (var (p, v) in setters) s.Setters.Add(new Setter(p, v));
        s.Seal();
        return s;
    }

    /// <summary>True when the button's text says it writes into BMS or undoes an installation.</summary>
    private sealed class WritesConverter : IValueConverter
    {
        private static readonly string[] Words = { "Install", "Restore", "Remove", "Delete" };
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string s && Words.Any(w => s.StartsWith(w, StringComparison.Ordinal));
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private static Style ButtonStyle(Style? basedOn)
    {
        var s = basedOn != null ? new Style(typeof(Button), basedOn) : new Style(typeof(Button));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, B(Edge)));
        s.Setters.Add(new Setter(Control.ForegroundProperty, B(Text)));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        if (basedOn == null) s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 3, 10, 3)));
        s.Setters.Add(new Setter(FrameworkElement.SnapsToDevicePixelsProperty, true));

        // ground, a veil that darkens it under the mouse, the text
        var root = new FrameworkElementFactory(typeof(Grid));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        root.AppendChild(bd);
        var veil = new FrameworkElementFactory(typeof(Border), "veil");
        veil.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        veil.SetValue(Border.BackgroundProperty, B(Colors.Black, 20));
        veil.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        veil.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(veil);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        cp.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        root.AppendChild(cp);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = root };
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "veil"));
        t.Triggers.Add(over);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "veil"));
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, B(Colors.Black, 46), "veil"));
        t.Triggers.Add(pressed);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, B(Ink), "bd"));
        t.Triggers.Add(focus);
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        t.Triggers.Add(off);
        t.Seal();
        s.Setters.Add(new Setter(Control.TemplateProperty, t));

        // the main action of a window: the programs write it in semibold or bold
        foreach (var weight in new[] { FontWeights.SemiBold, FontWeights.Bold })
        {
            var main = new Trigger { Property = Control.FontWeightProperty, Value = weight };
            main.Setters.Add(new Setter(Control.BackgroundProperty, B(Amber)));
            main.Setters.Add(new Setter(Control.BorderBrushProperty, B(Color.FromRgb(0xC9, 0x8C, 0x00))));
            main.Setters.Add(new Setter(Control.ForegroundProperty, B(Ink)));
            s.Triggers.Add(main);
        }
        // writes into BMS (or undoes it): graphite, whatever its weight
        var writes = new DataTrigger { Binding = new Binding("Content") { RelativeSource = RelativeSource.Self, Converter = new WritesConverter() }, Value = true };
        writes.Setters.Add(new Setter(Control.BackgroundProperty, B(Graphite)));
        writes.Setters.Add(new Setter(Control.BorderBrushProperty, B(Ink)));
        writes.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        s.Triggers.Add(writes);
        s.Seal();
        return s;
    }

    private static Style DataGridStyle()
    {
        var s = new Style(typeof(DataGrid));
        s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        s.Setters.Add(new Setter(Control.BorderBrushProperty, B(Color.FromRgb(0xD3, 0xD7, 0xDC))));
        s.Setters.Add(new Setter(DataGrid.HorizontalGridLinesBrushProperty, B(Color.FromRgb(0xE6, 0xE8, 0xEB))));
        s.Setters.Add(new Setter(DataGrid.VerticalGridLinesBrushProperty, B(Color.FromRgb(0xEE, 0xF0, 0xF2))));
        // selection: amber tint with dark text instead of the blue of Windows
        s.Resources[SystemColors.HighlightBrushKey] = B(Color.FromRgb(0xFF, 0xE2, 0x9A));
        s.Resources[SystemColors.HighlightTextBrushKey] = B(Text);
        s.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = B(Color.FromRgb(0xF1, 0xE7, 0xCC));
        s.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = B(Text);
        s.Seal();
        return s;
    }

    private static Style HeaderStyle()
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(new Setter(Control.ForegroundProperty, B(Muted)));
        s.Seal();
        return s;
    }

    /// <summary>The window: (title strip, only where Header is set) over the content, on the window's ground.</summary>
    private static ControlTemplate WindowTemplate()
    {
        var ground = new FrameworkElementFactory(typeof(Border));
        ground.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        ground.AppendChild(dock);

        var strip = new FrameworkElementFactory(typeof(Border), "strip");
        strip.SetValue(DockPanel.DockProperty, Dock.Top);
        strip.SetValue(Border.BackgroundProperty, B(Graphite));
        strip.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 8));
        strip.SetValue(TextElement.ForegroundProperty, B(Color.FromRgb(0xE6, 0xE4, 0xDE)));
        strip.SetBinding(UIElement.VisibilityProperty, new Binding
        {
            Path = new PropertyPath("(0)", HeaderProperty), RelativeSource = RelativeSource.TemplatedParent, Converter = new BooleanToVisibilityConverter(),
        });
        var row = new FrameworkElementFactory(typeof(DockPanel));
        strip.AppendChild(row);
        var suite = new FrameworkElementFactory(typeof(TextBlock));
        suite.SetValue(DockPanel.DockProperty, Dock.Right);
        suite.SetValue(TextBlock.TextProperty, "BMS SUITE");
        suite.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
        suite.SetValue(TextBlock.FontSizeProperty, 11.0);
        suite.SetValue(TextBlock.ForegroundProperty, B(Color.FromRgb(0xA4, 0xA8, 0xAC)));
        suite.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        suite.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        row.AppendChild(suite);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetValue(FrameworkElement.WidthProperty, 20.0);
        icon.SetValue(FrameworkElement.HeightProperty, 20.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetBinding(Image.SourceProperty, new Binding("Icon") { RelativeSource = RelativeSource.TemplatedParent });
        row.AppendChild(icon);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding("Title") { RelativeSource = RelativeSource.TemplatedParent });
        title.SetValue(TextBlock.FontSizeProperty, 14.0);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(title);
        dock.AppendChild(strip);

        var adorner = new FrameworkElementFactory(typeof(AdornerDecorator));
        adorner.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        dock.AppendChild(adorner);

        var t = new ControlTemplate(typeof(Window)) { VisualTree = ground };
        t.Seal();
        return t;
    }
}

/// <summary>
/// The message window of the suite, called exactly as the MessageBox of Windows (every program's "MessageBox" is this
/// one): English buttons whatever the language of Windows, long texts wrap and scroll, the text can be selected and
/// Ctrl+C copies it, the default button is the amber one. Called from another thread, it is shown by the window thread.
/// </summary>
internal static class SuiteMessageBox
{
    internal sealed class Dialog : Window { }

    public static MessageBoxResult Show(string text) => Show(null, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => Show(null, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(null, text, caption, button, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult def) => Show(null, text, caption, button, icon, def);
    public static MessageBoxResult Show(Window? owner, string text) => Show(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) => Show(owner, text, caption, button, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) => Show(owner, text, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult def)
    {
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(owner, text, caption, button, icon, def));
        if (owner == null && app != null)
            owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? (app.MainWindow is { IsLoaded: true } m ? m : null);
        if (owner != null && !owner.IsLoaded) owner = null;

        var results = button switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK },
        };
        // Esc / closing with the X: Cancel if there is one, else No, else OK (as the MessageBox of Windows)
        var escape = results.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : results.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        if (!results.Contains(def)) def = results[0];

        var result = escape;
        var w = new Dialog
        {
            Title = caption ?? "",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            MinWidth = 340,
            MaxWidth = 900,
            Background = Brushes.White,
        };
        if (owner?.Icon != null) w.Icon = owner.Icon;

        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 18), LastChildFill = true };
        var symbol = icon switch
        {
            MessageBoxImage.Error => ("✖", SuiteTheme.Red, Colors.White),
            MessageBoxImage.Warning => ("!", SuiteTheme.Amber, SuiteTheme.Ink),
            MessageBoxImage.Question => ("?", SuiteTheme.Graphite, Colors.White),
            MessageBoxImage.Information => ("i", SuiteTheme.Graphite, Colors.White),
            _ => ("", Colors.Transparent, Colors.White),
        };
        if (symbol.Item1.Length > 0)
        {
            var badge = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = SuiteTheme.B(symbol.Item2),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0),
                Child = new TextBlock { Text = symbol.Item1, Foreground = SuiteTheme.B(symbol.Item3), FontSize = 18, FontWeight = FontWeights.Bold,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
        }
        var msg = new TextBox
        {
            Text = text ?? "", TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = SuiteTheme.B(SuiteTheme.Text), FontSize = 13, MaxWidth = 780,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 220),
            VerticalAlignment = VerticalAlignment.Center, IsTabStop = false,
        };
        body.Children.Add(msg);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 11, 14, 12) };
        Button? first = null;
        foreach (var r in results)
        {
            var b = new Button
            {
                Content = r switch { MessageBoxResult.Yes => "_Yes", MessageBoxResult.No => "_No", MessageBoxResult.Cancel => "Cancel", _ => "OK" },
                MinWidth = 88, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
                IsDefault = r == def, IsCancel = r == escape,
                // the answer Enter gives is the main action (amber in the suite's look)
                FontWeight = r == def && results.Length > 1 ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var rr = r;
            b.Click += (_, _) => { result = rr; w.Close(); };
            if (r == def) first = b;
            bar.Children.Add(b);
        }
        var footer = new Border { Background = SuiteTheme.B(SuiteTheme.Ground), BorderBrush = SuiteTheme.B(Color.FromRgb(0xE1, 0xE4, 0xE8)), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        w.Content = root;

        // Ctrl+C copies the text, as the MessageBox of Windows
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try { Clipboard.SetText($"{caption}\r\n\r\n{text}"); } catch { }
                e.Handled = true;
            }
        };
        w.Loaded += (_, _) => first?.Focus();
        w.ShowDialog();
        return result;
    }
}
