using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerrainTool.Core.Theater;

namespace TerrainTool.App;

/// <summary>
/// Chooses the theater area on a world map: a square of 1024 or 2048 km (drawn as BMS projects it) that can be dragged,
/// and the airbases inside it taken from OurAirports. Nothing is written: the caller uses <see cref="Center"/>,
/// <see cref="SizeKm"/> and <see cref="Bases"/>.
/// </summary>
public sealed class AreaPickerWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public double CenterLat { get; private set; }
    public double CenterLon { get; private set; }
    public int SizeKm { get; private set; }
    /// <summary>Airbases inside the area (with the filter), empty when the OurAirports data is not available.</summary>
    public List<Airbase> Bases { get; private set; } = new();
    public bool HaveAirportData => _airports != null;
    /// <summary>Cities, towns and OpenStreetMap targets inside the area (for places.csv).</summary>
    public List<Place> Places { get; private set; } = new();

    private readonly WorldMap _map = new();
    private readonly ComboBox _size = new() { Width = 90 };
    private readonly TextBox _lat = new() { Width = 90 }, _lon = new() { Width = 90 };
    private readonly TextBox _minRwy = new() { Width = 70, Text = "1830" }, _margin = new() { Width = 70, Text = "30" },
        _maxMain = new() { Width = 70, Text = "40", ToolTip = "The most important airbases get the Main tier (built first): military airbases with a long runway, then large airports, then the longest runways" };
    private readonly CheckBox _mil = new() { Content = "Military airbases", IsChecked = true }, _civ = new() { Content = "Civil airports", IsChecked = true },
        _small = new() { Content = "Also small airfields (if the runway is long enough)" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _data = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ListBox _list = new() { Height = 250 };

    // places
    private readonly TextBox _cityPop = new() { Width = 70, Text = "250000" }, _townPop = new() { Width = 70, Text = "100000" }, _minMw = new() { Width = 70, Text = "300" };
    private readonly CheckBox _bordersChk = new() { Content = "Show country borders and names", IsChecked = true, Margin = new Thickness(0, 4, 0, 2) };
    private readonly CheckBox _cityChk = new() { Content = "Cities", IsChecked = true }, _townChk = new() { Content = "Towns", IsChecked = true };
    private readonly CheckBox _ports = new() { Content = "Ports", IsChecked = true }, _power = new() { Content = "Power plants (and nuclear)", IsChecked = true },
        _refin = new() { Content = "Refineries", IsChecked = true }, _army = new() { Content = "Military bases and barracks (named)" };
    private readonly Button _osmBtn = new() { Content = "Search OpenStreetMap in this area", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _pInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBlock _pData = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ListBox _pList = new() { Height = 250 };
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 8, 0, 0) };
    private WorldPlaces? _cities;
    private List<WorldPlaces.OsmThing>? _osm;
    private (double S, double W, double N, double E) _osmBox;

    private readonly TextBlock _mouse = new() { Foreground = Brushes.White, Margin = new Thickness(6, 2, 6, 2) };
    private readonly Button _ok = new() { Content = "Use this area", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly DispatcherTimer _recount = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private OurAirports? _airports;
    private List<Airbase> _curBases = new();
    private List<Place> _curPlaces = new();
    private bool _busy;

    public AreaPickerWindow(double lat, double lon, int sizeKm)
    {
        Title = "Choose the theater area on the world map";
        Width = 1320; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CenterLat = Math.Clamp(lat, -TheaterOutline.MaxCenterLat, TheaterOutline.MaxCenterLat); CenterLon = lon; SizeKm = sizeKm is 1024 or 2048 ? sizeKm : 1024;

        _size.Items.Add("1024"); _size.Items.Add("2048");
        _size.SelectedItem = SizeKm.ToString(Ci);
        _size.SelectionChanged += (_, _) => { SizeKm = int.Parse((string)_size.SelectedItem, Ci); Changed(); };
        _lat.LostFocus += (_, _) => FromBoxes(); _lon.LostFocus += (_, _) => FromBoxes();
        _lat.KeyDown += (_, e) => { if (e.Key == Key.Enter) FromBoxes(); };
        _lon.KeyDown += (_, e) => { if (e.Key == Key.Enter) FromBoxes(); };
        foreach (var t in new[] { _minRwy, _margin, _maxMain, _cityPop, _townPop, _minMw }) { t.LostFocus += (_, _) => Recount(); t.KeyDown += (_, e) => { if (e.Key == Key.Enter) Recount(); }; }
        foreach (var c in new[] { _mil, _civ, _small, _cityChk, _townChk, _ports, _power, _refin, _army }) { c.Checked += (_, _) => Recount(); c.Unchecked += (_, _) => Recount(); c.Margin = new Thickness(0, 2, 0, 2); }
        _recount.Tick += (_, _) => { _recount.Stop(); Recount(); };
        _osmBtn.Click += async (_, _) => await SearchOsmAsync();
        _bordersChk.Checked += (_, _) => { _map.ShowBorders = true; _map.InvalidateVisual(); };
        _bordersChk.Unchecked += (_, _) => { _map.ShowBorders = false; _map.InvalidateVisual(); };

        _map.CenterMoved += (la, lo) => { CenterLat = la; CenterLon = lo; Changed(true); };
        _map.MouseText += s => _mouse.Text = s;
        _map.DotClicked += i =>
        {
            if (i < _curBases.Count) { _tabs.SelectedIndex = 0; _list.SelectedIndex = i; _list.ScrollIntoView(_list.SelectedItem); }
            else if (i - _curBases.Count < _pList.Items.Count) { _tabs.SelectedIndex = 1; _pList.SelectedIndex = i - _curBases.Count; _pList.ScrollIntoView(_pList.SelectedItem); }
        };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedIndex >= 0) { _map.Highlight = _list.SelectedIndex; _map.InvalidateVisual(); } };
        _pList.SelectionChanged += (_, _) => { if (_pList.SelectedIndex >= 0) { _map.Highlight = _curBases.Count + _pList.SelectedIndex; _map.InvalidateVisual(); } };

        var right = new StackPanel { Margin = new Thickness(10) };
        right.Children.Add(Section("Theater"));
        right.Children.Add(Row("Size (km)", _size));
        right.Children.Add(Row("Centre latitude", _lat));
        right.Children.Add(Row("Centre longitude", _lon));
        right.Children.Add(Row("Distance from edge (km)", _margin));
        right.Children.Add(_bordersChk);
        right.Children.Add(Note("Drag the square to move it · drag elsewhere to pan · wheel = zoom · double-click = put the square there. " +
                                "BMS supports 1024 km (like Korea) and 2048 km (like Mideast) theaters."));

        var ab = new StackPanel { Margin = new Thickness(6) };
        ab.Children.Add(Row("Shortest runway (m)", _minRwy));
        ab.Children.Add(Row("Main airbases (most)", _maxMain));
        ab.Children.Add(_mil); ab.Children.Add(_civ); ab.Children.Add(_small);
        ab.Children.Add(_info);
        ab.Children.Add(_list);
        ab.Children.Add(_data);
        _tabs.Items.Add(new TabItem { Header = "Airbases", Content = ab });

        var pl = new StackPanel { Margin = new Thickness(6) };
        pl.Children.Add(_cityChk);
        pl.Children.Add(Row("   from (inhabitants)", _cityPop));
        pl.Children.Add(_townChk);
        pl.Children.Add(Row("   from (inhabitants)", _townPop));
        pl.Children.Add(Note("Cities and towns come from GeoNames and follow the square at once. The targets below come from OpenStreetMap: press the button after placing the square (it can take a few minutes)."));
        pl.Children.Add(_ports); pl.Children.Add(_power);
        pl.Children.Add(Row("   smallest plant (MW)", _minMw));
        pl.Children.Add(_refin); pl.Children.Add(_army);
        pl.Children.Add(_osmBtn);
        pl.Children.Add(_pInfo);
        pl.Children.Add(_pList);
        pl.Children.Add(_pData);
        _tabs.Items.Add(new TabItem { Header = "Cities & targets", Content = pl });
        right.Children.Add(_tabs);

        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        _ok.Click += (_, _) => { Recount(); Bases = _curBases; Places = _curPlaces; DialogResult = true; };
        right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { _ok, cancel } });

        var mapHost = new Grid { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(16, 30, 54)) };
        mapHost.Children.Add(_map);
        var credit = new TextBlock { Text = "Sentinel-2 cloudless – s2maps.eu by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2016) · © OpenStreetMap contributors · GeoNames", Foreground = Brushes.LightGray, FontSize = 10.5, Margin = new Thickness(6, 2, 6, 2) };
        DockPanel.SetDock(credit, Dock.Right);
        var barPanel = new DockPanel();
        barPanel.Children.Add(credit);
        barPanel.Children.Add(_mouse);
        mapHost.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Bottom, Child = barPanel });
        mapHost.Children.Add(Legend());

        var root = new DockPanel();
        var scroll = new ScrollViewer { Content = right, Width = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Right);
        root.Children.Add(scroll);
        root.Children.Add(mapHost);
        Content = root;

        _map.SetView(CenterLat, CenterLon, SizeKm >= 2048 ? 4 : 5);
        Changed();
        Loaded += async (_, _) =>
        {
            try { _map.Borders = await Task.Run(() => WorldBorders.Load()); _map.InvalidateVisual(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            await LoadAirportsAsync(false); await LoadCitiesAsync(false);
        };
    }

    private static TextBlock Section(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 8, 0, 4) };
    private static TextBlock Note(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 4) };
    private static FrameworkElement Row(string label, FrameworkElement c)
    {
        var g = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var t = new TextBlock { Text = label, Width = 160, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        g.Children.Add(t); g.Children.Add(c);
        c.HorizontalAlignment = HorizontalAlignment.Left;
        return g;
    }

    private static FrameworkElement Legend()
    {
        var sp = new StackPanel { Margin = new Thickness(8) };
        foreach (var k in new[] { "Main", "Secondary" }.Concat(PlaceKinds.All))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            bool square = k is not ("Main" or "Secondary");
            FrameworkElement mark = square
                ? new Border { Width = 9, Height = 9, Background = WorldMap.KindBrush(k), BorderBrush = Brushes.Black, BorderThickness = new Thickness(0.8) }
                : new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = WorldMap.KindBrush(k), Stroke = Brushes.Black, StrokeThickness = 0.8 };
            mark.Margin = new Thickness(0, 0, 6, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = k is "Main" or "Secondary" ? k + " airbase" : k, Foreground = Brushes.White, FontSize = 11.5 });
            sp.Children.Add(row);
        }
        return new Border { Child = sp, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(4),
                            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), IsHitTestVisible = false };
    }

    /// <summary>Cache folder of the tool (BMS Suite\TerrainTool\Cache next to build.bat, else Local AppData).</summary>
    public static string CacheRoot()
    {
        var pf = global::TerrainTool.Core.SuiteLayout.ProgramFolder();
        return pf != null ? Path.Combine(pf, "Cache") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMS TerrainTool", "Cache");
    }

    private static string AirportDir => Path.Combine(CacheRoot(), "OurAirports");
    private static string CitiesDir => Path.Combine(CacheRoot(), "GeoNames");

    private async Task LoadAirportsAsync(bool force)
    {
        if (_busy) return;
        _busy = true;
        string dir = AirportDir;
        try
        {
            if (force || !OurAirports.IsFresh(dir))
            {
                _data.Text = "Downloading the OurAirports data (about 17 MB, once every few months)…";
                try { await OurAirports.DownloadAsync(dir); }
                catch (IOException ex)
                {
                    if (!OurAirports.Files.All(f => File.Exists(Path.Combine(dir, f)))) { _data.Text = "⚠ " + ex.Message + "\nThe area can still be chosen; the airbases can be added later in tab 2."; return; }
                    _data.Text = "⚠ Download failed, using the data downloaded before. " + ex.Message;
                }
            }
            _data.Text = "Reading the OurAirports data…";
            _airports = await Task.Run(() => OurAirports.Load(dir));
            var when = File.GetLastWriteTime(Path.Combine(dir, "airports.csv"));
            _data.Inlines.Clear();
            _data.Inlines.Add($"OurAirports (public domain): {_airports.Airports.Count:N0} airports with a hard runway, data of {when:d}. ");
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Download again"));
            link.Click += async (_, _) => await LoadAirportsAsync(true);
            _data.Inlines.Add(link);
            Recount();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _data.Text = "⚠ " + ex.Message;
        }
        finally { _busy = false; }
    }

    private async Task LoadCitiesAsync(bool force)
    {
        string dir = CitiesDir;
        try
        {
            if (force || !WorldPlaces.IsFresh(dir))
            {
                _pData.Text = "Downloading the GeoNames cities (about 3 MB)…";
                try { await WorldPlaces.DownloadAsync(dir); }
                catch (IOException ex)
                {
                    if (!File.Exists(Path.Combine(dir, WorldPlaces.CitiesFile))) { _pData.Text = "⚠ " + ex.Message; return; }
                    _pData.Text = "⚠ Download failed, using the cities downloaded before. " + ex.Message;
                }
            }
            var countries = _airports?.Countries;
            _cities = await Task.Run(() => WorldPlaces.Load(dir, countries));
            var when = File.GetLastWriteTime(Path.Combine(dir, WorldPlaces.CitiesFile));
            _pData.Inlines.Clear();
            _pData.Inlines.Add($"GeoNames (CC BY 4.0): {_cities.Cities.Count:N0} places over 15,000 inhabitants, data of {when:d}. OpenStreetMap data © OpenStreetMap contributors (ODbL). ");
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Download again"));
            link.Click += async (_, _) => await LoadCitiesAsync(true);
            _pData.Inlines.Add(link);
            Recount();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _pData.Text = "⚠ " + ex.Message;
        }
    }

    private async Task SearchOsmAsync()
    {
        if (!(_ports.IsChecked == true || _power.IsChecked == true || _refin.IsChecked == true || _army.IsChecked == true))
        { MessageBox.Show(this, "Tick at least one kind of target.", Title); return; }
        var box = WorldPlaces.Bbox(Config());
        _osmBtn.IsEnabled = false;
        string old = (string)_osmBtn.Content;
        _osmBtn.Content = "Searching OpenStreetMap… (can take a few minutes)";
        try
        {
            var prog = new Progress<string>(t => _osmBtn.Content = t + " (can take a few minutes)");
            _osm = await WorldPlaces.FetchOsmAsync(box, Path.Combine(CacheRoot(), "OpenStreetMap"), prog);
            _osmBox = box;
            Recount();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, "OpenStreetMap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _osmBtn.IsEnabled = true; _osmBtn.Content = old; }
    }

    private void FromBoxes()
    {
        if (double.TryParse(_lat.Text.Replace(',', '.'), NumberStyles.Float, Ci, out double la) && double.TryParse(_lon.Text.Replace(',', '.'), NumberStyles.Float, Ci, out double lo))
        {
            CenterLat = Math.Clamp(la, -TheaterOutline.MaxCenterLat, TheaterOutline.MaxCenterLat);
            CenterLon = ((lo + 540) % 360) - 180;
            _map.SetView(CenterLat, CenterLon, null);
            Changed();
        }
        else { _lat.Text = CenterLat.ToString("0.##", Ci); _lon.Text = CenterLon.ToString("0.##", Ci); }
    }

    private TheaterConfig Config() => new() { CenterLat = Math.Round(CenterLat, 2), CenterLon = Math.Round(CenterLon, 2), SizeKm = SizeKm };

    /// <summary>The square moved or changed size.</summary>
    private void Changed(bool dragging = false)
    {
        CenterLat = Math.Clamp(CenterLat, -TheaterOutline.MaxCenterLat, TheaterOutline.MaxCenterLat);
        CenterLon = ((CenterLon + 540) % 360) - 180;
        if (!_lat.IsKeyboardFocused) _lat.Text = Math.Round(CenterLat, 2).ToString("0.##", Ci);
        if (!_lon.IsKeyboardFocused) _lon.Text = Math.Round(CenterLon, 2).ToString("0.##", Ci);
        _map.Outline = TheaterOutline.Points(Config());
        _map.Center = (CenterLat, CenterLon);
        if (dragging) { _recount.Stop(); _recount.Start(); } else Recount();
    }

    private double MarginKm => double.TryParse(_margin.Text.Replace(',', '.'), NumberStyles.Float, Ci, out var e) ? e : 30;
    private static int Int(TextBox t, int def) => int.TryParse(t.Text.Replace(".", "").Replace(",", "").Trim(), NumberStyles.Integer, Ci, out var v) ? Math.Max(0, v) : def;

    private List<Airbase> CurrentBases()
    {
        if (_airports == null) return new();
        var f = new AirportFilter
        {
            MinRunwayFt = (double.TryParse(_minRwy.Text.Replace(',', '.'), NumberStyles.Float, Ci, out var m) ? m : 1830) / 0.3048,
            EdgeMarginKm = MarginKm,
            MaxMain = Int(_maxMain, 40),
            Military = _mil.IsChecked == true, Civil = _civ.IsChecked == true, Small = _small.IsChecked == true,
        };
        return _airports.Select(Config(), f);
    }

    /// <summary>The places inside the square with the ticked kinds; each checkbox shows how many of its kind there are.</summary>
    private List<Place> CurrentPlaces(out bool osmStale)
    {
        osmStale = false;
        var all = new List<Place>();
        var c = Config();
        if (_cities != null)
        {
            var f = new PlaceFilter
            {
                CityMinPopulation = Int(_cityPop, 250000), TownMinPopulation = Int(_townPop, 100000), EdgeMarginKm = MarginKm,
                Ports = true, PowerPlants = true, Refineries = true, ArmyBases = true, PowerMinMw = Int(_minMw, 300),
            };
            all = _cities.SelectCities(c, f);
            if (_osm != null)
            {
                var b = WorldPlaces.Bbox(c);
                osmStale = b.S < _osmBox.S - 0.05 || b.W < _osmBox.W - 0.05 || b.N > _osmBox.N + 0.05 || b.E > _osmBox.E + 0.05;
                all.AddRange(_cities.SelectOsm(_osm, c, f));
            }
        }
        int N(params string[] kinds) => all.Count(p => kinds.Contains(p.Kind));
        string osmCount(params string[] kinds) => _osm == null ? "  – search OpenStreetMap first" : $"  ({N(kinds)} found)";
        _cityChk.Content = $"Cities ({N(PlaceKinds.City)})";
        _townChk.Content = $"Towns ({N(PlaceKinds.Town)})";
        _ports.Content = "Ports" + osmCount(PlaceKinds.Port);
        _power.Content = "Power plants and nuclear" + osmCount(PlaceKinds.PowerPlant, PlaceKinds.Nuclear);
        _refin.Content = "Refineries" + osmCount(PlaceKinds.Refinery);
        _army.Content = "Military bases and barracks" + osmCount(PlaceKinds.ArmyBase);
        bool On(string k) => k switch
        {
            PlaceKinds.City => _cityChk.IsChecked == true, PlaceKinds.Town => _townChk.IsChecked == true,
            PlaceKinds.Port => _ports.IsChecked == true, PlaceKinds.PowerPlant or PlaceKinds.Nuclear => _power.IsChecked == true,
            PlaceKinds.Refinery => _refin.IsChecked == true, PlaceKinds.ArmyBase => _army.IsChecked == true, _ => true,
        };
        return all.Where(p => On(p.Kind)).ToList();
    }

    private void Recount()
    {
        var c = Config();
        var warn = new List<string>();
        if (Math.Abs(c.CenterLat) > 60) warn.Add("⚠ Far from the equator: distances on the edges of the theater are stretched; check the terrain carefully.");

        _curBases = CurrentBases();
        _info.Text = _airports == null ? string.Join("\n", new[] { "Airbase data not loaded yet." }.Concat(warn))
            : string.Join("\n", new[] { $"{_curBases.Count} airbases inside ({_curBases.Count(b => b.Tier == "Main")} Main, {_curBases.Count(b => b.Tier != "Main")} Secondary)." }.Concat(warn));
        _list.Items.Clear();
        foreach (var b in _curBases) _list.Items.Add($"{(b.Tier == "Main" ? "●" : "○")} {b.Name}  ({b.Country})");

        _curPlaces = CurrentPlaces(out bool stale);
        var counts = PlaceKinds.All.Select(k => (k, n: _curPlaces.Count(p => p.Kind == k))).Where(t => t.n > 0).Select(t => $"{t.n} {t.k.ToLowerInvariant()}");
        _pInfo.Text = (_cities == null ? "City data not loaded yet." : _curPlaces.Count == 0 ? "No place inside." : string.Join(", ", counts) + ".") +
                      (_osm == null ? "\nOpenStreetMap targets: not searched yet." : stale ? "\n⚠ The square moved outside the area searched: search OpenStreetMap again." : "");
        _pList.Items.Clear();
        foreach (var p in _curPlaces) _pList.Items.Add($"{p.Kind}: {p.Name}  ({p.Country}{(p.Info.Length > 0 ? ", " + p.Info : "")})");

        _map.Dots = _curBases.Select(b => (b.Lat, b.Lon, b.Tier, b.Name))
            .Concat(_curPlaces.Select(p => (p.Lat, p.Lon, p.Kind, p.Name))).ToList();
        _map.Highlight = -1;
        _map.InvalidateVisual();
    }
}

