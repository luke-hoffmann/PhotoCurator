using System.IO.Compression;
using System.Text.RegularExpressions;
using PhotoCurator.Core.Library;

namespace PhotoCurator.Core.Media;

/// <summary>One photo found in a download, and what the check decided about it.</summary>
public sealed class ImportPhoto
{
    public required int Source { get; init; }
    /// <summary>Path inside the zip or folder, with forward slashes.</summary>
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    /// <summary>"new", "edited" (same moment as a photo you have), "existing" (same file is already in the folder) or "repeat" (twice in this download).</summary>
    public string Kind { get; set; } = "new";
    /// <summary>For "existing" and "edited": the matching photo's id in the folder. For "repeat": the first copy's path.</summary>
    public string? Match { get; set; }
    internal string? Sha256 { get; set; }
    internal Moment? Moment { get; set; }
}

/// <summary>The result of checking a download against the open folder. Running it copies the "new" and "edited" photos.</summary>
public sealed class ImportPlan
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    /// <summary>Where the photos go, relative to the open folder: "Inbox/&lt;album&gt;".</summary>
    public required string Destination { get; init; }
    public required IReadOnlyList<ImportPhoto> Photos { get; init; }
    public int Count(string kind) => Photos.Count(p => p.Kind == kind);
    public IEnumerable<ImportPhoto> ToCopy => Photos.Where(p => p.Kind is "new" or "edited");
}

public sealed record ImportResult(int Imported, string Folder, IReadOnlyList<string> Problems);

/// <summary>
/// Brings photos in from a download (Google Photos album zips, or any folder) without duplicates: a photo already in
/// the open folder (by content, wherever it was moved and whatever it's called now) is skipped; a different file of
/// the same moment (an edit) is copied but shows up under Duplicates. New photos land in Inbox/&lt;album&gt;, with their
/// Takeout .json and Live Photo clips. The download is only ever read.
/// </summary>
public static partial class Importer
{
    [GeneratedRegex(@"(-\d{3}| ?\(\d+\))$")]
    private static partial Regex DownloadSuffix();

    /// <summary>Google's duplicate counter: "IMG_2(1).jpg".</summary>
    [GeneratedRegex(@"^(?<stem>.*?) ?\((?<n>\d+)\)(?<ext>\.[^.]*)?$")]
    private static partial Regex CounterSuffix();

    public static ImportPlan Check(PhotoLibrary library, Fingerprints prints, IReadOnlyList<string> sources,
        Action<long, long, string>? report = null, CancellationToken cancel = default)
    {
        if (sources.Count == 0) throw new CuratorException("Choose a zip file or folder to import.");
        var normalized = sources.Select(s => PathGuard.Normalize(s.Trim().Trim('"'))).Distinct(PathGuard.Comparer).ToList();
        foreach (var source in normalized)
        {
            if (Directory.Exists(source))
            {
                if (PathGuard.Overlaps(source, library.Root)) throw new CuratorException("That folder is part of the open folder already. Import from your download folder or zip instead.");
            }
            else if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new CuratorException($"\"{Path.GetFileName(source)}\" isn't a folder or a .zip file.", 404);
            }
        }

        // 1. Everything in the open folder: its moments (cached after the first time) and sizes.
        var snapshot = library.Refresh();
        var bySize = snapshot.Photos.GroupBy(p => p.Size).ToDictionary(g => g.Key, g => g.ToList());
        var byTime = new Dictionary<string, List<(PhotoEntry Photo, Moment Moment)>>(StringComparer.Ordinal);
        var done = 0;
        foreach (var photo in snapshot.Photos)
        {
            cancel.ThrowIfCancellationRequested();
            var moment = prints.MomentOf(library.FullPath(photo.Id), Fingerprints.Key(photo.Name, photo.Size, photo.Modified));
            if (moment is not null)
            {
                if (!byTime.TryGetValue(moment.Taken, out var list)) byTime[moment.Taken] = list = new();
                list.Add((photo, moment));
            }
            if (++done % 50 == 0) report?.Invoke(done, snapshot.Photos.Count, "Reading your folder");
        }

