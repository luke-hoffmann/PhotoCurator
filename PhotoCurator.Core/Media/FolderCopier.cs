namespace PhotoCurator.Core.Media;

/// <summary>"Make a copy first": copies the whole folder (every file, not just photos) next to a chosen place.</summary>
public static class FolderCopier
{
    /// <summary>
    /// Copies <paramref name="source"/> to "&lt;destinationParent&gt;/&lt;name&gt; (copy)" (or "(copy 2)"…), keeping
    /// modified times. Returns the new folder. A failed or cancelled copy removes the partial copy it made.
    /// </summary>
    public static string Copy(string source, string destinationParent, IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken cancel = default)
    {
        source = PathGuard.Normalize(source);
        if (!Directory.Exists(source)) throw new CuratorException("That folder doesn't exist.", 404);
        if (string.IsNullOrWhiteSpace(destinationParent)) throw new CuratorException("Choose where to put the copy.");
        var parent = PathGuard.Normalize(destinationParent);
        if (!Directory.Exists(parent)) throw new CuratorException("The place for the copy doesn't exist.", 404);
        if (PathGuard.IsInside(source, parent)) throw new CuratorException("Put the copy somewhere outside the folder being copied.");

        var name = Path.GetFileName(source) is { Length: > 0 } n ? n : "Photos";
        var target = Path.Combine(parent, $"{name} (copy)");
        for (var i = 2; Directory.Exists(target) || File.Exists(target); i++) target = Path.Combine(parent, $"{name} (copy {i})");

        var files = new List<FileInfo>();
        var dirs = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(source));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            dirs.Add(dir.FullName);
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (entry is FileInfo file) files.Add(file);
                else if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
            }
        }

        var total = files.Sum(f => f.Length);
        long done = 0;
        try
        {
            foreach (var dir in dirs) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();
                var to = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
                File.Copy(file.FullName, to, overwrite: false);
                File.SetLastWriteTimeUtc(to, file.LastWriteTimeUtc);
                done += file.Length;
                progress?.Report((done, total, file.Name));
            }
        }
        catch
        {
            try { Directory.Delete(target, recursive: true); } catch { /* leave what's there */ }
            throw;
        }
        return target;
    }
}
