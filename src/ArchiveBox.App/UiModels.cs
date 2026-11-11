using System.Collections.ObjectModel;
using System.IO;
using ArchiveBox.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArchiveBox.App;

public partial class FileRow : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedAt { get; init; }
    public ArchiveItem? Item { get; init; }
    public ExpiryDecision? Expiry { get; init; }
    public bool InArchive { get; init; }
    public bool Exists { get; init; } = true;
    public string Icon => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string TypeText => IsDirectory ? "文件夹" : string.IsNullOrEmpty(System.IO.Path.GetExtension(Name))
        ? "文件" : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);
    public string ModifiedText => ModifiedAt.ToString("yyyy-MM-dd HH:mm");
    public string ExpiryText => Expiry?.DisplayDate ?? Item?.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";
    public string RuleSource => Expiry?.SourceText ?? "—";
    public string StatusText => InArchive && Item is null ? Expiry?.ExpiresAt <= DateTimeOffset.UtcNow ? "已到期" : "已归档" : Item is null ? IsDirectory ? "文件夹" : "文件" : Item.State switch
    {
        ItemState.Ready => !Exists ? "文件不可用" : (Expiry?.ExpiresAt ?? Item.ExpiresAt) <= DateTimeOffset.UtcNow ? "已到期" : "已归档",
        ItemState.SourceRetained => "源文件保留",
        ItemState.Failed => "失败 / 已取消",
        ItemState.Pending or ItemState.Verified => "待恢复",
        ItemState.Recycling => "回收待确认",
        ItemState.Recycled => "已移入回收站",
        _ => "未知"
    };
    public string Tooltip => $"{Path}\n{Item?.Error ?? StatusText}";
    public string ParentPath => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string SourceText => Item?.SourcePath ?? Path;

    public static FileRow FromEntry(DirectoryEntry entry) => new()
    { Path = entry.Path, Name = entry.Name, IsDirectory = entry.IsDirectory, Size = entry.Size, ModifiedAt = entry.ModifiedAt };

    public static FileRow FromItem(ArchiveItem item) => new()
    {
        Path = item.FullPath, Name = item.Name, Item = item, Size = item.Size,
        ModifiedAt = item.ArchivedAt.LocalDateTime, Exists = File.Exists(item.FullPath)
    };

    public static FileRow FromArchive(DirectoryEntry entry, ArchiveItem? item, ExpiryDecision expiry) => new()
    { Path = entry.Path, Name = entry.Name, Size = entry.Size, ModifiedAt = entry.ModifiedAt,
        Item = item is null ? null : item with { ExpiresAt = expiry.ExpiresAt }, Expiry = expiry, InArchive = true };

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
    private Task? _loading;
    private readonly Func<string, ExpiryDecision>? _policy;
    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _expiryLabel = "";
    [ObservableProperty] private string _policyDescription = "";

    public FolderNode(string path, bool placeholder = false, Func<string, ExpiryDecision>? policy = null)
    {
        Path = path;
        _policy = policy;
        Name = placeholder ? "加载中…" : System.IO.Path.GetFileName(PathSafety.Normalize(path));
        if (string.IsNullOrEmpty(Name)) Name = path;
        if (!placeholder) Children.Add(new("", true));
        if (!placeholder) RefreshPolicy();
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_loading is not null) { await _loading; return; }
        if ((_loaded && !force) || string.IsNullOrEmpty(Path)) return;
        var work = LoadCoreAsync();
        _loading = work;
        try { await work; } finally { _loading = null; }
    }
    private async Task LoadCoreAsync()
    {
        var folders = await Task.Run(() =>
        {
            PathSafety.RejectLinks(Path);
            return Directory.EnumerateDirectories(Path).Where(p =>
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase).ToArray();
        });
        Children.Clear();
        foreach (var folder in folders) Children.Add(new(folder, policy: _policy));
        _loaded = true;
    }
    public void RefreshPolicy()
    {
        if (_policy is not null)
        {
            var decision = _policy(Path);
            ExpiryLabel = decision.ExpiresAt?.ToLocalTime().ToString("MM-dd") ?? (decision.IsExplicit ? "不设到期" : "");
            PolicyDescription = decision.DisplayDate + " · " + decision.SourceText;
        }
        foreach (var child in Children) if (child.Path.Length > 0) child.RefreshPolicy();
    }
}