        // 2. The download.
        using var opened = new OpenedSources(normalized);
        var incoming = opened.Photos().ToList();
        var incomingSizes = incoming.GroupBy(p => p.Size).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        done = 0;
        foreach (var photo in incoming)
        {
            cancel.ThrowIfCancellationRequested();
            report?.Invoke(done++, incoming.Count, photo.Name);

            var sameSize = bySize.GetValueOrDefault(photo.Size);
            if (sameSize is not null || incomingSizes[photo.Size] > 1)
            {
                using (var stream = opened.Open(photo)) photo.Sha256 = Fingerprints.Hash(stream);
                var existing = sameSize?.FirstOrDefault(p => SafeSha(prints, library, p) == photo.Sha256);
                if (existing is not null)
                {
                    photo.Kind = "existing";
                    photo.Match = existing.Id;
                    continue;
                }
                if (seen.TryGetValue(photo.Sha256, out var first))
                {
                    photo.Kind = "repeat";
                    photo.Match = first;
                    continue;
                }
                seen[photo.Sha256] = photo.Path;
            }

            using (var stream = opened.Open(photo)) photo.Moment = Moment.Read(stream);
            if (photo.Moment is not null && byTime.TryGetValue(photo.Moment.Taken, out var candidates) &&
                candidates.FirstOrDefault(c => c.Moment.Matches(photo.Moment)) is { Photo: { } edited })
            {
                photo.Kind = "edited";
                photo.Match = edited.Id;
            }
        }
        report?.Invoke(incoming.Count, incoming.Count, "");

