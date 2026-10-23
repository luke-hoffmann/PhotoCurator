using System.Text.Json;
using ImageMagick;
using PhotoCurator.Core.Library;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Tests;

public class MediaTests
{
    [Fact]
    public void ExportMakesUprightWebCopiesWithoutGps()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        var original = dir.Photo("Field Work/Prepping for paint/IMG_4821.jpg", 4032, 3024, "2025:02:14 10:31:00", Orientation.RightTop, gps: true);
        dir.Write("Field Work/Prepping for paint/IMG_4821.jpg.md", "---\ncaption: 'Sanding: round two'\ntags: [drywall]\nfeatured: true\n---\n\nMore words.\n");
        dir.Photo("Field Work/IMG_1.jpg", 200, 100);
        dir.Photo("Camping/tent.jpg", 200, 100);
        var before = dir.Fingerprint();
        using var lib = new PhotoLibrary(dir.Path, watch: false);
        var dest = Path.Combine(site.Path, "src", "content", "field");

        var result = WebExporter.Export(lib, "Field Work", dest);

        Assert.Equal(2, result.Photos);
        Assert.Empty(result.Problems);
        Assert.Equal(before, dir.Fingerprint());

        using var web = new MagickImage(Path.Combine(dest, "prepping-for-paint", "img-4821.jpg"));
        Assert.Equal((1800u, 2400u), (web.Width, web.Height));
        Assert.Null(web.GetExifProfile());
        Assert.Null(web.GetXmpProfile());
        // Rotated 90° clockwise: the red (left) half of the original is now on top.
        Assert.True(web.GetPixels().GetPixel(900, 200).ToColor()!.R > 200);
        Assert.True(web.GetPixels().GetPixel(900, 2200).ToColor()!.B > 200);

        Assert.Equal("---\nimage: ./img-4821.jpg\ncaption: 'Sanding: round two'\ndate: 2025-02-14T10:31:00\ntags: [drywall]\nfeatured: true\n---\n\nMore words.\n",
            File.ReadAllText(Path.Combine(dest, "prepping-for-paint", "img-4821.md")));
        Assert.Equal("---\ntitle: Prepping for paint\n---\n", File.ReadAllText(Path.Combine(dest, "prepping-for-paint", "folder.md")));
        Assert.Equal("---\ntitle: Field Work\n---\n", File.ReadAllText(Path.Combine(dest, "folder.md")));
        Assert.True(File.Exists(Path.Combine(dest, "img-1.jpg")));
        Assert.False(Directory.Exists(Path.Combine(dest, "camping")));
        Assert.True(File.Exists(Path.Combine(dest, PhotoFiles.ExportMarker)));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void ReexportOnlyRewritesChangesAndOnlyDeletesItsOwnFiles()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        dir.Photo("Trip/a.jpg", 300, 200);
        dir.Photo("Trip/b.jpg", 300, 200);
        Directory.CreateDirectory(dir["Rejects"]);
        using var lib = new PhotoLibrary(dir.Path, watch: false) { TakenDate = _ => new DateTime(2024, 1, 1) };
        var dest = Path.Combine(site.Path, "out");

        var first = WebExporter.Export(lib, "Trip", dest);
        File.WriteAllText(Path.Combine(dest, "my-notes.txt"), "mine");
        var aTime = File.GetLastWriteTimeUtc(Path.Combine(dest, "a.jpg"));

        lib.Move(new[] { "Trip/b.jpg" }, "Rejects");
        lib.SetCaption(new[] { "Trip/a.jpg" }, "Tent pitched");
        var second = WebExporter.Export(lib, "Trip", dest);

