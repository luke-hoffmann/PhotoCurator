using System.Text;

namespace PhotoCurator.Core.Library;

/// <summary>Something the app did that it can take back. Entries are applied newest first.</summary>
public abstract class UndoEntry
{
    public required string Description { get; init; }
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>Takes the change back. Returns a note about anything that had to be left alone, or null.</summary>
    public abstract string? Undo();
}

/// <summary>Sidecar writes: restores each file's earlier text (or removes a file the app created).</summary>
public sealed class SidecarUndo : UndoEntry
{
    public sealed class Change
    {
        public required string Path { get; init; }
        public required string? Before { get; init; }
        public required string After { get; set; }
    }

    public List<Change> Changes { get; } = new();
    /// <summary>Consecutive edits of the same field of the same photo fold into one entry.</summary>
    public string? CoalesceKey { get; init; }

    public override string? Undo()
    {
        var skipped = 0;
        foreach (var change in Enumerable.Reverse(Changes))
        {
            // A file someone changed outside the app since is left alone rather than overwritten.
            var current = File.Exists(change.Path) ? File.ReadAllText(change.Path, Encoding.UTF8) : null;
            if (current != change.After)
            {
                skipped++;
                continue;
            }
            if (change.Before is null) File.Delete(change.Path);
            else Sidecar.WriteText(change.Path, change.Before);
        }
        return skipped == 0 ? null : $"{skipped} file{(skipped == 1 ? " was" : "s were")} changed outside PhotoCurator and left as is.";
    }
}

/// <summary>Photo moves (each with its companions), plus sidecars created on the way.</summary>
public sealed class MoveUndo : UndoEntry
{
    public List<IReadOnlyList<FileMove>> Moves { get; } = new();
    /// <summary>Sidecars created (at the photo's old location) so moved photos no longer count as new.</summary>
    public List<SidecarUndo.Change> CreatedSidecars { get; } = new();

    public override string? Undo()
    {
        foreach (var group in Enumerable.Reverse(Moves))
        {
            foreach (var move in group)
            {
                if (File.Exists(move.From)) throw new CuratorException($"{Path.GetFileName(move.From)} already exists where it used to be, so the move can't be undone.", 409);
            }
        }
        foreach (var group in Enumerable.Reverse(Moves)) FileMover.Revert(group);

        var undo = new SidecarUndo { Description = "" };
        undo.Changes.AddRange(CreatedSidecars);
        return undo.Undo();
    }
}

public sealed class FolderCreateUndo : UndoEntry
{
    public required string Path { get; init; }

    public override string? Undo()
    {
        if (!Directory.Exists(Path)) return null;
        if (Directory.EnumerateFileSystemEntries(Path).Any())
        {
            throw new CuratorException($"\"{System.IO.Path.GetFileName(Path)}\" isn't empty any more, so it was kept.", 409);
        }
        Directory.Delete(Path);
        return null;
    }
}

public sealed class FolderRenameUndo : UndoEntry
{
    public required string From { get; init; }
    public required string To { get; init; }

    public override string? Undo()
    {
        if (Directory.Exists(From) && !string.Equals(From, To, StringComparison.OrdinalIgnoreCase))
        {
            throw new CuratorException($"\"{Path.GetFileName(From)}\" exists again, so the rename can't be undone.", 409);
        }
        Directory.Move(To, From);
        return null;
    }
}

/// <summary>The session's undo history. Kept in memory only; it ends when the folder is closed.</summary>
public sealed class UndoLog
{
    const int MaxEntries = 200;
    static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(2);

    readonly LinkedList<UndoEntry> _entries = new();

    public int Count => _entries.Count;

    public void Push(UndoEntry entry)
    {
        if (entry is SidecarUndo { CoalesceKey: { } key } edit && edit.Changes.Count == 1 &&
            _entries.Last?.Value is SidecarUndo { CoalesceKey: { } lastKey } last && lastKey == key && last.Changes.Count == 1 &&
            last.Changes[0].Path == edit.Changes[0].Path && DateTime.UtcNow - last.At < CoalesceWindow)
        {
            last.Changes[0].After = edit.Changes[0].After;
            last.At = DateTime.UtcNow;
            return;
        }

        _entries.AddLast(entry);
        while (_entries.Count > MaxEntries) _entries.RemoveFirst();
    }

    public UndoEntry? Pop()
    {
        var last = _entries.Last;
        if (last is null) return null;
        _entries.RemoveLast();
        return last.Value;
    }

    public string? NextDescription => _entries.Last?.Value.Description;
}