        return new ImportPlan
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Sources = normalized,
            Destination = DuplicateFinder.InboxFolder + "/" + AlbumName(normalized[0]),
            Photos = incoming
        };
    }

    public static ImportResult Run(PhotoLibrary library, Fingerprints prints, ImportPlan plan,
        Action<long, long, string>? report = null, CancellationToken cancel = default)
    {
        var undo = new ImportUndo { Description = "" };
        var problems = new List<string>();
        var imported = 0;
        var destination = PathGuard.Resolve(library.Root, plan.Destination);
        try
        {
            for (var dir = destination; !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!) undo.CreatedFolders.Insert(0, dir);
            Directory.CreateDirectory(destination);

            using var opened = new OpenedSources(plan.Sources);
            var toCopy = plan.ToCopy.ToList();
            foreach (var photo in toCopy)
            {
                cancel.ThrowIfCancellationRequested();
                report?.Invoke(imported, toCopy.Count, photo.Name);
                try
                {
                    var siblings = opened.Siblings(photo);
                    var companions = FileMover.Companions(photo.Name, siblings);
                    var target = FileMover.FreeName(destination, photo.Name, companions);
                    var copied = Copy(opened, photo.Source, photo.Path, Path.Combine(destination, target), photo.ModifiedUtc, undo);
                    foreach (var companion in companions)
                    {
                        var rel = PathGuard.Join(PathGuard.ParentOf(photo.Path), companion);
                        if (opened.Find(photo.Source, rel) is { } file)
                        {
                            Copy(opened, photo.Source, rel, Path.Combine(destination, FileMover.RenameCompanion(companion, photo.Name, target)), file.ModifiedUtc, undo);
                        }
                    }
                    prints.Seed(Fingerprints.Key(target, copied.Size, copied.Modified), photo.Sha256, photo.Moment);
                    imported++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    problems.Add($"{photo.Path}: {e.Message}");
                }
            }
            report?.Invoke(imported, toCopy.Count, "");
        }
        finally
        {
            undo.Description = $"Import {imported} photo{(imported == 1 ? "" : "s")}";
            library.RecordImport(undo);
            prints.Save();
        }
        return new ImportResult(imported, plan.Destination, problems);
    }

    /// <summary>"Field Work-002.zip" → "Field Work"; a folder keeps its name.</summary>
    public static string AlbumName(string source)
    {
        var name = Directory.Exists(source) ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        name = DownloadSuffix().Replace(name ?? "", "").Trim();
        try { return Names.ValidateFolderName(name); }
        catch (CuratorException) { return "Import"; }
    }

    static string? SafeSha(Fingerprints prints, PhotoLibrary library, PhotoEntry photo)
    {
        try { return prints.Sha256(library.FullPath(photo.Id), Fingerprints.Key(photo.Name, photo.Size, photo.Modified)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static ImportUndo.CopiedFile Copy(OpenedSources opened, int source, string rel, string target, DateTime modifiedUtc, ImportUndo undo)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, "." + System.IO.Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var input = opened.Open(source, rel))
            using (var output = File.Create(temp))
            {
                input.CopyTo(output);
            }
            File.SetLastWriteTimeUtc(temp, modifiedUtc);
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
        var info = new FileInfo(target);
        var copied = new ImportUndo.CopiedFile(target, info.Length, info.LastWriteTimeUtc.Ticks);
        undo.Files.Add(copied);
        return copied;
    }

    /// <summary>The download's zips and folders, open for reading. Lists every file, opens any of them by path.</summary>
    sealed class OpenedSources : IDisposable
    {
        sealed record Item(string Path, string Name, long Size, DateTime ModifiedUtc, ZipArchiveEntry? Entry, string? FullPath);

        readonly List<ZipArchive?> _zips = new();
        readonly List<Dictionary<string, Item>> _files = new();

        public OpenedSources(IReadOnlyList<string> sources)
        {
            foreach (var source in sources)
            {
                var files = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(source))
                {
                    _zips.Add(null);
                    foreach (var file in Walk(new DirectoryInfo(source)))
                    {
                        var rel = PathGuard.ToRelative(source, file.FullName);
                        files[rel] = new Item(rel, file.Name, file.Length, file.LastWriteTimeUtc, null, file.FullName);
                    }
                }
                else
                {
                    ZipArchive zip;
                    try { zip = ZipFile.OpenRead(source); }
                    catch (InvalidDataException) { throw new CuratorException($"\"{System.IO.Path.GetFileName(source)}\" isn't a readable zip file."); }
                    _zips.Add(zip);
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
                        var rel = entry.FullName.Replace('\\', '/');
                        files[rel] = new Item(rel, entry.Name, entry.Length, entry.LastWriteTime.UtcDateTime, entry, null);
                    }
                }
                _files.Add(files);
            }
        }

        public IEnumerable<ImportPhoto> Photos()
        {
            for (var source = 0; source < _files.Count; source++)
            {
                foreach (var item in _files[source].Values.Where(i => PhotoFiles.IsPhoto(i.Name)).OrderBy(i => i.Path, Comparer<string>.Create(ComparePaths)))
                {
                    yield return new ImportPhoto { Source = source, Path = item.Path, Name = item.Name, Size = item.Size, ModifiedUtc = item.ModifiedUtc };
                }
            }
        }

        public Stream Open(ImportPhoto photo) => Open(photo.Source, photo.Path);

        public Stream Open(int source, string rel)
        {
            var item = _files[source][rel];
            return item.Entry?.Open() ?? File.OpenRead(item.FullPath!);
        }

        public (string Path, DateTime ModifiedUtc)? Find(int source, string rel) =>
            _files[source].TryGetValue(rel, out var item) ? (item.Path, item.ModifiedUtc) : null;

        /// <summary>Names of the files next to a photo (for its companions).</summary>
        public List<string> Siblings(ImportPhoto photo)
        {
            var dir = PathGuard.ParentOf(photo.Path);
            return _files[photo.Source].Values.Where(i => PathGuard.ParentOf(i.Path).Equals(dir, StringComparison.OrdinalIgnoreCase)).Select(i => i.Name).ToList();
        }

        static IEnumerable<FileInfo> Walk(DirectoryInfo root)
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (entry.Name.StartsWith('.')) continue;
                    if (entry is FileInfo file && (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0) yield return file;
                    else if (entry is DirectoryInfo sub && (sub.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0) pending.Push(sub);
                }
            }
        }

        /// <summary>Natural order, except that "IMG_2(1).jpg" comes right after "IMG_2.jpg", so the plain name counts as the original.</summary>
        static int ComparePaths(string a, string b)
        {
            var byFolder = Scanner.CompareFolderPaths(PathGuard.ParentOf(a), PathGuard.ParentOf(b));
            if (byFolder != 0) return byFolder;
            var (nameA, counterA) = SplitCounter(System.IO.Path.GetFileName(a));
            var (nameB, counterB) = SplitCounter(System.IO.Path.GetFileName(b));
            var byName = Names.Natural.Compare(nameA, nameB);
            return byName != 0 ? byName : counterA.CompareTo(counterB);
        }

        static (string Name, int Counter) SplitCounter(string name)
        {
            var match = CounterSuffix().Match(name);
            return match.Success && int.TryParse(match.Groups["n"].Value, out var n)
                ? (match.Groups["stem"].Value + match.Groups["ext"].Value, n)
                : (name, 0);
        }

        public void Dispose()
        {
            foreach (var zip in _zips) zip?.Dispose();
        }
    }
}
