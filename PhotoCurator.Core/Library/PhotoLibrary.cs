using System.Text;
using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Library;

/// <summary>Fields to change on one photo; null means "leave as is".</summary>
public sealed record PhotoUpdate(string? Caption = null, string? Date = null, List<string>? Tags = null, string? Alt = null, string? Description = null);

/// <summary>What an operation did, in words for the UI, plus where moved photos went (old id → new id).</summary>
public sealed record OperationResult(string Message, IReadOnlyDictionary<string, string>? Moved = null, string? Folder = null);

/// <summary>
/// The open folder: a live scan of its photos plus every change the app can make to it. All changes are plain file
/// operations (move, write a sidecar, create or rename a folder); originals are never modified or deleted.
/// </summary>
public sealed class PhotoLibrary : IDisposable
{
    readonly object _sync = new();
    readonly SidecarCache _cache = new();
    readonly UndoLog _undo = new();
    readonly FileSystemWatcher? _watcher;
    readonly Timer _rescanTimer;
    LibrarySnapshot _current;
    bool _disposed;

    public string Root { get; }
    public string Name => Path.GetFileName(Root) is { Length: > 0 } name ? name : Root;
    public LibrarySnapshot Current => Volatile.Read(ref _current);
    /// <summary>Goes up every time the scan finds something different. The UI refetches when it changes.</summary>
    public long Version { get; private set; } = 1;
    public string? UndoDescription { get { lock (_sync) return _undo.NextDescription; } }

    /// <summary>Raised (on a background thread) after a scan found changes, whether the app or someone else made them.</summary>
    public event Action? Changed;

    /// <summary>Reads a photo's taken date for new sidecars. Replaceable in tests.</summary>
    public Func<string, DateTime> TakenDate { get; init; } = path => MetadataReader.ReadTakenDate(path).Date;

    public PhotoLibrary(string root, bool watch = true)
    {
        Root = PathGuard.Normalize(root);
        if (!Directory.Exists(Root)) throw new CuratorException("That folder doesn't exist.", 404);
        _current = Scanner.Scan(Root, _cache);
        _rescanTimer = new Timer(_ => RescanFromWatcher());
        if (watch) _watcher = TryWatch();
    }

    public LibrarySnapshot Refresh()
    {
        lock (_sync)
        {
            var next = Scanner.Scan(Root, _cache);
            var changed = next.Signature != _current.Signature;
            Volatile.Write(ref _current, next);
            if (!changed) return next;
            Version++;
        }
        Changed?.Invoke();
        return Current;
    }

    public PhotoEntry Get(string id) =>
        Current.Find(id) ?? Refresh().Find(id) ?? throw new CuratorException("That photo is no longer there.", 404);

    public string FullPath(string id) => PathGuard.Resolve(Root, id);

    /// <summary>The sidecar as stored, or a fresh one with the taken date filled in when the photo has none yet.</summary>
    public (SidecarData Data, DateSource DateSource) Describe(string id)
    {
        var photo = Get(id);
        if (photo.SidecarError is not null) throw SidecarUnreadable(photo);
        if (photo.Sidecar is { } sidecar)
        {
            if (sidecar.Date is not null) return (sidecar, DateSource.Sidecar);
            var copy = sidecar.Clone();
            var (date, source) = MetadataReader.ReadTakenDate(FullPath(id));
            copy.Date = Sidecar.FormatDate(date);
            return (copy, source);
        }
        var (taken, from) = MetadataReader.ReadTakenDate(FullPath(id));
        return (new SidecarData { Date = Sidecar.FormatDate(taken) }, from);
    }

    // ---------- edits ----------

    /// <summary>Edits one photo. <paramref name="field"/> folds consecutive edits of the same field into one undo step.</summary>
    public OperationResult UpdatePhoto(string id, PhotoUpdate update, string? field = null)
    {
        lock (_sync)
        {
            var photo = Get(id);
            var undo = new SidecarUndo { Description = $"Edit {photo.Name}", CoalesceKey = field is null ? null : $"{photo.Id}|{field}" };
            EditSidecar(photo, undo, data =>
            {
                if (update.Caption is not null) data.Caption = SingleLine(update.Caption);
                if (update.Date is not null) data.Date = Sidecar.NormalizeDate(update.Date) ?? data.Date;
                if (update.Tags is not null) data.Tags = NormalizeTags(update.Tags);
                if (update.Alt is not null) data.Alt = SingleLine(update.Alt);
                if (update.Description is not null) data.Description = update.Description.Replace("\r\n", "\n").Trim();
            });
            if (undo.Changes.Count > 0) _undo.Push(undo);
        }
        Refresh();
        return new OperationResult("Saved");
    }

