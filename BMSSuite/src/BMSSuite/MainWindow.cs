using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TerrainTool.Core.Campaign;

namespace BMSSuite;

/// <summary>
/// Menu principale della BMS Suite (grafite, un solo accento ambra per l'azione principale e la selezione, verde per lo
/// stato): a sinistra i teatri di BMS (quello scelto e' il teatro di lavoro) con la sua scheda; a destra "riprendi", il
/// percorso per costruire un teatro (01 terreno → 02 obiettivi → 03 grafica → 04 campagna), modelli e dati; in fondo
/// backup, cartelle e guida. Ogni programma resta separato: qui si apre (con --theater e --bms-data), niente viene
/// scritto in BMS.
/// </summary>
public sealed class MainWindow : Window
{
    // ───────────────────────── colori: grafite, ambra (luci di cabina), verde (display)
    private static readonly Color Bg = Color.FromRgb(0x17, 0x19, 0x1C);
    private static readonly Color Panel = Color.FromRgb(0x20, 0x23, 0x27);
    private static readonly Color PanelHi = Color.FromRgb(0x29, 0x2D, 0x32);
    private static readonly Color Line = Color.FromRgb(0x3A, 0x3F, 0x45);
    private static readonly Color Text = Color.FromRgb(0xE6, 0xE4, 0xDE);
    private static readonly Color Sub = Color.FromRgb(0xA4, 0xA8, 0xAC);
    private static readonly Color SubHi = Color.FromRgb(0xB4, 0xB8, 0xBC);
    private static readonly Color Edge = Color.FromRgb(0x5A, 0x60, 0x68);
    private static readonly Color AmberC = Color.FromRgb(0xF2, 0xA9, 0x00);
    private static readonly Color Hud = Color.FromRgb(0x6E, 0xE7, 0x8E);
    private static readonly Color Red = Color.FromRgb(0xE5, 0x5A, 0x4E);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Mono = new("Consolas");
    /// <summary>Icona di un programma (Icons\&lt;nome&gt;.png nelle risorse, le stesse degli exe), o null.</summary>
    private static FrameworkElement? ToolIcon(string exe, double size)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Icons/{System.IO.Path.GetFileNameWithoutExtension(exe)}.png"));
            var img = new Image { Source = bmp, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        catch { return null; }
    }

