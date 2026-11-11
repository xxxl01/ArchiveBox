using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArchiveBox.Core;

namespace ArchiveBox.App;

internal sealed class SmokeDialogs : IUserDialogs
{
    public string? NextFolder { get; set; }
    public string? NextName { get; set; }
    public RetentionChoice? NextExpiry { get; set; }
    public bool CancelTransfer { get; set; }
    public int Confirmations { get; private set; }
    public ArchiveMode LastMode { get; private set; }
    public string? PickFolder(string title, string initial) { var value = NextFolder; NextFolder = null; return value; }
    public string? NewFolder(string parent) { var value = NextName; NextName = null; return value; }
    public ConflictPolicy? ConfirmTransfer(ArchiveMode mode, int count, string target, ConflictPolicy conflict)
    { Confirmations++; LastMode = mode; if (CancelTransfer) { CancelTransfer = false; return null; } return conflict; }
    public RetentionChoice? ChooseExpiry(string title, string description, ExpiryDecision current)
    { var choice = NextExpiry; NextExpiry = null; return choice; }
    public bool Confirm(string message) => true;
    public void Error(string message) => throw new InvalidOperationException(message);
}

internal static class UiRegression
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task RunAsync(MainWindow window)
    {
        var vm = window.ViewModel; var dialogs = window.TestDialogs!;
        var data = App.DataFolder;
        var source = Path.Combine(data, "source"); var archive = Path.Combine(data, "archive");
        Directory.CreateDirectory(source); Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(source, "复制示例.txt"), "copy fixture");
        File.WriteAllText(Path.Combine(source, "移动示例.txt"), "move fixture");
        Directory.CreateDirectory(Path.Combine(source, "项目文件夹", "子文件夹"));
        File.WriteAllText(Path.Combine(source, "项目文件夹", "子文件夹", "资料.txt"), "nested fixture");
        for (var i = 1; i <= 35; i++) Directory.CreateDirectory(Path.Combine(archive, $"归档分类 {i:00}"));
        File.WriteAllText(Path.Combine(archive, "已有文件.txt"), "existing archive file");
        Check(vm.IsHome, "Startup must open Home.");
        await vm.SwitchViewCommand.ExecuteAsync("settings");
        dialogs.NextFolder = archive; await vm.ChooseRootCommand.ExecuteAsync(null);
        Check(vm.ArchiveRoot == archive, "Settings must configure the single archive root.");
        await Capture(window, "settings-1360.png", 1360, 860);
        await vm.NavigateCommand.ExecuteAsync(source);
        vm.AddCurrentToHomeCommand.Execute(null);
        var repository = new ArchiveRepository(Path.Combine(data, "archivebox.db"));
        Check(repository.GetFavorites().Any(f => f.Path == source), "Home favorites must persist.");
        await vm.SwitchViewCommand.ExecuteAsync("home");
        await Capture(window, "home-1360.png", 1360, 860);
        await vm.NavigateCommand.ExecuteAsync(source);
        Select(vm, "复制示例.txt");
        dialogs.CancelTransfer = true; await vm.CopyCommand.ExecuteAsync(null);
        Check(!File.Exists(Path.Combine(archive, "复制示例.txt")), "Cancelling copy must not write files.");
        await vm.CopyCommand.ExecuteAsync(null);
        Check(dialogs.LastMode == ArchiveMode.Copy && File.Exists(Path.Combine(source, "复制示例.txt")) && File.Exists(Path.Combine(archive, "复制示例.txt")), "Copy must confirm and preserve source.");
        Select(vm, "移动示例.txt"); await vm.MoveCommand.ExecuteAsync(null);
        Check(dialogs.LastMode == ArchiveMode.Move && !File.Exists(Path.Combine(source, "移动示例.txt")) && File.Exists(Path.Combine(archive, "移动示例.txt")), "Move must confirm and remove verified source.");
        Select(vm, "项目文件夹");
        Check(vm.CopyCommand.CanExecute(null), "Folder selection must enable copy.");
        await vm.CopyCommand.ExecuteAsync(null);
        Check(File.Exists(Path.Combine(archive, "项目文件夹", "子文件夹", "资料.txt")), "Folder copy must preserve hierarchy.");
        dialogs.NextName = "新建分类/年度资料"; await vm.CreateFolderCommand.ExecuteAsync(null);
        Check(Directory.Exists(vm.TargetFolder), "New-folder dialog must create and select the destination.");
        Select(vm, "项目文件夹");
        await Capture(window, "browse-1360.png", 1360, 860);
        await Capture(window, "browse-1080.png", 1080, 640);
        var right = (FrameworkElement)window.FindName("TargetPanel");
        var copy = (FrameworkElement)window.FindName("CopyButton");
        var move = (FrameworkElement)window.FindName("MoveButton");
        Check(VisibleInside(copy, right) && VisibleInside(move, right), "Copy and move buttons must be visible without scrolling.");
        var tree = (FrameworkElement)window.FindName("TargetFoldersTree");
        Check(VisualChildren<TreeViewItem>(tree).Any(item => item.IsSelected && VisibleInside(item, tree)), "The chosen target must be brought into the visible tree viewport.");
        for (DependencyObject? parent = VisualTreeHelper.GetParent(tree); parent is not null && parent != right; parent = VisualTreeHelper.GetParent(parent))
            Check(parent is not ScrollViewer, "Destination tree must not have an outer scrolling panel.");
        await vm.SwitchViewCommand.ExecuteAsync("all");
        Check(vm.Rows.Any(r => r.Name == "已有文件.txt"), "Archive page must show existing untracked files.");
        Check(!right.IsVisible, "Archive page must not show a second destination panel.");
        var folderDate = DateTimeOffset.UtcNow.AddDays(8);
        dialogs.NextExpiry = new(false, folderDate); await vm.SetFolderExpiryCommand.ExecuteAsync(null);
        Check(vm.Rows.All(r => r.Expiry?.ExpiresAt == folderDate && r.Expiry.IsExplicit == false), "Folder expiry must flow to files.");
        Select(vm, "复制示例.txt");
        dialogs.NextExpiry = new(false, null); await vm.SetFileExpiryCommand.ExecuteAsync(null);
        Check(vm.Rows.Single(r => r.Name == "复制示例.txt").Expiry is { IsExplicit: true, ExpiresAt: null }, "File no-expiry override must win over folder expiry.");
        Select(vm, "复制示例.txt"); dialogs.NextExpiry = new(true, null); await vm.SetFileExpiryCommand.ExecuteAsync(null);
        Check(vm.Rows.Single(r => r.Name == "复制示例.txt").Expiry?.ExpiresAt == folderDate, "Clearing a file override must restore inheritance.");
        await Capture(window, "archive-1080.png", 1080, 640);
        await Capture(window, "archive-1360.png", 1360, 860);
        var folderColumn = (ColumnDefinition)window.FindName("ArchiveFoldersColumn");
        folderColumn.Width = new GridLength(280);
        await vm.SwitchViewCommand.ExecuteAsync("browse");
        window.UpdateLayout();
        Check(folderColumn.ActualWidth < 1, "Resizing archive folders must not leave a blank column on other pages.");
        foreach (var view in new[] { "soon", "due", "history", "home" }) await vm.SwitchViewCommand.ExecuteAsync(view);
        Check(dialogs.Confirmations == 4, "Every copy/move click must request confirmation exactly once.");
        Check(!App.SmokeFailed, "No unexpected managed errors may occur.");
        File.WriteAllText(Path.Combine(data, "smoke-success.txt"), "Home persistence, root settings, copy/move confirmation and cancellation, directory copy, folder/file expiry inheritance, minimum-window layout and screenshots passed.");
    }
    private static void Select(MainViewModel vm, string name)
    { foreach (var row in vm.Rows) row.IsSelected = row.Name == name; vm.SelectionChanged(); }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var descendant in VisualChildren<T>(child)) yield return descendant; }
    }
    private static bool VisibleInside(FrameworkElement child, FrameworkElement parent)
    {
        var point = child.TranslatePoint(new Point(), parent);
        return child.IsVisible && point.X >= 0 && point.Y >= 0 && point.X + child.ActualWidth <= parent.ActualWidth + 1 && point.Y + child.ActualHeight <= parent.ActualHeight + 1;
    }
    private static async Task Capture(MainWindow window, string name, double width, double height)
    {
        window.ViewModel.NoticeText = ""; window.Width = width; window.Height = height; window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(App.DataFolder, "screenshots"); Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, name)); encoder.Save(output);
    }
}
