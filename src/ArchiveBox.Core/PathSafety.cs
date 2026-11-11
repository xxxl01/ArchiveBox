namespace ArchiveBox.Core;

public static class PathSafety
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string Resolve(string root, string relative)
    {
        root = Normalize(root);
        if (Path.IsPathRooted(relative)) throw new IOException("归档路径必须是相对路径。");
        var full = Normalize(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, Comparison) && !full.StartsWith(prefix, Comparison))
            throw new IOException("目标路径超出了归档根目录。");
        return full;
    }

    public static void RejectLinks(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        // Check every existing component, including a file at the leaf.
        var leaf = Path.GetFullPath(path);
        if ((File.Exists(leaf) || Directory.Exists(leaf)) &&
            (File.GetAttributes(leaf) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("暂不支持符号链接或目录联接，请选择实际目录或文件。");
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("路径包含符号链接或目录联接，请选择实际目录。");
            current = current.Parent;
        }
    }

    public static string CreateFolder(string root, string parent, string relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || Path.IsPathRooted(relativeName))
            throw new IOException("请输入相对文件夹名称，例如 工作/项目 A。");
        var parts = relativeName.Replace('\\', '/').Split('/');
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
                           p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new IOException("文件夹名称无效，不能包含空层级、上级路径或非法字符。");
        var target = Resolve(root, Path.GetRelativePath(root, Path.Combine(parent, Path.Combine(parts))));
        RejectLinks(target);
        Directory.CreateDirectory(target);
        return target;
    }

    public static IReadOnlyList<DirectoryEntry> List(string folder)
    {
        RejectLinks(folder);
        return new DirectoryInfo(folder).EnumerateFileSystemInfos()
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !IsManagedTemporaryName(f.Name))
            .Select(f => new DirectoryEntry(f.FullName, f.Name, f is DirectoryInfo,
                f is FileInfo file ? file.Length : 0, f.LastWriteTime))
            .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsManagedTemporaryName(string name)
    {
        const string prefix = ".archivebox-";
        const string suffix = ".partial";
        return name.Length == prefix.Length + 32 + suffix.Length &&
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
    }
}
