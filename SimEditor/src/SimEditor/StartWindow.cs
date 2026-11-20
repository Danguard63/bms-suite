using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SimEditor;

/// <summary>Which TerrainTool project to open: the recent ones (Sim Editor's and Objective Studio's), or Browse.</summary>
public sealed class StartWindow : Window
{
    public string? ProjectFile { get; private set; }
    private readonly ListBox _recent = new() { MinHeight = 160 };

    public static string Root() => TerrainTool.Core.SuiteLayout.ProgramFolder()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMS Sim Editor");
    private static string RecentFile => Path.Combine(Root(), "Settings", "projects.txt");

    /// <summary>Recent TerrainTool projects: this program's first, then Objective Studio's (not its installed-theater work folders).</summary>
    public static List<string> Recent()
    {
        var res = new List<string>();
        void Add(string file, string? skipUnder)
        {
            try
            {
                if (!File.Exists(file)) return;
                foreach (var l in File.ReadAllLines(file).Select(x => x.Trim()))
                    if (l.EndsWith(".ttproj", StringComparison.OrdinalIgnoreCase) && File.Exists(l) && !res.Contains(l, StringComparer.OrdinalIgnoreCase)
                        && (skipUnder == null || !Path.GetFullPath(l).StartsWith(skipUnder, StringComparison.OrdinalIgnoreCase)))
                        res.Add(l);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Add(RecentFile, null);
        if (TerrainTool.Core.SuiteLayout.FolderOf("ObjectiveStudio") is { } os)
            Add(Path.Combine(os, "Settings", "projects.txt"), Path.GetFullPath(Path.Combine(os, "Theaters")));
        return res;
    }

    public static void AddRecent(string file)
    {
        try
        {
            var l = Recent().Where(x => !x.Equals(file, StringComparison.OrdinalIgnoreCase)).Prepend(file).Take(12).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllLines(RecentFile, l);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public StartWindow()
    {
        Title = "Sim Editor – open a project";
        Width = 640; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(new TextBlock { Text = "Sim Editor", FontSize = 18, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 10),
            Text = "The theater's own Sim files (SimOverrides), the database changes (DbEdits.txt) and the check of the air defence systems of a TerrainTool project. " +
                   "They go into the theater with Run Objects and Build theater, then Install.",
        });
        sp.Children.Add(new TextBlock { Text = "Recent projects", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var f in Recent()) _recent.Items.Add(new ListBoxItem { Content = $"{Path.GetFileNameWithoutExtension(f)}   –   {Path.GetDirectoryName(f)}", Tag = f, ToolTip = f });
        if (_recent.Items.Count > 0) _recent.SelectedIndex = 0;
        sp.Children.Add(_recent);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var open = new Button { Content = "Open", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var browse = new Button { Content = "Browse project…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        open.Click += (_, _) => Open();
        _recent.MouseDoubleClick += (_, _) => Open();
        browse.Click += (_, _) => Browse();
        bar.Children.Add(open); bar.Children.Add(browse); bar.Children.Add(cancel);
        sp.Children.Add(bar);
        Content = sp;
        open.IsEnabled = _recent.Items.Count > 0;
        _recent.SelectionChanged += (_, _) => open.IsEnabled = _recent.SelectedItem != null;
        _recent.KeyDown += (_, e) => { if (e.Key == Key.Enter) Open(); };
        // the first project selected and the list focused: the highlight shows, Enter opens it
        Loaded += (_, _) => { _recent.Focus(); if (_recent.SelectedItem is ListBoxItem it) it.Focus(); };
    }

    private void Open()
    {
        if (_recent.SelectedItem is ListBoxItem { Tag: string f }) { ProjectFile = f; DialogResult = true; }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "TerrainTool project", Filter = "TerrainTool project (*.ttproj)|*.ttproj" };
        string? last = Recent().FirstOrDefault();
        string? projects = TerrainTool.Core.SuiteLayout.FolderOf("TerrainTool") is { } tt ? Path.Combine(tt, "Projects") : null;
        string? start = last != null ? Path.GetDirectoryName(Path.GetDirectoryName(last)) : projects;
        if (start != null && Directory.Exists(start)) dlg.InitialDirectory = start;
        else if (projects != null && Directory.Exists(projects)) dlg.InitialDirectory = projects;
        if (dlg.ShowDialog(this) == true) { ProjectFile = dlg.FileName; DialogResult = true; }
    }
}
