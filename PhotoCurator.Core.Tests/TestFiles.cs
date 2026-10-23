using System.Security.Cryptography;
using ImageMagick;

namespace PhotoCurator.Core.Tests;

/// <summary>A temporary folder for one test, deleted afterwards.</summary>
sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoCurator-test-" + Guid.NewGuid().ToString("N")[..10]);

    public TempFolder() => Directory.CreateDirectory(Path);

    public string this[string rel] => System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string rel, string text)
    {
        var full = this[rel];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public string Photo(string rel, int width = 320, int height = 240, string? taken = null, Orientation orientation = Orientation.TopLeft, bool gps = false)
    {
        var full = this[rel];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        TestImages.Jpeg(full, width, height, taken, orientation, gps);
        return full;
    }

    /// <summary>Every file under the folder (relative path → SHA-256), to prove originals are untouched.</summary>
    public Dictionary<string, string> Fingerprint() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => System.IO.Path.GetRelativePath(Path, f).Replace('\\', '/'),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

static class TestImages
{
    public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>
    /// A small JPEG whose left half is red and right half blue, so tests can tell whether it was rotated.
    /// <paramref name="taken"/> is EXIF "yyyy:MM:dd HH:mm:ss".
    /// </summary>
    public static void Jpeg(string path, int width, int height, string? taken = null, Orientation orientation = Orientation.TopLeft, bool gps = false)
    {
        using var image = new MagickImage(MagickColors.Blue, (uint)width, (uint)height);
        new ImageMagick.Drawing.Drawables()
            .FillColor(MagickColors.Red)
            .Rectangle(0, 0, width / 2.0, height)
            .Draw(image);

        var exif = new ExifProfile();
        // Magick.NET only writes the orientation when both the EXIF value and the image property are set.
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        if (taken is not null) exif.SetValue(ExifTag.DateTimeOriginal, taken);
        if (gps)
        {
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(48, 1), new Rational(56, 1), new Rational(0, 1) });
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(122, 1), new Rational(27, 1), new Rational(0, 1) });
        }
        image.SetProfile(exif);
        image.Orientation = orientation;
        image.Quality = 90;
        image.Write(path, MagickFormat.Jpeg);
    }
}
