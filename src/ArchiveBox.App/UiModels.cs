using System.Collections.ObjectModel;
using System.IO;
using ArchiveBox.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArchiveBox.App;

public partial class FileRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedAt { get; init; }
    public ArchiveItem? Item { get; init; }
    public bool Exists { get; init; } = true;
    public string Icon => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string TypeText => IsDirectory ? "文件夹" : string.IsNullOrEmpty(System.IO.Path.GetExtension(Name))
        ? "文件" : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);
    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");
    public string ExpiryText => Item?.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";
    public string StatusText => Item is null ? IsDirectory ? "双击打开" : "待整理" : Item.State switch
    {
        ItemState.Ready => !Exists ? "文件不可用" : Item.ExpiresAt <= DateTimeOffset.UtcNow ? "已到期" : "已归档",
        ItemState.SourceRetained => "源文件保留",
        ItemState.Failed => "失败 / 已取消",
        ItemState.Pending or ItemState.Verified => "待恢复",
        ItemState.Recycling => "回收待确认",
        ItemState.Recycled => "已移入回收站",
        _ => "未知"
    };
    public string Tooltip => $"{Path}\n{Item?.Error ?? StatusText}";
    public string SourceText => Item?.SourcePath ?? Path;

    public static FileRow FromEntry(DirectoryEntry entry) => new()
    { Path = entry.Path, Name = entry.Name, IsDirectory = entry.IsDirectory, Size = entry.Size, ModifiedAt = entry.ModifiedAt };

    public static FileRow FromItem(ArchiveItem item) => new()
    {
        Path = item.FullPath, Name = item.Name, Item = item, Size = item.Size,
        ModifiedAt = item.ArchivedAt.LocalDateTime, Exists = File.Exists(item.FullPath)
    };

    public static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{size / (1024d * 1024):0.#} MB",
        _ => $"{size / (1024d * 1024 * 1024):0.##} GB"
    };
}

public partial class FolderNode : ObservableObject
{
    private bool _loaded;
    private bool _loading;
    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public FolderNode(string path, bool placeholder = false)
    {
        Path = path;
        Name = placeholder ? "加载中…" : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
        if (!placeholder) Children.Add(new("", true));
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_loading || (_loaded && !force) || string.IsNullOrEmpty(Path)) return;
        _loading = true;
        try
        {
            var folders = await Task.Run(() =>
            {
                PathSafety.RejectLinks(Path);
                return Directory.EnumerateDirectories(Path).Where(p =>
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                    .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            });
            Children.Clear();
            foreach (var folder in folders) Children.Add(new(folder));
            _loaded = true;
        }
        finally { _loading = false; }
    }
}