/// <summary>Web Mercator world map from Sentinel-2 cloudless tiles (EOX), cached on disk, with the theater outline on top.</summary>
internal sealed class WorldMap : FrameworkElement
{
    private static readonly string[] Urls =
    {
        "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2016_3857/default/g/{0}/{2}/{1}.jpg",
        "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{0}/{2}/{1}.jpg",
    };
    private const int MinZoom = 2, MaxZoom = 9, Tile = 256;
    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<(int, int, int), BitmapSource?> Tiles = new();
    private static readonly ConcurrentDictionary<(int, int, int), byte> Loading = new();

    public List<(double Lat, double Lon)> Outline { get; set; } = new();
    /// <summary>Markers: airbases (kind "Main"/"Secondary", drawn as dots) then places (kind = PlaceKinds, drawn as squares).</summary>
    public List<(double Lat, double Lon, string Kind, string Name)> Dots { get; set; } = new();

    public static Brush KindBrush(string kind) => kind switch
    {
        "Main" => Brushes.OrangeRed, "Secondary" => Brushes.Yellow,
        PlaceKinds.City => Brushes.White, PlaceKinds.Town => Brushes.Silver, PlaceKinds.Port => Brushes.DeepSkyBlue,
        PlaceKinds.PowerPlant => Brushes.MediumPurple, PlaceKinds.Nuclear => Brushes.Magenta, PlaceKinds.Refinery => Brushes.SandyBrown,
        PlaceKinds.ArmyBase => Brushes.LimeGreen, _ => Brushes.Gray,
    };
    /// <summary>Index in <see cref="Dots"/> drawn with a ring and its name (selected in the list), -1 = none.</summary>
    public int Highlight { get; set; } = -1;
    private int _hover = -1;
    public event Action<int>? DotClicked;
    /// <summary>Country borders and names (Natural Earth), drawn over the satellite tiles when <see cref="ShowBorders"/>.</summary>
    public WorldBorders? Borders { get; set; }
    public bool ShowBorders { get; set; } = true;
    /// <summary>Centre of the theater (the point the square is dragged by).</summary>
    public (double Lat, double Lon) Center { get; set; }
    public event Action<double, double>? CenterMoved;
    public event Action<string>? MouseText;

