using PhotoCurator.Core.Library;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Tests;

public class WriteUpTests
{
    [Fact]
    public void RoundTripKeepsHandAddedKeysAndTheStory()
    {
        var text = """
            ---
            featured: true
            title: Attic insulation
            cover: IMG_2.jpg
            summary: 'Out with the old: R-49 in'
            ---

            The attic had 40 years of old insulation.

            ![Pulling it out](IMG_1.jpg)
            """;
        var data = WriteUp.Parse(text);
        Assert.Equal("Attic insulation", data.Title);
        Assert.Equal("Out with the old: R-49 in", data.Summary);
        Assert.Equal("IMG_2.jpg", data.Cover);
        Assert.StartsWith("The attic had", data.Body);

        Assert.Equal("""
            ---
            title: Attic insulation
            summary: 'Out with the old: R-49 in'
            cover: IMG_2.jpg
            featured: true
            ---

            The attic had 40 years of old insulation.

            ![Pulling it out](IMG_1.jpg)

            """.Replace("\r\n", "\n"), WriteUp.Serialize(data));
    }

    [Fact]
    public void FindsPhotosInTheStory()
    {
        var body = "Intro ![a \\] b](IMG_1.jpg) text\n\n![](<Big Sur/IMG 2.jpg> \"title\")\n\n![x](sub%20dir/c.png)";
        var images = WriteUp.Images(body);
        Assert.Equal(new[] { ("a ] b", "IMG_1.jpg", (string?)null), ("", "Big Sur/IMG 2.jpg", "title"), ("x", "sub dir/c.png", null) },
            images.Select(i => (i.Caption, i.Path, i.Title)));
    }

    [Fact]
    public void RewritesAndDropsPhotos()
    {
        var body = "One.\n\n![Keep](a.jpg)\n\n![Gone](missing.jpg)\n\nTwo ![inline](b.jpg) here.";
        var rewritten = WriteUp.RewriteImages(body, i => i.Path == "missing.jpg" ? null : "./" + i.Path.Replace(".jpg", "-web.jpg"));
        Assert.Equal("One.\n\n![Keep](./a-web.jpg)\n\nTwo ![inline](./b-web.jpg) here.", rewritten);
    }

    [Theory]
    [InlineData("Caption", "IMG_1.jpg", "![Caption](IMG_1.jpg)")]
    [InlineData("A [b]", "Big Sur/IMG 1.jpg", "![A \\[b\\]](<Big Sur/IMG 1.jpg>)")]
    [InlineData("", "x(1).jpg", "![](<x(1).jpg>)")]
    public void PhotoMarkdown(string caption, string path, string expected)
    {
        Assert.Equal(expected, WriteUp.Markdown(caption, path));
        var back = Assert.Single(WriteUp.Images(expected));
        Assert.Equal((caption, path), (back.Caption, back.Path));
    }

    [Theory]
    [InlineData("A/B", "x.jpg", "A/B/x.jpg")]
    [InlineData("A/B", "./sub/x.jpg", "A/B/sub/x.jpg")]
    [InlineData("A/B", "../x.jpg", "A/x.jpg")]
    [InlineData("", "x.jpg", "x.jpg")]
    [InlineData("", "../x.jpg", null)]
    public void ResolvesPhotoPaths(string folder, string path, string? expected) => Assert.Equal(expected, WriteUp.ResolvePhoto(folder, path));

    [Fact]
    public void LibrarySavesUndoesAndFlagsTheFolder()
    {
        using var dir = new TempFolder();
        dir.Photo("Attic/IMG_1.jpg");
        using var lib = new PhotoLibrary(dir.Path, watch: false);

        Assert.Equal("", lib.ReadWriteUp("Attic").Title);
        lib.SaveWriteUp("Attic", new WriteUpData());
        Assert.False(File.Exists(dir["Attic/folder.md"])); // nothing written for nothing

        lib.SaveWriteUp("Attic", new WriteUpData { Title = "Attic insulation", Summary = "R-49", Date = "2024-05-01", Body = "Text.\n\n![It](IMG_1.jpg)" });
        Assert.Equal("---\ntitle: Attic insulation\nsummary: R-49\ndate: 2024-05-01\n---\n\nText.\n\n![It](IMG_1.jpg)\n", File.ReadAllText(dir["Attic/folder.md"]));
        Assert.True(lib.Current.Folders.Single(f => f.Path == "Attic").HasWriteUp);
        Assert.NotNull(lib.Current.Find("Attic/IMG_1.jpg")); // folder.md isn't a photo

        lib.SaveWriteUp("Attic", new WriteUpData { Title = "Attic insulation", Body = "Text, more." });
        lib.Undo(); // both edits fold into one step
        Assert.False(File.Exists(dir["Attic/folder.md"]));
    }

    [Fact]
    public void UnreadableWriteUpIsNeverOverwritten()
    {
        using var dir = new TempFolder();
        dir.Write("A/folder.md", "---\ntitle: [oops\n---\n");
        using var lib = new PhotoLibrary(dir.Path, watch: false);
        Assert.Equal(409, Assert.Throws<CuratorException>(() => lib.ReadWriteUp("A")).StatusCode);
        Assert.Throws<CuratorException>(() => lib.SaveWriteUp("A", new WriteUpData { Title = "x" }));
        Assert.Equal("---\ntitle: [oops\n---\n", File.ReadAllText(dir["A/folder.md"]));
    }

    [Fact]
    public void ExportTurnsTheWriteUpIntoAPageWithWebPhotos()
    {
        using var dir = new TempFolder();
        using var site = new TempFolder();
        dir.Photo("Field Work/Attic/IMG_0172.jpg", 400, 300);
        dir.Photo("Field Work/Attic/IMG_0966.jpg", 400, 300);
        dir.Photo("Field Work/Attic/Before/IMG_0001.jpg", 400, 300);
        dir.Photo("Camping/tent.jpg");
        dir.Write("Field Work/Attic/folder.md",
            "---\nsummary: Out with the old\ncover: IMG_0966.jpg\n---\n\nIntro.\n\n![Pulling it out](IMG_0172.jpg)\n\n![Before](Before/IMG_0001.jpg)\n\n![Elsewhere](../../Camping/tent.jpg)\n\nThe end.\n");
        using var lib = new PhotoLibrary(dir.Path, watch: false) { TakenDate = _ => new DateTime(2024, 1, 1) };

        var result = WebExporter.Export(lib, "Field Work", Path.Combine(site.Path, "field"));

        Assert.Equal("""
            ---
            title: Attic
            summary: Out with the old
            cover: ./img-0966.jpg
            ---

            Intro.

            ![Pulling it out](./img-0172.jpg)

            ![Before](./before/img-0001.jpg)

            The end.

            """.Replace("\r\n", "\n"), File.ReadAllText(Path.Combine(site.Path, "field", "attic", "folder.md")));
        Assert.Equal("---\ntitle: Field Work\n---\n", File.ReadAllText(Path.Combine(site.Path, "field", "folder.md")));
        Assert.Contains(result.Problems, p => p.Contains("Camping/tent.jpg"));
        Assert.True(File.Exists(Path.Combine(site.Path, "field", "attic", "before", "img-0001.jpg")));
    }
}
