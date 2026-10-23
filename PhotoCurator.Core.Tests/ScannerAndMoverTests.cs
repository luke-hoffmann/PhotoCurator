using PhotoCurator.Core.Library;

namespace PhotoCurator.Core.Tests;

public class ScannerTests
{
    [Fact]
    public void FindsPhotosAtAnyDepthAndSkipsHiddenGitAndExports()
    {
        using var dir = new TempFolder();
        dir.Write("top.jpg", "x");
        dir.Write("Takeout/Google Photos/2023/deep/IMG_10.HEIC", "x");
        dir.Write("Takeout/Google Photos/2023/deep/IMG_2.HEIC", "x");
        dir.Write("Takeout/Google Photos/2023/deep/IMG_2.HEIC.json", "{}");
        dir.Write("Takeout/Google Photos/2023/deep/IMG_2.MOV", "x");
        dir.Write("notes.txt", "x");
        dir.Write(".git/objects/a.jpg", "x");
        dir.Write(".hidden/b.png", "x");
        dir.Write("site/field/.photocurator-export", "{}");
        dir.Write("site/field/img.jpg", "x");
        dir.Write("Empty/.keep", "");

        var snap = Scanner.Scan(dir.Path, new SidecarCache());

        Assert.Equal(
            new[] { "top.jpg", "Takeout/Google Photos/2023/deep/IMG_2.HEIC", "Takeout/Google Photos/2023/deep/IMG_10.HEIC" },
            snap.Photos.Select(p => p.Id));
        Assert.Contains(snap.Folders, f => f.Path == "Empty");
        Assert.DoesNotContain(snap.Folders, f => f.Path.StartsWith("site/field"));
        Assert.DoesNotContain(snap.Folders, f => f.Path.Contains(".git"));
        Assert.Equal(3, snap.Folders.Single(f => f.Path == "").Total);
        Assert.Equal(2, snap.Folders.Single(f => f.Path == "Takeout").Total);
        Assert.Equal(0, snap.Folders.Single(f => f.Path == "Takeout").Count);
    }

    [Fact]
    public void ReadsSidecarsAndCountsTags()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "x");
        dir.Write("a.jpg.md", "---\ncaption: A\ntags: [hammer, headshot]\n---\n");
        dir.Write("b.jpg", "x");
        dir.Write("b.jpg.md", "---\ntags: [hammer]\n---\n");
        dir.Write("c.jpg", "x");
        dir.Write("c.jpg.md", "---\ncaption: [broken\n---\n");

        var snap = Scanner.Scan(dir.Path, new SidecarCache());

        Assert.Equal("A", snap.Find("a.jpg")!.Sidecar!.Caption);
        Assert.NotNull(snap.Find("c.jpg")!.SidecarError);
        Assert.True(snap.Find("c.jpg")!.HasSidecar);
        Assert.Equal(new[] { new KeyValuePair<string, int>("hammer", 2), new("headshot", 1) }, snap.Tags);
    }

    [Fact]
    public void SignatureChangesWhenASidecarChanges()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "x");
        var cache = new SidecarCache();
        var before = Scanner.Scan(dir.Path, cache).Signature;
        Assert.Equal(before, Scanner.Scan(dir.Path, cache).Signature);
        dir.Write("a.jpg.md", "---\ncaption: new\n---\n");
        Assert.NotEqual(before, Scanner.Scan(dir.Path, cache).Signature);
    }

    [Fact]
    public void NaturalOrder()
    {
        var names = new[] { "IMG_10.jpg", "img_2.jpg", "IMG_1.jpg", "IMG_02.jpg", "a.jpg" };
        Assert.Equal(new[] { "a.jpg", "IMG_1.jpg", "IMG_02.jpg", "img_2.jpg", "IMG_10.jpg" }, names.OrderBy(n => n, Names.Natural));
    }
}

