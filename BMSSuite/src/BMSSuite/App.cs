using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BMSSuite;

/// <summary>BMS Suite: menu principale degli strumenti per Falcon BMS 4.38 (apre ogni programma, teatro predefinito, backup).</summary>
public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args) => new App().Run(args);

    private App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    private int Run(string[] args)
    {
        Startup += (_, _) =>
        {
            try { var w = new MainWindow(); MainWindow = w; w.Show(); }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show(ex.Message, "BMS Suite could not start", MessageBoxButton.OK, MessageBoxImage.Error);
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
