using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhotoCurator.Core.Library;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Server;

public sealed partial class CuratorServer
{
    sealed record OpenRequest(string? Path, bool Acknowledge, bool DontAskAgain);
    sealed record PickRequest(string? Title, string? Initial);
    sealed record CopyRequest(string? Source, string? Destination);
    sealed record PhotoRequest(string? Id, string? Field, string? Caption, string? Date, List<string>? Tags, string? Alt, string? Description);
    sealed record TagsRequest(List<string>? Ids, List<string>? Add, List<string>? Remove);
    sealed record CaptionRequest(List<string>? Ids, string? Caption);
    sealed record MoveRequest(List<string>? Ids, string? Folder);
    sealed record FolderRequest(string? Parent, string? Path, string? Name);
    sealed record ExportRequest(string? Folder, string? Destination);
    sealed record IdRequest(string? Id);
    sealed record SourcesRequest(List<string>? Sources);
    sealed record PlanRequest(string? Id);
    sealed record KeepGroup(string? Keep, List<string>? Others);
    sealed record KeepRequest(List<KeepGroup>? Groups, bool AllExact);
    sealed record IdsRequest(List<string>? Ids);
    sealed record WriteUpRequest(string? Folder, string? Title, string? Summary, string? Date, string? Cover, string? Body);

