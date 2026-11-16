using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Theater;

namespace TerrainTool.App;

/// <summary>
/// New project: which airbase list to start from (empty, the Mideast list shipped with the tool,
/// or the airbases of a theater installed in BMS). BMS is only read.
/// </summary>
public sealed class NewProjectWindow : Window
{
    public enum Start { Empty, Mideast, Theater, World }

    public Start Choice { get; private set; } = Start.Empty;
    /// <summary>The import, when Choice is Theater.</summary>
    public TheaterBaseImport? Import { get; private set; }
    public BmsTheater? SourceTheater { get; private set; }
    /// <summary>Also copy size, centre and heights of the source theater (New Terrain theaters only).</summary>
    /// <summary>The area chosen on the world map (Choice World).</summary>
    public AreaPickerWindow? Area { get; private set; }
    public bool UseTheaterSettings => _useSettings.IsChecked == true && Import?.Georeferenced == true;

    private readonly RadioButton _empty = new() { Content = "Empty list – I will add the airbases myself in tab 2", Margin = new Thickness(0, 3, 0, 3) };
    private readonly RadioButton _mideast = new() { Margin = new Thickness(0, 3, 0, 3) };
    private readonly RadioButton _world = new() { Content = "Choose the area on a world map – airbases from OurAirports:", Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock _worldInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _theater = new() { Content = "Import the airbases of a theater installed in BMS:", Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBox _data = new() { MinWidth = 360, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _theaters = new() { MinWidth = 360, Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _useSettings = new() { Content = "Also use its size, centre and heights (tab 1), so its campaign and objects stay in place", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11.5, Foreground = Brushes.DimGray };
    private readonly string _mideastCsv;

    public NewProjectWindow(string projectName, string mideastCsv, string? bmsDataDir)
    {
        _mideastCsv = mideastCsv;
        Title = $"New project – {projectName}";
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        int mid = 0;
        try { if (File.Exists(mideastCsv)) mid = AirbaseCsv.Load(mideastCsv).Count; } catch (IOException) { }
        _mideast.Content = $"Mideast airbases shipped with the tool ({mid} bases: Iraq, Iran, Kuwait, Gulf, Saudi Arabia, Jordan)";
        _mideast.IsEnabled = mid > 0;

        _data.Text = SavedDataDir() ?? bmsDataDir ?? @"C:\Falcon BMS 4.38\Data";
        var dataRow = new WrapPanel { Margin = new Thickness(22, 2, 0, 4) };
        dataRow.Children.Add(new TextBlock { Text = "BMS Data folder  ", VerticalAlignment = VerticalAlignment.Center });
        dataRow.Children.Add(_data);
        dataRow.Children.Add(Btn("Browse…", Browse));
        var thRow = new WrapPanel { Margin = new Thickness(22, 2, 0, 0) };
        thRow.Children.Add(new TextBlock { Text = "Theater  ", VerticalAlignment = VerticalAlignment.Center, Width = 98 });
        thRow.Children.Add(_theaters);
        var thBox = new StackPanel { Children = { dataRow, thRow, new Border { Margin = new Thickness(22, 0, 0, 0), Child = _useSettings }, new Border { Margin = new Thickness(22, 0, 0, 0), Child = _info } } };

        var ok = new Button { Content = "Create project", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        ok.Click += (_, _) => Confirm();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } };

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock { Text = "Which airbases should the new theater start with? They set the centre of the theater (tab 1) and can be changed at any time in tab 2.",
                                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
                _empty, _mideast, _world, WorldRow(), _theater, thBox, buttons,
            },
        };

        _empty.Checked += (_, _) => Update();
        _mideast.Checked += (_, _) => Update();
        _theater.Checked += (_, _) => Update();
        _world.Checked += (_, _) => { Update(); if (Area == null) PickArea(); };
        _theaters.SelectionChanged += (_, _) => { if (_theaters.SelectedItem == null) return; if (_theater.IsChecked == true) Update(); else _theater.IsChecked = true; };
        _empty.IsChecked = true;
        LoadTheaters();
    }

    private static Button Btn(string text, Action a) { var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2) }; b.Click += (_, _) => a(); return b; }

