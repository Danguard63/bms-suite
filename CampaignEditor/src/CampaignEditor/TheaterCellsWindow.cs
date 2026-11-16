using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;

namespace CampaignEditor;

/// <summary>
/// Il file &lt;teatro&gt;.thr della campagna (copertura, rilievo, ferrovia per km²), che BMS 4.38 usa ancora per tempi di marcia,
/// posizioni coperte, "vicino all'acqua" e mappa delle priorità. Mostra quello attuale, ne costruisce uno nuovo dal New Terrain
/// e lo confronta con la mappa che BMS genera per la campagna (cache .terrain.binpb). Salva fuori da BMS; "Install into BMS"
/// lo copia nella cartella Campaign con avviso, BMS chiuso e copia di sicurezza del file sostituito.
/// </summary>
public sealed class TheaterCellsWindow : Window
{
    private const int Px = 1024;
    private readonly CampaignWorkspace _ws;
    private readonly string _ntDir;
    private readonly string? _bmsCampaign;
    private readonly string _thrName;
    private readonly string? _currentPath;
    private readonly string? _cachePath;
    private TheaterCells? _current, _built;
    private CampaignTerrainCache? _cache;

    private readonly RadioButton _vCur = new() { Content = "Current .thr", Margin = new Thickness(0, 0, 10, 0), IsChecked = true };
    private readonly RadioButton _vNew = new() { Content = "New .thr (from the New Terrain)", Margin = new Thickness(0, 0, 10, 0) };
    private readonly RadioButton _vBms = new() { Content = "BMS campaign terrain (cache)", Margin = new Thickness(0, 0, 10, 0) };
    private readonly Image _img = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _info = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Height = 150, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _water = new() { Text = "50", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly TextBox _flat = new() { Text = "40", Width = 40, Margin = new Thickness(4, 0, 4, 0) };
    private readonly TextBox _rough = new() { Text = "120", Width = 40, Margin = new Thickness(4, 0, 4, 0) };
    private readonly TextBox _hills = new() { Text = "350", Width = 40, Margin = new Thickness(4, 0, 12, 0) };
    private readonly Button _build = new() { Content = "Build from the New Terrain", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _save = new() { Content = "Save", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _install = new() { Content = "Install into BMS…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly ProgressBar _bar = new() { Width = 200, Height = 14, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _check = new() { Content = "Check ground links", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(16, 0, 8, 0), IsEnabled = false,
        ToolTip = "For every link of the theater's TE template used by ground units: is there a path of 1 km cells that BMS lets them through (road, town, bridge, or land) near the straight line? Lists the links blocked by water." };
    private readonly Button _units = new() { Content = "Ground units in water…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false,
        ToolTip = "Brigades and battalions that the campaign files put in a km BMS counts as water (sea, lake, river): BMS finds no path for them " +
                  "('GetUnitGridPathNew failed') and they stay where they are. Moves them to the nearest km of land (same land as their objective, " +
                  "else the mainland), in every campaign and TE file of the theater; saved outside BMS (then Install campaign files into BMS)." };
    private readonly ListBox _issues = new() { Height = 330, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Image _zoom = new() { Width = 360, Height = 360, Stretch = Stretch.Uniform };
    private readonly TextBlock _zoomInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.DimGray,
        Text = "Pick a blocked link: 64 km around the water cell, the two objectives and the water cell marked in magenta." };

    public TheaterCellsWindow(CampaignWorkspace ws)
    {
        _ws = ws;
        Title = "Campaign terrain (.thr) – " + ws.Name;
        Width = 1650; Height = 1000;
        _ntDir = ws.Project != null ? ws.Project.OutputNewTerrain : ws.BmsTheater?.NewTerrainDir ?? "";
        _bmsCampaign = ws.Project != null ? ProjectInstall.TargetCampaign(ws.Project) : ws.BmsTheater?.CampaignDir;
        var dirs = ws.SearchDirs().Concat(_bmsCampaign != null ? new[] { _bmsCampaign } : Array.Empty<string>()).Where(Directory.Exists).ToList();
        _currentPath = dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.thr")).FirstOrDefault();
        _thrName = _currentPath != null ? Path.GetFileName(_currentPath) : CampaignWorkspace.SafeName(ws.Name) + ".thr";
        _cachePath = _bmsCampaign != null && Directory.Exists(_bmsCampaign) ? Directory.EnumerateFiles(_bmsCampaign, "*.terrain.binpb").FirstOrDefault() : null;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "The campaign's ." + "thr file has one cell per km² with ground cover (water, plain, forest, urban …), relief and rail. BMS 4.38 still uses it for " +
                   "the time units take to move, the covered positions of battalions, 'near water' checks of battalions and naval task forces, the priority map " +
                   "and part of the ground vehicles' AI. The paths of ground units come instead from the New Terrain LandCoverMap, which BMS turns into the " +
                   "campaign terrain cache (*.terrain.binpb) when a campaign is loaded. 'Build' makes a new .thr from the New Terrain (water as BMS draws it, " +
                   "LandCoverMap, relief from the HeightMap), so the two agree.",
        };
        var opts = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        void T(string s) => opts.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center });
        T("Water if more than (%)"); opts.Children.Add(_water);
        T("Relief from the height range in the km² (m): flat <"); opts.Children.Add(_flat);
        T("rough <"); opts.Children.Add(_rough); T("hills <"); opts.Children.Add(_hills); T("else mountains");
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var b in new UIElement[] { _build, _bar, _save, _install, _check, _units }) btns.Children.Add(b);
        var views = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        views.Children.Add(new TextBlock { Text = "Show: ", VerticalAlignment = VerticalAlignment.Center });
        views.Children.Add(_vCur); views.Children.Add(_vNew); views.Children.Add(_vBms);

