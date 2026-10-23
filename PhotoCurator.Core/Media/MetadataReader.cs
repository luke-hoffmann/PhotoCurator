using System.Globalization;
using System.Text.Json;
using ImageMagick;

namespace PhotoCurator.Core.Media;

public enum DateSource { Sidecar, Camera, Takeout, File }

/// <summary>Reads when a photo was taken and how big it is, without decoding pixels.</summary>
public static class MetadataReader
{
    static readonly ExifTag<string>[] DateTags = { ExifTag.DateTimeOriginal, ExifTag.DateTimeDigitized, ExifTag.DateTime };

    /// <summary>Camera EXIF first, then a Google Takeout .json next to the photo, then the file's modified time.</summary>
    public static (DateTime Date, DateSource Source) ReadTakenDate(string path)
    {
        if (ReadExifDate(path) is { } exif) return (exif, DateSource.Camera);
        if (ReadTakeoutDate(path) is { } takeout) return (takeout, DateSource.Takeout);
        return (File.GetLastWriteTime(path), DateSource.File);
    }

    public static DateTime? ReadExifDate(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            var exif = image.GetExifProfile();
            if (exif is null) return null;
            foreach (var tag in DateTags)
            {
                var value = exif.GetValue(tag)?.Value?.Trim().TrimEnd('\0');
                if (value is not null && DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date;
                }
            }
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            // Unreadable or exotic file: fall through to the other sources.
        }
        return null;
    }

    /// <summary>Takeout stores "photoTakenTime": { "timestamp": "1707906660" } in UTC seconds; returned as local time.</summary>
    public static DateTime? ReadTakeoutDate(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        IEnumerable<string> siblings;
        try { siblings = Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileName).OfType<string>().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        foreach (var json in PhotoFiles.TakeoutCompanions(Path.GetFileName(path), siblings))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, json)));
                if (doc.RootElement.TryGetProperty("photoTakenTime", out var taken) &&
                    taken.TryGetProperty("timestamp", out var stamp) &&
                    long.TryParse(stamp.ValueKind == JsonValueKind.String ? stamp.GetString() : stamp.GetRawText(), out var seconds) &&
                    seconds > 0)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Not a Takeout file after all.
            }
        }
        return null;
    }

    /// <summary>Displayed size (rotated per EXIF orientation), or null if the file can't be read.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            var (w, h) = ((int)image.Width, (int)image.Height);
            return image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom
                ? (h, w)
                : (w, h);
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
