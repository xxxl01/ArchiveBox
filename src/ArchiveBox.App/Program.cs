using System.Windows;

namespace ArchiveBox.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            App.Configure(args);
            AppDomain.CurrentDomain.UnhandledException += (_, error) =>
                App.WriteLog(error.ExceptionObject as Exception ?? new Exception(error.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, error) =>
            {
                App.WriteLog(error.Exception);
                error.SetObserved();
            };
            var app = new App();
            app.DispatcherUnhandledException += (_, error) =>
            {
                error.Handled = true;
                App.ReportFatal(error.Exception);
                app.Shutdown(1);
            };
            // Catch failures while loading application resources, before OnStartup.
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception error)
        {
            App.ReportFatal(error);
            return 1;
        }
    }
}