public class FileMoverTests
{
    [Fact]
    public void CompanionsTravelWithThePhoto()
    {
        using var dir = new TempFolder();
        var photo = dir.Write("in/IMG_1.HEIC", "photo");
        dir.Write("in/IMG_1.HEIC.md", "sidecar");
        dir.Write("in/IMG_1.HEIC.supplemental-metadata.json", "{}");
        dir.Write("in/IMG_1.HEIC.supplemental-meta.json", "{}");
        dir.Write("in/IMG_1.MOV", "clip");
        dir.Write("in/IMG_12.HEIC", "other");
        dir.Write("in/IMG_12.HEIC.json", "{}");
        Directory.CreateDirectory(dir["out"]);

        var moves = FileMover.MovePhoto(photo, dir["out"]);

        Assert.Equal(5, moves.Count);
        Assert.Equal(new[] { "IMG_1.HEIC", "IMG_1.HEIC.md", "IMG_1.HEIC.supplemental-meta.json", "IMG_1.HEIC.supplemental-metadata.json", "IMG_1.MOV" },
            Directory.GetFiles(dir["out"]).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "IMG_12.HEIC", "IMG_12.HEIC.json" }, Directory.GetFiles(dir["in"]).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        FileMover.Revert(moves);
        Assert.Empty(Directory.GetFiles(dir["out"]));
        Assert.Equal(7, Directory.GetFiles(dir["in"]).Length);
    }

    [Fact]
    public void LiveClipStaysWhenAnotherStillSharesItsName()
    {
        using var dir = new TempFolder();
        var photo = dir.Write("in/IMG_1.HEIC", "photo");
        dir.Write("in/IMG_1.JPG", "edited copy");
        dir.Write("in/IMG_1.MOV", "clip");
        Directory.CreateDirectory(dir["out"]);

        FileMover.MovePhoto(photo, dir["out"]);

        Assert.True(File.Exists(dir["in/IMG_1.MOV"]));
    }

    [Fact]
    public void NameClashGetsANumberAndCompanionsFollow()
    {
        using var dir = new TempFolder();
        var photo = dir.Write("in/IMG_1.jpg", "new");
        dir.Write("in/IMG_1.jpg.md", "new sidecar");
        dir.Write("in/IMG_1.jpg.json", "{}");
        dir.Write("out/IMG_1.jpg", "existing");
        dir.Write("out/IMG_1.jpg.md", "existing sidecar");

        var moves = FileMover.MovePhoto(photo, dir["out"]);

        Assert.EndsWith("IMG_1 (1).jpg", moves[0].To);
        Assert.Equal("existing", File.ReadAllText(dir["out/IMG_1.jpg"]));
        Assert.Equal("new sidecar", File.ReadAllText(dir["out/IMG_1 (1).jpg.md"]));
        Assert.True(File.Exists(dir["out/IMG_1 (1).jpg.json"]));
    }

    [Fact]
    public void TakeoutDuplicateCounterIsRecognised()
    {
        var siblings = new[] { "IMG_1(1).jpg", "IMG_1.jpg(1).json", "IMG_1.jpg.supplemental-metadata(1).json", "IMG_1.jpg.json", "IMG_1.jpg.notes.json" };
        Assert.Equal(new[] { "IMG_1.jpg(1).json", "IMG_1.jpg.supplemental-metadata(1).json" }, PhotoFiles.TakeoutCompanions("IMG_1(1).jpg", siblings));
        Assert.Equal(new[] { "IMG_1.jpg.json" }, PhotoFiles.TakeoutCompanions("IMG_1.jpg", siblings));
    }
}

public class PathGuardTests
{
    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("a/../../outside.jpg")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    public void RejectsPathsOutsideTheRoot(string rel)
    {
        using var dir = new TempFolder();
        Assert.Throws<CuratorException>(() => PathGuard.Resolve(dir.Path, rel));
    }

    [Fact]
    public void ResolvesInsideTheRoot()
    {
        using var dir = new TempFolder();
        Assert.Equal(dir["a/b.jpg"], PathGuard.Resolve(dir.Path, "a/b.jpg"));
        Assert.Equal(dir.Path, PathGuard.Resolve(dir.Path, ""));
        Assert.False(PathGuard.IsInside(dir.Path, dir.Path + "-sibling"));
    }

    [Theory]
    [InlineData("Field Work", "Field Work")]
    [InlineData("  Camping  ", "Camping")]
    public void AcceptsFolderNames(string input, string expected) => Assert.Equal(expected, Names.ValidateFolderName(input));

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData(".hidden")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("ends.")]
    public void RejectsFolderNames(string input) => Assert.Throws<CuratorException>(() => Names.ValidateFolderName(input));

    [Theory]
    [InlineData("Prepping for Paint (Lynden)", "prepping-for-paint-lynden")]
    [InlineData("Café Déjà vu", "cafe-deja-vu")]
    [InlineData("IMG_4821", "img-4821")]
    [InlineData("!!!", "untitled")]
    public void Slugs(string input, string expected) => Assert.Equal(expected, Names.Slug(input));
}
