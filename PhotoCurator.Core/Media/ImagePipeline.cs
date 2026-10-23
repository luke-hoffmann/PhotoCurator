using ImageMagick;

namespace PhotoCurator.Core.Media;

/// <summary>Decodes any supported photo (HEIC included) and writes a resized, upright, metadata-free sRGB JPEG.</summary>
public static class ImagePipeline
{
    /// <summary>
    /// Writes <paramref name="source"/> as a JPEG no larger than <paramref name="maxEdge"/> on its long side (never upscaled),
    /// rotated per EXIF, converted to sRGB and with every metadata profile (EXIF, GPS, XMP, ICC) removed.
    /// The file appears atomically at <paramref name="target"/>.
    /// </summary>
    public static (int Width, int Height) WriteJpeg(string source, string target, int maxEdge, int quality, bool fastThumbnail = false)
    {
        using var image = new MagickImage();
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        if (IsJpeg(source))
        {
            // Lets libjpeg decode at a reduced scale: much faster for thumbnails of 12-48 MP photos.
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxEdge * 2}x{maxEdge * 2}");
        }
        image.Read(source, settings);
        image.AutoOrient();
        ToSrgb(image);

        if (image.Width > maxEdge || image.Height > maxEdge)
        {
            var geometry = new MagickGeometry((uint)maxEdge, (uint)maxEdge) { Greater = true };
            if (fastThumbnail) image.Thumbnail(geometry);
            else image.Resize(geometry);
        }
        image.Strip();
        image.Quality = (uint)quality;
        image.Format = MagickFormat.Jpeg;
        if (!fastThumbnail) image.Settings.Interlace = Interlace.Jpeg;

        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            image.Write(temp, MagickFormat.Jpeg);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
        return ((int)image.Width, (int)image.Height);
    }

    static void ToSrgb(MagickImage image)
    {
        var profile = image.GetColorProfile();
        if (profile is not null)
        {
            // iPhone HEICs are Display P3; converting keeps colors right once the profile is stripped.
            if (profile.ColorSpace != ColorSpace.sRGB || image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
        }
        else if (image.ColorSpace == ColorSpace.CMYK)
        {
            image.ColorSpace = ColorSpace.sRGB;
        }
    }

    static bool IsJpeg(string path) =>
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
