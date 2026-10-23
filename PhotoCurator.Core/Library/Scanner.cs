using System.Security.Cryptography;
using System.Text;

namespace PhotoCurator.Core.Library;

/// <summary>A photo found under the open folder.</summary>
/// <param name="Id">Root-relative path with forward slashes; also the photo's id in the API.</param>
/// <param name="Folder">Root-relative folder ("" is the open folder itself).</param>
/// <param name="Modified">Last-write time in UTC ticks; part of thumbnail URLs so changed files get new thumbnails.</param>
/// <param name="SidecarError">Set when the photo's .md exists but can't be read; the app then refuses to overwrite it.</param>
public sealed record PhotoEntry(string Id, string Folder, string Name, long Size, long Modified, SidecarData? Sidecar, string? SidecarError)
{
    public bool HasSidecar => Sidecar is not null || SidecarError is not null;
}

/// <param name="Count">Photos directly in this folder.</param>
/// <param name="Total">Photos in this folder and all folders below it.</param>
public sealed record FolderEntry(string Path, string Name, int Count, int Total);

/// <summary>Everything found in one scan. Immutable; a new one replaces it after each change.</summary>
public sealed class LibrarySnapshot
{
    public required IReadOnlyList<PhotoEntry> Photos { get; init; }
    public required IReadOnlyList<FolderEntry> Folders { get; init; }
    /// <summary>Tag → number of photos, most used first.</summary>
    public required IReadOnlyList<KeyValuePair<string, int>> Tags { get; init; }
    public required string Signature { get; init; }

    Dictionary<string, PhotoEntry>? _byId;

    public PhotoEntry? Find(string id)
    {
        _byId ??= Photos.ToDictionary(p => p.Id, PathGuard.Comparer);
        return _byId.GetValueOrDefault(id);
    }

    public bool HasFolder(string path) => Folders.Any(f => f.Path.Equals(path, PathGuard.Comparison));
}

/// <summary>Remembers parsed sidecars by size and modified time, so rescans only re-read files that changed.</summary>
public sealed class SidecarCache
{
    sealed record Item(long Size, long Modified, SidecarData? Data, string? Error);

    readonly Dictionary<string, Item> _items = new(PathGuard.Comparer);
    readonly object _sync = new();

    public (SidecarData? Data, string? Error) Get(FileInfo file)
    {
        var size = file.Length;
        var modified = file.LastWriteTimeUtc.Ticks;
        lock (_sync)
        {
            if (_items.TryGetValue(file.FullName, out var hit) && hit.Size == size && hit.Modified == modified) return (hit.Data, hit.Error);
        }

        Item item;
        try
        {
            item = new Item(size, modified, Sidecar.Read(file.FullName), null);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            item = new Item(size, modified, null, e.Message);
        }
        lock (_sync) _items[file.FullName] = item;
        return (item.Data, item.Error);
    }

    public void Retain(IReadOnlySet<string> paths)
    {
        lock (_sync)
        {
            foreach (var key in _items.Keys.Where(k => !paths.Contains(k)).ToList()) _items.Remove(key);
        }
    }
}

/// <summary>Walks the open folder at any depth and lists photos, folders and tags.</summary>
public static class Scanner
{
    public static LibrarySnapshot Scan(string root, SidecarCache cache)
    {
        var photos = new List<PhotoEntry>();
        var folders = new List<(string Path, string Name, int Count)>();
        var sidecarPaths = new HashSet<string>(PathGuard.Comparer);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };

        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            var rel = PathGuard.ToRelative(root, dir.FullName);
            var files = entries.OfType<FileInfo>().ToList();
            // Folders that Export for web wrote are output, not photos to sort.
            if (rel.Length > 0 && files.Any(f => f.Name == PhotoFiles.ExportMarker)) continue;

            var byName = files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in files)
            {
                if (!PhotoFiles.IsPhoto(file.Name) || (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                SidecarData? sidecar = null;
                string? error = null;
                long sidecarStamp = 0;
                if (byName.TryGetValue(PhotoFiles.SidecarName(file.Name), out var sidecarFile))
                {
                    (sidecar, error) = cache.Get(sidecarFile);
                    sidecarPaths.Add(sidecarFile.FullName);
                    sidecarStamp = sidecarFile.LastWriteTimeUtc.Ticks ^ sidecarFile.Length;
                }

                var id = PathGuard.Join(rel, file.Name);
                var modified = file.LastWriteTimeUtc.Ticks;
                photos.Add(new PhotoEntry(id, rel, file.Name, file.Length, modified, sidecar, error));
                hash.AppendData(Encoding.UTF8.GetBytes($"P|{id}|{file.Length}|{modified}|{sidecarStamp}\n"));
                count++;
            }

            folders.Add((rel, dir.Name, count));
            hash.AppendData(Encoding.UTF8.GetBytes($"D|{rel}\n"));

            foreach (var sub in entries.OfType<DirectoryInfo>())
            {
                if (sub.Name.StartsWith('.')) continue;
                if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                pending.Push(sub);
            }
        }

        cache.Retain(sidecarPaths);

        var totals = folders.ToDictionary(f => f.Path, _ => 0, PathGuard.Comparer);
        foreach (var photo in photos)
        {
            var folder = photo.Folder;
            while (true)
            {
                if (totals.ContainsKey(folder)) totals[folder]++;
                if (folder.Length == 0) break;
                folder = PathGuard.ParentOf(folder);
            }
        }

        var segmentOrder = Comparer<string>.Create(CompareFolderPaths);
        var folderList = folders
            .OrderBy(f => f.Path, segmentOrder)
            .Select(f => new FolderEntry(f.Path, f.Name, f.Count, totals[f.Path]))
            .ToList();
        photos.Sort((a, b) =>
        {
            var byFolder = CompareFolderPaths(a.Folder, b.Folder);
            return byFolder != 0 ? byFolder : Names.Natural.Compare(a.Name, b.Name);
        });

        var tags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            if (photo.Sidecar is null) continue;
            foreach (var tag in photo.Sidecar.Tags) tags[tag] = tags.GetValueOrDefault(tag) + 1;
        }

        return new LibrarySnapshot
        {
            Photos = photos,
            Folders = folderList,
            Tags = tags.OrderByDescending(t => t.Value).ThenBy(t => t.Key, Names.Natural).ToList(),
            Signature = Convert.ToHexString(hash.GetHashAndReset())
        };
    }

    /// <summary>Folder paths compared segment by segment, naturally: "A/B" before "A B", "Trip 2" before "Trip 10".</summary>
    public static int CompareFolderPaths(string a, string b)
    {
        if (a.Length == 0) return b.Length == 0 ? 0 : -1;
        if (b.Length == 0) return 1;
        var left = a.Split('/');
        var right = b.Split('/');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var c = Names.Natural.Compare(left[i], right[i]);
            if (c != 0) return c;
        }
        return left.Length.CompareTo(right.Length);
    }
}
