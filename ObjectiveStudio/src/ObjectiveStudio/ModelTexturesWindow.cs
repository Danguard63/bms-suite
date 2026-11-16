using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Models;
using TerrainTool.Core.Project;

namespace ObjectiveStudio;

/// <summary>
/// Texture dei modelli 3D di aerei, elicotteri, mezzi e navi: griglia delle texture del modello scelto (tutte le livree e
/// i LOD), ingrandimento, quanti altri modelli usano la stessa texture, sostituzione con un'immagine qualsiasi (DDS, PNG,
/// JPG, BMP, TGA, TIF) convertita nel formato dell'originale (stesse dimensioni, compressione e mipmap), ripristino.
/// Le texture stanno nella cartella comune di BMS (valgono per tutti i teatri): prima di sostituire, BMS deve essere chiuso
/// e l'originale va in &lt;Objective Studio&gt;\Backup.
/// </summary>
public sealed class ModelTexturesWindow : Window
{
    private readonly TheaterProject? _p;
    private readonly TextBox _data = new() { MinWidth = 380, Margin = new Thickness(0, 0, 4, 0) };
    private readonly TextBox _search = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _cat = new() { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ListBox _list = new() { Width = 300 };
    private readonly WrapPanel _grid = new() { Margin = new Thickness(4) };
    private readonly Image _big = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), FontSize = 12 };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 10, Minimum = 0, Maximum = 1, Margin = new Thickness(6, 0, 6, 4) };
    private readonly Button _replace = new() { Content = "Replace with…", FontWeight = FontWeights.SemiBold };
    private readonly Button _export = new() { Content = "Export as PNG…" };
    private readonly Button _undo = new() { Content = "Restore previous version" };
    private readonly Button _openDir = new() { Content = "Open folder" };
    private readonly Button _view3dBtn = new() { Content = "3D view", FontWeight = FontWeights.SemiBold };
    private ModelViewWindow? _view3d;
    private List<ModelEntry> _all = new();
    private CampaignDb? _db;
    private ModelEntry? _model;
    private List<ModelTexture> _texs = new();
    private ModelTexture? _sel;
    private TextureUsageIndex? _usage;
    private Dictionary<int, string> _modelNames = new();
    private bool _busy, _warned;
    private CancellationTokenSource? _cts;

    private static string DataSetting => Path.Combine(MainWindow.Root(), "Settings", "bms_data_folder.txt");
    private static string UsageCache => Path.Combine(MainWindow.Root(), "Cache", "texture_usage.txt");

    /// <summary>Modello da aprire subito (numero della cartella Models), es. dal Campaign Editor.</summary>
    public int? StartModel { get; init; }

    public ModelTexturesWindow(TheaterProject? p)
    {
        _p = p;
        Title = "Model textures — aircraft, helicopters, vehicles, ships";
        Width = 1500; Height = 900; MinWidth = 1000; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string? data = p != null ? CampaignWorkspace.BmsDataDir(p.KoreaNewTerrain) : null;
        try { if (data == null && File.Exists(DataSetting)) data = File.ReadAllText(DataSetting).Trim(); } catch { }
        _data.Text = data ?? @"C:\Falcon BMS 4.38\Data";

        var top = new WrapPanel { Margin = new Thickness(6) };
        void L(string s) => top.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        L("BMS Data folder"); top.Children.Add(_data);
        top.Children.Add(Btn("…", BrowseData));
        top.Children.Add(Btn("Load", async () => await LoadAsync()));
        L("   Search"); top.Children.Add(_search);
        L("Show"); top.Children.Add(_cat);
        top.Children.Add(Btn("Backups…", () => new TextureBackupWindow { Owner = this }.ShowDialog()));
        top.Children.Add(Btn("Rebuild 'used by' index", async () => await BuildUsageAsync(force: true)));
        foreach (var c in new[] { "All", "Aircraft", "Helicopters", "Ground vehicles", "Ships", "Other", "All models by number" }) _cat.Items.Add(c);
        _cat.SelectedIndex = 0;
        _cat.SelectionChanged += (_, _) => FillList();
        _search.TextChanged += (_, _) => FillList();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: ModelEntry m }) ShowModel(m); };
        _list.MouseDoubleClick += (_, _) => Open3D();

        // dettaglio a destra
        var right = new DockPanel { Width = 520, Margin = new Thickness(6) };
        var btns = new WrapPanel();
        foreach (var b in new[] { _view3dBtn, _replace, _export, _undo, _openDir }) { b.Padding = new Thickness(8, 3, 8, 3); b.Margin = new Thickness(0, 0, 6, 4); btns.Children.Add(b); }
        _replace.Click += async (_, _) => await ReplaceAsync();
        _view3dBtn.Click += (_, _) => Open3D();
        _export.Click += async (_, _) => await ExportAsync();
        _undo.Click += (_, _) => RestorePrevious();
        _openDir.Click += (_, _) => { if (_sel != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_sel.File}\"") { UseShellExecute = true }); };
        var bigBox = new Border { Background = new DrawingBrush(Checker()) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute }, Child = _big, Height = 480, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var infoScroll = new ScrollViewer { Content = _info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(bigBox, Dock.Top); right.Children.Add(bigBox);
        DockPanel.SetDock(btns, Dock.Bottom); right.Children.Add(btns);
        right.Children.Add(infoScroll);

        var center = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new DockPanel();
        DockPanel.SetDock(_list, Dock.Left); body.Children.Add(_list);
        DockPanel.SetDock(right, Dock.Right); body.Children.Add(right);
        body.Children.Add(center);

        var bottom = new StackPanel();
        bottom.Children.Add(_bar); bottom.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 8, 4), FontSize = 11,
            Text = "Choose a vehicle on the left (double-click or '3D view' for the 3D model), click a texture to see it large. 'Replace with…' takes any DDS, PNG, JPG, BMP, TGA or TIF and converts it to the " +
                   "format of the original (same size, compression and mipmaps). Model textures are shared by all theaters (and often by several models: see 'Used by'). " +
                   "BMS must be closed; every replaced file is first copied to " + TextureBackupStore.Root + " (Backups… to restore).",
        };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;
        UpdateButtons();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private static Button Btn(string t, Action a)
    {
        var b = new Button { Content = t, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private static Drawing Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(225, 225, 225)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(225, 225, 225)), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        g.Freeze();
        return g;
    }

    private string ObjectsDir => Path.Combine(_data.Text.Trim(), "Terrdata", "objects");
    private string ModelsRoot => Path.Combine(ObjectsDir, "Models");

    private void BrowseData()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Falcon BMS Data folder (the one with Terrdata, Campaign, Art…)" };
        if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; _ = LoadAsync(); }
    }

    private async Task LoadAsync()
    {
        if (!Directory.Exists(ModelsRoot)) { _status.Text = $"{ModelsRoot} not found: choose the BMS Data folder"; return; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(DataSetting)!); File.WriteAllText(DataSetting, _data.Text.Trim()); } catch { }
        _status.Text = "Reading the BMS database…";
        var dirs = new List<string>();
        if (_p != null) dirs.AddRange(CampaignWorkspace.FromProject(_p).DbDirs);
        dirs.Add(ObjectsDir);
        dirs = dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var (db, all) = await Task.Run(() => { var db = CampaignDb.Load(dirs); return (db, ModelCatalog.Vehicles(db)); });
            _db = db; _all = all;
            _modelNames = all.GroupBy(m => m.ModelId).ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(m => m.Name).Distinct().Take(3)));
            _status.Text = $"{all.Count} vehicles with a 3D model ({db.CtPath})";
            FillList();
            if (StartModel is int sm)
            {
                var hit = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ModelEntry m && m.ModelId == sm);
                if (hit != null) { _list.SelectedItem = hit; _list.ScrollIntoView(hit); }
                else ShowModel(new ModelEntry(-1, $"#{sm}", "Model", sm, Path.Combine(ModelsRoot, sm.ToString(CultureInfo.InvariantCulture))));
            }
        }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
        await BuildUsageAsync(force: false);
    }

    private void FillList()
    {
        _list.Items.Clear();
        string cat = _cat.SelectedItem as string ?? "All", q = _search.Text.Trim();
        IEnumerable<ModelEntry> src;
        if (cat == "All models by number")
        {
            src = Directory.Exists(ModelsRoot)
                ? Directory.EnumerateDirectories(ModelsRoot).Select(d => Path.GetFileName(d)).Where(n => int.TryParse(n, out _)).Select(int.Parse).OrderBy(n => n)
                    .Select(n => new ModelEntry(-1, $"#{n}  {_modelNames.GetValueOrDefault(n, "")}", "Model", n, Path.Combine(ModelsRoot, n.ToString(CultureInfo.InvariantCulture))))
                : Enumerable.Empty<ModelEntry>();
        }
        else src = _all.Where(m => cat == "All" || m.Category == cat);
        foreach (var m in src.Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || m.ModelId.ToString(CultureInfo.InvariantCulture) == q))
            _list.Items.Add(new ListBoxItem { Content = m.Category == "Model" ? m.Name : $"{m.Name}   ({m.Category}, model {m.ModelId}{(m.ModelDir == null ? ", missing" : "")})", Tag = m });
    }

    private async void ShowModel(ModelEntry m)
    {
        _model = m; _sel = null; _grid.Children.Clear(); _big.Source = null; _info.Text = ""; UpdateButtons();
        if (m.ModelDir == null || !Directory.Exists(m.ModelDir)) { _status.Text = $"{m.Name}: model folder not found"; return; }
        var notes = new List<string>();
        var texDirs = CampaignDb.ModelTextureDirs(m.ModelDir);
        _texs = await Task.Run(() => ModelTextures.List(m.ModelDir, texDirs, notes));
        _status.Text = $"{m.Name}: model {m.ModelId}, {_texs.Count} textures" + (notes.Count > 0 ? "  —  " + string.Join("; ", notes.Distinct().Take(3)) : "");
        foreach (var t in _texs) _grid.Children.Add(Tile(t));
        if (_texs.Count > 0) Select(_texs[0]);
    }

    private FrameworkElement Tile(ModelTexture t)
    {
        var img = new Image { Width = 150, Height = 150, Stretch = Stretch.Uniform };
        var info = DdsEncoder.Inspect(Head(t.File));
        int users = _usage?.ModelsUsing(t.File).Count ?? 0;
        var cap = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Width = 150,
            Text = $"{t.Name}\n{t.Role}\n{(info != null ? $"{info.Width}×{info.Height} {info.Label}" : "unknown format")}" +
                   (t.Sets.Count > 0 ? $"\nlivery {t.SetsText}" : "") + (users > 1 ? $"\nused by {users} models" : ""),
            Foreground = users > 1 ? Brushes.DarkRed : Brushes.Black,
        };
        var sp = new StackPanel();
        sp.Children.Add(new Border { Child = img, Background = new DrawingBrush(Checker()) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute } });
        sp.Children.Add(cap);
        var b = new Border { Child = sp, Margin = new Thickness(4), Padding = new Thickness(4), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand, Tag = t };
        b.MouseLeftButtonUp += (_, _) => Select(t);
        _ = LoadThumbAsync(t.File, img, 256);
        return b;
    }

    private static byte[] Head(string f)
    {
        try { using var fs = File.OpenRead(f); var b = new byte[148]; int n = fs.Read(b, 0, b.Length); return b; } catch { return Array.Empty<byte>(); }
    }

    private static async Task LoadThumbAsync(string file, Image target, int max)
    {
        var bmp = await Task.Run(() =>
        {
            var d = DdsImage.Load(file, max);
            if (d == null) return null;
            var s = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Bgra, d.Width * 4);
            s.Freeze();
            return s;
        });
        target.Source = bmp;
    }

    private void Select(ModelTexture t)
    {
        _sel = t;
        foreach (var c in _grid.Children.OfType<Border>()) c.BorderBrush = c.Tag == t ? Brushes.DodgerBlue : Brushes.Transparent;
        var info = DdsEncoder.Inspect(Head(t.File));
        var users = _usage?.ModelsUsing(t.File) ?? Array.Empty<int>();
        string usedBy = _usage == null ? "(index not ready)" : users.Count == 0 ? "this model only" :
            string.Join("\n", users.Take(40).Select(u => $"  model {u}: {_modelNames.GetValueOrDefault(u, "(object / building)")}")) + (users.Count > 40 ? $"\n  … and {users.Count - 40} more" : "");
        _info.Text = $"{t.Name}   ({t.Role})\n{t.File}\n" +
                     (info != null ? $"{info.Width}×{info.Height}, {info.Label}, {info.Mips} mipmaps, {new FileInfo(t.File).Length / 1024.0:N0} KB\n" : "format not supported for replacing\n") +
                     (t.OtherCopy != null ? $"Other copy with the same number: {t.OtherCopy}\n" : "") +
                     $"Liveries (texture sets): {(t.Sets.Count > 0 ? t.SetsText : "-")}   LODs: {string.Join(",", t.Lods)}\n" +
                     $"Materials: {string.Join(", ", t.Materials.Take(12))}\n\nUsed by ({users.Count} models):\n{usedBy}";
        _big.Source = null;
        _ = LoadThumbAsync(t.File, _big, 2048);
        if (_view3d is { IsVisible: true } && t.Sets.Count > 0 && string.Equals(_view3d.ModelDir, _model?.ModelDir, StringComparison.OrdinalIgnoreCase)) _view3d.SelectSet(t.Sets.Min);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ok = _sel != null && !_busy;
        _replace.IsEnabled = ok && DdsEncoder.Inspect(Head(_sel!.File)) != null;
        _export.IsEnabled = ok;
        _openDir.IsEnabled = _sel != null;
        _view3dBtn.IsEnabled = _model?.ModelDir != null;
        _undo.IsEnabled = ok && TextureBackupStore.List().Any(e => string.Equals(e.Original, Path.GetFullPath(_sel!.File), StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── vista 3D

    private void Open3D()
    {
        if (_model?.ModelDir == null) return;
        int set = _sel?.Sets.Count > 0 ? _sel.Sets.Min : 0;
        if (_view3d is { IsVisible: true } && string.Equals(_view3d.ModelDir, _model.ModelDir, StringComparison.OrdinalIgnoreCase))
        {
            _view3d.SelectSet(set);
            _view3d.Activate();
            return;
        }
        _view3d?.Close();
        _view3d = ModelViewWindow.Open(this, _model.Name, new List<ModelViewWindow.Vehicle> { new(_model.Name, _model.Ct, _model.ModelDir) }, set);
    }

    /// <summary>Dopo una sostituzione o un ripristino: la vista 3D aperta rilegge la texture.</summary>
    private void Refresh3D(IEnumerable<string> files)
    {
        if (_view3d is not { IsVisible: true }) return;
        foreach (var f in files) _view3d.RefreshTextures(f);
    }

    // ───────────────────────── "usato da"

    private async Task BuildUsageAsync(bool force)
    {
        if (!Directory.Exists(ModelsRoot)) return;
        string root = Path.GetFullPath(ModelsRoot);
        if (!force && TextureUsageIndex.Load(UsageCache, root) is { } cached) { _usage = cached; RefreshTiles(); return; }
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var texDirs = new[] { "KoreaObj_HiRes", "KoreaObj" }.Select(n => Path.Combine(ObjectsDir, n)).Where(Directory.Exists).ToList();
        _status.Text = "Building the 'used by' index (all models, only the first time)…";
        var prog = new Progress<double>(v => _bar.Value = v);
        try
        {
            var idx = await Task.Run(() => TextureUsageIndex.Build(root, texDirs, prog, ct), ct);
            idx.Save(UsageCache);
            _usage = idx;
            _status.Text = $"'Used by' index ready: {idx.ByFile.Count:N0} texture files";
            RefreshTiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "'Used by' index not built: " + ex.Message; }
        finally { _bar.Value = 0; }
    }

    private void RefreshTiles()
    {
        if (_model == null) return;
        var sel = _sel;
        _grid.Children.Clear();
        foreach (var t in _texs) _grid.Children.Add(Tile(t));
        if (sel != null) Select(sel);
    }

    // ───────────────────────── sostituzione

    /// <summary>Immagine qualsiasi → RGBA (riga 0 in alto).</summary>
    private static (byte[] Rgba, int W, int H) ReadImage(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var bytes = File.ReadAllBytes(path);
        if (ext == ".dds")
        {
            var d = DdsImage.Decode(bytes, int.MaxValue) ?? throw new InvalidDataException("DDS format not supported");
            var rgba = new byte[d.Bgra.Length];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = d.Bgra[i + 2]; rgba[i + 1] = d.Bgra[i + 1]; rgba[i + 2] = d.Bgra[i]; rgba[i + 3] = d.Bgra[i + 3]; }
            return (rgba, d.Width, d.Height);
        }
        if (ext == ".tga") return TgaImage.Read(bytes);
        using var ms = new MemoryStream(bytes);
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var bgra = new byte[w * h * 4];
        conv.CopyPixels(bgra, w * 4, 0);
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return (bgra, w, h);
    }

    private async Task ReplaceAsync()
    {
        var t = _sel;
        if (t == null || _busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Image to put in place of {t.Name}",
            Filter = "Images (*.dds;*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.tif;*.tiff)|*.dds;*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var targets = new List<string> { t.File };
        if (t.OtherCopy != null && MessageBox.Show(this,
                $"The same texture number also exists in\n{t.OtherCopy}\n(BMS uses one or the other depending on the texture quality setting).\n\nReplace that copy too (converted to its own format and size)?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            targets.Add(t.OtherCopy);

        _busy = true; UpdateButtons();
        try
        {
            _status.Text = $"Reading {dlg.FileName}…";
            var (rgba, w, h) = await Task.Run(() => ReadImage(dlg.FileName));
            var users = _usage?.ModelsUsing(t.File) ?? Array.Empty<int>();
            var infos = targets.Select(f => (File: f, Head: File.ReadAllBytes(f))).Select(x => (x.File, x.Head, Info: DdsEncoder.Inspect(x.Head))).ToList();
            if (infos.Any(x => x.Info == null)) throw new InvalidDataException("the original texture format is not supported");
            string sizeNote = infos.Any(x => x.Info!.Width != w || x.Info.Height != h)
                ? $"\n\nYour image is {w}×{h}: it will be resized to {string.Join(" and ", infos.Select(x => $"{x.Info!.Width}×{x.Info.Height}"))}" +
                  (Math.Abs((double)w / h - (double)infos[0].Info!.Width / infos[0].Info!.Height) > 0.01 ? " (the proportions change!)" : "") + "."
                : "";
            string shared = users.Count > 1 ? $"\n\nWARNING: this texture is used by {users.Count} models; all of them will change." : "";
            string where = $"\n\nThese files are in the BMS installation and are used by all theaters. The originals are copied first to\n{TextureBackupStore.Root}";
            if (!_warned) where += "\n\nTip: keep your own backup of the BMS Data\\Terrdata\\objects folder too.";
            if (MessageBox.Show(this, $"Replace\n{string.Join("\n", targets)}\n\nwith\n{dlg.FileName}\n\nconverted to {string.Join(" / ", infos.Select(x => $"{x.Info!.Label}, {x.Info.Mips} mipmaps"))}.{sizeNote}{shared}{where}",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _warned = true;

            var prog = new Progress<double>(v => _bar.Value = v);
            foreach (var (file, head, info) in infos)
            {
                _status.Text = $"Encoding {Path.GetFileName(file)} ({info!.Label} {info.Width}×{info.Height}, {info.Mips} mipmaps)…";
                var data = await Task.Run(() =>
                {
                    var src = info.Width == w && info.Height == h ? rgba : DdsEncoder.Resize(rgba, w, h, info.Width, info.Height);
                    return DdsEncoder.Encode(info, head, src, prog);
                });
                string tmp = file + ".new";
                await File.WriteAllBytesAsync(tmp, data);
                TextureBackupStore.Save(file);
                File.Move(tmp, file, true);
            }
            _status.Text = $"Replaced {string.Join(", ", targets.Select(Path.GetFileName))} — original(s) in {TextureBackupStore.Root}";
            RefreshTiles();
            Refresh3D(targets);
        }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; _bar.Value = 0; UpdateButtons(); }
    }

    private async Task ExportAsync()
    {
        var t = _sel;
        if (t == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(t.Name) + ".png", Filter = "PNG (*.png)|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = await Task.Run(() =>
            {
                var d = DdsImage.Load(t.File, int.MaxValue) ?? throw new InvalidDataException("format not readable");
                var s = BitmapSource.Create(d.Width, d.Height, 96, 96, PixelFormats.Bgra32, null, d.Bgra, d.Width * 4);
                s.Freeze();
                return s;
            });
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
            _status.Text = $"Saved {dlg.FileName} ({bmp.PixelWidth}×{bmp.PixelHeight}). Edit it and use 'Replace with…' to put it back.";
        }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
    }

    private void RestorePrevious()
    {
        var t = _sel;
        if (t == null) return;
        var e = TextureBackupStore.List().FirstOrDefault(x => string.Equals(x.Original, Path.GetFullPath(t.File), StringComparison.OrdinalIgnoreCase));
        if (e == null) return;
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"Put back the version of {e.Date}?\n{e.Original}\n(the current one is backed up first; older versions: Backups…)", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { TextureBackupStore.Restore(e); _status.Text = $"Restored {e.Name} ({e.Date})"; RefreshTiles(); Refresh3D(new[] { e.Original }); }
        catch (Exception ex) { _status.Text = "ERROR: " + ex.Message; }
    }
}

/// <summary>
/// Copie dei file di texture sostituiti: &lt;Objective Studio&gt;\Backup\&lt;aaaaMMgg_hhmmss[_n]&gt;\&lt;file&gt; + origin.txt (percorso originale).
/// </summary>
public static class TextureBackupStore
{
    public sealed record Entry(string Folder, string File, string Original, DateTime When, long Size)
    {
        public string Name => System.IO.Path.GetFileName(File);
        public string Date => When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string SizeKb => (Size / 1024.0).ToString("N0", CultureInfo.InvariantCulture) + " KB";
    }

    public static string Root => Path.Combine(MainWindow.Root(), "Backup");
    private const string Index = "origin.txt";

    public static string? Save(string file)
    {
        if (!System.IO.File.Exists(file)) return null;
        string full = Path.GetFullPath(file);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture), dir = Path.Combine(Root, stamp);
        for (int k = 2; Directory.Exists(dir) && System.IO.File.Exists(Path.Combine(dir, Path.GetFileName(full))); k++) dir = Path.Combine(Root, stamp + "_" + k);
        Directory.CreateDirectory(dir);
        System.IO.File.Copy(full, Path.Combine(dir, Path.GetFileName(full)), true);
        System.IO.File.AppendAllText(Path.Combine(dir, Index), Path.GetFileName(full) + "\t" + full + "\r\n");
        return dir;
    }

    public static List<Entry> List()
    {
        var res = new List<Entry>();
        if (!Directory.Exists(Root)) return res;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            string idx = Path.Combine(dir, Index);
            if (!System.IO.File.Exists(idx)) continue;
            string stamp = Path.GetFileName(dir); int us = stamp.IndexOf('_', 9);
            DateTime when = DateTime.TryParseExact(us > 0 ? stamp[..us] : stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
            foreach (var line in System.IO.File.ReadAllLines(idx))
            {
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                string f = Path.Combine(dir, p[0]);
                if (System.IO.File.Exists(f)) res.Add(new Entry(dir, f, p[1], when, new FileInfo(f).Length));
            }
        }
        return res.OrderByDescending(e => e.When).ThenByDescending(e => e.Folder, StringComparer.Ordinal).ToList();
    }

    /// <summary>Rimette il file; la versione attuale va a sua volta nel Backup.</summary>
    public static void Restore(Entry e)
    {
        Save(e.Original);
        Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
        System.IO.File.Copy(e.File, e.Original, true);
    }
}

/// <summary>Elenco dei backup delle texture con "Restore".</summary>
public sealed class TextureBackupWindow : Window
{
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
        GridLinesVisibility = DataGridGridLinesVisibility.All, RowHeight = 22,
    };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0) };

    public TextureBackupWindow()
    {
        Title = "Objective Studio – texture backups";
        Width = 1100; Height = 520; MinWidth = 700; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        void C(string h, string p, double w) => _grid.Columns.Add(new DataGridTextColumn
            { Header = h, Binding = new Binding(p), Width = w == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w) });
        C("Saved on", nameof(TextureBackupStore.Entry.Date), 140); C("File", nameof(TextureBackupStore.Entry.Name), 220);
        C("Size", nameof(TextureBackupStore.Entry.SizeKb), 90); C("Goes back to", nameof(TextureBackupStore.Entry.Original), 0);
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var r = new Button { Content = "Restore", FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        r.Click += (_, _) => Restore();
        var o = new Button { Content = "Open folder", Padding = new Thickness(8, 2, 8, 2) };
        o.Click += (_, _) =>
        {
            string d = _grid.SelectedItem is TextureBackupStore.Entry e ? e.Folder : TextureBackupStore.Root;
            Directory.CreateDirectory(d);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + d + "\"") { UseShellExecute = true });
        };
        bar.Children.Add(r); bar.Children.Add(o);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(_grid);
        Content = root;
        _grid.MouseDoubleClick += (_, _) => Restore();
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        var l = TextureBackupStore.List();
        _grid.ItemsSource = l;
        _status.Text = l.Count == 0 ? "No backups yet." : $"{l.Count} backed-up file(s) in {TextureBackupStore.Root}";
    }

    private void Restore()
    {
        var sel = _grid.SelectedItems.OfType<TextureBackupStore.Entry>().ToList();
        if (sel.Count == 0) return;
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, "Restore\n\n" + string.Join("\n", sel.Select(e => $"{e.Name} ({e.Date}) → {e.Original}")) + "\n\nThe current versions are backed up first.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int n = 0;
        foreach (var e in sel) try { TextureBackupStore.Restore(e); n++; } catch (Exception ex) { MessageBox.Show(this, $"{e.Name}: {ex.Message}", Title); }
        Fill();
        _status.Text = $"Restored {n} file(s).";
    }
}
