using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TerrainTool.Core.Campaign;
using TerrainTool.Core.Dds;

namespace TerrainTool.App;

/// <summary>
/// Scheda 8 · Sim &amp; SAM: Sim propria del teatro (copia della 4.38 + SimOverrides), modifiche al database (DbEdits.txt)
/// e controllo dei sistemi antiaerei: lanciatori, missili, cadenza di tiro (radar della Sim) e posizioni sugli obiettivi delle campagne.
/// </summary>
public partial class MainWindow
{
    private List<SamDb.SamSystem> _samSystems = new();
    private List<SamPlacement> _samPlaces = new();
    private bool _samBusy;

    private string AddOnName => new DirectoryInfo(_project.OldTheaterFolder).Name;
    private string SimInstallDir => Path.Combine(_project.ProjectFolder, "Output", "Install", AddOnName, TheaterSimData.SimRel);
    private string? BmsSimDir => AssembleJob.FindDataDir(_project.KoreaNewTerrain) is { } d ? Path.Combine(d, "Sim") : null;

    private void RefreshSim()
    {
        string ov = TheaterSimData.SimOverridesFolder(_project);
        var sb = new StringBuilder();
        if (!Directory.Exists(ov)) sb.Append("Theater Sim: OFF (no SimOverrides folder: the theater uses the shared BMS Data\\Sim). 'Copy a BMS Sim file…' turns it on.");
        else
        {
            var files = Directory.EnumerateFiles(ov, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(ov, f))
                .Where(f => !f.StartsWith("README", StringComparison.OrdinalIgnoreCase)).ToList();
            sb.Append($"Theater Sim: ON → <add-on>\\{TheaterSimData.SimRel} = BMS Data\\Sim + {files.Count} SimOverrides file(s): {string.Join(", ", files.DefaultIfEmpty("none"))}");
            sb.Append(Directory.Exists(SimInstallDir) ? $"\nOutput copy: {SimInstallDir} (run 'Build theater' after changing SimOverrides)" : "\nNot assembled yet: run 'Build theater' (tab 8), then 'Install theater Sim into BMS…'");
        }
        SimStatus.Text = sb.ToString();
        LoadDbEdits();
    }

    private void LoadDbEdits()
    {
        string f = TheaterSimData.DbEditsFile(_project);
        DbEditsBox.Text = File.Exists(f) ? File.ReadAllText(f) : "# DbEdits.txt - TABLE Num Field Value   # comment\r\n";
    }

    private void OnDbEditsReload(object sender, RoutedEventArgs e) => LoadDbEdits();