    private static SolidColorBrush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }

    /// <summary>Un programma della suite: gruppo, passo del percorso (0 = nessuno), cartella di sviluppo, exe, aspetto.</summary>
    public sealed record Tool(string Group, int Step, string Title, string Line, string Folder, string Project, string Exe, string Glyph, string Description);

    public static readonly Tool[] Tools =
    {
        new("BUILD", 1, "TerrainTool", "Terrain, textures, install", "TerrainTool", "TerrainTool.App", "TerrainTool.exe", "\uE909",
            "Builds a theater: projection, height map, ground textures and photo, objects, install into BMS."),
        new("BUILD", 2, "Objective Studio", "Airbases, layouts, bridges", "ObjectiveStudio", "ObjectiveStudio", "ObjectiveStudio.exe", "\uE7C1",
            "What is inside the objectives: airbases and their layouts, pieces, parking, runways, bridges, replacing bases, 3D preview by day and night."),
        new("BUILD", 3, "Art Editor", "Flags, banners, maps, patches", "ArtEditor", "ArtEditor", "ArtEditor.exe", "\uE790",
            "The theater's user interface art: team flags, main menu background, banner, campaign pictures, small maps, squadron patches, medals, Theater.irc texts."),
        new("BUILD", 4, "Campaign Editor", "Campaigns and TE missions", "CampaignEditor", "CampaignEditor", "CampaignEditor.exe", "\uE707",
            "Campaigns (.cam) and Tactical Engagements (.tac) of any installed theater: units, squadrons, brigades, objectives, teams."),
        new("DATA", 0, "Model Editor", "3D models and the database", "ModelEditor", "ModelEditor", "ModelEditor.exe", "\uE7B8",
            "3D models with PBR materials (DirectX view), DOFs, switches, slots and weapons; the theater database: aircraft, vehicles, weapons, hardpoints, with check, install and restore."),
        new("DATA", 0, "Database Interchange", "From one theater to another", "DatabaseInterchange", "DatabaseInterchange", "DatabaseInterchange.exe", "\uE8AB",
            "Copies bases, ground objectives, aircraft, vehicles and weapons from one BMS 4.38 theater to another, renumbered, with plan, install and restore."),
        new("DATA", 0, "4.37 Units Converter", "Old aircraft and vehicles into 4.38", "UnitsConverter", "UnitsConverter", "UnitsConverter.exe", "\uE8AB",
            "Converts aircraft, helicopters, vehicles and weapons of a BMS 4.37 database (installation, add-on or objects folder) into a BMS 4.38 theater: models, hardpoints, weapons, squadrons, TE list and names, with backup and restore."),
        new("DATA", 0, "Sim Editor", "Theater Sim, SAM, database changes", "SimEditor", "SimEditor", "SimEditor.exe", "\uE9D9",
            "Any add-on theater (or a TerrainTool project): its own Sim files (missiles, radars, vehicles, AI-only aircraft), the database changes (DbEdits.txt) and the check of the air defence systems in its campaigns; install with backup and restore."),
    };

    private readonly ListBox _theaters = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _theaterCard = new() { Margin = new Thickness(14, 12, 14, 0), Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
    private readonly Dictionary<Tool, (Ellipse Ring, TextBlock Num)> _tileStep = new();
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _continue = new();
    private readonly StackPanel _right = new() { Margin = new Thickness(28, 22, 16, 24) };
    private readonly Dictionary<Tool, TextBlock> _tileState = new();
    private Settings _s = new();
    private string _data = "";
    private List<BmsTheater> _list = new();

    public sealed class Settings
    {
        public string? BmsData { get; set; }
        public string? Theater { get; set; }
        public string? LastTool { get; set; }
        public string? LastTheater { get; set; }
        public DateTime? LastTime { get; set; }
        /// <summary>Ultima apertura di ogni programma su ogni teatro ("programma|teatro").</summary>
        public Dictionary<string, DateTime> Used { get; set; } = new();
    }

    /// <summary>Cartella della suite (C:\BMS Suite): quella sopra la cartella BMSSuite (sviluppo) o quella con Programs (rilascio).</summary>
    public static string SuiteRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(System.IO.Path.Combine(d.FullName, "Programs"))) return d.FullName;
            if (File.Exists(System.IO.Path.Combine(d.FullName, "build.bat")) && string.Equals(d.Name, "BMSSuite", StringComparison.OrdinalIgnoreCase)) return d.Parent?.FullName ?? d.FullName;
            d = d.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string SettingsFile => System.IO.Path.Combine(SuiteRoot(), "Settings", "suite.json");

    /// <summary>L'exe di un programma: Programs\ (pacchetto di rilascio) o la cartella di build dello sviluppo.</summary>
    public static string? ExeOf(Tool t)
    {
        string root = SuiteRoot();
        foreach (var p in new[]
        {
            System.IO.Path.Combine(root, "Programs", t.Exe),
            System.IO.Path.Combine(root, "Programs", t.Folder, t.Exe),
            System.IO.Path.Combine(root, t.Folder, "src", t.Project, "bin", "Release", "net8.0-windows", t.Exe),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    public MainWindow()
    {
        Title = "BMS Suite";
        Width = 1280; Height = 840; MinWidth = 1000; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Bg);
        Foreground = B(Text);
        FontFamily = new FontFamily("Segoe UI");
        LoadSettings();

        var root = new DockPanel();
        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var left = BuildLeft();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _right });
        Content = root;

        _theaters.SelectionChanged += (_, _) =>
        {
            if (_theaters.SelectedItem is ListBoxItem { Tag: BmsTheater t })
            {
                _s.Theater = t.Name; SaveSettings();
                FillTheaterCard(t);
                foreach (var it in _theaters.Items.OfType<ListBoxItem>()) StyleTheaterItem(it, it.IsSelected);
                RefreshTiles();
            }
        };
        Activated += (_, _) => { RefreshTiles(); UpdateChips(); };
        Loaded += (_, _) => { LoadBms(); BuildRight(); FillContinue(); };
    }

    private string Short(string p) => _data.Length > 0 && p.StartsWith(_data, StringComparison.OrdinalIgnoreCase) ? "Data" + p[_data.Length..] : p;

    // ───────────────────────── impostazioni e BMS

    private void LoadSettings()
    {
        try { if (File.Exists(SettingsFile)) _s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { _s = new(); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* le impostazioni sono una comodita' */ }
    }

    private static string? FindBmsData()
    {
        try
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Benchmark Sims\Falcon BMS 4.38", @"SOFTWARE\Benchmark Sims\Falcon BMS 4.38" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("baseDir") is string b && Directory.Exists(System.IO.Path.Combine(b, "Data"))) return System.IO.Path.Combine(b, "Data");
            }
        }
        catch { }
        return Directory.Exists(@"C:\Falcon BMS 4.38\Data") ? @"C:\Falcon BMS 4.38\Data" : null;
    }

    private void LoadBms()
    {
        _data = _s.BmsData is { } d && Directory.Exists(System.IO.Path.Combine(d, "Terrdata")) ? d : FindBmsData() ?? "";
        _theaters.Items.Clear();
        _list = new();
        if (_data.Length > 0)
        {
            try { _list = BmsTheater.List(_data); } catch { _list = new(); }
            _s.BmsData = _data; SaveSettings();
        }
        // voci senza l'evidenziazione azzurra di Windows: la selezione la disegna StyleTheaterItem (barra ambra)
        var tpl = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        foreach (var t in _list)
        {
            var it = new ListBoxItem { Tag = t, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, FocusVisualStyle = null, Template = tpl };
            StyleTheaterItem(it, false);
            _theaters.Items.Add(it);
        }
        int sel = _list.FindIndex(t => t.Name == _s.Theater);
        _theaters.SelectedIndex = sel >= 0 ? sel : _list.Count > 0 ? 0 : -1;
        UpdateChips();
    }

    private static bool AddOn(BmsTheater t) => t.CampaignDir.Contains("Add-On", StringComparison.OrdinalIgnoreCase) || t.ObjectDir.Contains("Add-On", StringComparison.OrdinalIgnoreCase);

    private void StyleTheaterItem(ListBoxItem it, bool selected)
    {
        if (it.Tag is not BmsTheater t) return;
        var row = new DockPanel();
        var bar = new Rectangle { Width = 4, Fill = selected ? B(AmberC) : Brushes.Transparent };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        var tag = new TextBlock { Text = AddOn(t) ? "ADD-ON" : "BMS", FontFamily = Mono, FontSize = 10.5, Foreground = B(Sub), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(tag, Dock.Right);
        row.Children.Add(tag);
        row.Children.Add(new TextBlock { Text = t.Name, FontSize = 14.5, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = B(Text), Margin = new Thickness(12, 9, 8, 9), TextTrimming = TextTrimming.CharacterEllipsis });
        it.Content = new Border { Background = selected ? B(PanelHi) : Brushes.Transparent, Child = row };
        it.Background = Brushes.Transparent;
    }

    private void UpdateChips()
    {
        _chips.Children.Clear();
        bool bms = _data.Length > 0;
        _chips.Children.Add(Chip(bms ? "BMS 4.38 found" : "BMS not found: choose its Data folder", bms ? Hud : Red, !bms, bms ? _data + " (click to change)" : "Click to choose the Falcon BMS Data folder", ChooseData));
        bool running = false;
        try { running = Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Falcon BMS", StringComparison.OrdinalIgnoreCase)); } catch { }
        _chips.Children.Add(Chip(running ? "BMS is running: close it before installing" : "BMS closed: safe to install", running ? AmberC : Hud, running, "The programs install into BMS only while it is closed", null));
        if (bms) _chips.Children.Add(Chip($"{_list.Count} theaters · {_list.Count(AddOn)} add-ons", null, false, "theater.lst of the Data folder", null));
    }

    /// <summary>Stato in alto: un punto colorato e una scritta (non un pulsante); in evidenza solo quando c'e' da fare qualcosa.</summary>
    private static FrameworkElement Chip(string text, Color? dot, bool alert, string tip, Action? click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), ToolTip = tip, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        if (dot is { } c) sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(c), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var tb = new TextBlock { Text = text, FontFamily = Mono, FontSize = 12, Foreground = B(alert && dot is { } a ? a : SubHi), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(tb);
        if (click != null)
        {
            sp.Cursor = Cursors.Hand;
            sp.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
            sp.MouseLeave += (_, _) => tb.TextDecorations = null;
            sp.MouseLeftButtonUp += (_, _) => click();
        }
        return sp;
    }

    private void ChooseData()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Falcon BMS Data folder (the one with Terrdata, Campaign, Art…)" };
        if (dlg.ShowDialog(this) != true) return;
        if (!Directory.Exists(System.IO.Path.Combine(dlg.FolderName, "Terrdata"))) { MessageBox.Show(this, "That folder has no Terrdata: choose the Data folder of Falcon BMS 4.38.", Title); return; }
        _s.BmsData = dlg.FolderName; SaveSettings();
        LoadBms();
    }

    // ───────────────────────── barra in alto e colonna dei teatri

    private FrameworkElement BuildTopBar()
    {
        var logo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // marchio: un rombo ambra con la sagoma di un'ala, poi il nome spaziato
        var mark = ToolIcon("BMSSuite.exe", 40) ?? new TextBlock { Text = "\uE709", FontFamily = Icons, FontSize = 28, Foreground = B(AmberC) };
        mark.Margin = new Thickness(0, 0, 12, 0);
        logo.Children.Add(mark);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "B M S   S U I T E", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = B(Text) });
        name.Children.Add(new TextBlock { Text = "EDITING TOOLS FOR FALCON BMS 4.38", FontFamily = Mono, FontSize = 10.5, Foreground = B(Sub) });
        logo.Children.Add(name);
        var dp = new DockPanel { Margin = new Thickness(22, 12, 22, 12) };
        var support = SupportButton();
        DockPanel.SetDock(support, Dock.Right);
        dp.Children.Add(support);
        DockPanel.SetDock(_chips, Dock.Right);
        dp.Children.Add(_chips);
        dp.Children.Add(logo);
        var strip = new Border { Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(0, 0, 0, 1), Child = dp };
        return strip;
    }

    /// <summary>Pagina delle donazioni dell'autore.</summary>
    public const string SupportUrl = "https://ko-fi.com/danguard63";

    /// <summary>"Support on Ko-fi": apre la pagina delle donazioni nel browser (pulsante con il solo bordo).</summary>
    private static FrameworkElement SupportButton()
    {
        var tb = new TextBlock { Text = "Support on Ko-fi", FontSize = 12.5, Foreground = B(SubHi), VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(22, 0, 4, 0), Child = tb, Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(4), BorderBrush = B(Edge), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The BMS Suite is free. If it helps you, you can buy the author a coffee (" + SupportUrl + ")",
        };
        b.MouseEnter += (_, _) => { b.BorderBrush = B(AmberC); tb.Foreground = B(Text); };
        b.MouseLeave += (_, _) => { b.BorderBrush = B(Edge); tb.Foreground = B(SubHi); };
        b.MouseLeftButtonUp += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); } catch { }
        };
        return b;
    }

    private FrameworkElement BuildLeft()
    {
        var sp = new DockPanel();
        var head = Label("WORKING THEATER", new Thickness(16, 20, 14, 10));
        DockPanel.SetDock(head, Dock.Top);
        sp.Children.Add(head);
        var hint = new TextBlock { Text = "Every program opens on the working theater.", Foreground = B(Sub), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 14, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        sp.Children.Add(hint);
        _theaterCard.Background = B(Bg);
        _theaterCard.BorderBrush = B(Line);
        var list = new StackPanel();
        list.Children.Add(_theaters);
        list.Children.Add(_theaterCard);
        // la rotella scorre la colonna, non l'elenco (che non ha barra sua)
        _theaters.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            list.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_theaters, ScrollBarVisibility.Disabled);
        sp.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        return new Border { Width = 300, Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 0, 14), Child = sp };
    }

    /// <summary>Scheda del teatro scelto: cosa ha di suo e dove sono i suoi file (solo letti).</summary>
    private void FillTheaterCard(BmsTheater t)
    {
        static int Count(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Count() : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
        bool ownDb = true, ownSim = false;
        string sim = "";
        try { ownDb = !TerrainTool.Core.Database.DbInstaller.IsShared(_data, t.ObjectDir); } catch (Exception ex) when (ex is IOException or ArgumentException) { }
        try { ownSim = TerrainTool.Core.Sim.TheaterSim.HasOwnSim(t); sim = TerrainTool.Core.Sim.TheaterSim.SimDir(t); } catch (Exception ex) when (ex is IOException or ArgumentException) { }

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = t.Subtitle, Foreground = B(SubHi), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var stats = new UniformGrid2(2);
        FrameworkElement Stat(string value, string label, string tip) => new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 10), ToolTip = tip,
            Children = { new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = B(Text) }, new TextBlock { Text = label, FontSize = 11, Foreground = B(Sub) } },
        };
        stats.Add(Stat(Count(t.CampaignDir, "*.cam").ToString(), "campaign files", "The .cam files of its Campaign folder (campaigns and their saves)"));
        stats.Add(Stat(Count(t.CampaignDir, "*.tac").ToString(), "TE missions", "The .tac files of its Campaign folder"));
        stats.Add(Stat(ownDb ? "Its own" : "BMS's", "database", ownDb ? "The theater has its own objects database: the programs can install into it" : "The theater uses BMS's shared database, which is never written"));
        stats.Add(Stat(ownSim ? "Its own" : "BMS's", "Sim files", ownSim ? "The theater has its own Sim folder" : "The theater uses BMS's Data\\Sim; it gets its own the first time a program installs Sim files"));
        sp.Children.Add(stats.Panel);
        sp.Children.Add(new Border { Height = 1, Background = B(Line), Margin = new Thickness(0, 0, 0, 8) });
        void PathLine(string label, string path)
        {
            if (path.Length == 0) return;
            sp.Children.Add(new TextBlock { Text = label.PadRight(10) + Short(path), FontFamily = Mono, FontSize = 10.5, Foreground = B(Sub), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path, Margin = new Thickness(0, 0, 0, 3) });
        }
        PathLine("Campaign", t.CampaignDir);
        PathLine("Objects", t.ObjectDir);
        PathLine("Sim", sim);
        _theaterCard.Child = sp;
    }

    private static TextBlock Label(string text, Thickness m) =>
        new() { Text = text, FontFamily = Mono, FontSize = 12, Foreground = B(Sub), Margin = m };

    /// <summary>"Riprendi": l'ultimo programma aperto, con l'unico pulsante pieno della pagina.</summary>
    private void FillContinue()
    {
        var last = Tools.FirstOrDefault(t => t.Title == _s.LastTool);
        var row = new DockPanel();
        if (last != null)
        {
            var ic = ToolIcon(last.Exe, 36) ?? new TextBlock { Text = last.Glyph, FontFamily = Icons, FontSize = 24, Foreground = B(AmberC) };
            ic.VerticalAlignment = VerticalAlignment.Center; ic.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(ic, Dock.Left);
            row.Children.Add(ic);
            var go = new Border
            {
                Background = B(AmberC), CornerRadius = new CornerRadius(4), Padding = new Thickness(22, 9, 22, 10), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Continue", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = B(Bg) },
            };
            go.MouseEnter += (_, _) => go.Background = B(Color.FromRgb(0xFF, 0xC2, 0x33));
            go.MouseLeave += (_, _) => go.Background = B(AmberC);
            go.MouseLeftButtonUp += ContinueClick;
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(go);
            var title = new TextBlock { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(new System.Windows.Documents.Run(last.Title) { FontWeight = FontWeights.SemiBold });
            string where = (_s.LastTheater is { Length: > 0 } th ? "  ·  " + th : "") + (_s.LastTime is { } w ? "  ·  " + When(w) : "");
            title.Inlines.Add(new System.Windows.Documents.Run(where) { Foreground = B(SubHi) });
            row.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children = { new TextBlock { Text = "Continue where you left off", Foreground = B(Sub), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) }, title },
            });
        }
        else row.Children.Add(new TextBlock { Text = "Choose the working theater on the left, then a program below.", Foreground = B(SubHi), FontSize = 13.5 });
        _continue.Child = row;
        _continue.Padding = new Thickness(16, 12, 16, 12);
        _continue.CornerRadius = new CornerRadius(5);
        _continue.BorderBrush = B(Line);
        _continue.BorderThickness = new Thickness(1);
        _continue.Background = B(Panel);
    }

    private static string When(DateTime w) =>
        w.Date == DateTime.Today ? "today " + w.ToString("HH:mm") : w.Date == DateTime.Today.AddDays(-1) ? "yesterday " + w.ToString("HH:mm")
        : w.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);

    private void ContinueClick(object sender, MouseButtonEventArgs e)
    {
        var last = Tools.FirstOrDefault(t => t.Title == _s.LastTool);
        if (last != null) Launch(last, _s.LastTheater);
    }

    // ───────────────────────── colonna di destra: percorso, dati, sicurezza

    private void BuildRight()
    {
        _right.Children.Clear();
        _tileState.Clear();
        _tileStep.Clear();
        if (_continue.Parent is Panel old) old.Children.Remove(_continue);
        _right.Children.Add(_continue);

        _right.Children.Add(Section("BUILD A THEATER", "Each step uses what the one before made"));
        var build = new UniformGrid2(4);
        foreach (var t in Tools.Where(t => t.Step > 0).OrderBy(t => t.Step)) build.Add(Tile(t));
        _right.Children.Add(build.Panel);

        _right.Children.Add(Section("MODELS & DATA", "Any theater, any time"));
        var data = new UniformGrid2(4);
        foreach (var t in Tools.Where(t => t.Group == "DATA")) data.Add(Tile(t));
        _right.Children.Add(data.Panel);
        RefreshTiles();
    }

    private static FrameworkElement Section(string title, string sub)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 10) };
        row.Children.Add(new TextBlock { Text = title, FontFamily = Mono, FontSize = 12, Foreground = B(Sub), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = "    " + sub, FontSize = 12.5, Foreground = B(Sub), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>
    /// Scheda di un programma: icona, (per i passi del percorso) il numero nel cerchio, nome, a cosa serve e in fondo lo
    /// stato sul teatro di lavoro. Il bordo si accende d'ambra al passaggio; clic = apre il programma.
    /// </summary>
    private FrameworkElement Tile(Tool t)
    {
        var state = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _tileState[t] = state;
        var inner = new DockPanel { Margin = new Thickness(16, 14, 16, 13), LastChildFill = true };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (t.Step > 0)
        {
            var num = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top };
            var ring = new Ellipse { StrokeThickness = 2 };
            var n = new TextBlock { Text = t.Step.ToString("00"), FontFamily = Mono, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            num.Children.Add(ring); num.Children.Add(n);
            _tileStep[t] = (ring, n);
            DockPanel.SetDock(num, Dock.Right);
            head.Children.Add(num);
        }
        var ic = ToolIcon(t.Exe, 44) ?? new TextBlock { Text = t.Glyph, FontFamily = Icons, FontSize = 28, Foreground = B(AmberC), HorizontalAlignment = HorizontalAlignment.Left };
        head.Children.Add(ic);
        DockPanel.SetDock(head, Dock.Top);
        inner.Children.Add(head);
        DockPanel.SetDock(state, Dock.Bottom);
        inner.Children.Add(state);
        inner.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = t.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = t.Line, Foreground = B(SubHi), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
            },
        });
        var b = new Border
        {
            Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 12, 0), MinHeight = t.Step > 0 ? 176 : 150,
            Cursor = Cursors.Hand, Child = inner,
            ToolTip = new ToolTip { Content = new TextBlock { Text = t.Description, MaxWidth = 340, TextWrapping = TextWrapping.Wrap } },
        };
        b.MouseEnter += (_, _) => { b.BorderBrush = B(AmberC); b.Background = B(PanelHi); };
        b.MouseLeave += (_, _) => { b.BorderBrush = B(Line); b.Background = B(Panel); };
        b.MouseLeftButtonUp += (_, _) => Launch(t, CurrentTheater);
        return b;
    }

    /// <summary>Barra in fondo: i backup, le cartelle e la guida (collegamenti, non pulsanti).</summary>
    private FrameworkElement BuildFooter()
    {
        var dp = new DockPanel { Margin = new Thickness(28, 10, 28, 11) };
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement Link(string text, string tip, Action act)
        {
            var tb = new TextBlock { Text = text, FontSize = 12.5, Foreground = B(SubHi), Margin = new Thickness(22, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent };
            tb.MouseEnter += (_, _) => { tb.Foreground = B(Text); tb.TextDecorations = TextDecorations.Underline; };
            tb.MouseLeave += (_, _) => { tb.Foreground = B(SubHi); tb.TextDecorations = null; };
            tb.MouseLeftButtonUp += (_, _) => act();
            return tb;
        }
        links.Children.Add(Link("Backups", "Every backup made by the programs, newest first", () => new BackupsWindow(SuiteRoot()) { Owner = this }.ShowDialog()));
        links.Children.Add(Link("Suite folder", "Programs, settings and logs of the BMS Suite", () => OpenFolder(SuiteRoot())));
        links.Children.Add(Link("BMS Data folder", "Falcon BMS 4.38\\Data (read it, do not change it by hand)", () => OpenFolder(_data)));
        if (GuideFile() is { } guide)
            links.Children.Add(Link("User guide", guide, () => { try { Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true }); } catch { } }));
        DockPanel.SetDock(links, Dock.Right);
        dp.Children.Add(links);
        dp.Children.Add(new TextBlock { Text = "Every program saves what it replaces before writing into BMS.", FontSize = 12.5, Foreground = B(Sub), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Background = B(Color.FromRgb(0x1B, 0x1D, 0x21)), BorderBrush = B(Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = dp };
    }

    private static string? GuideFile()
    {
        foreach (var p in new[] { System.IO.Path.Combine(SuiteRoot(), "BMS-Suite-User-Guide.pdf"), System.IO.Path.Combine(SuiteRoot(), "docs", "BMS-Suite-User-Guide.pdf") })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Griglia a colonne uguali che va a capo (le schede larghe di modelli e dati).</summary>
    private sealed class UniformGrid2
    {
        private readonly int _cols;
        public readonly Grid Panel = new();
        private int _n;
        public UniformGrid2(int cols) { _cols = cols; for (int i = 0; i < cols; i++) Panel.ColumnDefinitions.Add(new ColumnDefinition()); }
        public void Add(FrameworkElement e)
        {
            if (_n % _cols == 0) Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, _n / _cols); Grid.SetColumn(e, _n % _cols);
            Panel.Children.Add(e); _n++;
        }
    }

    private string UsedKey(Tool t, string? theater) => t.Title + "|" + (theater ?? "");

    /// <summary>Stato di una scheda sul teatro di lavoro: non compilato, in esecuzione, gia' aperto su questo teatro (quando) o no.</summary>
    private void UpdateState(Tool t)
    {
        if (!_tileState.TryGetValue(t, out var s)) return;
        var exe = ExeOf(t);
        bool running = false;
        try { running = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(t.Exe)).Length > 0; } catch { }
        bool used = _s.Used.TryGetValue(UsedKey(t, CurrentTheater), out var at);
        if (exe == null) { s.Text = "Not built: run its build.bat"; s.Foreground = B(Red); }
        else if (running) { s.Text = "Running now"; s.Foreground = B(Hud); }
        else if (used) { s.Text = $"Opened on {CurrentTheater ?? "this theater"} · {When(at)}"; s.Foreground = B(SubHi); }
        else { s.Text = CurrentTheater != null ? "Not opened on this theater yet" : ""; s.Foreground = B(Sub); }
        if (_tileStep.TryGetValue(t, out var step))
        {
            // pieno = aperto adesso, bordo verde = gia' usato su questo teatro, grigio = ancora da fare
            step.Ring.Fill = running ? B(Hud) : Brushes.Transparent;
            step.Ring.Stroke = B(running || used ? Hud : Edge);
            step.Num.Foreground = B(running ? Bg : used ? Hud : Sub);
        }
    }

    private void RefreshTiles() { foreach (var t in Tools) UpdateState(t); }

    private string? CurrentTheater => (_theaters.SelectedItem as ListBoxItem)?.Tag is BmsTheater t ? t.Name : null;

    // ───────────────────────── avvio dei programmi

    private void Launch(Tool t, string? theater)
    {
        var exe = ExeOf(t);
        if (exe == null)
        {
            MessageBox.Show(this, $"{t.Title} is not built yet.\n\nRun build.bat in {System.IO.Path.Combine(SuiteRoot(), t.Folder)}, then try again.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe)! };
            if (theater != null) { psi.ArgumentList.Add("--theater"); psi.ArgumentList.Add(theater); }
            if (_data.Length > 0) { psi.ArgumentList.Add("--bms-data"); psi.ArgumentList.Add(_data); }
            Process.Start(psi);
            _s.LastTool = t.Title; _s.LastTheater = theater; _s.LastTime = DateTime.Now;
            _s.Used[UsedKey(t, theater)] = DateTime.Now;
            SaveSettings();
            FillContinue();
            Dispatcher.BeginInvoke(new Action(RefreshTiles), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception e) { MessageBox.Show(this, $"{t.Title} could not start: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void OpenFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
        catch { }
    }
}
