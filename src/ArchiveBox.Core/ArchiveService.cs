using System.Security.Cryptography;

namespace ArchiveBox.Core;

public sealed class ArchiveService(ArchiveRepository repository)
{
    private const int BufferSize = 128 * 1024;

    public async Task<IReadOnlyList<TransferResult>> TransferAsync(
        IEnumerable<string> sources, string root, string targetFolder, ArchiveMode mode,
        ConflictPolicy conflict, DateTimeOffset? expiresAt, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        root = PathSafety.Normalize(root);
        targetFolder = PathSafety.Resolve(root, Path.GetRelativePath(root, targetFolder));
        PathSafety.RejectLinks(targetFolder);
        if (!Directory.Exists(root) || !Directory.Exists(targetFolder))
            throw new DirectoryNotFoundException("归档目录不可用，请检查磁盘连接后重试。");
        var files = sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<TransferResult>();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TransferOneAsync(files[index], root, targetFolder, mode, conflict, expiresAt,
                index + 1, files.Length, progress, cancellationToken));
        }
        return results;
    }

    private async Task<TransferResult> TransferOneAsync(string source, string root, string folder,
        ArchiveMode mode, ConflictPolicy conflict, DateTimeOffset? expiry, int index, int count,
        IProgress<TransferProgress>? progress, CancellationToken token)
    {
        ArchiveItem? item = null;
        string? staging = null;
        var promoted = false;
        try
        {
            PathSafety.RejectLinks(source);
            if (!File.Exists(source)) throw new FileNotFoundException("源文件不存在。", source);
            var target = Path.Combine(folder, Path.GetFileName(source));
            if (PathSafety.Normalize(target).Equals(PathSafety.Normalize(source), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("源文件和目标文件相同，请选择另一个归档目录。");
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (conflict == ConflictPolicy.Skip) return new(source, null, false, true, "目标目录已有同名项目。");
                target = FindAvailableName(target);
            }
            item = new(Guid.NewGuid().ToString("N"), root, Path.GetRelativePath(root, target), source,
                mode, DateTimeOffset.UtcNow, expiry, ItemState.Pending, new FileInfo(source).Length, null, null);
            repository.Save(item); // Durable intent before touching file contents.
            staging = Path.Combine(folder, $".archivebox-{item.Id}.partial");
            string hash;
            // On Windows FileShare.Read prevents writes/renames while the source is copied.
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[BufferSize];
                    long copied = 0;
                    long lastReported = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        digest.AppendData(buffer, 0, read);
                        copied += read;
                        if (Environment.TickCount64 - lastReported >= 100 || copied == input.Length)
                        {
                            progress?.Report(new(item.Name, index, count, copied, input.Length));
                            lastReported = Environment.TickCount64;
                        }
                    }
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                    hash = Convert.ToHexString(digest.GetHashAndReset());
                    item = item with { Size = copied };
                }
                if (!string.Equals(hash, await HashAsync(staging, token), StringComparison.Ordinal))
                    throw new IOException("复制后的内容校验失败，源文件已保留。");
            }
            File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(source));
            item = item with { State = ItemState.Verified, Sha256 = hash };
            repository.Save(item); // Recovery can now identify our complete destination by its digest.
            PathSafety.RejectLinks(folder);
            token.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: false); // A race cannot overwrite another file.
            promoted = true;
            item = item with { State = mode == ArchiveMode.Move ? ItemState.SourceRetained : ItemState.Ready,
                Error = mode == ArchiveMode.Move ? "归档副本已就绪，源文件尚未移除。" : null };
            repository.Save(item);

            if (mode == ArchiveMode.Move)
            {
                try
                {
                    // Recheck after releasing the copy handle. Never delete an edited source.
                    // Keep the destination open without write/delete sharing until source removal completes.
                    await using var destinationCheck = new FileStream(target, FileMode.Open, FileAccess.Read,
                        FileShare.Read, BufferSize, FileOptions.Asynchronous);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(destinationCheck, CancellationToken.None)) != hash)
                        throw new IOException("归档副本发生变化，已保留源文件。");
                    PathSafety.RejectLinks(source);
                    await using (var check = new FileStream(source, FileMode.Open, FileAccess.Read,
                                     FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous))
                    {
                        var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(check, CancellationToken.None));
                        if (currentHash != hash) throw new IOException("复制后源文件发生了变化，已保留源文件。");
                        File.Delete(source);
                    }
                    item = item with { State = ItemState.Ready, Error = null };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    item = item with { State = ItemState.SourceRetained, Error = $"归档副本已保存；源文件保留：{error.Message}" };
                }
                repository.Save(item);
            }
            progress?.Report(new(item.Name, index, count, item.Size, item.Size));
            return new(source, target, true, false, item.Error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (item is not null)
                repository.Save(item with { State = promoted ? ItemState.SourceRetained : ItemState.Failed,
                    Error = error is OperationCanceledException ? "操作已取消，源文件已保留。" : error.Message });
            if (error is OperationCanceledException) throw;
            return new(source, promoted ? item?.FullPath : null, false, false, error.Message);
        }
        finally
        {
            if (staging is not null)
            {
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Recover on next launch. */ }
            }
        }
    }

    public async Task<IReadOnlyList<string>> RecoverAsync(CancellationToken token = default)
    {
        var messages = new List<string>();
        foreach (var item in repository.GetItems().Where(i => i.State is ItemState.Pending or ItemState.Verified or ItemState.Recycling))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                PathSafety.RejectLinks(item.RootPath);
                if (!Directory.Exists(item.RootPath))
                {
                    messages.Add($"{item.Name}：归档磁盘未连接，稍后刷新时重试恢复。");
                    continue;
                }
                PathSafety.RejectLinks(Path.GetDirectoryName(item.FullPath)!);
                if (item.State == ItemState.Recycling)
                {
                    repository.Save(item with { State = File.Exists(item.FullPath) ? ItemState.Ready : ItemState.Recycled,
                        Error = File.Exists(item.FullPath) ? "上次回收操作中断，请检查后重试。" : null });
                    continue;
                }
                PathSafety.RejectLinks(item.FullPath);
                var verified = item.State == ItemState.Verified && item.Sha256 is not null &&
                    File.Exists(item.FullPath) && await HashAsync(item.FullPath, token) == item.Sha256;
                var state = verified ? (item.Mode == ArchiveMode.Move && File.Exists(item.SourcePath)
                    ? ItemState.SourceRetained : ItemState.Ready) : ItemState.Failed;
                repository.Save(item with { State = state, Error = verified
                    ? state == ItemState.SourceRetained ? "已恢复归档副本；源文件保留，请人工核对。" : null
                    : "上次归档中断，未确认完成；源文件未被自动删除，请检查后重试。" });
                var staging = Path.Combine(Path.GetDirectoryName(item.FullPath)!, $".archivebox-{item.Id}.partial");
                PathSafety.RejectLinks(staging);
                if (File.Exists(staging)) File.Delete(staging);
                messages.Add($"{item.Name}：已检查中断任务。");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                messages.Add($"{item.Name}：暂时无法恢复，{error.Message}");
            }
        }
        return messages;
    }

    public async Task RecycleAsync(string id, IRecycleBin recycleBin, CancellationToken token = default)
    {
        var item = repository.Get(id);
        if (!item.IsAvailable) throw new IOException("该文件当前不能移入回收站。");
        PathSafety.RejectLinks(item.FullPath);
        if (item.Sha256 is null || !File.Exists(item.FullPath)) throw new IOException("归档文件不存在或缺少校验信息。");
        if (await HashAsync(item.FullPath, token) != item.Sha256)
            throw new IOException("文件在归档后被修改，为避免误删，本次未移入回收站。请先核对文件。");
        token.ThrowIfCancellationRequested();
        repository.Save(item with { State = ItemState.Recycling });
        try
        {
            recycleBin.Recycle(item.FullPath);
            repository.Save(item with { State = ItemState.Recycled, Error = null });
        }
        catch
        {
            repository.Save(item with { State = File.Exists(item.FullPath) ? item.State : ItemState.Recycled });
            throw;
        }
    }

    private static string FindAvailableName(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var number = 1; number < 100_000; number++)
        {
            var candidate = Path.Combine(directory, $"{name} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("同名文件过多，请换一个归档目录。");
    }

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
}