    static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
        [".avif"] = "image/avif", [".gif"] = "image/gif"
    };

    async Task HandleApiAsync(HttpContext context, string method, string path)
    {
        var res = context.Response;
        switch (method, path)
        {
            case ("GET", "/api/events"):
                await _events.ServeAsync(context);
                return;
            case ("GET", "/api/state"):
                await JsonAsync(res, 200, State());
                return;
            case ("POST", "/api/open"):
                await JsonAsync(res, 200, Open(await BodyAsync<OpenRequest>(context)));
                return;
            case ("POST", "/api/pick-folder"):
                var pick = await BodyAsync<PickRequest>(context);
                if (_host is null) await JsonAsync(res, 200, new { unsupported = true });
                else await JsonAsync(res, 200, new { path = await _host.PickFolderAsync(pick.Title ?? "Choose a folder", pick.Initial) });
                return;
            case ("POST", "/api/pick-files"):
                var pickFiles = await BodyAsync<PickRequest>(context);
                if (_host is null) await JsonAsync(res, 200, new { unsupported = true });
                else await JsonAsync(res, 200, new { paths = await _host.PickZipFilesAsync(pickFiles.Title ?? "Choose zip files") });
                return;
            case ("POST", "/api/import/check"):
                await JsonAsync(res, 200, StartImportCheck(await BodyAsync<SourcesRequest>(context)));
                return;
            case ("POST", "/api/import/run"):
                await JsonAsync(res, 200, StartImport(await BodyAsync<PlanRequest>(context)));
                return;
            case ("GET", "/api/duplicates"):
                await JsonAsync(res, 200, DuplicatesJson(RequireLibrary()));
                return;
            case ("POST", "/api/duplicates/keep"):
                await JsonAsync(res, 200, KeepDuplicates(await BodyAsync<KeepRequest>(context)));
                return;
            case ("POST", "/api/duplicates/dismiss"):
            {
                var r = await BodyAsync<IdsRequest>(context);
                var lib = RequireLibrary();
                var keys = (r.Ids ?? new()).Select(id => lib.Get(id)).Select(p => Fingerprints.Key(p.Name, p.Size, p.Modified)).ToList();
                if (keys.Count < 2) throw new CuratorException("Pick the group to dismiss.");
                _prints!.Dismiss(keys);
                FindDuplicates(notify: false);
                await JsonAsync(res, 200, new { message = "Marked as not duplicates", duplicates = _duplicates.Count });
                return;
            }
            case ("GET", "/api/writeup"):
            {
                var lib = RequireLibrary();
                var folder = context.Request.Query["folder"].FirstOrDefault() ?? "";
                var data = lib.ReadWriteUp(folder);
                await JsonAsync(res, 200, new
                {
                    folder,
                    exists = File.Exists(Path.Combine(lib.FullPath(folder), WriteUp.FileName)),
                    title = data.Title,
                    summary = data.Summary,
                    date = data.Date,
                    cover = data.Cover,
                    body = data.Body
                });
                return;
            }
            case ("PUT", "/api/writeup"):
            {
                var r = await BodyAsync<WriteUpRequest>(context);
                var lib = RequireLibrary();
                var result = lib.SaveWriteUp(r.Folder ?? "", new WriteUpData
                {
                    Title = r.Title ?? "",
                    Summary = r.Summary ?? "",
                    Date = r.Date,
                    Cover = r.Cover,
                    Body = r.Body ?? ""
                });
                await JsonAsync(res, 200, Done(lib, result));
                return;
            }
            case ("POST", "/api/copy"):
                await JsonAsync(res, 200, StartCopy(await BodyAsync<CopyRequest>(context)));
                return;
            case ("POST", "/api/job/cancel"):
                _jobs.Cancel();
                await JsonAsync(res, 200, new { ok = true });
                return;
            case ("GET", "/api/library"):
                await JsonAsync(res, 200, LibraryJson(RequireLibrary()));
                return;
            case ("GET", "/api/photo"):
                await JsonAsync(res, 200, PhotoJson(RequireLibrary(), context.Request.Query["id"].FirstOrDefault()));
                return;
            case ("GET", "/api/thumb"):
                await SendImageAsync(context, preview: false);
                return;
            case ("GET", "/api/image"):
                await SendImageAsync(context, preview: true);
                return;
            case ("PUT", "/api/photo"):
            {
                var r = await BodyAsync<PhotoRequest>(context);
                var lib = RequireLibrary();
                var result = lib.UpdatePhoto(r.Id ?? "", new PhotoUpdate(r.Caption, r.Date, r.Tags, r.Alt, r.Description), r.Field);
                await JsonAsync(res, 200, Done(lib, result));
                return;
            }
            case ("POST", "/api/tags"):
            {
                var r = await BodyAsync<TagsRequest>(context);
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.ChangeTags(r.Ids ?? new(), r.Add, r.Remove)));
                return;
            }
            case ("POST", "/api/caption"):
            {
                var r = await BodyAsync<CaptionRequest>(context);
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.SetCaption(r.Ids ?? new(), r.Caption ?? "")));
                return;
            }
            case ("POST", "/api/move"):
            {
                var r = await BodyAsync<MoveRequest>(context);
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.Move(r.Ids ?? new(), r.Folder ?? "")));
                return;
            }
            case ("POST", "/api/folders"):
            {
                var r = await BodyAsync<FolderRequest>(context);
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.CreateFolder(r.Parent ?? "", r.Name ?? "")));
                return;
            }
            case ("POST", "/api/folders/rename"):
            {
                var r = await BodyAsync<FolderRequest>(context);
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.RenameFolder(r.Path ?? "", r.Name ?? "")));
                return;
            }
            case ("POST", "/api/undo"):
            {
                var lib = RequireLibrary();
                await JsonAsync(res, 200, Done(lib, lib.Undo()));
                return;
            }
            case ("POST", "/api/reveal"):
            {
                var r = await BodyAsync<IdRequest>(context);
                var full = RequireLibrary().FullPath(r.Id ?? "");
                _host?.Reveal(full);
                await JsonAsync(res, 200, new { ok = _host is not null });
                return;
            }
            case ("GET", "/api/export-destination"):
            {
                var lib = RequireLibrary();
                var folder = PathGuard.Resolve(lib.Root, context.Request.Query["folder"].FirstOrDefault() ?? "");
                await JsonAsync(res, 200, new { destination = _settings.ExportDestinations.GetValueOrDefault(folder) });
                return;
            }
            case ("POST", "/api/export"):
                await JsonAsync(res, 200, StartExport(await BodyAsync<ExportRequest>(context)));
                return;
            default:
                await JsonAsync(res, 404, new { error = "Not found." });
                return;
        }
    }

    /// <summary>Which build this is: the commit it was built from (the SDK stamps it into the version), so it's easy
    /// to tell an old exe from a new one.</summary>
    static readonly string Build = BuildId();

    static string BuildId()
    {
        var version = typeof(CuratorServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[(plus + 1)..Math.Min(version.Length, plus + 8)];
    }

    object State()
    {
        var lib = Library;
        return new
        {
            folder = lib is null ? null : new { path = lib.Root, name = lib.Name, version = lib.Version },
            recent = _settings.Recent.Where(Directory.Exists).Select(p => new { path = p, name = Path.GetFileName(p) is { Length: > 0 } n ? n : p }),
            nativePicker = _host is not null,
            canReveal = _host is not null,
            undo = lib?.UndoDescription,
            job = _jobs.Current,
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            build = Build,
            separator = Path.DirectorySeparatorChar.ToString()
        };
    }

    object Open(OpenRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Path)) throw new CuratorException("Choose a folder to open.");
        string path;
        try { path = PathGuard.Normalize(r.Path.Trim().Trim('"')); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new CuratorException("That isn't a folder path."); }
        if (!Directory.Exists(path)) throw new CuratorException("That folder doesn't exist.", 404);

        if (!r.Acknowledge && !_settings.IsAcknowledged(path))
        {
            return new { needsAcknowledge = true, path, name = Path.GetFileName(path) is { Length: > 0 } n ? n : path };
        }
        if (r.DontAskAgain)
        {
            _settings.Acknowledge(path);
            _settings.Save();
        }
        OpenFolder(path);
        return State();
    }

    object StartCopy(CopyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Source)) throw new CuratorException("Choose a folder to copy.");
        var source = PathGuard.Normalize(r.Source);
        var job = _jobs.Start("copy", $"Copying {Path.GetFileName(source)}", (report, cancel) =>
        {
            var copy = FolderCopier.Copy(source, r.Destination ?? "", new Progress(report), cancel);
            _settings.Acknowledge(copy); // it's the copy; no need to warn about it
            _settings.Save();
            return new { path = copy };
        });
        return new { job };
    }

    object StartExport(ExportRequest r)
    {
        var lib = RequireLibrary();
        var folder = r.Folder ?? "";
        var folderPath = PathGuard.Resolve(lib.Root, folder);
        var destination = WebExporter.CheckDestination(lib, r.Destination ?? "");
        _settings.ExportDestinations[folderPath] = destination;
        _settings.Save();

        var name = folder.Length == 0 ? lib.Name : Path.GetFileName(folderPath);
        var job = _jobs.Start("export", $"Exporting {name}", (report, cancel) =>
        {
            return WebExporter.Export(lib, folder, destination, new SyncProgress<(int, int, string)>(p => report(p.Item1, p.Item2, p.Item3)), cancel);
        });
        return new { job };
    }

    object LibraryJson(PhotoLibrary lib)
    {
        var snapshot = lib.Current;
        var dates = _dates;
        return new
        {
            version = lib.Version,
            root = new { path = lib.Root, name = lib.Name },
            undo = lib.UndoDescription,
            duplicates = _duplicates.Count,
            checking = _prints?.Pending ?? 0,
            photos = snapshot.Photos.Select(p => new
            {
                id = p.Id,
                folder = p.Folder,
                name = p.Name,
                size = p.Size,
                modified = new DateTimeOffset(new DateTime(p.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                v = p.Modified.ToString("x"),
                caption = p.Sidecar?.Caption ?? "",
                tags = (IReadOnlyList<string>?)p.Sidecar?.Tags ?? Array.Empty<string>(),
                date = p.Sidecar?.Date ?? dates?.Get(lib.FullPath(p.Id), p.Size, p.Modified),
                sidecar = p.HasSidecar,
                error = p.SidecarError
            }),
            folders = snapshot.Folders.Select(f => new { path = f.Path, name = f.Name, count = f.Count, total = f.Total, writeUp = f.HasWriteUp }),
            tags = snapshot.Tags.Select(t => new { name = t.Key, count = t.Value })
        };
    }

    object StartImportCheck(SourcesRequest r)
    {
        var lib = RequireLibrary();
        var prints = _prints!;
        var sources = (r.Sources ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sources.Count == 0) throw new CuratorException("Choose a zip file or folder to import.");
        var job = _jobs.Start("check", "Checking the download against your folder", (report, cancel) =>
        {
            var plan = Importer.Check(lib, prints, sources, report, cancel);
            lock (_plans) _plans[plan.Id] = plan;
            return new
            {
                id = plan.Id,
                destination = plan.Destination,
                total = plan.Photos.Count,
                existing = plan.Count("existing"),
                repeat = plan.Count("repeat"),
                @new = plan.Count("new"),
                edited = plan.Photos.Where(p => p.Kind == "edited").Select(p => new { name = p.Name, path = p.Path, match = p.Match }),
                toCopy = plan.ToCopy.Count()
            };
        });
        return new { job };
    }

    object StartImport(PlanRequest r)
    {
        var lib = RequireLibrary();
        var prints = _prints!;
        ImportPlan? plan;
        lock (_plans) _plans.Remove(r.Id ?? "", out plan);
        if (plan is null) throw new CuratorException("That check is out of date. Check the download again.", 409);
        var job = _jobs.Start("import", $"Importing into {plan.Destination}", (report, cancel) =>
        {
            var result = Importer.Run(lib, prints, plan, report, cancel);
            ScheduleDuplicates();
            return result;
        });
        return new { job };
    }

    object DuplicatesJson(PhotoLibrary lib)
    {
        var groups = _duplicates;
        if (groups.Count == 0) groups = FindDuplicates(notify: false);
        return new
        {
            checking = _prints?.Pending ?? 0,
            groups = groups.Select(g => new
            {
                kind = g.Kind,
                keep = g.Keep,
                photos = g.Ids.Select(id => lib.Current.Find(id)).OfType<PhotoEntry>().Select(p =>
                {
                    var size = MetadataReader.ReadSize(lib.FullPath(p.Id));
                    return new
                    {
                        id = p.Id,
                        folder = p.Folder,
                        name = p.Name,
                        size = p.Size,
                        v = p.Modified.ToString("x"),
                        caption = p.Sidecar?.Caption ?? "",
                        tags = (IReadOnlyList<string>?)p.Sidecar?.Tags ?? Array.Empty<string>(),
                        width = size?.Width,
                        height = size?.Height
                    };
                })
            })
        };
    }

    object KeepDuplicates(KeepRequest r)
    {
        var lib = RequireLibrary();
        List<(string Keep, IReadOnlyList<string> Others)> groups;
        if (r.AllExact)
        {
            groups = FindDuplicates(notify: false).Where(g => g.Kind == "file")
                .Select(g => (g.Keep, (IReadOnlyList<string>)g.Ids.Where(id => id != g.Keep).ToList())).ToList();
            if (groups.Count == 0) throw new CuratorException("There are no exact copies to set aside.");
        }
        else
        {
            groups = (r.Groups ?? new())
                .Where(g => !string.IsNullOrEmpty(g.Keep) && g.Others is { Count: > 0 })
                .Select(g => (g.Keep!, (IReadOnlyList<string>)g.Others!)).ToList();
        }
        var result = lib.SetAsideDuplicates(groups);
        FindDuplicates(notify: false);
        return Done(lib, result);
    }

    static object PhotoJson(PhotoLibrary lib, string? id)
    {
        var photo = lib.Get(id ?? "");
        var full = lib.FullPath(photo.Id);
        var size = MetadataReader.ReadSize(full);
        if (photo.SidecarError is not null)
        {
            return new { id = photo.Id, name = photo.Name, folder = photo.Folder, error = photo.SidecarError, width = size?.Width, height = size?.Height };
        }
        var (data, source) = lib.Describe(photo.Id);
        return new
        {
            id = photo.Id,
            name = photo.Name,
            folder = photo.Folder,
            v = photo.Modified.ToString("x"),
            caption = data.Caption,
            date = data.Date,
            dateSource = source.ToString().ToLowerInvariant(),
            tags = data.Tags,
            alt = data.Alt,
            description = data.Description,
            extraKeys = data.ExtraKeys,
            sidecar = photo.HasSidecar,
            bytes = photo.Size,
            width = size?.Width,
            height = size?.Height,
            browser = PhotoFiles.IsBrowserFriendly(photo.Name)
        };
    }

    static object Done(PhotoLibrary lib, OperationResult result) => new
    {
        message = result.Message,
        moved = result.Moved,
        folder = result.Folder,
        version = lib.Version,
        undo = lib.UndoDescription
    };

    async Task SendImageAsync(HttpContext context, bool preview)
    {
        var lib = RequireLibrary();
        var full = lib.FullPath(context.Request.Query["id"].FirstOrDefault() ?? "");
        if (!PhotoFiles.IsPhoto(Path.GetFileName(full)) || !File.Exists(full)) throw new CuratorException("That photo is no longer there.", 404);

        string? file;
        string contentType = "image/jpeg";
        if (preview && PhotoFiles.IsBrowserFriendly(full))
        {
            file = full;
            contentType = ImageTypes[Path.GetExtension(full)];
        }
        else
        {
            file = preview
                ? await _thumbs.GetPreviewAsync(full, context.RequestAborted)
                : await _thumbs.GetThumbnailAsync(full, context.RequestAborted);
        }
        if (file is null) throw new CuratorException("This file can't be shown.", 415);

        context.Response.ContentType = contentType;
        // URLs carry the photo's modified time, so a cached copy is never stale.
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        await context.Response.SendFileAsync(file, context.RequestAborted);
    }

    PhotoLibrary RequireLibrary() => Library ?? throw new CuratorException("Open a folder first.", 409);

    static async Task<T> BodyAsync<T>(HttpContext context)
    {
        var body = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, JsonOptions, context.RequestAborted);
        return body ?? throw new CuratorException("That request was empty.");
    }

    /// <summary>Reports straight away on the worker thread (System.Progress would post to the thread pool out of order).</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    sealed class Progress(Action<long, long, string> report) : IProgress<(long Done, long Total, string Name)>
    {
        public void Report((long Done, long Total, string Name) value) => report(value.Done, value.Total, value.Name);
    }
}