    private int _z = 4;
    private double _cx, _cy;   // view centre, world pixels at zoom _z
    private Point _last;
    private bool _pan, _box;
    private Vector _boxOffset;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("BMS-TerrainTool/1.0 (Falcon BMS community theater tool)");
        return h;
    }

    public WorldMap()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    private double World => Tile * Math.Pow(2, _z);
    private Point ToWorld(double lat, double lon)
    {
        lat = Math.Clamp(lat, -85, 85);
        double x = (lon + 180) / 360 * World;
        double s = Math.Sin(lat * Math.PI / 180);
        double y = (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * World;
        return new Point(x, y);
    }
    private (double Lat, double Lon) FromWorld(Point p)
    {
        double lon = p.X / World * 360 - 180;
        double n = Math.PI - 2 * Math.PI * p.Y / World;
        return (180 / Math.PI * Math.Atan(Math.Sinh(n)), lon);
    }
    private Point ToScreen(Point w) => new(w.X - _cx + ActualWidth / 2, w.Y - _cy + ActualHeight / 2);
    private Point FromScreen(Point s) => new(s.X + _cx - ActualWidth / 2, s.Y + _cy - ActualHeight / 2);

    /// <summary>Centres the view on a point (zoom unchanged when null).</summary>
    public void SetView(double lat, double lon, int? zoom)
    {
        if (zoom is int z) _z = Math.Clamp(z, MinZoom, MaxZoom);
        var p = ToWorld(lat, lon);
        _cx = p.X; _cy = p.Y;
        InvalidateVisual();
    }

    /// <summary>Longitude moved by whole turns next to the view centre (the view can be panned past the date line).</summary>
    private double Near(double lon)
    {
        double viewLon = FromWorld(new Point(_cx, _cy)).Lon;
        while (lon - viewLon > 180) lon -= 360;
        while (lon - viewLon < -180) lon += 360;
        return lon;
    }
    private Point Screen(double lat, double lon) => ToScreen(ToWorld(lat, Near(lon)));

    private Geometry? OutlineGeometry()
    {
        if (Outline.Count < 3) return null;
        // longitudes kept continuous around the view centre (theaters crossing the date line)
        double c = Near(Center.Lon);
        var pts = Outline.Select(p =>
        {
            double lon = p.Lon;
            while (lon - c > 180) lon -= 360;
            while (lon - c < -180) lon += 360;
            return ToScreen(ToWorld(p.Lat, lon));
        }).ToList();
        var g = new StreamGeometry();
        using (var sg = g.Open()) { sg.BeginFigure(pts[0], true, true); sg.PolyLineTo(pts.Skip(1).ToList(), true, true); }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 30, 54)), null, new Rect(0, 0, w, h));
        int n = 1 << _z;
        var tl = FromScreen(new Point(0, 0));
        int tx0 = (int)Math.Floor(tl.X / Tile), ty0 = Math.Max(0, (int)Math.Floor(tl.Y / Tile));
        int tx1 = (int)Math.Floor((tl.X + w) / Tile), ty1 = Math.Min(n - 1, (int)Math.Floor((tl.Y + h) / Tile));
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int wx = ((tx % n) + n) % n;
                var rect = new Rect(ToScreen(new Point(tx * Tile, ty * Tile)), new Size(Tile + 0.5, Tile + 0.5));
                var img = Get(_z, wx, ty);
                if (img != null) { dc.DrawImage(img, rect); continue; }
                // a lower zoom tile already loaded, enlarged, while this one arrives
                for (int up = 1; up <= 4 && _z - up >= 0; up++)
                {
                    int k = 1 << up;
                    var parent = Tiles.TryGetValue((_z - up, wx / k, ty / k), out var pimg) ? pimg : null;
                    if (parent == null) continue;
                    double ps = Tile * k;
                    var prect = new Rect(ToScreen(new Point(Math.Floor((double)tx / k) * ps, Math.Floor((double)ty / k) * ps)), new Size(ps, ps));
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawImage(parent, prect);
                    dc.Pop();
                    break;
                }
            }

        // graticule every 10°
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 0.7);
        grid.Freeze();
        for (int lat = -80; lat <= 80; lat += 10)
        {
            double y = ToScreen(ToWorld(lat, 0)).Y;
            if (y >= 0 && y <= h) dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }
        double lonStep = _z <= 3 ? 30 : 10;
        var l0 = FromScreen(new Point(0, 0)).X / World * 360 - 180;
        for (double lon = Math.Floor(l0 / lonStep) * lonStep; ; lon += lonStep)
        {
            double x = ToScreen(new Point((lon + 180) / 360 * World, 0)).X;
            if (x > w) break;
            if (x >= 0) dc.DrawLine(grid, new Point(x, 0), new Point(x, h));
        }

        if (ShowBorders && Borders != null) DrawBorders(dc, w, h);

        var geo = OutlineGeometry();
        if (geo != null)
        {
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 255, 200, 0)), new Pen(Brushes.Gold, 2), geo);
            var cp = Screen(Center.Lat, Center.Lon);
            var cross = new Pen(Brushes.Gold, 1.2);
            dc.DrawLine(cross, cp + new Vector(-6, 0), cp + new Vector(6, 0));
            dc.DrawLine(cross, cp + new Vector(0, -6), cp + new Vector(0, 6));
        }
        // places first (squares), then Secondary and Main airbases on top
        var outlinePen = new Pen(Brushes.Black, 0.8);
        for (int pass = 0; pass < 3; pass++)
            for (int i = 0; i < Dots.Count; i++)
            {
                var (lat, lon, kind, _) = Dots[i];
                int want = kind == "Main" ? 2 : kind == "Secondary" ? 1 : 0;
                if (want != pass) continue;
                var p = Screen(lat, lon);
                if (pass == 0)
                {
                    double r = kind == PlaceKinds.City ? 3.5 : kind == PlaceKinds.Town ? 2.5 : 3;
                    dc.DrawRectangle(KindBrush(kind), outlinePen, new Rect(p.X - r, p.Y - r, 2 * r, 2 * r));
                }
                else dc.DrawEllipse(KindBrush(kind), outlinePen, p, pass == 2 ? 3.5 : 2.8, pass == 2 ? 3.5 : 2.8);
            }
        foreach (int i in new[] { _hover, Highlight }.Distinct())
        {
            if (i < 0 || i >= Dots.Count) continue;
            var (lat, lon, kind, name) = Dots[i];
            var p = Screen(lat, lon);
            dc.DrawEllipse(null, new Pen(i == Highlight ? Brushes.Cyan : Brushes.White, 2), p, 8, 8);
            string label = kind is "Main" or "Secondary" ? $"{name}  ({kind} airbase)" : $"{name}  ({kind})";
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var at = p + new Vector(11, -ft.Height / 2);
            if (at.X + ft.Width + 6 > ActualWidth) at = p + new Vector(-11 - ft.Width, -ft.Height / 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), null, new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4), 3, 3);
            dc.DrawText(ft, at);
        }
    }

    private void DrawBorders(DrawingContext dc, double w, double h)
    {
        // visible latitude/longitude window (longitudes as seen around the view centre)
        var (nLat, wLon) = FromWorld(FromScreen(new Point(0, 0)));
        var (sLat, eLon) = FromWorld(FromScreen(new Point(w, h)));
        double viewLon = FromWorld(new Point(_cx, _cy)).Lon;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            foreach (var (pts, s, west, n, east) in Borders!.Lines)
            {
                if (n < sLat || s > nLat) continue;
                // the same line, moved by whole turns next to the view
                double shift = 0;
                while ((west + east) / 2 + shift - viewLon > 180) shift -= 360;
                while ((west + east) / 2 + shift - viewLon < -180) shift += 360;
                if (east + shift < wLon || west + shift > eLon) continue;
                Point last = default;
                bool open = false;
                foreach (var (lat, lon) in pts)
                {
                    var p = ToScreen(ToWorld(lat, lon + shift));
                    if (!open) { c.BeginFigure(p, false, false); open = true; last = p; continue; }
                    if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 1.2) continue;   // closer than a pixel
                    c.LineTo(p, true, false);
                    last = p;
                }
            }
        }
        g.Freeze();
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3); shadow.Freeze();
        var line = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 2 }, 0) }; line.Freeze();
        dc.DrawGeometry(null, shadow, g);
        dc.DrawGeometry(null, line, g);

        if (_z < 4) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (name, lat, lon) in Borders!.Labels)
        {
            var p = Screen(lat, lon);
            if (p.X < -100 || p.X > w + 100 || p.Y < -20 || p.Y > h + 20) continue;
            double size = _z >= 6 ? 13 : 11;
            string text = name.ToUpperInvariant();
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), dpi);
            var at = new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2);
            dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, dpi), at + new Vector(1, 1));
            dc.DrawText(ft, at);
        }
    }

    /// <summary>Dot under the mouse (within 7 pixels), -1 = none.</summary>
    private int DotAt(Point p)
    {
        int best = -1; double bd = 49;
        for (int i = 0; i < Dots.Count; i++)
        {
            double d = (Screen(Dots[i].Lat, Dots[i].Lon) - p).LengthSquared;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private BitmapSource? Get(int z, int x, int y)
    {
        if (Tiles.TryGetValue((z, x, y), out var b)) return b;
        if (Loading.TryAdd((z, x, y), 0)) _ = LoadAsync(z, x, y);
        return null;
    }

    private async Task LoadAsync(int z, int x, int y)
    {
        BitmapSource? bmp = null;
        try
        {
            string file = Path.Combine(AreaPickerWindow.CacheRoot(), "WorldTiles", z.ToString(CultureInfo.InvariantCulture), $"{x}_{y}.jpg");
            byte[]? bytes = File.Exists(file) ? await File.ReadAllBytesAsync(file) : null;
            if (bytes == null)
            {
                foreach (var u in Urls)
                {
                    try { bytes = await Http.GetByteArrayAsync(string.Format(CultureInfo.InvariantCulture, u, z, x, y)); break; }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                }
                if (bytes != null)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllBytesAsync(file, bytes); } catch (IOException) { }
                }
            }
            if (bytes != null)
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(bytes); bi.EndInit();
                bi.Freeze();
                bmp = bi;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { }
        // a missing tile stays empty (no new attempts until the window is opened again)
        Tiles[(z, x, y)] = bmp;
        Loading.TryRemove((z, x, y), out _);
        if (bmp != null) await Dispatcher.InvokeAsync(InvalidateVisual);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int nz = Math.Clamp(_z + (e.Delta > 0 ? 1 : -1), MinZoom, MaxZoom);
        if (nz == _z) return;
        var m = e.GetPosition(this);
        var wm = FromScreen(m);
        double f = Math.Pow(2, nz - _z);
        _z = nz;
        // keep the point under the mouse in place
        _cx = wm.X * f - (m.X - ActualWidth / 2);
        _cy = wm.Y * f - (m.Y - ActualHeight / 2);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            var (lat, lon) = FromWorld(FromScreen(p));
            CenterMoved?.Invoke(lat, lon);
            return;
        }
        _last = p;
        int dot = DotAt(p);
        if (dot >= 0) DotClicked?.Invoke(dot);
        var geo = OutlineGeometry();
        if (geo != null && geo.FillContains(p))
        {
            _box = true;
            _boxOffset = Screen(Center.Lat, Center.Lon) - p;
        }
        else _pan = true;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var (la, lo) = FromWorld(FromScreen(p));
        MouseText?.Invoke($"{la:0.00}° {(la >= 0 ? "N" : "S")}  {((lo + 540) % 360) - 180:0.00}° {(((lo + 540) % 360) - 180 >= 0 ? "E" : "W")}   zoom {_z}");
        if (_pan)
        {
            _cx -= p.X - _last.X;
            _cy = Math.Clamp(_cy - (p.Y - _last.Y), 0, World);
            _last = p;
            InvalidateVisual();
        }
        else if (_box)
        {
            var (lat, lon) = FromWorld(FromScreen(p + _boxOffset));
            CenterMoved?.Invoke(lat, lon);
        }
        int h = _pan || _box ? -1 : DotAt(p);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
        Cursor = _box || (OutlineGeometry()?.FillContains(p) ?? false) ? Cursors.SizeAll : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover >= 0) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _pan = _box = false;
        ReleaseMouseCapture();
    }
}
