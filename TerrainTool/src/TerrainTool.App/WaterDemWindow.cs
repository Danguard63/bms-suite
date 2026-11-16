using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Project;

namespace TerrainTool.App;

/// <summary>
/// Modalita' "Water DEM" di Terrain Builder rifatta nel tool: controlli prima di partire (errori e avvisi, invece di TB che si
/// chiude senza dire perche'), thread scelti in base a core e RAM della macchina, barre di avanzamento (totale e passo),
/// registro a video e su file (Output\WaterDem\WaterDem_log.txt), confronto con le mappe di Terrain Builder.
/// </summary>
public sealed class WaterDemWindow : Window
{
    private readonly TheaterProject _project;
    private readonly Action _saveProject;
    private readonly TextBox _dem = new(), _water = new(), _coast = new(), _points = new(), _out = new(), _tbOut = new();
    private readonly TextBox _depth = new() { Width = 50 }, _hidden = new() { Width = 50 }, _culling = new() { Width = 50 };
    private readonly ComboBox _threads = new() { Width = 70 };
    private readonly TextBlock _machine = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListBox _checks = new() { Height = 120, Margin = new Thickness(0, 4, 0, 6) };
    private readonly ProgressBar _total = new() { Height = 16, Minimum = 0, Maximum = 1 };
    private readonly ProgressBar _step = new() { Height = 10, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Button _run = new() { Content = "Run Water DEM", FontWeight = FontWeights.SemiBold };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _check = new() { Content = "Check again" };
    private readonly Button _compare = new() { Content = "Compare with Terrain Builder" };
    private readonly Button _openOut = new() { Content = "Open output folder" };
    private readonly Button _islands = new() { Content = "Check islands (OSM)", ToolTip = "Lists the islands OpenStreetMap knows in the theater (named, or 1 km² and more) and checks each one on the finished terrain: present, missing in the Watermap, or in the Watermap but under water. Internet needed; reads only; writes islands_check.csv in the project folder." };
    private readonly Button _coastCheck = new() { Content = "Check coasts and islands", ToolTip = "For every objective with pieces under water in the finished terrain (as BMS draws it): why — the Watermap says water, the DEM is at water level, the shore smoothing lowered the land, or the water level is above the land. Reads only; writes coast_check.csv in the project folder." };
    private readonly CheckBox _use = new() { Margin = new Thickness(0, 6, 0, 0), Content = "Use these water maps for the texture jobs (tab 5) instead of Terrain Builder's Water DEM output" };
    private CancellationTokenSource? _cts;
    private string? _logFile;

    public WaterDemWindow(TheaterProject project, string tbOutput, Action saveProject)
    {
        _project = project; _saveProject = saveProject;
        Title = "Water DEM (replaces the Terrain Builder mode)";
        Width = 1050; Height = 860; MinWidth = 820; MinHeight = 620;
        string tbIn = Path.Combine(project.ProjectFolder, "Output", "TB_Input");
        var (maps, _) = TbAnalysis.Find(tbIn, "");
        _dem.Text = maps.FirstOrDefault(m => m.Key == "DEM")?.Path ?? "";
        _water.Text = maps.FirstOrDefault(m => m.Key == "Watermap")?.Path ?? Path.Combine(tbIn, "Watermap.raw");
        _coast.Text = maps.FirstOrDefault(m => m.Key == "Coastline")?.Path ?? Path.Combine(tbIn, "Coastline.raw");
        _points.Text = Path.Combine(tbIn, "CoastPoints.shp");
        _out.Text = project.WaterDemOutput;
        _tbOut.Text = tbOutput;
        var def = new WaterDemOptions();
        _depth.Text = def.DepthFt.ToString(); _hidden.Text = def.HiddenWaterFt.ToString(); _culling.Text = def.CullingKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 1; i <= Environment.ProcessorCount; i++) _threads.Items.Add(i);
        _threads.SelectedItem = def.Threads;
        long ram = WaterDem.AvailableMemory();
        _machine.Text = $"this PC: {Environment.ProcessorCount} logical cores, {(ram > 0 ? $"{ram / 1073741824.0:0.0} GB RAM free" : "free RAM unknown")} " +
                        "(default: all cores but one, fewer if the RAM is not enough)";
        _use.IsChecked = project.UseToolWaterDem;

        var form = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        void Row(string label, TextBox box, bool file, string tip)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2), ToolTip = tip };
            Grid.SetRow(l, row); form.Children.Add(l);
            box.Margin = new Thickness(0, 2, 0, 2); box.ToolTip = tip;
            Grid.SetRow(box, row); Grid.SetColumn(box, 1); form.Children.Add(box);
            var b = new Button { Content = "…", Width = 28, Margin = new Thickness(6, 2, 0, 2) };
            b.Click += (_, _) => Browse(box, file);
            Grid.SetRow(b, row); Grid.SetColumn(b, 2); form.Children.Add(b);
            row++;
        }
        Row("DEM (16-bit RAW, feet)", _dem, true, "The heightmap written in tab 4 (Output\\TB_Input\\<theater>_<size>.RAW)");
        Row("Watermap (8-bit RAW)", _water, true, "0 land, 25 inland water, 45 ocean (tab 4)");
        Row("Coastline (8-bit RAW)", _coast, true, "Optional: the coast lines (tab 4). Without it the sea floor has no gentle slope at the beaches");
        Row("Coast points (SHP)", _points, true, "Optional: CoastPoints.shp (tab 4). Places the shore-wave patches of the FFTMap; needs Watermap.hdr next to the Watermap");
        Row("Output folder", _out, false, "HeightMaps, WaterMaps and GroundTypes are written here, as Terrain Builder does");
        Row("Terrain Builder output (compare)", _tbOut, false, "Only for 'Compare with Terrain Builder': the folder where TB wrote its Water DEM maps");

        var opts = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        void Opt(string label, FrameworkElement c, string tip)
        {
            opts.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = tip });
            c.Margin = new Thickness(0, 0, 14, 0); c.ToolTip = tip; opts.Children.Add(c);
        }
        Opt("Water depth ft", _depth, "Sea and lake floor below the DEM away from the shore (TB: 40)");
        Opt("Hidden water ft", _hidden, "How far below the land the water surface is put where there is no water (TB: ~300)");
        Opt("Culling km", _culling, "Open sea farther than this from land is not drawn under water (TB: ~10.5)");
        Opt("Threads", _threads, "Parallel bands of rows");
        opts.Children.Add(_machine);

        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var b in new[] { _run, _cancel, _check, _compare, _coastCheck, _islands, _openOut })
        {
            b.Padding = new Thickness(10, 3, 10, 3); b.Margin = new Thickness(0, 0, 8, 4); bar.Children.Add(b);
        }
        _run.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _check.Click += (_, _) => RunChecks();
        _compare.Click += async (_, _) => await CompareAsync();
        _coastCheck.Click += async (_, _) => await CoastCheckAsync();
        _islands.Click += async (_, _) => await IslandCheckAsync();
        _openOut.Click += (_, _) => { if (Directory.Exists(_out.Text)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_out.Text}\"") { UseShellExecute = true }); };
        _use.Checked += (_, _) => SetUse(true);
        _use.Unchecked += (_, _) => SetUse(false);

        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.DimGray,
            Text = "Rebuilds Terrain Builder's 'Water DEM' mode: sea and lake floor in Heightmap.raw, WaterHeightmap.raw, WaterReflectionDemRefmap.raw, " +
                   "Depthmap_Red/Blue.raw, GlobalAlpha_Blue.raw, CullingMap.raw and FFTMap.raw, with the rules found by comparing TB's inputs and outputs " +
                   "(docs\\TB_WaterDEM_spec.txt). The checks below run before starting: red lines stop the job, orange lines are warnings.",
        });
        top.Children.Add(form);
        top.Children.Add(opts);
        top.Children.Add(new TextBlock { Text = "Checks", FontWeight = FontWeights.SemiBold });
        top.Children.Add(_checks);
        top.Children.Add(bar);
        top.Children.Add(_total);
        top.Children.Add(_step);
        top.Children.Add(_status);
        top.Children.Add(_use);
        top.Children.Add(new TextBlock { Text = "Log", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });

        var dock = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_log);
        Content = dock;

        foreach (var tb in new[] { _dem, _water, _coast, _points, _out }) tb.LostFocus += (_, _) => RunChecks();
        Loaded += (_, _) => RunChecks();
        Closing += (_, e) =>
        {
            if (_cts == null) return;
            if (MessageBox.Show(this, "The job is still running. Cancel it and close?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
            else _cts.Cancel();
        };
    }

    private void SetUse(bool on)
    {
        if (_project.UseToolWaterDem == on) return;
        if (on && !File.Exists(Path.Combine(_out.Text, "HeightMaps", "WaterHeightmap.raw")))
        {
            MessageBox.Show(this, "Run the Water DEM first: the output folder has no maps yet.", Title);
            _use.IsChecked = false; return;
        }
        _project.UseToolWaterDem = on;
        _saveProject();
        Log(on ? $"Texture jobs now take the water maps from {_project.WaterDemOutput}" : "Texture jobs now take the water maps from Terrain Builder's output");
    }

    private void Browse(TextBox box, bool file)
    {
        if (file)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = box == _points ? "Shapefiles (*.shp)|*.shp|All files|*.*" : "RAW files (*.raw)|*.raw|All files|*.*" };
            if (File.Exists(box.Text)) dlg.InitialDirectory = Path.GetDirectoryName(box.Text);
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FileName;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
        }
        RunChecks();
    }

    private WaterDemOptions? Options(out string? error)
    {
        error = null;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(_depth.Text.Trim(), out int depth) || depth < 1 || depth > 1000) { error = "Water depth: a number of feet between 1 and 1000"; return null; }
        if (!int.TryParse(_hidden.Text.Trim(), out int hidden) || hidden < 10 || hidden > 5000) { error = "Hidden water: a number of feet between 10 and 5000"; return null; }
        if (!double.TryParse(_culling.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, ci, out double cul) || cul < 0 || cul > 200) { error = "Culling: km between 0 and 200"; return null; }
        string outDir = _out.Text.Trim();
        if (outDir.Length > 0 && _project.IsProtectedPath(outDir)) { error = "The output folder is inside BMS, the old theater or Terrain Builder: choose another one"; return null; }
        return new WaterDemOptions
        {
            DemPath = _dem.Text.Trim(), WatermapPath = _water.Text.Trim(),
            CoastlinePath = string.IsNullOrWhiteSpace(_coast.Text) ? null : _coast.Text.Trim(),
            CoastPointsPath = string.IsNullOrWhiteSpace(_points.Text) ? null : _points.Text.Trim(),
            OutDir = outDir, Threads = _threads.SelectedItem is int t ? t : Math.Max(1, Environment.ProcessorCount - 1),
            DepthFt = depth, HiddenWaterFt = hidden, CullingKm = cul,
        };
    }

    private bool RunChecks()
    {
        _checks.Items.Clear();
        var o = Options(out var err);
        if (o == null) { AddCheck(new WaterDemCheck(true, err!)); return false; }
        List<WaterDemCheck> res;
        try { res = WaterDem.Preflight(o); }
        catch (Exception ex) { res = new() { new WaterDemCheck(true, "Check failed: " + ex.Message) }; }
        foreach (var c in res) AddCheck(c);
        bool ok = !res.Any(c => c.Error);
        _run.IsEnabled = ok && _cts == null;
        return ok;
    }

    private void AddCheck(WaterDemCheck c)
    {
        bool okLine = !c.Error && c.Text.StartsWith("OK");
        _checks.Items.Add(new TextBlock
        {
            Text = (c.Error ? "✖ " : okLine ? "✔ " : "⚠ ") + c.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 980,
            Foreground = c.Error ? Brushes.Firebrick : okLine ? Brushes.DarkGreen : Brushes.DarkOrange,
        });
    }

    private void Log(string s)
    {
        string line = $"{DateTime.Now:HH:mm:ss} {s}";
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
        if (_logFile != null)
            try { File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8); } catch { /* il registro a video resta */ }
    }

    private void Busy(bool on)
    {
        _run.IsEnabled = !on; _check.IsEnabled = !on; _compare.IsEnabled = !on; _coastCheck.IsEnabled = !on; _islands.IsEnabled = !on; _cancel.IsEnabled = on; _use.IsEnabled = !on;
        foreach (var tb in new[] { _dem, _water, _coast, _points, _out, _tbOut, _depth, _hidden, _culling }) tb.IsReadOnly = on;
        _threads.IsEnabled = !on;
        if (!on) { _total.Value = 0; _step.Value = 0; }
    }

    private IProgress<WaterDemProgress> MakeProgress(Stopwatch sw)
    {
        string lastStep = "";
        return new Progress<WaterDemProgress>(x =>
        {
            _total.Value = Math.Clamp(x.Total, 0, 1); _step.Value = Math.Clamp(x.StepFraction, 0, 1);
            string eta = x.Total > 0.03 ? $", about {TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - x.Total) / x.Total):mm\\:ss} left" : "";
            _status.Text = $"{x.Total * 100:0}%  {x.Step} ({x.StepFraction * 100:0}%)  elapsed {sw.Elapsed:mm\\:ss}{eta}";
            if (x.Step != lastStep) { lastStep = x.Step; Log("▶ " + x.Step); }
        });
    }

    private async Task RunAsync()
    {
        if (_cts != null || !RunChecks()) return;
        var o = Options(out _)!;
        Directory.CreateDirectory(o.OutDir);
        _logFile = Path.Combine(o.OutDir, "WaterDem_log.txt");
        try { File.WriteAllText(_logFile, $"Water DEM log  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Environment.MachineName}  {Environment.ProcessorCount} cores\r\n", Encoding.UTF8); }
        catch (Exception ex) { _logFile = null; Log("Warning: log file not written: " + ex.Message); }
        Log($"DEM {o.DemPath}"); Log($"Watermap {o.WatermapPath}"); Log($"Coastline {o.CoastlinePath ?? "(none)"}"); Log($"Coast points {o.CoastPointsPath ?? "(none)"}");
        Log($"Depth {o.DepthFt} ft, hidden water {o.HiddenWaterFt} ft, culling {o.CullingKm} km, threads {o.Threads}");
        // acqua aggiunta o tolta a mano nell'editor degli obiettivi (water_paint.csv): solo sulla Watermap del progetto a piena dimensione
        try
        {
            string projWm = Path.Combine(_project.ProjectFolder, "Output", "TB_Input", "Watermap.raw");
            if (string.Equals(Path.GetFullPath(o.WatermapPath), Path.GetFullPath(projWm), StringComparison.OrdinalIgnoreCase))
                Log(TerrainTool.Core.Editor.WaterPaint.ApplyToWatermap(_project.ProjectFolder, o.WatermapPath, _project.Theater.SizeMeters) ?? "Water paint: no strokes");
            else if (File.Exists(TerrainTool.Core.Editor.WaterPaint.PathFor(_project.ProjectFolder)))
                Log("Water paint: not applied (the Watermap is not Output\\TB_Input\\Watermap.raw of the project)");
        }
        catch (Exception ex) { Log("Water paint not applied: " + ex.Message); }
        _cts = new CancellationTokenSource();
        Busy(true);
        var sw = Stopwatch.StartNew();
        var prog = MakeProgress(sw);
        void LogAny(string s) => Dispatcher.BeginInvoke(() => Log(s));
        try
        {
            var notes = await Task.Run(() => WaterDem.Run(o, prog, LogAny, _cts.Token), _cts.Token);
            foreach (var n in notes) Log(n);
            _total.Value = 1;
            Log($"DONE in {sw.Elapsed:hh\\:mm\\:ss}. Tick 'Use these water maps…' to use them in tab 5, or compare them with Terrain Builder first.");
        }
        catch (OperationCanceledException) { Log("Cancelled: the maps in the output folder are incomplete."); }
        catch (Exception ex)
        {
            string why = Explain(ex);
            Log("ERROR: " + why);
            Log(ex.ToString());
            MessageBox.Show(this, why + (_logFile != null ? $"\n\nDetails in {_logFile}" : ""), "Water DEM failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose(); _cts = null;
            Busy(false);
            RunChecks();
        }
    }

    private async Task CompareAsync()
    {
        if (_cts != null) return;
        string ours = _out.Text.Trim(), tb = _tbOut.Text.Trim();
        if (!File.Exists(Path.Combine(ours, "HeightMaps", "Heightmap.raw"))) { MessageBox.Show(this, "Run the Water DEM first.", Title); return; }
        if (!Directory.Exists(tb)) { MessageBox.Show(this, "Set the folder where Terrain Builder wrote its Water DEM maps.", Title); return; }
        if (!File.Exists(_water.Text.Trim())) { MessageBox.Show(this, "The Watermap is needed to split the differences by land / inland water / sea.", Title); return; }
        string outDir = Path.Combine(ours, "Compare");
        int threads = _threads.SelectedItem is int t ? t : Math.Max(1, Environment.ProcessorCount - 1);
        _logFile ??= Path.Combine(ours, "WaterDem_log.txt");
        Log($"Compare: {ours} vs Terrain Builder {tb}");
        _cts = new CancellationTokenSource();
        Busy(true);
        var sw = Stopwatch.StartNew();
        var prog = MakeProgress(sw);
        string water = _water.Text.Trim();
        try
        {
            var lines = await Task.Run(() => WaterDem.Compare(ours, tb, water, outDir, threads, prog, _cts.Token), _cts.Token);
            foreach (var l in lines) Log(l);
            Log($"Compare done: {Path.Combine(outDir, "compare.txt")} (diff_*.raw: differences reduced to 2048, 16-bit)");
        }
        catch (OperationCanceledException) { Log("Compare cancelled"); }
        catch (Exception ex) { Log("Compare ERROR: " + Explain(ex)); }
        finally { _cts.Dispose(); _cts = null; Busy(false); RunChecks(); }
    }

    private async Task IslandCheckAsync()
    {
        if (_cts != null) return;
        string water = _water.Text.Trim();
        _logFile ??= Directory.Exists(_out.Text.Trim()) ? Path.Combine(_out.Text.Trim(), "WaterDem_log.txt") : null;
        Log("Check islands: OpenStreetMap islands of the theater against the finished terrain (Output\\NewTerrain) and the Watermap above");
        _cts = new CancellationTokenSource();
        Busy(true);
        var prog = new Progress<string>(m => _status.Text = m);
        try
        {
            var (_, summary) = await IslandCheck.RunAsync(_project, water, prog, _cts.Token);
            foreach (var l in summary) Log(l);
        }
        catch (OperationCanceledException) { Log("Check cancelled"); }
        catch (Exception ex) { Log("Check ERROR: " + Explain(ex)); }
        finally { _cts.Dispose(); _cts = null; Busy(false); RunChecks(); _status.Text = ""; }
    }

    private async Task CoastCheckAsync()
    {
        if (_cts != null) return;
        string demPath = _dem.Text.Trim(), water = _water.Text.Trim();
        _logFile ??= Directory.Exists(_out.Text.Trim()) ? Path.Combine(_out.Text.Trim(), "WaterDem_log.txt") : null;
        Log("Check coasts and islands: objectives under water in the finished terrain (Output\\NewTerrain), with the DEM and the Watermap above");
        _cts = new CancellationTokenSource();
        Busy(true);
        var prog = new Progress<double>(x => { _total.Value = x; _status.Text = $"Checking objectives {x * 100:0}%"; });
        try
        {
            var (_, summary) = await Task.Run(() => CoastCheck.Run(_project, demPath, water, prog, _cts.Token), _cts.Token);
            foreach (var l in summary) Log(l);
        }
        catch (OperationCanceledException) { Log("Check cancelled"); }
        catch (Exception ex) { Log("Check ERROR: " + Explain(ex)); }
        finally { _cts.Dispose(); _cts = null; Busy(false); RunChecks(); }
    }

    /// <summary>Spiega in chiaro gli errori piu' comuni (invece di chiudersi senza dire niente, come TB).</summary>
    private static string Explain(Exception ex)
    {
        var e = ex is AggregateException ag ? ag.Flatten().InnerExceptions.First() : ex;
        return e switch
        {
            IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => "The disk is full. Free some space and run again.",
            IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 => "A file is open in another program (Photoshop, Terrain Builder, BMS…): close it and run again. " + io.Message,
            UnauthorizedAccessException => "Windows refused to write a file (read-only file or protected folder): " + e.Message,
            OutOfMemoryException => "Out of memory: close other programs or choose fewer threads.",
            FileNotFoundException f => "File not found: " + (f.FileName ?? f.Message),
            DirectoryNotFoundException => "Folder not found: " + e.Message,
            _ => Regex.Replace(e.Message, @"\s+", " "),
        };
    }
}
