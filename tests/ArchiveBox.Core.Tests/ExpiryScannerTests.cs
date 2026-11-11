using ArchiveBox.Core;
using Xunit;

namespace ArchiveBox.Core.Tests;

public sealed class ExpiryScannerTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scanner-virtual-root");
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DirectoryEntry Entry(string relative, bool directory = false)
    { var path = Path.Combine(_root, relative); return new(path, Path.GetFileName(path), directory, directory ? 0 : 10, _now.LocalDateTime); }
    private ExpiryRule Rule(string relative, bool directory, int? days) => new(_root, relative, directory, days.HasValue ? _now.AddDays(days.Value) : null);

    [Fact]
    public void NoRules_DoesNotTouchTheFilesystem()
    {
        var scanner = new ExpiryScanner(new([]), (_, _) => throw new Exception("Unexpected directory traversal"), _ => throw new Exception("Unexpected file stat"));
        Assert.Equal(0, scanner.Scan(_root, true, _now).Files);
    }
    [Fact]
    public void FutureOrNeverRules_DoNotStartExpiredTraversal()
    {
        var scanner = new ExpiryScanner(new([Rule(".", true, 30), Rule("keep", true, null), Rule("file.txt", false, 1)]),
            (_, _) => throw new Exception("Unexpected directory traversal"), _ => throw new Exception("Unexpected file stat"));
        Assert.Equal(0, scanner.Scan(_root, true, _now).Folders);
    }
    [Fact]
    public void SoonOnly_VisitsRulesWithinTheNextSevenDays()
    {
        var visited = new List<string>();
        var scanner = new ExpiryScanner(new([Rule("old", true, -1), Rule("soon", true, 2), Rule("later", true, 20)]),
            (path, _) => { visited.Add(path); return new[] { Entry(Path.Combine("soon", "file.txt")) }; });
        Assert.Equal(1, scanner.Scan(_root, false, _now).Matches);
        Assert.Equal(Path.Combine(_root, "soon"), Assert.Single(visited));
    }
    [Fact]
    public void IneligibleBranches_ArePrunedButExplicitDescendantScopesStillWork()
    {
        var visited = new List<string>();
        var scanner = new ExpiryScanner(new([Rule(".", true, -1), Rule("keep", true, null), Rule(Path.Combine("keep", "expired"), true, -1)]),
            (path, _) =>
            {
                visited.Add(path);
                if (path == _root) return new[] { Entry("keep", true), Entry("root.txt") };
                if (path == Path.Combine(_root, "keep", "expired")) return new[] { Entry(Path.Combine("keep", "expired", "file.txt")) };
                throw new Exception("Unexpected traversal into an excluded folder");
            });
        Assert.Equal(2, scanner.Scan(_root, true, _now).Matches);
        Assert.DoesNotContain(Path.Combine(_root, "keep"), visited);
    }
    [Fact]
    public void ExpiredFileOverride_IsFoundWithoutScanningUnrelatedFolders()
    {
        var path = Path.Combine("keep", "file.txt");
        var scanner = new ExpiryScanner(new([Rule("keep", true, null), Rule(path, false, -1)]),
            (_, _) => throw new Exception("A file-only rule must not traverse directories"), _ => Entry(path));
        Assert.Equal(1, scanner.Scan(_root, true, _now).Matches);
    }
    [Fact]
    public void FileOverrides_AreRespectedAndNotEmittedTwice()
    {
        var matches = new List<ExpiryMatch>();
        var scanner = new ExpiryScanner(new([Rule(".", true, -1), Rule("keep.txt", false, null), Rule("later.txt", false, 30), Rule("due.txt", false, -2)]),
            (_, _) => new[] { Entry("keep.txt"), Entry("later.txt"), Entry("due.txt"), Entry("inherited.txt") }, p => Entry(Path.GetFileName(p)));
        Assert.Equal(2, scanner.Scan(_root, true, _now, batch => matches.AddRange(batch)).Matches);
        Assert.Equal(new[] { "due.txt", "inherited.txt" }, matches.Select(m => m.Entry.Name).OrderBy(n => n).ToArray());
    }
    [Fact]
    public void Cancellation_IsCheckedWithinLargeDirectoryEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var scanned = 0;
        IEnumerable<DirectoryEntry> Entries(string path, CancellationToken token)
        { for (var i = 0; i < 10000; i++) { scanned++; yield return Entry($"file-{i}.txt"); } }
        var scanner = new ExpiryScanner(new([Rule(".", true, -1)]), Entries);
        Assert.Throws<OperationCanceledException>(() => scanner.Scan(_root, true, _now, _ => cancellation.Cancel(), token: cancellation.Token));
        Assert.InRange(scanned, 1, 128);
    }
    [Fact]
    public void LargeRuleSet_IsIndexedAndResultsAreBatched()
    {
        var rules = new List<ExpiryRule> { Rule(".", true, -1) };
        for (var i = 0; i < 6000; i++) rules.Add(Rule($"file-{i}.txt", false, null));
        var batches = 0; var count = 0;
        IEnumerable<DirectoryEntry> Entries(string path, CancellationToken token)
        { for (var i = 0; i < 10000; i++) yield return Entry($"file-{i}.txt"); }
        var scanner = new ExpiryScanner(new(rules), Entries);
        var result = scanner.Scan(_root, true, _now, batch => { Assert.InRange(batch.Count, 1, 128); batches++; count += batch.Count; });
        Assert.Equal(4000, result.Matches); Assert.Equal(4000, count); Assert.True(batches > 1);
    }
    [Fact]
    public void InaccessibleScope_DoesNotPreventOtherScopesFromLoading()
    {
        var scanner = new ExpiryScanner(new([Rule("denied", true, -1), Rule("ok", true, -1)]),
            (path, _) => path.EndsWith("denied") ? throw new UnauthorizedAccessException() : new[] { Entry(Path.Combine("ok", "file.txt")) });
        var result = scanner.Scan(_root, true, _now);
        Assert.Equal(1, result.Skipped); Assert.Equal(1, result.Matches);
    }
}
