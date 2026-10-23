namespace PhotoCurator.Core;

/// <summary>Which files count as photos, and which files travel with a photo when it moves.</summary>
public static class PhotoFiles
{
    public const string SidecarExtension = ".md";

    /// <summary>Written by Export for web into folders it owns; the scanner never descends into them.</summary>
    public const string ExportMarker = ".photocurator-export";

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".avif", ".gif", ".tif", ".tiff"
    };

    /// <summary>Formats WebView2/Chromium shows directly. Everything else is shown through a JPEG preview.</summary>
    static readonly HashSet<string> BrowserExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif"
    };

    /// <summary>Live Photo motion clips that Google Photos downloads next to the still.</summary>
    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4" };

    public static bool IsPhoto(string fileName) =>
        !fileName.StartsWith('.') && ImageExtensions.Contains(Path.GetExtension(fileName));

    public static bool IsBrowserFriendly(string fileName) => BrowserExtensions.Contains(Path.GetExtension(fileName));

    public static string SidecarName(string photoName) => photoName + SidecarExtension;

    /// <summary>
    /// Google Takeout metadata files for <paramref name="photoName"/> among <paramref name="siblings"/>. Takeout names them
    /// "IMG_1.HEIC.json" or "IMG_1.HEIC.supplemental-metadata.json", truncates long names ("IMG_1.HEIC.supplemental-meta.json")
    /// and moves a duplicate counter to the end ("IMG_1(1).HEIC" → "IMG_1.HEIC(1).json" / "IMG_1.HEIC.supplemental-metadata(1).json").
    /// </summary>
    public static IEnumerable<string> TakeoutCompanions(string photoName, IEnumerable<string> siblings)
    {
        var counter = "";
        var plainName = photoName;
        var stem = Path.GetFileNameWithoutExtension(photoName);
        var open = stem.LastIndexOf('(');
        if (open > 0 && stem.EndsWith(')') && int.TryParse(stem.AsSpan(open + 1, stem.Length - open - 2), out _))
        {
            counter = stem[open..];
            plainName = stem[..open] + Path.GetExtension(photoName);
        }

        foreach (var sibling in siblings)
        {
            if (!sibling.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (Matches(sibling, photoName, "")) yield return sibling;
            else if (counter.Length > 0 && Matches(sibling, plainName, counter)) yield return sibling;
        }
    }

    static bool Matches(string jsonName, string photoName, string counter)
    {
        if (!jsonName.StartsWith(photoName, StringComparison.OrdinalIgnoreCase)) return false;
        // What sits between the photo name and ".json", e.g. ".supplemental-metadata(1)".
        var middle = jsonName.Substring(photoName.Length, jsonName.Length - photoName.Length - ".json".Length);
        if (counter.Length > 0)
        {
            if (!middle.EndsWith(counter, StringComparison.Ordinal)) return false;
            middle = middle[..^counter.Length];
        }
        if (middle.Length == 0) return true;
        if (!middle.StartsWith('.')) return false;
        var suffix = middle[1..];
        return suffix.Length > 0 && "supplemental-metadata".StartsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Live Photo clip with the same base name ("IMG_1.HEIC" + "IMG_1.MOV"). Only returned when no other photo in the
    /// folder shares the base name, so the clip never leaves a still it might belong to.
    /// </summary>
    public static string? VideoCompanion(string photoName, IEnumerable<string> siblings)
    {
        var stem = Path.GetFileNameWithoutExtension(photoName);
        string? video = null;
        foreach (var sibling in siblings)
        {
            if (!Path.GetFileNameWithoutExtension(sibling).Equals(stem, StringComparison.OrdinalIgnoreCase)) continue;
            if (sibling.Equals(photoName, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsPhoto(sibling)) return null;
            if (VideoExtensions.Contains(Path.GetExtension(sibling))) video = sibling;
        }
        return video;
    }
}
