using PhotoCurator.Core.Library;

namespace PhotoCurator.Core.Tests;

public class LibraryTests
{
    static PhotoLibrary Open(TempFolder dir) => new(dir.Path, watch: false) { TakenDate = _ => new DateTime(2025, 2, 14, 10, 31, 0) };

    [Fact]
    public void TaggingABatchTouchesOnlyTheSelectedPhotos()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        dir.Write("b.jpg", "b");
        dir.Write("c.jpg", "c");
        dir.Write("c.jpg.md", "---\ncaption: keep me\ntags: [old]\n---\n");
        using var lib = Open(dir);

        lib.ChangeTags(new[] { "a.jpg", "c.jpg" }, new[] { "Hammer", "headshot" }, null);

        Assert.Equal("---\ndate: 2025-02-14T10:31:00\ntags: [hammer, headshot]\n---\n", File.ReadAllText(dir["a.jpg.md"]));
        Assert.False(File.Exists(dir["b.jpg.md"]));
        Assert.Equal("---\ncaption: keep me\ntags: [old, hammer, headshot]\n---\n", File.ReadAllText(dir["c.jpg.md"]));
        Assert.Equal(2, lib.Current.Tags.Single(t => t.Key == "hammer").Value);

        lib.ChangeTags(new[] { "a.jpg", "b.jpg", "c.jpg" }, null, new[] { "hammer" });
        Assert.False(File.Exists(dir["b.jpg.md"]));
        Assert.DoesNotContain(lib.Current.Tags, t => t.Key == "hammer");
    }

    [Fact]
    public void MoveAndUndoRestoreTheExactLayout()
    {
        using var dir = new TempFolder();
        dir.Write("dl/IMG_1.HEIC", "1");
        dir.Write("dl/IMG_1.HEIC.json", "{}");
        dir.Write("dl/IMG_1.MOV", "clip");
        dir.Write("dl/IMG_2.HEIC", "2");
        dir.Write("dl/IMG_2.HEIC.md", "---\ncaption: two\n---\n");
        dir.Write("dl/IMG_3.HEIC", "3");
        var before = dir.Fingerprint();
        using var lib = Open(dir);

        var folder = lib.CreateFolder("", "Field Work").Folder!;
        var sub = lib.CreateFolder(folder, "Prepping for paint").Folder!;
        var result = lib.Move(new[] { "dl/IMG_1.HEIC", "dl/IMG_2.HEIC" }, sub);

        Assert.Equal("Field Work/Prepping for paint/IMG_1.HEIC", result.Moved!["dl/IMG_1.HEIC"]);
        Assert.True(File.Exists(dir["Field Work/Prepping for paint/IMG_1.MOV"]));
        Assert.True(File.Exists(dir["Field Work/Prepping for paint/IMG_1.HEIC.json"]));
        Assert.Contains("date: 2025-02-14T10:31:00", File.ReadAllText(dir["Field Work/Prepping for paint/IMG_1.HEIC.md"]));
        Assert.Equal("---\ncaption: two\n---\n", File.ReadAllText(dir["Field Work/Prepping for paint/IMG_2.HEIC.md"]));
        Assert.Equal("1", File.ReadAllText(dir["Field Work/Prepping for paint/IMG_1.HEIC"]));

        lib.Undo(); // the move
        lib.Undo(); // "Prepping for paint"
        lib.Undo(); // "Field Work"

        Assert.Equal(before, dir.Fingerprint());
        Assert.False(Directory.Exists(dir["Field Work"]));
        Assert.Throws<CuratorException>(() => lib.Undo());
    }

    [Fact]
    public void EditsOfOneFieldFoldIntoOneUndoStep()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        using var lib = Open(dir);

        lib.UpdatePhoto("a.jpg", new PhotoUpdate(Caption: "S"), "caption");
        lib.UpdatePhoto("a.jpg", new PhotoUpdate(Caption: "Sand"), "caption");
        lib.UpdatePhoto("a.jpg", new PhotoUpdate(Caption: "Sanding walls\nbefore paint"), "caption");
        lib.UpdatePhoto("a.jpg", new PhotoUpdate(Tags: new() { "drywall" }), "tags");

        Assert.Equal("Sanding walls before paint", lib.Current.Find("a.jpg")!.Sidecar!.Caption);
        lib.Undo();
        Assert.Empty(lib.Current.Find("a.jpg")!.Sidecar!.Tags);
        Assert.Equal("Sanding walls before paint", lib.Current.Find("a.jpg")!.Sidecar!.Caption);
        lib.Undo();
        Assert.False(File.Exists(dir["a.jpg.md"]));
    }

    [Fact]
    public void UndoLeavesFilesChangedOutsideTheApp()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        using var lib = Open(dir);
        lib.SetCaption(new[] { "a.jpg" }, "From the app");
        File.WriteAllText(dir["a.jpg.md"], "---\ncaption: Edited in VS Code\n---\n");

        var result = lib.Undo();

        Assert.Contains("left as is", result.Message);
        Assert.Contains("Edited in VS Code", File.ReadAllText(dir["a.jpg.md"]));
    }

    [Fact]
    public void UnreadableSidecarIsNeverOverwritten()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        dir.Write("a.jpg.md", "---\ncaption: [oops\n---\n");
        using var lib = Open(dir);

        var e = Assert.Throws<CuratorException>(() => lib.SetCaption(new[] { "a.jpg" }, "new"));
        Assert.Contains("couldn't be read", e.Message);
        Assert.Equal("---\ncaption: [oops\n---\n", File.ReadAllText(dir["a.jpg.md"]));
    }

    [Fact]
    public void RenameFolderAndUndo()
    {
        using var dir = new TempFolder();
        dir.Write("Camping/a.jpg", "a");
        using var lib = Open(dir);

        Assert.Throws<CuratorException>(() => lib.RenameFolder("Camping", "bad/name"));
        lib.RenameFolder("Camping", "Camping Trips");
        Assert.True(File.Exists(dir["Camping Trips/a.jpg"]));
        Assert.NotNull(lib.Current.Find("Camping Trips/a.jpg"));

        lib.Undo();
        Assert.True(File.Exists(dir["Camping/a.jpg"]));
    }

    [Fact]
    public void RefusesIdsOutsideTheRoot()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        using var lib = Open(dir);
        Assert.Throws<CuratorException>(() => lib.Move(new[] { "a.jpg" }, "../elsewhere"));
        Assert.Throws<CuratorException>(() => lib.SetCaption(new[] { "../a.jpg" }, "x"));
    }

    [Fact]
    public async Task NewFilesShowUpWithoutReopening()
    {
        using var dir = new TempFolder();
        dir.Write("a.jpg", "a");
        using var lib = new PhotoLibrary(dir.Path, watch: true);
        var changed = new TaskCompletionSource();
        lib.Changed += () => changed.TrySetResult();

        dir.Write("later/b.jpg", "b");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(lib.Current.Find("later/b.jpg"));
    }
}
