using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerrainTool.Core.Dds;
using TerrainTool.Core.NewTerrain;

namespace TerrainTool.App;

/// <summary>Una parte del teatro nella lista di "Build theater": stato e dove andare per farla.</summary>
public sealed class BuildRow
{
    public string Part { get; init; } = "";
    public string State { get; init; } = "";
    public string Details { get; init; } = "";
    public string GoLabel { get; init; } = "";
    public Action? Go { get; init; }
}

/// <summary>
/// Schede 8 · Build theater e 9 · Install into BMS: l'elenco delle parti del teatro con il loro stato, la costruzione (l'ex
/// "Assemble") con il rapporto e la .tdf, l'installazione completa in BMS con le copie di sicurezza, il ripristino e il controllo.
/// Piu' i lavori in comune (lista di lavori con barra di avanzamento) e i punti di prova della scheda 5.
/// </summary>
public partial class MainWindow
{
    // ───────────────────────── lavori in comune ─────────────────────────

    /// <summary>Esegue dei lavori uno dopo l'altro con barra e stato; restituisce le note di tutti (null se annullato o con errori).</summary>
    private async Task<List<string>?> RunJobsAsync(IReadOnlyList<TexRow> todo, ProgressBar bar, TextBlock status, Button run, Button? cancel)
    {
        if (_texCts != null || _procCts != null) return null;
        ReadForm();
        SaveProject();
        TextureContext ctx;
        try { ctx = new TextureContext(_project, TbOutBox.Text.Trim()); }
        catch (Exception ex) { Log(ex.Message); return null; }
        _texCts = new CancellationTokenSource();
        run.IsEnabled = false;
        if (cancel != null) cancel.IsEnabled = true;
        int ok = 0, err = 0;
        var all = new List<string>();
        bool stopped = false;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var row = todo[i]; int idx = i;
                string? miss = row.Job.Missing(ctx);
                if (miss != null) { row.Result = "skipped: missing input"; Log($"{row.Name}: skipped, missing {miss}"); all.Add($"{row.Name}: NOT DONE, missing {miss}"); err++; continue; }
                row.Result = "running…";
                Log($"{row.Name}: started");
                var t0 = DateTime.Now;
                var prog = new Progress<double>(f =>
                {
                    bar.Value = (idx + Math.Clamp(f, 0, 1)) / todo.Count;
                    status.Text = $"[{idx + 1}/{todo.Count}] {row.Name} {f * 100:0}%";
                });
                try
                {
                    var (prev, notes) = await Task.Run(() => row.Job.Run(ctx, prog, _texCts.Token), _texCts.Token);
                    row.Preview = prev;
                    row.Notes.Clear(); row.Notes.AddRange(notes);
                    row.Result = $"OK in {DateTime.Now - t0:mm\\:ss}";
                    foreach (var n in notes) Log("  " + n);
                    all.Add($"── {row.Name} ({DateTime.Now - t0:mm\\:ss})");
                    all.AddRange(notes.Select(n => "   " + n));
                    ok++;
                }
                catch (OperationCanceledException) { row.Result = "cancelled"; Log($"{row.Name}: cancelled"); stopped = true; break; }
                catch (Exception ex) { row.Result = "ERROR: " + ex.Message; Log($"{row.Name}: ERROR {ex.Message}"); all.Add($"{row.Name}: ERROR {ex.Message}"); err++; }
            }
        }
        finally
        {
            _texCts.Dispose(); _texCts = null;
            run.IsEnabled = true;
            if (cancel != null) cancel.IsEnabled = false;
            bar.Value = 0;
            status.Text = stopped ? "Cancelled" : err > 0 ? $"Finished: {ok} OK, {err} not done or with errors (see the log)" : $"✓ {ok} job(s) OK";
        }
        return stopped ? null : all;
    }

    private async void OnProbe(object sender, RoutedEventArgs e)
    {
        var job = TextureJob.All.OfType<ProbeJob>().First();
        await RunJobsAsync(new[] { new TexRow { Job = job } }, TexProgressBar, TexStatus, ProbeButton, CancelTexButton);
    }

    // ───────────────────────── 7 · contenuti (oggetti e campagne) ─────────────────────────

    private string InstallRoot => Path.Combine(_project.ProjectFolder, "Output", "Install", AddOnName);

    private void RefreshContent()
    {
        try
        {
            var lines = new List<string>();
            string obj = Path.Combine(InstallRoot, "Terrdata", ObjectsJob.FolderName);
            lines.Add(Directory.Exists(obj) ? $"Objects: made ({Directory.GetLastWriteTime(obj):g}) → {obj}" : "Objects: not made yet (Objective Studio → Run Objects): BMS would use the Korean airbase layouts");
            string edits = ObjectsJob.CampaignEditsFolder(_project);
            int ed = Directory.Exists(edits) ? Directory.EnumerateFiles(edits).Count() : 0;
            lines.Add($"Campaigns: {CampaignFiles().Count} files of the old add-on" + (ed > 0 ? $", {ed} saved by the Campaign Editor ({edits})" : ""));
            ContentStatus.Text = string.Join("\n", lines);
        }
        catch (Exception ex) { ContentStatus.Text = ex.Message; }
    }

    private List<string> CampaignFiles()
    {
        string d = Path.Combine(_project.OldTheaterFolder, "Campaign");
        return Directory.Exists(d) ? Directory.EnumerateFiles(d).Where(f => f.EndsWith(".cam", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tac", StringComparison.OrdinalIgnoreCase)).ToList() : new();
    }

    // ───────────────────────── 8 · Build theater ─────────────────────────

    private bool _buildNameSync;

    private string? CampMapFile()
    {
        string a = Path.Combine(_project.ProjectFolder, "Output", "Art");
        foreach (var f in new[] { Path.Combine(a, "Art", "resource", "campmap.rsc"), Path.Combine(a, "resource", "campmap.rsc") })
            if (File.Exists(f)) return f;
        return null;
    }

    private static DateTime Newest(string dir) => Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
        : DateTime.MinValue;

    private void GoTab(int tab, int sub = -1)
    {
        Tabs.SelectedIndex = tab;
        if (sub >= 0) ContentTabs.SelectedIndex = sub;
    }

    private void RefreshBuild()
    {
        ReadForm();
        _buildNameSync = true;
        BuildNameBox.Text = AssembleTheaterName();
        _buildNameSync = false;
        var rows = new List<BuildRow>();
        try
        {
            // terreno: il controllo della scheda 6 sulla cartella del progetto
            string nt = _project.OutputNewTerrain;
            if (Directory.Exists(nt))
            {
                var res = NewTerrainChecker.Check(nt, _project.Theater, Directory.Exists(_project.KoreaNewTerrain) ? _project.KoreaNewTerrain : null);
                int ok = res.Count(r => r.Status is CheckStatus.Ok or CheckStatus.Present), wrong = res.Count(r => r.Status == CheckStatus.WrongSize);
                // only the files this tool makes stop the build: the generic art comes from Korea during the build, and BMS writes
                // its own caches (LOS250, LOSMin/Max, TerrainLevelingData, LandCoverData, SpecialPolygonData) at the first start
                int miss = AssembleJob.Required.Count(r => !File.Exists(Path.Combine(nt, r)) && !Directory.Exists(Path.Combine(nt, r)));
                rows.Add(new BuildRow
                {
                    Part = "Terrain files", State = miss + wrong == 0 ? "Ready" : "Missing",
                    Details = miss + wrong == 0 ? $"{ok}/{res.Count} present (the others come from Korea during the build or are written by BMS)" : $"{miss} missing, {wrong} wrong size (of {AssembleJob.Required.Length} files this tool makes): make them in tab 5",
                    GoLabel = "Go to Terrain check", Go = () => { GoTab(5); CheckFolderBox.Text = nt; OnRunCheck(this, new RoutedEventArgs()); },
                });
            }
            else rows.Add(new BuildRow { Part = "Terrain files", State = "Missing", Details = "Output\\NewTerrain does not exist yet: tabs 3-5", GoLabel = "Go to Terrain files", Go = () => GoTab(4) });

            // mappa 2D: fatta dalla mappa a colori del satellite
            var map = CampMapFile();
            string color = Path.Combine(nt, "Photoreal", "GlobalColorMap.dds");
            bool old = map != null && File.Exists(color) && File.GetLastWriteTime(color) > File.GetLastWriteTime(map);
            rows.Add(new BuildRow
            {
                Part = "2D map", State = map == null ? "Missing" : old ? "To re-make" : "Ready",
                Details = map == null ? "Not made: the UI would show the Korea map" : old ? "Older than the satellite colour map (GlobalColorMap.dds)" : $"campmap.rsc of {File.GetLastWriteTime(map):g}",
                GoLabel = "Go to 2D map", Go = () => GoTab(6, 0),
            });

            string obj = Path.Combine(InstallRoot, "Terrdata", ObjectsJob.FolderName);
            rows.Add(new BuildRow
            {
                Part = "Objects", State = Directory.Exists(obj) ? "Ready" : "Missing",
                Details = Directory.Exists(obj) ? $"Airbase and objective layouts of the theater ({Directory.GetLastWriteTime(obj):g})" : "Not made: BMS would use the Korean airbase layouts (Objective Studio → Run Objects)",
                GoLabel = "Open Objective Studio", Go = () => LaunchTool("ObjectiveStudio", "Objective Studio"),
            });

            var camps = CampaignFiles();
            rows.Add(new BuildRow
            {
                Part = "Campaigns & TE", State = camps.Count > 0 ? "Ready" : "Missing",
                Details = camps.Count > 0 ? $"{camps.Count} files of the old add-on; the build adds the BMS 4.38 fixes (TE template, TE Builder lists, Ppt.ini, clear weather) and the Campaign Editor saves" : "No Campaign folder in the old add-on",
                GoLabel = "Open Campaign Editor", Go = () => LaunchTool("CampaignEditor", "Campaign Editor"),
            });

            string tdfDir = Path.Combine(_project.OldTheaterFolder, "TerrData", "TheaterDefinition");
            bool hasTdf = Directory.Exists(tdfDir) && Directory.EnumerateFiles(tdfDir, "*.tdf").Any();
            rows.Add(new BuildRow
            {
                Part = "Theater definition", State = hasTdf ? "Ready" : "Missing",
                Details = hasTdf ? $"A new .tdf is made from the old add-on's one; name in BMS: \"{AssembleTheaterName()}\"" : $"No .tdf in {tdfDir}: check the old theater folder in tab 1",
                GoLabel = "Go to tab 1", Go = () => GoTab(0),
            });

            bool sim = TheaterSimData.Enabled(_project);
            int ov = sim ? Directory.EnumerateFiles(TheaterSimData.SimOverridesFolder(_project), "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase)) : 0;
            rows.Add(new BuildRow
            {
                Part = "Sim", State = sim ? "Ready" : "Default",
                Details = sim ? $"The theater's own Sim: BMS Data\\Sim + {ov} SimOverrides file(s)" : "The shared BMS Data\\Sim (no SimOverrides folder)",
                GoLabel = "Open Sim Editor", Go = () => LaunchTool("SimEditor", "Sim Editor"),
            });

            string art = Path.Combine(_project.ProjectFolder, "Output", "Art");
            int artFiles = Directory.Exists(art) ? Directory.EnumerateFiles(art, "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).StartsWith("campmap", StringComparison.OrdinalIgnoreCase)) : 0;
            rows.Add(new BuildRow
            {
                Part = "UI art", State = artFiles > 0 ? "Ready" : "Default",
                Details = artFiles > 0 ? $"{artFiles} files of the theater's art (flags, backgrounds, texts…) in Output\\Art" : "The BMS art (KTO) apart from the 2D map: customise it with the Art Editor",
                GoLabel = "Open Art Editor", Go = () => LaunchTool("ArtEditor", "Art Editor"),
            });

            // la costruzione: c'e'? e' piu' vecchia delle parti?
            if (Directory.Exists(InstallRoot))
            {
                var built = Directory.GetLastWriteTime(InstallRoot);
                var tdf = Directory.EnumerateFiles(InstallRoot, "*_NT.tdf", SearchOption.AllDirectories).FirstOrDefault();
                if (tdf != null) built = File.GetLastWriteTime(tdf);
                var parts = new[] { (map != null ? File.GetLastWriteTime(map) : DateTime.MinValue), Newest(ObjectsJob.CampaignEditsFolder(_project)), Newest(art), Newest(TheaterSimData.SimOverridesFolder(_project)),
                                    File.Exists(Path.Combine(nt, "HeightMaps", "HeightMap.raw")) ? File.GetLastWriteTime(Path.Combine(nt, "HeightMaps", "HeightMap.raw")) : DateTime.MinValue };
                bool stale = parts.Any(t => t > built);
                rows.Add(new BuildRow
                {
                    Part = "Last build", State = stale ? "To re-make" : "Ready",
                    Details = stale ? $"Built {built:g}: some parts changed since then, build again" : $"Built {built:g}",
                    GoLabel = "Open Output\\Install", Go = () => OpenInExplorer(InstallRoot),
                });
                ShowTdf(tdf);
            }
            else { rows.Add(new BuildRow { Part = "Last build", State = "Missing", Details = "Not built yet", GoLabel = "", Go = null }); ShowTdf(null); }
        }
        catch (Exception ex) { Log("Build theater: " + ex.Message); }
        BuildGrid.ItemsSource = rows;
    }

    private void ShowTdf(string? tdf)
    {
        TdfPath.Text = tdf ?? "The .tdf is written by 'Build theater'.";
        TdfPreview.Text = tdf != null && File.Exists(tdf) ? File.ReadAllText(tdf) : "";
    }

    private string AssembleTheaterName()
    {
        try { return AssembleJob.TheaterDisplayName(new TextureContext(_project, TbOutBox.Text.Trim())); }
        catch { return _project.Theater.DisplayName; }
    }

    private void OnBuildNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_buildNameSync || !IsLoaded) return;
        DisplayNameBox.Text = BuildNameBox.Text;      // stesso campo della scheda 1 (Display name)
    }

    private void OnRefreshBuild(object sender, RoutedEventArgs e) => RefreshBuild();

    private void OnOpenInstallFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.Combine(_project.ProjectFolder, "Output", "Install"));
        OpenInExplorer(Path.Combine(_project.ProjectFolder, "Output", "Install"));
    }

    private void OnBuildGo(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BuildRow { Go: { } go }) go();
    }

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        ReadForm();
        SaveProject();
        RefreshBuild();
        var rows = (BuildGrid.ItemsSource as List<BuildRow>) ?? new();
        var stop = rows.Where(r => r.State == "Missing" && r.Part is "Terrain files" or "Theater definition").ToList();
        if (stop.Count > 0)
        {
            MessageBox.Show(this, "The theater cannot be built yet:\n\n" + string.Join("\n", stop.Select(r => $"• {r.Part}: {r.Details}")), "Build theater", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var warn = rows.Where(r => r.State == "Missing" && r.Part is "Objects" or "Campaigns & TE").ToList();
        if (warn.Count > 0 && MessageBox.Show(this, "Some parts are missing:\n\n" + string.Join("\n", warn.Select(r => $"• {r.Part}: {r.Details}")) + "\n\nBuild anyway?",
                "Build theater", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var todo = new List<TexRow>();
        var mapRow = rows.FirstOrDefault(r => r.Part == "2D map");
        if (BuildRemake.IsChecked == true && mapRow != null && mapRow.State != "Ready")
            todo.Add(new TexRow { Job = TextureJob.All.OfType<CampMapJob>().First(j => j.Id == "campmap") });
        todo.Add(new TexRow { Job = TextureJob.All.OfType<AssembleJob>().First() });
        var t0 = DateTime.Now;
        BuildReport.Text = $"Building the theater ({t0:g})…";
        var notes = await RunJobsAsync(todo, BuildProgressBar, BuildStatus, BuildButton, CancelBuildButton);
        BuildReport.Text = notes == null ? "Build cancelled." :
            $"Theater \"{AssembleTheaterName()}\" built in {DateTime.Now - t0:mm\\:ss} → {InstallRoot}\r\n\r\n" + string.Join("\r\n", notes) +
            "\r\n\r\nNext: tab 9 'Install into BMS'.";
        RefreshBuild();
    }

    // ───────────────────────── 9 · Install into BMS ─────────────────────────

    private void RefreshInstall()
    {
        ReadForm();
        try
        {
            var i = new TheaterInstaller(_project);
            var l = new List<string> { $"Theater in BMS: {i.Target}" + (Directory.Exists(i.Target) ? "" : "  (not there yet)") };
            if (i.LstLine != null) l.Add($"BMS theater list: {(i.Listed ? "listed" : "not listed")} ({i.LstLine})");
            l.Add(Directory.Exists(i.Source) ? $"Build: {i.Source} — {i.Changed.Count} files to copy (new or changed)" : "Build: not made yet (tab 8)");
            var b = TheaterInstaller.Backups(_project);
            l.Add(b.Count == 0 ? "Backups: none yet" : $"Backups: {b.Count}, last {Path.GetFileName(b[0].Dir)} ({b[0].Files} files)");
            if (i.Problems.Count > 0) l.Add("⚠ " + string.Join("\n⚠ ", i.Problems));
            InstallInfo.Text = string.Join("\n", l);
        }
        catch (Exception ex) { InstallInfo.Text = ex.Message; }
    }

    private async void OnInstallAll(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        ReadForm();
        SaveProject();
        var inst = new TheaterInstaller(_project);
        if (inst.Problems.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", inst.Problems), "Install into BMS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, inst.Plan() + "\n\nProceed?", "Install into BMS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        InstallAllButton.IsEnabled = false;
        Log("Install into BMS: started → " + inst.Target);
        _texCts = new CancellationTokenSource();
        try
        {
            var prog = new Progress<double>(f => InstallInfo.Text = $"Copying… {f * 100:0}%");
            await Task.Run(() => inst.Run(l => Dispatcher.Invoke(() => Log(l)), prog, _texCts.Token));
            MessageBox.Show(this, "The theater is installed in BMS.\n\nStart the BMS Launcher and choose \"" + AssembleTheaterName() + "\". The first start is slow: BMS rebuilds its cache files.", "Install into BMS");
        }
        catch (OperationCanceledException) { Log("Install into BMS: cancelled"); }
        catch (Exception ex) { Log("Install into BMS: ERROR " + ex.Message); MessageBox.Show(this, ex.Message, "Install into BMS", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _texCts.Dispose(); _texCts = null; InstallAllButton.IsEnabled = true; RefreshInstall(); }
    }

    private async void OnInstallObjects(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        await InstallAsync(true, false, (Button)sender);
        RefreshInstall();
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        string d = TheaterInstaller.BackupsFolder(_project);
        Directory.CreateDirectory(d);
        OpenInExplorer(d);
    }

    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        ReadForm();
        var list = TheaterInstaller.Backups(_project);
        if (list.Count == 0) { MessageBox.Show(this, "There are no backups yet.", "Restore"); return; }
        var lb = new ListBox { Height = 260 };
        foreach (var b in list) lb.Items.Add(new ListBoxItem { Content = $"{Path.GetFileName(b.Dir)}   ({b.Files} files)", Tag = b.Dir });
        lb.SelectedIndex = 0;
        var ok = new Button { Content = "Restore", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 8, 6, 0) };
        var w = new Window
        {
            Title = "Restore a backup", Width = 520, SizeToContent = SizeToContent.Height, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock { Text = "Choose the backup to put back into BMS (each one holds the files that installation replaced; the newest is first):", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) },
                    lb, ok,
                },
            },
        };
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true || (lb.SelectedItem as ListBoxItem)?.Tag is not string dir) return;
        if (MessageBox.Show(this, $"Put back into BMS the files of {Path.GetFileName(dir)}?\nThe files there now are overwritten.", "Restore", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var done = new TheaterInstaller(_project).Restore(dir, Log);
            MessageBox.Show(this, $"{done.Count} files restored.", "Restore");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshInstall();
    }

    private void OnRemoveFromLst(object sender, RoutedEventArgs e)
    {
        ReadForm();
        var i = new TheaterInstaller(_project);
        if (!i.Listed) { MessageBox.Show(this, "The theater is not in BMS's theater list.", "Remove from BMS"); return; }
        if (MessageBox.Show(this, $"Remove the theater from BMS's theater list (theater.lst)?\n\nIt will not appear in the Launcher any more. Its files stay in {i.Target} (delete that folder yourself to remove them); 'Install everything' adds it again.",
                "Remove from BMS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Log(i.RemoveFromLst()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Remove from BMS", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshInstall();
    }

    private void OnCheckInstalled(object sender, RoutedEventArgs e)
    {
        ReadForm();
        var i = new TheaterInstaller(_project);
        string? nt = Directory.Exists(i.Target) ? Directory.EnumerateDirectories(i.Target, "NewTerrain", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (nt == null) { MessageBox.Show(this, $"No NewTerrain folder in {i.Target}: install the theater first.", "Check"); return; }
        GoTab(5);
        CheckFolderBox.Text = nt;
        OnRunCheck(this, new RoutedEventArgs());
    }
}
