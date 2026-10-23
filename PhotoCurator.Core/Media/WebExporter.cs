using System.Text;
using System.Text.Json;
using ImageMagick;
using PhotoCurator.Core.Library;

namespace PhotoCurator.Core.Media;

public sealed record ExportResult(string Destination, int Photos, int Written, int Unchanged, int Removed, IReadOnlyList<string> Problems);

/// <summary>
/// Export for web: writes a folder's photos as web-ready copies for a static site (Astro content collections).
/// Each photo becomes "&lt;slug&gt;.jpg" (≤2400px, upright, sRGB, no metadata, so no GPS) plus "&lt;slug&gt;.md" with
/// its caption/date/tags and "image: ./&lt;slug&gt;.jpg"; each folder gets "folder.md" with its real name as the title.
/// Re-running only rewrites what changed, and only ever deletes files a previous export wrote (listed in the
/// ".photocurator-export" manifest at the destination).
/// </summary>
public static class WebExporter
{
    public const int MaxEdge = 2400;
    public const int Quality = 82;
    const string FolderFile = "folder.md";

    sealed class Manifest
    {
        public string Tool { get; set; } = "PhotoCurator";
        public int Version { get; set; } = 1;
        public string Source { get; set; } = "";
        public DateTime ExportedAt { get; set; }
        public Dictionary<string, ManifestFile> Files { get; set; } = new();
    }

    sealed class ManifestFile
    {
        public string Source { get; set; } = "";
        public long Size { get; set; }
        public long Modified { get; set; }
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Throws with a readable message when <paramref name="destination"/> isn't a safe place to export to.</summary>
    public static string CheckDestination(PhotoLibrary library, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new CuratorException("Choose where to export to.");
        var dest = PathGuard.Normalize(destination);
        if (PathGuard.Overlaps(dest, library.Root))
        {
            throw new CuratorException("Export somewhere outside the open folder (for example your website's src/content/field folder).");
        }
        if (Directory.Exists(dest) && !File.Exists(Path.Combine(dest, PhotoFiles.ExportMarker)) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            throw new CuratorException("That folder already has other files in it. Pick an empty folder, or one PhotoCurator exported to before.", 409);
        }
        return dest;
    }

    public static ExportResult Export(PhotoLibrary library, string folder, string destination,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken cancel = default)
    {
        var source = PathGuard.Resolve(library.Root, folder);
        if (!Directory.Exists(source)) throw new CuratorException("That folder is no longer there.", 404);
        var dest = CheckDestination(library, destination);
        var sourceRel = PathGuard.ToRelative(library.Root, source);
        var markerPath = Path.Combine(dest, PhotoFiles.ExportMarker);
        var previous = ReadManifest(markerPath);
        var manifest = new Manifest { Source = source, ExportedAt = DateTime.UtcNow };
        var problems = new List<string>();

        var snapshot = library.Refresh();
        bool Under(string rel) => sourceRel.Length == 0 || rel.Equals(sourceRel, PathGuard.Comparison) || rel.StartsWith(sourceRel + "/", PathGuard.Comparison);
        var folders = snapshot.Folders.Where(f => Under(f.Path)).ToList();

        // Folder names → URL-safe slugs, unique among siblings, decided in the scan's (natural) order so re-exports agree.
        var destFolder = new Dictionary<string, string>(PathGuard.Comparer) { [sourceRel] = "" };
        var usedFolderSlugs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            if (f.Path.Equals(sourceRel, PathGuard.Comparison)) continue;
            var parentDest = destFolder[PathGuard.ParentOf(f.Path)];
            destFolder[f.Path] = PathGuard.Join(parentDest, Unique(Names.Slug(f.Name), usedFolderSlugs, parentDest));
        }