    private void OnAddOnImport(object sender, RoutedEventArgs e)
    {
        string? data = AssembleJob.FindDataDir(_project.KoreaNewTerrain);
        if (string.IsNullOrWhiteSpace(_project.OldTheaterFolder) || !Directory.Exists(_project.OldTheaterFolder))
        { MessageBox.Show(this, "Set the old theater folder in tab 1 first.", "Old add-on", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (data == null)
        { MessageBox.Show(this, "BMS 4.38 not found: set 'Korea NewTerrain' in tab 1 first.", "Old add-on", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new AddOnImportWindow(_project, data) { Owner = this }.ShowDialog();
    }

    private void OnDbEditsSave(object sender, RoutedEventArgs e)
    {
        string f = TheaterSimData.DbEditsFile(_project);
        string tmp = Path.Combine(Path.GetTempPath(), "DbEdits_check.txt");
        try
        {
            File.WriteAllText(tmp, DbEditsBox.Text);
            int n = TheaterSimData.ParseDbEdits(tmp).Count;     // errore di formato: non si salva
            File.WriteAllText(f, DbEditsBox.Text, new UTF8Encoding(false));
            Log($"Sim & SAM: DbEdits.txt saved ({n} changes). Run 'Objects' + 'Build theater' and install the objects to get them into BMS.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "DbEdits.txt", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OnInstallSim(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        await InstallAsync(false, false, (Button)sender, sim: true);
    }

    private void OnSimOpenOverrides(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TheaterSimData.SimOverridesFolder(_project));
        OpenInExplorer(TheaterSimData.SimOverridesFolder(_project));
        RefreshSim();
    }

    private void OnSimCopyFile(object sender, RoutedEventArgs e)
    {
        string? sim = BmsSimDir;
        if (sim == null || !Directory.Exists(sim)) { Log("Sim & SAM: BMS Data\\Sim not found"); return; }
        var dlg = new OpenFileDialog { InitialDirectory = sim, Filter = "Sim data (*.dat;*.lst;*.txt)|*.dat;*.lst;*.txt|All files|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        string ov = TheaterSimData.SimOverridesFolder(_project);
        foreach (var f in dlg.FileNames)
        {
            string full = Path.GetFullPath(f), root = Path.GetFullPath(sim).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Log($"Sim & SAM: {f} is not inside {sim}"); continue; }
            string to = Path.Combine(ov, full[root.Length..]);
            if (File.Exists(to) && MessageBox.Show(this, $"{to}\nalready exists (it may contain your changes). Replace it with the BMS file?", "SimOverrides",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(full, to, true);
            Log($"Sim & SAM: {full[root.Length..]} copied to SimOverrides (edit it there, then 'Build theater' and 'Install theater Sim into BMS…')");
        }
        RefreshSim();
    }

    private async void OnSamCheck(object sender, RoutedEventArgs e)
    {
        if (_samBusy) return;
        _samBusy = true; SamCheckButton.IsEnabled = false;
        ReadForm();
        // le modifiche non salvate di DbEdits.txt valgono per il controllo
        string edits = Path.Combine(Path.GetTempPath(), "DbEdits_check.txt");
        File.WriteAllText(edits, DbEditsBox.Text);
        try
        {
            var p = _project;
            string simDir = Directory.Exists(SimInstallDir) ? SimInstallDir : BmsSimDir ?? "";
            var (systems, places, notes) = await Task.Run(() =>
            {
                var ws = CampaignWorkspace.FromProject(p);
                var db = CampaignDb.Load(ws.DbDirs);
                var s = SamDb.Load(ws.DbDirs, simDir, edits);
                var nts = new List<string>(s.Notes);
                // ogni campagna / TE una volta sola, dalla prima cartella che la contiene (modifiche, Install, originali)
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in ws.SearchDirs().Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(d, "*.cam").Concat(Directory.EnumerateFiles(d, "*.tac")))
                        files.TryAdd(Path.GetFileName(f), f);
                var pl = new List<SamPlacement>();
                foreach (var f in files.Values.OrderBy(x => Path.GetFileName(x)))
                {
                    try { pl.AddRange(SamCheck.Check(f, db, s)); }
                    catch (Exception ex) { nts.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
                }
                var sys = s.Nums("UCD").Select(s.SystemOf).Where(x => x != null).Select(x => x!).ToList();
                foreach (var x in sys)
                {
                    var mine = pl.Where(q => q.Ucd == x.Ucd).ToList();
                    x.Battalions = mine.Count;
                    int bad = mine.Count(q => q.Problem), fb = mine.Count(q => q.Fallback > 0 && !q.Problem);
                    x.Worst = mine.Count == 0 ? "" : bad > 0 ? $"{bad} of {mine.Count} with vehicles off the points" : fb > 0 ? $"all fit ({fb} use points of another type)" : "all fit";
                }
                return (sys, pl, nts);
            });
            _samSystems = systems; _samPlaces = places;
            foreach (var n in notes) Log("Sim & SAM: " + n);
            int bad = places.Count(q => q.Problem && systems.Any(x => x.Ucd == q.Ucd));
            Log($"Sim & SAM: {systems.Count(x => x.Battalions > 0)} SAM battalion types in the campaigns, {places.Count(q => systems.Any(x => x.Ucd == q.Ucd))} battalions, {bad} with vehicles off the base points");
            ApplySamFilter();
        }
        catch (Exception ex) { Log("Sim & SAM: ERROR " + ex.Message); }
        finally { _samBusy = false; SamCheckButton.IsEnabled = true; }
    }

    private void OnSamFilter(object sender, RoutedEventArgs e) => ApplySamFilter();

    private void ApplySamFilter()
    {
        bool all = SamAllTypes.IsChecked == true;
        SamGrid.ItemsSource = _samSystems.Where(x => all || x.Battalions > 0).OrderByDescending(x => x.Battalions).ThenBy(x => x.Missile).ToList();
        OnSamSelected(SamGrid, null!);
    }

    private void OnSamSelected(object sender, SelectionChangedEventArgs e)
    {
        bool only = SamProblemsOnly.IsChecked == true;
        var sel = SamGrid.SelectedItem as SamDb.SamSystem;
        var ucds = sel != null ? new HashSet<int> { sel.Ucd } : _samSystems.Select(x => x.Ucd).ToHashSet();
        SamPlaceGrid.ItemsSource = _samPlaces.Where(q => ucds.Contains(q.Ucd) && (!only || q.Problem)).ToList();
    }
}
