using System.Diagnostics;

namespace ArchiveBox.Core;

public sealed record ExpiryMatch(DirectoryEntry Entry, ExpiryDecision Expiry);
public sealed record ExpiryScanProgress(int Folders, int Files, int Matches, int Skipped);
public sealed record ExpiryScanResult(int Folders, int Files, int Matches, int Skipped);

/// <summary>Visits only explicitly relevant rule scopes; contains no UI or write operations.</summary>
public sealed class ExpiryScanner
{
    private readonly ExpiryRules _rules;
    private readonly Func<string, CancellationToken, IEnumerable<DirectoryEntry>> _enumerate;
    private readonly Func<string, DirectoryEntry?> _file;
    public ExpiryScanner(ExpiryRules rules,
        Func<string, CancellationToken, IEnumerable<DirectoryEntry>>? enumerate = null,
        Func<string, DirectoryEntry?>? file = null)
    { _rules = rules; _enumerate = enumerate ?? Enumerate; _file = file ?? ReadFile; }

    public ExpiryScanResult Scan(string root, bool due, DateTimeOffset now,
        Action<IReadOnlyList<ExpiryMatch>>? batch = null, Action<ExpiryScanProgress>? progress = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        root = PathSafety.Normalize(root);
        var rules = _rules.ForRoot(root);
        bool Eligible(DateTimeOffset? date) => date.HasValue && (due ? date.Value <= now : date.Value > now && date.Value <= now.AddDays(7));
        var scopes = rules.Where(r => Eligible(r.ExpiresAt)).ToArray();
        if (scopes.Length == 0) { progress?.Invoke(new(0, 0, 0, 0)); return new(0, 0, 0, 0); }
        var folders = new Queue<string>(scopes.Where(r => r.IsDirectory).Select(r => r.RelativePath));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<ExpiryMatch>(128);
        var clock = Stopwatch.StartNew(); long lastReport = -100;
        int checkedFolders = 0, checkedFiles = 0, found = 0, skipped = 0;
        void Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastReport < 100 && pending.Count < 128) return;
            token.ThrowIfCancellationRequested();
            if (pending.Count > 0) { batch?.Invoke(pending.ToArray()); pending.Clear(); }
            progress?.Invoke(new(checkedFolders, checkedFiles, found, skipped)); lastReport = clock.ElapsedMilliseconds;
        }
        void Consider(DirectoryEntry entry, ExpiryDecision decision)
        {
            checkedFiles++;
            if (Eligible(decision.ExpiresAt) && emitted.Add(entry.Path)) { pending.Add(new(entry, decision)); found++; }
            Report();
        }
        // Eligible file overrides can live below an otherwise excluded folder.
        foreach (var rule in scopes.Where(r => !r.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entry = _file(PathSafety.Resolve(root, rule.RelativePath));
                if (entry is not null) Consider(entry, _rules.Resolve(root, rule.RelativePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var relative = folders.Dequeue();
            if (!visited.Add(relative)) continue;
            var policy = _rules.Resolve(root, relative, true);
            if (!Eligible(policy.ExpiresAt)) continue;
            var path = PathSafety.Resolve(root, relative);
            checkedFolders++;
            Report();
            try
            {
                foreach (var entry in _enumerate(path, token))
                {
                    token.ThrowIfCancellationRequested();
                    var child = PathSafety.RelativePath(root, entry.Path);
                    if (entry.IsDirectory)
                    {
                        if (Eligible(_rules.Resolve(root, child, true).ExpiresAt)) folders.Enqueue(child);
                    }
                    else Consider(entry, _rules.Resolve(root, child));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            Report();
        }
        Report(true);
        return new(checkedFolders, checkedFiles, found, skipped);
    }

    private static IEnumerable<DirectoryEntry> Enumerate(string folder, CancellationToken token)
    {
        PathSafety.RejectLinks(folder);
        // Stream cached FindFirst/FindNext metadata; no per-directory materialization or sorting.
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (PathSafety.IsManagedTemporaryName(info.Name)) continue;
            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            yield return new(info.FullName, info.Name, directory, directory ? 0 : ((FileInfo)info).Length, info.LastWriteTime);
        }
    }
    private static DirectoryEntry? ReadFile(string path)
    {
        PathSafety.RejectLinks(path);
        var info = new FileInfo(path);
        return info.Exists ? new(info.FullName, info.Name, false, info.Length, info.LastWriteTime) : null;
    }
}
