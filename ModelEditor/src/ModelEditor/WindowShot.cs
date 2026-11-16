using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmsSuiteUi;

/// <summary>
/// F12 in any window of the program: saves the window (without its title bar) as a PNG at twice the screen resolution,
/// in the Screenshots folder of the program. Used for the user guide; handy to report a problem.
/// </summary>
internal static class WindowShot
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
        {
            if (e.Key != Key.F12 || s is not Window w) return;
            e.Handled = true;
            try { if (Save(w) != null) System.Media.SystemSounds.Asterisk.Play(); }
            catch (Exception) { System.Media.SystemSounds.Hand.Play(); }
        }));

    public static string? Save(Window w)
    {
        if (w.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return null;
        const double scale = 2;
        double width = root.ActualWidth, height = root.ActualHeight;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(w.Background ?? SystemColors.ControlBrush, null, new Rect(0, 0, width, height));
        bmp.Render(bg);
        bmp.Render(root);
        string dir = Path.Combine(global::TerrainTool.Core.SuiteLayout.ProgramFolder() ?? AppContext.BaseDirectory, "Screenshots");
        Directory.CreateDirectory(dir);
        string name = new string((w.Title ?? "window").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (name.Length > 60) name = name[..60];
        string file = Path.Combine(dir, $"{name} {DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }
}
