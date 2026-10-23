using PhotoCurator.Core.Media;

namespace PhotoCurator.Core.Library;

/// <summary>
/// Photos that are the same picture. "file": byte-for-byte copies. "moment": the same EXIF capture moment and shape but
/// different bytes (an edited or recompressed copy, or a burst frame from the same second). <see cref="Keep"/> is the
/// suggested one to keep.
/// </summary>
public sealed record DuplicateGroup(string Kind, IReadOnlyList<string> Ids, string Keep);

public static class DuplicateFinder
{
    /// <summary>Where "keep one, set the others aside" puts the others. Never searched for duplicates itself.</summary>
    public const string SetAsideFolder = "Duplicates set aside";

    /// <summary>Where imports land. Photos still here count as not yet sorted when choosing which copy to keep.</summary>
    public const string InboxFolder = "Inbox";

    public static bool IsSetAside(string folder) =>
        folder.Equals(SetAsideFolder, PathGuard.Comparison) || folder.StartsWith(SetAsideFolder + "/", PathGuard.Comparison);

    public static IReadOnlyList<DuplicateGroup> Find(PhotoLibrary library, Fingerprints prints, CancellationToken cancel = default)
    {
        var photos = library.Current.Photos.Where(p => !IsSetAside(p.Folder) && p.SidecarError is null).ToList();
        var keys = photos.Select(p => Fingerprints.Key(p.Name, p.Size, p.Modified)).ToArray();
        var parent = Enumerable.Range(0, photos.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        void Union(int a, int b) => parent[Root(a)] = Root(b);
        var sha = new Dictionary<int, string>();

        // Byte-for-byte: only photos sharing a size are ever hashed.
        foreach (var sameSize in Enumerable.Range(0, photos.Count).GroupBy(i => photos[i].Size).Where(g => g.Count() > 1))
        {
            foreach (var i in sameSize)
            {
                cancel.ThrowIfCancellationRequested();
                try { sha[i] = prints.Sha256(library.FullPath(photos[i].Id), keys[i]); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* gone or locked; skip */ }
            }
            foreach (var same in sameSize.Where(sha.ContainsKey).GroupBy(i => sha[i]).Where(g => g.Count() > 1))
            {
                var first = same.First();
                foreach (var other in same.Skip(1)) Union(first, other);
            }
        }

        // Same moment: from the moments read in the background (see Fingerprints.Fill).
        var withMoment = Enumerable.Range(0, photos.Count).Select(i => (i, moment: prints.CachedMoment(keys[i]))).Where(x => x.moment is not null);
        foreach (var sameTime in withMoment.GroupBy(x => x.moment!.Taken).Where(g => g.Count() > 1))
        {
            var members = sameTime.ToList();
            for (var a = 0; a < members.Count; a++)
            {
                for (var b = a + 1; b < members.Count; b++)
                {
                    if (members[a].moment!.Matches(members[b].moment!)) Union(members[a].i, members[b].i);
                }
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var cluster in Enumerable.Range(0, photos.Count).GroupBy(Root).Where(g => g.Count() > 1))
        {
            var members = cluster.ToList();
            var exact = members.All(sha.ContainsKey) && members.Select(i => sha[i]).Distinct().Count() == 1;
            if (!exact && prints.IsDismissed(members.Select(i => keys[i]).ToList())) continue;
            var entries = members.Select(i => photos[i]).ToList();
            groups.Add(new DuplicateGroup(exact ? "file" : "moment", entries.Select(p => p.Id).ToList(), Best(entries).Id));
        }
        return groups.OrderBy(g => g.Ids[0], Comparer<string>.Create(CompareIds)).ToList();
    }

    /// <summary>The copy worth keeping: captioned and tagged, already sorted out of the Inbox, then the biggest file.</summary>
    public static PhotoEntry Best(IEnumerable<PhotoEntry> photos) =>
        photos
            .OrderByDescending(p => p.Sidecar?.Caption.Length > 0)
            .ThenByDescending(p => p.Sidecar?.Tags.Count ?? 0)
            .ThenByDescending(p => !InInbox(p.Folder))
            .ThenByDescending(p => p.HasSidecar)
            .ThenByDescending(p => p.Size)
            .ThenBy(p => p.Id, Comparer<string>.Create(CompareIds))
            .First();

    static bool InInbox(string folder) =>
        folder.Equals(InboxFolder, PathGuard.Comparison) || folder.StartsWith(InboxFolder + "/", PathGuard.Comparison);

    static int CompareIds(string a, string b)
    {
        var byFolder = Scanner.CompareFolderPaths(PathGuard.ParentOf(a), PathGuard.ParentOf(b));
        return byFolder != 0 ? byFolder : Names.Natural.Compare(Path.GetFileName(a), Path.GetFileName(b));
    }
}
