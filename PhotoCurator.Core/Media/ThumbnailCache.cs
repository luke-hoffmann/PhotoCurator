using System.Security.Cryptography;
using System.Text;
using ImageMagick;

namespace PhotoCurator.Core.Media;

/// <summary>
/// Grid thumbnails and full-window previews (for formats WebView2 can't show, like HEIC and TIFF), cached on disk in
/// the app's data folder. Keyed by path, size and modified time, so an edited photo gets a fresh thumbnail.
/// </summary>
public sealed class ThumbnailCache
{
    public const int ThumbnailEdge = 512;
    public const int PreviewEdge = 2560;
    const long DefaultBudget = 2L * 1024 * 1024 * 1024;

    readonly string _dir;
    readonly SemaphoreSlim _workers = new(Math.Max(1, Environment.ProcessorCount - 1));

    public ThumbnailCache(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public Task<string?> GetThumbnailAsync(string photoPath, CancellationToken cancel) => GetAsync(photoPath, "thumbs", ThumbnailEdge, 78, true, cancel);

    public Task<string?> GetPreviewAsync(string photoPath, CancellationToken cancel) => GetAsync(photoPath, "previews", PreviewEdge, 85, false, cancel);

    async Task<string?> GetAsync(string photoPath, string kind, int edge, int quality, bool fast, CancellationToken cancel)
    {
        var info = new FileInfo(photoPath);
        if (!info.Exists) return null;
        var key = Key(info, edge);
        var target = Path.Combine(_dir, kind, key[..2], key + ".jpg");
        if (File.Exists(target)) return target;

        // Waiting here (not rendering) is what gets cancelled when a tile scrolls away before its turn.
        await _workers.WaitAsync(cancel);
        try
        {
            if (File.Exists(target)) return target;
            return await Task.Run(() =>
            {
                try
                {
                    ImagePipeline.WriteJpeg(photoPath, target, edge, quality, fast);
                    return target;
                }
                catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }, CancellationToken.None);
        }
        finally
        {
            _workers.Release();
        }
    }

    /// <summary>Deletes the least recently written cache files once the cache grows past its budget.</summary>
    public void Trim(long budget = DefaultBudget)
    {
        try
        {
            var files = new DirectoryInfo(_dir).EnumerateFiles("*.jpg", SearchOption.AllDirectories).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= budget) return;
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= budget * 3 / 4) break;
                total -= file.Length;
                try { file.Delete(); } catch (IOException) { /* in use; skip */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache we can't tidy is still a working cache.
        }
    }

    static string Key(FileInfo info, int edge)
    {
        var raw = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{edge}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}
