using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ObjectiveStudio;

/// <summary>
/// Sostituisce System.Windows.MessageBox in tutto il programma (stesso nome, stesso namespace del programma: vince sul using
/// System.Windows). Quella di Windows scrive i pulsanti nella lingua del sistema ("Sì/No" su un Windows italiano); questa li
/// scrive sempre in inglese (Yes / No / OK / Cancel), come il resto dell'interfaccia. Stesse firme e stessi risultati.
/// </summary>
public static class MessageBox
{
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
        // Esc / chiusura con la X: Cancel se c'e', altrimenti No, altrimenti OK (come la MessageBox di Windows)
        var escape = results.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : results.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        if (!results.Contains(def)) def = results[0];

        var result = escape;
        var w = new Window
        {
            Title = caption ?? "",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            MinWidth = 320,
            MaxWidth = 900,
            Background = SystemColors.WindowBrush,
        };

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14), LastChildFill = true };
        var symbol = icon switch
        {
            MessageBoxImage.Error => ("✖", Color.FromRgb(0xC4, 0x2B, 0x1C)),
            MessageBoxImage.Warning => ("!", Color.FromRgb(0xC7, 0x8A, 0x00)),
            MessageBoxImage.Question => ("?", Color.FromRgb(0x1F, 0x6F, 0xC5)),
            MessageBoxImage.Information => ("i", Color.FromRgb(0x1F, 0x6F, 0xC5)),
            _ => ("", Colors.Transparent),
        };
        if (symbol.Item1.Length > 0)
        {
            var badge = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(symbol.Item2),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
                Child = new TextBlock { Text = symbol.Item1, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(badge, Dock.Left);
            body.Children.Add(badge);
        }
        var msg = new TextBox
        {
            Text = text ?? "", TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = SystemColors.WindowTextBrush, FontSize = 12.5, MaxWidth = 780,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 220),
            VerticalAlignment = VerticalAlignment.Center, IsTabStop = false,
        };
        body.Children.Add(msg);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 12) };
        Button? first = null;
        foreach (var r in results)
        {
            var b = new Button
            {
                Content = r switch { MessageBoxResult.Yes => "_Yes", MessageBoxResult.No => "_No", MessageBoxResult.Cancel => "Cancel", _ => "OK" },
                MinWidth = 84, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0),
                IsDefault = r == def, IsCancel = r == escape,
            };
            var rr = r;
            b.Click += (_, _) => { result = rr; w.Close(); };
            if (r == def) first = b;
            bar.Children.Add(b);
        }
        var footer = new Border { Background = SystemColors.ControlBrush, Child = bar };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        w.Content = root;

        // Ctrl+C copia il testo, come la MessageBox di Windows
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
