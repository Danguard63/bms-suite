using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Campaign;

namespace CampaignEditor;

/// <summary>
/// The "Squad patch" field of the squadron windows: the theater's patches (Art\Patches\ImageIds.id and Patches.rsc) with their
/// picture, name and number, so that one can see which patch a squadron wears. Without a patch list (no Art found) it is the old
/// number box.
/// </summary>
internal static class PatchPicker
{
    /// <summary>Art folders of the open workspace: the theater's first, then BMS's (set by the main window).</summary>
    public static IReadOnlyList<string> ArtDirs { get; set; } = Array.Empty<string>();

    private static string? _key;
    private static SortedDictionary<int, SquadronPatches.Patch> _patches = new();
    private static readonly Dictionary<int, BitmapSource?> _images = new();

    private static SortedDictionary<int, SquadronPatches.Patch> Patches()
    {
        string key = string.Join("|", ArtDirs);
        if (key == _key) return _patches;
        _key = key; _images.Clear();
        try { _patches = SquadronPatches.Load(ArtDirs); } catch (Exception) { _patches = new(); }
        return _patches;
    }

    private static BitmapSource? Image(SquadronPatches.Patch p)
    {
        if (_images.TryGetValue(p.Number, out var b)) return b;
        if (p.Bgra != null && p.Width > 0 && p.Height > 0)
        {
            b = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Bgra, p.Width * 4);
            b.Freeze();
        }
        return _images[p.Number] = b;
    }

    /// <summary>The patch a squadron wears, read only (picture, number, name), for the properties panel.</summary>
    public static UIElement View(ushort n)
    {
        var p = Patches().GetValueOrDefault(n);
        var sp = new StackPanel { Orientation = Orientation.Horizontal,
            ToolTip = "Change it in the squadron window (Units → Units list → Open…). Number = id in Art\\Patches\\ImageIds.id minus 70001." };
        if (p != null && Image(p) is { } img) sp.Children.Add(new Image { Source = img, Width = 48, Height = 48, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            Text = p != null ? $"{n}  {p.Name}" : $"{n}  (not in this theater's ImageIds.id)" });
        return sp;
    }

    /// <summary>The field and how to read its value (null = not a valid number).</summary>
    public static (UIElement Field, Func<ushort?> Value) Make(ushort current)
    {
        var all = Patches();
        if (all.Count == 0)
        {
            var tb = new TextBox { Text = current.ToString(CultureInfo.InvariantCulture),
                ToolTip = "Squadron patch number: the patch's id in Art\\Patches\\ImageIds.id minus 70001 (no patch list found for this theater)" };
            return (tb, () => ushort.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort v) ? v : null);
        }
        var cb = new ComboBox { MaxDropDownHeight = 520, IsTextSearchEnabled = true,
            ToolTip = "The patch this squadron wears in BMS (squadron lists, logbook). Number = id in Art\\Patches\\ImageIds.id minus 70001; " +
                      "add new patches with the Art Editor (Squadron patches). Type the first letters of a name to jump to it." };
        ComboBoxItem Item(int n, string name, BitmapSource? img)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Border { Width = 34, Height = 34, Margin = new Thickness(0, 1, 6, 1),
                Child = img != null ? new Image { Source = img, Stretch = Stretch.Uniform } : null });
            sp.Children.Add(new TextBlock { Text = $"{n}  {name}", VerticalAlignment = VerticalAlignment.Center });
            var it = new ComboBoxItem { Content = sp, Tag = (ushort)n };
            TextSearch.SetText(it, name);
            return it;
        }
        foreach (var p in all.Values) cb.Items.Add(Item(p.Number, p.Name, Image(p)));
        if (!all.ContainsKey(current))
            cb.Items.Insert(0, Item(current, "(not in this theater's ImageIds.id)", null));
        cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().First(i => (ushort)i.Tag == current);
        return (cb, () => (cb.SelectedItem as ComboBoxItem)?.Tag as ushort?);
    }
}
