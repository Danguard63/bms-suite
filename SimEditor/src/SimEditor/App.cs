using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SimEditor;

/// <summary>
/// BMS Sim Editor: the theater's own Sim (BMS Data\Sim + SimOverrides), the database changes (DbEdits.txt) and the check of the
/// air defence systems, for a theater installed in BMS or a TerrainTool project. Arguments: --project &lt;file.ttproj&gt;, or --theater/--bms-data (BMS Suite menu); without them, it asks.
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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        Exit += (_, e) => Note($"exit (code {e.ApplicationExitCode})");
        BmsSuiteUi.WindowShot.Register();   // F12 = PNG of the window (Screenshots folder)
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            Note("start " + string.Join(" ", args));
            try
            {
                string? project = TerrainTool.Core.SuiteArgs.Value(args, "project");
                if (project != null && !File.Exists(project)) { MessageBox.Show("Project not found: " + project, "Sim Editor"); project = null; }
                // the theater chosen in the BMS Suite main menu: its work folder
                if (project == null && TerrainTool.Core.SuiteArgs.Theater(args) is { } t)
                {
                    try { project = StartWindow.OpenTheater(t); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Note("theater " + t.Name + ": " + ex.Message); }
                }
                if (project == null)
                {
                    var start = new StartWindow();
                    if (start.ShowDialog() != true || start.ProjectFile == null) { Shutdown(0); return; }
                    project = start.ProjectFile;
                }
                Note("project " + project);
                StartWindow.AddRecent(project);
                var w = new MainWindow(project);
                Note("window made");
                MainWindow = w;
                w.Show();
                Note($"window shown at {w.Left:0},{w.Top:0} {w.ActualWidth:0}x{w.ActualHeight:0}, state {w.WindowState}, visible {w.IsVisible}");
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

    /// <summary>A line in start.log (next to the exe): what happened at the start, to find a start that shows nothing.</summary>
    public static void Note(string s)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "start.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}\n"); }
        catch { /* nulla */ }
    }

    public static void Log(Exception ex)
    {
        Note("error: " + ex.Message);
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
