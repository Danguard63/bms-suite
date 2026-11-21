using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TerrainTool.App;

public partial class App : Application
{
    /// <summary>
    /// Avvio: la finestra del progetto.
    /// (StartupUri non si puo' azzerare: la finestra iniziale si apre qui.)
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BmsSuiteUi.WindowShot.Register();   // F12 = PNG of the window (Screenshots folder)
        BmsSuiteUi.SuiteTheme.Apply(this);   // the look shared by the suite (Shared\SuiteTheme.cs)
        // il processo finisce quando non resta nessuna finestra visibile (anche se una finestra e' stata creata ma mai mostrata,
        // o se l'avvio fallisce): cosi' non resta in memoria a bloccare TerrainTool.exe per il build
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler((_, _) => ExitIfNoWindows()));
        try
        {
            // il campaign editor e' ora un programma a parte (cartella CampaignEditor)
            // from the BMS Suite main menu: --theater <name> opens the project of that theater, if there is one
            Window w = new TerrainTool.App.MainWindow(global::TerrainTool.Core.SuiteArgs.TheaterName(e.Args));
            MainWindow = w;
            w.Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show(ex.Message, "TerrainTool could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}\n\n");
        }
        catch { /* nulla */ }
        MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        ExitIfNoWindows();   // errore prima che una finestra si aprisse: non lasciare il processo appeso
    }
}
