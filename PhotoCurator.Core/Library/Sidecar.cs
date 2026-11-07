using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PhotoCurator.Core.Library;

/// <summary>What a photo's sidecar (.md) holds. Keys the app doesn't know are kept, in order, so hand edits survive.</summary>
public sealed class SidecarData
{
    public string Caption { get; set; } = "";
    /// <summary>"yyyy-MM-ddTHH:mm:ss" (local wall-clock time, like EXIF) or "yyyy-MM-dd". Null when unknown.</summary>
    public string? Date { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Alt { get; set; } = "";
    /// <summary>Markdown below the front matter.</summary>
    public string Description { get; set; } = "";

    internal List<KeyValuePair<YamlNode, YamlNode>> Extra { get; } = new();

    /// <summary>Unknown keys as plain values, for the API and the exporter.</summary>
    public IEnumerable<string> ExtraKeys => Extra.Select(pair => pair.Key is YamlScalarNode s ? s.Value ?? "" : "");

    public SidecarData Clone()
    {
        var copy = new SidecarData
        {
            Caption = Caption,
            Date = Date,
            Tags = new List<string>(Tags),
            Alt = Alt,
            Description = Description
        };
        copy.Extra.AddRange(Extra);
        return copy;
    }

    public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.Ordinal);
}

/// <summary>Reads and writes sidecar files: YAML front matter between "---" lines, then optional markdown.</summary>
public static class Sidecar
{
    static readonly string[] DateFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"
    };

    public static SidecarData Parse(string text)
    {
        var data = new SidecarData();
        var mapping = SplitFrontMatter(text, out var body);
        data.Description = body;
        if (mapping is null) return data;

        foreach (var pair in mapping.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value;
            switch (key)
            {
                case "caption":
                    data.Caption = ScalarText(pair.Value);
                    break;
                case "date":
                    var date = ScalarText(pair.Value);
                    data.Date = date.Length == 0 ? null : date;
                    break;
                case "tags":
                    data.Tags = ReadTags(pair.Value);
                    break;
                case "alt":
                    data.Alt = ScalarText(pair.Value);
                    break;
                default:
                    data.Extra.Add(pair);
                    break;
            }
        }
        return data;
    }

    /// <summary>Front matter in a fixed key order (caption, date, tags, alt, then hand-added keys), then the description.</summary>
    public static string Serialize(SidecarData data) => Serialize(data, image: null);

    /// <summary>As <see cref="Serialize(SidecarData)"/>, with an "image:" key first (the exported web copy).</summary>
    public static string Serialize(SidecarData data, string? image)
    {
        var mapping = new YamlMappingNode();
        if (image is not null) mapping.Add("image", Scalar(image));
        if (data.Caption.Length > 0) mapping.Add("caption", Scalar(data.Caption));
        if (data.Date is { Length: > 0 }) mapping.Add("date", Scalar(data.Date));
        if (data.Tags.Count > 0)
        {
            var tags = new YamlSequenceNode { Style = YamlDotNet.Core.Events.SequenceStyle.Flow };
            foreach (var tag in data.Tags) tags.Add(Scalar(tag));
            mapping.Add("tags", tags);
        }
        if (data.Alt.Length > 0) mapping.Add("alt", Scalar(data.Alt));
        foreach (var pair in data.Extra) mapping.Add(pair.Key, pair.Value);
        return Compose(mapping, data.Description);
    }

    /// <summary>A front-matter-only document from plain key/value pairs, e.g. an exported folder's "title:".</summary>
    public static string SerializeFields(params (string Key, string Value)[] fields)
    {
        var mapping = new YamlMappingNode();
        foreach (var (key, value) in fields) mapping.Add(key, Scalar(value));
        return "---\n" + EmitYaml(mapping) + "---\n";
    }

    /// <summary>Null when the photo has no sidecar yet. Throws <see cref="FormatException"/> when the file can't be parsed.</summary>
    public static SidecarData? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Writes atomically (temp file, then replace) so a crash never leaves half a sidecar.</summary>
    public static void Write(string path, SidecarData data) => WriteText(path, Serialize(data));

    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Accepts what a date input sends and returns the stored form, or throws a readable error.</summary>
    public static string? NormalizeDate(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        if (!DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new CuratorException("Dates look like 2025-02-14 or 2025-02-14T10:31.");
        }
        return trimmed.Length == 10 ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : FormatDate(parsed);
    }

    public static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateTime? ParseDate(string? value) =>
        value is not null && DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Splits "---" front matter from the markdown after it. Returns null (and the whole text as the body) when the file
    /// has no front matter; throws <see cref="FormatException"/> when it has broken front matter.
    /// </summary>
    internal static YamlMappingNode? SplitFrontMatter(string text, out string body)
    {
        text = text.Replace("\r\n", "\n").TrimStart('\uFEFF');
        if (!text.StartsWith("---\n", StringComparison.Ordinal) && text != "---")
        {
            body = text.Trim('\n');
            return null;
        }

        var end = FindClosingFence(text, out var bodyStart);
        if (end < 0) throw new FormatException("The front matter has no closing \"---\" line.");
        var yaml = text[4..end];
        body = bodyStart >= text.Length ? "" : text[bodyStart..].Trim('\n');

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new FormatException($"The front matter isn't valid YAML: {e.Message}", e);
        }
        if (stream.Documents.Count == 0) return null;
        if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            if (stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" }) return null;
            throw new FormatException("The front matter must be a list of \"key: value\" lines.");
        }
        return mapping;
    }

    /// <summary>"---", the front matter, "---", then the markdown body (if any).</summary>
    internal static string Compose(YamlMappingNode mapping, string body)
    {
        var sb = new StringBuilder("---\n");
        if (mapping.Children.Count > 0) sb.Append(EmitYaml(mapping));
        sb.Append("---\n");
        body = body.Replace("\r\n", "\n").Trim('\n');
        if (body.Length > 0) sb.Append('\n').Append(body).Append('\n');
        return sb.ToString();
    }

    static int FindClosingFence(string text, out int bodyStart)
    {
        var pos = 4;
        while (pos <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var line = lineEnd < 0 ? text[pos..] : text[pos..lineEnd];
            if (line.TrimEnd() is "---" or "...")
            {
                bodyStart = lineEnd < 0 ? text.Length : lineEnd + 1;
                return pos;
            }
            if (lineEnd < 0) break;
            pos = lineEnd + 1;
        }
        bodyStart = -1;
        return -1;
    }

    internal static string ScalarText(YamlNode node) => node is YamlScalarNode scalar ? (scalar.Value ?? "").Trim() : "";

    static List<string> ReadTags(YamlNode node)
    {
        IEnumerable<string> raw = node switch
        {
            YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? ""),
            YamlScalarNode scalar => (scalar.Value ?? "").Split(','),
            _ => Array.Empty<string>()
        };
        var tags = new List<string>();
        foreach (var tag in raw)
        {
            var normalized = Names.NormalizeTag(tag);
            if (normalized is not null && !tags.Contains(normalized)) tags.Add(normalized);
        }
        return tags;
    }

    internal static YamlScalarNode Scalar(string value) => new(value);

    static string EmitYaml(YamlMappingNode mapping)
    {
        var writer = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(writer, bestIndent: 2, bestWidth: int.MaxValue);
        new YamlStream(new YamlDocument(mapping)).Save(emitter, assignAnchors: false);
        // The emitter writes an explicit document end ("...") that front matter doesn't want.
        var lines = writer.ToString().Replace("\r\n", "\n").Split('\n').Where(line => line != "..." && line != "---");
        var yaml = string.Join('\n', lines).TrimEnd('\n');
        return yaml + "\n";
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
