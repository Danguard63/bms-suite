using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Editor;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace TerrainTool.App;

/// <summary>
/// Editor delle tessere Photoreal 4K: mappa 2D dall'alto (zoom e trascinamento, dettaglio che sale con lo zoom), griglia e
/// numeri delle tessere, obiettivi con nomi, collegamenti e sagome delle basi dall'uscita del progetto (Output\Install);
/// spostamento per BMS X/Y, lat/lon o numero di tessera. Modifica: la tessera va in PNG 4096² (con un livello PNG trasparente
/// degli oggetti) e si apre in GIMP/Photoshop; al salvataggio la si reimporta (DXT1 con mipmap, originale salvato).
/// </summary>
public sealed class PhotoTileEditorWindow : Window
{
    private readonly TheaterProject _p;
    private readonly PhotoTileEdits _t;
    private readonly TheaterGrid _grid;
    private TileOverlay? _ov;
    private readonly TileMapView _map;
    private readonly TextBlock _cursor = new() { FontFamily = new FontFamily("Consolas"), Margin = new Thickness(4, 2, 4, 2) };
    private readonly TextBlock _sel = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _log = new() { IsReadOnly = true, Height = 150, FontFamily = new FontFamily("Consolas"), FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _x = new() { Width = 100 }, _y = new() { Width = 100 }, _ll = new() { Width = 180 }, _tileNo = new() { Width = 70 };
    private readonly Button _edit = new() { Content = "Edit in external program", FontWeight = FontWeights.SemiBold };
    private readonly Button _import = new() { Content = "Import edited PNG" };
    private readonly Button _restore = new() { Content = "Restore original" };
    private readonly TextBlock _editor = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11 };
    private readonly ComboBox _editorCombo = new() { Margin = new Thickness(0, 2, 0, 2) };
    private FileSystemWatcher? _watch;
    private int _selected;
    private bool _busy;

    private static string EditorSetting => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BmsTerrainTool", "image_editor.txt");

    public PhotoTileEditorWindow(TheaterProject p)
    {
        _p = p;
        _t = new PhotoTileEdits(p);
        _grid = p.Theater.CreateGrid();
        Title = $"Photoreal tile editor — {p.Theater.Name} ({_t.TileDir})";
        Width = 1500; Height = 950; MinWidth = 1000; MinHeight = 650;
        _map = new TileMapView(_t);
        _map.CursorMoved += (n, e) =>
        {
            var (lat, lon) = _grid.ToLatLon(e / TerrainSampler.FtPerM, n / TerrainSampler.FtPerM);
            int idx = _t.IndexAt(n, e);
            _cursor.Text = $"BMS X {n:0}  Y {e:0} ft   {Math.Abs(lat):0.00000}°{(lat >= 0 ? "N" : "S")} {Math.Abs(lon):0.00000}°{(lon >= 0 ? "E" : "W")}   tile {(idx > 0 ? idx.ToString(CultureInfo.InvariantCulture) : "-")}   {_map.MetersPerPixel:0.#} m/px on screen";
        };
        _map.TileClicked += Select;

        // ── pannello
        var panel = new StackPanel { Width = 330, Margin = new Thickness(8) };
        void H(string t) => panel.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        H("Go to");
        var r1 = new WrapPanel();
        r1.Children.Add(new TextBlock { Text = "BMS X ", VerticalAlignment = VerticalAlignment.Center }); r1.Children.Add(_x);
        r1.Children.Add(new TextBlock { Text = " Y ", VerticalAlignment = VerticalAlignment.Center }); r1.Children.Add(_y);
        var gxy = Btn("Go", (_, _) =>
        {
            if (Num(_x.Text, out double bx) && Num(_y.Text, out double by)) GoTo(bx, by, true);
            else Log("Write BMS X (north) and Y (east) in feet");
        });
        r1.Children.Add(gxy);
        panel.Children.Add(r1);
        var r2 = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        r2.Children.Add(new TextBlock { Text = "lat, lon ", VerticalAlignment = VerticalAlignment.Center }); r2.Children.Add(_ll);
        r2.Children.Add(Btn("Go", (_, _) =>
        {
            var parts = _ll.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Num(parts[0], out double la) && Num(parts[1], out double lo))
            {
                var (x, y) = _grid.ToTheater(la, lo);
                GoTo(y * TerrainSampler.FtPerM, x * TerrainSampler.FtPerM, true);
            }
            else Log("Write latitude, longitude in degrees, e.g. 29.37, 47.98");
        }));
        panel.Children.Add(r2);
        var r3 = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        r3.Children.Add(new TextBlock { Text = "Tile number ", VerticalAlignment = VerticalAlignment.Center }); r3.Children.Add(_tileNo);
        r3.Children.Add(Btn("Go", (_, _) =>
        {
            if (int.TryParse(_tileNo.Text.Trim(), out int n) && n >= 1 && n <= _t.N * _t.N)
            {
                var b = _t.Bounds(n);
                _map.ShowArea((b.South + b.North) / 2, (b.West + b.East) / 2, _t.TileFt * 1.1);
                Select(n);
            }
            else Log($"Tile numbers go from 1 to {_t.N * _t.N}");
        }));
        r3.Children.Add(Btn("Whole theater", (_, _) => _map.ShowArea(_t.SizeFt / 2, _t.SizeFt / 2, _t.SizeFt)));
        panel.Children.Add(r3);

