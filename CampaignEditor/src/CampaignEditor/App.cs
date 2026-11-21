using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CampaignEditor;

/// <summary>
/// Campaign Editor per Falcon BMS: campagne (.cam) e missioni TE (.tac) di qualunque teatro installato, o di un progetto di TerrainTool.
/// Salva anche nelle cartelle Campaign di BMS (avviso e backup in Backup\). Argomenti: --project &lt;file.ttproj&gt; [--open &lt;file .cam/.tac&gt;], oppure --theater &lt;nome&gt; --bms-data &lt;cartella&gt; (dal menu della BMS Suite).
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
        BmsSuiteUi.WindowShot.Register();   // F12 = PNG of the window (Screenshots folder)
        BmsSuiteUi.SuiteTheme.Apply(this);   // the look shared by the suite (Shared\SuiteTheme.cs)
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((_, _) => ExitIfNoWindows()));
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            try
            {
                string? Arg(string name)
                {
                    int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("/" + name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                }
                string? project = Arg("project"), open = Arg("open");
                if (project != null && !File.Exists(project)) { MessageBox.Show("Project not found: " + project, "Campaign Editor"); project = null; }
                // from the BMS Suite main menu: --theater <name> --bms-data <folder> select that theater in the list
                var w = new StartWindow(project, project == null ? TerrainTool.Core.SuiteArgs.TheaterName(args) : null, TerrainTool.Core.SuiteArgs.BmsData(args));
                MainWindow = w;
                w.Show();
                if (open != null && File.Exists(open)) w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => w.OpenEditor(open)));
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "Campaign Editor could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        return base.Run();
    }

    /// <summary>Chiude il processo se non c'e' piu' nessuna finestra visibile (controllato dopo che la chiusura e' finita).</summary>
    private void ExitIfNoWindows() => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
    {
        if (!Windows.Cast<Window>().Any(w => w.IsVisible)) Shutdown();
    }));

    private static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n"); }
        catch { /* nulla */ }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        ExitIfNoWindows();
    }
}
