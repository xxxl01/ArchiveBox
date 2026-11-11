using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using System.Windows;

namespace ArchiveBox.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private static string? _smokeDataFolder;
    public static bool IsSmokeTest { get; private set; }
    public static bool SmokeFailed { get; set; }
    public static bool IsFatalShutdown { get; private set; }
    public static string DataFolder => _smokeDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArchiveBox");

    internal static void Configure(string[] args)
    {
        IsSmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        if (IsSmokeTest)
            _smokeDataFolder = Environment.GetEnvironmentVariable("ARCHIVEBOX_SMOKE_DATA")
                ?? Path.Combine(Path.GetTempPath(), "ArchiveBox-smoke-" + Guid.NewGuid().ToString("N"));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var mutexName = IsSmokeTest ? @"Local\ArchiveBox.Smoke." + Guid.NewGuid().ToString("N") : @"Local\ArchiveBox.Desktop";
            _instance = new Mutex(true, mutexName, out _ownsMutex);
            if (!_ownsMutex)
            {
                if (!IsSmokeTest) MessageBox.Show("归档盒已经在运行，请切换到已打开的窗口。", "归档盒");
                Shutdown();
                return;
            }
            Directory.CreateDirectory(DataFolder);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            ReportFatal(error);
            Shutdown(1);
        }
    }

    public static void ReportFatal(Exception error)
    {
        IsFatalShutdown = true;
        SmokeFailed = true;
        var path = WriteLog(error);
        if (!IsSmokeTest)
        {
            try { MessageBox.Show($"程序遇到错误，需要退出。\n\n{error.GetBaseException().Message}\n\n日志：{path}\n也可以使用程序目录中的 Diagnose-ArchiveBox.cmd 记录启动诊断。", "归档盒", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception dialogError) { WriteLog(dialogError); }
        }
    }

    public static string WriteLog(Exception error)
    {
        var header = $"{DateTimeOffset.Now:O} | ArchiveBox {Assembly.GetExecutingAssembly().GetName().Version} | .NET Framework 4.8 | CLR {Environment.Version} | OS {Environment.OSVersion} | Process {(Environment.Is64BitProcess ? "x64" : "x86")}\n";
        foreach (var directory in new[] { DataFolder, Path.Combine(Path.GetTempPath(), "ArchiveBox") })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"error-{DateTime.Today:yyyy-MM-dd}.log");
                File.AppendAllText(path, header + error + "\n\n", Encoding.UTF8);
                try
                {
                    foreach (var old in Directory.GetFiles(directory, "error-*.log").OrderByDescending(p => p).Skip(14)) File.Delete(old);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or SecurityException) { }
                return path;
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return "无法写入错误日志，请使用 Diagnose-ArchiveBox.cmd 获取退出代码。";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