        H("Show");
        panel.Children.Add(Check("Tile grid and numbers", true, v => _map.ShowGrid = v));
        panel.Children.Add(Check("Objectives", true, v => _map.ShowObjectives = v));
        panel.Children.Add(Check("Names", true, v => _map.ShowNames = v));
        panel.Children.Add(Check("Links between objectives", true, v => _map.ShowLinks = v));
        panel.Children.Add(Check("Base layouts (runways, buildings) when zoomed in", true, v => _map.ShowLayouts = v));

        H("Selected tile");
        panel.Children.Add(_sel);
        foreach (var b in new[] { _edit, _import, _restore }) { b.Margin = new Thickness(0, 2, 0, 2); b.Padding = new Thickness(8, 3, 8, 3); panel.Children.Add(b); }
        _edit.Click += async (_, _) => await EditAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _restore.Click += (_, _) => RestoreTile();
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        tools.Children.Add(Btn("Choose program…", (_, _) => ChooseEditor()));
        tools.Children.Add(Btn("Open edits folder", (_, _) => { Directory.CreateDirectory(_t.EditDir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_t.EditDir}\"") { UseShellExecute = true }); }));
        panel.Children.Add(tools);
        panel.Children.Add(new TextBlock { Text = "Image program", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(_editorCombo);
        _editorCombo.SelectionChanged += (_, _) => SaveEditorChoice();
        panel.Children.Add(_editor);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
            Text = "Mouse: wheel = zoom, left drag = move, click = select a tile.\n" +
                   "Edit: the tile is saved as a 4096×4096 PNG in Output\\TileEdits and opened in your program, with <n>_objects.png next to it " +
                   "(transparent: objectives, names, links, runways) to open as a layer on top. Paint, save the PNG with the same name and size, " +
                   "then 'Import edited PNG': the tile is rebuilt as DXT1 with mipmaps in Output\\NewTerrain\\Photoreal\\4K (the original is kept " +
                   "in Output\\TileEdits\\Originals). Then 'Install Photoreal tiles into BMS…' in tab 5 copies the changed tiles.\n" +
                   "Painting up to the tile edge does not change the neighbour tile: keep the edges as they are to avoid seams.",
        });
        H("Log");
        panel.Children.Add(_log);

        var root = new DockPanel();
        var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(sv, Dock.Left);
        root.Children.Add(sv);
        var bottom = new Border { Child = _cursor, Background = new SolidColorBrush(Color.FromRgb(235, 235, 235)) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_map);
        Content = root;
        UpdateSelection();

        Loaded += async (_, _) =>
        {
            _ = FillEditorsAsync();
            Log($"{_t.N}×{_t.N} tiles of {_t.TileFt / TerrainSampler.FtPerM / 1000:0} km ({_t.TileDir})");
            try
            {
                await _map.LoadOverviewAsync();
                var ov = await Task.Run(() => new TileOverlay(_p));
                _ov = ov; _map.Overlay = ov;
                foreach (var n in ov.Notes) Log(n);
            }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
        };
        Closed += (_, _) => { _watch?.Dispose(); _map.Dispose(); };
    }

    private static bool Num(string s, out double v) => double.TryParse(s.Trim().Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 2, 0, 2) };
        b.Click += click;
        return b;
    }

    private CheckBox Check(string text, bool on, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 1, 0, 1) };
        c.Click += (_, _) => { set(c.IsChecked == true); _map.InvalidateVisual(); };
        return c;
    }

    private void Log(string s) { _log.AppendText($"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); _log.ScrollToEnd(); }

    private void GoTo(double north, double east, bool select)
    {
        if (north < 0 || east < 0 || north > _t.SizeFt || east > _t.SizeFt) { Log($"Outside the theater (0…{_t.SizeFt:0} ft)"); return; }
        _map.ShowArea(north, east, 30000);
        _map.Mark = (north, east);
        if (select) Select(_t.IndexAt(north, east));
    }

    private void Select(int index)
    {
        if (index <= 0) return;
        _selected = index;
        _map.Selected = index;
        _tileNo.Text = index.ToString(CultureInfo.InvariantCulture);
        UpdateSelection();
        WatchPng();
    }

    private void UpdateSelection()
    {
        if (_selected <= 0) { _sel.Text = "Click a tile on the map."; _edit.IsEnabled = _import.IsEnabled = _restore.IsEnabled = false; return; }
        var b = _t.Bounds(_selected);
        bool ex = _t.Exists(_selected), ed = _t.IsEdited(_selected), png = File.Exists(_t.PngPath(_selected));
        var (la0, lo0) = _grid.ToLatLon(b.West / TerrainSampler.FtPerM, b.South / TerrainSampler.FtPerM);
        var (la1, lo1) = _grid.ToLatLon(b.East / TerrainSampler.FtPerM, b.North / TerrainSampler.FtPerM);
        _sel.Text = $"Tile {_selected} ({_selected}.dds){(ex ? "" : " — missing (sea-only tile, not made)")}{(ed ? " — EDITED (original kept)" : "")}\n" +
                    $"BMS X {b.South:0} … {b.North:0}\nBMS Y {b.West:0} … {b.East:0}\n" +
                    $"{la0:0.000}…{la1:0.000}°N  {lo0:0.000}…{lo1:0.000}°E" +
                    (png ? $"\nPNG for editing: {File.GetLastWriteTime(_t.PngPath(_selected)):dd/MM HH:mm}" : "");
        _edit.IsEnabled = ex && !_busy; _import.IsEnabled = png && !_busy; _restore.IsEnabled = ed && !_busy;
    }

    // ───────────────────────── modifica ─────────────────────────

    // programma di disegno: quelli trovati sul PC (registro di Windows e cartelle dei programmi), la scelta resta ricordata
    private const string DefaultChoice = "(Windows default program for .png)";
    private string? EditorExe() => _editorCombo.SelectedItem is ComboBoxItem { Tag: string exe } && File.Exists(exe) ? exe : null;

    private async Task FillEditorsAsync()
    {
        var found = await Task.Run(ImageEditors.Detect);
        string saved = "";
        try { if (File.Exists(EditorSetting)) saved = File.ReadAllText(EditorSetting).Trim(); } catch { }
        if (saved.Length > 0 && saved != DefaultChoice && File.Exists(saved) && !found.Any(f => string.Equals(f.Exe, saved, StringComparison.OrdinalIgnoreCase)))
            found.Insert(0, (Path.GetFileNameWithoutExtension(saved) + " (chosen)", saved));
        _fillingEditors = true;
        _editorCombo.Items.Clear();
        foreach (var (name, exe) in found) _editorCombo.Items.Add(new ComboBoxItem { Content = name, Tag = exe, ToolTip = exe });
        _editorCombo.Items.Add(new ComboBoxItem { Content = DefaultChoice, Tag = null });
        int sel = found.FindIndex(f => string.Equals(f.Exe, saved, StringComparison.OrdinalIgnoreCase));
        _editorCombo.SelectedIndex = saved == DefaultChoice ? found.Count : sel >= 0 ? sel : 0;
        _fillingEditors = false;
        _editor.Text = found.Count > 0
            ? $"Found on this PC: {string.Join(", ", found.Select(f => f.Name))}"
            : "No GIMP, Photoshop, Krita, Paint.NET or Affinity Photo found: the PNG opens with the Windows default program ('Choose program…' to pick one).";
        Log(found.Count > 0 ? $"Image programs found: {string.Join("; ", found.Select(f => f.Exe))}" : "No image program found automatically");
    }
    private bool _fillingEditors;

    private void SaveEditorChoice()
    {
        if (_fillingEditors) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EditorSetting)!);
            File.WriteAllText(EditorSetting, EditorExe() ?? DefaultChoice);
        }
        catch { }
    }

    private void ChooseEditor()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose GIMP, Photoshop or another image program" };
        if (dlg.ShowDialog(this) != true) return;
        var item = new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(dlg.FileName) + " (chosen)", Tag = dlg.FileName, ToolTip = dlg.FileName };
        _editorCombo.Items.Insert(0, item);
        _editorCombo.SelectedItem = item;
    }

    private async Task EditAsync()
    {
        int idx = _selected;
        if (idx <= 0 || _busy || !_t.Exists(idx)) return;
        string png = _t.PngPath(idx);
        if (File.Exists(png) && MessageBox.Show(this, $"{png} already exists (maybe with your changes).\n\nYes = open it as it is\nNo = make it again from the tile (changes in it are lost)",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { Open(png); return; }
        _busy = true; UpdateSelection();
        try
        {
            Log($"Tile {idx}: reading…");
            var bgra = await Task.Run(() => _t.ReadTileBgra(idx));
            Directory.CreateDirectory(_t.EditDir);
            int s = PhotoTiles.TilePx;
            SavePng(BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgr32, null, bgra, s * 4), png);
            Log($"  {png}");
            string ovPng = _t.OverlayPngPath(idx);
            SavePng(_map.RenderOverlayForTile(idx, s), ovPng);
            Log($"  {ovPng} (open it as a layer)");
            Open(png);
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        finally { _busy = false; UpdateSelection(); WatchPng(); }
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, path, true);
    }

    private void Open(string png)
    {
        var exe = EditorExe();
        try
        {
            if (exe != null) { var psi = new ProcessStartInfo(exe) { UseShellExecute = false }; psi.ArgumentList.Add(png); Process.Start(psi); }
            else Process.Start(new ProcessStartInfo(png) { UseShellExecute = true });
            Log($"Opened in {(exe != null ? Path.GetFileName(exe) : "the default program")}. When you have saved the PNG, press 'Import edited PNG'.");
        }
        catch (Exception ex) { Log("Could not open the program: " + ex.Message); }
    }

    private void WatchPng()
    {
        _watch?.Dispose(); _watch = null;
        if (_selected <= 0 || !Directory.Exists(_t.EditDir)) return;
        int idx = _selected;
        _watch = new FileSystemWatcher(_t.EditDir, idx + ".png") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        DateTime last = DateTime.MinValue;
        void Changed(object _, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() =>
        {
            if ((DateTime.Now - last).TotalSeconds < 2) return;
            last = DateTime.Now;
            Log($"Tile {idx}: the PNG was saved — press 'Import edited PNG' to put it in the tile");
            UpdateSelection();
        });
        _watch.Changed += Changed; _watch.Created += Changed; _watch.Renamed += (s, e) => Changed(s, e);
    }

    private async Task ImportAsync()
    {
        int idx = _selected;
        string png = _t.PngPath(idx);
        if (idx <= 0 || _busy || !File.Exists(png)) return;
        if (MessageBox.Show(this, $"Rebuild tile {idx} from\n{png}?\n\nThe tile in Output\\NewTerrain\\Photoreal\\4K is replaced " +
                (_t.IsEdited(idx) ? "(the original is already kept in Output\\TileEdits\\Originals)." : "and the original is kept in Output\\TileEdits\\Originals."),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true; UpdateSelection();
        try
        {
            byte[] rgb;
            int s = PhotoTiles.TilePx;
            using (var fs = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var f = dec.Frames[0];
                if (f.PixelWidth != s || f.PixelHeight != s) throw new InvalidDataException($"the PNG is {f.PixelWidth}×{f.PixelHeight}: it must stay {s}×{s} (do not resize or crop it)");
                var conv = new FormatConvertedBitmap(f, PixelFormats.Rgb24, null, 0);
                rgb = new byte[(long)s * s * 3];
                conv.CopyPixels(rgb, s * 3, 0);
            }
            Log($"Tile {idx}: encoding DXT1 with mipmaps…");
            var sw = Stopwatch.StartNew();
            await Task.Run(() => _t.Import(idx, rgb));
            Log($"Tile {idx}: done in {sw.Elapsed.TotalSeconds:0} s → {_t.TilePath(idx)}");
            _map.TileChanged(idx);
        }
        catch (Exception ex) { Log("Import ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; UpdateSelection(); }
    }

    private void RestoreTile()
    {
        int idx = _selected;
        if (idx <= 0 || !_t.IsEdited(idx)) return;
        if (MessageBox.Show(this, $"Put back the original tile {idx}? Your changes stay only in the PNG ({_t.PngPath(idx)}).", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            if (_t.Restore(idx)) { Log($"Tile {idx}: original restored"); _map.TileChanged(idx); }
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        UpdateSelection();
    }
}

/// <summary>
/// Mappa 2D delle tessere: sfondo d'insieme (mip 6 di ogni tessera) e, con lo zoom, pezzi da 512 px del livello mip adatto,
/// letti in background e tenuti in memoria (i piu' recenti). Disegna griglia, obiettivi, collegamenti e disposizioni.
/// </summary>
public sealed class TileMapView : FrameworkElement, IDisposable
{
    private readonly PhotoTileEdits _t;
    private double _cn, _ce, _scale = 1;           // centro (piedi) e pixel dello schermo per piede
    private Point? _drag; private Point _down; private bool _moved;
    private BitmapSource? _overview;
    private const int OvPx = 64;                    // pixel per tessera nella mappa d'insieme (mip 6)
    private readonly Dictionary<(int Tile, int Mip, int Cx, int Cy), BitmapSource?> _cache = new();
    private readonly LinkedList<(int, int, int, int)> _lru = new();
    private readonly HashSet<(int, int, int, int)> _loading = new();
    private const int MaxCached = 220;
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount / 2));
    private bool _disposed;

    public TileOverlay? Overlay { get => _ovl; set { _ovl = value; InvalidateVisual(); } }
    private TileOverlay? _ovl;
    public bool ShowGrid { get; set; } = true;
    public bool ShowObjectives { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool ShowLinks { get; set; } = true;
    public bool ShowLayouts { get; set; } = true;
    public int Selected { get => _selected; set { _selected = value; InvalidateVisual(); } }
    private int _selected;
    public (double N, double E)? Mark { get => _mark; set { _mark = value; InvalidateVisual(); } }
    private (double N, double E)? _mark;
    public double MetersPerPixel => 1 / _scale / TerrainSampler.FtPerM;

    public event Action<double, double>? CursorMoved;
    public event Action<int>? TileClicked;

    public TileMapView(PhotoTileEdits t)
    {
        _t = t;
        _cn = _ce = t.SizeFt / 2;
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0) ShowArea(_t.SizeFt / 2, _t.SizeFt / 2, _t.SizeFt); };
    }

    public void Dispose() { _disposed = true; }

    public void ShowArea(double north, double east, double sideFt)
    {
        _cn = north; _ce = east;
        double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
        _scale = Math.Min(w, h) / sideFt;
        InvalidateVisual();
    }

    private Point S(double n, double e) => new((e - _ce) * _scale + ActualWidth / 2, (_cn - n) * _scale + ActualHeight / 2);
    private (double N, double E) W(Point p) => (_cn - (p.Y - ActualHeight / 2) / _scale, _ce + (p.X - ActualWidth / 2) / _scale);

    public async Task LoadOverviewAsync()
    {
        int n = _t.N, side = n * OvPx;
        var px = await Task.Run(() =>
        {
            var buf = new byte[side * side * 4];
            for (int ty = 0; ty < n; ty++)
                for (int tx = 0; tx < n; tx++)
                {
                    byte[]? c;
                    lock (_t.Tiles) c = _t.Tiles.GetChunk(tx, ty, 6, 0, 0, out _);
                    for (int r = 0; r < OvPx; r++)
                    {
                        int o = ((ty * OvPx + r) * side + tx * OvPx) * 4;
                        if (c != null) Buffer.BlockCopy(c, r * OvPx * 4, buf, o, OvPx * 4);
                        else for (int i = 0; i < OvPx; i++) { buf[o + i * 4] = 110; buf[o + i * 4 + 1] = 60; buf[o + i * 4 + 2] = 25; }
                        for (int i = 0; i < OvPx; i++) buf[o + i * 4 + 3] = 255;
                    }
                }
            return buf;
        });
        var bmp = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4);
        bmp.Freeze();
        _overview = bmp;
        InvalidateVisual();
    }

    /// <summary>Dopo l'importazione o il ripristino: si ricaricano i pezzi della tessera e il suo quadrato d'insieme.</summary>
    public async void TileChanged(int idx)
    {
        foreach (var k in _cache.Keys.Where(k => k.Tile == idx).ToList()) { _cache.Remove(k); _lru.Remove(k); }
        await LoadOverviewAsync();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 45)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var tl = S(_t.SizeFt, 0); var br = S(0, _t.SizeFt);
        if (_overview != null) dc.DrawImage(_overview, new Rect(tl, br));
        double tilePx = _t.TileFt * _scale;
        var (nTop, eLeft) = W(new Point(0, 0)); var (nBot, eRight) = W(new Point(ActualWidth, ActualHeight));
        int tx0 = Math.Max(0, (int)(eLeft / _t.TileFt)), tx1 = Math.Min(_t.N - 1, (int)(eRight / _t.TileFt));
        int ty0 = Math.Max(0, (int)((_t.SizeFt - nTop) / _t.TileFt)), ty1 = Math.Min(_t.N - 1, (int)((_t.SizeFt - nBot) / _t.TileFt));

        // dettaglio: livello mip con circa un pixel della tessera per pixel dello schermo
        if (tilePx > OvPx * 1.4)
        {
            int mip = Math.Clamp((int)Math.Floor(Math.Log2(PhotoTiles.TilePx / tilePx)), 0, 5);
            int w = PhotoTiles.TilePx >> mip, side = Math.Min(PhotoTiles.Chunk, w), per = w / side;
            double chunkFt = _t.TileFt / per;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    int idx = _t.Index(tx, ty);
                    for (int cy = 0; cy < per; cy++)
                        for (int cx = 0; cx < per; cx++)
                        {
                            double west = tx * _t.TileFt + cx * chunkFt, north = _t.SizeFt - ty * _t.TileFt - cy * chunkFt;
                            var a = S(north, west); var b = S(north - chunkFt, west + chunkFt);
                            if (b.X < 0 || b.Y < 0 || a.X > ActualWidth || a.Y > ActualHeight) continue;
                            var key = (idx, mip, cx, cy);
                            if (_cache.TryGetValue(key, out var img)) { if (img != null) dc.DrawImage(img, new Rect(a, b)); _lru.Remove(key); _lru.AddFirst(key); }
                            else Request(key, tx, ty);
                        }
                }
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1); pen.Freeze();
        if (ShowGrid && tilePx > 12)
        {
            for (int tx = tx0; tx <= tx1 + 1; tx++) { var a = S(_t.SizeFt, tx * _t.TileFt); dc.DrawLine(pen, new Point(a.X, Math.Max(0, tl.Y)), new Point(a.X, Math.Min(ActualHeight, br.Y))); }
            for (int ty = ty0; ty <= ty1 + 1; ty++) { var a = S(_t.SizeFt - ty * _t.TileFt, 0); dc.DrawLine(pen, new Point(Math.Max(0, tl.X), a.Y), new Point(Math.Min(ActualWidth, br.X), a.Y)); }
            if (tilePx > 40)
                for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        int idx = _t.Index(tx, ty);
                        var a = S(_t.SizeFt - ty * _t.TileFt, tx * _t.TileFt);
                        Text(dc, idx.ToString(CultureInfo.InvariantCulture) + (_t.IsEdited(idx) ? " ✎" : ""), new Point(a.X + 4, a.Y + 2), 12, Brushes.White, true);
                    }
        }
        if (_selected > 0)
        {
            var bd = _t.Bounds(_selected);
            var selPen = new Pen(Brushes.Yellow, 3); selPen.Freeze();
            dc.DrawRectangle(null, selPen, new Rect(S(bd.North, bd.West), S(bd.South, bd.East)));
        }
        DrawOverlay(dc, S, _scale, ActualWidth, ActualHeight, labels: true);
        if (_mark is { } m)
        {
            var p = S(m.N, m.E);
            var mp = new Pen(Brushes.Red, 2); mp.Freeze();
            dc.DrawLine(mp, new Point(p.X - 10, p.Y), new Point(p.X + 10, p.Y)); dc.DrawLine(mp, new Point(p.X, p.Y - 10), new Point(p.X, p.Y + 10));
        }
    }

    private static readonly Typeface Face = new("Segoe UI");
    private void Text(DrawingContext dc, string s, Point at, double size, Brush fg, bool halo)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, fg, 1.0);
        if (halo)
        {
            var g = ft.BuildGeometry(at);
            var hp = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3); hp.Freeze();
            dc.DrawGeometry(null, hp, g);
            dc.DrawGeometry(fg, null, g);
        }
        else dc.DrawText(ft, at);
    }

    private static Brush TypeBrush(int t) => t switch
    {
        1 or 2 => Brushes.DeepSkyBlue, 3 or 13 => Brushes.OliveDrab, 6 => Brushes.Orange, 8 or 28 or 29 => Brushes.Khaki,
        19 => Brushes.Aqua, 21 or 31 => Brushes.Red, 15 => Brushes.LightGray, 10 or 11 or 20 or 25 or 7 or 17 => Brushes.MediumPurple, _ => Brushes.White,
    };

    /// <summary>Obiettivi, collegamenti e disposizioni; usato sia per lo schermo sia per il livello PNG della tessera.</summary>
    private void DrawOverlay(DrawingContext dc, Func<double, double, Point> S, double scale, double w, double h, bool labels)
    {
        var ov = _ovl;
        if (ov == null) return;
        bool In(Point p, double m = 50) => p.X > -m && p.Y > -m && p.X < w + m && p.Y < h + m;
        var pos = ov.Objectives.ToDictionary(o => o.CampId);
        double mPerPx = 1 / scale / TerrainSampler.FtPerM;
        if (ShowLinks && mPerPx < 600)
        {
            var lp = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 230, 60)), mPerPx < 20 ? 2 : 1); lp.Freeze();
            foreach (var (a, b) in ov.Links)
                if (pos.TryGetValue(a, out var oa) && pos.TryGetValue(b, out var ob))
                {
                    var pa = S(oa.North, oa.East); var pb = S(ob.North, ob.East);
                    if (In(pa, 2000) || In(pb, 2000)) dc.DrawLine(lp, pa, pb);
                }
        }
        if (ShowLayouts && mPerPx < 25)
        {
            var bp = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 80, 80)), 1); bp.Freeze();
            var pp = new Pen(new SolidColorBrush(Color.FromArgb(220, 80, 255, 120)), mPerPx < 3 ? 2 : 1); pp.Freeze();
            foreach (var o in ov.Objectives)
            {
                if (!In(S(o.North, o.East), 4000)) continue;
                var (boxes, paths, lbl) = ov.Layout(o.CampId);
                foreach (var bx in boxes)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(S(bx[0].N, bx[0].E), false, true);
                        for (int i = 1; i < 4; i++) c.LineTo(S(bx[i].N, bx[i].E), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, bp, g);
                }
                foreach (var pth in paths)
                    for (int i = 1; i < pth.Count; i++) dc.DrawLine(pp, S(pth[i - 1].N, pth[i - 1].E), S(pth[i].N, pth[i].E));
                if (labels && ShowNames && mPerPx < 4)
                    foreach (var (ln, le, name) in lbl) Text(dc, name, S(ln, le), 10, Brushes.Pink, true);
            }
        }
        if (ShowObjectives)
        {
            double r = mPerPx < 50 ? 6 : mPerPx < 300 ? 4 : 2.5;
            foreach (var o in ov.Objectives)
            {
                var p = S(o.North, o.East);
                if (!In(p)) continue;
                if (mPerPx > 300 && o.Type is not (1 or 2 or 8 or 19)) continue;     // vista larga: solo basi aeree, citta', porti
                var br = TypeBrush(o.Type);
                dc.DrawEllipse(br, new Pen(Brushes.Black, 1), p, r, r);
                bool big = o.Type is 1 or 2;
                if (ShowNames && (mPerPx < 120 || (big && mPerPx < 600)))
                    Text(dc, o.Name, new Point(p.X + r + 2, p.Y - 7), big ? 12 : 11, br, true);
            }
        }
    }

    /// <summary>Livello trasparente della tessera (side×side) con obiettivi, nomi, collegamenti e disposizioni, alla scala della tessera.</summary>
    public BitmapSource RenderOverlayForTile(int idx, int side)
    {
        var bd = _t.Bounds(idx);
        double sc = side / _t.TileFt;
        Point P(double n, double e) => new((e - bd.West) * sc, (bd.North - n) * sc);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            bool g = ShowGrid; ShowGrid = false;
            DrawOverlay(dc, P, sc, side, side, labels: true);
            ShowGrid = g;
        }
        var rtb = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private async void Request((int Tile, int Mip, int Cx, int Cy) key, int tx, int ty)
    {
        if (_loading.Contains(key) || _loading.Count > 48) return;
        _loading.Add(key);
        try
        {
            await _gate.WaitAsync();
            BitmapSource? img = null;
            try
            {
                if (_disposed) return;
                img = await Task.Run(() =>
                {
                    byte[]? px; int s;
                    lock (_t.Tiles) px = _t.Tiles.GetChunk(tx, ty, key.Mip, key.Cx, key.Cy, out s);
                    if (px == null) return null;
                    var b = BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgra32, null, px, s * 4);
                    b.Freeze();
                    return b;
                });
            }
            finally { _gate.Release(); }
            _cache[key] = img; _lru.AddFirst(key);
            while (_lru.Count > MaxCached) { _cache.Remove(_lru.Last!.Value); _lru.RemoveLast(); }
            InvalidateVisual();
        }
        catch { }
        finally { _loading.Remove(key); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var (n, ea) = W(p);
        double f = e.Delta > 0 ? 1.25 : 0.8;
        double maxScale = 4.0 / (_t.TileFt / PhotoTiles.TilePx);      // fino a 4 pixel dello schermo per pixel della tessera
        _scale = Math.Clamp(_scale * f, Math.Min(ActualWidth, ActualHeight) / _t.SizeFt / 2, maxScale);
        _cn = n + (p.Y - ActualHeight / 2) / _scale;
        _ce = ea - (p.X - ActualWidth / 2) / _scale;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = _down = e.GetPosition(this); _moved = false;
        CaptureMouse(); Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (n, ea) = W(p);
        CursorMoved?.Invoke(n, ea);
        if (_drag is Point d && e.LeftButton == MouseButtonState.Pressed)
        {
            if ((p - _down).Length > 4) _moved = true;
            _ce -= (p.X - d.X) / _scale; _cn += (p.Y - d.Y) / _scale;
            _drag = p;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _drag = null;
        if (!_moved)
        {
            var (n, ea) = W(e.GetPosition(this));
            int idx = _t.IndexAt(n, ea);
            if (idx > 0) TileClicked?.Invoke(idx);
        }
    }
}

