using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ArtEditor;

/// <summary>
/// Art Editor per Falcon BMS: la grafica dell'interfaccia di un teatro (bandiere, sfondi, immagini delle campagne, mappe piccole,
/// patch degli squadroni, testi di Theater.irc), su qualunque teatro installato, un progetto di TerrainTool o una cartella Art.
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
                // from the BMS Suite main menu: --theater <name> --bms-data <folder> open the art of that theater
                var w = new MainWindow(TerrainTool.Core.SuiteArgs.Theater(args, Settings.Get("data")));
                MainWindow = w;
                w.Show();
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "Art Editor could not start", MessageBoxButton.OK, MessageBoxImage.Error);
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
