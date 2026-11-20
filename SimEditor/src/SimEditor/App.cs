using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SimEditor;

/// <summary>
/// BMS Sim Editor: the theater's own Sim (BMS Data\Sim + SimOverrides), the database changes (DbEdits.txt) and the check of the
/// air defence systems, for a TerrainTool project. Arguments: --project &lt;file.ttproj&gt; (from TerrainTool); without it, it asks.
/// </summary>
public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args) => new App().Run(args);

    private App()
    {
        // the project window comes after the start dialog: with the default "close on last window" the program would end
        // as soon as the dialog closes, before the project window is shown
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        BmsSuiteUi.WindowShot.Register();   // F12 = PNG of the window (Screenshots folder)
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            try
            {
                string? project = TerrainTool.Core.SuiteArgs.Value(args, "project");
                if (project != null && !File.Exists(project)) { MessageBox.Show("Project not found: " + project, "Sim Editor"); project = null; }
                if (project == null)
                {
                    var start = new StartWindow();
                    if (start.ShowDialog() != true || start.ProjectFile == null) { Shutdown(0); return; }
                    project = start.ProjectFile;
                }
                StartWindow.AddRecent(project);
                var w = new MainWindow(project);
                MainWindow = w;
                w.Show();
                ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "Sim Editor could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        return base.Run();
    }

    public static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n"); }
        catch { /* nulla */ }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(e.Exception.Message, "Sim Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
