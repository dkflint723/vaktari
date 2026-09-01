using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// The two virtual listings, and everything that knows they are not folders.
///
/// **Why a path at all.** Dolphin's Recent Files and Recent Locations open in
/// the main view with columns, sorting, view modes and selection — not in a side
/// panel like search. Giving them a path means the entire pane works unchanged:
/// the result is still <see cref="FileEntry"/> in <c>Entries</c>, so grouping,
/// filtering, the three layouts and the selection machinery need to know nothing
/// about where the list came from. The cost is that the handful of places which
/// assume a path is on disk have to be taught otherwise, and they are listed on
/// <see cref="IsRecent"/>.
///
/// **The scheme cannot collide.** Every real path here begins with '/', so a
/// prefix of "vaktari:" is unambiguous without a lookup.
/// </summary>
public static class VirtualPaths
{
    public const string Files = "vaktari:recent-files";
    public const string Locations = "vaktari:recent-locations";
    public const string Trash = "vaktari:trash";

    /// <summary>
    /// Every drive on the machine, in one listing — Explorer's This PC.
    ///
    /// **The place a drive root goes UP to.** Without it, C:\ was the top of the
    /// world: Up was disabled there by construction, the breadcrumbs stopped at
    /// the drive, and there was nowhere that showed the machine's drives beside
    /// each other. The sidebar lists them, but a sidebar is not somewhere you
    /// can sort, select, or open two of in tabs.
    /// </summary>
    public const string Computer = "vaktari:computer";

    /// <summary>Any listing that is not a directory.</summary>
    public static bool IsVirtual(string? path)
        => IsRecent(path) || path == Trash || path == Computer;

    /// <summary>
    /// True for a virtual listing. Callers that must check this:
    /// <c>LoadListingAsync</c> (enumerates the store, not the disk, and does not
    /// start a watcher), <c>RebuildBreadcrumbs</c> (one crumb, not a '/' split),
    /// and <c>NavigateAsync</c> (recording "recent" as a recently visited folder
    /// would be circular).
    /// </summary>
    public static bool IsRecent(string? path)
        => path is Files or Locations;

    public static RecentKind KindOf(string path)
        => path == Files ? RecentKind.File : RecentKind.Folder;

    /// <summary>What the breadcrumb and the tab title show.</summary>
    public static string Label(string path) => path switch
    {
        Files => "Recent files",
        Trash => Core.Naming.BinTitle,
        Computer => Core.Naming.ComputerTitle,
        _ => "Recent locations",
    };
}

/// <summary>
/// Turns the recency store into a listing.
/// </summary>
public static class RecentListing
{
    /// <summary>
    /// How many entries a listing shows. The store keeps more, so forgetting a
    /// few does not shorten the list. Dolphin shows about thirty; this is
    /// higher because the day bands make a longer list navigable rather than
    /// overwhelming. It is a constant precisely so it is easy to argue about.
    /// </summary>
    private const int Show = 100;

    /// <summary>
    /// One batch, because there are at most <see cref="Show"/> entries and the
    /// streaming machinery exists for folders with hundreds of thousands.
    ///
    /// Shaped as the same <c>IAsyncEnumerable</c> the filesystem provider
    /// returns so the caller can pick a source and change nothing else.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        IRecentStore? store,
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        if (store is null) yield break;

        // **On a pool thread, and the old comment here was wrong about that.**
        // It claimed the caller's ConfigureAwait(false) put this work on the
        // pool; it does not. An async iterator runs on the CALLER's thread
        // until it reaches a genuine suspension, and `await Task.CompletedTask`
        // never suspends - so every stat call below happened on the UI thread
        // while the window sat still. Task.Run is the same shape
        // XdgTrashMaintenance.SweepAsync already uses, and it also satisfies
        // the CS1998 the old line was really there for.
        var entries = await Task.Run(
            () => Gather(store, VirtualPaths.KindOf(path), ct), ct).ConfigureAwait(false);

        yield return entries;
    }

    private static List<FileEntry> Gather(IRecentStore store, RecentKind kind, CancellationToken ct)
    {
        var entries = new List<FileEntry>(Show);

        foreach (var recent in store.Recent(kind, Show))
        {
            ct.ThrowIfCancellationRequested();

            if (Build(recent) is { } entry) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// One store record as a listing row, or null if it is gone.
    ///
    /// **Entries that no longer exist are DROPPED, not shown greyed out.** A
    /// file manager that offers you a row which cannot be opened is worse than
    /// one that quietly forgets — and the store is not authoritative about the
    /// filesystem, it only remembers what you asked for.
    ///
    /// **`LastWriteTime` carries the ACCESS time here, not the modification
    /// time.** That is deliberate and it is the whole reason this listing needs
    /// no new machinery: `GroupMode.Modified` then bands it into Today /
    /// Yesterday exactly like Dolphin, sorting by time works, and the existing
    /// timestamp column shows the right value. The cost is that one field means
    /// something different in these two listings than everywhere else — which is
    /// why it is written down here rather than left to be discovered.
    /// </summary>
    /// <summary>
    /// The trash as a listing. Rows carry the item's ORIGINAL name and the
    /// deletion time, not the deduplicated key it is filed under — the key is an
    /// implementation detail of the trash and means nothing to a person.
    ///
    /// `FullPath` is the ORIGINAL path, so the Path column, sorting and the
    /// tooltip all say where the thing came from. Restore therefore has to map
    /// back to the trash key, which `PaneViewModel` does by asking the store
    /// again rather than by parsing the name.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateTrashAsync(
        ITrashMaintenance? trash,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        if (trash is null) yield break;

        // Off the caller's thread for the same reason as above, and it matters
        // more here: trash.List() walks every volume's bin and reads a metadata
        // file per item, so a full Recycle Bin froze the window for as long as
        // that took.
        var entries = await Task.Run(() => GatherTrash(trash, ct), ct).ConfigureAwait(false);

        yield return entries;
    }

    private static List<FileEntry> GatherTrash(ITrashMaintenance trash, CancellationToken ct)
    {
        var entries = new List<FileEntry>();

        foreach (var item in trash.List())
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(item.OriginalPath);
            if (string.IsNullOrEmpty(name)) name = item.TrashName;

            var flags = item.IsDirectory ? EntryFlags.Directory : EntryFlags.None;
            if (name.StartsWith('.')) flags |= EntryFlags.Hidden;

            entries.Add(new FileEntry(name, item.OriginalPath, item.Size, item.Deleted, flags));
        }

        return entries;
    }

    private static FileEntry? Build(RecentEntry recent)
    {
        try
        {
            var flags = EntryFlags.None;
            long length = 0;

            if (Directory.Exists(recent.Path))
            {
                flags |= EntryFlags.Directory;
            }
            else if (File.Exists(recent.Path))
            {
                length = new FileInfo(recent.Path).Length;
            }
            else
            {
                return null;
            }

            var name = Path.GetFileName(recent.Path);

            // A root has no name of its own.
            if (string.IsNullOrEmpty(name)) name = recent.Path;

            if (name.StartsWith('.')) flags |= EntryFlags.Hidden;

            return new FileEntry(name, recent.Path, length, recent.When, flags);
        }
        catch
        {
            // An unreadable entry is one we cannot honestly list.
            return null;
        }
    }
}
