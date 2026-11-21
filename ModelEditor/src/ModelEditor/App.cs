using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ModelEditor;

/// <summary>
/// Model Editor per Falcon BMS 4.38: vista dei modelli BML con DOF, switch e armi negli slot, editor di Parent.dat,
/// controlli su tutti i modelli. Argomenti: [--data &lt;cartella Data di BMS&gt;] [--model &lt;numero del modello&gt;].
/// </summary>
public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args) => new App().Run(args);

    private App()
    {
        // il processo finisce quando non resta nessuna finestra visibile: cosi' non blocca l'exe per il build
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
                // --textures <model> (from the Campaign Editor): only the texture window of that model
                if (int.TryParse(Arg("textures"), out int tex))
                {
                    var tw = new ModelTexturesWindow(null) { StartModel = tex };
                    MainWindow = tw;
                    tw.Show();
                    return;
                }
                // --data/--model (from other programs), or --bms-data/--theater (from the BMS Suite main menu)
                var w = new MainWindow(Arg("data") ?? TerrainTool.Core.SuiteArgs.BmsData(args), int.TryParse(Arg("model"), out int m) ? m : null, TerrainTool.Core.SuiteArgs.TheaterName(args));
                MainWindow = w;
                w.Show();
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "Model Editor could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        };
        return base.Run();
    }

    private void ExitIfNoWindows() => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
    {
        if (!Windows.Cast<Window>().Any(w => w.IsVisible)) Shutdown();
    }));

    public static void Log(Exception ex)
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
