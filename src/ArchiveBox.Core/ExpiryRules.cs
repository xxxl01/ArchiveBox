namespace ArchiveBox.Core;

public sealed record FavoriteFolder(string Path, string Name);
public sealed record ExpiryRule(string Root, string RelativePath, bool IsDirectory, DateTimeOffset? ExpiresAt);
public sealed record ExpiryDecision(DateTimeOffset? ExpiresAt, bool IsExplicit, string? InheritedFrom)
{
    public string DisplayDate => ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "不设到期";
    public string SourceText => IsExplicit ? "单独设置" : InheritedFrom is null ? "未设置" :
        "继承：" + (InheritedFrom == "." ? "归档根目录" : System.IO.Path.GetFileName(InheritedFrom));
}

public sealed class ExpiryRules(IReadOnlyList<ExpiryRule> rules)
{
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

    private ExpiryRule? Find(string root, string relative, bool directory) => rules.FirstOrDefault(r =>
        r.IsDirectory == directory && string.Equals(r.Root, root, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
}
