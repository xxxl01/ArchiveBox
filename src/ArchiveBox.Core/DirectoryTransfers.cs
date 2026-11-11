namespace ArchiveBox.Core;

public sealed partial class ArchiveService
{
    public async Task<IReadOnlyList<TransferResult>> TransferSelectionAsync(IEnumerable<string> paths, string root,
        string target, ArchiveMode mode, ConflictPolicy conflict, IProgress<TransferProgress>? progress = null,
        CancellationToken token = default)
    {
        root = PathSafety.Normalize(root);
        target = PathSafety.Resolve(root, PathSafety.RelativePath(root, target));
        var selected = paths.Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Selecting a folder and its descendants must not transfer the same data twice.
        selected = selected.Where(p => !selected.Any(other => !string.Equals(other, p, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(other) && IsInside(other, p))).ToArray();
        var results = new List<TransferResult>();
        foreach (var path in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
            {
                results.AddRange(await TransferAsync([path], root, target, mode, conflict, null, progress, token));
                continue;
            }
            try
            {
                PathSafety.RejectLinks(path);
                if (IsInside(path, target)) throw new IOException("不能把文件夹复制或移动到它自身或它的子目录。");
                PathSafety.RejectLinks(target);
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException("目标归档目录不可用。");
                var destination = Path.Combine(target, Path.GetFileName(path));
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (conflict == ConflictPolicy.Skip) { results.Add(new(path, null, false, true, "已有同名文件夹。")); continue; }
                    destination = FindAvailableName(destination);
                }
                var directories = new List<string> { path };
                var files = new List<string>();
                for (var i = 0; i < directories.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    PathSafety.RejectLinks(directories[i]);
                    foreach (var child in Directory.EnumerateFileSystemEntries(directories[i]))
                    {
                        PathSafety.RejectLinks(child);
                        if (Directory.Exists(child)) directories.Add(child); else files.Add(child);
                    }
                }
                foreach (var folder in directories)
                {
                    token.ThrowIfCancellationRequested();
                    var child = PathSafety.Resolve(root, PathSafety.RelativePath(root,
                        Path.Combine(destination, PathSafety.RelativePath(path, folder))));
                    PathSafety.RejectLinks(child);
                    Directory.CreateDirectory(child);
                }
                var completed = new List<TransferResult>();
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var childTarget = Path.Combine(destination, Path.GetDirectoryName(PathSafety.RelativePath(path, file)) ?? "");
                    completed.AddRange(await TransferAsync([file], root, childTarget, mode, conflict, null, progress, token));
                }
                results.AddRange(completed);
                if (mode == ArchiveMode.Move)
                {
                    // Only remove empty directories; new or failed files keep their source folders intact.
                    foreach (var folder in directories.OrderByDescending(p => p.Length))
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                if (files.Count == 0) results.Add(new(path, destination, true, false, null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { results.Add(new(path, null, false, false, error.Message)); }
        }
        return results;
    }

    public async Task<ArchiveItem> RegisterExistingAsync(string root, string path, CancellationToken token = default)
    {
        root = PathSafety.Normalize(root);
        path = PathSafety.Resolve(root, PathSafety.RelativePath(root, path));
        PathSafety.RejectLinks(path);
        var existing = repository.GetItems().FirstOrDefault(i => i.IsAvailable && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", path);
        var item = new ArchiveItem(Guid.NewGuid().ToString("N"), root, PathSafety.RelativePath(root, path), path,
            ArchiveMode.Copy, DateTimeOffset.UtcNow, null, ItemState.Ready, info.Length, await HashAsync(path, token), null);
        repository.Save(item);
        return item;
    }

    private static bool IsInside(string root, string path)
    {
        try { PathSafety.Resolve(root, PathSafety.RelativePath(root, path)); return true; }
        catch (IOException) { return false; }
    }
}
