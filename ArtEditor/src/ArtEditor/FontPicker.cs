using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtEditor;

/// <summary>Scelta di un font dell'interfaccia (Fonts\FontRc.irc) con l'anteprima del testo dell'elemento in ogni font, come lo disegna BMS.</summary>
public sealed class FontPicker : Window
{
    private readonly ListBox _list = new() { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)) };
    public string? Result { get; private set; }

    public FontPicker(UiArt ui, string sample, string? current, (byte R, byte G, byte B) color)
    {
        Title = "Change font";
        Width = 760; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (string.IsNullOrWhiteSpace(sample)) sample = "Sample text 0123";
        foreach (var name in ui.FontFiles().Keys.OrderBy(k => k))
        {
            var f = ui.Font(name);
            var sp = new StackPanel { Margin = new Thickness(4) };
            sp.Children.Add(new TextBlock { Text = name + (name.Equals(current, StringComparison.OrdinalIgnoreCase) ? "   (current)" : "") + (f != null ? $"   ·  {f.Height} px" : "   ·  file missing"),
                Foreground = Brushes.LightGray, FontSize = 11 });
            if (f != null)
            {
                int w = Math.Clamp(f.Width(sample) + 4, 8, 1400), h = f.Height + 2;
                var c = new byte[w * h * 4];
                f.Draw(c, w, h, sample, 2, 1, color.R + color.G + color.B < 120 ? ((byte)230, (byte)230, (byte)230) : color);
                var img = new Image { Source = ImageOps.ToBitmap(new Pic(w, h, c)), Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                sp.Children.Add(img);
            }
            var item = new ListBoxItem { Content = sp, Tag = name, IsEnabled = f != null };
            _list.Items.Add(item);
            if (name.Equals(current, StringComparison.OrdinalIgnoreCase)) _list.SelectedItem = item;
        }
        _list.MouseDoubleClick += (_, _) => Ok();
        var ok = new Button { Content = "Use this font", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => Ok();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.DimGray,
            Text = "These are the interface fonts of BMS (pixel fonts at a fixed height). A taller font may not fit the place of the text: check the screen after the change. " +
                   "New fonts (converted from TrueType) can be added in the Fonts section.",
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(_list);
        Content = root;
    }

    private void Ok()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not string n) return;
        Result = n;
        DialogResult = true;
    }
}