/// <summary>
/// Programmi di disegno installati: Photoshop, GIMP, Krita, Paint.NET, Affinity Photo. Cerca nel registro (App Paths e
/// "Programmi e funzionalita'", 64 e 32 bit, PC e utente) e nelle cartelle dei programmi. Solo lettura.
/// </summary>
public static class ImageEditors
{
    private static readonly (string Name, Regex Display, Regex Exe)[] Known =
    {
        ("Photoshop", new Regex(@"Adobe Photoshop", RegexOptions.IgnoreCase), new Regex(@"^photoshop\.exe$", RegexOptions.IgnoreCase)),
        ("GIMP", new Regex(@"^GIMP\b", RegexOptions.IgnoreCase), new Regex(@"^gimp(-\d+(\.\d+)*)?\.exe$", RegexOptions.IgnoreCase)),
        ("Krita", new Regex(@"^Krita\b", RegexOptions.IgnoreCase), new Regex(@"^krita\.exe$", RegexOptions.IgnoreCase)),
        ("Paint.NET", new Regex(@"paint\.net", RegexOptions.IgnoreCase), new Regex(@"^paintdotnet\.exe$", RegexOptions.IgnoreCase)),
        ("Affinity Photo", new Regex(@"Affinity Photo", RegexOptions.IgnoreCase), new Regex(@"^photo\.exe$", RegexOptions.IgnoreCase)),
    };

