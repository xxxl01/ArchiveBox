namespace ArchiveBox.Core;

public enum ArchiveMode { Copy, Move }
public enum ConflictPolicy { Rename, Skip }
public enum ItemState { Pending, Verified, Ready, SourceRetained, Failed, Recycling, Recycled }

public sealed record ArchiveItem(
    string Id, string RootPath, string RelativePath, string SourcePath,
    ArchiveMode Mode, DateTimeOffset ArchivedAt, DateTimeOffset? ExpiresAt,
    ItemState State, long Size, string? Sha256, string? Error)
{
    public string FullPath => PathSafety.Resolve(RootPath, RelativePath);
    public string Name => Path.GetFileName(RelativePath);
    public bool IsAvailable => State is ItemState.Ready or ItemState.SourceRetained;
}

public sealed record DirectoryEntry(string Path, string Name, bool IsDirectory, long Size, DateTime ModifiedAt);
public sealed record RecentFolder(string Path, DateTimeOffset OpenedAt);
public sealed record TransferProgress(string Name, int Index, int Count, long Bytes, long TotalBytes);
public sealed record TransferResult(string Source, string? Destination, bool Success, bool Skipped, string? Error);

public interface IRecycleBin
{
    void Recycle(string path);
}
