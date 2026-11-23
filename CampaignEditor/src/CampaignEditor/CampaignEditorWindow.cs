using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;
using TerrainTool.Core.Editor;
using TerrainTool.Core.Project;
using TerrainTool.Core.Theater;

namespace CampaignEditor;

/// <summary>
/// Editor di campagna (finestra separata): apre un .tac (missione TE) o un .cam (campagna) e mostra sulla mappa del
/// teatro, con o senza la foto, gli obiettivi colorati per squadra, i collegamenti e le unita' (divisione → brigata →
/// battaglione, squadroni). Si spostano gli obiettivi e le unita' col mouse, si creano/tolgono collegamenti, si cambiano
/// proprietari, ordini e obiettivi assegnati; "Validate" controlla tutto. Ispirato a Mission Commander, formati
/// studiati su di esso (con il permesso dell'autore, nessun codice copiato). Salva nel progetto (CampaignEdits + Output\Install); in BMS solo con "Install".
/// </summary>
public sealed class CampaignEditorWindow : Window
{
    private readonly TheaterProject? _p;     // progetto del nuovo terreno, o null per un teatro qualsiasi di BMS
    private readonly CampaignWorkspace _ws;
    private readonly CampaignDb _db;          // database BMS 4.38 del nuovo terreno (ObjectsNT)
    private CampaignDb? _oldDb;               // database del vecchio add-on (per i file con unita' non ancora convertite)
    private CampaignDb _unitDb;               // quello con cui si leggono le unita' del file aperto
    private readonly TextBlock _banner = new() { Background = new SolidColorBrush(Color.FromRgb(255, 235, 180)), Padding = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TheaterGrid _grid;
    private CampaignDoc? _doc;
    private readonly CampaignMap _map;
    private readonly Stack<Action> _undo = new();

    private readonly TextBox _search = new() { MinWidth = 120 };
    private readonly ComboBox _typeFilter = new() { MinWidth = 120, Margin = new Thickness(6, 0, 0, 0) };
    private readonly ListBox _objList = new();
    private readonly TreeView _unitTree = new();
    private readonly ListBox _teamList = new();
    private readonly TreeView _pkgTree = new();
    private readonly TreeView _sqnTree = new();
    private readonly CheckBox _showRoutes = new() { Content = "All routes", IsChecked = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Draw the route of every flight (thin). The route of the selected package is always drawn, as in the TE editor of BMS" };
    private readonly StackPanel _props = new() { Margin = new Thickness(8) };
    private readonly MapLegend _legend = new();
    private TabItem? _propsTab, _legendTab;
    private readonly DataGrid _issues = new();
    private readonly ComboBox _issueFilter = new() { Width = 130 };
    private readonly TextBlock _issueCount = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2), FontFamily = new FontFamily("Consolas") };
    private readonly RadioButton _modeSelect = new() { Content = "Select / move", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Click to select, drag to move an objective or a unit (S)" };
    private readonly RadioButton _modeLink = new() { Content = "Link", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Click two objectives: links them, or removes the link if they are already linked (K)" };
    private readonly RadioButton _modeAssign = new() { Content = "Assign objective", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "With a battalion or brigade selected, click an objective to assign it (A). Ctrl+click does the same in any mode." };
    private readonly RadioButton _modePaint = new() { Content = "Paint owner", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Drag on the map: every objective under the brush goes to the chosen team (P). Ctrl+Z undoes a whole stroke." };
    private readonly ComboBox _paintTeam = new() { Width = 120, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Team to paint" };
    private readonly ComboBox _brush = new() { Width = 70, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Brush radius" };
    private readonly CheckBox _paintFirst = new() { Content = "also pre-war owner", IsChecked = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Also set the first owner (who held it before the war). As in the Korea campaigns: owner = who holds it now, first owner = whose territory it was at the start." };
    private readonly CheckBox _showPhoto = new() { Content = "Terrain texture", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _iconStyle = new() { Width = 95, Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "BMS icons: shapes of the BMS 2D map in the colour of each team. NATO icons (APP-6 / MIL-STD-2525): the frame and colour tell the side as seen from the Viewpoint team – rectangle friend, diamond hostile, square neutral (circle / diamond / square for ships); black bar on top = objective" };
    private readonly ComboBox _viewpoint = new() { Width = 150, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed,
        ToolTip = "Team you look from: its allies are friends, the teams it is hostile to or at war with are enemies, the others neutral (relations of the Teams tab)" };
    private readonly TextBlock _viewpointLbl = new() { Text = "Viewpoint ", VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ComboBox _background = new() { Width = 120, Margin = new Thickness(0, 0, 10, 0),
        ToolTip = "Map background: the terrain texture of the theater, or a plain political map (sea in light blue, land, lakes, state borders and names: Natural Earth data) to see the whole picture, or nothing" };
    private readonly CheckBox _showLinks = new() { Content = "Links", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showUnits = new() { Content = "Units", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showNames = new() { Content = "Names", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showLines = new() { Content = "Front lines", IsChecked = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Rings on the objectives flagged as 1st (red), 2nd (orange), 3rd (yellow) line" };
    private readonly CheckBox _autoLines = new() { Content = "Auto front lines", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Recompute the 1st/2nd/3rd line of every objective after each change of owners, links or relations, as BMS does (checked on the Korea files): " +
                  "1st line = linked to an objective of a hostile/at war team, 2nd = linked to a 1st line, 3rd = linked to a 2nd line" };
    private readonly CheckBox _showSam = new() { Content = "SAM", IsChecked = false, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "SAM coverage (and ships): filled circles in the team's colour, radius = range of the battalion in the database" };
    private readonly CheckBox _showAaa = new() { Content = "AAA", IsChecked = false, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "AAA coverage: dashed circles in the team's colour" };
    private readonly ComboBox _covAlt = new() { Width = 78, Margin = new Thickness(0, 0, 8, 0),
        ToolTip = "Range shown: against high targets (Rng_Air) or low targets (Rng_LowAir, about below 2,000 m)" };
    private readonly ComboBox _teamOnly = new() { Width = 130, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _save = new() { Content = "Save", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _saveAs = new() { Content = "Save As…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
        ToolTip = "Save with another name or in another folder (also the theater's Campaign folder in BMS, with a backup of the file it replaces). The names inside the file and the companion files (.twx, .iff, .fmap, .ini, .l16.txtpb …) follow the new name" };
    private readonly Button _install = new() { Content = "Install into BMS…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0) };
    private bool _syncing;
    private readonly TabControl _leftTabs = new();

    public CampaignEditorWindow(TheaterProject p, string? open = null) : this(CampaignWorkspace.FromProject(p), open) { }

    /// <summary>Editor su un teatro qualsiasi (o sul progetto): database, mappa e cartelle dal "workspace".</summary>
    public CampaignEditorWindow(CampaignWorkspace ws, string? open = null)
    {
        _ws = ws;
        _p = ws.Project;
        Title = "Campaign editor – " + ws.Name;
        Width = 1500; Height = 950; MinWidth = 1000; MinHeight = 650;
        RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();
        _grid = ws.Theater.CreateGrid();
        _db = CampaignDb.Load(ws.DbDirs);
        _unitDb = _db;
        _map = new CampaignMap(ws.Theater.SizeKm, ws.PhotoDir, ws.Theater.SizeMeters);

        // ── barra in alto
        var bar = new WrapPanel { Margin = new Thickness(6, 6, 6, 2) };
        var openB = new Button { Content = "Open…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var validate = new Button { Content = "Validate", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 12, 0) };
        bar.Children.Add(openB); bar.Children.Add(_save); bar.Children.Add(_saveAs);
        var bkB = new Button { Content = "Backups…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Files replaced by Save / Save As, kept in " + BackupStore.Root + ": restore them" };
        bkB.Click += (_, _) => new BackupWindow { Owner = this }.Show();
        bar.Children.Add(bkB);
        if (_p != null) bar.Children.Add(_install);
        else
        {
            // teatro di BMS: niente installazione automatica, si salva fuori da BMS
            var folderB = new Button { Content = "Saved files folder", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Copies saved by earlier versions of the editor (outside BMS): " + ws.EditsFolder };
            folderB.Click += (_, _) =>
            {
                Directory.CreateDirectory(ws.EditsFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + ws.EditsFolder + "\"") { UseShellExecute = true });
            };
            bar.Children.Add(folderB);
        }
        bar.Children.Add(validate);
        var convertB = new Button { Content = "Convert unit types…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Campaigns and TE missions of the old add-on: their units use the old add-on's class table; convert them to the BMS 4.38 types" };
        var adB = new Button { Content = "Air defense plan…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Move the air defense battalions of a team to the most important objectives and/or add new ones (Korea rules)" };
        var depB = new Button { Content = "Deploy divisions…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Put a team's brigades in front / second / reserve echelons behind the front, as the Korea campaigns start" };
        var utB = new Button { Content = "Battalions & brigades…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Table of every battalion and brigade type of BMS 4.38 (as in Mission Commander): add units to the campaign or change a unit's type" };
        utB.Click += (_, _) => OpenUnitTable();
        var ulB = new Button { Content = "Units list…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "All units in a table (as the Units tab of Mission Commander): squadrons, packages, flights, brigades, battalions, task forces; filters, problems in colour, double-click = unit window" };
        ulB.Click += (_, _) => OpenUnitList();
        var stB = new Button { Content = "Station ILS…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "stations+ils.dat of the theater (as Mission Commander): TACAN, tower / ground / approach / ATIS frequencies, ILS per runway, checked against the runways of the new base layouts" };
        stB.Click += (_, _) => OpenStations();
        var aiB = new Button { Content = "Campaign AI…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Falcon4.aii (how far from the front AWACS, JSTARS, tankers, ECM, BAI, SAR, BARCAP fly; AI rules) and MissionData (altitudes, loiter, flight size of every mission type), compared with the Korea" };
        aiB.Click += (_, _) => new CampaignAiWindow(_ws) { Owner = this }.Show();
        var teB = new Button { Content = "TE lists…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "TePlanes.lst, TeUnits.lst, TeNavalUnits.lst: the aircraft, battalions and naval units offered in the Tactical Engagement (by producer), checked against this theater's database and the Korea" };
        teB.Click += (_, _) => new TeListsWindow(_ws, _db) { Owner = this }.Show();
        var thrB = new Button { Content = "Campaign terrain (.thr)…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "The campaign's .thr (ground cover, relief, rail per km², still used by BMS 4.38 for move times, cover and 'near water'): view it, rebuild it from the New Terrain, compare it with the terrain BMS generates for the campaign" };
        // the tools in two menus (one entry per group instead of nine buttons in a row)
        var unitTools = new List<Button> { utB, ulB, depB, adB };
        if (_p != null) unitTools.Add(convertB);
        var unitsMenu = BmsSuiteUi.SuiteTheme.MenuButton("Units", unitTools, "Battalions and brigades, the units list, deploying divisions, the air defense plan, converting old unit types");
        var filesMenu = BmsSuiteUi.SuiteTheme.MenuButton("Theater files", new[] { stB, aiB, teB, thrB }, "Station ILS, Campaign AI, TE lists, campaign terrain (.thr)");
        filesMenu.Margin = new Thickness(0, 0, 12, 0);
        bar.Children.Add(unitsMenu);
        bar.Children.Add(filesMenu);
        thrB.Click += (_, _) => new TheaterCellsWindow(_ws) { Owner = this }.Show();
        depB.Click += (_, _) => OpenDeploy();
        adB.Click += (_, _) => OpenAirDefense();
        convertB.Click += (_, _) => OpenConverter();
        bar.Children.Add(new TextBlock { Text = "Mouse: ", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        bar.Children.Add(_modeSelect); bar.Children.Add(_modeLink); bar.Children.Add(_modeAssign);
        bar.Children.Add(_modePaint); bar.Children.Add(_paintTeam); bar.Children.Add(_brush); bar.Children.Add(_paintFirst);
        foreach (var km in new[] { 2, 5, 10, 20, 40, 80 }) _brush.Items.Add(new ComboBoxItem { Content = km + " km", Tag = km });
        _brush.SelectedIndex = 2;
        bar.Children.Add(new Separator { Width = 12, Visibility = Visibility.Hidden });
        // riquadro "Map": sfondo (texture del terreno o mappa politica) e cosa mostrare, insieme anche quando la barra va a capo
        _background.Items.Add("Terrain texture"); _background.Items.Add("Political map"); _background.Items.Add("None");
        _background.SelectedIndex = 0;
        var mapBox = new StackPanel { Orientation = Orientation.Horizontal };
        mapBox.Children.Add(new TextBlock { Text = "Map ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        mapBox.Children.Add(_background);
        _iconStyle.Items.Add("BMS icons"); _iconStyle.Items.Add("NATO icons"); _iconStyle.SelectedIndex = 0;
        mapBox.Children.Add(_iconStyle);
        mapBox.Children.Add(_viewpointLbl);
        mapBox.Children.Add(_viewpoint);
        mapBox.Children.Add(new TextBlock { Text = "Show: ", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        mapBox.Children.Add(_showLinks); mapBox.Children.Add(_showUnits); mapBox.Children.Add(_showNames); mapBox.Children.Add(_showLines); mapBox.Children.Add(_autoLines); mapBox.Children.Add(_showRoutes);
        bar.Children.Add(new Border
        {
            Child = mapBox, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 0, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        // copertura antiaerea: un solo riquadro, cosi' quando la barra va a capo SAM, AAA e la quota restano insieme
        _covAlt.Items.Add("High alt"); _covAlt.Items.Add("Low alt"); _covAlt.SelectedIndex = 0;
        _covAlt.Margin = new Thickness(0);
        var adBox = new StackPanel { Orientation = Orientation.Horizontal };
        adBox.Children.Add(new TextBlock { Text = "Air defence ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        adBox.Children.Add(_showSam); adBox.Children.Add(_showAaa); adBox.Children.Add(_covAlt);
        bar.Children.Add(new Border
        {
            Child = adBox, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 4, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        bar.Children.Add(new TextBlock { Text = "Team ", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_teamOnly);

        // ── sinistra: obiettivi, unita', squadre
        var objPanel = new DockPanel { Margin = new Thickness(4) };
        var fr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        fr.Children.Add(new TextBlock { Text = "Find ", VerticalAlignment = VerticalAlignment.Center });
        fr.Children.Add(_search); fr.Children.Add(_typeFilter);
        DockPanel.SetDock(fr, Dock.Top); objPanel.Children.Add(fr);
        objPanel.Children.Add(_objList);
        var tabs = _leftTabs;
        tabs.Items.Add(new TabItem { Header = "Objectives", Content = objPanel });
        tabs.Items.Add(new TabItem { Header = "Units", Content = _unitTree });
        tabs.Items.Add(new TabItem { Header = "Teams", Content = _teamList });
        tabs.Items.Add(new TabItem { Header = "Packages", Content = _pkgTree });
        tabs.Items.Add(new TabItem { Header = "Squadrons", Content = _sqnTree });

        // ── basso: problemi
        _issues.AutoGenerateColumns = false; _issues.IsReadOnly = true; _issues.HeadersVisibility = DataGridHeadersVisibility.Column;
        _issues.SelectionMode = DataGridSelectionMode.Single; _issues.CanUserAddRows = false;
        _issues.Columns.Add(new DataGridTextColumn { Header = "Level", Binding = new System.Windows.Data.Binding(nameof(CampIssue.Level)), Width = new DataGridLength(70) });
        _issues.Columns.Add(new DataGridTextColumn { Header = "Area", Binding = new System.Windows.Data.Binding(nameof(CampIssue.Category)), Width = new DataGridLength(80) });
        _issues.Columns.Add(new DataGridTextColumn { Header = "Problem (double-click = show on map)", Binding = new System.Windows.Data.Binding(nameof(CampIssue.Message)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        foreach (var s in new[] { "Errors and warnings", "Errors only", "Everything" }) _issueFilter.Items.Add(s);
        _issueFilter.SelectedIndex = 0;
        var issueBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        issueBar.Children.Add(new TextBlock { Text = "Validation  ", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        issueBar.Children.Add(_issueFilter); issueBar.Children.Add(_issueCount);
        var issuePanel = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(issueBar, Dock.Top); issuePanel.Children.Add(issueBar); issuePanel.Children.Add(_issues);

        // ── layout
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var mid = new Grid();
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        Grid.SetColumn(tabs, 0); mid.Children.Add(tabs);
        var gs1 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(gs1, 1); mid.Children.Add(gs1);
        var mapBorder = new Border { Child = _map, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        Grid.SetColumn(mapBorder, 2); mid.Children.Add(mapBorder);
        var gs2 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(gs2, 3); mid.Children.Add(gs2);
        var propsScroll = new ScrollViewer { Content = _props, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // a destra: due schede a tutta altezza, Properties e Map legend (la legenda non copre la mappa e non ha limiti di altezza)
        var legendScroll = new ScrollViewer { Content = _legend, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _propsTab = new TabItem { Header = "Properties", Content = propsScroll };
        _legendTab = new TabItem { Header = "Map legend", Content = legendScroll };
        var right = new TabControl { Margin = new Thickness(2, 0, 2, 2) };
        right.Items.Add(_propsTab); right.Items.Add(_legendTab);
        right.SelectedItem = _legendTab;
        Grid.SetColumn(right, 4); mid.Children.Add(right);
        var top = new StackPanel();
        top.Children.Add(bar); top.Children.Add(_banner);
        Grid.SetRow(top, 0); root.Children.Add(top);
        Grid.SetRow(mid, 1); root.Children.Add(mid);
        var gs3 = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(gs3, 2); root.Children.Add(gs3);
        Grid.SetRow(issuePanel, 3); root.Children.Add(issuePanel);
        Grid.SetRow(_status, 4); root.Children.Add(_status);
        Content = root;

        // ── eventi
        openB.Click += (_, _) => OpenDialog();
        _save.Click += (_, _) => Save();
        _saveAs.Click += (_, _) => SaveAs();
        _showRoutes.Click += (_, _) => { _map.ShowAllRoutes = _showRoutes.IsChecked == true; _map.InvalidateVisual(); };
        _showSam.Click += (_, _) => { _map.ShowSam = _showSam.IsChecked == true; _map.InvalidateVisual(); };
        _showAaa.Click += (_, _) => { _map.ShowAaa = _showAaa.IsChecked == true; _map.InvalidateVisual(); };
        _covAlt.SelectionChanged += (_, _) => { _map.CoverageLow = _covAlt.SelectedIndex == 1; _map.InvalidateVisual(); };
        _pkgTree.SelectedItemChanged += (_, _) => { if (!_syncing && (_pkgTree.SelectedItem as TreeViewItem)?.Tag is CampUnit u) Select(null, u, true); };
        _sqnTree.SelectedItemChanged += (_, _) => { if (!_syncing && (_sqnTree.SelectedItem as TreeViewItem)?.Tag is CampUnit u) Select(null, u, true); };
        _install.Click += (_, _) => Install();
        validate.Click += (_, _) => Validate();
        _modeSelect.Checked += (_, _) => _map.Mode = MapMode.Select;
        _modeLink.Checked += (_, _) => _map.Mode = MapMode.Link;
        _modeAssign.Checked += (_, _) => _map.Mode = MapMode.Assign;
        _modePaint.Checked += (_, _) => { _map.Mode = MapMode.Paint; _map.InvalidateVisual(); };
        _brush.SelectionChanged += (_, _) => { if (_brush.SelectedItem is ComboBoxItem { Tag: int km }) _map.BrushKm = km; };
        _paintTeam.SelectionChanged += (_, _) => _map.PaintTeam = _paintTeam.SelectedIndex;
        _paintFirst.Click += (_, _) => _map.PaintFirstOwner = _paintFirst.IsChecked == true;
        _showPhoto.Click += (_, _) => { _map.ShowPhoto = _showPhoto.IsChecked == true; _map.InvalidateVisual(); };
        _background.SelectionChanged += (_, _) => SetBackground(_background.SelectedIndex);
        _iconStyle.SelectionChanged += (_, _) =>
        {
            _map.NatoIcons = _iconStyle.SelectedIndex == 1;
            _viewpoint.Visibility = _viewpointLbl.Visibility = _map.NatoIcons ? Visibility.Visible : Visibility.Collapsed;
            FillLegend(); _map.InvalidateVisual();
        };
        _viewpoint.SelectionChanged += (_, _) =>
        {
            if (_viewpoint.SelectedItem is StackPanel { Tag: int vp }) { _map.Viewpoint = vp; FillLegend(); _map.InvalidateVisual(); }
        };
        _showLinks.Click += (_, _) => { _map.ShowLinks = _showLinks.IsChecked == true; _map.InvalidateVisual(); };
        _showUnits.Click += (_, _) => { _map.ShowUnits = _showUnits.IsChecked == true; _map.InvalidateVisual(); };
        _showNames.Click += (_, _) => { _map.ShowNames = _showNames.IsChecked == true; _map.InvalidateVisual(); };
        _showLines.Click += (_, _) => { _map.ShowFrontLines = _showLines.IsChecked == true; _map.InvalidateVisual(); };
        _teamOnly.SelectionChanged += (_, _) => { _map.TeamOnly = _teamOnly.SelectedIndex <= 0 ? -1 : _teamOnly.SelectedIndex - 1; _map.InvalidateVisual(); };
        _search.TextChanged += (_, _) => FillObjectives();
        _typeFilter.SelectionChanged += (_, _) => FillObjectives();
        _objList.SelectionChanged += (_, _) => { if (!_syncing && _objList.SelectedItem is ObjItem it) Select(it.O, null, true); };
        _unitTree.SelectedItemChanged += (_, _) => { if (!_syncing && (_unitTree.SelectedItem as TreeViewItem)?.Tag is CampUnit u) Select(null, u, true); };
        _teamList.SelectionChanged += (_, _) => { if (_teamList.SelectedItem is ListBoxItem { Tag: CampTeam t }) ShowTeam(t); };
        _issueFilter.SelectionChanged += (_, _) => FillIssues();
        _issues.MouseDoubleClick += (_, _) => { if (_issues.SelectedItem is CampIssue i) GoToIssue(i); };
        _map.SelectionChanged += (o, u) => Select(o, u, false);
        _map.Edited += () => { MarkDirty(); RefreshProps(); };
        _map.Hover += t => _status.Text = t;
        _map.PushUndo += a => _undo.Push(a);
        PreviewKeyDown += OnKey;
        Closing += (_, e) =>
        {
            if (_doc?.Dirty != true) return;
            var r = MessageBox.Show(this, "Save the changes before closing?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };

        _status.Text = _db.CtPath == null ? "Falcon4_CT.xml not found: run Objects first, or check the BMS folder" : "Open a .tac or .cam file";
        Loaded += (_, _) =>
        {
            string? f = open ?? DefaultFile();
            if (f != null) Open(f);
        };
    }

    private string? InstallCampaignDir => _ws.InstallCopyDir;

    private string? DefaultFile()
    {
        if (_p == null) return null;
        foreach (var dir in new[] { _ws.EditsFolder, InstallCampaignDir! })
        {
            string f = Path.Combine(dir, "Te_New_Nt.tac");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private void OpenStations()
    {
        if (_doc == null || _doc.Objectives.Count == 0) { MessageBox.Show(this, "Open a campaign or TE file first (its objectives are used to check the stations).", Title); return; }
        // disposizioni del vecchio add-on: stesso file nella cartella Campaign del vecchio add-on, o save0 / te_new
        CampaignDoc? oldDoc = null;
        var odb = OldDb();
        string oc = _p == null ? "" : Path.Combine(_p.OldTheaterFolder, "Campaign");
        if (odb != null && Directory.Exists(oc))
            foreach (var n in new[] { Path.GetFileName(_doc.Path), "save0.cam", "te_new.tac" })
            {
                var f = Directory.EnumerateFiles(oc).FirstOrDefault(x => Path.GetFileName(x).Equals(n, StringComparison.OrdinalIgnoreCase));
                if (f == null) continue;
                try { oldDoc = CampaignDoc.Load(f, odb.ClassOf); if (oldDoc.Objectives.Count > 0) break; } catch (Exception) { oldDoc = null; }
            }
        new StationWindow(_ws, _doc, _db, oldDoc, odb) { Owner = this }.Show();
    }

    private CampaignDb? OldDb()
    {
        if (_oldDb != null) return _oldDb;
        if (_p == null) return null;
        string dir = Path.Combine(_p.OldTheaterFolder, "Terrdata", "Objects");
        if (!File.Exists(Path.Combine(dir, "Falcon4_CT.xml"))) return null;
        _oldDb = CampaignDb.Load(new[] { dir });
        return _oldDb;
    }

    /// <summary>File del vecchio add-on (o sue copie nel progetto) non ancora convertito: le unita' usano la vecchia CT.</summary>
    private bool UsesOldDatabase(string path)
    {
        if (_p == null) return false;
        string full = Path.GetFullPath(path);
        bool ours = new[] { _p.OldTheaterFolder, ObjectsJob.CampaignEditsFolder(_p), InstallCampaignDir! }
            .Any(d => full.StartsWith(Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase));
        return ours && !UnitTypeConverter.LoadDone(ObjectsJob.CampaignEditsFolder(_p)).Contains(Path.GetFileName(path))
               && File.Exists(Path.Combine(_p.OldTheaterFolder, "Terrdata", "Objects", "Falcon4_CT.xml"));
    }

    private void OpenAirDefense()
    {
        if (_doc?.Units == null || _doc.Units.Error != null) { MessageBox.Show(this, "This file has no units.", Title); return; }
        if (!ReferenceEquals(_unitDb, _db)) { MessageBox.Show(this, "Convert the unit types first: this file still uses the old add-on database.", Title); return; }
        var w = new AirDefenseWindow(_doc, _db) { Owner = this };
        w.Applied += undo =>
        {
            var d = _doc;
            _undo.Push(() => { undo(); FillUnits(); });
            MarkDirty(); FillUnits(); _map.InvalidateVisual(); RefreshProps(); Validate();
        };
        w.Show();
    }

    private UnitTableWindow? _unitTableWin;

    private void OpenUnitTable()
    {
        if (_doc?.Units == null || _doc.Units.Error != null) { MessageBox.Show(this, "This file has no units.", Title); return; }
        if (!ReferenceEquals(_unitDb, _db)) { MessageBox.Show(this, "Convert the unit types first: this file still uses the old add-on database.", Title); return; }
        if (_unitTableWin is { IsLoaded: true }) { _unitTableWin.Activate(); return; }
        var w = new UnitTableWindow(_doc, _db, () => _selObj, () => _selUnit) { Owner = this };
        w.Applied += (undo, unit) => UnitsChanged(undo, unit);
        w.Closed += (_, _) => _unitTableWin = null;
        _unitTableWin = w;
        w.Show();
    }

    private UnitListWindow? _unitListWin;

    private void OpenUnitList(CampUnit? open = null)
    {
        if (_doc?.Units == null || _doc.Units.Error != null) { MessageBox.Show(this, "This file has no units.", Title); return; }
        if (!ReferenceEquals(_unitDb, _db)) { MessageBox.Show(this, "Convert the unit types first: this file still uses the old add-on database.", Title); return; }
        if (_unitListWin is not { IsLoaded: true })
        {
            var w = new UnitListWindow(_doc, _db, () => _selObj) { Owner = this };
            w.Applied += (undo, unit) => UnitsChanged(undo, unit);
            w.ShowOnMap += u => { Activate(); Select(null, u, true); };
            w.AddUnit += _ => OpenUnitTable();
            w.Closed += (_, _) => _unitListWin = null;
            _unitListWin = w;
            w.Show();
        }
        else _unitListWin.Activate();
        if (open != null) _unitListWin.Open(open);
    }

    /// <summary>Dopo un'aggiunta / cambio / spostamento / eliminazione di unita': annullamento, liste, mappa, controllo.</summary>
    private void UnitsChanged(Action undo, CampUnit? select)
    {
        _unitListWin?.Fill();
        _undo.Push(() => { undo(); FillUnits(); _unitTableWin?.Refresh(); _unitListWin?.Fill(); });
        MarkDirty(); FillUnits(); _map.InvalidateVisual();
        if (select != null && _doc?.Units?.Units.Contains(select) == true) Select(null, select, true);
        else { _selUnit = null; RefreshProps(); }
        Validate();
    }

    private void OpenDeploy()
    {
        if (_doc?.Units == null || _doc.Units.Error != null) { MessageBox.Show(this, "This file has no units.", Title); return; }
        if (!ReferenceEquals(_unitDb, _db)) { MessageBox.Show(this, "Convert the unit types first: this file still uses the old add-on database.", Title); return; }
        if (!_doc.ObjectivesOwn && _doc.ObjectivesFrom == null) { MessageBox.Show(this, "The objectives of this file were not found.", Title); return; }
        var w = new DeployWindow(_doc, _db) { Owner = this };
        w.Applied += undo =>
        {
            _undo.Push(() => { undo(); FillUnits(); });
            MarkDirty(); FillUnits(); _map.InvalidateVisual(); RefreshProps(); Validate();
        };
        w.Show();
    }

    private void OpenConverter()
    {
        var old = OldDb();
        if (_p == null || old == null) { MessageBox.Show(this, "The old add-on database (Terrdata\\Objects\\Falcon4_CT.xml) was not found" + (_p == null ? "" : " in " + _p.OldTheaterFolder), Title); return; }
        var w = new UnitConvertWindow(_p, old, _db) { Owner = this };
        w.FileConverted += name =>
        {
            if (_doc != null && string.Equals(Path.GetFileName(_doc.Path), name, StringComparison.OrdinalIgnoreCase) && !_doc.Dirty)
                Open(Path.Combine(ObjectsJob.CampaignEditsFolder(_p!), name));
        };
        w.Show();
    }

    // ───────────────────────── file ─────────────────────────

    private void OpenDialog()
    {
        if (_doc?.Dirty == true && MessageBox.Show(this, "Discard the unsaved changes?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Falcon campaign / TE (*.tac;*.cam)|*.tac;*.cam|All files|*.*" };
        string? start = _ws.SearchDirs().FirstOrDefault(d => Directory.Exists(d) && Directory.EnumerateFiles(d).Any(f => f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase)));
        if (start != null) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
    }

    private void Open(string path)
    {
        try
        {
            Cursor = Cursors.Wait;
            bool oldUnits = UsesOldDatabase(path);
            _unitDb = oldUnits ? OldDb() ?? _db : _db;
            _doc = CampaignDoc.Load(path, _unitDb.ClassOf, _ws.SearchDirs());
            _banner.Visibility = oldUnits && (_doc.Units?.Units.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            _banner.Text = "The units of this file still use the old add-on's class table (names are shown from it). In BMS 4.38 many of them would be other " +
                           "vehicles: use 'Convert unit types…' before installing.";
            _undo.Clear();
            _map.SetDoc(_doc, _db, _unitDb);
            Title = $"Campaign editor – {_ws.Name} – {Path.GetFileName(path)}" + (_ws.IsProtected(path) ? (_p == null ? "  (BMS file: Save overwrites it, with a backup)" : "  (original: Save writes into the project)") : "");
            _teamOnly.Items.Clear();
            _teamOnly.Items.Add("All");
            foreach (var t in _doc.Teams?.Teams ?? new List<CampTeam>()) _teamOnly.Items.Add($"{t.Index} {t}");
            _teamOnly.SelectedIndex = 0;
            _paintTeam.Items.Clear();
            foreach (var t in _doc.Teams?.Teams ?? new List<CampTeam>()) _paintTeam.Items.Add($"{t.Index} {t}");
            _paintTeam.SelectedIndex = Math.Min(1, _paintTeam.Items.Count - 1);
            _typeFilter.Items.Clear();
            _typeFilter.Items.Add(new TypeItem(-1, $"All types ({_doc.Objectives.Count})"));
            foreach (var g in _doc.Objectives.GroupBy(o => _db.ObjectiveType(o.Type)).OrderBy(g => EditorData.TypeName(g.Key)))
                _typeFilter.Items.Add(new TypeItem(g.Key, $"{EditorData.TypeName(g.Key)} ({g.Count()})"));
            _typeFilter.SelectedIndex = 0;
            FillObjectives();
            FillUnits();
            FillTeams();
            int pkgs = FillPackages();
            _issues.ItemsSource = null; _allIssues.Clear(); _issueCount.Text = "";
            _props.Children.Clear();
            int units = _doc.Units?.Units.Count ?? 0;
            _status.Text = $"{Path.GetFileName(path)}: version {_doc.Version}, {_doc.Objectives.Count} objectives, {units} units, " +
                           $"{_doc.Teams?.Teams.Count ?? 0} teams. " + string.Join(" ", _doc.Log);
            if (_doc.Log.Count > 0) _props.Children.Add(Note(string.Join("\n", _doc.Log)));
            Validate();
            // missione TE con pacchetti: come il TE di BMS, si mostra il primo pacchetto con la sua rotta
            if (pkgs > 0 && path.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)
                && _doc.Units!.Units.FirstOrDefault(x => x.Kind == UnitKind.Package && x.Elements.Count > 0) is { } first)
            {
                _leftTabs.SelectedIndex = 3;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => Select(null, first, true)));
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Cursor = null; }
    }

    private bool Save()
    {
        if (_doc == null) return false;
        try
        {
            string name = Path.GetFileName(_doc.Path);
            // teatro di BMS: si salva sul file aperto (anche nella cartella Campaign del teatro, con avviso e backup);
            // progetto di TerrainTool: nella CampaignEdits del progetto (+ copia Install), in BMS con "Install"
            string dst;
            if (_p == null) dst = _doc.Path;
            else { Directory.CreateDirectory(_ws.EditsFolder); dst = Path.Combine(_ws.EditsFolder, name); }
            if (!PrepareWrite(dst)) return false;
            _doc.Save(dst);
            if (InstallCampaignDir != null)
            {
                Directory.CreateDirectory(InstallCampaignDir);
                File.Copy(dst, Path.Combine(InstallCampaignDir, name), true);
            }
            _doc.Path = dst;   // da qui in poi il file e' la copia salvata (i dati restano quelli in memoria)
            Title = $"Campaign editor – {_ws.Name} – {name}";
            _status.Text = _p != null
                ? $"Saved {dst} (and the Install copy). 'Install into BMS…' copies it into the add-on."
                : $"Saved {dst}" + (_lastBackup != null ? $" (previous version backed up in {_lastBackup})." : ".");
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    private void Install()
    {
        if (_doc == null || _p == null) return;
        if (_doc.Dirty && !Save()) return;
        string name = Path.GetFileName(_doc.Path);
        string src = Path.Combine(ObjectsJob.CampaignEditsFolder(_p), name);
        if (!File.Exists(src)) { MessageBox.Show(this, "Save first.", "Install", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string? targetDir = ProjectInstall.TargetCampaign(_p);
        if (targetDir == null) { MessageBox.Show(this, "The add-on's Campaign folder was not found in BMS.", "Install", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (BmsInstaller.IsBmsRunning()) { MessageBox.Show(this, "Falcon BMS is running: close it first.", "Install", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string dst = Path.Combine(targetDir, name);
        if (MessageBox.Show(this, $"Copy\n{src}\ninto\n{dst}?\n\nThe file now in BMS (if any) is kept in {BackupStore.Root} ('Backups…').", "Install into BMS",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            BackupStore.Save(dst);
            File.Copy(src, dst, true);
            _status.Text = "Installed " + dst;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Install", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void MarkDirty()
    {
        if (_doc == null) return;
        _doc.Dirty = true;
        if (_autoLines.IsChecked == true && _doc.CanEditObjectives) _doc.RecomputeFrontLines();
        Title = $"Campaign editor – {Path.GetFileName(_doc.Path)} *";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undo.Count > 0)
        {
            _undo.Pop()();
            MarkDirty(); _map.InvalidateVisual(); RefreshProps();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.S) _modeSelect.IsChecked = true;
            else if (e.Key == Key.K) _modeLink.IsChecked = true;
            else if (e.Key == Key.A) _modeAssign.IsChecked = true;
            else if (e.Key == Key.P) _modePaint.IsChecked = true;
            else if (e.Key == Key.F) _map.CenterOnSelection();
            else if (e.Key == Key.Escape) { _modeSelect.IsChecked = true; _map.CancelLink(); }
            else return;
            e.Handled = true;
        }
    }

    // ───────────────────────── liste ─────────────────────────

    private sealed record TypeItem(int Type, string Text) { public override string ToString() => Text; }
    private sealed record ObjItem(CampObjective O, string Text) { public override string ToString() => Text; }

    private void FillObjectives()
    {
        if (_doc == null) return;
        string q = _search.Text.Trim();
        int type = (_typeFilter.SelectedItem as TypeItem)?.Type ?? -1;
        var items = _doc.Objectives
            .Where(o => type < 0 || _db.ObjectiveType(o.Type) == type)
            .Where(o => q.Length == 0 || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.CampId.ToString(CultureInfo.InvariantCulture) == q)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new ObjItem(o, $"{o.Name}  ·  {EditorData.TypeName(_db.ObjectiveType(o.Type))}  ·  {CampaignValidator.TeamName(_doc, o.Owner)}"))
            .ToList();
        _objList.ItemsSource = items;
    }

    private string UnitLabel(CampUnit u)
    {
        string type = _unitDb.UnitTypeName(u.Type);
        string kind = u.Kind switch { UnitKind.Battalion => "Bn", UnitKind.Brigade => "Bde", UnitKind.Squadron => "Sqn", UnitKind.TaskForce => "TF", UnitKind.Flight => "Flight", UnitKind.Package => "Package", _ => "" };
        string label = $"{kind} {u.NameId}" + (type.Length > 0 ? " – " + type : "");
        if (u.IsGround) label += $"  [{CampUnit.OrdersName(u.Orders)}]";
        return label;
    }

    private void FillUnits()
    {
        _unitTree.Items.Clear();
        if (_doc?.Units == null) return;
        var units = _doc.Units.Units;
        foreach (var team in units.GroupBy(u => u.Owner).OrderBy(g => g.Key))
        {
            var tn = new TreeViewItem { Header = $"{CampaignValidator.TeamName(_doc, team.Key)} ({team.Count()})", IsExpanded = false };
            var brig = team.Where(u => u.Kind == UnitKind.Brigade).ToList();
            foreach (var div in brig.GroupBy(b => b.Division).OrderBy(g => g.Key))
            {
                var dn = new TreeViewItem { Header = div.Key == 0 ? $"No division ({div.Count()} brigades)" : $"Division {div.Key} ({div.Count()} brigades)" };
                foreach (var b in div.OrderBy(b => b.NameId))
                {
                    var bn = new TreeViewItem { Header = UnitLabel(b), Tag = b };
                    foreach (var e in b.Elements)
                        if (_doc.Unit(e) is { } bat) bn.Items.Add(new TreeViewItem { Header = UnitLabel(bat), Tag = bat });
                        else bn.Items.Add(new TreeViewItem { Header = $"missing battalion {e}", Foreground = Brushes.Red });
                    dn.Items.Add(bn);
                }
                tn.Items.Add(dn);
            }
            var indep = team.Where(u => u.Kind == UnitKind.Battalion && (u.Parent.IsNull || _doc.Unit(u.Parent) == null)).ToList();
            if (indep.Count > 0)
            {
                var inode = new TreeViewItem { Header = $"Independent battalions ({indep.Count})" };
                foreach (var g in indep.GroupBy(u => u.Orders).OrderBy(g => g.Key))
                {
                    var on = new TreeViewItem { Header = $"{CampUnit.OrdersName(g.Key)} ({g.Count()})" };
                    foreach (var u in g.OrderBy(u => u.NameId)) on.Items.Add(new TreeViewItem { Header = UnitLabel(u), Tag = u });
                    inode.Items.Add(on);
                }
                tn.Items.Add(inode);
            }
            void Group(string title, UnitKind k)
            {
                var l = team.Where(u => u.Kind == k).OrderBy(u => u.NameId).ToList();
                if (l.Count == 0) return;
                var n = new TreeViewItem { Header = $"{title} ({l.Count})" };
                foreach (var u in l) n.Items.Add(new TreeViewItem { Header = UnitLabel(u), Tag = u });
                tn.Items.Add(n);
            }
            Group("Squadrons", UnitKind.Squadron);
            Group("Task forces", UnitKind.TaskForce);
            Group("Packages", UnitKind.Package);
            Group("Flights", UnitKind.Flight);
            _unitTree.Items.Add(tn);
        }
    }

    /// <summary>Pacchetto di un volo (o il pacchetto stesso); null per le altre unita'.</summary>
    private CampUnit? PackageOf(CampUnit? u) =>
        u == null || _doc == null ? null : u.Kind == UnitKind.Package ? u : u.Kind == UnitKind.Flight ? _doc.Unit(u.PackageId) ?? u : null;

    private string FlightText(CampUnit f)
    {
        string ac = _unitDb.UnitVehicles(f.Type - 100).FirstOrDefault() ?? _unitDb.UnitTypeName(f.Type);
        int n = UnitText.Vehicles(f.Roster);
        string tot = f.TimeOnTarget > 0 ? "  TOT " + CampWaypoint.Time(f.TimeOnTarget) : "";
        return $"{ac}{(n > 0 ? " ×" + n : "")} · {f.MissionName}{tot}";
    }

    /// <summary>Scheda "Packages": per squadra i pacchetti con i loro voli (aereo, missione, orario sul bersaglio). Restituisce quanti.</summary>
    private int FillPackages()
    {
        _pkgTree.Items.Clear();
        if (_doc?.Units == null) return 0;
        var d = _doc;
        var pk = d.Units.Units.Where(u => u.Kind == UnitKind.Package).ToList();
        foreach (var team in pk.GroupBy(u => u.Owner).OrderBy(g => g.Key))
        {
            var tn = new TreeViewItem { Header = $"{CampaignValidator.TeamName(d, team.Key)} ({team.Count()} packages)", IsExpanded = true };
            foreach (var p in team.OrderBy(p => p.Elements.Select(e => d.Unit(e)?.TimeOnTarget ?? uint.MaxValue).DefaultIfEmpty(uint.MaxValue).Min()))
            {
                var flights = p.Elements.Select(e => d.Unit(e)).OfType<CampUnit>().ToList();
                string mis = flights.Count > 0 ? flights[0].MissionName : "empty";
                var pn = new TreeViewItem { Header = $"Package {p.CampId} · {mis} · {flights.Count} flight{(flights.Count == 1 ? "" : "s")}", Tag = p, IsExpanded = true };
                foreach (var f in flights) pn.Items.Add(WithMenu(new TreeViewItem { Header = FlightText(f) + SquadronSuffix(f), Tag = f }));
                tn.Items.Add(pn);
            }
            _pkgTree.Items.Add(tn);
        }
        var loose = d.Units.Units.Where(u => u.Kind == UnitKind.Flight && d.Unit(u.PackageId) == null).ToList();
        if (loose.Count > 0)
        {
            var ln = new TreeViewItem { Header = $"Flights without package ({loose.Count})" };
            foreach (var f in loose) ln.Items.Add(new TreeViewItem { Header = $"{CampaignValidator.TeamName(d, f.Owner)} · {FlightText(f)}", Tag = f });
            _pkgTree.Items.Add(ln);
        }
        if (pk.Count == 0 && loose.Count == 0) _pkgTree.Items.Add(new TreeViewItem { Header = "No packages in this file (a campaign plans them while it runs)", IsEnabled = false });
        FillSquadrons();
        return pk.Count;
    }

    private string SquadronName(CampUnit sq)
    {
        string ac = _unitDb.UnitVehicles(sq.Type - 100).FirstOrDefault() ?? _unitDb.UnitTypeName(sq.Type);
        return $"Sqn {sq.NameId} {ac}";
    }

    private string SquadronSuffix(CampUnit f) => _doc?.Unit(f.Squadron) is { Kind: UnitKind.Squadron } sq ? "   [" + SquadronName(sq) + "]" : "";

    /// <summary>Scheda "Squadrons": per squadra gli squadroni (aereo, base) con i loro voli e il pacchetto di ciascuno.</summary>
    private void FillSquadrons()
    {
        _sqnTree.Items.Clear();
        if (_doc?.Units == null) return;
        var d = _doc;
        var flightsOf = d.Units.Units.Where(u => u.Kind == UnitKind.Flight).GroupBy(f => f.Squadron).ToDictionary(g => g.Key, g => g.ToList());
        var sqns = d.Units.Units.Where(u => u.Kind == UnitKind.Squadron).ToList();
        foreach (var team in sqns.GroupBy(u => u.Owner).OrderBy(g => g.Key))
        {
            var tn = new TreeViewItem { Header = $"{CampaignValidator.TeamName(d, team.Key)} ({team.Count()} squadrons)", IsExpanded = true };
            // prima gli squadroni con voli (quelli della missione), poi gli altri
            foreach (var sq in team.OrderBy(s => flightsOf.ContainsKey(s.Id) ? 0 : 1).ThenBy(s => d.Obj(s.Airbase)?.Name ?? "").ThenBy(s => s.NameId))
            {
                var fl = flightsOf.GetValueOrDefault(sq.Id) ?? new List<CampUnit>();
                string bas = d.Obj(sq.Airbase)?.Name ?? "no base";
                var sn = WithMenu(new TreeViewItem { Header = $"{SquadronName(sq)} · {bas}{(fl.Count > 0 ? $" · {fl.Count} flight{(fl.Count == 1 ? "" : "s")}" : "")}", Tag = sq, IsExpanded = fl.Count > 0 });
                if (fl.Count > 0) sn.FontWeight = FontWeights.SemiBold;
                foreach (var f in fl)
                {
                    string pk = d.Unit(f.PackageId) is { Kind: UnitKind.Package } p ? $"Package {p.CampId} · " : "";
                    sn.Items.Add(WithMenu(new TreeViewItem { Header = pk + FlightText(f), Tag = f, FontWeight = FontWeights.Normal }));
                }
                tn.Items.Add(sn);
            }
            _sqnTree.Items.Add(tn);
        }
        if (sqns.Count == 0) _sqnTree.Items.Add(new TreeViewItem { Header = "No squadrons in this file", IsEnabled = false });
    }

    /// <summary>Menu del tasto destro: squadrone → cambio aereo; volo → altro squadrone. Il tasto destro seleziona anche la voce.</summary>
    private TreeViewItem WithMenu(TreeViewItem it)
    {
        if (it.Tag is not CampUnit u) return it;
        var m = new ContextMenu();
        if (u.Kind == UnitKind.Squadron)
        {
            var mi = new MenuItem { Header = "Change aircraft type…" };
            mi.Click += (_, _) => ChangeSquadronAircraft(u);
            m.Items.Add(mi);
        }
        else if (u.Kind == UnitKind.Flight)
        {
            var lo = new MenuItem { Header = "Loadout…", FontWeight = FontWeights.SemiBold };
            lo.Click += (_, _) => OpenLoadout(u);
            m.Items.Add(lo);
            var mi = new MenuItem { Header = "Move to squadron…" };
            mi.Click += (_, _) => MoveFlightToSquadron(u);
            m.Items.Add(mi);
            if (_doc?.Unit(u.Squadron) is { Kind: UnitKind.Squadron } sq)
            {
                var mi2 = new MenuItem { Header = $"Change aircraft type of its squadron (Sqn {sq.NameId})…" };
                mi2.Click += (_, _) => ChangeSquadronAircraft(sq);
                m.Items.Add(mi2);
            }
        }
        else return it;
        it.ContextMenu = m;
        // evento che sale dalla voce cliccata: la prima a riceverlo e' quella sotto il mouse (non lo squadrone che contiene il volo)
        it.MouseRightButtonDown += (_, e) => { it.IsSelected = true; it.Focus(); e.Handled = true; };
        return it;
    }

    /// <summary>Finestrina di scelta: una lista e OK / Cancel. Restituisce l'elemento scelto (Tag) o null.</summary>
    private object? Pick(string title, string text, IEnumerable<(object Tag, string Text)> items, object? current = null)
    {
        var list = new ListBox { Height = 320, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var (tag, t) in items) list.Items.Add(new ListBoxItem { Content = t, Tag = tag });
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, current));
        list.SelectedItem ??= list.Items.Count > 0 ? list.Items[0] : null;
        if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        var w = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { w.DialogResult = list.SelectedItem != null; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        w.Content = new StackPanel { Margin = new Thickness(10), Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, list, buttons } };
        return w.ShowDialog() == true && list.SelectedItem is ListBoxItem { Tag: var t2 } ? t2 : null;
    }

    private void AfterAirChange(Action? undo, string? err, string? note)
    {
        if (err != null) { MessageBox.Show(this, err, Title); return; }
        if (undo != null) _undo.Push(() => { undo(); FillUnits(); FillPackages(); });
        MarkDirty(); FillUnits(); FillPackages(); RefreshProps(); _map.InvalidateVisual();
        if (note != null) MessageBox.Show(this, note, Title);
    }

    private void OpenLoadout(CampUnit f)
    {
        if (_doc?.Units == null) return;
        if (f.LoadoutAt == 0 || f.LoadoutIdSize != 2) { MessageBox.Show(this, "This file format has no editable loadout for the flight.", Title); return; }
        var w = new LoadoutWindow(_doc, _unitDb, f) { Owner = this };
        if (w.ShowDialog() != true || w.Undo is not { } undo) return;
        _undo.Push(() => { undo(); RefreshProps(); });
        MarkDirty(); RefreshProps();
    }

    private void ChangeSquadronAircraft(CampUnit sq)
    {
        if (_doc?.Units == null) return;
        var se = new SquadronEditor(_doc, _unitDb);
        var flights = se.FlightsOf(sq);
        string text = $"New aircraft for {SquadronName(sq)}. The squadron gets full roster, standard stores and fuel of the new type." +
                      (flights.Count > 0 ? $"\nIts {flights.Count} planned flight{(flights.Count == 1 ? "" : "s")} will fly the new aircraft: their loadout is reset to the gun only (the pylons of another aircraft are not valid). Set it again in the loadout window or in the TE editor of BMS." : "");
        if (Pick("Change aircraft type", text, se.SquadronTypes().Select(x => ((object)x.Ct, x.Text)), sq.Type - 100) is not int ct || ct == sq.Type - 100) return;
        var (undo, err) = se.ChangeAircraft(sq, ct);
        AfterAirChange(undo, err, err == null && flights.Count > 0 ? $"{flights.Count} flight{(flights.Count == 1 ? "" : "s")} now fly {_unitDb.UnitVehicles(ct).FirstOrDefault()}: loadout reset to the gun only." : null);
    }

    private void MoveFlightToSquadron(CampUnit f)
    {
        if (_doc?.Units == null) return;
        var se = new SquadronEditor(_doc, _unitDb);
        var targets = se.FlightTargets(f);
        string bas = _doc.Unit(f.Squadron) is { } cur ? _doc.Obj(cur.Airbase)?.Name ?? "its base" : "its base";
        if (targets.Count == 0) { MessageBox.Show(this, $"No other squadron of this team at {bas}. A flight can only change squadron at the same base (its route starts and ends there).", Title); return; }
        string text = $"Other squadrons of the same team at {bas}. If the aircraft is different, the flight's loadout is reset to the gun only.";
        if (Pick("Move to squadron", text, targets.Select(s => ((object)s, SquadronName(s)))) is not CampUnit sq) return;
        var (undo, err, reset) = se.MoveFlight(f, sq);
        AfterAirChange(undo, err, err == null && reset ? $"The flight now flies {_unitDb.UnitVehicles(sq.Type - 100).FirstOrDefault()}: loadout reset to the gun only." : null);
    }

    /// <summary>Mette in evidenza l'unita' anche negli alberi (Units, Packages, Squadrons) che la contengono, aprendo i rami.</summary>
    private static bool SelectInTree(ItemsControl parent, CampUnit u)
    {
        foreach (var o in parent.Items)
        {
            if (o is not TreeViewItem it) continue;
            if (it.Tag == u) { it.IsSelected = true; it.BringIntoView(); return true; }
            if (it.Items.Count > 0 && SelectInTree(it, u)) { it.IsExpanded = true; return true; }
        }
        return false;
    }

    private bool _bmsWriteOk;          // l'utente ha gia' confermato di scrivere dentro BMS (in questa sessione)
    private string? _lastBackup;

    /// <summary>Cartella Campaign di un teatro installato in BMS (o della Korea): si puo' scriverci, con avviso e backup.</summary>
    private static bool IsCampaignDir(string dir) => string.Equals(new DirectoryInfo(dir).Name, "Campaign", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prima di scrivere dst. Fuori da BMS: niente da fare. Dentro BMS (o dentro le cartelle sorgente del progetto) si scrive solo in una
    /// cartella Campaign: la prima volta si chiede conferma consigliando un backup, BMS non deve essere aperto, e il file che viene
    /// sovrascritto si copia prima in &lt;cartella di lavoro&gt;\Backup\&lt;data_ora&gt;.
    /// </summary>
    private bool PrepareWrite(string dst)
    {
        _lastBackup = null;
        string full = Path.GetFullPath(dst), dir = Path.GetDirectoryName(full)!;
        if (!_ws.IsProtected(full)) { _lastBackup = BackupStore.Save(full); return true; }
        if (!IsCampaignDir(dir))
        {
            MessageBox.Show(this, $"{dir} is inside BMS (or a protected source folder) but it is not a Campaign folder: the editor writes there only into Campaign folders.",
                "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (BmsInstaller.IsBmsRunning())
        {
            MessageBox.Show(this, "Falcon BMS is running: close it before saving into its Campaign folder.", "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!_bmsWriteOk)
        {
            var r = MessageBox.Show(this,
                $"You are saving into the BMS installation:\n{dir}\n\n" +
                "Make a backup of this Campaign folder first if you have not done it yet (a BMS update or a reinstall also overwrites these files).\n" +
                "If a file is replaced, the editor also keeps a copy of the old one in " + BackupStore.Root + " (button 'Backups…' to restore it).\n\nSave into BMS?",
                "Save into BMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return false;
            _bmsWriteOk = true;
        }
        _lastBackup = BackupStore.Save(full);
        return true;
    }

    private static readonly string[] Companions = { ".twx", ".iff", ".fmap", ".ini", ".l16.txtpb", ".tri", ".frc", ".his", ".skn", ".run" };

    /// <summary>
    /// Salva con un altro nome o in un'altra cartella (mai dentro BMS). I file interni dell'archivio prendono il nuovo nome
    /// (BMS li cerca con il nome del file), e i file compagni accanto all'originale (.twx, .iff, .fmap ...) vengono copiati col nuovo nome.
    /// </summary>
    private bool SaveAs()
    {
        if (_doc == null) return false;
        string oldPath = _doc.Path, ext = Path.GetExtension(oldPath);
        Directory.CreateDirectory(_ws.EditsFolder);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save As", FileName = Path.GetFileName(oldPath), DefaultExt = ext,
            Filter = ext.Equals(".tac", StringComparison.OrdinalIgnoreCase) ? "TE mission (*.tac)|*.tac" : "Campaign (*.cam)|*.cam",
            InitialDirectory = Path.GetDirectoryName(oldPath) is { } od && Directory.Exists(od) ? od : _ws.EditsFolder, AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return false;
        string dst = dlg.FileName;
        try
        {
            if (!PrepareWrite(dst)) return false;
            _doc.Save(dst);
            int comp = CopyCompanions(oldPath, dst);
            bool inEdits = string.Equals(Path.GetDirectoryName(Path.GetFullPath(dst))?.TrimEnd('\\'), Path.GetFullPath(_ws.EditsFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (inEdits && InstallCampaignDir != null)
            {
                Directory.CreateDirectory(InstallCampaignDir);
                string ic = Path.Combine(InstallCampaignDir, Path.GetFileName(dst));
                File.Copy(dst, ic, true);
                CopyCompanions(oldPath, ic);
            }
            _doc.Path = dst;
            Title = $"Campaign editor – {_ws.Name} – {Path.GetFileName(dst)}";
            _status.Text = $"Saved {dst}" + (comp > 0 ? $" and {comp} companion file(s)" : "") + (inEdits && InstallCampaignDir != null ? " (and the Install copy)" : "")
                           + (_lastBackup != null ? $"; previous version backed up in {_lastBackup}." : ".");
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save As", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    /// <summary>File compagni del vecchio nome (stessa cartella dell'originale o cartelle del teatro) copiati accanto a dst col nuovo nome, se non ci sono gia'.</summary>
    private int CopyCompanions(string src, string dst)
    {
        string ob = Path.GetFileNameWithoutExtension(src), nb = Path.GetFileNameWithoutExtension(dst), dd = Path.GetDirectoryName(dst)!;
        var dirs = new[] { Path.GetDirectoryName(src)! }.Concat(_ws.SearchDirs()).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int n = 0;
        foreach (var e in Companions)
        {
            string to = Path.Combine(dd, nb + e);
            if (File.Exists(to)) continue;
            string? from = dirs.Select(d => Path.Combine(d, ob + e)).FirstOrDefault(File.Exists);
            if (from == null || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(from, to); n++;
        }
        return n;
    }

    /// <summary>Pacchetto: i voli (cliccabili). Volo: aereo, missione, callsign e la tabella dei waypoint.</summary>
    private void ShowRoute(CampUnit u)
    {
        var d = _doc!;
        if (u.Kind == UnitKind.Package)
        {
            _props.Children.Add(Sub("Flights (route on the map)"));
            foreach (var e in u.Elements)
                if (d.Unit(e) is { } f) _props.Children.Add(LinkButton(FlightText(f), () => Select(null, f, true)));
                else _props.Children.Add(Line($"missing flight {e}"));
            return;
        }
        _props.Children.Add(Line(FlightText(u) + $"   ·   callsign {u.CallsignId}-{u.CallsignNum}"));
        if (PackageOf(u) is { } p && p != u) _props.Children.Add(LinkButton($"Package {p.CampId} (all its flights)", () => Select(null, p, true)));
        if (u.Kind == UnitKind.Flight) _props.Children.Add(LinkButton("Loadout…", () => OpenLoadout(u)));
        if (u.Waypoints.Count == 0) { _props.Children.Add(Note("No route.")); return; }
        _props.Children.Add(Sub($"Route ({u.Waypoints.Count} steerpoints)"));
        var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var w in new[] { 24.0, 0, 70, 100 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Cell(int r, int c, string t, bool b = false)
        {
            var tb = new TextBlock { Text = t, FontWeight = b ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 1, 4, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(tb, r); Grid.SetColumn(tb, c); g.Children.Add(tb);
        }
        g.RowDefinitions.Add(new RowDefinition());
        Cell(0, 0, "#", true); Cell(0, 1, "Action", true); Cell(0, 2, "Altitude", true); Cell(0, 3, "Arrive", true);
        for (int i = 0; i < u.Waypoints.Count; i++)
        {
            var w = u.Waypoints[i];
            g.RowDefinitions.Add(new RowDefinition());
            string act = w.ActionName + (w.IsTarget ? " (target)" : w.IsIp ? " (IP)" : "");
            if (d.Obj(w.Target) is { } to) act += " → " + to.Name;
            Cell(i + 1, 0, (i + 1).ToString(CultureInfo.InvariantCulture)); Cell(i + 1, 1, act);
            if (u.Kind == UnitKind.Flight && w.Action is not (1 or 7) && i < u.WpAt.Length)
            {
                int wi = i;
                var box = new TextBox
                {
                    Text = w.AltitudeFt.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 1, 4, 1), Padding = new Thickness(2, 0, 2, 0),
                    ToolTip = "Altitude of this steerpoint in feet (e.g. 500 for a low attack on ships). Enter or leave the box to apply; Ctrl+Z undoes.",
                };
                void Apply()
                {
                    string t = box.Text.Trim().Replace(",", "").Replace(".", "").Replace("ft", "").Trim();
                    int now = u.Waypoints[wi].AltitudeFt;
                    if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ft) || ft < 0 || ft > 80000)
                    { box.Text = now.ToString(CultureInfo.InvariantCulture); _status.Text = "Altitude: a number of feet from 0 to 80,000."; return; }
                    ft = (int)Math.Round(ft / 10.0) * 10;
                    if (ft == now) { box.Text = now.ToString(CultureInfo.InvariantCulture); return; }
                    d.Units!.SetWaypointAltitude(u, wi, ft);
                    _undo.Push(() => { d.Units!.SetWaypointAltitude(u, wi, now); RefreshProps(); _map.InvalidateVisual(); });
                    MarkDirty(); _map.InvalidateVisual();
                    box.Text = ft.ToString(CultureInfo.InvariantCulture);
                    _status.Text = $"Steerpoint {wi + 1}: altitude {now:N0} → {ft:N0} ft.";
                }
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
                box.LostKeyboardFocus += (_, _) => Apply();
                Grid.SetRow(box, i + 1); Grid.SetColumn(box, 2); g.Children.Add(box);
            }
            else Cell(i + 1, 2, w.AltitudeFt > 0 ? w.AltitudeFt.ToString("N0", CultureInfo.InvariantCulture) + " ft" : "ground");
            Cell(i + 1, 3, CampWaypoint.Time(w.Arrive));
        }
        _props.Children.Add(g);
        _props.Children.Add(Note(u.Kind == UnitKind.Flight
            ? "Drag a steerpoint on the map to move it (not takeoff and landing): the speed of each leg stays the same, so its time and the times after it change, TOT included. Altitudes in feet: type a new one in the box. Ctrl+Z undoes."
            : "Select a flight of the package to move its steerpoints on the map."));
    }

    // bandiere delle squadre (Art del teatro, poi BMS), lette una volta sola
    private Dictionary<int, BitmapSource>? _flags;
    private Dictionary<int, BitmapSource> Flags
    {
        get
        {
            if (_flags != null) return _flags;
            _flags = new Dictionary<int, BitmapSource>();
            try
            {
                foreach (var kv in TeamFlags.Load(_ws.ArtDirs))
                {
                    var bs = BitmapSource.Create(kv.Value.Width, kv.Value.Height, 96, 96, PixelFormats.Bgra32, null, kv.Value.Bgra, kv.Value.Width * 4);
                    bs.Freeze();
                    _flags[kv.Key] = bs;
                }
            }
            catch (Exception) { }
            return _flags;
        }
    }

    /// <summary>Bandiera della squadra in piccolo (26x15); senza immagine, il quadratino del colore della mappa.</summary>
    private FrameworkElement FlagBox(int flag, byte color, double w = 26, double h = 15)
    {
        if (Flags.TryGetValue(flag, out var img))
            return new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = img, Width = w, Height = h, Stretch = Stretch.Fill },
            };
        return new Border { Width = 14, Height = 14, Background = new SolidColorBrush(CampaignMap.TeamColor(color)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    // ---- dimensione della finestra: ingrandita la prima volta, poi come l'ha lasciata l'utente ----
    private static string PlacementFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMS Suite", "CampaignEditor", "window.txt");

    private void RestoreWindowPlacement()
    {
        WindowState = WindowState.Maximized;
        try
        {
            if (!System.IO.File.Exists(PlacementFile)) return;
            var v = System.IO.File.ReadAllText(PlacementFile).Split(';');
            if (v.Length < 5 || v[0] != "normal") return;
            var ci = CultureInfo.InvariantCulture;
            double l = double.Parse(v[1], ci), t = double.Parse(v[2], ci), w = double.Parse(v[3], ci), h = double.Parse(v[4], ci);
            // deve stare sullo schermo di adesso (un secondo monitor che non c'e' piu': si torna ingranditi)
            var desk = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (w < MinWidth || h < MinHeight || !desk.IntersectsWith(new Rect(l + 50, t + 10, Math.Max(1, w - 100), 40))) return;
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t; Width = Math.Min(w, desk.Width); Height = Math.Min(h, desk.Height);
        }
        catch (Exception) { WindowState = WindowState.Maximized; }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;
            var r = RestoreBounds;
            string txt = WindowState == WindowState.Maximized || r.IsEmpty
                ? "maximized"
                : string.Join(";", "normal", Left.ToString(ci), Top.ToString(ci), ActualWidth.ToString(ci), ActualHeight.ToString(ci));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlacementFile)!);
            System.IO.File.WriteAllText(PlacementFile, txt);
        }
        catch (Exception) { }
    }

    private bool _politicalLoading;

    /// <summary>Sfondo della mappa: 0 texture del terreno, 1 mappa politica (caricata la prima volta in background), 2 niente.</summary>
    private void SetBackground(int mode)
    {
        _map.ShowPhoto = mode == 0;
        _map.ShowPolitical = mode == 1;
        _map.InvalidateVisual();
        if (mode != 1 || _map.Political != null || _politicalLoading) return;
        _politicalLoading = true;
        _status.Text = "Reading the political map (Natural Earth)...";
        var grid = _grid;
        System.Threading.Tasks.Task.Run(() =>
        {
            var m = TerrainTool.Core.Theater.PoliticalMap.Load(grid, out var err);
            Dispatcher.BeginInvoke(() =>
            {
                _politicalLoading = false;
                _map.Political = m;
                _status.Text = m == null ? "Political map not available: " + err
                    : $"Political map: {m.Land.Count} land areas, {m.Borders.Count} border lines, {m.Labels.Count} states (Natural Earth, public domain)";
                _map.InvalidateVisual();
            });
        });
    }

    private IEnumerable<CampTeam> TeamsInUse() =>
        _doc?.Teams?.Teams.Where(t => _doc.Objectives.Any(o => o.Owner == t.Index) || _doc.Units?.Units.Any(u => u.Owner == t.Index) == true) ?? Enumerable.Empty<CampTeam>();

    private void FillLegend()
    {
        string vp = _doc?.Teams != null && _map.Viewpoint >= 0 && _map.Viewpoint < _doc.Teams.Teams.Count ? _doc.Teams.Teams[_map.Viewpoint].ToString() : "";
        _legend.Fill(TeamsInUse().Select(t => (t.ToString(), CampaignMap.TeamColor(t.Color), Flags.TryGetValue(t.Flag, out var f) ? (ImageSource?)f : null, _map.Aff(t.Index))),
                     _map.NatoIcons, vp);
    }

    /// <summary>Menu del punto di vista (stile NATO): le squadre in uso con la bandiera; di default la prima.</summary>
    private void FillViewpoint()
    {
        int keep = _map.Viewpoint;
        _viewpoint.Items.Clear();
        var teams = TeamsInUse().ToList();
        foreach (var t in teams)
        {
            var it = new StackPanel { Orientation = Orientation.Horizontal, Tag = t.Index };
            it.Children.Add(FlagBox(t.Flag, t.Color, 20, 12));
            it.Children.Add(new TextBlock { Text = t.ToString(), VerticalAlignment = VerticalAlignment.Center });
            _viewpoint.Items.Add(it);
        }
        int idx = teams.FindIndex(t => t.Index == keep);
        _viewpoint.SelectedIndex = idx >= 0 ? idx : teams.Count > 0 ? 0 : -1;
    }

    private void FillTeams()
    {
        FillViewpoint();
        FillLegend();
        _teamList.Items.Clear();
        if (_doc?.Teams == null) return;
        foreach (var t in _doc.Teams.Teams)
        {
            int objs = _doc.Objectives.Count(o => o.Owner == t.Index), units = _doc.Units?.Units.Count(u => u.Owner == t.Index) ?? 0;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(FlagBox(t.Flag, t.Color));
            sp.Children.Add(new TextBlock { Text = $"{t.Index}  {t}   ·  {objs} objectives, {units} units", VerticalAlignment = VerticalAlignment.Center });
            _teamList.Items.Add(new ListBoxItem { Content = sp, Tag = t });
        }
    }

    // ───────────────────────── selezione e proprieta' ─────────────────────────

    private CampObjective? _selObj;
    private CampUnit? _selUnit;

    private void Select(CampObjective? o, CampUnit? u, bool center)
    {
        _selObj = o; _selUnit = u;
        var pkg = PackageOf(u);
        _map.RoutePackage = pkg;
        _map.Select(o, u, center && pkg == null);
        if (center && pkg != null) _map.ZoomToPackage(pkg);
        _syncing = true;
        if (o != null && _objList.ItemsSource is List<ObjItem> l) { var it = l.FirstOrDefault(x => x.O == o); _objList.SelectedItem = it; if (it != null) _objList.ScrollIntoView(it); }
        if (u != null && u.Kind is UnitKind.Flight or UnitKind.Package or UnitKind.Squadron)
            foreach (var tv in new[] { _pkgTree, _sqnTree })
                if ((tv.SelectedItem as TreeViewItem)?.Tag != u) SelectInTree(tv, u);
        _syncing = false;
        RefreshProps();
        _unitTableWin?.Refresh();
    }

    private void RefreshProps()
    {
        _props.Children.Clear();
        if (_doc == null) return;
        if (_selObj != null) ShowObjective(_selObj);
        else if (_selUnit != null) ShowUnit(_selUnit);
        // selezionando qualcosa si torna alle proprieta'
        if ((_selObj != null || _selUnit != null) && _propsTab != null) _propsTab.IsSelected = true;
    }

    private static TextBlock Head(string t) => new() { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
    private static TextBlock Line(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
    private static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4) };
    private static TextBlock Sub(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };

    private FrameworkElement Row(string label, UIElement c)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var l = new TextBlock { Text = label, Width = 95, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left);
        dp.Children.Add(l); dp.Children.Add(c);
        return dp;
    }

    private Button LinkButton(string text, Action a)
    {
        var b = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        b.Click += (_, _) => a();
        return b;
    }

    private ComboBox TeamCombo(int sel, Action<byte> set)
    {
        var cb = new ComboBox();
        int n = _doc?.Teams?.Teams.Count ?? 8;
        for (int i = 0; i < n; i++) cb.Items.Add($"{i}  {CampaignValidator.TeamName(_doc!, i)}");
        cb.SelectedIndex = sel < n ? sel : -1;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) set((byte)cb.SelectedIndex); };
        return cb;
    }

    private (double Lat, double Lon) LatLon(double eKm, double nKm) => _grid.ToLatLon(eKm * 1000, nKm * 1000);

    private void ShowObjective(CampObjective o)
    {
        var d = _doc!;
        int type = _db.ObjectiveType(o.Type);
        var (e, n) = CampaignMap.Pos(o);
        var (lat, lon) = LatLon(e, n);
        _props.Children.Add(Head(o.Name));
        _props.Children.Add(Line($"{EditorData.TypeName(type)}   ·   CampId {o.CampId}   ·   VU {o.Id}"));
        _props.Children.Add(Line($"Position {e:0.00} km E, {n:0.00} km N  (grid {o.X}, {o.Y})"));
        _props.Children.Add(Line($"{lat:0.00000}, {lon:0.00000}"));
        _props.Children.Add(Row("Owner", TeamCombo(o.Owner, t =>
        {
            byte old = o.Owner;
            if (old == t) return;
            d.SetOwner(o, t); _undo.Push(() => d.SetOwner(o, old));
            MarkDirty(); _map.InvalidateVisual();
        })));
        _props.Children.Add(Line($"First owner: {CampaignValidator.TeamName(d, o.FirstOwner)}"));
        var line = new ComboBox { IsEnabled = d.CanEditObjectives };
        foreach (var s in new[] { "—", "1st line (front)", "2nd line", "3rd line" }) line.Items.Add(s);
        line.SelectedIndex = o.Line;
        line.SelectionChanged += (_, _) =>
        {
            int old = o.Line;
            if (line.SelectedIndex == old) return;
            d.SetLine(o, line.SelectedIndex); _undo.Push(() => d.SetLine(o, old));
            MarkDirty(); _map.InvalidateVisual();
        };
        _props.Children.Add(Row("Front line", line));
        _props.Children.Add(Line($"Priority {o.Priority}   ·   features {o.FeatureCount}   ·   flags 0x{o.Flags:X}"));
        if (!o.Parent.IsNull)
        {
            var par = d.Obj(o.Parent);
            _props.Children.Add(Row("Parent", par != null ? LinkButton(par.Name, () => Select(par, null, true)) : Line($"missing {o.Parent}")));
        }
        if (!d.CanEditObjectives)
            _props.Children.Add(Note($"This file has no objectives of its own: they come from {Path.GetFileName(d.ObjectivesFrom ?? d.Scenario)}. " +
                                     "Positions and links are edited there; owners are saved here."));

        _props.Children.Add(Sub($"Links ({o.Links.Count})"));
        foreach (var l in o.Links)
        {
            var b = d.Obj(l.To);
            var dp = new DockPanel();
            if (d.CanEditObjectives && b != null)
            {
                var rm = new Button { Content = "✕", ToolTip = "Remove this link (both ways)", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 1, 0, 1) };
                rm.Click += (_, _) => { RemoveLinkWithUndo(o, b); };
                DockPanel.SetDock(rm, Dock.Right); dp.Children.Add(rm);
            }
            if (b == null) dp.Children.Add(new TextBlock { Text = $"missing objective {l.To}", Foreground = Brushes.Red });
            else
            {
                var (be, bn) = CampaignMap.Pos(b);
                string way = d.Linked(b, o) ? "" : "  (one way!)";
                dp.Children.Add(LinkButton($"{b.Name} – {EditorData.TypeName(_db.ObjectiveType(b.Type))}, {Math.Sqrt((be - e) * (be - e) + (bn - n) * (bn - n)):0} km{way}", () => Select(b, null, true)));
            }
            _props.Children.Add(dp);
        }
        if (d.CanEditObjectives) _props.Children.Add(Note("Link mode (K): click this objective and another one to link them, or to remove their link."));

        if (d.Units != null)
        {
            var here = d.Units.Units.Where(u => u.IsGround && u.AssignedObj == o.Id).ToList();
            var sq = d.Units.Units.Where(u => u.Kind == UnitKind.Squadron && u.Airbase == o.Id).ToList();
            if (here.Count > 0)
            {
                _props.Children.Add(Sub($"Ground units assigned here ({here.Count})"));
                foreach (var u in here) _props.Children.Add(LinkButton($"{UnitLabel(u)} · {CampaignValidator.TeamName(d, u.Owner)}", () => Select(null, u, true)));
            }
            if (sq.Count > 0)
            {
                _props.Children.Add(Sub($"Squadrons based here ({sq.Count})"));
                foreach (var u in sq) _props.Children.Add(LinkButton($"{UnitLabel(u)} · {CampaignValidator.TeamName(d, u.Owner)}", () => Select(null, u, true)));
            }
        }
        AirDefenseSection(o);
    }

    /// <summary>Difese aeree dell'obiettivo: quelle che ha, quante ne puo' ospitare, aggiungerne o toglierne.</summary>
    private void AirDefenseSection(CampObjective o)
    {
        var d = _doc!;
        if (d.Units == null || d.Units.Error != null || d.Teams == null) return;
        if (!ReferenceEquals(_unitDb, _db)) { _props.Children.Add(Note("Air defense: convert the unit types first (this file still uses the old add-on database).")); return; }
        var ad = new AirDefense(d, _db);
        int cap = ad.Capacity(o);
        var here = ad.AdAt(o);
        if (cap == 0 && here.Count == 0) return;
        var l = ad.Lists(o);
        _props.Children.Add(Sub($"Air defense ({here.Count} / {cap})"));
        if (ad.ObjType(o) is 1 or 2) _props.Children.Add(Note($"Layout: {l[4]} SAM and {l[6]} AAA point lists."));
        foreach (var u in here)
        {
            var dp = new DockPanel();
            var rm = new Button { Content = "✕", ToolTip = "Delete this air defense battalion", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 1, 0, 1) };
            var uu = u;
            rm.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Delete {UnitLabel(uu)}?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var rec = d.Units!.Data.AsSpan(uu.Start, uu.End - uu.Start).ToArray();
                d.Units.Remove(uu); d.Reindex(); MarkDirty(); FillUnits(); _map.InvalidateVisual(); RefreshProps();
                _undo.Push(() => { d.Units!.Restore(rec, uu); d.Reindex(); FillUnits(); });
            };
            DockPanel.SetDock(rm, Dock.Right); dp.Children.Add(rm);
            dp.Children.Add(LinkButton($"{UnitLabel(u)} · {CampaignValidator.TeamName(d, u.Owner)}", () => Select(null, uu, true)));
            _props.Children.Add(dp);
        }
        if (cap > 0)
        {
            var types = new ComboBox { Margin = new Thickness(0, 2, 4, 2) };
            var used = ad.AdUnits(o.Owner).Select(u => u.Type - 100).ToHashSet();
            foreach (var t in _db.AirDefenseTypes().OrderByDescending(t => used.Contains(t)).ThenByDescending(t => _db.UcdOf(t)?.RngAir ?? 0))
            {
                bool fits = AirDefense.TypeFit(ad.ClassOf(t), ad.ObjType(o)) > 0;
                types.Items.Add(new ComboBoxItem { Content = $"{_db.UnitTypeName(t + 100)} – {AirDefense.ClassName(ad.ClassOf(t))} {_db.UcdOf(t)?.RngAir ?? 0} km" + (used.Contains(t) ? " · team uses it" : "") + (fits ? "" : " · not usual here"), Tag = t, Foreground = fits ? Brushes.Black : Brushes.Gray });
            }
            types.SelectedIndex = 0;
            var add = new Button { Content = "Add", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 0, 2), IsEnabled = here.Count < cap,
                ToolTip = here.Count < cap ? "New air defense battalion of the objective's team, placed on it with orders Air defense" : "No free SAM/AAA place on this objective" };
            add.Click += (_, _) =>
            {
                if (types.SelectedItem is not ComboBoxItem { Tag: int t }) return;
                var nu = ad.AddUnit(t, o);
                if (nu == null) { _status.Text = "No battalion in this file to copy the record from."; return; }
                _undo.Push(() => { d.Units!.Remove(nu); d.Reindex(); FillUnits(); });
                MarkDirty(); FillUnits(); _map.InvalidateVisual(); RefreshProps();
            };
            var row = new DockPanel();
            DockPanel.SetDock(add, Dock.Right); row.Children.Add(add); row.Children.Add(types);
            _props.Children.Add(row);
        }
    }

    private void RemoveLinkWithUndo(CampObjective a, CampObjective b)
    {
        var d = _doc!;
        var la = a.Links.FirstOrDefault(l => l.To == b.Id); var lb = b.Links.FirstOrDefault(l => l.To == a.Id);
        if (!d.RemoveLink(a, b)) return;
        _undo.Push(() => { if (la != null) a.Links.Add(la); if (lb != null) b.Links.Add(lb); });
        MarkDirty(); _map.InvalidateVisual(); RefreshProps();
    }

    private void ShowUnit(CampUnit u)
    {
        var d = _doc!;
        _props.Children.Add(Head(UnitLabel(u)));
        if (u.Kind is UnitKind.Package or UnitKind.Flight) ShowRoute(u);
        _props.Children.Add(Line($"{u.Kind}   ·   team {CampaignValidator.TeamName(d, u.Owner)}   ·   VU {u.Id}"));
        var (lat, lon) = LatLon(u.X + 0.5, u.Y + 0.5);
        _props.Children.Add(Line($"Grid {u.X}, {u.Y} km   ·   {lat:0.0000}, {lon:0.0000}"));
        if (u.IsGround || u.Kind == UnitKind.TaskForce)
        {
            var ord = new ComboBox();
            for (int i = 0; i <= 11; i++) ord.Items.Add(CampUnit.OrdersName(i));
            ord.SelectedIndex = u.Orders <= 11 ? u.Orders : -1;
            ord.SelectionChanged += (_, _) =>
            {
                byte old = u.Orders;
                if (ord.SelectedIndex < 0 || ord.SelectedIndex == old) return;
                d.Units!.SetOrders(u, (byte)ord.SelectedIndex); _undo.Push(() => d.Units!.SetOrders(u, old));
                MarkDirty();
            };
            _props.Children.Add(Row("Orders", ord));
        }
        if (u.IsGround)
        {
            var div = new TextBox { Text = u.Division.ToString(CultureInfo.InvariantCulture) };
            div.LostFocus += (_, _) =>
            {
                if (!short.TryParse(div.Text, out short v) || v == u.Division) return;
                short old = u.Division;
                d.Units!.SetDivision(u, v); _undo.Push(() => d.Units!.SetDivision(u, old));
                if (u.Kind == UnitKind.Brigade)
                    foreach (var e in u.Elements) if (d.Unit(e) is { } bat) d.Units.SetDivision(bat, v);
                MarkDirty(); FillUnits();
            };
            _props.Children.Add(Row("Division", div));
            _props.Children.Add(Sub("Assigned objective"));
            var ao = d.Obj(u.AssignedObj);
            if (ao != null) _props.Children.Add(LinkButton($"{ao.Name} – {EditorData.TypeName(_db.ObjectiveType(ao.Type))}, {CampaignValidator.TeamName(d, ao.Owner)}", () => Select(ao, null, true)));
            else _props.Children.Add(Line(u.AssignedObj.IsNull ? "none" : $"missing objective {u.AssignedObj}"));
            var pick = new Button { Content = "Pick on the map…", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0), ToolTip = "Then click an objective (or Ctrl+click it in any mode)" };
            pick.Click += (_, _) => _modeAssign.IsChecked = true;
            _props.Children.Add(pick);
        }
        if (u.Kind == UnitKind.Battalion)
        {
            var br = d.Unit(u.Parent);
            _props.Children.Add(Sub("Brigade"));
            _props.Children.Add(br != null ? LinkButton(UnitLabel(br), () => Select(null, br, true)) : Line(u.Parent.IsNull ? "independent" : $"missing {u.Parent}"));
            _props.Children.Add(Line($"Supply {u.Supply}  ·  fatigue {u.Fatigue}  ·  morale {u.Morale}"));
            if (ReferenceEquals(_unitDb, _db))
            {
                var mv = new ComboBox { ToolTip = "Put the battalion in a brigade of its team (it moves to the brigade's objective, takes its division and a Korea-style number) or make it independent" };
                mv.Items.Add(new ComboBoxItem { Content = "(independent)" });
                foreach (var b in d.Units!.Units.Where(x => x.Kind == UnitKind.Brigade && x.Owner == u.Owner).OrderBy(x => x.Division).ThenBy(x => x.NameId))
                    mv.Items.Add(new ComboBoxItem { Content = $"{UnitLabel(b)} – {d.Obj(b.AssignedObj)?.Name ?? "no objective"}", Tag = b });
                mv.SelectedItem = mv.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag == br) ?? mv.Items[0];
                mv.SelectionChanged += (_, _) =>
                {
                    var target = (mv.SelectedItem as ComboBoxItem)?.Tag as CampUnit;
                    if (target == br) return;
                    var (undo, err) = new UnitEditor(d, _db).SetBrigade(u, target);
                    if (err != null) { MessageBox.Show(this, err, Title); return; }
                    UnitsChanged(undo!, u);
                };
                _props.Children.Add(Row("Move to", mv));
            }
        }
        if (u.Kind == UnitKind.Brigade)
        {
            _props.Children.Add(Sub($"Battalions ({u.Elements.Count})"));
            foreach (var e in u.Elements)
                _props.Children.Add(d.Unit(e) is { } b ? LinkButton(UnitLabel(b), () => Select(null, b, true)) : Line($"missing {e}"));
        }
        if (u.Kind == UnitKind.Squadron)
        {
            var ab = d.Obj(u.Airbase);
            _props.Children.Add(Sub("Airbase"));
            _props.Children.Add(ab != null ? LinkButton($"{ab.Name} ({CampaignValidator.TeamName(d, ab.Owner)})", () => Select(ab, null, true)) : Line($"{u.Airbase}"));
            if (ReferenceEquals(_unitDb, _db))
            {
                var sw = new Button { Content = "Squadron window…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "As in Mission Commander: aircraft, base, roster, stores (Resupply), pilots, specialty, camp specific role, delete" };
                sw.Click += (_, _) => OpenUnitList(u);
                _props.Children.Add(sw);
            }
        }
        if (u.IsGround && ReferenceEquals(_unitDb, _db))
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var win = new Button { Content = u.Kind == UnitKind.Brigade ? "Brigade window…" : "Battalion window…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold,
                ToolTip = "All the fields of the unit, as in Mission Commander: roster, morale, supply, flags, place at objective, battalions of the brigade…" };
            win.Click += (_, _) => OpenUnitList(u);
            bar.Children.Add(win);
            var tbl = new Button { Content = u.Kind == UnitKind.Brigade ? "Add battalions / change type…" : "Change type…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Opens the battalions & brigades table with this unit selected" };
            tbl.Click += (_, _) => OpenUnitTable();
            var del = new Button { Content = u.Kind == UnitKind.Brigade ? $"Remove brigade and its {u.Elements.Count} battalions" : "Remove battalion", Padding = new Thickness(8, 2, 8, 2), Foreground = Brushes.DarkRed };
            del.Click += (_, _) =>
            {
                var (undo, err) = new UnitEditor(d, _db).Delete(u);
                if (err != null) { MessageBox.Show(this, err, Title); return; }
                UnitsChanged(undo!, null);
            };
            bar.Children.Add(tbl); bar.Children.Add(del);
            _props.Children.Add(bar);
        }
        _props.Children.Add(Note("Drag the unit on the map to move it. Ctrl+Z = undo."));
    }

    private bool _bothWays = true;

    private static Brush StanceBrush(int s) => s switch
    {
        1 => new SolidColorBrush(Color.FromRgb(60, 170, 60)), 2 => new SolidColorBrush(Color.FromRgb(140, 200, 120)),
        3 => new SolidColorBrush(Color.FromRgb(170, 170, 170)), 4 => new SolidColorBrush(Color.FromRgb(240, 150, 40)),
        5 => new SolidColorBrush(Color.FromRgb(220, 40, 40)), _ => Brushes.Transparent,
    };

    private void ShowTeam(CampTeam t)
    {
        var d = _doc!;
        var T = d.Teams!.Teams;
        _selObj = null; _selUnit = null; _map.Select(null, null, false);
        _props.Children.Clear();
        _props.Children.Add(Head($"Team {t.Index}: {t}"));

        var name = new TextBox { Text = t.Name, MaxLength = 19, ToolTip = "Team name (max 19 characters): written in the team file and in the campaign header" };
        name.LostFocus += (_, _) =>
        {
            if (name.Text == t.Name) return;
            string old = t.Name;
            d.SetTeamName(t, name.Text); _undo.Push(() => d.SetTeamName(t, old));
            MarkDirty(); FillTeams(); RefillTeamCombos();
        };
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        _props.Children.Add(Row("Name", name));
        // bandiera: le immagini del teatro (i posti 1-7 di BMS); un numero diverso, se il file lo ha, resta scelto come "Flag n"
        var flag = new ComboBox { ToolTip = "Flag of the team, from the theater's art (Art\\resource\\Main.rsc). The theater decides which country each picture is." };
        var flagIds = Enumerable.Range(0, TeamFlags.Slots.Length).Select(i => (byte)i).ToList();
        if (!flagIds.Contains(t.Flag)) flagIds.Add(t.Flag);
        foreach (byte i in flagIds)
        {
            var it = new StackPanel { Orientation = Orientation.Horizontal, Tag = i };
            if (Flags.ContainsKey(i)) it.Children.Add(FlagBox(i, t.Color));
            it.Children.Add(new TextBlock { Text = i == 0 ? "0  (none)" : $"{i}", VerticalAlignment = VerticalAlignment.Center });
            flag.Items.Add(it);
        }
        flag.SelectedIndex = flagIds.IndexOf(t.Flag);
        flag.SelectionChanged += (_, _) =>
        {
            if (flag.SelectedItem is not StackPanel { Tag: byte v } || v == t.Flag) return;
            byte old = t.Flag; d.SetTeamFlag(t, v);
            _undo.Push(() => { d.SetTeamFlag(t, old); FillTeams(); });
            MarkDirty(); FillTeams();
        };
        _props.Children.Add(Row("Flag", flag));
        var col = new ComboBox();
        foreach (var c in new[] { "White", "Green", "Blue", "Brown", "Orange", "Yellow", "Red", "Gray" }) col.Items.Add(c);
        col.SelectedIndex = t.Color < 8 ? t.Color : -1;
        col.SelectionChanged += (_, _) => { if (col.SelectedIndex >= 0 && col.SelectedIndex != t.Color) { byte old = t.Color; d.SetTeamColor(t, (byte)col.SelectedIndex); _undo.Push(() => d.SetTeamColor(t, old)); MarkDirty(); FillTeams(); _map.InvalidateVisual(); } };
        _props.Children.Add(Row("Map colour", col));

        // satelliti: la squadra vede tutte le unita' nemiche sulla mappa 2D (flag TEAM HAS SATS di Mission Commander)
        if (d.Teams != null)
        {
            var sats = new CheckBox
            {
                Content = "Has satellites: sees every enemy unit on the 2D map", IsChecked = (t.TeamFlags & TeamFile.HasSats) != 0,
                ToolTip = "The team flag of Mission Commander (TEAM HAS SATS). In a BMS 4.38 TE it did not show the enemy units on the 2D map by itself: use 'Show all enemy units' below for that.",
                Margin = new Thickness(0, 2, 0, 2),
            };
            sats.Click += (_, _) =>
            {
                short old = t.TeamFlags;
                short now = sats.IsChecked == true ? (short)(old | TeamFile.HasSats) : (short)(old & ~TeamFile.HasSats);
                if (now == old) return;
                d.Teams!.SetTeamFlags(t, now);
                _undo.Push(() => { d.Teams!.SetTeamFlags(t, old); ShowTeam(t); });
                MarkDirty();
                _status.Text = $"{t.Name}: satellites {(now != old && (now & TeamFile.HasSats) != 0 ? "on" : "off")}.";
            };
            _props.Children.Add(Row("Intelligence", sats));
        }
        // unita' nemiche gia' "viste" all'inizio: il bit della squadra (e delle squadre del suo schieramento) nel campo spotted
        // di ogni unita' dell'altro schieramento, con l'ora della TE. Cosi' le vede sulla mappa 2D prima di decollare.
        if (d.Units != null && d.Teams != null)
        {
            var ourSide = d.Teams.Teams.Where(x => x.CTeam == t.CTeam).Select(x => x.Index).Append(t.Index).Distinct().ToList();
            short bits = 0; foreach (int i in ourSide) bits |= (short)(1 << i);
            bool Enemy(CampUnit u) => u.Kind is UnitKind.Battalion or UnitKind.Brigade or UnitKind.TaskForce or UnitKind.Flight or UnitKind.Package or UnitKind.Squadron
                                      && u.Owner < d.Teams.Teams.Count && d.Teams.Teams[u.Owner].CTeam != t.CTeam;
            var enemies = d.Units.Units.Where(Enemy).ToList();
            int seen = enemies.Count(u => (d.Units.Spotted(u) & bits) == bits);
            var bar = new WrapPanel();
            var spot = new Button { Content = $"Show all {enemies.Count} enemy units", Margin = new Thickness(0, 2, 6, 2),
                ToolTip = "Marks every unit of the other side as already spotted by this team (and the teams on its side), at the time of this file: its pilots see them on the 2D map from the start. Ctrl+Z undoes." };
            var hide = new Button { Content = "Hide them", Margin = new Thickness(0, 2, 6, 2),
                ToolTip = "Removes this side's spotted mark from every enemy unit: they appear on the map only once found in the mission. Ctrl+Z undoes." };
            void Mark(bool show)
            {
                var old = enemies.Select(u => (u, d.Units.Spotted(u), d.Units.SpotTime(u))).ToList();
                uint now = d.CurrentTime;
                foreach (var u in enemies)
                {
                    short m = d.Units.Spotted(u);
                    short nm = show ? (short)(m | bits) : (short)(m & ~bits);
                    if (nm != m) d.Units.SetSpotted(u, nm, show ? now : d.Units.SpotTime(u));
                }
                _undo.Push(() => { foreach (var (u, m, st) in old) d.Units!.SetSpotted(u, m, st); ShowTeam(t); });
                MarkDirty(); ShowTeam(t);
                _status.Text = $"{t.Name}: {(show ? "all enemy units spotted" : "enemy units hidden")} ({enemies.Count}).";
            }
            spot.Click += (_, _) => Mark(true);
            hide.Click += (_, _) => Mark(false);
            bar.Children.Add(spot); bar.Children.Add(hide);
            bar.Children.Add(new TextBlock { Text = $"{seen} of {enemies.Count} already spotted", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            _props.Children.Add(Row("Enemy units", bar));
        }

        // produttore dei mezzi (come in Korea: USA/Giappone/NATO occidentale, ROK sudcoreano, CIS sovietico, PRC cinese, DPRK nordcoreano)
        var eq = new ComboBox { ToolTip = "Who builds this team's equipment (the old Western / Soviet division). Korea: U.S., Japan, NATO = US/Western; ROK = South Korean; CIS = Soviet/Russian; PRC = Chinese; DPRK = North Korean." };
        foreach (var n in Producer.EquipmentNames) eq.Items.Add(n);
        eq.SelectedIndex = t.Equipment < Producer.EquipmentNames.Length ? t.Equipment : -1;
        eq.SelectionChanged += (_, _) =>
        {
            if (eq.SelectedIndex < 0 || eq.SelectedIndex == t.Equipment) return;
            byte old = t.Equipment;
            d.Teams!.SetEquipment(t, (byte)eq.SelectedIndex); _undo.Push(() => d.Teams!.SetEquipment(t, old));
            MarkDirty(); ShowTeam(t);
        };
        _props.Children.Add(Row("Equipment", eq));
        {
            // che mezzi ha davvero la squadra (dal nome dei mezzi delle sue unita')
            var cnt = new Dictionary<Bloc, int>();
            foreach (var u in d.Units?.Units.Where(u => u.Owner == t.Index && u.Kind is UnitKind.Battalion or UnitKind.Squadron) ?? Enumerable.Empty<CampUnit>())
            {
                var b = Producer.OfName(_unitDb.UnitVehicles(u.Type - 100).FirstOrDefault());
                if (b != Bloc.Unknown) cnt[b] = cnt.GetValueOrDefault(b) + 1;
            }
            if (cnt.Count > 0)
            {
                var want = Producer.OfEquipment(t.Equipment);
                string txt = "Its battalions and squadrons: " + string.Join(", ", cnt.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {Producer.Short(k.Key)}"));
                var top = cnt.MaxBy(k => k.Value).Key;
                if (want != Bloc.Unknown && top != want) txt += $"  –  mostly {Producer.Short(top)}, but the equipment is set to {Producer.EquipmentNames[t.Equipment]}.";
                _props.Children.Add(Note(txt));
            }
        }

        // schieramento: con chi combatte (cteam); i membri si ricalcolano da soli
        var side = new ComboBox { ToolTip = "The side this team fights on (cteam). Teams on the same side share the war: e.g. Kuwait and Saudi Arabia on the Coalition side." };
        for (int i = 0; i < T.Count; i++) side.Items.Add($"{i}  {CampaignValidator.TeamName(d, i)}");
        side.SelectedIndex = t.CTeam < T.Count ? t.CTeam : -1;
        side.SelectionChanged += (_, _) =>
        {
            if (side.SelectedIndex < 0 || side.SelectedIndex == t.CTeam) return;
            byte old = t.CTeam;
            d.SetSide(t, (byte)side.SelectedIndex); _undo.Push(() => d.SetSide(t, old));
            MarkDirty(); ShowTeam(t);
        };
        _props.Children.Add(Row("Fights with", side));
        var mem = Enumerable.Range(0, T.Count).Where(k => t.Member[k] != 0).Select(k => CampaignValidator.TeamName(d, k)).ToList();
        _props.Children.Add(Note(mem.Count > 0 ? "Side members: " + string.Join(", ", mem) : "No team fights on this team's side."));

        _props.Children.Add(Sub("Relations of " + t));
        var both = new CheckBox { Content = "Same relation both ways", IsChecked = _bothWays, Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "When you set A → B, B → A gets the same relation (recommended: the campaign AI reads both)" };
        both.Click += (_, _) => _bothWays = both.IsChecked == true;
        _props.Children.Add(both);
        for (int i = 1; i < T.Count; i++)
        {
            if (i == t.Index) continue;
            int other = i;
            var cb = new ComboBox();
            foreach (var sn in TeamFile.StanceNames) cb.Items.Add(sn);
            cb.SelectedIndex = t.Stance[i] >= 0 && t.Stance[i] < TeamFile.StanceNames.Length ? t.Stance[i] : -1;
            cb.Background = StanceBrush(t.Stance[i]);
            cb.SelectionChanged += (_, _) =>
            {
                if (cb.SelectedIndex < 0 || cb.SelectedIndex == t.Stance[other]) return;
                short oldA = t.Stance[other], oldB = T[other].Stance[t.Index];
                bool bw = _bothWays;
                d.SetStance(t, other, (short)cb.SelectedIndex, bw);
                _undo.Push(() => { d.SetStance(t, other, oldA, false); if (bw) d.SetStance(T[other], t.Index, oldB, false); });
                MarkDirty(); ShowTeam(t);
            };
            string back = T[other].Stance[t.Index] != t.Stance[i] ? $"  (they: {CampaignValidator.StanceName(T[other].Stance[t.Index])})" : "";
            var lbl = new TextBlock { Text = CampaignValidator.TeamName(d, i) + back, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (back.Length > 0) lbl.Foreground = Brushes.DarkOrange;
            var dp = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(lbl, Dock.Left); dp.Children.Add(lbl); dp.Children.Add(cb);
            _props.Children.Add(dp);
        }

        // tabella di tutti i rapporti (riga = chi, colonna = verso chi)
        _props.Children.Add(Sub("All relations (row → column)"));
        var g = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 1; i < T.Count; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        for (int r = 0; r < T.Count; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 1; i < T.Count; i++)
        {
            var h = new TextBlock { Text = i.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center, ToolTip = CampaignValidator.TeamName(d, i) };
            Grid.SetColumn(h, i); Grid.SetRow(h, 0); g.Children.Add(h);
            var rl = new TextBlock { Text = $"{i} {CampaignValidator.TeamName(d, i)}", Margin = new Thickness(0, 0, 6, 0), FontWeight = i == t.Index ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetColumn(rl, 0); Grid.SetRow(rl, i); g.Children.Add(rl);
            for (int j = 1; j < T.Count; j++)
            {
                int s = T[i].Stance[j];
                var cell = new Border { Background = i == j ? Brushes.Black : StanceBrush(s), Margin = new Thickness(1), Height = 16,
                    ToolTip = $"{CampaignValidator.TeamName(d, i)} → {CampaignValidator.TeamName(d, j)}: {CampaignValidator.StanceName(s)}" };
                Grid.SetColumn(cell, j); Grid.SetRow(cell, i); g.Children.Add(cell);
            }
        }
        _props.Children.Add(g);
        _props.Children.Add(Note("Green = allied/friendly, grey = neutral, orange = hostile, red = at war. The campaign AI only attacks hostile/at-war teams; " +
                                 "allied teams share airbases and objectives. 'Fights with' groups teams into sides."));
        PrioritySection(t);
    }

    /// <summary>Cartella con i file .pri da confrontare: accanto al file aperto, poi la copia Install, poi il vecchio add-on.</summary>
    private string? PriDir()
    {
        var dirs = new List<string>();
        if (_doc != null) dirs.Add(Path.GetDirectoryName(_doc.Path) ?? "");
        dirs.AddRange(_ws.SearchDirs());
        return dirs.FirstOrDefault(d => d.Length > 0 && File.Exists(Path.Combine(d, "Offense.pri")));
    }

    private List<PriFile> LoadPris(string? dir) =>
        dir == null ? new() : PriFile.Names.Select(n => PriFile.Load(Path.Combine(dir, n + ".pri"))).OfType<PriFile>().ToList();

    /// <summary>
    /// Priorita' della squadra (le stesse dei file .pri): con quale file coincidono, copiarle da un file, modificarle;
    /// prova col marcatore per sapere quale .pri usa davvero BMS.
    /// </summary>
    private void PrioritySection(CampTeam t)
    {
        var d = _doc!;
        var tf = d.Teams!;
        var pris = LoadPris(PriDir());
        _props.Children.Add(Sub("Priorities (what the AI attacks first)"));
        if (pris.Count > 0)
        {
            var m = pris.Select(p => (p, M: p.Match(t))).OrderByDescending(x => x.M.Same).ToList();
            _props.Children.Add(Line("Same as: " + string.Join("  ·  ", m.Select(x => $"{x.p.Name}.pri {x.M.Same}/{x.M.Total}"))));
            if (PriFile.FileOfMarker(t.ObjPriority[PriFile.MarkerType]) is string mk)
                _props.Children.Add(new TextBlock { Text = $"MARKER TEST: this team has the marker of {mk}.pri (beach priority {t.ObjPriority[PriFile.MarkerType]}) → BMS loaded {mk}.pri.",
                    TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkGreen, Margin = new Thickness(0, 4, 0, 4) });
            var pick = new ComboBox { Margin = new Thickness(0, 2, 4, 2) };
            foreach (var p in pris) pick.Items.Add(new ComboBoxItem { Content = $"{p.Name}.pri ({PriFile.ActionOf(p.Name)})", Tag = p });
            pick.SelectedIndex = 0;
            var one = new Button { Content = "Copy to this team", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 2, 4, 2) };
            var all = new Button { Content = "to all", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 2, 0, 2), ToolTip = "Copy into every team" };
            one.Click += (_, _) => { if (pick.SelectedItem is ComboBoxItem { Tag: PriFile p }) { _undo.Push(TeamPriorities.CopyFrom(tf, t, p)); MarkDirty(); ShowTeam(t); } };
            all.Click += (_, _) =>
            {
                if (pick.SelectedItem is not ComboBoxItem { Tag: PriFile p }) return;
                var undos = tf.Teams.Where(x => x.Index > 0).Select(x => TeamPriorities.CopyFrom(tf, x, p)).ToList();
                _undo.Push(() => undos.ForEach(u => u())); MarkDirty(); ShowTeam(t);
            };
            var row = new DockPanel();
            DockPanel.SetDock(all, Dock.Right); row.Children.Add(all);
            DockPanel.SetDock(one, Dock.Right); row.Children.Add(one);
            row.Children.Add(pick);
            _props.Children.Add(row);
        }
        else _props.Children.Add(Note("No .pri files found next to this file, in Output\\Install or in the old add-on."));

        var labels = pris.FirstOrDefault()?.Labels;
        Expander Section(string title, byte[] arr, IEnumerable<int> idx, int sec, Action<int, byte> set)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            int r = 0;
            foreach (int i in idx)
            {
                if (i >= arr.Length) continue;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                string lab = labels != null && labels[sec].TryGetValue(i, out var l) ? l.Replace("TYPE_", "").Replace("STYPE_UNIT_", "").Replace("AMIS_", "") : $"#{i}";
                var tb = new TextBlock { Text = $"{i} {lab}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                var box = new TextBox { Text = arr[i].ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 1, 0, 1) };
                int ii = i;
                box.LostFocus += (_, _) =>
                {
                    if (!byte.TryParse(box.Text, out byte v) || v == arr[ii]) return;
                    byte old = arr[ii]; set(ii, v); _undo.Push(() => set(ii, old)); MarkDirty();
                };
                Grid.SetRow(tb, r); g.Children.Add(tb);
                Grid.SetRow(box, r); Grid.SetColumn(box, 1); g.Children.Add(box);
                r++;
            }
            return new Expander { Header = title, Content = g, Margin = new Thickness(0, 2, 0, 2) };
        }
        IEnumerable<int> Keys(int sec, int n) => labels != null && labels[sec].Count > 0 ? labels[sec].Keys.Where(k => k < n).OrderBy(k => k) : Enumerable.Range(1, n - 1);
        _props.Children.Add(Section("Objective types", t.ObjPriority, Keys(0, 36), 0, (i, v) => TeamPriorities.SetObj(tf, t, i, v)));
        _props.Children.Add(Section("Unit types", t.UnitPriority, Keys(1, 20), 1, (i, v) => TeamPriorities.SetUnit(tf, t, i, v)));
        _props.Children.Add(Section("Missions", t.MissionPriority, Keys(2, t.MissionPriority.Length), 2, (i, v) => TeamPriorities.SetMission(tf, t, i, v)));

        var marker = new Button { Content = "Marker test…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Prepare the 5 .pri files with a different marker each, to find out which one BMS really loads" };
        marker.Click += (_, _) => MarkerTest();
        _props.Children.Add(marker);
    }

    private void MarkerTest()
    {
        if (_p == null || InstallCampaignDir == null) { MessageBox.Show(this, "The marker test works on the project of the new terrain (it writes into Output\\Install).\n\n" +
            "This editor was opened on a BMS theater (read-only for BMS). Open the TerrainTool project instead: " +
            "in the start window choose it under 'TerrainTool projects' (or 'Add TerrainTool project…').", Title); return; }
        string src = Path.Combine(_p.OldTheaterFolder, "Campaign");
        var pris = LoadPris(File.Exists(Path.Combine(src, "Offense.pri")) ? src : null);
        if (pris.Count == 0) { MessageBox.Show(this, "The .pri files of the old add-on were not found in " + src, Title); return; }
        var r = MessageBox.Show(this,
            "Which .pri file does BMS really use? Test with markers:\n\n" +
            "Yes = write the 5 .pri files into Output\\Install\\Campaign with a different BEACH priority each " +
            "(Offense 11, Defense 12, Attrit 13, Intdict 14, Cas 15; beaches do not matter for the air AI).\n" +
            "Then: 'Install campaign files into BMS…' (start window), start the campaign, let it run some hours (until air actions start), save, " +
            "open the save here: the Teams tab says which marker each team has.\n\n" +
            "No = put back the original .pri files (then Install again).\nCancel = nothing.",
            "Marker test", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        try
        {
            Directory.CreateDirectory(InstallCampaignDir);
            foreach (var p in pris)
            {
                string dst = Path.Combine(InstallCampaignDir, Path.GetFileName(p.Path));
                if (r == MessageBoxResult.Yes) { p.Obj[PriFile.MarkerType] = PriFile.MarkerOf(p.Name); p.Save(dst); }
                else File.Copy(p.Path, dst, true);
            }
            _status.Text = r == MessageBoxResult.Yes ? "Marked .pri files written to Output\\Install\\Campaign: now 'Install campaign files into BMS…' (start window) and play." : "Original .pri files put back in Output\\Install\\Campaign: Install again.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Marker test", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RefillTeamCombos()
    {
        if (_doc?.Teams == null) return;
        int sel = _teamOnly.SelectedIndex;
        _teamOnly.Items.Clear();
        _teamOnly.Items.Add("All");
        foreach (var tm in _doc.Teams.Teams) _teamOnly.Items.Add($"{tm.Index} {tm}");
        _teamOnly.SelectedIndex = Math.Max(0, sel);
        FillUnits();
    }

    // ───────────────────────── validazione ─────────────────────────

    private readonly List<CampIssue> _allIssues = new();

    private void Validate()
    {
        if (_doc == null) return;
        _allIssues.Clear();
        _allIssues.AddRange(CampaignValidator.Run(_doc, o => _db.ObjectiveType(o.Type), unitSubType: ct => _unitDb.Ct.TryGetValue(ct, out var cc) ? cc.SubType : -1));
        FillIssues();
        _map.Issues = _allIssues;
        _map.InvalidateVisual();
    }

    private void FillIssues()
    {
        var lv = _issueFilter.SelectedIndex;
        var list = _allIssues.Where(i => lv == 2 || i.Level == IssueLevel.Error || lv == 0 && i.Level == IssueLevel.Warning)
            .OrderBy(i => i.Level).ThenBy(i => i.Category).ToList();
        _issues.ItemsSource = list;
        int err = _allIssues.Count(i => i.Level == IssueLevel.Error), warn = _allIssues.Count(i => i.Level == IssueLevel.Warning), info = _allIssues.Count(i => i.Level == IssueLevel.Info);
        _issueCount.Text = _allIssues.Count == 0 ? "no problems found" : $"{err} errors, {warn} warnings, {info} notes";
        _issueCount.Foreground = err > 0 ? Brushes.Red : warn > 0 ? Brushes.DarkOrange : Brushes.Green;
    }

    private void GoToIssue(CampIssue i)
    {
        if (_doc == null) return;
        if (i.Category == "Teams") { _leftTabs.SelectedIndex = 2; return; }
        if (i.Unit is VuId uid && _doc.Unit(uid) is { } u) Select(null, u, true);
        else if (i.Objective is VuId oid && _doc.Obj(oid) is { } o) Select(o, null, true);
        _map.MarkAt(i.X + 0.5, i.Y + 0.5);
    }
}

public enum MapMode { Select, Link, Assign, Paint }

/// <summary>
/// Mappa del teatro per l'editor di campagna, in km (est, nord): foto (panoramica 2048 px quando si e' lontani, tessere
/// vicine quando si zooma), collegamenti, obiettivi per squadra, unita'.
/// </summary>
public sealed class CampaignMap : FrameworkElement
{
    private readonly double _sizeKm;
    private readonly string? _photoDir;
    private readonly double _sizeM;
    private PhotoTiles? _photo;
    private BitmapSource? _overview;
    private readonly Dictionary<(int, int, int, int, int), BitmapSource?> _bmp = new();
    private readonly LinkedList<(int, int, int, int, int)> _bmpLru = new();
    private CampaignDoc? _doc;
    private CampaignDb? _db;
    private double _cx, _cy, _scale;          // centro (km est, km nord), pixel per km
    private Point? _panStart; private double _panCx, _panCy;
    private Point? _dragStart; private bool _dragMoved; private Action? _dragUndo;
    private CampObjective? _linkFrom;
    private (double E, double N)? _mark;
    // trascinamento di uno steerpoint del volo scelto
    private CampUnit? _wpFlight; private int _wpIdx = -1; private List<CampWaypoint>? _wpOrig; private List<uint>? _wpDep;
    private uint _wpTot; private UniFile.Snapshot? _wpSnap; private bool _wpMoved; private (short X, short Y) _wpLast;
    private bool _holdMsg;      // messaggio in basso da tenere finche' il tasto e' premuto

    public MapMode Mode { get; set; } = MapMode.Select;
    public double BrushKm { get; set; } = 10;
    public int PaintTeam { get; set; } = 1;
    public bool PaintFirstOwner { get; set; }
    private Point? _mouse;
    private Dictionary<CampObjective, (byte Owner, byte First)>? _stroke;
    public bool ShowPhoto { get; set; } = true;
    public bool ShowPolitical { get; set; }
    private TerrainTool.Core.Theater.PoliticalMap? _political;
    private Geometry? _polLand, _polLakes, _polBorders;
    /// <summary>Mappa politica (terre, laghi, confini, nomi degli stati) in km del teatro; le geometrie si fanno una volta sola.</summary>
    public TerrainTool.Core.Theater.PoliticalMap? Political
    {
        get => _political;
        set
        {
            _political = value;
            if (value == null) { _polLand = _polLakes = _polBorders = null; return; }
            // y = -nord: la trasformazione della vista e' poi una scala uniforme
            Geometry Make(List<(double E, double N)[]> rings, bool closed, FillRule rule)
            {
                var g = new StreamGeometry { FillRule = rule };
                using (var c = g.Open())
                    foreach (var r in rings)
                    {
                        c.BeginFigure(new Point(r[0].E, -r[0].N), closed, closed);
                        c.PolyLineTo(r.Skip(1).Select(q => new Point(q.E, -q.N)).ToList(), true, false);
                    }
                g.Freeze();
                return g;
            }
            _polLand = Make(value.Land, true, FillRule.EvenOdd);
            _polLakes = Make(value.Lakes, true, FillRule.EvenOdd);
            _polBorders = Make(value.Borders, false, FillRule.Nonzero);
        }
    }
    private static readonly Brush SeaBrush = Fz(new SolidColorBrush(Color.FromRgb(168, 208, 240)));
    private static readonly Brush LandBrush = Fz(new SolidColorBrush(Color.FromRgb(236, 232, 220)));
    private static readonly Brush BorderBrushPol = Fz(new SolidColorBrush(Color.FromRgb(95, 95, 95)));
    private static readonly Brush StateName = Fz(new SolidColorBrush(Color.FromArgb(200, 70, 70, 70)));

    private void DrawPolitical(DrawingContext dc, double W, double H, double dpi)
    {
        var tl = ToScreen(0, _sizeKm); var br = ToScreen(_sizeKm, 0);
        dc.DrawRectangle(SeaBrush, null, new Rect(tl, br));
        if (_polLand == null) return;
        dc.PushClip(new RectangleGeometry(new Rect(tl, br)));
        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, W / 2 - _cx * _scale, H / 2 + _cy * _scale));
        dc.DrawGeometry(LandBrush, null, _polLand);
        dc.DrawGeometry(SeaBrush, null, _polLakes!);
        dc.DrawGeometry(null, new Pen(BorderBrushPol, 1.6 / _scale), _polBorders!);
        dc.Pop();
        dc.Pop();
        foreach (var (name, e, n) in _political!.Labels)
        {
            var ft = new FormattedText(name.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Expanded), 15, StateName, dpi);
            var c = ToScreen(e, n);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
    public bool ShowLinks { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowNames { get; set; } = true;
    public bool ShowFrontLines { get; set; }
    public int TeamOnly { get; set; } = -1;
    public List<CampIssue>? Issues { get; set; }
    public CampObjective? SelObj { get; private set; }
    public CampUnit? SelUnit { get; private set; }
    /// <summary>Pacchetto di cui si disegna la rotta (come nel TE di BMS); il volo scelto e' in evidenza.</summary>
    public CampUnit? RoutePackage { get; set; }
    public bool ShowAllRoutes { get; set; }
    /// <summary>Copertura antiaerea: cerchi pieni semitrasparenti nel colore della squadra (portata dell'UCD del battaglione).</summary>
    public bool ShowSam { get; set; }
    public bool ShowAaa { get; set; }
    /// <summary>true = portata contro bersagli bassi (Rng_LowAir), false = alti (Rng_Air).</summary>
    public bool CoverageLow { get; set; }

    public event Action<CampObjective?, CampUnit?>? SelectionChanged;
    public event Action? Edited;
    public event Action<string>? Hover;
    public event Action<Action>? PushUndo;

    public CampaignMap(int sizeKm, string? photoDir, double sizeM)
    {
        _sizeKm = sizeKm; _photoDir = photoDir; _sizeM = sizeM;
        _cx = _cy = sizeKm / 2.0;
        ClipToBounds = true; Focusable = true;
        if (photoDir != null)
        {
            _photo = new PhotoTiles(photoDir, sizeM);
            Task.Run(BuildOverview);
        }
    }

    private CampaignDb? _unitDb;
    private readonly Dictionary<int, (int Icon, bool Sam)> _symbols = new();

    /// <summary>Simbolo del tipo di unita' (UnitIcon dell'UCD, come la mappa di BMS); SAM = difesa aerea con almeno un lanciamissili.</summary>
    private (int Icon, bool Sam) Symbol(int ct)
    {
        if (_symbols.TryGetValue(ct, out var v)) return v;
        var db = _unitDb ?? _db;
        int icon = db?.UcdOf(ct)?.UnitIcon ?? 0;
        bool sam = icon == UnitSymbols.AirDefense && db != null && db.UnitVehicleCts(ct).Any(x => db.RoleOf(x) == VehicleRole.Sam);
        return _symbols[ct] = (icon, sam);
    }
    /// <summary>Difesa aerea dell'unita': 1 SAM, 2 AAA, 0 nessuna; portate in km contro bersagli alti e bassi (UCD Rng_Air / Rng_LowAir).</summary>
    private (int Kind, int HighKm, int LowKm) AirDefenseOf(CampUnit u)
    {
        if ((u.UnitFlags & 1) != 0) return (0, 0, 0);                    // distrutta
        var db = _unitDb ?? _db;
        if (db?.UcdOf(u.Type - 100) is not { } ucd) return (0, 0, 0);
        if (u.Kind == UnitKind.Battalion)
        {
            var (icon, sam) = Symbol(u.Type - 100);
            if (icon != UnitSymbols.AirDefense) return (0, 0, 0);
            return (sam ? 1 : 2, ucd.RngAir, ucd.RngLowAir);
        }
        if (u.Kind == UnitKind.TaskForce && (ucd.RngAir > 0 || ucd.RngLowAir > 0)) return (1, ucd.RngAir, ucd.RngLowAir);
        return (0, 0, 0);
    }

    private void DrawCoverage(DrawingContext dc, double W, double H)
    {
        if (_doc?.Units == null || (!ShowSam && !ShowAaa)) return;
        // per squadra e tipo un solo gruppo di cerchi (FillRule Nonzero): dove si sovrappongono il colore non si scurisce
        var groups = new Dictionary<(int Owner, int Kind), GeometryGroup>();
        foreach (var u in _doc.Units.Units)
        {
            if (!Visible(u.Owner) || u.Kind is not (UnitKind.Battalion or UnitKind.TaskForce)) continue;
            var (kind, hi, lo) = AirDefenseOf(u);
            if (kind == 0 || kind == 1 && !ShowSam || kind == 2 && !ShowAaa) continue;
            int km = CoverageLow ? lo : hi;
            if (km <= 0) continue;
            // a teatro intero un cannone da 4 km sarebbe meno di un pixel: almeno 3 pixel, perche' si veda che c'e'
            double r = Math.Max(km * _scale, 3);
            var c = ToScreen(Pos(u).E, Pos(u).N);
            if (c.X + r < 0 || c.Y + r < 0 || c.X - r > W || c.Y - r > H) continue;
            if (!groups.TryGetValue((u.Owner, kind), out var g)) groups[(u.Owner, kind)] = g = new GeometryGroup { FillRule = FillRule.Nonzero };
            g.Children.Add(new EllipseGeometry(c, r, r));
        }
        foreach (var ((owner, kind), g) in groups.OrderBy(x => x.Key.Kind))
        {
            var col = OwnerColor(owner);
            var fill = new SolidColorBrush(Color.FromArgb(kind == 1 ? (byte)55 : (byte)40, col.R, col.G, col.B));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, col.R, col.G, col.B)), kind == 1 ? 1.2 : 1);
            if (kind == 2) pen.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(fill, pen, g);
        }
    }

    private void DrawCoverageLegend(DrawingContext dc, double H, double dpi)
    {
        if (!ShowSam && !ShowAaa) return;
        string alt = CoverageLow ? "LOW altitude targets (Rng_LowAir)" : "HIGH altitude targets (Rng_Air)";
        var lines = new List<string> { "Air defence coverage – " + alt };
        if (ShowSam) lines.Add("solid circle = SAM (and ships)");
        if (ShowAaa) lines.Add("dashed circle = AAA");
        lines.Add("radius = range of the battalion in the database; hover a unit for nm");
        var ft = new FormattedText(string.Join("\n", lines), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11.5, Brushes.White, dpi);
        var rect = new Rect(8, H - ft.Height - 18, ft.Width + 16, ft.Height + 10);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(190, 20, 24, 30)), new Pen(Brushes.Gray, 1), rect, 4, 4);
        dc.DrawText(ft, new Point(rect.X + 8, rect.Y + 5));
    }

    public void SetDoc(CampaignDoc d, CampaignDb db, CampaignDb? unitDb = null) { _doc = d; _db = db; _unitDb = unitDb ?? db; _symbols.Clear(); SelObj = null; SelUnit = null; RoutePackage = null; _linkFrom = null; InvalidateVisual(); }

    public static Color TeamColor(int c) => c switch
    {
        0 => Colors.White, 1 => Color.FromRgb(40, 170, 40), 2 => Color.FromRgb(30, 144, 255), 3 => Color.FromRgb(139, 69, 19),
        4 => Colors.Orange, 5 => Colors.Yellow, 6 => Color.FromRgb(230, 30, 30), _ => Color.FromRgb(60, 60, 60),
    };

    private Color OwnerColor(int owner) =>
        NatoIcons ? ObjectiveIcons.AffColors[Aff(owner)]
        : _doc?.Teams != null && owner < _doc.Teams.Teams.Count ? TeamColor(_doc.Teams.Teams[owner].Color) : Colors.White;

    /// <summary>Stile delle icone: false = BMS (colore della squadra), true = NATO APP-6 (cornice e colore della parte vista da Viewpoint).</summary>
    public bool NatoIcons { get; set; }
    /// <summary>Squadra dal cui punto di vista si decide amico / nemico / neutrale (stile NATO).</summary>
    public int Viewpoint { get; set; } = 1;

    /// <summary>Parte APP-6 della squadra owner vista da Viewpoint: 1 amico (stessa squadra, stessa coalizione, alleato o amico),
    /// 2 nemico (ostile o in guerra), 3 neutrale (neutrale o nessun rapporto).</summary>
    public int Aff(int owner)
    {
        if (_doc?.Teams == null) return 3;
        var tt = _doc.Teams.Teams;
        if (owner == Viewpoint) return 1;
        if (Viewpoint < 0 || Viewpoint >= tt.Count || owner <= 0 || owner >= tt.Count) return 3;
        if (CampaignValidator.Allied(_doc, Viewpoint, owner)) return 1;
        int st = tt[Viewpoint].Stance[Math.Min(owner, 7)];
        return st >= 4 ? 2 : 3;
    }

    public static (double E, double N) Pos(CampObjective o) =>
        o.HasSim ? (o.SimEast / CampaignDoc.FeetPerKm, o.SimNorth / CampaignDoc.FeetPerKm) : (o.X + 0.5, o.Y + 0.5);
    private static (double E, double N) Pos(CampUnit u) => (u.X + 0.5, u.Y + 0.5);

    private Point ToScreen(double e, double n) => new(ActualWidth / 2 + (e - _cx) * _scale, ActualHeight / 2 - (n - _cy) * _scale);
    private (double E, double N) ToWorld(Point p) => (_cx + (p.X - ActualWidth / 2) / _scale, _cy - (p.Y - ActualHeight / 2) / _scale);

    public void Select(CampObjective? o, CampUnit? u, bool center)
    {
        SelObj = o; SelUnit = u;
        if (center) CenterOnSelection();
        InvalidateVisual();
    }

    private static (double E, double N) Pos(CampWaypoint w) => (w.X + 0.5, w.Y + 0.5);

    private IEnumerable<CampUnit> FlightsOf(CampUnit pkg) =>
        _doc == null ? Enumerable.Empty<CampUnit>()
        : pkg.Kind == UnitKind.Flight ? new[] { pkg }
        : pkg.Elements.Select(e => _doc.Unit(e)).OfType<CampUnit>();

    /// <summary>Inquadra tutta la rotta del pacchetto (tutti i suoi voli), con un po' di margine.</summary>
    public void ZoomToPackage(CampUnit pkg)
    {
        var pts = FlightsOf(pkg).SelectMany(f => f.Waypoints).Select(Pos).ToList();
        pts.Add(Pos(pkg));
        double minE = pts.Min(p => p.E), maxE = pts.Max(p => p.E), minN = pts.Min(p => p.N), maxN = pts.Max(p => p.N);
        _cx = (minE + maxE) / 2; _cy = (minN + maxN) / 2;
        double W = Math.Max(ActualWidth, 200), H = Math.Max(ActualHeight, 200);
        double w = Math.Max(maxE - minE, 20) * 1.25, h = Math.Max(maxN - minN, 20) * 1.25;
        _scale = Math.Clamp(Math.Min(W / w, H / h), 0.05, 60);
        InvalidateVisual();
    }

    private static readonly Pen RouteSelPen = Fz(new Pen(Brushes.Yellow, 2.5));
    private static readonly Brush WpFill = Fz(new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)));

    /// <summary>Rotte: tutte sottili (se richiesto), quelle del pacchetto scelto spesse, col volo scelto in giallo; punti, bersagli, IP, decollo e atterraggio.</summary>
    private void DrawRoutes(DrawingContext dc, double dpi)
    {
        if (_doc?.Units == null) return;
        if (ShowAllRoutes)
            foreach (var f in _doc.Units.Units)
            {
                if (f.Kind != UnitKind.Flight || f.Waypoints.Count < 2 || !Visible(f.Owner)) continue;
                var c = OwnerColor(f.Owner);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B)), 1);
                for (int i = 1; i < f.Waypoints.Count; i++)
                    dc.DrawLine(pen, ToScreen(Pos(f.Waypoints[i - 1]).E, Pos(f.Waypoints[i - 1]).N), ToScreen(Pos(f.Waypoints[i]).E, Pos(f.Waypoints[i]).N));
            }
        if (RoutePackage is not { } pkg) return;
        foreach (var f in FlightsOf(pkg))
        {
            if (f.Waypoints.Count == 0) continue;
            bool sel = f == SelUnit;
            var c = OwnerColor(f.Owner);
            var pen = sel ? RouteSelPen : new Pen(new SolidColorBrush(c), 2);
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), sel ? 5 : 4);
            var pts = f.Waypoints.Select(w => ToScreen(Pos(w).E, Pos(w).N)).ToList();
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(shadow, pts[i - 1], pts[i]);
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
            for (int i = 0; i < pts.Count; i++)
            {
                var w = f.Waypoints[i]; var p = pts[i];
                if (w.IsTarget) dc.DrawRectangle(B(Colors.Red), new Pen(Brushes.White, 1.2), new Rect(p.X - 5, p.Y - 5, 10, 10));
                else dc.DrawEllipse(WpFill, pen, p, 4, 4);
                if (!sel && i > 0 && i < pts.Count - 1 && !w.IsTarget && !w.IsIp) continue;
                string t = w.Action is 1 or 7 ? w.ActionName : w.IsTarget ? $"{i + 1} {w.ActionName}" : w.IsIp ? $"{i + 1} IP" : $"{i + 1}";
                if (sel && w.AltitudeFt > 0) t += $"  {w.AltitudeFt / 1000.0:0.#}k";
                var ft = new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, sel ? Brushes.Yellow : Brushes.White, dpi);
                dc.DrawRectangle(TextBg, null, new Rect(p.X + 6, p.Y - 16, ft.Width + 4, ft.Height + 1));
                dc.DrawText(ft, new Point(p.X + 8, p.Y - 16));
            }
        }
    }

    public void CenterOnSelection()
    {
        (double E, double N)? p = SelObj != null ? Pos(SelObj) : SelUnit != null ? Pos(SelUnit) : null;
        if (p == null) return;
        (_cx, _cy) = p.Value;
        if (_scale < 6) _scale = 6;
        InvalidateVisual();
    }

    public void MarkAt(double e, double n)
    {
        _mark = (e, n); _cx = e; _cy = n;
        if (_scale < 6) _scale = 6;
        InvalidateVisual();
    }

    public void CancelLink() { _linkFrom = null; InvalidateVisual(); }

    // ───────────────────────── foto ─────────────────────────

    private void BuildOverview()
    {
        try
        {
            var ph = new PhotoTiles(_photoDir!, _sizeM);
            int n = ph.N, mip = 6, side = PhotoTiles.TilePx >> mip, W = n * side;
            var px = new byte[W * W * 4];
            for (int ty = 0; ty < n; ty++)
                for (int tx = 0; tx < n; tx++)
                {
                    var c = ph.GetChunk(tx, ty, mip, 0, 0, out int s);
                    if (c == null || s != side) continue;
                    for (int y = 0; y < s; y++) Buffer.BlockCopy(c, y * s * 4, px, ((ty * side + y) * W + tx * side) * 4, s * 4);
                }
            var bmp = BitmapSource.Create(W, W, 96, 96, PixelFormats.Bgra32, null, px, W * 4);
            bmp.Freeze();
            Dispatcher.BeginInvoke(() => { _overview = bmp; InvalidateVisual(); });
        }
        catch (Exception) { /* niente foto: la mappa resta scura */ }
    }

    private void DrawPhoto(DrawingContext dc, double W, double H)
    {
        if (_overview != null) dc.DrawImage(_overview, new Rect(ToScreen(0, _sizeKm), ToScreen(_sizeKm, 0)));
        var ph = _photo;
        if (ph == null) return;
        double kmPerPx0 = ph.TileM / 1000 / PhotoTiles.TilePx;
        int mip = Math.Clamp((int)Math.Ceiling(Math.Log2(1.0 / (kmPerPx0 * _scale))), 0, 12);
        if (mip >= 6) return;   // la panoramica basta
        int w = PhotoTiles.TilePx >> mip, side = Math.Min(PhotoTiles.Chunk, w), per = w / side;
        double chunkKm = ph.TileM / 1000 / per, sizeKm = ph.SizeM / 1000;
        var tl = ToWorld(new Point(0, 0)); var br = ToWorld(new Point(W, H));
        int c0 = Math.Max(0, (int)Math.Floor(tl.E / chunkKm)), c1 = Math.Min(per * ph.N - 1, (int)Math.Floor(br.E / chunkKm));
        int r0 = Math.Max(0, (int)Math.Floor((sizeKm - tl.N) / chunkKm)), r1 = Math.Min(per * ph.N - 1, (int)Math.Floor((sizeKm - br.N) / chunkKm));
        if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > 300) return;
        for (int rr = r0; rr <= r1; rr++)
            for (int cc = c0; cc <= c1; cc++)
            {
                int tx = cc / per, ty = rr / per, cx = cc % per, cy = rr % per;
                var bmp = Chunk(ph, tx, ty, mip, cx, cy);
                if (bmp == null) continue;
                double eL = cc * chunkKm, nT = sizeKm - rr * chunkKm;
                dc.DrawImage(bmp, new Rect(ToScreen(eL, nT), ToScreen(eL + chunkKm, nT - chunkKm)));
            }
    }

    private BitmapSource? Chunk(PhotoTiles ph, int tx, int ty, int mip, int cx, int cy)
    {
        var key = (tx, ty, mip, cx, cy);
        if (_bmp.TryGetValue(key, out var hit)) { _bmpLru.Remove(key); _bmpLru.AddFirst(key); return hit; }
        BitmapSource? res = null;
        var px = ph.GetChunk(tx, ty, mip, cx, cy, out int side);
        if (px != null) { res = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4); res.Freeze(); }
        _bmp[key] = res; _bmpLru.AddFirst(key);
        while (_bmpLru.Count > 300) { _bmp.Remove(_bmpLru.Last!.Value); _bmpLru.RemoveLast(); }
        return res;
    }

    // ───────────────────────── disegno ─────────────────────────

    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static readonly Pen LinkPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(150, 230, 230, 230)), 1));
    private static readonly Pen LinkSelPen = Fz(new Pen(Brushes.Yellow, 2));
    private static readonly Pen LinkBadPen = Fz(new Pen(Brushes.Red, 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen LinkLongPen = Fz(new Pen(Brushes.Orange, 1.5));
    private static readonly Pen AssignPen = Fz(new Pen(Brushes.Cyan, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen BrigPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1));
    private static readonly Pen SelPen = Fz(new Pen(Brushes.Yellow, 2.5));
    private static readonly Pen Outline = Fz(new Pen(Brushes.Black, 0.8));
    private static readonly Brush TextBg = Fz(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Pen[] LinePens = { null!, Fz(new Pen(Brushes.Red, 2)), Fz(new Pen(Brushes.Orange, 2)), Fz(new Pen(Brushes.Yellow, 2)) };
    private readonly Dictionary<Color, Brush> _brushes = new();
    private Brush B(Color c) { if (!_brushes.TryGetValue(c, out var b)) { b = Fz(new SolidColorBrush(c)); _brushes[c] = b; } return b; }

    private bool Visible(int owner) => TeamOnly < 0 || owner == TeamOnly;

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(B(Color.FromRgb(0x14, 0x18, 0x1E)), null, new Rect(0, 0, W, H));
        if (W < 2 || H < 2) return;
        if (_scale <= 0 || _scale < MinScale) _scale = MinScale;
        // il centro della vista resta sopra il teatro: niente vista vuota
        _cx = Math.Clamp(_cx, 0, _sizeKm); _cy = Math.Clamp(_cy, 0, _sizeKm);
        if (ShowPhoto) DrawPhoto(dc, W, H);
        else if (ShowPolitical) DrawPolitical(dc, W, H, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(ToScreen(0, _sizeKm), ToScreen(_sizeKm, 0)));
        if (_doc == null) return;
        var d = _doc;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tl = ToWorld(new Point(-50, -50)); var br = ToWorld(new Point(W + 50, H + 50));
        bool In(double e, double n) => e >= tl.E && e <= br.E && n <= tl.N && n >= br.N;

        DrawCoverage(dc, W, H);

        // collegamenti
        if (ShowLinks && _scale > 0.25)
            foreach (var o in d.Objectives)
            {
                if (!Visible(o.Owner)) continue;
                var (ae, an) = Pos(o);
                foreach (var l in o.Links)
                {
                    var b = d.Obj(l.To);
                    if (b == null || b.Index < o.Index && d.Linked(b, o)) continue;
                    var (be, bn) = Pos(b);
                    if (!In(ae, an) && !In(be, bn) && !SegmentVisible(ae, an, be, bn, tl, br)) continue;
                    bool sel = o == SelObj || b == SelObj;
                    double km = Math.Sqrt((ae - be) * (ae - be) + (an - bn) * (an - bn));
                    var pen = !d.Linked(b, o) ? LinkBadPen : sel ? LinkSelPen : km > 60 ? LinkLongPen : LinkPen;
                    dc.DrawLine(pen, ToScreen(ae, an), ToScreen(be, bn));
                }
            }
        if (_linkFrom != null)
        {
            var (le, ln) = Pos(_linkFrom);
            var p = ToScreen(le, ln);
            dc.DrawEllipse(null, new Pen(Brushes.Cyan, 2), p, 10, 10);
        }

        // obiettivi
        double rBase = Math.Clamp(_scale * 0.9, 1.5, 6);
        foreach (var o in d.Objectives)
        {
            if (!Visible(o.Owner)) continue;
            var (e, n) = Pos(o);
            if (!In(e, n)) continue;
            int t = _db?.ObjectiveType(o.Type) ?? -1;
            var p = ToScreen(e, n);
            var fill = B(OwnerColor(o.Owner));
            bool big = t is 1 or 2 or 8 or 3;
            double r = big ? rBase * 1.6 : rBase;
            // da vicino l'icona del tipo con le forme di BMS; da lontano (teatro intero) il puntino, se no e' illeggibile
            double icon = Math.Min(_scale * 2.6, 24) * ObjectiveIcons.Weight(t);
            if (icon >= 8 && NatoIcons)
            {
                double ni = Math.Max(icon, 14) * 1.25;          // la cornice NATO ha bisogno di un po' piu' di spazio
                ObjectiveIcons.DrawNato(dc, t, p, ni, Aff(o.Owner));
                r = ni / 2;
                if (o == SelObj) dc.DrawEllipse(null, SelPen, p, r + 3, r + 3);
            }
            else if (icon >= 8)
            {
                ObjectiveIcons.Draw(dc, t, p, icon, fill);
                r = icon / 2;
                if (o == SelObj) dc.DrawEllipse(null, SelPen, p, r + 3, r + 3);
            }
            else if (t is 1 or 2) dc.DrawRectangle(fill, o == SelObj ? SelPen : Outline, new Rect(p.X - r, p.Y - r, 2 * r, 2 * r));
            else dc.DrawEllipse(fill, o == SelObj ? SelPen : Outline, p, r, r);
            if (ShowFrontLines && o.Line > 0) dc.DrawEllipse(null, LinePens[o.Line], p, r + 3, r + 3);
            if (ShowNames && (o == SelObj || _scale > (big ? 1.5 : 6)))
                Label(dc, o.Name, p, o == SelObj, dpi, r);
        }

        // unita'
        if (ShowUnits && d.Units != null)
        {
            double s = Math.Clamp(_scale * 1.2, 4, 10);
            foreach (var u in d.Units.Units)
            {
                if (!Visible(u.Owner) || u.Kind is UnitKind.Flight or UnitKind.Package) continue;
                var (e, n) = Pos(u);
                if (!In(e, n)) continue;
                var p = ToScreen(e, n);
                var fill = B(OwnerColor(u.Owner));
                var pen = u == SelUnit ? SelPen : Outline;
                switch (u.Kind)
                {
                    case UnitKind.Battalion:
                    case UnitKind.Brigade:
                    {
                        var (icon, sam) = Symbol(u.Type - 100);
                        UnitSymbols.DrawGround(dc, p, s, icon, u.Kind == UnitKind.Brigade, sam, fill, pen, dpi, NatoIcons ? Aff(u.Owner) : 0);
                        break;
                    }
                    case UnitKind.Squadron: Triangle(dc, fill, pen, p, s); break;
                    case UnitKind.TaskForce: UnitSymbols.DrawNaval(dc, p, s, Symbol(u.Type - 100).Icon, fill, pen, dpi, NatoIcons ? Aff(u.Owner) : 0); break;
                }
            }
            // unita' scelta: obiettivo assegnato, brigata e battaglioni
            if (SelUnit is { } su)
            {
                var sp = ToScreen(Pos(su).E, Pos(su).N);
                if (d.Obj(su.AssignedObj) is { } ao) { var (e, n) = Pos(ao); dc.DrawLine(AssignPen, sp, ToScreen(e, n)); }
                if (su.Kind == UnitKind.Brigade)
                    foreach (var el in su.Elements) if (d.Unit(el) is { } b) { var (e, n) = Pos(b); dc.DrawLine(BrigPen, sp, ToScreen(e, n)); }
                if (su.Kind == UnitKind.Battalion && d.Unit(su.Parent) is { } pb) { var (e, n) = Pos(pb); dc.DrawLine(BrigPen, sp, ToScreen(e, n)); }
                if (su.Kind == UnitKind.Squadron && d.Obj(su.Airbase) is { } ab) { var (e, n) = Pos(ab); dc.DrawLine(AssignPen, sp, ToScreen(e, n)); }
                Label(dc, $"{su.Kind} {su.NameId}", sp, true, dpi);
            }
            // obiettivo scelto: unita' assegnate
            if (SelObj is { } so)
            {
                var op = ToScreen(Pos(so).E, Pos(so).N);
                foreach (var u in d.Units.Units)
                    if (u.IsGround && u.AssignedObj == so.Id || u.Kind == UnitKind.Squadron && u.Airbase == so.Id)
                    { var (e, n) = Pos(u); dc.DrawLine(AssignPen, op, ToScreen(e, n)); }
            }
        }

        DrawRoutes(dc, dpi);

        if (Mode == MapMode.Paint && _mouse is Point mp)
        {
            var col = OwnerColor(PaintTeam);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(50, col.R, col.G, col.B)), new Pen(new SolidColorBrush(col), 1.5), mp, BrushKm * _scale, BrushKm * _scale);
        }

        // problemi trovati dalla validazione (puntini rossi) e segnaposto
        if (Issues != null && _scale > 0.3)
            foreach (var i in Issues)
            {
                if (i.Level != IssueLevel.Error) continue;
                if (!In(i.X + 0.5, i.Y + 0.5)) continue;
                var p = ToScreen(i.X + 0.5, i.Y + 0.5);
                dc.DrawEllipse(null, new Pen(Brushes.Red, 1.5), p, 12, 12);
            }
        if (_mark is { } m)
        {
            var p = ToScreen(m.E, m.N);
            dc.DrawEllipse(null, new Pen(Brushes.Magenta, 2.5), p, 22, 22);
        }
        DrawCoverageLegend(dc, H, dpi);
    }

    private static bool SegmentVisible(double ae, double an, double be, double bn, (double E, double N) tl, (double E, double N) br) =>
        !(Math.Max(ae, be) < tl.E || Math.Min(ae, be) > br.E || Math.Min(an, bn) > tl.N || Math.Max(an, bn) < br.N);

    private static void Triangle(DrawingContext dc, Brush fill, Pen pen, Point p, double s)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(new Point(p.X, p.Y - s * 1.2), true, true); c.LineTo(new Point(p.X + s, p.Y + s * 0.8), true, false); c.LineTo(new Point(p.X - s, p.Y + s * 0.8), true, false); }
        g.Freeze();
        dc.DrawGeometry(fill, pen, g);
    }

    private static void Label(DrawingContext dc, string text, Point p, bool sel, double dpi, double r = 5)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), sel ? 13 : 11, sel ? Brushes.Yellow : Brushes.White, dpi);
        var at = new Point(p.X + Math.Max(7, r + 2), p.Y + Math.Max(5, r * 0.6));
        dc.DrawRectangle(TextBg, null, new Rect(at.X - 2, at.Y - 1, ft.Width + 4, ft.Height + 2));
        dc.DrawText(ft, at);
    }

    // ───────────────────────── mouse ─────────────────────────

    /// <summary>
    /// Steerpoint spostabile sotto il mouse (entro 8 pixel) tra i voli della rotta mostrata (prima il volo scelto, poi gli altri
    /// del pacchetto), o (null, -1). Non serve aver scelto il volo: cliccando il suo steerpoint lo si sceglie.
    /// </summary>
    private (CampUnit? F, int I) HitWaypoint(Point p)
    {
        if (RoutePackage is not { } pkg || _doc?.Units == null) return (null, -1);
        var flights = FlightsOf(pkg).ToList();
        if (SelUnit is { Kind: UnitKind.Flight } sf) { flights.Remove(sf); flights.Insert(0, sf); }
        foreach (var f in flights)
        {
            int best = -1; double bd = 8 * 8;
            for (int i = 0; i < f.Waypoints.Count; i++)
            {
                if (!RouteEdit.CanMove(f, i)) continue;
                var q = ToScreen(Pos(f.Waypoints[i]).E, Pos(f.Waypoints[i]).N);
                double dd = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (dd < bd) { bd = dd; best = i; }
            }
            if (best >= 0) return (f, best);
        }
        return (null, -1);
    }

    private static string Shift(long ms) => (ms < 0 ? "−" : "+") + $"{Math.Abs(ms) / 60000}:{Math.Abs(ms) / 1000 % 60:00}";

    private (CampObjective? O, CampUnit? U) HitTest(Point p)
    {
        if (_doc == null) return (null, null);
        double best = 10 * 10;
        CampObjective? bo = null; CampUnit? bu = null;
        if (ShowUnits && _doc.Units != null)
            foreach (var u in _doc.Units.Units)
            {
                if (!Visible(u.Owner) || u.Kind is UnitKind.Flight or UnitKind.Package) continue;
                var q = ToScreen(Pos(u).E, Pos(u).N);
                double dd = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
                if (dd < best) { best = dd; bu = u; }
            }
        double bestO = bu != null ? best * 0.5 : best;   // a parita' di distanza vince l'unita'
        foreach (var o in _doc.Objectives)
        {
            if (!Visible(o.Owner)) continue;
            var q = ToScreen(Pos(o).E, Pos(o).N);
            double dd = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
            if (dd < bestO) { bestO = dd; bo = o; }
        }
        return bo != null ? (bo, null) : (null, bu);
    }

    /// <summary>Zoom minimo: il teatro intero occupa il lato corto della vista (piu' lontano la mappa diventa un francobollo inutile).</summary>
    private double MinScale => ActualWidth > 2 && ActualHeight > 2 ? Math.Min(ActualWidth, ActualHeight) / _sizeKm : 0.05;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var before = ToWorld(p);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 1 / 1.25), MinScale, 400);
        var after = ToWorld(p);
        _cx += before.E - after.E; _cy += before.N - after.N;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = p; _panCx = _cx; _panCy = _cy; CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _doc == null) return;
        _mark = null;
        if (Mode == MapMode.Select && HitWaypoint(p) is ({ } wf, int wi) && wi >= 0 && _doc.Units is { } wuf)
        {
            if (SelUnit != wf) { SelObj = null; SelUnit = wf; SelectionChanged?.Invoke(null, wf); }
            _wpFlight = wf; _wpIdx = wi; _wpMoved = false;
            _wpOrig = wf.Waypoints.ToList();
            _wpDep = Enumerable.Range(0, wf.Waypoints.Count).Select(j => wuf.WaypointDepart(wf, j)).ToList();
            _wpTot = wf.TimeOnTarget; _wpSnap = wuf.TakeSnapshot();
            _wpLast = (wf.Waypoints[wi].X, wf.Waypoints[wi].Y);
            CaptureMouse();
            return;
        }
        var (o, u) = HitTest(p);
        bool assign = Mode == MapMode.Assign || (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (assign && o != null && SelUnit is { IsGround: true } su)
        {
            var old = su.AssignedObj; int ox = su.X, oy = su.Y;
            var uf = _doc.Units!;
            uf.SetAssignedObjective(su, o.Id);
            if (su.Orders == 9) uf.SetPosition(su, o.X, o.Y);   // difesa aerea: si schiera sull'obiettivo, come in Korea
            PushUndo?.Invoke(() => { uf.SetAssignedObjective(su, old); uf.SetPosition(su, ox, oy); });
            Edited?.Invoke();
            InvalidateVisual();
            return;
        }
        if (Mode == MapMode.Paint)
        {
            _stroke = new();
            PaintAt(p);
            CaptureMouse();
            return;
        }
        if (Mode == MapMode.Link)
        {
            if (o == null) { _linkFrom = null; InvalidateVisual(); return; }
            if (_linkFrom == null || _linkFrom == o) { _linkFrom = o; SelObj = o; SelUnit = null; SelectionChanged?.Invoke(o, null); InvalidateVisual(); return; }
            var a = _linkFrom; _linkFrom = null;
            if (!_doc.CanEditObjectives) { Hover?.Invoke("This file takes its objectives from the scenario: links are edited there"); return; }
            if (_doc.Linked(a, o) || _doc.Linked(o, a))
            {
                var la = a.Links.FirstOrDefault(l => l.To == o.Id); var lb = o.Links.FirstOrDefault(l => l.To == a.Id);
                _doc.RemoveLink(a, o);
                PushUndo?.Invoke(() => { if (la != null) a.Links.Add(la); if (lb != null) o.Links.Add(lb); });
                Hover?.Invoke($"Link removed: {a.Name} – {o.Name}");
            }
            else if (_doc.AddLink(a, o))
            {
                PushUndo?.Invoke(() => _doc.RemoveLink(a, o));
                Hover?.Invoke($"Link added: {a.Name} – {o.Name}");
            }
            SelObj = o; SelectionChanged?.Invoke(o, null);
            Edited?.Invoke();
            InvalidateVisual();
            return;
        }
        SelObj = o; SelUnit = o == null ? u : null;
        SelectionChanged?.Invoke(SelObj, SelUnit);
        InvalidateVisual();
        if (Mode == MapMode.Select && o != null && !_doc.CanEditObjectives)
            { _holdMsg = true; Hover?.Invoke($"{o.Name}: in a TE the objectives come from the scenario ({System.IO.Path.GetFileName(_doc.ObjectivesFrom ?? "te_new")}), so they cannot be moved here. Units and steerpoints can."); }
        if (Mode == MapMode.Select && (o != null && _doc.CanEditObjectives || SelUnit != null))
        {
            _dragStart = p; _dragMoved = false;
            if (o != null) { double n0 = o.SimNorth, e0 = o.SimEast; int x0 = o.X, y0 = o.Y; var d = _doc; _dragUndo = () => d.MoveObjective(o, n0, e0); if (!o.HasSim) _dragUndo = () => { o.X = x0; o.Y = y0; }; }
            else if (SelUnit is { } su2) { int x0 = su2.X, y0 = su2.Y; var uf = _doc.Units!; _dragUndo = () => uf.SetPosition(su2, x0, y0); }
            CaptureMouse();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var w = ToWorld(p);
        if (_panStart is Point ps)
        {
            _cx = _panCx - (p.X - ps.X) / _scale; _cy = _panCy + (p.Y - ps.Y) / _scale;
            InvalidateVisual();
            return;
        }
        if (Mode == MapMode.Paint)
        {
            _mouse = p;
            if (_stroke != null) PaintAt(p);
            InvalidateVisual();
            Hover?.Invoke($"{w.E:0.0} km E, {w.N:0.0} km N   brush {BrushKm:0} km → {(_doc != null ? CampaignValidator.TeamName(_doc, PaintTeam) : "")}");
            return;
        }
        if (_wpFlight is { } mf && _wpOrig != null && _wpDep != null && _doc?.Units is { } muf)
        {
            short gx = (short)Math.Clamp((int)Math.Floor(w.E), 0, (int)_sizeKm - 1), gy = (short)Math.Clamp((int)Math.Floor(w.N), 0, (int)_sizeKm - 1);
            if ((gx, gy) != _wpLast)
            {
                _wpLast = (gx, gy); _wpMoved = true;
                var plan = RouteEdit.Compute(_wpOrig, _wpDep, _wpTot, _wpIdx, gx, gy);
                RouteEdit.Apply(muf, mf, plan);
                long dArr = (long)plan.Arrive[_wpIdx] - _wpOrig[_wpIdx].Arrive;
                Hover?.Invoke($"Steerpoint {_wpIdx + 1}: arrive {CampWaypoint.Time(plan.Arrive[_wpIdx])} ({Shift(dArr)})" +
                              (_wpTot > 0 ? $"   TOT {CampWaypoint.Time(plan.Tot)} ({Shift(plan.TotShiftMs)})" : "") + "   – times after it move by the same amount");
                InvalidateVisual();
            }
            return;
        }
        if (_dragStart is Point ds && _doc != null)
        {
            if (!_dragMoved && Math.Abs(p.X - ds.X) + Math.Abs(p.Y - ds.Y) < 4) return;
            _dragMoved = true;
            double e2 = Math.Clamp(w.E, 0, _sizeKm - 0.01), n2 = Math.Clamp(w.N, 0, _sizeKm - 0.01);
            if (SelObj != null) _doc.MoveObjective(SelObj, n2 * CampaignDoc.FeetPerKm, e2 * CampaignDoc.FeetPerKm);
            else if (SelUnit != null) _doc.Units!.SetPosition(SelUnit, (int)e2, (int)n2);
            InvalidateVisual();
            Hover?.Invoke($"{e2:0.00} km E, {n2:0.00} km N");
            return;
        }
        if (_holdMsg && e.LeftButton == MouseButtonState.Pressed) return;
        var hw = Mode == MapMode.Select ? HitWaypoint(p) : (null, -1);
        Cursor = hw.I >= 0 ? Cursors.SizeAll : null;
        if (hw.F is { } hf) { var hp = hf.Waypoints[hw.I]; Hover?.Invoke($"Steerpoint {hw.I + 1} of {hf.MissionName} flight: {hp.ActionName}, arrive {CampWaypoint.Time(hp.Arrive)} – drag to move it"); return; }
        var (o, u) = HitTest(p);
        string what = o != null ? $"{o.Name} ({EditorData.TypeName(_db?.ObjectiveType(o.Type) ?? -1)}, {(_doc != null ? CampaignValidator.TeamName(_doc, o.Owner) : "")}, {o.Links.Count} links)"
                    : u != null ? $"{u.Kind} {u.NameId} {(_unitDb?.UnitTypeName(u.Type) ?? "")} ({(_doc != null ? CampaignValidator.TeamName(_doc, u.Owner) : "")}){AdText(u)}" : "";
        Hover?.Invoke($"{w.E:0.0} km E, {w.N:0.0} km N   {what}");
    }

    private string AdText(CampUnit u)
    {
        var (kind, hi, lo) = AirDefenseOf(u);
        if (kind == 0) return "";
        static string Nm(int km) => km > 0 ? $"{km / 1.852:0} nm ({km} km)" : "none";
        return $"   {(kind == 1 ? "SAM" : "AAA")} range: high {Nm(hi)}, low {Nm(lo)}";
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; if (Mode == MapMode.Paint) InvalidateVisual(); }

    /// <summary>Pennello: ogni obiettivo (visibile) entro il raggio passa alla squadra scelta; il tratto si annulla in blocco.</summary>
    private void PaintAt(Point p)
    {
        if (_doc == null || _stroke == null || _doc.Teams == null || PaintTeam < 0 || PaintTeam >= _doc.Teams.Teams.Count) return;
        var (we, wn) = ToWorld(p);
        double r2 = BrushKm * BrushKm;
        byte t = (byte)PaintTeam;
        bool any = false;
        foreach (var o in _doc.Objectives)
        {
            if (!Visible(o.Owner)) continue;
            var (e, n) = Pos(o);
            if ((e - we) * (e - we) + (n - wn) * (n - wn) > r2) continue;
            if (o.Owner == t && (!PaintFirstOwner || o.FirstOwner == t)) continue;
            if (!_stroke.ContainsKey(o)) _stroke[o] = (o.Owner, o.FirstOwner);
            _doc.SetOwner(o, t);
            if (PaintFirstOwner) _doc.SetFirstOwner(o, t);
            any = true;
        }
        if (any) InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _holdMsg = false;
        if (_stroke != null)
        {
            var st = _stroke; _stroke = null; ReleaseMouseCapture();
            if (st.Count > 0 && _doc != null)
            {
                var d = _doc;
                PushUndo?.Invoke(() => { foreach (var (o, (ow, fo)) in st) { d.SetOwner(o, ow); d.SetFirstOwner(o, fo); } });
                Hover?.Invoke($"{st.Count} objectives painted to {CampaignValidator.TeamName(d, PaintTeam)}");
                Edited?.Invoke();
            }
            return;
        }
        if (_panStart != null) { _panStart = null; ReleaseMouseCapture(); return; }
        if (_wpFlight != null)
        {
            ReleaseMouseCapture();
            if (_wpMoved && _wpSnap != null && _doc?.Units is { } uuf)
            {
                var sn = _wpSnap;
                PushUndo?.Invoke(() => uuf.RestoreSnapshot(sn));
                Edited?.Invoke();
            }
            _wpFlight = null; _wpIdx = -1; _wpOrig = null; _wpDep = null; _wpSnap = null;
            return;
        }
        if (_dragStart != null)
        {
            _dragStart = null; ReleaseMouseCapture();
            if (_dragMoved && _dragUndo != null) { PushUndo?.Invoke(_dragUndo); Edited?.Invoke(); }
            _dragUndo = null;
        }
    }
}
