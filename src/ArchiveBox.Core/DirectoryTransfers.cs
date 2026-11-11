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
                var plan = await Task.Run(() =>
                {
                    var scannedDirectories = new List<string> { path };
                    var scannedFiles = new List<string>();
                    for (var i = 0; i < scannedDirectories.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        PathSafety.RejectLinks(scannedDirectories[i]);
                        foreach (var child in Directory.EnumerateFileSystemEntries(scannedDirectories[i]))
                        {
                            PathSafety.RejectLinks(child);
                            if (Directory.Exists(child)) scannedDirectories.Add(child); else scannedFiles.Add(child);
                        }
                    }
                    return Tuple.Create(scannedDirectories, scannedFiles);
                }, token);
                var directories = plan.Item1;
                var files = plan.Item2;
                await Task.Run(() =>
                {
                    foreach (var folder in directories)
                    {
                        token.ThrowIfCancellationRequested();
                        var child = PathSafety.Resolve(root, PathSafety.RelativePath(root,
                            Path.Combine(destination, PathSafety.RelativePath(path, folder))));
                        PathSafety.RejectLinks(child);
                        Directory.CreateDirectory(child);
                    }
                }, token);
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
                    await Task.Run(() =>
                    {
                        foreach (var folder in directories.OrderByDescending(p => p.Length))
                        {
                            token.ThrowIfCancellationRequested();
                            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                        }
                    }, token);
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
