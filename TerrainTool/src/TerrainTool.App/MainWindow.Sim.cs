using System.Windows;
using System.Windows.Controls;

namespace TerrainTool.App;

/// <summary>
/// The theater's Sim, the database changes (DbEdits.txt) and the SAM check are in the Sim Editor (a separate program, folder
/// SimEditor); here only the install of the theater Sim (tab 9) and the button that opens the Sim Editor on this project.
/// </summary>
public partial class MainWindow
{
    private string AddOnName => new System.IO.DirectoryInfo(_project.OldTheaterFolder).Name;

    private async void OnInstallSim(object sender, RoutedEventArgs e)
    {
        if (_texCts != null || _procCts != null) return;
        await InstallAsync(false, false, (Button)sender, sim: true);
    }

    private void OnOpenSimEditor(object sender, RoutedEventArgs e) => LaunchTool("SimEditor", "Sim Editor");
}
