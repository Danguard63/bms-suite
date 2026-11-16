using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerrainTool.Core.Campaign;

namespace ArtEditor;

/// <summary>Impostazioni dell'editor (cartella Data di BMS, ultimo Art aperto) in %LocalAppData%\BMS Suite\ArtEditor.</summary>
public static class Settings
{
    private static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMS Suite", "ArtEditor", "settings.txt");

    public static string? Get(string key)
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            foreach (var l in System.IO.File.ReadAllLines(File))
                if (l.StartsWith(key + "=", StringComparison.Ordinal)) return l[(key.Length + 1)..];
        }
        catch { }
        return null;
    }

    public static void Set(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var lines = System.IO.File.Exists(File) ? System.IO.File.ReadAllLines(File).Where(l => !l.StartsWith(key + "=", StringComparison.Ordinal)).ToList() : new List<string>();
            lines.Add(key + "=" + value);
            System.IO.File.WriteAllLines(File, lines);
        }
        catch { }
    }

    /// <summary>La cartella del programma nella suite (BMS Suite\ArtEditor), risalendo dall'exe; se no la cartella dell'exe.</summary>
    public static string ToolFolder
    {
        get => global::TerrainTool.Core.SuiteLayout.ProgramFolder() ?? AppContext.BaseDirectory;
    }

    public static string BackupFolder => Path.Combine(ToolFolder, "Backup");
}

/// <summary>
/// Scelta dell'Art da modificare (o da usare come riferimento): un teatro installato in BMS, l'Art di un progetto di TerrainTool,
/// una cartella qualsiasi (anche l'Art di un vecchio add-on 4.36/4.37), oppure un Art nuovo che parte da quello di BMS.
/// </summary>
public sealed class OpenWindow : Window
{
    public sealed record Choice(string Label, string Sub, string? Root, bool IsBmsArt);

    private readonly TextBox _data = new() { Margin = new Thickness(0, 0, 6, 0) };
    private readonly ListBox _list = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly bool _reference;

    public string DataDir => _data.Text.Trim();
    public string? Root { get; private set; }
    public string Label { get; private set; } = "";

