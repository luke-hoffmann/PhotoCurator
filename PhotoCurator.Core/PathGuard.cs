namespace PhotoCurator.Core;

/// <summary>
/// Converts between full paths and the root-relative ids the UI uses ("Field Work/IMG_1.HEIC", "" for the root),
/// and makes sure nothing ever resolves outside the open folder.
/// </summary>
public static class PathGuard
{
    /// <summary>Windows and macOS file systems are case-insensitive by default.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Full path without a trailing separator (except for a drive or file-system root).</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Resolves a root-relative id to a full path inside <paramref name="root"/>, or throws.</summary>
    public static string Resolve(string root, string? relative)
    {
        if (relative is null) throw new CuratorException("Missing path.");
        if (relative.Contains('\0')) throw new CuratorException("Invalid path.");

        var rel = relative.Replace('\\', '/').TrimEnd('/');
        if (rel.Length == 0) return root;
        if (rel.StartsWith('/') || Path.IsPathRooted(rel) || (OperatingSystem.IsWindows() && rel.Contains(':')))
        {
            throw new CuratorException("Paths must be inside the open folder.");
        }
        foreach (var segment in rel.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") throw new CuratorException("Invalid path.");
        }

        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, full)) throw new CuratorException("Paths must be inside the open folder.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or anything below it.</summary>
    public static bool IsInside(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Equals(root, Comparison)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    /// <summary>True when one path contains the other (or they are the same).</summary>
    public static bool Overlaps(string a, string b) => IsInside(a, b) || IsInside(b, a);

    public static string ToRelative(string root, string full)
    {
        var rel = Path.GetRelativePath(root, full);
        return rel == "." ? "" : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>The folder part of a relative id: "a/b/c.jpg" → "a/b", "c.jpg" → "".</summary>
    public static string ParentOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }

    public static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;
}
