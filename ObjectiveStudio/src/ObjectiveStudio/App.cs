using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ObjectiveStudio;

/// <summary>
/// Objective Studio for Falcon BMS: what is inside the objectives of any installed theater (or of a TerrainTool project).
/// Without arguments it asks which theater to open. Arguments: --project &lt;file.ttproj&gt; [--textures &lt;model number&gt;].
/// </summary>
public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        return app.Run(args);
    }

    private App()
    {
        // il processo finisce quando non resta nessuna finestra visibile: cosi' non resta in memoria a bloccare l'exe per il build
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        // errori fuori dal thread dell'interfaccia (lavori in background): prima chiudevano il programma senza lasciare traccia
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "fatal");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception, "background task"); e.SetObserved(); };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((_, _) => ExitIfNoWindows()));
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            Note("start " + string.Join(" ", args));
            try
            {
                string? Arg(string name)
                {
                    int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("/" + name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                }
                string? project = Arg("project");
                if (project != null && !File.Exists(project)) { MessageBox.Show("Project not found: " + project, "Objective Studio"); project = null; }
                // --textures <model> without a project (from the Campaign Editor): only the texture window
                if (project == null && int.TryParse(Arg("textures"), out int only))
                {
                    var tw = new ModelTexturesWindow(null) { StartModel = only };
                    MainWindow = tw;
                    tw.Show();
                    return;
                }
                if (project == null)
                {
                    var start = new StartWindow();
                    if (start.ShowDialog() != true || start.ProjectFile == null) { Shutdown(0); return; }
                    project = start.ProjectFile;
                }
                var w = new ObjectiveStudio.MainWindow(project);
                MainWindow = w;
                w.Show();
                // --textures <numero del modello>: apre subito le texture di quel modello (dal Campaign Editor)
                if (int.TryParse(Arg("textures"), out int model))
                    new ModelTexturesWindow(project != null ? TerrainTool.Core.Project.TheaterProject.Load(project) : null) { Owner = w, StartModel = model }.Show();
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "Objective Studio could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        Exit += (_, e) => Note($"exit (code {e.ApplicationExitCode})");
        return base.Run();
    }

    /// <summary>Chiude il processo se non c'e' piu' nessuna finestra visibile (controllato dopo che la chiusura e' finita).</summary>
    private void ExitIfNoWindows() => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
    {
        // la finestra principale ancora aperta (anche se per un attimo non visibile, es. durante un dialogo): il programma resta
        if (MainWindow is { IsLoaded: true } && Windows.Cast<Window>().Contains(MainWindow)) return;
        if (Windows.Cast<Window>().Any(w => w.IsVisible)) return;
        Note("closed: no window left");
        Shutdown();
    }));

    /// <summary>Riga in session.log (accanto all'exe): avvio e chiusura, per capire una chiusura inattesa.</summary>
    private static void Note(string what)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "session.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}\n"); }
        catch { /* nulla */ }
    }

    private static void Log(Exception ex, string where = "")
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where} {ex}\n\n"); }
        catch { /* nulla */ }
        Note("error " + where + ": " + ex.Message);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        ExitIfNoWindows();
    }
}
