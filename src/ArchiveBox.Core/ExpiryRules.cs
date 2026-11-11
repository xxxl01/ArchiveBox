namespace ArchiveBox.Core;

public sealed record FavoriteFolder(string Path, string Name);
public sealed record ExpiryRule(string Root, string RelativePath, bool IsDirectory, DateTimeOffset? ExpiresAt);
public sealed record ExpiryDecision(DateTimeOffset? ExpiresAt, bool IsExplicit, string? InheritedFrom)
{
    public string DisplayDate => ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "不设到期";
    public string SourceText => IsExplicit ? "单独设置" : InheritedFrom is null ? "未设置" :
        "继承：" + (InheritedFrom == "." ? "归档根目录" : System.IO.Path.GetFileName(InheritedFrom));
}

public sealed class ExpiryRules
{
    private readonly IReadOnlyList<ExpiryRule> _rules;
    private readonly Dictionary<string, ExpiryRule> _index = new(StringComparer.OrdinalIgnoreCase);
    public ExpiryRules(IReadOnlyList<ExpiryRule> rules)
    {
        _rules = rules;
        foreach (var rule in rules) _index[Key(PathSafety.Normalize(rule.Root), rule.RelativePath, rule.IsDirectory)] = rule;
    }
    private static string Key(string root, string relative, bool directory) => root + "\0" + relative + (directory ? "\0D" : "\0F");
    public IReadOnlyList<ExpiryRule> ForRoot(string root) => _rules.Where(r =>
        string.Equals(r.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray();
    public ExpiryDecision Resolve(string root, string relative, bool directory = false)
    {
        root = PathSafety.Normalize(root);
        relative = PathSafety.RelativePath(root, PathSafety.Resolve(root, relative));
        var own = Find(root, relative, directory);
        if (own is not null) return new(own.ExpiresAt, true, null);
        var parent = relative == "." ? null : System.IO.Path.GetDirectoryName(relative);
        while (parent is not null)
        {
            if (parent.Length == 0) parent = ".";
            var rule = Find(root, parent, true);
            if (rule is not null) return new(rule.ExpiresAt, false, parent);
            parent = parent == "." ? null : System.IO.Path.GetDirectoryName(parent);
        }
        return new(null, false, null);
    }
    private ExpiryRule? Find(string root, string relative, bool directory)
        => _index.TryGetValue(Key(root, relative, directory), out var rule) ? rule : null;
}
