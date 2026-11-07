using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace PhotoCurator.Core.Library;

/// <summary>A folder's write-up: the project's story, with photos from the folder placed in the text.</summary>
public sealed class WriteUpData
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>"yyyy-MM-dd" (or a full timestamp), or null.</summary>
    public string? Date { get; set; }
    /// <summary>A photo path relative to the folder, or null for "the first photo in the story".</summary>
    public string? Cover { get; set; }
    /// <summary>Markdown. Photos are images with paths relative to the folder: ![Caption](IMG_1.HEIC).</summary>
    public string Body { get; set; } = "";

    internal List<KeyValuePair<YamlNode, YamlNode>> Extra { get; } = new();

    public bool IsEmpty => Title.Length == 0 && Summary.Length == 0 && Date is null && Cover is null && Body.Trim().Length == 0 && Extra.Count == 0;
}

/// <summary>A photo placed in a write-up: ![Caption](path "title").</summary>
public sealed record WriteUpImage(int Index, int Length, string Caption, string Path, string? Title);

/// <summary>Reads and writes "folder.md", the write-up that lives in each folder next to its photos.</summary>
public static partial class WriteUp
{
    public const string FileName = "folder.md";

    // ![caption](path) or ![caption](<path with spaces>), optionally with a "title".
    [GeneratedRegex(@"!\[(?<caption>(?:[^\]\\]|\\.)*)\]\((?:<(?<path>[^>\n]*)>|(?<path>[^)\s]+))(?:\s+""(?<title>[^""\n]*)"")?\)")]
    private static partial Regex ImagePattern();

    public static WriteUpData Parse(string text)
    {
        var data = new WriteUpData();
        var mapping = Sidecar.SplitFrontMatter(text, out var body);
        data.Body = body;
        if (mapping is null) return data;

        foreach (var pair in mapping.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value;
            var value = Sidecar.ScalarText(pair.Value);
            switch (key)
            {
                case "title": data.Title = value; break;
                case "summary": data.Summary = value; break;
                case "date": data.Date = value.Length == 0 ? null : value; break;
                case "cover": data.Cover = value.Length == 0 ? null : value; break;
                default: data.Extra.Add(pair); break;
            }
        }
        return data;
    }

    /// <summary>Front matter in a fixed order (title, summary, date, cover, then hand-added keys), then the story.</summary>
    public static string Serialize(WriteUpData data)
    {
        var mapping = new YamlMappingNode();
        if (data.Title.Length > 0) mapping.Add("title", Sidecar.Scalar(data.Title));
        if (data.Summary.Length > 0) mapping.Add("summary", Sidecar.Scalar(data.Summary));
        if (data.Date is { Length: > 0 }) mapping.Add("date", Sidecar.Scalar(data.Date));
        if (data.Cover is { Length: > 0 }) mapping.Add("cover", Sidecar.Scalar(data.Cover));
        foreach (var pair in data.Extra) mapping.Add(pair.Key, pair.Value);
        return Sidecar.Compose(mapping, data.Body);
    }

    /// <summary>The folder's write-up, or null when it has none. Throws <see cref="FormatException"/> when it can't be read.</summary>
    public static WriteUpData? Read(string folderPath)
    {
        var file = Path.Combine(folderPath, FileName);
        return File.Exists(file) ? Parse(File.ReadAllText(file, Encoding.UTF8)) : null;
    }

    public static IReadOnlyList<WriteUpImage> Images(string body) =>
        ImagePattern().Matches(body).Select(m => new WriteUpImage(
            m.Index, m.Length,
            Unescape(m.Groups["caption"].Value),
            DecodePath(m.Groups["path"].Value),
            m.Groups["title"].Success ? m.Groups["title"].Value : null)).ToList();

    /// <summary>
    /// Rewrites every photo in <paramref name="body"/>: <paramref name="replace"/> returns the new path, or null to drop the
    /// photo (with the line it was on, if nothing else was on it).
    /// </summary>
    public static string RewriteImages(string body, Func<WriteUpImage, string?> replace)
    {
        var images = Images(body);
        var sb = new StringBuilder(body);
        foreach (var image in images.Reverse())
        {
            var path = replace(image);
            if (path is null)
            {
                var (start, length) = (image.Index, image.Length);
                var lineStart = body.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
                if (start == 0) lineStart = 0;
                var lineEnd = body.IndexOf('\n', start + length);
                if (lineEnd < 0) lineEnd = body.Length;
                if (body[lineStart..start].Trim().Length == 0 && body[(start + length)..lineEnd].Trim().Length == 0)
                {
                    (start, length) = (lineStart, Math.Min(body.Length, lineEnd + 1) - lineStart);
                }
                sb.Remove(start, length);
            }
            else
            {
                sb.Remove(image.Index, image.Length).Insert(image.Index, Markdown(image.Caption, path, image.Title));
            }
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim('\n');
    }

    /// <summary>The markdown for a photo: ![Caption](path), with &lt;angle brackets&gt; around paths that need them.</summary>
    public static string Markdown(string caption, string path, string? title = null)
    {
        var escapedCaption = caption.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("\n", " ");
        var target = path.IndexOfAny(new[] { ' ', '(', ')', '<', '>' }) >= 0 ? $"<{path}>" : path;
        var titlePart = title is null ? "" : $" \"{title}\"";
        return $"![{escapedCaption}]({target}{titlePart})";
    }

    /// <summary>A photo path from a write-up, relative to the folder, as a root-relative photo id ("Field Work/IMG_1.jpg").</summary>
    public static string? ResolvePhoto(string folder, string path)
    {
        var parts = new List<string>(folder.Length == 0 ? Array.Empty<string>() : folder.Split('/'));
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    static string Unescape(string caption) => Regex.Replace(caption, @"\\(.)", "$1");

    static string DecodePath(string path)
    {
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }
}
