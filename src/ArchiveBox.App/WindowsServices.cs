using System.Diagnostics;
using System.IO;
using System.Windows;
using ArchiveBox.Core;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace ArchiveBox.App;

public sealed class WindowsRecycleBin : IRecycleBin
{
    // AllDialogs deliberately avoids FOF_NOCONFIRMATION: never auto-accept a Shell
    // fallback warning when the volume or file cannot use the Recycle Bin.
    public void Recycle(string path) => FileSystem.DeleteFile(path, UIOption.AllDialogs,
        RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
}

public static class WindowsServices
{
    public static string? PickFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public static bool Confirm(string message) => MessageBox.Show(Application.Current.MainWindow,
        message, "归档盒", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件或目录不存在，请检查磁盘连接。");
        // Windows file names cannot contain quotes. Keep commas inside the quoted shell path.
        var info = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"");
        Process.Start(info);
    }

    public static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件或目录不存在，请检查磁盘连接。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
