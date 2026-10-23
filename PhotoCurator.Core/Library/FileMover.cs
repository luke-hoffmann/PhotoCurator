namespace PhotoCurator.Core.Library;

/// <summary>One file moved from one full path to another.</summary>
public sealed record FileMove(string From, string To);

/// <summary>
/// Moves a photo together with everything that belongs to it (its .md sidecar, Google Takeout .json files, a Live Photo
/// clip). Never overwrites: on a clash the photo and its companions become "name (1).ext". All-or-nothing: if any file
/// can't move, the ones already moved go back.
/// </summary>
public static class FileMover
{
    public static IReadOnlyList<FileMove> MovePhoto(string photoPath, string destinationDir)
    {
        var sourceDir = Path.GetDirectoryName(photoPath)!;
        if (Path.TrimEndingDirectorySeparator(sourceDir).Equals(Path.TrimEndingDirectorySeparator(destinationDir), PathGuard.Comparison))
        {
            return Array.Empty<FileMove>();
        }
        if (!File.Exists(photoPath)) throw new CuratorException($"{Path.GetFileName(photoPath)} is no longer there.", 404);
        if (!Directory.Exists(destinationDir)) throw new CuratorException("The destination folder is no longer there.", 404);

        var photoName = Path.GetFileName(photoPath);
        var siblings = Directory.EnumerateFiles(sourceDir).Select(Path.GetFileName).OfType<string>().ToList();
        var companions = Companions(photoName, siblings);

        var targetName = FreeName(destinationDir, photoName, companions);
        var plan = new List<FileMove> { new(photoPath, Path.Combine(destinationDir, targetName)) };
        foreach (var companion in companions)
        {
            plan.Add(new FileMove(Path.Combine(sourceDir, companion), Path.Combine(destinationDir, RenameCompanion(companion, photoName, targetName))));
        }

        var done = new List<FileMove>();
        try
        {
            foreach (var move in plan)
            {
                File.Move(move.From, move.To, overwrite: false);
                done.Add(move);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryRevert(done);
            throw new CuratorException($"Couldn't move {photoName}: {e.Message}", 409);
        }
        return done;
    }

    /// <summary>Moves files back, last first. Throws without touching anything if a file's old spot has been taken.</summary>
    public static void Revert(IReadOnlyList<FileMove> moves)
    {
        foreach (var move in moves)
        {
            if (File.Exists(move.From)) throw new CuratorException($"{Path.GetFileName(move.From)} already exists where it used to be.", 409);
        }
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            if (File.Exists(moves[i].To)) File.Move(moves[i].To, moves[i].From, overwrite: false);
        }
    }

    /// <summary>Names of the files that move with <paramref name="photoName"/>.</summary>
    public static List<string> Companions(string photoName, IReadOnlyCollection<string> siblings)
    {
        var list = new List<string>();
        var sidecar = PhotoFiles.SidecarName(photoName);
        if (siblings.Contains(sidecar, StringComparer.OrdinalIgnoreCase)) list.Add(siblings.First(s => s.Equals(sidecar, StringComparison.OrdinalIgnoreCase)));
        list.AddRange(PhotoFiles.TakeoutCompanions(photoName, siblings));
        if (PhotoFiles.VideoCompanion(photoName, siblings) is { } video) list.Add(video);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"IMG_1.HEIC" if free in <paramref name="dir"/>, else "IMG_1 (1).HEIC", "IMG_1 (2).HEIC"…, checking companions too.</summary>
    internal static string FreeName(string dir, string photoName, IReadOnlyList<string> companions)
    {
        var stem = Path.GetFileNameWithoutExtension(photoName);
        var ext = Path.GetExtension(photoName);
        for (var n = 0; n < 10_000; n++)
        {
            var candidate = n == 0 ? photoName : $"{stem} ({n}){ext}";
            if (File.Exists(Path.Combine(dir, candidate)) || Directory.Exists(Path.Combine(dir, candidate))) continue;
            if (companions.Any(c => File.Exists(Path.Combine(dir, RenameCompanion(c, photoName, candidate))))) continue;
            return candidate;
        }
        throw new CuratorException($"Couldn't find a free name for {photoName}.", 409);
    }

    internal static string RenameCompanion(string companion, string oldPhotoName, string newPhotoName)
    {
        if (oldPhotoName.Equals(newPhotoName, StringComparison.Ordinal)) return companion;
        if (companion.StartsWith(oldPhotoName, StringComparison.OrdinalIgnoreCase))
        {
            return newPhotoName + companion[oldPhotoName.Length..];
        }
        var oldStem = Path.GetFileNameWithoutExtension(oldPhotoName);
        var newStem = Path.GetFileNameWithoutExtension(newPhotoName);
        if (Path.GetFileNameWithoutExtension(companion).Equals(oldStem, StringComparison.OrdinalIgnoreCase))
        {
            return newStem + Path.GetExtension(companion); // Live Photo clip
        }
        return newPhotoName + ".json"; // Takeout's "IMG.HEIC(1).json" spelling; keep it findable under the new name
    }

    static void TryRevert(List<FileMove> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            try { File.Move(done[i].To, done[i].From, overwrite: false); } catch { /* leave it where it is; nothing is lost */ }
        }
    }
}
