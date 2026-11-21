using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace UnitsConverter;

/// <summary>4.37 Units Converter: aircraft, vehicles and weapons of an old BMS database into a theater of BMS 4.38.</summary>
public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args) => new App().Run(args);

    private App()
    {
        DispatcherUnhandledException += OnUnhandled;
        BmsSuiteUi.WindowShot.Register();   // F12 = PNG of the window (Screenshots folder)
        BmsSuiteUi.SuiteTheme.Apply(this);   // the look shared by the suite (Shared\SuiteTheme.cs)
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            try { var w = new MainWindow(TerrainTool.Core.SuiteArgs.BmsData(args), TerrainTool.Core.SuiteArgs.TheaterName(args)); MainWindow = w; w.Show(); }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "4.37 Units Converter could not start", MessageBoxButton.OK, MessageBoxImage.Error);
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
        MessageBox.Show(e.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
