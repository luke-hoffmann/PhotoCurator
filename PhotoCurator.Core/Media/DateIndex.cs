using System.Collections.Concurrent;
using System.Text.Json;

namespace PhotoCurator.Core.Media;

/// <summary>
/// Taken dates for photos that have no sidecar date yet, read in the background (EXIF, then Takeout) so the grid can
/// sort by date. Results are cached on disk by path, size and modified time.
/// </summary>
public sealed class DateIndex
{
    readonly ConcurrentDictionary<string, string> _dates = new();
    readonly string? _cacheFile;
    readonly object _runLock = new();
    CancellationTokenSource? _run;

    /// <summary>Raised now and then while filling, and once when done.</summary>
    public event Action? Updated;

    public DateIndex(string? cacheFile)
    {
        _cacheFile = cacheFile;
        if (cacheFile is null || !File.Exists(cacheFile)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cacheFile));
            if (saved is not null) foreach (var (key, value) in saved) _dates[key] = value;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Start over with an empty cache.
        }
    }

    /// <summary>"yyyy-MM-ddTHH:mm:ss", or null when unknown or not read yet.</summary>
    public string? Get(string fullPath, long size, long modified) =>
        _dates.TryGetValue(Key(fullPath, size, modified), out var date) && date.Length > 0 ? date : null;

    public void Fill(IReadOnlyList<(string FullPath, long Size, long Modified)> photos)
    {
        var missing = photos.Where(p => !_dates.ContainsKey(Key(p.FullPath, p.Size, p.Modified))).ToList();
        if (missing.Count == 0) return;

        CancellationToken cancel;
        lock (_runLock)
        {
            _run?.Cancel();
            _run = new CancellationTokenSource();
            cancel = _run.Token;
        }

        _ = Task.Run(() =>
        {
            var lastNotice = Environment.TickCount64;
            var jsonByDir = new Dictionary<string, bool>(PathGuard.Comparer);
            foreach (var photo in missing)
            {
                if (cancel.IsCancellationRequested) return;
                var dir = Path.GetDirectoryName(photo.FullPath)!;
                if (!jsonByDir.TryGetValue(dir, out var hasJson))
                {
                    try { hasJson = Directory.EnumerateFiles(dir, "*.json").Any(); } catch (IOException) { hasJson = false; }
                    jsonByDir[dir] = hasJson;
                }

                var date = MetadataReader.ReadExifDate(photo.FullPath) ?? (hasJson ? MetadataReader.ReadTakeoutDate(photo.FullPath) : null);
                _dates[Key(photo.FullPath, photo.Size, photo.Modified)] = date is null ? "" : Library.Sidecar.FormatDate(date.Value);

                if (Environment.TickCount64 - lastNotice > 1500)
                {
                    lastNotice = Environment.TickCount64;
                    Updated?.Invoke();
                }
            }
            Save();
            Updated?.Invoke();
        }, cancel);
    }

    void Save()
    {
        if (_cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_dates));
            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch (IOException)
        {
            // Only a cache.
        }
    }

    static string Key(string fullPath, long size, long modified) => $"{fullPath}|{size}|{modified}";
}