    public static List<(string Name, string Exe)> Detect()
    {
        var res = new List<(string Name, string Exe, int Rank)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? exe, string? label)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            exe = exe.Trim().Trim('"');
            int comma = exe.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (comma > 0) exe = exe[..(comma + 4)];
            if (!File.Exists(exe) || !seen.Add(Path.GetFullPath(exe))) return;
            string fn = Path.GetFileName(exe);
            if (fn.Contains("console", StringComparison.OrdinalIgnoreCase)) return;
            for (int k = 0; k < Known.Length; k++)
                if (Known[k].Exe.IsMatch(fn))
                {
                    string name = label ?? Known[k].Name;
                    if (Known[k].Name == "Affinity Photo" && !exe.Contains("Affinity", StringComparison.OrdinalIgnoreCase)) return;
                    res.Add((name, exe, k));
                    return;
                }
        }
        string? ExeIn(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            foreach (var d in new[] { dir, Path.Combine(dir, "bin") })
            {
                if (!Directory.Exists(d)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d, "*.exe"))
                        if (Known.Any(k => k.Exe.IsMatch(Path.GetFileName(f))) && !f.Contains("console", StringComparison.OrdinalIgnoreCase)) return f;
                }
                catch { }
            }
            return null;
        }

        // 1) registro: App Paths e programmi installati
        foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using (var ap = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                        if (ap != null)
                            foreach (var n in ap.GetSubKeyNames())
                                if (Known.Any(k => k.Exe.IsMatch(n)))
                                    using (var k = ap.OpenSubKey(n)) Add(k?.GetValue(null) as string, null);
                    using var un = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (un == null) continue;
                    foreach (var n in un.GetSubKeyNames())
                    {
                        using var k = un.OpenSubKey(n);
                        if (k?.GetValue("DisplayName") is not string dn) continue;
                        if (!Known.Any(x => x.Display.IsMatch(dn))) continue;
                        string label = dn.Trim();
                        int before = res.Count;
                        if (k.GetValue("DisplayIcon") is string icon && icon.Contains(".exe", StringComparison.OrdinalIgnoreCase)) Add(icon, label);
                        if (res.Count == before) Add(ExeIn(k.GetValue("InstallLocation") as string), label);
                    }
                }
                catch { }
            }

        // 2) cartelle dei programmi
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        foreach (var r in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(r))
                {
                    string dn = Path.GetFileName(d);
                    if (dn.Equals("Adobe", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var ps in Directory.EnumerateDirectories(d, "Adobe Photoshop*")) Add(ExeIn(ps), Path.GetFileName(ps));
                    }
                    else if (dn.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase) || dn.StartsWith("Krita", StringComparison.OrdinalIgnoreCase) || dn.Equals("paint.net", StringComparison.OrdinalIgnoreCase))
                        Add(ExeIn(d), dn);
                    else if (dn.StartsWith("Affinity", StringComparison.OrdinalIgnoreCase))
                        foreach (var sub in Directory.EnumerateDirectories(d)) if (Path.GetFileName(sub).StartsWith("Photo", StringComparison.OrdinalIgnoreCase)) Add(ExeIn(sub), "Affinity " + Path.GetFileName(sub));
                }
            }
            catch { }
        }
        return res.OrderBy(x => x.Rank).ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => (x.Name, x.Exe)).ToList();
    }
}
