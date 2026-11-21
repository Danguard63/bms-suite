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
/// Menu principale della BMS Suite, in stile pannello di cabina (grafite, luci ambra, verde dei display): a sinistra i
/// teatri di BMS (quello scelto e' il teatro di lavoro) e "riprendi"; a destra il percorso per costruire un teatro
/// (01 terreno → 02 obiettivi → 03 grafica → 04 campagna), poi modelli e database, poi i backup. Ogni programma resta
/// separato: qui si apre (con --theater e --bms-data), niente viene scritto in BMS.
/// </summary>
public sealed class MainWindow : Window
{
    // ───────────────────────── colori: grafite, ambra (luci di cabina), verde (display)
    private static readonly Color Bg = Color.FromRgb(0x17, 0x19, 0x1C);
    private static readonly Color Panel = Color.FromRgb(0x20, 0x23, 0x27);
    private static readonly Color PanelHi = Color.FromRgb(0x29, 0x2D, 0x32);
    private static readonly Color Line = Color.FromRgb(0x3A, 0x3F, 0x45);
    private static readonly Color Text = Color.FromRgb(0xE6, 0xE4, 0xDE);
    private static readonly Color Sub = Color.FromRgb(0x98, 0x9C, 0xA0);
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
    private readonly TextBlock _theaterInfo = new() { Foreground = B(Sub), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 6, 14, 0) };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _continue = new();
    private readonly StackPanel _right = new() { Margin = new Thickness(28, 22, 28, 28) };
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
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _right });
        Content = root;

        _theaters.SelectionChanged += (_, _) =>
        {
            if (_theaters.SelectedItem is ListBoxItem { Tag: BmsTheater t })
            {
                _s.Theater = t.Name; SaveSettings();
                _theaterInfo.Text = $"{t.Subtitle}\nCampaign: {Short(t.CampaignDir)}\nObjects: {Short(t.ObjectDir)}";
                foreach (var it in _theaters.Items.OfType<ListBoxItem>()) StyleTheaterItem(it, it.IsSelected);
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
        var tag = new TextBlock { Text = AddOn(t) ? "ADD-ON" : "BMS", FontFamily = Mono, FontSize = 10.5, Foreground = B(AddOn(t) ? Sub : Hud), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(tag, Dock.Right);
        row.Children.Add(tag);
        row.Children.Add(new TextBlock { Text = t.Name, FontSize = 14.5, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = B(selected ? AmberC : Text), Margin = new Thickness(12, 8, 8, 8), TextTrimming = TextTrimming.CharacterEllipsis });
        it.Content = new Border { Background = selected ? B(PanelHi) : Brushes.Transparent, Child = row };
        it.Background = Brushes.Transparent;
    }

    private void UpdateChips()
    {
        _chips.Children.Clear();
        bool bms = _data.Length > 0;
        _chips.Children.Add(Chip(bms ? "BMS 4.38 FOUND" : "BMS NOT FOUND", bms ? Hud : Red, bms ? _data + " (click to change)" : "Click to choose the Falcon BMS Data folder", ChooseData));
        if (bms) _chips.Children.Add(Chip($"{_list.Count} THEATERS · {_list.Count(AddOn)} ADD-ONS", Sub, "theater.lst of the Data folder", null));
        bool running = false;
        try { running = Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Falcon BMS", StringComparison.OrdinalIgnoreCase)); } catch { }
        _chips.Children.Add(Chip(running ? "BMS RUNNING – CLOSE IT BEFORE INSTALLING" : "BMS CLOSED", running ? AmberC : Sub, "The programs install into BMS only while it is closed", null));
    }

    private static FrameworkElement Chip(string text, Color c, string tip, Action? click)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = B(c), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(dot);
        sp.Children.Add(new TextBlock { Text = text, FontFamily = Mono, FontSize = 11.5, Foreground = B(c), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { BorderBrush = B(c, 90), BorderThickness = new Thickness(1), Padding = new Thickness(9, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), Child = sp, ToolTip = tip };
        if (click != null) { b.Cursor = Cursors.Hand; b.MouseLeftButtonUp += (_, _) => click(); }
        return b;
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
        var strip = new Border { Background = B(Panel), BorderBrush = B(AmberC, 160), BorderThickness = new Thickness(0, 0, 0, 2), Child = dp };
        return strip;
    }

    /// <summary>Pagina delle donazioni dell'autore.</summary>
    public const string SupportUrl = "https://ko-fi.com/danguard63";

    /// <summary>"Support on Ko-fi": apre la pagina delle donazioni nel browser.</summary>
    private static FrameworkElement SupportButton(bool large = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "\u2615", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = large ? 24 : 20, Foreground = B(Bg), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "SUPPORT ON KO-FI", FontFamily = Mono, FontSize = large ? 18 : 15, FontWeight = FontWeights.Bold, Foreground = B(Bg), VerticalAlignment = VerticalAlignment.Center });
        var b = new Border
        {
            Background = B(AmberC), Padding = large ? new Thickness(24, 12, 26, 12) : new Thickness(18, 7, 20, 7), Margin = large ? new Thickness(0) : new Thickness(18, 0, 4, 0), Child = sp, Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(5), BorderBrush = B(Hud), BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "The BMS Suite is free. If it helps you, you can buy the author a coffee (" + SupportUrl + ")",
        };
        b.MouseEnter += (_, _) => b.Background = B(Hud);
        b.MouseLeave += (_, _) => b.Background = B(AmberC);
        b.MouseLeftButtonUp += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true }); } catch { }
        };
        return b;
    }

    /// <summary>Riquadro delle donazioni nello spazio principale del menu.</summary>
    private static FrameworkElement SupportCard()
    {
        var dp = new DockPanel();
        var cup = new TextBlock { Text = "\u2615", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 40, Foreground = B(AmberC), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(cup, Dock.Left);
        dp.Children.Add(cup);
        var btn = SupportButton(large: true);
        DockPanel.SetDock(btn, Dock.Right);
        dp.Children.Add(btn);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Free and open source, made in spare time", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B(Text) });
        text.Children.Add(new TextBlock
        {
            Text = "If the BMS Suite saves you time, you can buy the author a coffee on Ko-fi. Every coffee helps new tools and updates. Thank you!",
            FontSize = 13, Foreground = B(Sub), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 16, 0),
        });
        dp.Children.Add(text);
        return new Border
        {
            Background = B(PanelHi), BorderBrush = B(AmberC, 200), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(0, 0, 0, 8), Child = dp,
        };
    }

    private FrameworkElement BuildLeft()
    {
        var sp = new DockPanel();
        var head = Label("WORKING THEATER", new Thickness(14, 18, 14, 4));
        DockPanel.SetDock(head, Dock.Top);
        sp.Children.Add(head);
        var hint = new TextBlock { Text = "Every program opens on it", Foreground = B(Sub), FontSize = 11.5, Margin = new Thickness(14, 0, 14, 10) };
        DockPanel.SetDock(hint, Dock.Top);
        sp.Children.Add(hint);
        var bottom = new StackPanel();
        bottom.Children.Add(_theaterInfo);
        bottom.Children.Add(Label("CONTINUE", new Thickness(14, 22, 14, 6)));
        bottom.Children.Add(_continue);
        DockPanel.SetDock(bottom, Dock.Bottom);
        sp.Children.Add(bottom);
        sp.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _theaters });
        return new Border { Width = 290, Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 0, 16), Child = sp };
    }

    private static TextBlock Label(string text, Thickness m) =>
        new() { Text = text, FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = B(AmberC), Margin = m };

    private void FillContinue()
    {
        var last = Tools.FirstOrDefault(t => t.Title == _s.LastTool);
        var sp = new StackPanel();
        if (last != null)
        {
            var row = new DockPanel();
            var ic = ToolIcon(last.Exe, 34) ?? new TextBlock { Text = last.Glyph, FontFamily = Icons, FontSize = 22, Foreground = B(Hud) };
            ic.VerticalAlignment = VerticalAlignment.Center; ic.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(ic, Dock.Left);
            row.Children.Add(ic);
            var go = new TextBlock { Text = "▶", Foreground = B(Hud), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(go, Dock.Right);
            row.Children.Add(go);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = last.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = (_s.LastTheater ?? "") + (_s.LastTime is { } w ? " · " + w.ToString("dd/MM HH:mm") : ""), Foreground = B(Sub), FontSize = 11.5 },
                },
            });
            sp.Children.Add(row);
        }
        else sp.Children.Add(new TextBlock { Text = "Nothing opened yet", Foreground = B(Sub), FontSize = 12.5 });
        _continue.Child = sp;
        _continue.Margin = new Thickness(14, 0, 14, 0);
        _continue.Padding = new Thickness(12, 10, 12, 10);
        _continue.BorderBrush = B(last != null ? Hud : Line, 140);
        _continue.BorderThickness = new Thickness(1);
        _continue.Background = B(Hud, 14);
        _continue.Cursor = last != null ? Cursors.Hand : null;
        _continue.MouseLeftButtonUp -= ContinueClick;
        if (last != null) _continue.MouseLeftButtonUp += ContinueClick;
    }

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

        _right.Children.Add(Section("BUILD A THEATER", "From the ground up: each step uses what the one before made"));
        var steps = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        var build = Tools.Where(t => t.Step > 0).OrderBy(t => t.Step).ToList();
        for (int i = 0; i < build.Count; i++) steps.ColumnDefinitions.Add(new ColumnDefinition());
        // linea del percorso dietro i numeri
        var rail = new Rectangle { Height = 2, Fill = B(AmberC, 120), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(40, 17, 40, 0) };
        Grid.SetColumnSpan(rail, build.Count);
        steps.Children.Add(rail);
        for (int i = 0; i < build.Count; i++)
        {
            var card = StepCard(build[i]);
            Grid.SetColumn(card, i);
            steps.Children.Add(card);
        }
        _right.Children.Add(steps);

        _right.Children.Add(Section("MODELS & DATA", "Any theater, any time"));
        var data = new UniformGrid2(2);
        foreach (var t in Tools.Where(t => t.Group == "DATA")) data.Add(WideCard(t));
        _right.Children.Add(data.Panel);

        _right.Children.Add(Section("SAFETY", "Every program saves what it replaces before writing into BMS"));
        var safety = new WrapPanel();
        safety.Children.Add(SmallButton("\uE81C", "Backups", "Every backup made by the programs, newest first", () => new BackupsWindow(SuiteRoot()) { Owner = this }.ShowDialog()));
        safety.Children.Add(SmallButton("\uE8B7", "Suite folder", "Programs, settings and logs of the BMS Suite", () => OpenFolder(SuiteRoot())));
        safety.Children.Add(SmallButton("\uE838", "BMS Data folder", "Falcon BMS 4.38\\Data (read it, do not change it by hand)", () => OpenFolder(_data)));
        _right.Children.Add(safety);

        _right.Children.Add(Section("SUPPORT", "The BMS Suite is free"));
        _right.Children.Add(SupportCard());
        RefreshTiles();
    }

    private static FrameworkElement Section(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 22, 0, 10) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = title, FontFamily = Mono, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = B(AmberC) });
        row.Children.Add(new TextBlock { Text = "  —  " + sub, FontSize = 12.5, Foreground = B(Sub), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(row);
        return sp;
    }

    /// <summary>Passo del percorso: numero nel cerchio sulla linea, poi la scheda del programma.</summary>
    private FrameworkElement StepCard(Tool t)
    {
        var col = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
        var num = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center };
        num.Children.Add(new Ellipse { Fill = B(Bg), Stroke = B(AmberC), StrokeThickness = 2 });
        num.Children.Add(new TextBlock { Text = t.Step.ToString("00"), FontFamily = Mono, FontWeight = FontWeights.Bold, FontSize = 13, Foreground = B(AmberC), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        col.Children.Add(num);
        var state = new TextBlock { FontFamily = Mono, FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0) };
        _tileState[t] = state;
        var inner = new StackPanel { Margin = new Thickness(16, 16, 16, 14) };
        var big = ToolIcon(t.Exe, 52) ?? new TextBlock { Text = t.Glyph, FontFamily = Icons, FontSize = 30, Foreground = B(Text) };
        big.Margin = new Thickness(0, 0, 0, 12);
        inner.Children.Add(big);
        inner.Children.Add(new TextBlock { Text = t.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        inner.Children.Add(new TextBlock { Text = t.Line, Foreground = B(Sub), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        inner.Children.Add(state);
        col.Children.Add(Panelize(inner, t, new Thickness(0, 12, 0, 0)));
        return col;
    }

    private FrameworkElement WideCard(Tool t)
    {
        var state = new TextBlock { FontFamily = Mono, FontSize = 10.5, Margin = new Thickness(0, 6, 0, 0) };
        _tileState[t] = state;
        var row = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
        var ic = ToolIcon(t.Exe, 52) ?? new TextBlock { Text = t.Glyph, FontFamily = Icons, FontSize = 30, Foreground = B(Text) };
        ic.VerticalAlignment = VerticalAlignment.Center; ic.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(ic, Dock.Left);
        row.Children.Add(ic);
        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = t.Title, FontSize = 16, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = t.Line, Foreground = B(Sub), FontSize = 12.5 },
                state,
            },
        });
        return Panelize(row, t, new Thickness(0, 0, 12, 12));
    }

    /// <summary>Pannello da cabina: angolo smussato in alto a destra, bordo che si accende d'ambra al passaggio.</summary>
    private Border Panelize(FrameworkElement inner, Tool t, Thickness margin)
    {
        var corner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L14,0 L14,14 Z"), Fill = B(AmberC, 200), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        var g = new Grid();
        g.Children.Add(inner);
        g.Children.Add(corner);
        var b = new Border
        {
            Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(1), Margin = margin, Cursor = Cursors.Hand, Child = g,
            ToolTip = new ToolTip { Content = new TextBlock { Text = t.Description, MaxWidth = 340, TextWrapping = TextWrapping.Wrap } },
        };
        b.MouseEnter += (_, _) => { b.BorderBrush = B(AmberC); b.Background = B(PanelHi); corner.Visibility = Visibility.Visible; };
        b.MouseLeave += (_, _) => { b.BorderBrush = B(Line); b.Background = B(Panel); corner.Visibility = Visibility.Hidden; };
        b.MouseLeftButtonUp += (_, _) => Launch(t, CurrentTheater);
        return b;
    }

    private static FrameworkElement SmallButton(string glyph, string title, string tip, Action act)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 9, 16, 9) };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, Foreground = B(AmberC), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Background = B(Panel), BorderBrush = B(Line), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, Child = sp, ToolTip = tip };
        b.MouseEnter += (_, _) => b.BorderBrush = B(AmberC);
        b.MouseLeave += (_, _) => b.BorderBrush = B(Line);
        b.MouseLeftButtonUp += (_, _) => act();
        return b;
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

    private void UpdateState(Tool t)
    {
        if (!_tileState.TryGetValue(t, out var s)) return;
        var exe = ExeOf(t);
        bool running = false;
        try { running = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(t.Exe)).Length > 0; } catch { }
        if (exe == null) { s.Text = "NOT BUILT"; s.Foreground = B(Red); }
        else if (running) { s.Text = "● RUNNING"; s.Foreground = B(Hud); }
        else { s.Text = "READY"; s.Foreground = B(Sub); }
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
            _s.LastTool = t.Title; _s.LastTheater = theater; _s.LastTime = DateTime.Now; SaveSettings();
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
