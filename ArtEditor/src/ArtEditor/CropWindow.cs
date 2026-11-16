using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ArtEditor;

/// <summary>
/// Scelta della parte dell'immagine da usare: un riquadro con le proporzioni della voce di BMS, che si sposta trascinandolo e si
/// allarga o restringe con la rotella o il cursore. A destra l'anteprima alla misura vera.
/// </summary>
public sealed class CropWindow : Window
{
    private readonly Pic _src;
    private readonly double _aspect;
    private readonly int _outW, _outH;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Rectangle _frame = new() { Stroke = Brushes.Yellow, StrokeThickness = 2, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll };
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Slider _size = new() { Minimum = 0.05, Maximum = 1, Value = 1, Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly TextBlock _info = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private double _scale = 1, _ox, _oy;      // immagine mostrata: scala e posizione nel canvas
    private double _cx, _cy;                  // centro del riquadro (pixel dell'immagine)
    private Point? _drag;

    public Int32Rect Result { get; private set; }

    public CropWindow(Pic src, int outW, int outH, string title, IEnumerable<string>? rules = null)
    {
        _src = src; _outW = outW; _outH = outH; _aspect = (double)outW / outH;
        Title = "Choose the part of the image · " + title;
        Width = 1150; Height = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _cx = src.W / 2.0; _cy = src.H / 2.0;

        var img = new Image { Source = ImageOps.ToBitmap(src), Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(img); _canvas.Children.Add(_shade); _canvas.Children.Add(_frame);
        _canvas.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B));
        _canvas.SizeChanged += (_, _) => Layout(img);
        _frame.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(_canvas); _frame.CaptureMouse(); };
        _frame.MouseMove += (_, e) =>
        {
            if (_drag is not { } d) return;
            var p = e.GetPosition(_canvas);
            _cx += (p.X - d.X) / _scale; _cy += (p.Y - d.Y) / _scale; _drag = p;
            Update();
        };
        _frame.MouseLeftButtonUp += (_, _) => { _drag = null; _frame.ReleaseMouseCapture(); UpdatePreview(); };
        _canvas.MouseWheel += (_, e) => _size.Value = Math.Clamp(_size.Value * (e.Delta > 0 ? 1.08 : 1 / 1.08), _size.Minimum, _size.Maximum);
        _size.ValueChanged += (_, _) => { Update(); UpdatePreview(); };

        var ok = new Button { Content = "Use this part", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var center = new Button { Content = "Center", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => { Result = Rect(); DialogResult = true; };
        center.Click += (_, _) => { _cx = _src.W / 2.0; _cy = _src.H / 2.0; _size.Value = 1; Update(); UpdatePreview(); };

        var bar = new DockPanel { Margin = new Thickness(8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(center); right.Children.Add(ok); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        left.Children.Add(_size); left.Children.Add(_info);
        bar.Children.Add(left);

        var side = new StackPanel { Width = 300, Margin = new Thickness(8) };
        side.Children.Add(new TextBlock { Text = $"Result in BMS ({outW}×{outH})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        side.Children.Add(new Border { Child = _preview, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)), MaxHeight = 400, Padding = new Thickness(4) });
        side.Children.Add(new TextBlock
        {
            Text = "Drag the yellow frame to move it; use the mouse wheel or the Size slider to make it bigger or smaller. " +
                   "The frame keeps the proportions BMS needs, so the picture is never stretched.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray,
        });

        if (rules != null)
        {
            side.Children.Add(new TextBlock { Text = "Image rules", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var r in rules) side.Children.Add(new TextBlock { Text = "• " + r, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), FontSize = 11 });
        }
        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        DockPanel.SetDock(sideScroll, Dock.Right); root.Children.Add(sideScroll);
        root.Children.Add(_canvas);
        Content = root;
        Loaded += (_, _) => { Layout(img); UpdatePreview(); };
    }

    private void Layout(Image img)
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 10 || h < 10) return;
        _scale = Math.Min((w - 20) / _src.W, (h - 20) / _src.H);
        _ox = (w - _src.W * _scale) / 2; _oy = (h - _src.H * _scale) / 2;
        img.Width = _src.W * _scale; img.Height = _src.H * _scale;
        Canvas.SetLeft(img, _ox); Canvas.SetTop(img, _oy);
        Update();
    }

    /// <summary>Riquadro attuale in pixel dell'immagine (proporzioni fisse, dentro l'immagine).</summary>
    private Int32Rect Rect()
    {
        var full = ImageOps.CenterCrop(_src.W, _src.H, _aspect);
        double rw = full.Width * _size.Value, rh = full.Height * _size.Value;
        _cx = Math.Clamp(_cx, rw / 2, _src.W - rw / 2); _cy = Math.Clamp(_cy, rh / 2, _src.H - rh / 2);
        return new Int32Rect((int)Math.Round(_cx - rw / 2), (int)Math.Round(_cy - rh / 2), Math.Max(1, (int)Math.Round(rw)), Math.Max(1, (int)Math.Round(rh)));
    }

    private void Update()
    {
        var r = Rect();
        double x = _ox + r.X * _scale, y = _oy + r.Y * _scale, w = r.Width * _scale, h = r.Height * _scale;
        _frame.Width = w; _frame.Height = h; Canvas.SetLeft(_frame, x); Canvas.SetTop(_frame, y);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new System.Windows.Rect(_ox, _oy, _src.W * _scale, _src.H * _scale)), new RectangleGeometry(new System.Windows.Rect(x, y, w, h)));
        _info.Text = $"{r.Width}×{r.Height} px of {_src.W}×{_src.H}" + (r.Width < _outW || r.Height < _outH ? "  ·  smaller than the BMS size: it will look blurred" : "");
    }

    private void UpdatePreview()
    {
        var p = ImageOps.Resize(ImageOps.Crop(_src, Rect()), _outW, _outH);
        _preview.Source = ImageOps.ToBitmap(p);
        _preview.Width = Math.Min(290, _outW * Math.Max(1, 120.0 / Math.Max(_outW, _outH)));
        RenderOptions.SetBitmapScalingMode(_preview, _outW < 120 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }
}
