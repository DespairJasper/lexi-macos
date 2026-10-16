namespace Lexi;

public static class StoragePathIdentity
{
    public static string Resolve(string path) => Resolve(path, 0);

    // Resolve existing ancestors before creating anything, including links whose final child does not yet exist.
    private static string Resolve(string path, int depth = 0)
    {
        if (depth > 40) throw new IOException("档案路径链接过多；原数据已保留。");
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var resolved = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(resolved, part);
            // Query existence using the volume's own rules. A nonexistent differently-cased name
            // on a case-sensitive volume remains a distinct new name.
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                var names = Directory.EnumerateFileSystemEntries(resolved).Select(Path.GetFileName).ToArray();
                var actual = names.FirstOrDefault(name => string.Equals(name, part, StringComparison.Ordinal))
                    ?? names.SingleOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase));
                if (actual is null) throw new IOException("档案路径实际名称无法确认；原数据已保留。");
                candidate = Path.Combine(resolved, actual);
            }
            resolved = candidate;
            FileSystemInfo? existing = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : File.Exists(resolved) ? new FileInfo(resolved) : null;
            if (existing?.LinkTarget is not null) resolved = Resolve(existing.ResolveLinkTarget(true)?.FullName ?? throw new IOException("档案路径无法确认；原数据已保留。"), depth + 1);
        }
        return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar);
    }

}