    /// <summary>The BMS Data folder chosen last time in the Campaign editor's theater picker.</summary>
    private static string? SavedDataDir()
    {
        try
        {
            // same file the Campaign Editor's theater picker writes (BMS Suite\TerrainTool\Theaters\bms_data_folder.txt)
            var pf = global::TerrainTool.Core.SuiteLayout.ProgramFolder();
            string root = pf != null ? Path.Combine(pf, "Theaters") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS TerrainTool", "Theaters");
            string f = Path.Combine(root, "bms_data_folder.txt");
            return File.Exists(f) && File.ReadAllText(f).Trim() is { Length: > 0 } s && Directory.Exists(s) ? s : null;
        }
        catch (IOException) { return null; }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "BMS Data folder (the one with TerrData, Campaign, Add-On …)" };
        if (Directory.Exists(_data.Text)) dlg.InitialDirectory = _data.Text;
        if (dlg.ShowDialog(this) == true) { _data.Text = dlg.FolderName; LoadTheaters(); }
    }

    private void LoadTheaters()
    {
        _theaters.Items.Clear();
        string dir = _data.Text.Trim();
        var list = Directory.Exists(dir) ? BmsTheater.List(dir) : new List<BmsTheater>();
        foreach (var t in list) _theaters.Items.Add(new ComboBoxItem { Content = $"{t.Name}   ({t.Subtitle})", Tag = t });
        _theater.IsEnabled = list.Count > 0;
        if (list.Count == 0) _info.Text = Directory.Exists(dir) ? "No theater found in TerrData\\TheaterDefinition\\theater.lst of this folder." : "Choose the BMS Data folder to see its theaters.";
        Update();
    }

    private void Update()
    {
        Import = null; SourceTheater = null;
        bool th = _theater.IsChecked == true;
        _useSettings.IsEnabled = false;
        if (!th) { if (_theater.IsEnabled) _info.Text = ""; return; }
        if ((_theaters.SelectedItem as ComboBoxItem)?.Tag is not BmsTheater t) { _info.Text = "Choose a theater."; return; }
        try
        {
            Import = TheaterBaseImport.FromTheater(t);
            SourceTheater = t;
            var cfg = Import.Theater;
            string head = $"{Import.Bases.Count} airbases and airstrips found" +
                          (Import.Georeferenced ? $", with position ({cfg!.SizeKm} km theater, centre {cfg.CenterLat:0.###} / {cfg.CenterLon:0.###})." : ", names only.");
            _info.Text = string.Join("\n", new[] { head }.Concat(Import.Notes));
            _useSettings.IsEnabled = Import.Georeferenced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _info.Text = "⚠ " + ex.Message;
        }
    }

    private FrameworkElement WorldRow()
    {
        var row = new DockPanel { Margin = new Thickness(22, 2, 0, 6) };
        var b = Btn("Choose area…", PickArea);
        DockPanel.SetDock(b, Dock.Left);
        row.Children.Add(b);
        row.Children.Add(_worldInfo);
        _worldInfo.Text = "A 1024 or 2048 km square anywhere in the world; the airbases inside it are listed automatically.";
        return row;
    }

    private void PickArea()
    {
        var w = new AreaPickerWindow(Area?.CenterLat ?? 30, Area?.CenterLon ?? 40, Area?.SizeKm ?? 1024) { Owner = this };
        if (w.ShowDialog() != true) { if (Area == null && _world.IsChecked == true) _empty.IsChecked = true; return; }
        Area = w;
        _world.IsChecked = true;
        _worldInfo.Text = $"{w.SizeKm} km, centre {w.CenterLat:0.##} / {w.CenterLon:0.##}: " +
                          (w.HaveAirportData ? $"{w.Bases.Count} airbases" : "no airbase data (add them in tab 2)") + $", {w.Places.Count} cities and targets.";
    }

    private void Confirm()
    {
        if (_mideast.IsChecked == true) Choice = Start.Mideast;
        else if (_theater.IsChecked == true)
        {
            if (Import == null) { MessageBox.Show(this, "Choose a theater that can be read, or another option.", Title); return; }
            if (Import.Bases.Count == 0 && MessageBox.Show(this, "That theater has no airbases: start with an empty list?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Choice = Start.Theater;
        }
        else if (_world.IsChecked == true)
        {
            if (Area == null) { PickArea(); if (Area == null) return; }
            Choice = Start.World;
        }
        else Choice = Start.Empty;
        DialogResult = true;
    }
}
