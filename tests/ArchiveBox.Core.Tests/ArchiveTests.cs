using ArchiveBox.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArchiveBox.Core.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "archivebox-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_base, "source");
    private string Root => Path.Combine(_base, "archive");
    private string Db => Path.Combine(_base, "data", "test.db");
    private readonly ArchiveRepository _repository;
    private readonly ArchiveService _service;

    public ArchiveTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Root);
        _repository = new(Db);
        _service = new(_repository);
    }

    private string MakeFile(string name = "报告.txt", string content = "hello 归档盒")
    {
        var path = Path.Combine(Source, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<IReadOnlyList<TransferResult>> Transfer(string path, ArchiveMode mode = ArchiveMode.Copy,
        ConflictPolicy conflict = ConflictPolicy.Rename, DateTimeOffset? expiry = null, string? target = null,
        CancellationToken token = default, IProgress<TransferProgress>? progress = null) =>
        _service.TransferAsync([path], Root, target ?? Root, mode, conflict, expiry, progress, token);

    [Fact]
    public async Task Copy_PreservesSourceAndPersistsVerifiedArchive()
    {
        var source = MakeFile();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var result = Assert.Single(await Transfer(source, expiry: expiry));
        Assert.True(result.Success);
        Assert.True(File.Exists(source));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.Destination!));
        var item = Assert.Single(new ArchiveRepository(Db).GetItems());
        Assert.Equal(ItemState.Ready, item.State);
        Assert.Equal(expiry, item.ExpiresAt);
        Assert.Equal(await ArchiveService.HashAsync(source), item.Sha256);
    }

    [Fact]
    public async Task Move_OnlyRemovesSourceAfterVerifiedDestination()
    {
        var source = MakeFile(content: new string('x', 300_000));
        var beforeHash = await ArchiveService.HashAsync(source);
        var result = Assert.Single(await Transfer(source, ArchiveMode.Move));
        Assert.True(result.Success);
        Assert.False(File.Exists(source));
        Assert.Equal(beforeHash, await ArchiveService.HashAsync(result.Destination!));
        Assert.Equal(ItemState.Ready, Assert.Single(_repository.GetItems()).State);
    }

    [Fact]
    public async Task Rename_DoesNotOverwriteExistingFile()
    {
        var source = MakeFile();
        var existing = Path.Combine(Root, Path.GetFileName(source));
        File.WriteAllText(existing, "existing");
        var result = Assert.Single(await Transfer(source));
        Assert.True(result.Success);
        Assert.Equal("报告 (1).txt", Path.GetFileName(result.Destination));
        Assert.Equal("existing", File.ReadAllText(existing));
    }

    [Fact]
    public async Task DirectoryNameCollision_IsAlsoRenamed()
    {
        var source = MakeFile();
        Directory.CreateDirectory(Path.Combine(Root, Path.GetFileName(source)));
        var result = Assert.Single(await Transfer(source));
        Assert.True(result.Success);
        Assert.Equal("报告 (1).txt", Path.GetFileName(result.Destination));
    }

    [Fact]
    public async Task Skip_OnNameConflict_DoesNotMoveSource()
    {
        var source = MakeFile();
        File.WriteAllText(Path.Combine(Root, Path.GetFileName(source)), "existing");
        var result = Assert.Single(await Transfer(source, ArchiveMode.Move, ConflictPolicy.Skip));
        Assert.True(result.Skipped);
        Assert.False(result.Success);
        Assert.True(File.Exists(source));
        Assert.Empty(_repository.GetItems());
    }

    [Fact]
    public async Task ExpiryChanges_OnlyAffectArchiveMetadata()
    {
        var source = MakeFile();
        await Transfer(source);
        var item = Assert.Single(_repository.GetItems());
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        _repository.SetExpiry([item.Id], expiry);
        Assert.Equal(expiry, _repository.Get(item.Id).ExpiresAt);
        _repository.SetExpiry([item.Id], null);
        Assert.Null(_repository.Get(item.Id).ExpiresAt);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Recycle_OnlyTouchesArchivedCopy()
    {
        var source = MakeFile();
        await Transfer(source);
        var item = Assert.Single(_repository.GetItems());
        var bin = new FakeBin(Path.Combine(_base, "bin"));
        await _service.RecycleAsync(item.Id, bin);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(item.FullPath));
        Assert.True(File.Exists(Path.Combine(bin.Folder, item.Name)));
        Assert.Equal(ItemState.Recycled, _repository.Get(item.Id).State);
    }

    [Fact]
    public async Task Recycle_RefusesChangedArchive()
    {
        await Transfer(MakeFile());
        var item = Assert.Single(_repository.GetItems());
        File.WriteAllText(item.FullPath, "updated after archiving");
        await Assert.ThrowsAsync<IOException>(() => _service.RecycleAsync(item.Id, new FakeBin(Path.Combine(_base, "bin"))));
        Assert.True(File.Exists(item.FullPath));
        Assert.Equal(ItemState.Ready, _repository.Get(item.Id).State);
    }

    [Fact]
    public async Task RecycleFailure_DoesNotMarkFileAsRecycled()
    {
        await Transfer(MakeFile());
        var item = Assert.Single(_repository.GetItems());
        await Assert.ThrowsAsync<IOException>(() => _service.RecycleAsync(item.Id, new BrokenBin()));
        Assert.True(File.Exists(item.FullPath));
        Assert.Equal(ItemState.Ready, _repository.Get(item.Id).State);
    }

    [Fact]
    public async Task PreCancelledMove_PreservesSourceAndCreatesNoDestination()
    {
        var source = MakeFile();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transfer(source, ArchiveMode.Move, token: cancellation.Token));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(Root));
    }

    [Fact]
    public async Task CancelDuringCopy_RemovesPartialAndPreservesSource()
    {
        var source = MakeFile(content: new string('x', 4 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transfer(source, ArchiveMode.Move,
            token: cancellation.Token, progress: progress));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(Root));
        Assert.Equal(ItemState.Failed, Assert.Single(_repository.GetItems()).State);
    }

    [Fact]
    public async Task DestinationAppearingDuringCopy_IsNeverOverwritten()
    {
        var source = MakeFile(content: new string('x', 400_000));
        var target = Path.Combine(Root, Path.GetFileName(source));
        var progress = new InlineProgress(_ => { if (!File.Exists(target)) File.WriteAllText(target, "external file"); });
        var result = Assert.Single(await Transfer(source, ArchiveMode.Move, progress: progress));
        Assert.False(result.Success);
        Assert.Equal("external file", File.ReadAllText(target));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(Root, "*.partial"));
    }

    [Fact]
    public async Task SameSourceAndTarget_ReturnsFailureWithoutChanges()
    {
        var source = Path.Combine(Root, "already.txt");
        File.WriteAllText(source, "keep");
        var result = Assert.Single(await Transfer(source, ArchiveMode.Move));
        Assert.False(result.Success);
        Assert.Equal("keep", File.ReadAllText(source));
    }

    [Fact]
    public async Task MissingSource_DoesNotPreventOtherFilesInBatch()
    {
        var good = MakeFile();
        var results = await _service.TransferAsync([Path.Combine(Source, "missing"), good], Root, Root,
            ArchiveMode.Copy, ConflictPolicy.Rename, null);
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
    }

    [Fact]
    public async Task NestedFolder_IsRealAndRelativePathIsStored()
    {
        var nested = PathSafety.CreateFolder(Root, Root, "工作/项目 A");
        Assert.True(Directory.Exists(nested));
        var result = Assert.Single(await Transfer(MakeFile(), target: nested));
        Assert.True(result.Success);
        var item = Assert.Single(_repository.GetItems());
        Assert.Equal(Path.Combine("工作", "项目 A", "报告.txt"), item.RelativePath);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("foo/../../outside")]
    [InlineData("foo//bar")]
    [InlineData(".")]
    public void CreateFolder_RejectsTraversalAndInvalidNames(string relative)
    {
        Assert.Throws<IOException>(() => PathSafety.CreateFolder(Root, Root, relative));
    }

    [Fact]
    public async Task Transfer_RejectsTargetOutsideRoot()
    {
        var source = MakeFile();
        await Assert.ThrowsAsync<IOException>(() => Transfer(source, target: Source));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ReparsePointGuard_RejectsDirectoryJunction()
    {
        var link = Path.Combine(Root, "linked-folder");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/d /c mklink /J \"{link}\" \"{Source}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
        Assert.True(process.WaitForExit(5000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try { Assert.Throws<IOException>(() => PathSafety.RejectLinks(link)); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Fact]
    public void RelativePaths_PreserveUnicodeAndSpecialCharacters()
    {
        var path = Path.Combine(Root, "项目 #1", "文件 %20.txt");
        var relative = PathSafety.RelativePath(Root, path);
        Assert.Equal(Path.Combine("项目 #1", "文件 %20.txt"), relative);
        Assert.Equal(PathSafety.Normalize(path), PathSafety.Resolve(Root, relative));
        Assert.Equal(".", PathSafety.RelativePath(Root, Root));
        Assert.Equal(Path.GetPathRoot(Root), PathSafety.Normalize(Path.GetPathRoot(Root)!));
    }

    [Fact]
    public void RootBoundary_RejectsSiblingWithSamePrefix()
    {
        var sibling = Root + "-outside";
        var relative = PathSafety.RelativePath(Root, Path.Combine(sibling, "report.txt"));
        Assert.Throws<IOException>(() => PathSafety.Resolve(Root, relative));
    }

    [Fact]
    public void DirectoryListing_IncludesAllCurrentLevelFilesAndFolders()
    {
        MakeFile("old.txt");
        MakeFile("new.txt");
        MakeFile(".archivebox-notes.txt");
        MakeFile($".archivebox-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(Path.Combine(Source, "sub"));
        File.WriteAllText(Path.Combine(Source, "sub", "nested.txt"), "nested");
        var entries = PathSafety.List(Source);
        Assert.Equal(4, entries.Count);
        Assert.True(entries[0].IsDirectory);
        Assert.Contains(entries, e => e.Name == ".archivebox-notes.txt");
        Assert.DoesNotContain(entries, e => e.Name == "nested.txt");
    }

    [Fact]
    public void RecentFolders_ArePersistentUniqueAndLimited()
    {
        for (var i = 0; i < 20; i++) _repository.RememberFolder(Path.Combine(_base, $"folder-{i}"));
        _repository.RememberFolder(Source);
        _repository.RememberFolder(Source);
        var reloaded = new ArchiveRepository(Db);
        Assert.Equal(15, reloaded.GetRecentFolders().Count);
        Assert.Equal(Source, reloaded.GetRecentFolders()[0].Path);
        Assert.Equal(Source, reloaded.GetSetting("lastFolder"));
    }

    [Fact]
    public async Task Recovery_VerifiedDestinationNeverDeletesOriginal()
    {
        var source = MakeFile();
        var target = Path.Combine(Root, "recovered.txt");
        File.Copy(source, target);
        var item = NewItem(source, "recovered.txt", ItemState.Verified) with { Sha256 = await ArchiveService.HashAsync(target) };
        _repository.Save(item);
        await _service.RecoverAsync();
        Assert.Equal(ItemState.SourceRetained, _repository.Get(item.Id).State);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Recovery_UnfinishedCopyRemovesOwnedPartialAndPreservesSource()
    {
        var source = MakeFile();
        var item = NewItem(source, "incomplete.txt", ItemState.Pending);
        _repository.Save(item);
        var partial = Path.Combine(Root, $".archivebox-{item.Id}.partial");
        File.WriteAllText(partial, "partial");
        await _service.RecoverAsync();
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(partial));
        Assert.Equal(ItemState.Failed, _repository.Get(item.Id).State);
    }

    [Fact]
    public async Task Recovery_DoesNotClaimUnrelatedDestination()
    {
        var source = MakeFile();
        var item = NewItem(source, "collision.txt", ItemState.Verified) with { Sha256 = await ArchiveService.HashAsync(source) };
        _repository.Save(item);
        File.WriteAllText(item.FullPath, "someone else's file");
        await _service.RecoverAsync();
        Assert.Equal(ItemState.Failed, _repository.Get(item.Id).State);
        Assert.Equal("someone else's file", File.ReadAllText(item.FullPath));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Recovery_OfflineRootDoesNotMarkRecyclingComplete()
    {
        var source = MakeFile();
        var item = NewItem(source, "offline.txt", ItemState.Recycling) with { RootPath = Path.Combine(_base, "offline") };
        _repository.Save(item);
        await _service.RecoverAsync();
        Assert.Equal(ItemState.Recycling, _repository.Get(item.Id).State);
    }

    private ArchiveItem NewItem(string source, string relative, ItemState state) => new(
        Guid.NewGuid().ToString("N"), Root, relative, source, ArchiveMode.Move, DateTimeOffset.UtcNow,
        null, state, new FileInfo(source).Length, null, null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_base, recursive: true);
    }

    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }
    private sealed class FakeBin(string folder) : IRecycleBin
    {
        public string Folder => folder;
        public void Recycle(string path) { Directory.CreateDirectory(folder); File.Move(path, Path.Combine(folder, Path.GetFileName(path))); }
    }
    private sealed class BrokenBin : IRecycleBin
    { public void Recycle(string path) => throw new IOException("recycle failed"); }
}
