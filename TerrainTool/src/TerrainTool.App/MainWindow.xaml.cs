using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TerrainTool.Core.Data;
using TerrainTool.Core.NewTerrain;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;
using TerrainTool.Gis;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Editor;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TerrainTool.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private TheaterProject _project = new();
    private string _projectFile = "";
    private bool _loading;
    private readonly ObservableCollection<BaseRow> _baseRows = new();
    private List<BaseMapLine> _mapLines = new();

    private static string AppData => Path.Combine(AppContext.BaseDirectory, "Data");
    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BmsTerrainTool", "last_project.txt");

    /// <summary>Theater chosen in the BMS Suite main menu: the project that builds it is opened, if there is one.</summary>
    private readonly string? _startTheater;

    public MainWindow(string? startTheater = null)
    {
        _startTheater = startTheater;
        InitializeComponent();
        _mapLines = BaseMap.Load(Path.Combine(AppData, "mappa_base.txt"));
        Map.Lines = _mapLines;
        Map.BaseClicked += b =>
        {
            var row = _baseRows.FirstOrDefault(r => r.Base == b);
            if (row != null) { BasesGrid.SelectedItem = row; BasesGrid.ScrollIntoView(row); }
        };
        Map.HoverText += t => HoverBox.Text = t;
        BasesGrid.ItemsSource = _baseRows;
        Loaded += (_, _) => Startup();
    }

    // ───────────────────────── Progetto ─────────────────────────

    private void Startup()
    {
        Log($"BMS New Terrain Tool {typeof(MainWindow).Assembly.GetName().Version} — data in {AppData}");
        // from the BMS Suite main menu: the project whose theater has that name in BMS (otherwise the last project, as always)
        if (_startTheater != null && ProjectOf(_startTheater) is { } mine) { OpenProject(mine); return; }
        try
        {
            string last = File.Exists(SettingsFile) ? TheaterProject.FindMoved(File.ReadAllText(SettingsFile).Trim()) : "";
            if (last.Length > 0 && File.Exists(last)) { OpenProject(last); return; }
        }
        catch (Exception ex) { Log("Cannot read the last project: " + ex.Message); }

        // Primo avvio: progetto Mideast predefinito accanto al tool
        string folder = Path.Combine(DefaultProjectsRoot(), "Mideast");
        CreateProject(folder, "Mideast");
    }

    /// <summary>The project (in the Projects folder) that builds the theater with this name in BMS, or null.</summary>
    private static string? ProjectOf(string theater)
    {
        try
        {
            string root = DefaultProjectsRoot();
            if (!Directory.Exists(root)) return null;
            foreach (var f in Directory.EnumerateFiles(root, "*.ttproj", SearchOption.AllDirectories))
            {
                try
                {
                    var p = TheaterProject.Load(f);
                    if (p.InstalledTheaterTdf is { Length: > 0 }) continue;   // work folders of installed theaters are not TerrainTool projects
                    if (string.Equals(p.Theater.DisplayName, theater, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Theater.Name, theater, StringComparison.OrdinalIgnoreCase)) return f;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string DefaultProjectsRoot()
    {
        // Se il tool gira da dentro la cartella sorgente (C:\TerrainTool\...), usa C:\TerrainTool\Progetti
        if (global::TerrainTool.Core.SuiteLayout.ProgramFolder() is { } pf) return Path.Combine(pf, "Projects");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS TerrainTool");
    }

    private void CreateProject(string folder, string name, NewProjectWindow? start = null)
    {
        var choice = start?.Choice ?? NewProjectWindow.Start.Mideast;
        bool keep = start?.UseTheaterSettings == true || choice == NewProjectWindow.Start.World;
        var p = new TheaterProject { ProjectFolder = folder };
        if (p.IsProtectedPath(folder))
        {
            MessageBox.Show(this, "This folder is inside BMS, Korea, Terrain Builder or the old theater: choose another one.",
                "Protected folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Directory.CreateDirectory(folder);
        if (choice == NewProjectWindow.Start.Theater && keep && start!.Import!.Theater is { } src)
        {
            var t = p.Theater;
            t.SizeKm = src.SizeKm; t.MapSizePixels = src.MapSizePixels;
            t.CenterLat = src.CenterLat; t.CenterLon = src.CenterLon;
            t.MinHeightFt = src.MinHeightFt; t.MaxHeightFt = src.MaxHeightFt; t.ScaleFactor = src.ScaleFactor;
        }
        if (choice == NewProjectWindow.Start.World && start!.Area is { } area)
        {
            p.Theater.SizeKm = area.SizeKm;
            p.Theater.CenterLat = Math.Round(area.CenterLat, 2);
            p.Theater.CenterLon = Math.Round(area.CenterLon, 2);
        }
        p.Theater.Name = name;
        string csv = Path.Combine(folder, "airbases.csv");
        if (!File.Exists(csv))
        {
            string mid = Path.Combine(AppData, "airbases.csv");
            switch (choice)
            {
                case NewProjectWindow.Start.Mideast when File.Exists(mid): File.Copy(mid, csv); break;
                case NewProjectWindow.Start.Theater: AirbaseCsv.Save(csv, start!.Import!.Bases); break;
                case NewProjectWindow.Start.World: AirbaseCsv.Save(csv, start!.Area!.Bases); break;
                default: AirbaseCsv.Save(csv, Array.Empty<Airbase>()); break;
            }
        }
        p.AirbasesCsv = csv;
        if (choice == NewProjectWindow.Start.World && start!.Area!.Places.Count > 0)
            PlacesCsv.Save(Path.Combine(folder, PlacesCsv.FileName), start.Area.Places);
        _project = p;
        _projectFile = Path.Combine(folder, name + ".ttproj");
        LoadBases(csv);
        // Centro iniziale dalle basi, se presenti (non quando si tengono centro e dimensione del teatro importato)
        var real = _baseRows.Where(r => r.Base.HasReal).Select(r => (r.Base.Lat, r.Base.Lon)).ToList();
        if (real.Count > 0 && !keep) (p.Theater.CenterLat, p.Theater.CenterLon) = CenterFinder.Fit(real);
        ShowProject();   // prima riempie il modulo, poi SaveProject lo rilegge
        SaveProject();
        string from = choice switch
        {
            NewProjectWindow.Start.Mideast => "the Mideast airbase list",
            NewProjectWindow.Start.Theater => $"{start!.Import!.Bases.Count} airbases of {start.SourceTheater!.Name}" + (keep ? " (same size and centre)" : ""),
            NewProjectWindow.Start.World => $"the area chosen on the world map ({start!.Area!.SizeKm} km), {start.Area.Bases.Count} airbases from OurAirports and {start.Area.Places.Count} cities and targets (places.csv)",
            _ => "an empty airbase list",
        };
        Log($"Created project {name} in {folder}, with {from}");
        if (choice == NewProjectWindow.Start.Theater && !start!.Import!.Georeferenced)
            MessageBox.Show(this, "The airbase names were imported without position (classic terrain).\nIn tab 2 enter the real Lat/Lon of each base and set Status = ok; then set the centre in tab 1.",
                "New project", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenProject(string file)
    {
        _project = TheaterProject.Load(file);
        _projectFile = file;
        if (string.IsNullOrWhiteSpace(_project.ProjectFolder)) _project.ProjectFolder = Path.GetDirectoryName(file) ?? "";
        LoadBases(_project.AirbasesCsv);
        ShowProject();
        RememberProject();
        Log($"Opened {file}");
        if (_project.Theater.Warp is { } w) Log($"Georeference: {w.Description} (center fields are informational only)");
    }

    private void SaveProject()
    {
        ReadForm();
        Directory.CreateDirectory(_project.ProjectFolder);
        _project.Save(_projectFile);
        RememberProject();
    }

    private void RememberProject()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, _projectFile);
        }
        catch { /* non essenziale */ }
    }

    private void ShowProject()
    {
        _loading = true;
        var t = _project.Theater;
        NameBox.Text = t.Name;
        SelectCombo(SizeCombo, t.SizeKm.ToString(Ci));
        SelectCombo(PixelsCombo, t.MapSizePixels.ToString(Ci));
        LatBox.Text = t.CenterLat.ToString("0.######", Ci);
        LonBox.Text = t.CenterLon.ToString("0.######", Ci);
        MinHBox.Text = t.MinHeightFt.ToString(Ci);
        MaxHBox.Text = t.MaxHeightFt.ToString(Ci);
        DisplayNameBox.Text = t.DisplayName;
        DescriptionBox.Text = t.Description;
        ProjFolderBox.Text = _project.ProjectFolder;
        KoreaBox.Text = _project.KoreaNewTerrain;
        TbBox.Text = _project.TerrainBuilderFolder;
        OldBox.Text = _project.OldTheaterFolder;
        CheckFolderBox.Text = _project.OutputNewTerrain;
        TbOutBox.Text = _project.TbOutputOrDefault;
        ProjectPathText.Text = _projectFile;
        foreach (ComboBoxItem it in PhotoYearCombo.Items)
            if ((string)it.Tag == _project.PhotorealYear.ToString(Ci)) PhotoYearCombo.SelectedItem = it;
        if (PhotoYearCombo.SelectedItem == null) PhotoYearCombo.SelectedIndex = 0;
        Title = $"BMS New Terrain Tool — {t.Name}";
        _loading = false;
        Recalculate();
        RefreshData();
    }

    private static void SelectCombo(ComboBox c, string value)
    {
        foreach (ComboBoxItem it in c.Items)
            if ((string)it.Content == value) { c.SelectedItem = it; return; }
        c.Items.Add(new ComboBoxItem { Content = value });
        c.SelectedIndex = c.Items.Count - 1;
    }

    /// <summary>Copia i valori del modulo nel progetto. Restituisce l'errore, o null se tutto valido.</summary>
    private string? ReadForm()
    {
        var t = _project.Theater;
        var errors = new List<string>();
        t.Name = NameBox.Text.Trim();
        if (t.Name.Length == 0) errors.Add("empty name");
        if (SizeCombo.SelectedItem is ComboBoxItem s) t.SizeKm = int.Parse((string)s.Content, Ci);
        if (PixelsCombo.SelectedItem is ComboBoxItem px) t.MapSizePixels = int.Parse((string)px.Content, Ci);
        double lat = BaseRow.Parse(LatBox.Text), lon = BaseRow.Parse(LonBox.Text);
        if (double.IsNaN(lat) || lat < -80 || lat > 80) errors.Add("invalid latitude"); else t.CenterLat = lat;
        if (double.IsNaN(lon) || lon < -180 || lon > 180) errors.Add("invalid longitude"); else t.CenterLon = lon;
        if (int.TryParse(MinHBox.Text.Trim(), NumberStyles.Integer, Ci, out int mn)) t.MinHeightFt = mn; else errors.Add("invalid min height");
        if (int.TryParse(MaxHBox.Text.Trim(), NumberStyles.Integer, Ci, out int mx)) t.MaxHeightFt = mx; else errors.Add("invalid max height");
        t.DisplayName = DisplayNameBox.Text.Trim();
        t.Description = DescriptionBox.Text.Trim();
        _project.KoreaNewTerrain = KoreaBox.Text.Trim();
        _project.TerrainBuilderFolder = TbBox.Text.Trim();
        _project.OldTheaterFolder = OldBox.Text.Trim();
        _project.TbOutputFolder = TbOutBox.Text.Trim();
        return errors.Count == 0 ? null : string.Join(", ", errors);
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose or create an empty folder for the new project", InitialDirectory = DefaultProjectsRoot() };
        if (dlg.ShowDialog(this) != true) return;
        string folder = dlg.FolderName;
        string name = new DirectoryInfo(folder).Name;
        if (Directory.EnumerateFiles(folder, "*.ttproj").Any())
        {
            OpenProject(Directory.EnumerateFiles(folder, "*.ttproj").First());
            return;
        }
        var p0 = new TheaterProject { ProjectFolder = folder };
        if (p0.IsProtectedPath(folder))
        {
            MessageBox.Show(this, "This folder is inside BMS, Korea, Terrain Builder or the old theater: choose another one.",
                "Protected folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? data = null;
        try { data = Directory.GetParent(Directory.GetParent(Directory.GetParent(_project.KoreaNewTerrain)!.FullName)!.FullName)?.FullName; } catch (Exception) { }
        var w = new NewProjectWindow(name, Path.Combine(AppData, "airbases.csv"), data) { Owner = this };
        if (w.ShowDialog() != true) return;
        CreateProject(folder, name, w);
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Theater project (*.ttproj)|*.ttproj", InitialDirectory = DefaultProjectsRoot() };
        if (dlg.ShowDialog(this) == true) OpenProject(dlg.FileName);
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        string? err = ReadForm();
        if (err != null) { MessageBox.Show(this, "Please fix first: " + err, "Invalid data"); return; }
        SaveProject();
        SaveBasesCsv();
        Log($"Saved {_projectFile}");
    }

    private void OnOpenProjectFolder(object sender, RoutedEventArgs e) => OpenInExplorer(_project.ProjectFolder);

    // ───────────────────────── Teatro ─────────────────────────

    private void OnParamChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        Recalculate();
    }

    private void OnPathsChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        ReadForm();
    }

    private void Recalculate()
    {
        string? err = ReadForm();
        if (err != null)
        {
            InfoBox.Text = "⚠ " + err;
            PreviewBox.Text = "";
            return;
        }
        var t = _project.Theater;
        var g = t.CreateGrid();
        var sb = new StringBuilder();
        sb.AppendLine($"Heightmap resolution    {t.MetersPerPixel:0.###} m/px  ({t.MapSizePixels}×{t.MapSizePixels})");
        sb.AppendLine($"250 m maps              {t.Size250m}×{t.Size250m}  (WaterHeightMap, LOS250)");
        sb.AppendLine($"Segments                {t.Segments}×{t.Segments}  of 16 km");
        sb.AppendLine($"ForestMap               {t.MapSizePixels / 2}×{t.MapSizePixels / 2}");
        sb.AppendLine($"False easting/northing  {t.FalseEasting.ToString("0.###", Ci)} / {t.FalseNorthing.ToString("0.###", Ci)} m");
        sb.AppendLine();
        string[] names = { "SW", "SE", "NE", "NW" };
        var corners = g.Corners();
        sb.AppendLine("Theater corners");
        for (int i = 0; i < 4; i++)
            sb.AppendLine($"  {names[i]}  {corners[i].Lat.ToString("0.0000", Ci)}°N  {corners[i].Lon.ToString("0.0000", Ci)}°E");

        var real = _baseRows.Where(r => r.Base.HasReal).ToList();
        if (real.Count > 0)
        {
            var pts = real.Select(r => (r.Base.Lat, r.Base.Lon)).ToList();
            var m = CenterFinder.Margins(g, pts);
            var outside = real.Where(r => { var (x, y) = g.ToTheater(r.Base.Lat, r.Base.Lon); return !g.Contains(x, y); }).ToList();
            sb.AppendLine();
            sb.AppendLine($"Airbases with real position  {real.Count} (to verify: {_baseRows.Count - real.Count})");
            sb.AppendLine($"Minimum edge margin  W {m.W:0} km · E {m.E:0} km · S {m.S:0} km · N {m.N:0} km");
            if (outside.Count > 0)
                sb.AppendLine($"⚠ OUTSIDE the theater: {string.Join(", ", outside.Select(o => o.Name))}");
            else
                sb.AppendLine("✓ All real airbases are inside the theater");
            if (Math.Min(Math.Min(m.W, m.E), Math.Min(m.S, m.N)) < 50)
                sb.AppendLine("⚠ Some airbases are less than 50 km from the edge");
            var servers = _baseRows.Where(r => r.Base.Tier == Tiers.Server).ToList();
            if (servers.Count == 0) sb.AppendLine("ℹ No Server airbase set (needed only for PvP missions)");
            else if (servers.Count > 1) sb.AppendLine($"⚠ More than one Server airbase: {string.Join(", ", servers.Select(r => r.Name))}");
            else sb.AppendLine($"Server airbase (PvP)  {servers[0].Name} ({servers[0].Base.Country})");
        }
        InfoBox.Text = sb.ToString();
        PreviewBox.Text = t.ToTheaterTxt();
        Title = $"BMS New Terrain Tool — {t.Name}";
        UpdateMap();
    }

    private void OnPickArea(object sender, RoutedEventArgs e)
    {
        ReadForm();
        var t = _project.Theater;
        if (t.Warp != null)
        {
            MessageBox.Show(this, "This project follows the georeferencing of the old theater (its campaign and objects stay in place): its area cannot be moved.",
                "World map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new AreaPickerWindow(t.CenterLat, t.CenterLon, t.SizeKm) { Owner = this };
        if (w.ShowDialog() != true) return;
        _loading = true;
        SelectCombo(SizeCombo, w.SizeKm.ToString(Ci));
        LatBox.Text = Math.Round(w.CenterLat, 2).ToString("0.##", Ci);
        LonBox.Text = Math.Round(w.CenterLon, 2).ToString("0.##", Ci);
        _loading = false;
        Recalculate();
        Log($"Theater area from the world map: {w.SizeKm} km, centre {w.CenterLat.ToString("0.##", Ci)}°N {w.CenterLon.ToString("0.##", Ci)}°E");
        if (w.Places.Count > 0)
        {
            string pf = Path.Combine(_project.ProjectFolder, PlacesCsv.FileName);
            int now = PlacesCsv.Load(pf).Count;
            if (now == 0 || MessageBox.Show(this, $"Replace the {now} cities and targets of {PlacesCsv.FileName} with the {w.Places.Count} found now?", "Cities and targets",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                PlacesCsv.Save(pf, w.Places);
                Log($"{w.Places.Count} cities and targets saved in {pf}");
            }
        }
        if (!w.HaveAirportData) return;
        string q = $"Replace the airbase list of tab 2 ({_baseRows.Count} bases now) with the {w.Bases.Count} airbases found in OurAirports?\n\n" +
                   "No = keep the list as it is.";
        if (MessageBox.Show(this, q, "Airbases", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _baseRows.Clear();
        foreach (var b in w.Bases) _baseRows.Add(new BaseRow(b));
        SaveBasesCsv();
        UpdateBasesSummary();
        Recalculate();
        Log($"Airbase list replaced: {w.Bases.Count} airbases from OurAirports (saved in {_project.AirbasesCsv})");
    }

    private void OnFitCenter(object sender, RoutedEventArgs e)
    {
        var real = _baseRows.Where(r => r.Base.HasReal).Select(r => (r.Base.Lat, r.Base.Lon)).ToList();
        if (real.Count == 0) { MessageBox.Show(this, "No airbase with real coordinates.", "Center"); return; }
        var c = CenterFinder.Fit(real);
        _loading = true;
        LatBox.Text = c.Lat.ToString("0.##", Ci);
        LonBox.Text = c.Lon.ToString("0.##", Ci);
        _loading = false;
        Recalculate();
        Log($"Center fitted to {real.Count} airbases: {c.Lat.ToString(Ci)}°N {c.Lon.ToString(Ci)}°E");
    }

    private void OnWriteTheaterTxt(object sender, RoutedEventArgs e)
    {
        string? err = ReadForm();
        if (err != null) { MessageBox.Show(this, "Please fix first: " + err, "Invalid data"); return; }
        try
        {
            string path = _project.WriteTheaterTxt();
            SaveProject();
            Log($"Written {path}");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Error"); }
    }

    private void OnPhotoYearChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PhotoYearCombo.SelectedItem is not ComboBoxItem it || !int.TryParse((string)it.Tag, NumberStyles.Integer, Ci, out int y)) return;
        if (y == _project.PhotorealYear) return;
        _project.PhotorealYear = y;
        SaveProject();
        Log($"Photoreal imagery: EOX Sentinel-2 cloudless {y}" + (y == 2016 ? " (CC BY 4.0)" : " (CC BY-NC-SA 4.0: non-commercial, share-alike; credit it in the theater)")
            + ". 'Sentinel-2 photoreal 4K' makes all tiles again with it; run the Kuwait/test step first to compare.");
    }

    private void OnStandardProjection(object sender, RoutedEventArgs e)
    {
        SaveProject();
        if (!GeorefMigration.CanMigrate(_project))
        {
            MessageBox.Show(this, _project.Theater.SourceGeoref != null
                    ? "This project already uses the standard projection. The old add-on's georeference is kept: its objectives and campaign units are carried over by Run Objects and Apply object changes."
                    : "This project already uses the standard projection.", "Standard projection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string msg = "BMS computes lat/long (DED, DTC, Tacview) with a standard transverse Mercator on the theater centre. This project follows "
                   + "the old theater's own georeference, so lat/long in BMS can be off by kilometres away from the centre.\n\n"
                   + "Switching to the standard projection:\n"
                   + "  • the project files with positions in theater feet (objective_edits, coastal_moves, bridges, real_places, water_paint, "
                   + "network_edits, the Campaign Editor's saves) are converted, with a backup copy;\n"
                   + "  • the old add-on's objectives and campaign units are carried over by Run Objects and Apply object changes.\n\n"
                   + "Afterwards the terrain has to be rebuilt: all GIS steps, Terrain Builder and the textures, then Run Objects and Apply object changes.\n\nContinue?";
        if (MessageBox.Show(this, msg, "Standard projection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var n in GeorefMigration.Migrate(_project, _projectFile)) Log(n);
            ShowProject();
            string path = _project.WriteTheaterTxt();
            SaveProject();
            Log($"Written {path}");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Error"); }
    }

    private void OnShowKorea(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(_project.KoreaNewTerrain, "Theater.txt");
        if (!File.Exists(path)) { Log("Korea Theater.txt not found in " + path); return; }
        var k = TheaterConfig.ParseTheaterTxt(File.ReadAllText(path));
        var computed = new TheaterConfig { SizeKm = k.SizeKm, MapSizePixels = k.MapSizePixels, CenterLat = k.CenterLat, CenterLon = k.CenterLon, ScaleFactor = k.ScaleFactor };
        Log("— Korea Theater.txt (read-only) —");
        foreach (var l in File.ReadAllLines(path)) Log("  " + l);
        Log($"  Recomputed by this tool: {computed.ProjectionString}");
        Log("  (Korea writes y_0 with 6 significant digits; a difference of a few meters is just rounding)");
    }

    private void OnBrowseKorea(object sender, RoutedEventArgs e) => BrowseInto(KoreaBox);
    private void OnBrowseTb(object sender, RoutedEventArgs e) => BrowseInto(TbBox);
    private void OnBrowseOld(object sender, RoutedEventArgs e) => BrowseInto(OldBox);

    private void BrowseInto(TextBox box)
    {
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    // ───────────────────────── Basi e mappa ─────────────────────────

    private void LoadBases(string csv)
    {
        _baseRows.Clear();
        if (string.IsNullOrWhiteSpace(csv) || !File.Exists(csv)) { Log("Airbase CSV not found: " + csv); UpdateBasesSummary(); return; }
        var list = AirbaseCsv.Load(csv);
        // CSV della prima versione senza livello: lo prende dal file di partenza, per nome
        if (list.Count > 0 && list.All(b => string.IsNullOrEmpty(b.Tier)))
        {
            string src = Path.Combine(AppData, "airbases.csv");
            var tiers = File.Exists(src) ? AirbaseCsv.Load(src).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First().Tier) : new();
            foreach (var b in list) b.Tier = tiers.TryGetValue(b.Name, out var t) ? t : "Secondary";
            Log("Tier added to the airbase list (save to keep it)");
        }
        foreach (var b in list) _baseRows.Add(new BaseRow(b));
        Log($"Loaded {_baseRows.Count} airbases from {csv}");
        UpdateBasesSummary();
    }

    private void SaveBasesCsv()
    {
        if (string.IsNullOrWhiteSpace(_project.AirbasesCsv)) _project.AirbasesCsv = Path.Combine(_project.ProjectFolder, "airbases.csv");
        if (_project.IsProtectedPath(_project.AirbasesCsv)) { Log("CSV is in a protected folder: not saved"); return; }
        AirbaseCsv.Save(_project.AirbasesCsv, _baseRows.Select(r => r.Base));
    }

    private void UpdateBasesSummary()
    {
        int ok = _baseRows.Count(r => r.Base.HasReal);
        string tiers = string.Join(" · ", Tiers.All.Select(t => $"{t} {_baseRows.Count(r => r.Base.Tier == t)}"));
        BasesSummary.Text = $"{_baseRows.Count} airbases · {ok} with real position · {_baseRows.Count - ok} to verify · {tiers}. " +
                            "The theater center is fitted only to airbases with a real position.";
    }

    private void UpdateMap()
    {
        if (_project.Theater.SizeKm <= 0) return;
        Map.Grid = _project.Theater.CreateGrid();
        Map.Bases = _baseRows.Select(r => r.Base).ToList();
        Map.ShowOld = ShowOldCheck.IsChecked == true;
        Map.Selected = (BasesGrid.SelectedItem as BaseRow)?.Base;
        Map.InvalidateVisual();
    }

    private void OnLoadBases(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", InitialDirectory = _project.ProjectFolder };
        if (dlg.ShowDialog(this) != true) return;
        _project.AirbasesCsv = dlg.FileName;
        LoadBases(dlg.FileName);
        Recalculate();
    }

    private void OnSaveBases(object sender, RoutedEventArgs e)
    {
        SaveBasesCsv();
        SaveProject();
        Log($"Airbases saved to {_project.AirbasesCsv}");
    }

    private void OnFilterBases(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_baseRows);
        bool onlyTodo = OnlyTodoCheck.IsChecked == true;
        string tier = (TierFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        if (!onlyTodo && tier == "All") { view.Filter = null; return; }
        view.Filter = o => o is BaseRow r && (!onlyTodo || !r.Base.HasReal) && (tier == "All" || r.Base.Tier == tier);
    }

    private void OnBaseSelected(object sender, SelectionChangedEventArgs e) => UpdateMap();

    private void OnBaseEdited(object sender, DataGridCellEditEndingEventArgs e)
    {
        // aggiorna dopo che il valore è stato scritto nella riga
        Dispatcher.BeginInvoke(new Action(() => { UpdateBasesSummary(); Recalculate(); }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnShowOld(object sender, RoutedEventArgs e) => UpdateMap();
    private void OnResetMap(object sender, RoutedEventArgs e) => Map.ResetView();

    // ───────────────────────── Dati ─────────────────────────

    private readonly Dictionary<string, bool> _includeChoice = new();
    private CancellationTokenSource? _downloadCts;

    private void RefreshData()
    {
        // ricorda le spunte dell'utente tra un aggiornamento e l'altro
        if (DataGridSources.ItemsSource is IEnumerable<SourceRow> old)
            foreach (var o in old) _includeChoice[o.Name] = o.Include;

        var rows = DataSources.All.Select(s =>
        {
            var (n, b) = DataSources.Scan(_project.ProjectFolder, s);
            string planned = "manual", present = n.ToString(Ci);
            if (s.Plan != null)
            {
                var plan = s.Plan(_project.Theater);
                int have = plan.Count(i => File.Exists(Path.Combine(_project.ProjectFolder, i.RelPath)) ||
                                           File.Exists(Path.Combine(_project.ProjectFolder, i.RelPath) + ".notavailable"));
                planned = plan.Count.ToString(Ci);
                present = have.ToString(Ci);
            }
            bool inc = _includeChoice.TryGetValue(s.Name, out var choice) ? choice : s.SelectedByDefault;
            return new SourceRow { Source = s, Files = n, Size = n > 0 ? NewTerrainChecker.FormatSize(b) : "", Planned = planned, Present = present, Include = inc && s.IsAutomatic };
        }).ToList();
        DataGridSources.ItemsSource = rows;

        var c = _project.Theater.CreateGrid().Corners();
        AreaText.Text = string.Create(Ci,
            $"Area to cover: lat {c.Min(p => p.Lat):0.00}…{c.Max(p => p.Lat):0.00}°N, lon {c.Min(p => p.Lon):0.00}…{c.Max(p => p.Lon):0.00}°E (plus some margin)");
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null) return;
        ReadForm();
        DataGridSources.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = (DataGridSources.ItemsSource as IEnumerable<SourceRow>)?.Where(r => r.Include && r.IsAutomatic).ToList() ?? new();
        if (rows.Count == 0) { Log("Nothing selected to download"); return; }
        if (_project.IsProtectedPath(_project.ProjectFolder)) { Log("Project folder is protected: download blocked"); return; }

        var items = rows.SelectMany(r => r.Source.Plan!(_project.Theater)).ToList();
        Log($"Download started: {items.Count} files from {rows.Count} datasets → {Path.Combine(_project.ProjectFolder, "Sources")}");
        _downloadCts = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        CancelDownloadButton.IsEnabled = true;
        DownloadProgressBar.Value = 0;
        var started = DateTime.Now;
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal;
            double secs = Math.Max(1, (DateTime.Now - started).TotalSeconds);
            DownloadStatus.Text = $"{p.FilesDone}/{p.FilesTotal} files · {Downloader.FormatBytes(p.BytesReceived)} this session · {Downloader.FormatBytes((long)(p.BytesReceived / secs))}/s · {p.Current}";
            if (p.Line != null && !p.Line.StartsWith("Already present") && !p.Line.StartsWith("Not available")) Log(p.Line);
        });
        try
        {
            using var d = new Downloader();
            var res = await d.RunAsync(_project.ProjectFolder, items, progress, _downloadCts.Token);
            int ok = res.Count(r => r.State == DownloadState.Done), skip = res.Count(r => r.State == DownloadState.Skipped),
                na = res.Count(r => r.State == DownloadState.NotAvailable), fail = res.Count(r => r.State == DownloadState.Failed),
                canc = res.Count(r => r.State == DownloadState.Cancelled);
            Log($"Download finished: {ok} downloaded, {skip} already present, {na} not available (sea), {fail} failed, {canc} cancelled");
            DownloadStatus.Text = canc > 0 ? $"Download cancelled — {ok + skip} files present, {canc} not done (press Download to resume)"
                                : fail > 0 ? $"Download finished with {fail} errors — press Download again to retry them"
                                : $"✓ Download complete — {ok + skip} files present, {na} sea tiles not available (normal)";
            foreach (var f in res.Where(r => r.State == DownloadState.Failed)) Log("  " + Downloader.Describe(f));
            if (fail > 0) Log("Press Download again to retry the failed files.");
        }
        catch (Exception ex) { Log("Download error: " + ex.Message); }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            DownloadProgressBar.Value = 0;
            DownloadButton.IsEnabled = true;
            CancelDownloadButton.IsEnabled = false;
            RefreshData();
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        Log("Cancelling download… (partial files are kept and will resume next time)");
    }

    private void OnRefreshData(object sender, RoutedEventArgs e) => RefreshData();

    private void OnCreateSourceFolders(object sender, RoutedEventArgs e)
    {
        foreach (var s in DataSources.All) Directory.CreateDirectory(Path.Combine(_project.ProjectFolder, s.Folder));
        Log($"Created Sources folders in {_project.ProjectFolder}");
        RefreshData();
    }

    private void OnOpenSourceUrl(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SourceRow r) OpenUrl(r.Source.Url);
    }

    private void OnOpenSourceFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SourceRow r) return;
        string dir = Path.Combine(_project.ProjectFolder, r.Source.Folder);
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir);
    }

    // ───────────────────────── Controllo NewTerrain ─────────────────────────

    private void OnBrowseCheckFolder(object sender, RoutedEventArgs e) => BrowseInto(CheckFolderBox);
    private void OnCheckUseOutput(object sender, RoutedEventArgs e) => CheckFolderBox.Text = _project.OutputNewTerrain;
    private void OnCheckUseKorea(object sender, RoutedEventArgs e) => CheckFolderBox.Text = _project.KoreaNewTerrain;

    private void OnRunCheck(object sender, RoutedEventArgs e)
    {
        ReadForm();
        string folder = CheckFolderBox.Text.Trim();
        if (!Directory.Exists(folder)) { CheckSummary.Text = "Folder does not exist: " + folder; CheckGrid.ItemsSource = null; return; }

        // Se si controlla la Korea stessa, usa i parametri della Korea
        var cfg = _project.Theater;
        string theaterTxt = Path.Combine(folder, "Theater.txt");
        bool isKorea = string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(_project.KoreaNewTerrain).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (isKorea && File.Exists(theaterTxt)) cfg = TheaterConfig.ParseTheaterTxt(File.ReadAllText(theaterTxt));

        string? korea = Directory.Exists(_project.KoreaNewTerrain) ? _project.KoreaNewTerrain : null;
        var res = NewTerrainChecker.Check(folder, cfg, korea);
        CheckGrid.ItemsSource = res.Select(r => new CheckRow { Result = r }).ToList();
        int ok = res.Count(r => r.Status is CheckStatus.Ok or CheckStatus.Present);
        int miss = res.Count(r => r.Status == CheckStatus.Missing);
        int wrong = res.Count(r => r.Status == CheckStatus.WrongSize);
        CheckSummary.Text = $"{folder}{(isKorea ? "  (Korea: parameters read from its Theater.txt)" : "")}\n" +
                            $"{ok} present/OK · {wrong} wrong size · {miss} missing · {res.Count} items checked. Read-only: no file was modified.";
        Log($"NewTerrain check: {folder} → OK {ok}, wrong {wrong}, missing {miss}");
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || !IsLoaded) return;
        if (Tabs.SelectedIndex == 1) UpdateMap();
        if (Tabs.SelectedIndex == 2) RefreshData();
        if (Tabs.SelectedIndex == 3) RefreshSteps();
        if (Tabs.SelectedIndex == 4) RefreshTex();
        if (Tabs.SelectedIndex == 6) { RefreshMap(); RefreshSim(); RefreshContent(); }
        if (Tabs.SelectedIndex == 7) RefreshBuild();
        if (Tabs.SelectedIndex == 8) RefreshInstall();
    }

    // ───────────────────────── Elaborazione (GDAL) ─────────────────────────

    private readonly List<StepRow> _stepRows = ProcessingStep.All.Select(st => new StepRow { Step = st }).ToList();
    private CancellationTokenSource? _procCts;

    private int ProcSize()
    {
        string tag = (ProcSizeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1024";
        return tag == "full" ? _project.Theater.MapSizePixels : int.Parse(tag, Ci);
    }

    private void RefreshSteps()
    {
        if (StepsGrid.ItemsSource == null) StepsGrid.ItemsSource = _stepRows;
        try
        {
            ReadForm();
            var ctx = new GisContext(_project, ProcSize());
            foreach (var r in _stepRows)
            {
                string? miss = r.Step.MissingInputs(ctx);
                r.Inputs = miss == null ? "ready" : "missing: " + miss;
            }
        }
        catch (Exception ex)
        {
            foreach (var r in _stepRows) r.Inputs = "?";
            Log("GDAL not available: " + ex.Message);
        }
    }

    private async void OnRunSteps(object sender, RoutedEventArgs e)
    {
        if (_procCts != null) return;
        if (_downloadCts != null) { Log("Wait for the download to finish (or cancel it) before processing."); return; }
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var todo = _stepRows.Where(r => r.Run).ToList();
        if (todo.Count == 0) { Log("No step selected"); return; }
        ReadForm();
        SaveProject();
        int size = ProcSize();

        // I dati scaricati coprono il teatro attuale? (se si cambia il centro dopo il download, no)
        var gaps = new List<string>();
        foreach (var src in DataSources.All.Where(d => d.IsAutomatic && d.SelectedByDefault))
        {
            var plan = src.Plan!(_project.Theater);
            int miss = plan.Count(i =>
            {
                string f = Path.Combine(_project.ProjectFolder, i.RelPath);
                return !File.Exists(f) && !File.Exists(f + ".notavailable");
            });
            if (miss > 0) gaps.Add($"{src.Name}: {miss} of {plan.Count} files missing");
        }
        if (gaps.Count > 0)
        {
            foreach (var g in gaps) Log("⚠ " + g);
            var answer = MessageBox.Show(this,
                "The downloaded source data does not fully cover the current theater:\n\n" + string.Join("\n", gaps) +
                "\n\nThis happens if the theater center changed after the download. Download the missing files first (tab 3)?\n\n" +
                "Yes = stop and download   No = process anyway (missing areas stay empty)",
                "Missing source data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) { Tabs.SelectedIndex = 2; return; }
        }

        _procCts = new CancellationTokenSource();
        RunStepsButton.IsEnabled = false;
        CancelStepsButton.IsEnabled = true;
        var log = new Progress<string>(Log);
        var started = DateTime.Now;
        string procSummary = "Processing stopped";
        try
        {
            GisContext ctx = await Task.Run(() => new GisContext(_project, size));
            ctx.Log = log;
            ctx.Token = _procCts.Token;
            Log($"Processing at {size}×{size} with {GisContext.GdalVersion} → {ctx.OutDir}");
            for (int i = 0; i < todo.Count; i++)
            {
                var row = todo[i];
                int idx = i;
                string? miss = row.Step.MissingInputs(ctx);
                if (miss != null) { row.Result = "skipped: missing " + miss; Log($"{row.Name}: skipped, missing {miss}"); continue; }
                Log($"{row.Name}: started");
                row.Result = "running…";
                var t0 = DateTime.Now;
                var prog = new Progress<StepProgress>(sp =>
                {
                    ProcProgressBar.Value = (idx + Math.Clamp(sp.Fraction, 0, 1)) / todo.Count;
                    ProcStatus.Text = $"[{idx + 1}/{todo.Count}] {row.Name}: {sp.Text} {sp.Fraction * 100:0}%";
                });
                try
                {
                    var res = await Task.Run(() => row.Step.Run(ctx, prog), _procCts.Token);
                    var dt = DateTime.Now - t0;
                    row.Result = $"OK in {dt:hh\\:mm\\:ss}";
                    row.PreviewPath = res.Preview;
                    row.Notes.Clear();
                    row.Notes.AddRange(res.Notes);
                    foreach (var n in res.Notes) Log("  " + n);
                    foreach (var o in res.Outputs) Log($"  → {o} ({Downloader.FormatBytes(new FileInfo(o).Length)})");
                    StepsGrid.SelectedItem = row;
                    ShowPreview(row);
                }
                catch (OperationCanceledException) { row.Result = "cancelled"; Log($"{row.Name}: cancelled"); break; }
                catch (Exception ex)
                {
                    if (_procCts.IsCancellationRequested) { row.Result = "cancelled"; Log($"{row.Name}: cancelled"); break; }
                    row.Result = "ERROR: " + ex.Message;
                    Log($"{row.Name}: ERROR {ex.Message}");
                    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"), $"{DateTime.Now:s} {row.Name}\n{ex}\n\n"); } catch { }
                }
            }
            Log($"Processing finished in {DateTime.Now - started:hh\\:mm\\:ss}");
            int okSteps = todo.Count(r => r.Result.StartsWith("OK")), errSteps = todo.Count(r => r.Result.StartsWith("ERROR"));
            procSummary = errSteps > 0 ? $"Processing finished: {okSteps} OK, {errSteps} with errors (see log)"
                                       : $"✓ Processing complete — {okSteps} steps OK in {DateTime.Now - started:hh\\:mm\\:ss}";
        }
        catch (Exception ex) { Log("Processing error: " + ex.Message); }
        finally
        {
            _procCts.Dispose();
            _procCts = null;
            RunStepsButton.IsEnabled = true;
            CancelStepsButton.IsEnabled = false;
            ProcStatus.Text = procSummary;
            ProcProgressBar.Value = 0;
        }
    }

    private void OnCancelSteps(object sender, RoutedEventArgs e)
    {
        _procCts?.Cancel();
        Log("Cancelling processing…");
    }

    private void OnOpenProcOutput(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(_project.ProjectFolder, "Output");
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir);
    }

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StepsGrid.SelectedItem is StepRow r) ShowPreview(r);
    }

    private void ShowPreview(StepRow r)
    {
        PreviewTitle.Text = "Preview — " + r.Name;
        PreviewInfo.Text = string.Join("\n", r.Notes);
        PreviewImage.Source = null;
        string? path = r.PreviewPath;
        if (path == null)
        {
            // anteprima di una corsa precedente, se c'è
            string name = r.Step.Id switch
            {
                "heightmap" => "Heightmap", "watermap" => "Watermap", "coast" => "Coastline", "depth" => "OceanDepth",
                "trees" => "TreesCover", "landcover" => "LandCover", _ => "Roads",
            };
            int size = ProcSize();
            string suffix = size == _project.Theater.MapSizePixels ? "" : "_" + size.ToString(Ci);
            path = Path.Combine(_project.ProjectFolder, "Output", "TB_Input" + suffix, "Previews", name + ".png");
        }
        if (!File.Exists(path)) { PreviewInfo.Text = (PreviewInfo.Text + "\n(no preview yet)").Trim(); return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // non tiene il file bloccato
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage.Source = bmp;
        }
        catch (Exception ex) { PreviewInfo.Text += "\n(preview error: " + ex.Message + ")"; }
    }

    // ───────────────────────── Texture DDS ─────────────────────────

    // scheda 5: solo i file del terreno (la costruzione del teatro e' nella scheda 8, i punti di prova sono un pulsante a parte)
    private readonly List<TexRow> _texRows = TextureJob.All.Where(j => j is not ObjectsJob && !IsArtJob(j) && j is not AssembleJob && j is not ProbeJob).Select(j => new TexRow { Job = j }).ToList();
    private CancellationTokenSource? _texCts;

    private void RefreshTex()
    {
        if (TexGrid.ItemsSource == null) TexGrid.ItemsSource = _texRows;
        try
        {
            ReadForm();
            var ctx = new TextureContext(_project, TbOutBox.Text.Trim());
            foreach (var r in _texRows)
            {
                var parts = r.Job.Inputs(ctx).Select(i =>
                    $"{(i.Path != null ? "✓" : i.Required ? "✗" : "–")} {i.Role}{(i.Path != null && i.Size > 0 ? $" ({i.Size}²)" : "")}");
                r.Inputs = string.Join("\n", parts);
            }
            if (!Directory.Exists(ctx.TbOutput)) Log($"TB output folder does not exist yet: {ctx.TbOutput} (set it as the output folder in Terrain Builder)");
        }
        catch (Exception ex) { Log("Textures: " + ex.Message); }
    }

    private void OnRefreshTex(object sender, RoutedEventArgs e) => RefreshTex();

    private void OnBrowseTbOut(object sender, RoutedEventArgs e)
    {
        BrowseInto(TbOutBox);
        RefreshTex();
    }

    private void OnOpenNewTerrainOut(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_project.OutputNewTerrain);
        OpenInExplorer(_project.OutputNewTerrain);
    }

    // ───────────────────────── altri programmi ─────────────────────────

    /// <summary>
    /// Object Editor e Campaign Editor sono programmi a parte (cartelle ObjectEditor e CampaignEditor accanto a TerrainTool):
    /// si aprono sul progetto (--project &lt;file.ttproj&gt;).
    /// </summary>
    private void LaunchTool(string name, string title)
    {
        try
        {
            ReadForm();
            SaveProject();
            string? exe = global::TerrainTool.Core.SuiteLayout.ExeOf(name);
            if (exe == null)
            {
                MessageBox.Show(this, $"The {title} was not found. It is a separate program: build it with build.bat in the {name} folder (next to the TerrainTool folder), then try again.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_projectFile);
            System.Diagnostics.Process.Start(psi);
            Log($"{title} started: {exe}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnOpenObjectEditor(object sender, RoutedEventArgs e) => LaunchTool("ObjectiveStudio", "Objective Studio");
    private void OnOpenCampaignEditor(object sender, RoutedEventArgs e) => LaunchTool("CampaignEditor", "Campaign Editor");

    private async void OnInstallBms(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        ReadForm();
        SaveProject();
        await InstallAsync(false, true, InstallBmsButton);
    }

    private async void OnInstallWater(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        await InstallAsync(false, false, (Button)sender, water: true);
    }

    private async Task InstallAsync(bool objects, bool photo, Button button, bool water = false, bool art = false, bool sim = false)
    {
        ReadForm();
        SaveProject();
        var inst = new BmsInstaller(_project, objects, photo, water, art, sim);
        if (inst.Problems.Count > 0)
        {
            foreach (var pr in inst.Problems) Log("Install into BMS: " + pr);
            MessageBox.Show(this, string.Join("\n", inst.Problems), "Install into BMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, inst.Plan() + "\n\nProceed?", "Install into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        button.IsEnabled = false;
        Log("Install into BMS: started → " + inst.Target);
        try
        {
            await Task.Run(() => inst.Run(l => Dispatcher.Invoke(() => Log(l))));
        }
        catch (Exception ex) { Log("Install into BMS: ERROR " + ex.Message); }
        finally { button.IsEnabled = true; }
    }

    private async void OnRunTex(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        TexGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ReadForm();
        SaveProject();
        TextureContext ctx;
        try { ctx = new TextureContext(_project, TbOutBox.Text.Trim()); }
        catch (Exception ex) { Log("Textures: " + ex.Message); return; }
        var todo = _texRows.Where(r => r.Run).ToList();
        if (todo.Count == 0) { Log("No texture job selected"); return; }
        Log(_project.UseToolWaterDem
            ? $"Water maps (Heightmap, WaterHeightmap, Depthmap, GlobalAlpha_Blue, Culling, FFT) from this tool's Water DEM: {_project.WaterDemOutput}"
            : $"Water maps from Terrain Builder's output: {ctx.TbOutput} (Water DEM… → 'Use these water maps' to use the tool's)");

        _texCts = new CancellationTokenSource();
        RunTexButton.IsEnabled = false;
        CancelTexButton.IsEnabled = true;
        int ok = 0, err = 0;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var row = todo[i];
                int idx = i;
                string? miss = row.Job.Missing(ctx);
                if (miss != null) { row.Result = "skipped: missing input"; Log($"{row.Name}: skipped, missing {miss}"); continue; }
                row.Result = "running…";
                Log($"{row.Name}: started");
                var t0 = DateTime.Now;
                var prog = new Progress<double>(f =>
                {
                    TexProgressBar.Value = (idx + Math.Clamp(f, 0, 1)) / todo.Count;
                    TexStatus.Text = $"[{idx + 1}/{todo.Count}] {row.Name} {f * 100:0}%";
                });
                try
                {
                    var (prev, notes) = await Task.Run(() => row.Job.Run(ctx, prog, _texCts.Token), _texCts.Token);
                    row.Preview = prev;
                    row.Notes.Clear();
                    row.Notes.AddRange(notes);
                    row.Result = $"OK in {DateTime.Now - t0:mm\\:ss}";
                    foreach (var n in notes) Log("  " + n);
                    if (row.Job.Output.EndsWith(".dds")) Log($"  → {Path.Combine(ctx.NewTerrain, row.Job.Output)}");
                    TexGrid.SelectedItem = row;
                    ShowTexPreview(row);
                    ok++;
                }
                catch (OperationCanceledException) { row.Result = "cancelled"; Log($"{row.Name}: cancelled"); break; }
                catch (Exception ex) { row.Result = "ERROR: " + ex.Message; Log($"{row.Name}: ERROR {ex.Message}"); err++; }
            }
        }
        finally
        {
            _texCts.Dispose();
            _texCts = null;
            RunTexButton.IsEnabled = true;
            CancelTexButton.IsEnabled = false;
            TexProgressBar.Value = 0;
            TexStatus.Text = err > 0 ? $"Finished: {ok} OK, {err} with errors (see log)" : $"✓ Textures done — {ok} jobs OK";
            RefreshTex();
        }
    }

    private void OnCancelTex(object sender, RoutedEventArgs e) => _texCts?.Cancel();

    /// <summary>Analisi delle uscite Water DEM di Terrain Builder (Core.NewTerrain.TbAnalysis) in Output\TB_Analysis.</summary>
    private void OnTileEditor(object sender, RoutedEventArgs e)
    {
        ReadForm();
        SaveProject();
        try { new PhotoTileEditorWindow(_project) { Owner = this }.Show(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Photoreal tile editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnTerrainPreview(object sender, RoutedEventArgs e)
    {
        ReadForm();
        SaveProject();
        try { new TerrainPreviewWindow(_project) { Owner = this }.Show(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Terrain preview", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnWaterDem(object sender, RoutedEventArgs e)
    {
        ReadForm();
        SaveProject();
        string tbOut = TbOutBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tbOut)) tbOut = _project.TbOutputOrDefault;
        var w = new WaterDemWindow(_project, tbOut, SaveProject) { Owner = this };
        w.Closed += (_, _) => RefreshTex();
        w.Show();
    }

    private async void OnAnalyseTb(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        ReadForm();
        SaveProject();
        string tbIn = Path.Combine(_project.ProjectFolder, "Output", "TB_Input"), tbOut = TbOutBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tbOut)) tbOut = _project.TbOutputOrDefault;
        string outDir = Path.Combine(_project.ProjectFolder, "Output", "TB_Analysis");
        var (maps, missing) = TerrainTool.Core.NewTerrain.TbAnalysis.Find(tbIn, tbOut);
        if (missing.Count > 0) Log("TB analysis: missing " + string.Join(", ", missing));
        if (!maps.Any(m => m.Key == "DEM") || !maps.Any(m => m.Key == "Watermap") || !maps.Any(m => m.Key.StartsWith("TB_")))
        {
            MessageBox.Show(this, $"Needs the DEM and Watermap.raw in {tbIn} and the Terrain Builder outputs in {tbOut}.", "Analyse Terrain Builder output");
            return;
        }
        int threads = Math.Max(1, Environment.ProcessorCount - 1);
        Log($"TB analysis: {maps.Count} maps, {threads} threads (of {Environment.ProcessorCount} cores) → {outDir}");
        _texCts = new CancellationTokenSource();
        TbAnalyseButton.IsEnabled = false; CancelTexButton.IsEnabled = true;
        var t0 = DateTime.Now; string last = "";
        var prog = new Progress<(double F, string Msg)>(x =>
        {
            TexProgressBar.Value = Math.Clamp(x.F, 0, 1);
            TexStatus.Text = $"{x.Msg} ({x.F * 100:0}%)";
            if (x.Msg != last && x.F >= 0.75) { last = x.Msg; Log("  " + x.Msg); }
        });
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var notes = await Task.Run(() => TerrainTool.Core.NewTerrain.TbAnalysis.Run(tbIn, tbOut, outDir, threads, prog, _texCts.Token), _texCts.Token);
            foreach (var n in notes) Log("  " + n);
            Log($"TB analysis done in {DateTime.Now - t0:mm\\:ss}");
        }
        catch (OperationCanceledException) { Log("TB analysis: cancelled"); }
        catch (Exception ex) { Log("TB analysis: ERROR " + ex.Message); }
        finally
        {
            _texCts.Dispose(); _texCts = null;
            TbAnalyseButton.IsEnabled = true; CancelTexButton.IsEnabled = false; TexProgressBar.Value = 0;
        }
    }

    private void OnTexSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TexGrid.SelectedItem is TexRow r) ShowTexPreview(r);
    }

    private void ShowTexPreview(TexRow r)
    {
        TexPreviewTitle.Text = "Preview — " + r.Name;
        TexPreviewInfo.Text = string.Join("\n", r.Notes);
        TexPreviewImage.Source = null;
        if (r.Preview != null)
        {
            int s = DdsComposer.PreviewSize;
            var bmp = BitmapSource.Create(s, s, 96, 96, PixelFormats.Rgb24, null, r.Preview, s * 3);
            bmp.Freeze();
            TexPreviewImage.Source = bmp;
            return;
        }
        // anteprima non in memoria (tool riaperto): la si legge dalla DDS già scritta
        if (!r.Job.Output.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return;
        string path = Path.Combine(_project.OutputNewTerrain, r.Job.Output);
        if (!File.Exists(path)) { TexPreviewInfo.Text = (TexPreviewInfo.Text + "\n(not created yet)").Trim(); return; }
        _ = LoadDdsPreviewAsync(r, path);
    }

    private async Task LoadDdsPreviewAsync(TexRow r, string path)
    {
        try
        {
            TexPreviewInfo.Text = (TexPreviewInfo.Text + "\n(reading " + Path.GetFileName(path) + "…)").Trim();
            var (bmp, w) = await Task.Run(() =>
            {
                int size;
                using (var fs = File.OpenRead(path)) { var h = new byte[128]; fs.ReadExactly(h); size = BitConverter.ToInt32(h, 16); }
                int step = Math.Max(1, size / DdsComposer.PreviewSize);
                var (sz, rgb) = DdsComposer.DecodeDxt1(path, step);
                int ow = sz / step;
                var b = BitmapSource.Create(ow, ow, 96, 96, PixelFormats.Rgb24, null, rgb, ow * 3);
                b.Freeze();
                return (b, sz);
            });
            if (TexGrid.SelectedItem != r) return; // nel frattempo è stata scelta un'altra riga
            TexPreviewImage.Source = bmp;
            TexPreviewInfo.Text = string.Join("\n", r.Notes.Append($"{Path.GetFileName(path)} on disk: {w}²")).Trim();
        }
        catch (Exception ex) { TexPreviewInfo.Text += "\n(preview error: " + ex.Message + ")"; }
    }

    // ───────────────────────── Seleziona / deseleziona tutto ─────────────────────────

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(sender, true);
    private void OnDeselectAll(object sender, RoutedEventArgs e) => SetAll(sender, false);

    private void SetAll(object sender, bool on)
    {
        string tag = (sender as FrameworkElement)?.Tag as string ?? "";
        DataGrid grid = tag switch { "sources" => DataGridSources, "steps" => StepsGrid, _ => TexGrid };
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (grid.ItemsSource == null) return;
        foreach (var item in grid.ItemsSource)
        {
            switch (item)
            {
                case SourceRow s when s.IsAutomatic: s.Include = on; break; // le fonti solo manuali non si scaricano
                case StepRow st: st.Run = on; break;
                case TexRow t: t.Run = on; break;
            }
        }
        grid.Items.Refresh();
    }

    // ───────────────────────── Utilità ─────────────────────────

    private void Log(string msg)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
        LogBox.ScrollToEnd();
        // copia anche su file, nella cartella del progetto (tool.log)
        try
        {
            if (!string.IsNullOrWhiteSpace(_project.ProjectFolder) && Directory.Exists(_project.ProjectFolder))
                File.AppendAllText(Path.Combine(_project.ProjectFolder, "tool.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
        }
        catch { /* il log su file non è essenziale */ }
    }

    private void OpenInExplorer(string dir)
    {
        if (!Directory.Exists(dir)) { Log("Folder does not exist: " + dir); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
