using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;

namespace PhotoCurator.Core.Media;

/// <summary>
/// When a photo was taken, from its own EXIF: "2025-02-14T10:31:00" plus sub-seconds when the camera recorded them,
/// and its displayed aspect ratio. Two different files with the same moment are almost always the same shot (an edit,
/// a recompressed copy) or a burst frame from the same second.
/// </summary>
public sealed record Moment(string Taken, double Aspect)
{
    /// <summary>Same instant, and the same shape within 2% (a recompressed or downsized copy; not a crop).</summary>
    public bool Matches(Moment other) => Taken == other.Taken && Math.Abs(Aspect - other.Aspect) <= 0.02 * Math.Max(Aspect, other.Aspect);

    public override string ToString() => $"{Taken}|{Aspect.ToString("0.0000", CultureInfo.InvariantCulture)}";

    public static Moment? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var bar = text.LastIndexOf('|');
        return bar > 0 && double.TryParse(text[(bar + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var aspect)
            ? new Moment(text[..bar], aspect)
            : null;
    }

    /// <summary>Reads the moment from a file, without decoding pixels. Null when it has no EXIF capture time.</summary>
    public static Moment? Read(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            return From(image);
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>As <see cref="Read(string)"/>, for a zip entry or other stream.</summary>
    public static Moment? Read(Stream stream)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(stream);
            return From(image);
        }
        catch (Exception e) when (e is MagickException or IOException)
        {
            return null;
        }
    }

    static Moment? From(MagickImage image)
    {
        var exif = image.GetExifProfile();
        var taken = exif?.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif?.GetValue(ExifTag.DateTimeDigitized)?.Value;
        if (taken is null || !DateTime.TryParseExact(taken.Trim().TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }
        var subsec = (exif!.GetValue(ExifTag.SubsecTimeOriginal)?.Value ?? "").Trim().TrimEnd('\0');
        var stamp = date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (subsec.Length > 0 ? "." + subsec : "");

        double w = image.Width, h = image.Height;
        if (image.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom) (w, h) = (h, w);
        return h > 0 ? new Moment(stamp, w / h) : null;
    }
}

/// <summary>
/// What identifies each photo in the open folder regardless of where it is or what it's called: its bytes (SHA-256,
/// computed only when another photo has the same size) and its <see cref="Moment"/>. Cached in the app's data folder,
/// keyed by name, size and modified time, which a move into another folder keeps.
/// Also remembers groups the person said aren't duplicates.
/// </summary>
public sealed class Fingerprints
{
    sealed class Entry
    {
        public string? Sha256 { get; set; }
        public bool MomentRead { get; set; }
        public string? Moment { get; set; }
    }

    sealed class Saved
    {
        public Dictionary<string, Entry> Entries { get; set; } = new();
        public List<List<string>> Dismissed { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly List<HashSet<string>> _dismissed = new();
    readonly string? _file;
    readonly object _saveLock = new();
    readonly object _runLock = new();
    CancellationTokenSource? _run;
    int _pending;

    /// <summary>Raised now and then while moments are being read, and once when done.</summary>
    public event Action? Updated;

    /// <summary>Photos whose moment hasn't been read yet.</summary>
    public int Pending => Volatile.Read(ref _pending);

    public Fingerprints(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), JsonOptions);
            if (saved is null) return;
            foreach (var (key, entry) in saved.Entries) _entries[key] = entry;
            foreach (var group in saved.Dismissed) _dismissed.Add(new HashSet<string>(group, StringComparer.Ordinal));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Only a cache: start over.
        }
    }

    public static string Key(string name, long size, long modified) => $"{name}|{size}|{modified}";

    /// <summary>The file's SHA-256 (hex), from the cache or computed now.</summary>
    public string Sha256(string fullPath, string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        if (entry.Sha256 is { } known) return known;
        using var stream = File.OpenRead(fullPath);
        entry.Sha256 = Hash(stream);
        return entry.Sha256;
    }

    public static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    /// <summary>The photo's moment, from the cache or read now. Null when it has no EXIF capture time.</summary>
    public Moment? MomentOf(string fullPath, string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        if (!entry.MomentRead)
        {
            entry.Moment = Moment.Read(fullPath)?.ToString();
            entry.MomentRead = true;
        }
        return Moment.Parse(entry.Moment);
    }

    /// <summary>The cached moment, without reading the file. Null when unknown or not read yet.</summary>
    public Moment? CachedMoment(string key) => _entries.TryGetValue(key, out var entry) ? Moment.Parse(entry.Moment) : null;

    public bool HasMoment(string key) => _entries.TryGetValue(key, out var entry) && entry.MomentRead;

    /// <summary>Records what an import already worked out about a file, so it isn't read again.</summary>
    public void Seed(string key, string? sha256, Moment? moment)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        if (sha256 is not null) entry.Sha256 = sha256;
        entry.Moment = moment?.ToString();
        entry.MomentRead = true;
    }

    /// <summary>Reads missing moments in the background (one EXIF ping per photo), then saves the cache.</summary>
    public void Fill(IReadOnlyList<(string FullPath, string Key)> photos)
    {
        var missing = photos.Where(p => !HasMoment(p.Key)).ToList();
        Volatile.Write(ref _pending, missing.Count);
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
            foreach (var photo in missing)
            {
                if (cancel.IsCancellationRequested) return;
                MomentOf(photo.FullPath, photo.Key);
                Interlocked.Decrement(ref _pending);
                if (Environment.TickCount64 - lastNotice > 2000)
                {
                    lastNotice = Environment.TickCount64;
                    Updated?.Invoke();
                }
            }
            Save();
            Updated?.Invoke();
        }, cancel);
    }

    // ---------- "not duplicates" ----------

    /// <summary>True when every one of these photos is in one group the person dismissed.</summary>
    public bool IsDismissed(IReadOnlyCollection<string> keys)
    {
        lock (_dismissed) return _dismissed.Any(group => keys.All(group.Contains));
    }

    public void Dismiss(IEnumerable<string> keys)
    {
        lock (_dismissed) _dismissed.Add(new HashSet<string>(keys, StringComparer.Ordinal));
        Save();
    }

    public void Save()
    {
        if (_file is null) return;
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                Saved saved;
                lock (_dismissed) saved = new Saved { Entries = new(_entries), Dismissed = _dismissed.Select(g => g.ToList()).ToList() };
                var temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(saved, JsonOptions));
                File.Move(temp, _file, overwrite: true);
            }
            catch (IOException)
            {
                // Only a cache.
            }
        }
    }
}
