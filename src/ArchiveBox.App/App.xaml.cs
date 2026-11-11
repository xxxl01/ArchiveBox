using System.IO;
using System.Windows;

namespace ArchiveBox.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArchiveBox");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, @"Local\ArchiveBox.Desktop", out _ownsMutex);
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
            MessageBox.Show($"启动失败：{error.Message}\n\n请检查本地数据目录：{DataFolder}", "归档盒");
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
