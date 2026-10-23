using PhotoCurator.Core.Library;

namespace PhotoCurator.Core.Tests;

public class SidecarTests
{
    [Fact]
    public void RoundTripKeepsHandAddedKeysInOrder()
    {
        var text = """
            ---
            featured: true
            caption: Sanding the patched drywall
            client:
              name: Lynden house
            tags: [drywall, Sanding]
            date: 2025-02-14T10:31:00
            ---

            Primer went on the next day.
            """;
        var data = Sidecar.Parse(text);
        Assert.Equal("Sanding the patched drywall", data.Caption);
        Assert.Equal(new[] { "drywall", "sanding" }, data.Tags);
        Assert.Equal("2025-02-14T10:31:00", data.Date);
        Assert.Equal("Primer went on the next day.", data.Description);

        data.Tags.Add("primer");
        Assert.Equal("""
            ---
            caption: Sanding the patched drywall
            date: 2025-02-14T10:31:00
            tags: [drywall, sanding, primer]
            featured: true
            client:
              name: Lynden house
            ---

            Primer went on the next day.

            """.Replace("\r\n", "\n"), Sidecar.Serialize(data));
    }

    [Theory]
    [InlineData("Wire: phase 2")]
    [InlineData("\"Quoted\" and 'single'")]
    [InlineData("# not a comment")]
    [InlineData("yes")]
    [InlineData("- dash first")]
    [InlineData("Très bien, 100%")]
    public void CaptionsSurviveARoundTrip(string caption)
    {
        var data = new SidecarData { Caption = caption, Tags = { "a b", "c" } };
        var back = Sidecar.Parse(Sidecar.Serialize(data));
        Assert.Equal(caption, back.Caption);
        Assert.Equal(new[] { "a b", "c" }, back.Tags);
    }

    [Fact]
    public void LongCaptionsStayOnOneLine()
    {
        var caption = string.Join(' ', Enumerable.Repeat("troweling the garage slab", 12));
        var text = Sidecar.Serialize(new SidecarData { Caption = caption });
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TagsMayBeACommaSeparatedString()
    {
        var data = Sidecar.Parse("---\ntags: hammer, Headshot , hammer\n---\n");
        Assert.Equal(new[] { "hammer", "headshot" }, data.Tags);
    }

    [Fact]
    public void FileWithoutFrontMatterIsAllDescription()
    {
        var data = Sidecar.Parse("Just some notes\r\nabout this photo\r\n");
        Assert.Equal("Just some notes\nabout this photo", data.Description);
        Assert.Equal("", data.Caption);
    }

    [Theory]
    [InlineData("---\ncaption: x\n")]
    [InlineData("---\ncaption: [unclosed\n---\n")]
    [InlineData("---\n- a\n- b\n---\n")]
    public void BrokenFrontMatterIsReportedNotGuessed(string text)
    {
        Assert.Throws<FormatException>(() => Sidecar.Parse(text));
    }

    [Theory]
    [InlineData("2025-02-14", "2025-02-14")]
    [InlineData("2025-02-14T10:31", "2025-02-14T10:31:00")]
    [InlineData("2025-02-14 10:31:05", "2025-02-14T10:31:05")]
    [InlineData("  ", null)]
    public void DatesNormalize(string input, string? expected)
    {
        Assert.Equal(expected, Sidecar.NormalizeDate(input));
    }

    [Fact]
    public void BadDateIsAReadableError()
    {
        var e = Assert.Throws<CuratorException>(() => Sidecar.NormalizeDate("Feb 14"));
        Assert.Contains("2025-02-14", e.Message);
    }

    [Fact]
    public void EmptySidecarIsJustFences()
    {
        Assert.Equal("---\n---\n", Sidecar.Serialize(new SidecarData()));
        Assert.Equal("", Sidecar.Parse("---\n---\n").Caption);
    }
}
