using System.IO;
using System.Windows;

namespace ArchiveBox.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private static string? _smokeDataFolder;
    public static bool IsSmokeTest { get; private set; }
    public static bool SmokeFailed { get; set; }
    public static string DataFolder => _smokeDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArchiveBox");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsSmokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        if (IsSmokeTest)
        {
            _smokeDataFolder = Environment.GetEnvironmentVariable("ARCHIVEBOX_SMOKE_DATA")
                ?? Path.Combine(Path.GetTempPath(), "ArchiveBox-smoke-" + Guid.NewGuid().ToString("N"));
            DispatcherUnhandledException += (_, error) =>
            {
                WriteLog(error.Exception);
                SmokeFailed = true;
                error.Handled = true;
                Shutdown(1);
            };
        }
        var mutexName = IsSmokeTest ? @"Local\ArchiveBox.Smoke." + Guid.NewGuid().ToString("N") : @"Local\ArchiveBox.Desktop";
        _instance = new Mutex(true, mutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("归档盒已经在运行，请切换到已打开的窗口。", "归档盒");
            Shutdown();
            return;
        }
        try
        {
            Directory.CreateDirectory(DataFolder);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            WriteLog(error);
            if (!IsSmokeTest) MessageBox.Show($"启动失败：{error.Message}\n\n请检查本地数据目录：{DataFolder}", "归档盒");
            Shutdown(1);
        }
    }

    public static void WriteLog(Exception error)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var path = Path.Combine(DataFolder, $"error-{DateTime.Today:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
            foreach (var old in Directory.GetFiles(DataFolder, "error-*.log").OrderDescending().Skip(14)) File.Delete(old);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
