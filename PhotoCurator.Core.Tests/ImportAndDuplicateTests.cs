using System.IO.Compression;
using System.Security.Cryptography;
using PhotoCurator.Core.Library;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Tests;

public class ImportAndDuplicateTests
{
    const string Taken = "2024:05:01 10:00:00";

    static PhotoLibrary Open(TempFolder dir) => new(dir.Path, watch: false) { TakenDate = _ => new DateTime(2024, 5, 1, 10, 0, 0) };

    static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// A Google-Photos-style album download, zipped: IMG_1 (a photo the folder already has, since moved, renamed and
    /// captioned), IMG_2 (new, with a Live Photo clip and Takeout JSON), IMG_2(1) (IMG_2 again), IMG_3 (an edited copy of
    /// IMG_1: same moment, different bytes).
    /// </summary>
    static string AlbumZip(TempFolder work, string originalA)
    {
        var album = work["download/Field Work album"];
        Directory.CreateDirectory(album);
        File.Copy(originalA, Path.Combine(album, "IMG_1.jpg"));
        TestImages.Jpeg(Path.Combine(album, "IMG_2.jpg"), 300, 200, "2024:06:01 12:00:00");
        File.Copy(Path.Combine(album, "IMG_2.jpg"), Path.Combine(album, "IMG_2(1).jpg"));
        File.WriteAllText(Path.Combine(album, "IMG_2.MOV"), "live photo clip");
        File.WriteAllText(Path.Combine(album, "IMG_2.jpg.supplemental-metadata.json"), "{\"photoTakenTime\":{\"timestamp\":\"1717243200\"}}");
        TestImages.Jpeg(Path.Combine(album, "IMG_3.jpg"), 320, 240, Taken, quality: 55);
        var zip = work["Field Work album-001.zip"];
        ZipFile.CreateFromDirectory(album, zip);
        return zip;
    }

