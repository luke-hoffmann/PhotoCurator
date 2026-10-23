using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageMagick;
using PhotoCurator.Core.Server;

namespace PhotoCurator.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    readonly TempFolder _photos = new();
    readonly TempFolder _data = new();
    CuratorServer _server = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _photos.Photo("dl/IMG_1.jpg", 800, 600, "2024:06:12 09:00:00");
        _photos.Photo("dl/IMG_2.jpg", 800, 600);
        _photos.Write("dl/notes.txt", "not a photo");
        _server = new CuratorServer(_data.Path, token: "test-token");
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
        _http.DefaultRequestHeaders.Add("X-Curator-Token", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _photos.Dispose();
        _data.Dispose();
    }

    async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    async Task OpenAcknowledged() =>
        await Json(await _http.PostAsJsonAsync("/api/open", new { path = _photos.Path, acknowledge = true }));

    [Fact]
    public async Task RejectsMissingTokenAndForeignHosts()
    {
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state?t=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/state?t=test-token")).StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        rebinding.Headers.Host = $"evil.example:{_server.Port}";
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(rebinding)).StatusCode);
    }

    [Fact]
    public async Task FirstOpenAsksAboutACopyUnlessToldNotTo()
    {
        var first = await Json(await _http.PostAsJsonAsync("/api/open", new { path = _photos.Path }));
        Assert.True(first.GetProperty("needsAcknowledge").GetBoolean());
        Assert.Null(_server.Library);

        await Json(await _http.PostAsJsonAsync("/api/open", new { path = _photos.Path, acknowledge = true, dontAskAgain = true }));
        Assert.NotNull(_server.Library);

        var again = await Json(await _http.PostAsJsonAsync("/api/open", new { path = _photos.Path }));
        Assert.Equal(_photos.Path, again.GetProperty("folder").GetProperty("path").GetString());
        Assert.Equal(_photos.Path, again.GetProperty("recent")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task OpeningSomethingThatIsNotAFolderIsAReadableError()
    {
        var error = await Json(await _http.PostAsJsonAsync("/api/open", new { path = _photos["nope"] }), HttpStatusCode.NotFound);
        Assert.Equal("That folder doesn't exist.", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TagMoveUndoRoundTrip()
    {
        await OpenAcknowledged();
        var library = await Json(await _http.GetAsync("/api/library"));
        Assert.Equal(2, library.GetProperty("photos").GetArrayLength());

        var tagged = await Json(await _http.PostAsJsonAsync("/api/tags", new { ids = new[] { "dl/IMG_1.jpg", "dl/IMG_2.jpg" }, add = new[] { "Hammer" } }));
        Assert.Equal("Tagged 2 photos \"hammer\"", tagged.GetProperty("message").GetString());

        await Json(await _http.PostAsJsonAsync("/api/folders", new { parent = "", name = "Field Work" }));
        var moved = await Json(await _http.PostAsJsonAsync("/api/move", new { ids = new[] { "dl/IMG_1.jpg" }, folder = "Field Work" }));
        Assert.Equal("Field Work/IMG_1.jpg", moved.GetProperty("moved").GetProperty("dl/IMG_1.jpg").GetString());
        Assert.StartsWith("Move 1 photo", moved.GetProperty("undo").GetString());

        var detail = await Json(await _http.GetAsync("/api/photo?id=" + Uri.EscapeDataString("Field Work/IMG_1.jpg")));
        Assert.Equal("2024-06-12T09:00:00", detail.GetProperty("date").GetString());
        Assert.Equal("hammer", detail.GetProperty("tags")[0].GetString());
        Assert.Equal(800, detail.GetProperty("width").GetInt32());

        await Json(await _http.PutAsJsonAsync("/api/photo", new { id = "Field Work/IMG_1.jpg", field = "caption", caption = "Troweling the slab" }));
        Assert.Contains("caption: Troweling the slab", File.ReadAllText(_photos["Field Work/IMG_1.jpg.md"]));

        await Json(await _http.PostAsync("/api/undo", null));
        await Json(await _http.PostAsync("/api/undo", null));
        Assert.True(File.Exists(_photos["dl/IMG_1.jpg"]));
    }

    [Fact]
    public async Task PathsOutsideTheFolderAreRefused()
    {
        await OpenAcknowledged();
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/thumb?id=../../etc/passwd")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/move", new { ids = new[] { "dl/IMG_1.jpg" }, folder = "../.." })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/image?id=dl/notes.txt")).StatusCode);
        Assert.True(File.Exists(_photos["dl/IMG_1.jpg"]));
    }

    [Fact]
    public async Task ThumbnailsArePhotosAndCacheable()
    {
        await OpenAcknowledged();
        var response = await _http.GetAsync("/api/thumb?id=dl/IMG_1.jpg&v=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        using var thumb = new MagickImage(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(512u, thumb.Width);
    }

    [Fact]
    public async Task ExportRunsAsAJobAndReportsBack()
    {
        await OpenAcknowledged();
        using var site = new TempFolder();
        var events = await _http.GetStreamAsync("/api/events");
        using var reader = new StreamReader(events);

        await Json(await _http.PostAsJsonAsync("/api/export", new { folder = "dl", destination = site["field"] }));

        string? line;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((line = await reader.ReadLineAsync()) is not null && DateTime.UtcNow < deadline)
        {
            if (line.StartsWith("data:") && line.Contains("\"state\":\"done\"")) break;
        }
        Assert.NotNull(line);
        Assert.True(File.Exists(site["field/img-1.jpg"]));
        var remembered = await Json(await _http.GetAsync("/api/export-destination?folder=dl"));
        Assert.Equal(site["field"], remembered.GetProperty("destination").GetString());
    }

    [Fact]
    public async Task ServesTheApp()
    {
        var page = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/../PhotoCurator.Core.dll")).StatusCode);
    }
}