    public OpenWindow(bool reference)
    {
        _reference = reference;
        Title = reference ? "Reference art (to take images from)" : "Open the art of a theater";
        Width = 760; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _data.Text = Settings.Get("data") ?? @"C:\Falcon BMS 4.38\Data";

        var top = new DockPanel();
        top.Children.Add(new TextBlock { Text = "BMS Data folder ", VerticalAlignment = VerticalAlignment.Center });
        var browse = new Button { Content = "…", Padding = new Thickness(8, 2, 8, 2) };
        browse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "BMS Data folder (e.g. C:\\Falcon BMS 4.38\\Data)" };
            if (d.ShowDialog(this) == true) { _data.Text = d.FolderName; Fill(); }
        };
        DockPanel.SetDock(browse, Dock.Right); top.Children.Add(browse);
        top.Children.Add(_data);
        _data.LostFocus += (_, _) => Fill();

        _list.MouseDoubleClick += (_, _) => Ok();
        _list.SelectionChanged += (_, _) => _note.Text = (_list.SelectedItem as ListBoxItem)?.Tag is Choice c ? c.Sub : "";

        var open = new Button { Content = reference ? "Use as reference" : "Open", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
        open.Click += (_, _) => Ok();
        var folder = new Button { Content = "Art folder…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Any Art folder: another theater, an old 4.36/4.37 add-on (e.g. E:\\Add-On Mideast128\\Art)…" };
        folder.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Art folder (the one with _FHD, Resource, Main…)" };
            if (d.ShowDialog(this) == true) { Root = ArtSet.ArtRoot(d.FolderName); Label = Path.GetFileName(Path.GetDirectoryName(Root.TrimEnd('\\')) ?? Root) + " · " + Root; Done(); }
        };
        var neu = new Button { Content = "New theater art…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Visibility = reference ? Visibility.Collapsed : Visibility.Visible,
            ToolTip = "Starts from the BMS 4.38 art: the files you change are written into ArtEditor\\Theaters\\<name>\\Art" };
        neu.Click += (_, _) => NewArt();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(folder); bar.Children.Add(neu); bar.Children.Add(open); bar.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        DockPanel.SetDock(_note, Dock.Bottom); root.Children.Add(_note);
        root.Children.Add(_list);
        Content = root;
        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        string data = DataDir;
        string bmsArt = Path.Combine(data, "Art");
        void Head(string t) => _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) }, IsEnabled = false });
        void Add(Choice c) => _list.Items.Add(new ListBoxItem
        {
            Tag = c,
            Content = new StackPanel
            {
                Margin = new Thickness(8, 2, 0, 2),
                Children = { new TextBlock { Text = c.Label }, new TextBlock { Text = c.Root ?? "", FontSize = 11, Foreground = Brushes.Gray } },
            },
        });

        Head("TerrainTool projects");
        string projects = Path.Combine(Path.GetDirectoryName(Settings.ToolFolder.TrimEnd('\\')) ?? "", "TerrainTool", "Projects");
        if (Directory.Exists(projects))
            foreach (var p in Directory.EnumerateDirectories(projects).Where(d => Directory.EnumerateFiles(d, "*.ttproj").Any()))
                Add(new Choice($"{Path.GetFileName(p)} (project)", "The art made by the TerrainTool (tab 7) in the project's Output\\Art\\Art. Assemble and 'Install UI art into BMS…' take it from here. " +
                    "Careful: running 'Theater art' again in the TerrainTool rebuilds these files and your changes here are lost.",
                    Path.Combine(p, "Output", "Art", "Art"), false));

        Head("Theaters installed in BMS");
        if (Directory.Exists(data))
            foreach (var t in BmsTheater.List(data))
            {
                string r = ArtSet.ArtRoot(t.ArtDir);
                bool bms = Path.GetFullPath(r).TrimEnd('\\').Equals(Path.GetFullPath(bmsArt).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                Add(new Choice($"{t.Name}   ·   {t.Subtitle}",
                    bms ? "This theater uses the BMS art itself (Data\\Art): it cannot be changed in place. Opening it starts a new theater art from it."
                        : "The theater's own art folder (artdir of its .tdf) in BMS. Save writes there: every file it replaces is first copied into ArtEditor\\Backup.",
                    r, bms));
            }
        else Head("   (BMS Data folder not found: choose it above)");

        Head("Art made with this editor");
        string mine = Path.Combine(Settings.ToolFolder, "Theaters");
        if (Directory.Exists(mine))
            foreach (var d in Directory.EnumerateDirectories(mine))
                Add(new Choice(Path.GetFileName(d), "Theater art made here (New theater art…). Copy it into a theater with 'Copy into a theater…'.", Path.Combine(d, "Art"), false));
        _note.Text = _reference ? "Choose the art to take images from (it is only read). 'Art folder…' also opens the Art of an old 4.36/4.37 add-on." : "Choose the art to change.";
    }

    private void Ok()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not Choice c || c.Root == null) return;
        if (c.IsBmsArt && !_reference) { NewArt(c.Label.Split("   ·")[0]); return; }
        Root = c.Root; Label = c.Label.Split("   ·")[0];
        Done();
    }

    private void NewArt(string? suggestion = null)
    {
        var name = Prompt.Ask(this, "New theater art", "Name (a folder ArtEditor\\Theaters\\<name>\\Art is made; it starts empty = all BMS 4.38 images):", suggestion ?? "MyTheater");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = CampaignWorkspace.SafeName(name.Trim());
        Root = Path.Combine(Settings.ToolFolder, "Theaters", name, "Art");
        Directory.CreateDirectory(Root);
        Label = name;
        Done();
    }

    private void Done()
    {
        Settings.Set("data", DataDir);
        DialogResult = true;
    }
}

/// <summary>Richiesta di un testo (InputBox).</summary>
public static class Prompt
{
    public static string? Ask(Window owner, string title, string text, string value)
    {
        var w = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize };
        var tb = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 8) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(tb);
        sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? tb.Text : null;
    }
}
