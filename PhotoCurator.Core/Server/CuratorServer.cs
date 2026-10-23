using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoCurator.Core.Library;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Server;

/// <summary>What the desktop window provides to the web UI: native dialogs and Explorer.</summary>
public interface IDesktopHost
{
    /// <summary>The OS folder picker. Null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? initialPath);

    /// <summary>The OS file picker for .zip files (several allowed). Empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickZipFilesAsync(string title);

    /// <summary>Opens Explorer with the file selected.</summary>
    void Reveal(string path);
}

/// <summary>
/// The local web server behind the app window. Listens on 127.0.0.1 only, on a random port; every API call must carry
/// the per-launch token and a 127.0.0.1/localhost Host header, so neither other machines nor web pages can use it.
/// </summary>
public sealed partial class CuratorServer : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly string _dataDir;
    readonly IDesktopHost? _host;
    readonly AppSettings _settings;
    readonly ThumbnailCache _thumbs;
    readonly EventHub _events = new();
    readonly JobRunner _jobs;
    readonly object _sync = new();
    readonly byte[] _tokenBytes;
    readonly Dictionary<string, ImportPlan> _plans = new();
    PhotoLibrary? _library;
    DateIndex? _dates;
    Fingerprints? _prints;
    IReadOnlyList<DuplicateGroup> _duplicates = Array.Empty<DuplicateGroup>();
    Timer? _duplicateTimer;
    WebApplication? _app;

    public string Token { get; }
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";
    public PhotoLibrary? Library => Volatile.Read(ref _library);

    public CuratorServer(string dataDir, IDesktopHost? host = null, string? token = null)
    {
        _dataDir = dataDir;
        _host = host;
        Directory.CreateDirectory(dataDir);
        _settings = AppSettings.Load(dataDir);
        _thumbs = new ThumbnailCache(Path.Combine(dataDir, "cache"));
        _jobs = new JobRunner(_events);
        Token = token ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        _tokenBytes = Encoding.UTF8.GetBytes(Token);
        _ = Task.Run(() => _thumbs.Trim());
    }

    /// <summary>The default data folder: %LOCALAPPDATA%\PhotoCurator on Windows.</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PhotoCurator");

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));

        _app = builder.Build();
        _app.Run(HandleRequestAsync);
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        _duplicateTimer?.Dispose();
        await StopAsync();
        Interlocked.Exchange(ref _library, null)?.Dispose();
    }

    /// <summary>Opens a folder without the copy warning (used by the dev host and tests, and after a confirmed open).</summary>
    public void OpenFolder(string path)
    {
        var library = new PhotoLibrary(path);
        DateIndex dates;
        Fingerprints prints;
        lock (_sync)
        {
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(library.Root.ToLowerInvariant())))[..16];
            dates = new DateIndex(Path.Combine(_dataDir, "dates", key + ".json"));
            prints = new Fingerprints(Path.Combine(_dataDir, "fingerprints", key + ".json"));
            var old = Interlocked.Exchange(ref _library, library);
            _dates = dates;
            _prints = prints;
            _duplicates = Array.Empty<DuplicateGroup>();
            _plans.Clear();
            old?.Dispose();
            _settings.AddRecent(library.Root);
            _settings.Save();
        }

        library.Changed += () =>
        {
            if (!ReferenceEquals(Library, library)) return;
            FillDates(library, dates);
            FillPrints(library, prints);
            _events.Publish("library", new { version = library.Version });
        };
        dates.Updated += () =>
        {
            if (ReferenceEquals(Library, library)) _events.Publish("library", new { version = library.Version, dates = true });
        };
        prints.Updated += () =>
        {
            if (ReferenceEquals(Library, library)) ScheduleDuplicates();
        };
        FillDates(library, dates);
        FillPrints(library, prints);
        _events.Publish("library", new { version = library.Version });
    }

    void FillPrints(PhotoLibrary library, Fingerprints prints)
    {
        prints.Fill(library.Current.Photos.Select(p => (library.FullPath(p.Id), Fingerprints.Key(p.Name, p.Size, p.Modified))).ToList());
        ScheduleDuplicates();
    }

    /// <summary>Re-finds duplicates a moment after the folder settles, and tells the window when the result changed.</summary>
    void ScheduleDuplicates()
    {
        lock (_sync)
        {
            _duplicateTimer ??= new Timer(_ => FindDuplicates(notify: true));
            _duplicateTimer.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }

    IReadOnlyList<DuplicateGroup> FindDuplicates(bool notify)
    {
        var library = Library;
        var prints = _prints;
        if (library is null || prints is null) return Array.Empty<DuplicateGroup>();
        IReadOnlyList<DuplicateGroup> groups;
        try { groups = DuplicateFinder.Find(library, prints); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return _duplicates; }

        var before = _duplicates;
        _duplicates = groups;
        var changed = before.Count != groups.Count || !before.Zip(groups).All(pair => pair.First.Ids.SequenceEqual(pair.Second.Ids));
        if (notify && changed && ReferenceEquals(Library, library)) _events.Publish("library", new { version = library.Version, duplicates = groups.Count });
        return groups;
    }

    static void FillDates(PhotoLibrary library, DateIndex dates) =>
        dates.Fill(library.Current.Photos
            .Where(p => p.Sidecar?.Date is null)
            .Select(p => (library.FullPath(p.Id), p.Size, p.Modified))
            .ToList());

    async Task HandleRequestAsync(HttpContext context)
    {
        var req = context.Request;
        var res = context.Response;
        SetSecurityHeaders(res);

        try
        {
            // DNS-rebinding guard: only requests addressed to this exact loopback origin.
            var host = req.Host.Value ?? "";
            if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
            {
                await JsonAsync(res, 400, new { error = "Bad host." });
                return;
            }

            var path = req.Path.Value ?? "/";
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await ServeAssetAsync(context, path);
                return;
            }

            if (!HasToken(req))
            {
                await JsonAsync(res, 401, new { error = "Missing or wrong token." });
                return;
            }

            await HandleApiAsync(context, req.Method, path);
        }
        catch (CuratorException e)
        {
            await JsonAsync(res, e.StatusCode, new { error = e.Message });
        }
        catch (JsonException)
        {
            await JsonAsync(res, 400, new { error = "That request couldn't be read." });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The window went away mid-request.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await JsonAsync(res, 409, new { error = e.Message });
        }
    }

    bool HasToken(HttpRequest req)
    {
        string? given = req.Headers["X-Curator-Token"].FirstOrDefault() ?? req.Query["t"].FirstOrDefault();
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _tokenBytes);
    }

    static async Task ServeAssetAsync(HttpContext context, string path)
    {
        var name = path == "/" ? "index.html" : path.TrimStart('/');
        if (context.Request.Method != "GET" || !StaticAssets.TryGet(name, out var bytes, out var contentType, out var etag))
        {
            await JsonAsync(context.Response, 404, new { error = "Not found." });
            return;
        }
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            context.Response.StatusCode = 304;
            return;
        }
        context.Response.ContentType = contentType;
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    static void SetSecurityHeaders(HttpResponse res)
    {
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    }

    internal static async Task JsonAsync(HttpResponse res, int status, object payload)
    {
        if (res.HasStarted) return;
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(res.Body, payload, payload.GetType(), JsonOptions);
    }
}