    public OperationResult ChangeTags(IReadOnlyCollection<string> ids, IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        var adding = NormalizeTags(add ?? Array.Empty<string>());
        var removing = NormalizeTags(remove ?? Array.Empty<string>());
        if (adding.Count == 0 && removing.Count == 0) throw new CuratorException("Pick a tag to add or remove.");

        lock (_sync)
        {
            var photos = Resolve(ids);
            var verb = adding.Count > 0 ? $"Tagged {Count(photos.Count)} \"{string.Join("\", \"", adding)}\"" : $"Removed \"{string.Join("\", \"", removing)}\" from {Count(photos.Count)}";
            var undo = new SidecarUndo { Description = verb };
            foreach (var photo in photos)
            {
                // Removing a tag from a photo that has no sidecar has nothing to do; don't create one just for that.
                if (adding.Count == 0 && photo.Sidecar is null && photo.SidecarError is null) continue;
                EditSidecar(photo, undo, data =>
                {
                    data.Tags.RemoveAll(t => removing.Contains(t));
                    foreach (var tag in adding) if (!data.Tags.Contains(tag)) data.Tags.Add(tag);
                });
            }
            if (undo.Changes.Count > 0) _undo.Push(undo);
            Refresh();
            return new OperationResult(verb);
        }
    }

    public OperationResult SetCaption(IReadOnlyCollection<string> ids, string caption)
    {
        var text = SingleLine(caption);
        lock (_sync)
        {
            var photos = Resolve(ids);
            var message = text.Length == 0 ? $"Cleared the caption on {Count(photos.Count)}" : $"Captioned {Count(photos.Count)}";
            var undo = new SidecarUndo { Description = message };
            foreach (var photo in photos) EditSidecar(photo, undo, data => data.Caption = text);
            if (undo.Changes.Count > 0) _undo.Push(undo);
            Refresh();
            return new OperationResult(message);
        }
    }

    /// <summary>
    /// Moves photos (with their companions) into <paramref name="folder"/>. Photos without a sidecar get one first, with the
    /// taken date, so a sorted photo no longer shows up under "New".
    /// </summary>
    public OperationResult Move(IReadOnlyCollection<string> ids, string folder)
    {
        lock (_sync)
        {
            var destination = PathGuard.Resolve(Root, folder);
            if (!Directory.Exists(destination)) throw new CuratorException("That folder is no longer there.", 404);
            var destRel = PathGuard.ToRelative(Root, destination);
            var photos = Resolve(ids);
            var undo = new MoveUndo { Description = $"Move {Count(photos.Count)} to {DisplayFolder(destRel)}" };
            var moved = new Dictionary<string, string>(PathGuard.Comparer);

            try
            {
                foreach (var photo in photos)
                {
                    var full = FullPath(photo.Id);
                    if (Path.GetDirectoryName(full)!.Equals(destination, PathGuard.Comparison)) continue;

                    if (!photo.HasSidecar)
                    {
                        var sidecarPath = full + PhotoFiles.SidecarExtension;
                        var data = new SidecarData { Date = Sidecar.FormatDate(TakenDate(full)) };
                        var text = Sidecar.Serialize(data);
                        Sidecar.WriteText(sidecarPath, text);
                        undo.CreatedSidecars.Add(new SidecarUndo.Change { Path = sidecarPath, Before = null, After = text });
                    }

                    var moves = FileMover.MovePhoto(full, destination);
                    if (moves.Count == 0) continue;
                    undo.Moves.Add(moves);
                    moved[photo.Id] = PathGuard.ToRelative(Root, moves[0].To);
                }
            }
            finally
            {
                // Whatever got done is undoable, even if a later photo failed.
                if (undo.Moves.Count > 0 || undo.CreatedSidecars.Count > 0) _undo.Push(undo);
                Refresh();
            }

            var message = moved.Count == 0 ? "Already in that folder" : $"Moved {Count(moved.Count)} to {DisplayFolder(destRel)}";
            return new OperationResult(message, moved, destRel);
        }
    }

    public OperationResult CreateFolder(string parent, string name)
    {
        var clean = Names.ValidateFolderName(name);
        lock (_sync)
        {
            var parentPath = PathGuard.Resolve(Root, parent);
            if (!Directory.Exists(parentPath)) throw new CuratorException("The parent folder is no longer there.", 404);
            var path = Path.Combine(parentPath, clean);
            if (Directory.Exists(path) || File.Exists(path)) throw new CuratorException($"\"{clean}\" already exists there.", 409);
            Directory.CreateDirectory(path);
            var rel = PathGuard.ToRelative(Root, path);
            _undo.Push(new FolderCreateUndo { Description = $"Create folder {clean}", Path = path });
            Refresh();
            return new OperationResult($"Created {DisplayFolder(rel)}", Folder: rel);
        }
    }

    public OperationResult RenameFolder(string folder, string newName)
    {
        var clean = Names.ValidateFolderName(newName);
        lock (_sync)
        {
            var path = PathGuard.Resolve(Root, folder);
            if (path.Equals(Root, PathGuard.Comparison)) throw new CuratorException("The open folder itself can't be renamed here.");
            if (!Directory.Exists(path)) throw new CuratorException("That folder is no longer there.", 404);
            var target = Path.Combine(Path.GetDirectoryName(path)!, clean);
            var caseOnly = target.Equals(path, StringComparison.OrdinalIgnoreCase);
            if (target.Equals(path, StringComparison.Ordinal)) return new OperationResult("Name unchanged", Folder: folder);
            if (!caseOnly && (Directory.Exists(target) || File.Exists(target))) throw new CuratorException($"\"{clean}\" already exists there.", 409);

            Directory.Move(path, target);
            var rel = PathGuard.ToRelative(Root, target);
            _undo.Push(new FolderRenameUndo { Description = $"Rename {Path.GetFileName(path)} to {clean}", From = path, To = target });
            Refresh();
            return new OperationResult($"Renamed to {clean}", Folder: rel);
        }
    }

    public OperationResult Undo()
    {
        lock (_sync)
        {
            var entry = _undo.Pop() ?? throw new CuratorException("Nothing to undo.", 409);
            try
            {
                var note = entry.Undo();
                return new OperationResult(note is null ? $"Undid: {entry.Description}" : $"Undid: {entry.Description}. {note}");
            }
            finally
            {
                Refresh();
            }
        }
    }

    // ---------- helpers ----------

    void EditSidecar(PhotoEntry photo, SidecarUndo undo, Action<SidecarData> change)
    {
        if (photo.SidecarError is not null) throw SidecarUnreadable(photo);
        var full = FullPath(photo.Id);
        if (!File.Exists(full)) throw new CuratorException($"{photo.Name} is no longer there.", 404);

        var sidecarPath = full + PhotoFiles.SidecarExtension;
        string? before = File.Exists(sidecarPath) ? File.ReadAllText(sidecarPath, Encoding.UTF8) : null;
        SidecarData data;
        try
        {
            data = before is null ? new SidecarData { Date = Sidecar.FormatDate(TakenDate(full)) } : Sidecar.Parse(before);
        }
        catch (FormatException e)
        {
            throw new CuratorException($"{photo.Name}.md couldn't be read ({e.Message}). Fix or delete that file, then try again.", 409);
        }

        change(data);
        var after = Sidecar.Serialize(data);
        if (after == before) return;
        Sidecar.WriteText(sidecarPath, after);
        undo.Changes.Add(new SidecarUndo.Change { Path = sidecarPath, Before = before, After = after });
    }

    List<PhotoEntry> Resolve(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) throw new CuratorException("Select some photos first.");
        var distinct = ids.Distinct(PathGuard.Comparer).ToList();
        foreach (var id in distinct) PathGuard.Resolve(Root, id);

        // The live scan is normally current; rescan once if something was renamed outside the app meanwhile.
        var snapshot = Current;
        if (distinct.Any(id => snapshot.Find(id) is null)) snapshot = Refresh();
        return distinct
            .Select(id => snapshot.Find(id) ?? throw new CuratorException($"{Path.GetFileName(id)} is no longer there.", 404))
            .ToList();
    }

    static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var list = new List<string>();
        foreach (var tag in tags)
        {
            var normalized = Names.NormalizeTag(tag);
            if (normalized is not null && !list.Contains(normalized)) list.Add(normalized);
        }
        return list;
    }

    static string SingleLine(string text) => string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    static string Count(int n) => n == 1 ? "1 photo" : $"{n} photos";

    string DisplayFolder(string rel) => rel.Length == 0 ? Name : rel;

    static CuratorException SidecarUnreadable(PhotoEntry photo) =>
        new($"{photo.Name}.md couldn't be read ({photo.SidecarError}). Fix or delete that file, then try again.", 409);

    FileSystemWatcher? TryWatch()
    {
        try
        {
            var watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            FileSystemEventHandler schedule = (_, _) => ScheduleRescan();
            watcher.Created += schedule;
            watcher.Deleted += schedule;
            watcher.Changed += schedule;
            watcher.Renamed += (_, _) => ScheduleRescan();
            watcher.Error += (_, _) => ScheduleRescan();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Some file systems can't be watched; fall back to a slow poll.
            _rescanTimer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            return null;
        }
    }

    void ScheduleRescan()
    {
        if (_disposed) return;
        try { _rescanTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* closing */ }
    }

    void RescanFromWatcher()
    {
        if (_disposed) return;
        try { Refresh(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The folder is busy or briefly gone; the next event or poll tries again.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _rescanTimer.Dispose();
    }
}
