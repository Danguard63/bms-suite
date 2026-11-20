using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerrainTool.Core.Dds;

namespace TerrainTool.App;

/// <summary>
/// Scheda 7 · Theater content, parte "2D map": la mappa 2D del teatro (campmap) che BMS mostra in Instant Action, TE e campagna.
/// Il resto dell'Art dell'interfaccia (bandiere, sfondi, immagini delle campagne...) si fa con l'Art Editor.
/// </summary>
public partial class MainWindow
{
    private static bool IsArtJob(TextureJob j) => j is CampMapJob || j is TheaterArtJob;

    private List<TexRow>? _mapRows;

    private List<TexRow> MapRows => _mapRows ??= TextureJob.All.Where(IsArtJob)
        .Select(j => new TexRow { Job = j, Run = j.Id == "campmap" }).ToList();

    private void RefreshMap()
    {
        if (MapGrid.ItemsSource == null) MapGrid.ItemsSource = MapRows;
        try
        {
            ReadForm();
            var ctx = new TextureContext(_project, TbOutBox.Text.Trim());
            foreach (var r in MapRows)
                r.Inputs = string.Join("\n", r.Job.Inputs(ctx).Select(i => $"{(i.Path != null ? "✓" : i.Required ? "✗" : "–")} {i.Role}"));
        }
        catch (Exception ex) { Log("2D map: " + ex.Message); }
    }

    private void OnRefreshMap(object sender, RoutedEventArgs e) => RefreshMap();

    private string ArtOut => Path.Combine(_project.ProjectFolder, "Output", "Art", "Art");

    private void OnOpenArtFolder(object sender, RoutedEventArgs e)
    {
        string d = Path.Combine(_project.ProjectFolder, "Output", "Art");
        Directory.CreateDirectory(d);
        OpenInExplorer(d);
    }

    private void OnOpenArtEditor(object sender, RoutedEventArgs e) => LaunchTool("ArtEditor", "Art Editor");

    private async void OnInstallArt(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        await InstallAsync(false, false, (Button)sender, art: true);
    }

    private async void OnRunMap(object sender, RoutedEventArgs e)
    {
        MapGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var todo = MapRows.Where(r => r.Run).ToList();
        if (todo.Count == 0) { Log("No 2D map job selected"); return; }
        await RunJobsAsync(todo, MapProgressBar, MapStatus, RunMapButton, CancelMapButton);
        RefreshMap();
    }
}