        Directory.CreateDirectory(dest);
        var unchanged = 0;
        var written = 0;
        var renders = new List<(string Source, string Target, string Name)>();
        var usedPhotoSlugs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var f in folders)
        {
            cancel.ThrowIfCancellationRequested();
            var folderDest = destFolder[f.Path];
            var folderDir = Combine(dest, folderDest);
            Directory.CreateDirectory(folderDir);
            var folderMd = PathGuard.Join(folderDest, FolderFile);
            if (WriteIfChanged(Path.Combine(folderDir, FolderFile), Sidecar.SerializeFields(("title", f.Name)))) written++; else unchanged++;
            manifest.Files[folderMd] = new ManifestFile { Source = f.Path };
            Reserve(usedPhotoSlugs, folderDest, "folder");

            foreach (var photo in snapshot.Photos.Where(p => p.Folder.Equals(f.Path, PathGuard.Comparison)))
            {
                if (photo.SidecarError is not null)
                {
                    problems.Add($"{photo.Id}: its .md couldn't be read, so it was skipped.");
                    continue;
                }
                var slug = Unique(Names.Slug(Path.GetFileNameWithoutExtension(photo.Name)), usedPhotoSlugs, folderDest);
                var jpgRel = PathGuard.Join(folderDest, slug + ".jpg");
                var mdRel = PathGuard.Join(folderDest, slug + ".md");
                var photoPath = library.FullPath(photo.Id);
                var jpgPath = Combine(dest, jpgRel);

                var data = photo.Sidecar?.Clone() ?? new SidecarData();
                data.Date ??= Sidecar.FormatDate(MetadataReader.ReadTakenDate(photoPath).Date);
                if (WriteIfChanged(Combine(dest, mdRel), Sidecar.Serialize(data, image: "./" + slug + ".jpg"))) written++; else unchanged++;
                manifest.Files[mdRel] = new ManifestFile { Source = photo.Id };

                var stamp = new ManifestFile { Source = photo.Id, Size = photo.Size, Modified = photo.Modified };
                manifest.Files[jpgRel] = stamp;
                if (previous?.Files.TryGetValue(jpgRel, out var before) == true && File.Exists(jpgPath) &&
                    before.Source == stamp.Source && before.Size == stamp.Size && before.Modified == stamp.Modified)
                {
                    unchanged++;
                }
                else
                {
                    renders.Add((photoPath, jpgPath, photo.Id));
                }
            }
        }

        var photoCount = manifest.Files.Keys.Count(k => k.EndsWith(".jpg", StringComparison.Ordinal));
        var done = photoCount - renders.Count;
        progress?.Report((done, photoCount, ""));
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(renders, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancel }, job =>
        {
            try
            {
                ImagePipeline.WriteJpeg(job.Source, job.Target, MaxEdge, Quality);
            }
            catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
            {
                failed.Add(job.Name);
            }
            progress?.Report((Interlocked.Increment(ref done), photoCount, Path.GetFileName(job.Source)));
        });
        written += renders.Count - failed.Count;
        foreach (var name in failed.OrderBy(n => n, Names.Natural)) problems.Add($"{name}: couldn't be converted (unreadable or unsupported image).");

        var removed = 0;
        if (previous is not null)
        {
            foreach (var stale in previous.Files.Keys.Where(k => !manifest.Files.ContainsKey(k)))
            {
                var path = Combine(dest, stale);
                if (!PathGuard.IsInside(dest, path) || !File.Exists(path)) continue;
                File.Delete(path);
                removed++;
                RemoveEmptyParents(dest, Path.GetDirectoryName(path)!);
            }
        }

        WriteManifest(markerPath, manifest);
        return new ExportResult(dest, photoCount, written, unchanged, removed, problems);
    }

    static string Unique(string slug, Dictionary<string, HashSet<string>> used, string scope)
    {
        if (!used.TryGetValue(scope, out var set)) used[scope] = set = new HashSet<string>(StringComparer.Ordinal);
        var candidate = slug;
        for (var n = 2; set.Contains(candidate); n++) candidate = $"{slug}-{n}";
        set.Add(candidate);
        return candidate;
    }

    static void Reserve(Dictionary<string, HashSet<string>> used, string scope, string slug)
    {
        if (!used.TryGetValue(scope, out var set)) used[scope] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(slug);
    }

    static string Combine(string root, string rel) => rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    static bool WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Sidecar.WriteText(path, text);
        return true;
    }

    static void RemoveEmptyParents(string root, string dir)
    {
        while (!dir.Equals(root, PathGuard.Comparison) && PathGuard.IsInside(root, dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    static Manifest? ReadManifest(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions); }
        catch (JsonException) { return null; }
    }

    static void WriteManifest(string path, Manifest manifest) => Sidecar.WriteText(path, JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
}