    [Fact]
    public void ImportSkipsWhatYouHaveEvenAfterMovingItAndCopiesTheRest()
    {
        using var lib = new TempFolder();
        using var work = new TempFolder();
        var a = lib.Photo("Field Work/IMG_1 (1).jpg", 320, 240, Taken);
        lib.Write("Field Work/IMG_1 (1).jpg.md", "---\ncaption: Sanding the patch\n---\n");
        var zip = AlbumZip(work, a);
        var zipHash = FileHash(zip);
        var before = lib.Fingerprint();
        using var library = Open(lib);
        var prints = new Fingerprints(null);

        var plan = Importer.Check(library, prints, new[] { zip });

        Assert.Equal("Inbox/Field Work album", plan.Destination);
        Assert.Equal(("existing", "Field Work/IMG_1 (1).jpg"), Find(plan, "IMG_1.jpg"));
        Assert.Equal(("new", null), Find(plan, "IMG_2.jpg"));
        Assert.Equal(("repeat", "IMG_2.jpg"), Find(plan, "IMG_2(1).jpg"));
        Assert.Equal(("edited", "Field Work/IMG_1 (1).jpg"), Find(plan, "IMG_3.jpg"));

        var result = Importer.Run(library, prints, plan);

        Assert.Equal(2, result.Imported);
        Assert.Empty(result.Problems);
        Assert.Equal(new[] { "IMG_2.jpg", "IMG_2.jpg.supplemental-metadata.json", "IMG_2.MOV", "IMG_3.jpg" },
            Directory.GetFiles(lib["Inbox/Field Work album"]).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(zipHash, FileHash(zip));
        Assert.NotNull(library.Current.Find("Inbox/Field Work album/IMG_2.jpg"));

        // The same download again: nothing left to bring in.
        var again = Importer.Check(library, prints, new[] { zip });
        Assert.Empty(again.ToCopy);
        Assert.Equal(4, again.Count("existing")); // IMG_2(1) now matches the imported IMG_2

        library.Undo();
        Assert.Equal(before, lib.Fingerprint());
        Assert.False(Directory.Exists(lib["Inbox"]));
    }

    [Fact]
    public void ImportFromAFolderWorksTooAndNeverTouchesIt()
    {
        using var lib = new TempFolder();
        using var work = new TempFolder();
        var a = lib.Photo("IMG_1.jpg", 320, 240, Taken);
        AlbumZip(work, a);
        var download = work["download"];
        var before = Directory.EnumerateFiles(download, "*", SearchOption.AllDirectories).ToDictionary(f => f, FileHash);
        using var library = Open(lib);

        var plan = Importer.Check(library, new Fingerprints(null), new[] { work["download/Field Work album"] });
        var result = Importer.Run(library, new Fingerprints(null), plan);

        Assert.Equal(2, result.Imported);
        Assert.True(File.Exists(lib["Inbox/Field Work album/IMG_3.jpg"]));
        Assert.Equal(before, Directory.EnumerateFiles(download, "*", SearchOption.AllDirectories).ToDictionary(f => f, FileHash));
    }

    [Fact]
    public void ImportRefusesThePhotoFolderItself()
    {
        using var lib = new TempFolder();
        lib.Photo("Inbox/x.jpg");
        using var library = Open(lib);
        Assert.Throws<CuratorException>(() => Importer.Check(library, new Fingerprints(null), new[] { lib["Inbox"] }));
        Assert.Throws<CuratorException>(() => Importer.Check(library, new Fingerprints(null), new[] { lib["nope.zip"] }));
    }

    [Theory]
    [InlineData("Field Work-001.zip", "Field Work")]
    [InlineData("Camping (2).zip", "Camping")]
    [InlineData("Lynden house.zip", "Lynden house")]
    public void AlbumNames(string file, string expected) => Assert.Equal(expected, Importer.AlbumName(Path.Combine(Path.GetTempPath(), file)));

    [Fact]
    public void FindsCopiesAndEditsAndKeepingOneMergesAndSetsTheOthersAside()
    {
        using var lib = new TempFolder();
        lib.Photo("a.jpg", 320, 240, Taken);
        lib.Write("a.jpg.md", "---\ntags: [hammer]\n---\n");
        lib.Copy("a.jpg", "Inbox/b.jpg");
        lib.Write("Inbox/b.jpg.md", "---\ncaption: Nice wall\ntags: [drywall]\n---\n\nNotes.\n");
        lib.Photo("Inbox/c.jpg", 320, 240, Taken, quality: 55);
        lib.Photo("d.jpg", 320, 240, "2023:01:01 08:00:00");
        var before = lib.Fingerprint();
        using var library = Open(lib);
        var prints = Filled(library);

        var group = Assert.Single(DuplicateFinder.Find(library, prints));
        Assert.Equal("moment", group.Kind);
        Assert.Equal(new[] { "a.jpg", "Inbox/b.jpg", "Inbox/c.jpg" }, group.Ids);
        Assert.Equal("Inbox/b.jpg", group.Keep); // the captioned one

        library.SetAsideDuplicates(new[] { ("a.jpg", (IReadOnlyList<string>)new[] { "Inbox/b.jpg", "Inbox/c.jpg" }) });

        var kept = Sidecar.Parse(File.ReadAllText(lib["a.jpg.md"]));
        Assert.Equal("Nice wall", kept.Caption);
        Assert.Equal(new[] { "hammer", "drywall" }, kept.Tags);
        Assert.Equal("Notes.", kept.Description);
        Assert.True(File.Exists(lib["Duplicates set aside/b.jpg"]));
        Assert.True(File.Exists(lib["Duplicates set aside/c.jpg"]));
        Assert.Empty(DuplicateFinder.Find(library, Filled(library)));

        library.Undo();
        Assert.Equal(before, lib.Fingerprint());
    }

    [Fact]
    public void ExactCopiesAreOneFileGroup()
    {
        using var lib = new TempFolder();
        lib.Photo("x/one.jpg", 200, 100);
        lib.Copy("x/one.jpg", "y/two.jpg");
        lib.Photo("z/other.jpg", 201, 100);
        using var library = Open(lib);

        var group = Assert.Single(DuplicateFinder.Find(library, Filled(library)));
        Assert.Equal("file", group.Kind);
        Assert.Equal(new[] { "x/one.jpg", "y/two.jpg" }, group.Ids);
    }

    [Fact]
    public void DismissedBurstStaysHiddenUntilAnotherCopyTurnsUp()
    {
        using var lib = new TempFolder();
        lib.Photo("burst/1.jpg", 320, 240, Taken);
        lib.Photo("burst/2.jpg", 320, 240, Taken, quality: 60);
        using var library = Open(lib);
        var prints = Filled(library);
        var group = Assert.Single(DuplicateFinder.Find(library, prints));

        prints.Dismiss(group.Ids.Select(id => KeyOf(library, id)));
        Assert.Empty(DuplicateFinder.Find(library, prints));

        lib.Photo("burst/3.jpg", 320, 240, Taken, quality: 40);
        library.Refresh();
        Assert.Equal(3, Assert.Single(DuplicateFinder.Find(library, Filled(library, prints))).Ids.Count);
    }

    [Fact]
    public void MomentsMatchRecompressedAndDownsizedCopiesButNotCrops()
    {
        using var lib = new TempFolder();
        var original = Moment.Read(lib.Photo("a.jpg", 400, 300, Taken))!;
        Assert.True(original.Matches(Moment.Read(lib.Photo("b.jpg", 400, 300, Taken, quality: 40))!));
        Assert.True(original.Matches(Moment.Read(lib.Photo("c.jpg", 200, 150, Taken))!));
        Assert.False(original.Matches(Moment.Read(lib.Photo("d.jpg", 300, 300, Taken))!));
        Assert.Null(Moment.Read(lib.Photo("e.jpg", 400, 300)));
        Assert.Equal(original.ToString(), Moment.Parse(original.ToString())!.ToString());
    }

    static (string Kind, string? Match) Find(ImportPlan plan, string name)
    {
        var photo = plan.Photos.Single(p => p.Name == name);
        return (photo.Kind, photo.Match);
    }

    static string KeyOf(PhotoLibrary library, string id)
    {
        var photo = library.Current.Find(id)!;
        return Fingerprints.Key(photo.Name, photo.Size, photo.Modified);
    }

    /// <summary>Reads every photo's moment now (the app does this in the background).</summary>
    static Fingerprints Filled(PhotoLibrary library, Fingerprints? prints = null)
    {
        prints ??= new Fingerprints(null);
        foreach (var photo in library.Current.Photos) prints.MomentOf(library.FullPath(photo.Id), KeyOf(library, photo.Id));
        return prints;
    }
}