        Assert.Equal(2, first.Photos);
        Assert.Equal(1, second.Photos);
        Assert.Equal(2, second.Removed);
        Assert.False(File.Exists(Path.Combine(dest, "b.jpg")));
        Assert.False(File.Exists(Path.Combine(dest, "b.md")));
        Assert.Equal(aTime, File.GetLastWriteTimeUtc(Path.Combine(dest, "a.jpg")));
        Assert.Contains("caption: Tent pitched", File.ReadAllText(Path.Combine(dest, "a.md")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "my-notes.txt")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dest, PhotoFiles.ExportMarker)));
        Assert.Equal(3, doc.RootElement.GetProperty("files").EnumerateObject().Count());
    }

    [Fact]
    public void ExportRefusesUnsafeDestinations()
    {
        using var dir = new TempFolder();
        using var other = new TempFolder();
        dir.Photo("Trip/a.jpg");
        other.Write("existing.txt", "x");
        using var lib = new PhotoLibrary(dir.Path, watch: false);

        Assert.Throws<CuratorException>(() => WebExporter.Export(lib, "Trip", dir["Trip/web"]));
        Assert.Throws<CuratorException>(() => WebExporter.Export(lib, "Trip", Path.GetDirectoryName(dir.Path)!));
        Assert.Throws<CuratorException>(() => WebExporter.Export(lib, "Trip", other.Path));
        Assert.Equal("x", File.ReadAllText(other["existing.txt"]));
    }

    [Fact]
    public void SameNamesGetDistinctSlugs()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        dir.Photo("T/IMG_1.jpg");
        dir.Photo("T/img-1.png");
        dir.Photo("T/folder.jpg");
        using var lib = new PhotoLibrary(dir.Path, watch: false);

        WebExporter.Export(lib, "T", site["out"]);

        Assert.Equal(new[] { "folder-2.jpg", "img-1-2.jpg", "img-1.jpg" },
            Directory.GetFiles(site["out"], "*.jpg").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task HeicDecodesForThumbnailsDatesAndExport()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        Directory.CreateDirectory(dir["Camping"]);
        var heic = dir["Camping/IMG_0001.HEIC"];
        File.Copy(Path.Combine(TestImages.FixturesDir, "sample.heic"), heic);

        Assert.Equal((new DateTime(2024, 7, 4, 16, 20, 0), DateSource.Camera), MetadataReader.ReadTakenDate(heic));
        Assert.Equal((640, 480), MetadataReader.ReadSize(heic));

        var cache = new ThumbnailCache(site["cache"]);
        var thumb = await cache.GetThumbnailAsync(heic, CancellationToken.None);
        using (var t = new MagickImage(thumb!)) Assert.Equal((512u, 384u), (t.Width, t.Height));
        Assert.Equal(thumb, await cache.GetThumbnailAsync(heic, CancellationToken.None));

        using var lib = new PhotoLibrary(dir.Path, watch: false);
        var result = WebExporter.Export(lib, "Camping", site["out"]);
        Assert.Empty(result.Problems);
        Assert.Contains("date: 2024-07-04T16:20:00", File.ReadAllText(site["out/img-0001.md"]));
        using var web = new MagickImage(site["out/img-0001.jpg"]);
        Assert.Equal(MagickFormat.Jpeg, web.Format);
    }

    [Fact]
    public void BrokenImageIsReportedNotFatal()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        dir.Write("T/broken.jpg", "not really a jpeg");
        dir.Photo("T/fine.jpg");
        using var lib = new PhotoLibrary(dir.Path, watch: false);

        var result = WebExporter.Export(lib, "T", site["out"]);

        Assert.Single(result.Problems);
        Assert.True(File.Exists(site["out/fine.jpg"]));
    }

    [Fact]
    public void TakeoutDateIsUsedWhenThereIsNoExif()
    {
        using var dir = new TempFolder();
        var photo = dir.Photo("p/IMG_9.jpg");
        dir.Write("p/IMG_9.jpg.supplemental-metadata.json", "{\"photoTakenTime\": {\"timestamp\": \"1707906660\", \"formatted\": \"…\"}}");

        var (date, source) = MetadataReader.ReadTakenDate(photo);

        Assert.Equal(DateSource.Takeout, source);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1707906660).ToLocalTime().DateTime, date);
    }

    [Fact]
    public void CopyKeepsEverythingAndNeverCopiesIntoItself()
    {
        using var dir = new TempFolder();
        using var parent = new TempFolder();
        dir.Write("a.jpg", "a");
        dir.Write("deep/b.HEIC.json", "{}");
        dir.Write(".hidden/c.txt", "c");
        File.SetLastWriteTimeUtc(dir["a.jpg"], new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var copy = FolderCopier.Copy(dir.Path, parent.Path);
        var second = FolderCopier.Copy(dir.Path, parent.Path);

        Assert.EndsWith(" (copy)", copy);
        Assert.EndsWith(" (copy 2)", second);
        Assert.Equal(dir.Fingerprint(), new TempFolderView(copy).Fingerprint());
        Assert.Equal(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(copy, "a.jpg")));
        Assert.Throws<CuratorException>(() => FolderCopier.Copy(dir.Path, dir["deep"]));
    }

    sealed class TempFolderView(string path)
    {
        public Dictionary<string, string> Fingerprint() =>
            Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(path, f).Replace('\\', '/'),
                    f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
    }
}