        var top = new StackPanel();
        foreach (var e in new UIElement[] { intro, opts, btns, views, _legend }) top.Children.Add(e);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_info, Dock.Bottom); root.Children.Add(_info);
        var side = new StackPanel { Width = 380, Margin = new Thickness(8, 4, 0, 0) };
        side.Children.Add(new TextBlock { Text = "Ground links blocked by water", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        side.Children.Add(_issues); side.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _zoom, HorizontalAlignment = HorizontalAlignment.Left });
        side.Children.Add(_zoomInfo);
        DockPanel.SetDock(side, Dock.Right); root.Children.Add(side);
        root.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _img, Margin = new Thickness(0, 4, 0, 0) });
        Content = root;

        _vCur.Checked += (_, _) => ShowView(); _vNew.Checked += (_, _) => ShowView(); _vBms.Checked += (_, _) => ShowView();
        _build.Click += async (_, _) => await BuildAsync();
        _save.Click += (_, _) => SaveCopy();
        _install.Click += (_, _) => Install();
        _check.Click += async (_, _) => await CheckLinksAsync();
        _units.Click += async (_, _) => await UnitsInWaterAsync();
        _issues.SelectionChanged += (_, _) => ShowIssue();
        Loaded += (_, _) => LoadExisting();
    }

    private void Log(string s) { _info.AppendText(s + Environment.NewLine); _info.ScrollToEnd(); }

    private void LoadExisting()
    {
        Log($"Theater {_ws.Name}: {_ws.Theater.SizeKm} km. New Terrain: {(_ntDir.Length > 0 ? _ntDir : "not found")}");
        try
        {
            if (_currentPath != null) { _current = TheaterCells.Read(_currentPath); Log($"Current {_thrName} ({_currentPath}): {_current.Describe()}"); }
            else Log("No .thr file in the campaign folders: a new one will be named " + _thrName);
        }
        catch (Exception ex) { Log("Current .thr: " + ex.Message); }
        try
        {
            if (_cachePath != null)
            {
                _cache = CampaignTerrainCache.Read(_cachePath);
                Log($"BMS campaign terrain {Path.GetFileName(_cachePath)} ({File.GetLastWriteTime(_cachePath):yyyy-MM-dd HH:mm}): {_cache.SizeX}×{_cache.SizeY} km");
                if (_current != null) Log("Current .thr – " + _cache.CompareWater(_current));
            }
            else Log("No *.terrain.binpb in the BMS Campaign folder: BMS writes it when a campaign of this theater is loaded (start one once, then reopen this window).");
        }
        catch (Exception ex) { Log("BMS campaign terrain: " + ex.Message); }
        _vBms.IsEnabled = _cache != null;
        _check.IsEnabled = _cache != null;
        _units.IsEnabled = _cache != null;
        if (_cache == null)
        {
            const string why = "Needs the campaign terrain BMS writes (*.terrain.binpb in the Campaign folder): start a campaign of this theater once in BMS " +
                                "(Install objects deletes it so that BMS writes it again), then reopen this window.";
            ToolTipService.SetShowOnDisabled(_check, true); ToolTipService.SetShowOnDisabled(_units, true);
            _check.ToolTip = why; _units.ToolTip = why;
        }
        _vNew.IsEnabled = false;
        if (_current == null) _vBms.IsChecked = _cache != null;
        ShowView();
    }

    /// <summary>Il modello del TE del teatro (tutti gli obiettivi e collegamenti): prima quello installato in BMS, poi le copie del progetto.</summary>
    private string? TemplatePath()
    {
        foreach (var n in new[] { "Te_New_Nt.tac", "te_new.tac" })
        {
            if (_bmsCampaign != null && Directory.Exists(_bmsCampaign) &&
                Directory.EnumerateFiles(_bmsCampaign).FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase)) is { } b) return b;
            if (_ws.FindCampaignFile(n) is { } w) return w;
        }
        return null;
    }

    private async Task CheckLinksAsync()
    {
        if (_cache == null) return;
        string? tpl = TemplatePath();
        if (tpl == null) { Log("No Te_New_Nt.tac / te_new.tac found: nothing to check."); return; }
        _check.IsEnabled = false;
        try
        {
            var cache = _cache;
            var (issues, n) = await Task.Run(() =>
            {
                var doc = CampaignDoc.Load(tpl, _ => null, new[] { Path.GetDirectoryName(tpl)! });
                return CampaignTerrainCheck.Check(doc, cache);
            });
            _issues.Items.Clear();
            foreach (var i in issues.OrderBy(i => i.WaterY).ThenBy(i => i.WaterX)) _issues.Items.Add(i);
            Log($"Ground links of {Path.GetFileName(tpl)}: {n} checked, {issues.Count} with no way across the water (road, town or bridge cell) near the straight line. " +
                "Rule of BMS 4.38: road or town cell = passable, bridge cell = passable if the bridge objective is intact, " +
                "water or river cell = not passable, the rest = passable with the relief cost.");
            if (issues.Count > 0)
            {
                Directory.CreateDirectory(_ws.EditsFolder);
                string csv = Path.Combine(_ws.EditsFolder, "ground_links_blocked.csv");
                File.WriteAllLines(csv, new[] { "CampIdA;NameA;CampIdB;NameB;Km;WaterX_km;WaterY_km" }.Concat(issues.Select(i =>
                    string.Join(";", i.CampA, i.NameA, i.CampB, i.NameB, i.Km.ToString("0", CultureInfo.InvariantCulture), i.WaterX, i.WaterY))));
                Log("List saved in " + csv);
            }
        }
        catch (Exception ex) { Log("Check failed: " + ex.Message); }
        finally { _check.IsEnabled = true; }
    }

    private async Task UnitsInWaterAsync()
    {
        if (_cache == null) return;
        _units.IsEnabled = false;
        try
        {
            var cache = _cache;
            var ws = _ws;
            // prima solo il conto (niente si scrive), poi la conferma
            Log("Ground units in water: reading the campaign and TE files of the theater…");
            var (moves, files, db) = await Task.Run(() =>
            {
                var db = CampaignDb.Load(ws.DbDirs);
                var g = new GroundUnitsInWater(cache);
                var all = new List<GroundUnitsInWater.Move>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var touched = new List<string>();
                foreach (var (path, _) in ws.CampaignFiles())
                {
                    string name = Path.GetFileName(path);
                    if (!seen.Add(name)) continue;   // la copia salvata prima dell'originale
                    // salvataggi automatici e Instant Action: non sono scenari da distribuire
                    if (name.StartsWith("Auto Save", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Instant", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var doc = CampaignDoc.Load(path, db.ClassOf, ws.SearchDirs().ToArray());
                        var m = g.Fix(doc, name, false);
                        if (m.Count > 0) { all.AddRange(m); touched.Add(path); }
                    }
                    catch (Exception) { /* file non leggibile: si salta */ }
                }
                return (all, touched, db);
            });
            if (moves.Count == 0) { Log("Ground units in water: none."); return; }
            var byFile = moves.GroupBy(m => m.File).Select(gr => $"{gr.Key}: {gr.Count()}").ToList();
            Log($"Ground units in water: {moves.Count} in {files.Count} files ({string.Join(", ", byFile)}); moves of at most {moves.Max(m => Math.Max(Math.Abs(m.ToX - m.FromX), Math.Abs(m.ToY - m.FromY)))} km.");
            if (MessageBox.Show(this, $"{moves.Count} brigades and battalions are in a km of water in {files.Count} campaign/TE files.\n\n" +
                    "Move them to the nearest km of land? The files are saved outside BMS (the project's campaign copies, with a .bak); " +
                    "then use 'Install campaign files into BMS'.", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { Log("Ground units in water: nothing changed."); return; }
            var done = await Task.Run(() =>
            {
                var g = new GroundUnitsInWater(cache);
                var res = new List<GroundUnitsInWater.Move>();
                foreach (var path in files)
                {
                    string name = Path.GetFileName(path);
                    var doc = CampaignDoc.Load(path, db.ClassOf, ws.SearchDirs().ToArray());
                    var m = g.Fix(doc, name, true);
                    if (m.Count == 0) continue;
                    ws.SaveCampaignFile(name, dst => doc.Save(dst));
                    res.AddRange(m);
                }
                return res;
            });
            Directory.CreateDirectory(_ws.EditsFolder);
            string csv = Path.Combine(_ws.EditsFolder, "ground_units_moved.csv");
            GroundUnitsInWater.SaveCsv(csv, done);
            Log($"Ground units in water: {done.Count} moved and saved in {_ws.EditsFolder} (list: {csv}). Next: Install campaign files into BMS.");
        }
        catch (Exception ex) { Log("Ground units in water failed: " + ex.Message); }
        finally { _units.IsEnabled = _cache != null; }
    }

    private void ShowIssue()
    {
        if (_cache == null || _issues.SelectedItem is not CampaignTerrainCheck.Issue i) return;
        string? tpl = TemplatePath();
        var marks = new List<(int, int)> { (i.WaterX, i.WaterY) };
        try
        {
            if (tpl != null)
            {
                _tplDoc ??= CampaignDoc.Load(tpl, _ => null, new[] { Path.GetDirectoryName(tpl)! });
                foreach (var o in _tplDoc.Objectives.Where(o => o.CampId == i.CampA || o.CampId == i.CampB)) marks.Add(CampaignTerrainCheck.Cell(o));
            }
        }
        catch (Exception) { }
        const int px = 512;
        var img = CampaignTerrainCheck.Zoom(_cache, i.WaterX, i.WaterY, 32, px, marks);
        _zoom.Source = BitmapSource.Create(px, px, 96, 96, PixelFormats.Bgra32, null, img, px * 4);
        _zoomInfo.Text = $"{i}\nGrid {i.WaterX},{i.WaterY} km (x east, y north). Fix: move the bridge onto the crossing, add a bridge objective there, or link the objectives on the same bank.";
    }

    private CampaignDoc? _tplDoc;

    private static double Num(TextBox b, double def) => double.TryParse(b.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    private async Task BuildAsync()
    {
        if (!Directory.Exists(_ntDir)) { MessageBox.Show(this, "New Terrain folder not found: " + _ntDir, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var opt = new TheaterCellsBuilder.Options
        {
            WaterFraction = Math.Clamp(Num(_water, 50) / 100, 0.05, 0.95),
            FlatM = Num(_flat, 40), RoughM = Num(_rough, 120), HillsM = Num(_hills, 350),
        };
        _build.IsEnabled = false; _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        var prog = new Progress<double>(v => _bar.Value = v);
        int km = _ws.Theater.SizeKm;
        try
        {
            Log("Building from " + _ntDir + " (reads the whole HeightMap, WaterHeightMap and LandCoverMap: it can take a few minutes) …");
            var res = await Task.Run(() => TheaterCellsBuilder.Build(_ntDir, km, opt, prog));
            _built = res.Cells;
            foreach (var n in res.Notes) Log("  " + n);
            if (_current != null) Log("New vs current: " + _built.Compare(_current));
            if (_cache != null) Log("New .thr – " + _cache.CompareWater(_built));
            if (_current != null && (_current.MaxX != km || _current.MaxY != km)) Log($"Note: the current .thr is {_current.MaxX}×{_current.MaxY}, the theater is {km} km.");
            _vNew.IsEnabled = true; _vNew.IsChecked = true;
            _save.IsEnabled = true; _install.IsEnabled = _bmsCampaign != null;
            ShowView();
        }
        catch (Exception ex) { Log("Build failed: " + ex.Message); }
        finally { _build.IsEnabled = true; _bar.Visibility = Visibility.Collapsed; }
    }

    private void ShowView()
    {
        byte[]? px = null;
        _legend.Children.Clear();
        if (_vBms.IsChecked == true && _cache != null)
        {
            px = _cache.Image(Px);
            foreach (var (n, c) in new[] { ("Water", (40, 80, 170)), ("River", (60, 140, 255)), ("Road", (215, 45, 35)), ("Bridge", (255, 215, 0)), ("Runway", (0, 0, 0)),
                         ("Urban", (150, 150, 150)), ("Flat", (222, 205, 160)), ("Rough", (190, 160, 120)), ("Highlands", (170, 140, 110)), ("Mountain", (130, 110, 100)), ("Woods", (70, 130, 60)) })
                AddLegend(n, c.Item1, c.Item2, c.Item3);
        }
        else
        {
            var t = _vNew.IsChecked == true ? _built : _current;
            if (t != null) px = t.Image(Px);
            for (int i = 0; i < 8; i++) AddLegend(TheaterCells.CoverNames[i], TheaterCells.CoverColors[i].R, TheaterCells.CoverColors[i].G, TheaterCells.CoverColors[i].B);
            AddLegend("Rail", 0, 0, 0);
            _legend.Children.Add(new TextBlock { Text = "  (darker = rougher relief)", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        }
        _img.Source = px == null ? null : BitmapSource.Create(Px, Px, 96, 96, PixelFormats.Bgra32, null, px, Px * 4);
    }

    private void AddLegend(string name, int r, int g, int b)
    {
        _legend.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 3, 0) });
        _legend.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
    }

    /// <summary>Salva fuori da BMS (cartella di salvataggio del teatro e, nel progetto, la copia Install).</summary>
    private void SaveCopy()
    {
        if (_built == null) return;
        try
        {
            string dst = _ws.SaveCampaignFile(_thrName, p => _built.Write(p));
            Log("Saved " + dst + (_ws.InstallCopyDir != null ? " (and the Install copy)" : ""));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Install()
    {
        if (_built == null || _bmsCampaign == null) return;
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string dst = Path.Combine(_bmsCampaign, _thrName);
        if (MessageBox.Show(this,
                $"Write the new {_thrName} into the BMS installation?\n{dst}\n\n" +
                "Make a backup of this Campaign folder first if you have not done it yet (a BMS update or a reinstall also overwrites these files).\n" +
                $"The file now in BMS is also kept in {BackupStore.Root} ('Backups…' to restore it).",
                "Install into BMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            SaveCopy();
            string? bk = BackupStore.Save(dst);
            _built.Write(dst);
            Log("Installed " + dst + (bk != null ? "; previous file backed up in " + bk : ""));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
