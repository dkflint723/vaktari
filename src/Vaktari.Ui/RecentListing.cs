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

    /// <summary>
    /// A search, as somewhere you can be.
    ///
    /// **Results were a popup and nothing else could be done with them.** You
    /// could arrow through the list and press Enter; there was no
    /// multi-select, no drag, no context menu, no columns and no sorting, and
    /// the panel was drawn OVER the listing it was meant to help you act on.
    /// Everything else in this application acts on a pane's entries, so the
    /// results were the one collection of files it could not do anything to.
    ///
    /// Giving a search a path is the same move Recent and This PC already
    /// made, for the same payoff: the rows are FileEntry in Entries, so
    /// grouping, filtering, the three layouts, sorting, the details panel and
    /// the whole selection machinery need to know nothing about where they
    /// came from.
    ///
    /// **Shape: prefix, query, origin, scope, case — four fields, three colons.**
    ///
    ///   vaktari:search:report:C%3A%5CUsers%5Cme:here:any
    ///   vaktari:search:%2A.cs::everywhere:case
    ///
    /// The CASE field is what makes <see cref="Core.Search.SearchQuery.CaseSensitive"/>
    /// reachable. It is a field of the path for the same reason the scope is:
    /// asking the same question two ways is being in two places, so Back
    /// returns to the answer you had instead of re-running it, and a tab
    /// restored from the session file comes back asking what it was asking.
    ///
    /// **Three fields still parse, and that is not politeness.** Every path
    /// here goes into session.json verbatim — <c>PaneViewModel.ToTabState</c>
    /// writes <c>Path = CurrentPath</c> — so every search tab left open by a
    /// build older than this one comes back with three. Rejecting those would
    /// turn them into empty searches at the next start.
    ///
    /// The ORIGIN is carried even when the search is unscoped, and that is the
    /// whole reason there is a separate flag rather than just an empty scope:
    /// without it, ticking "everywhere" would throw away the folder you started
    /// from and the box could never be unticked back to it. A one-way door.
    ///
    /// **Both variable fields are percent-escaped, which is not decoration.**
    /// PathRules.Normalise runs over every path this pane compares — it swaps
    /// '/' for '' on Windows and trims a trailing separator — and it sits on
    /// both sides of every PathRules.Same. A query is arbitrary text: it can
    /// hold a colon, which would break the split, a backslash, or a trailing
    /// slash. Uri.EscapeDataString leaves only RFC 3986 unreserved characters,
    /// which contain none of ':', '/', '' or '%', so Normalise has nothing to
    /// rewrite and no separator is found to call a parent.
    /// </summary>
    public const string SearchPrefix = "vaktari:search:";

    private const string Here = "here";
    private const string Everywhere = "everywhere";

    /// <summary>
    /// The case field's two words. Spelled out rather than left as "present or
    /// absent", so a three-field path is unambiguously an OLD path rather than
    /// a new one that happens to be case-insensitive.
    /// </summary>
    private const string Cased = "case";
    private const string AnyCase = "any";

    /// <summary>
    /// One key for every search, so a view is remembered as "how I like
    /// searches to look" rather than once per query ever typed — which would
    /// grow the per-folder view store by one record for every search anybody
    /// ever ran, on an unbounded key.
    /// </summary>
    public const string SearchViewKey = SearchPrefix + "*";

    public static string Search(string query, string? origin, bool scoped, bool matchCase = false)
        => SearchPrefix
           + Uri.EscapeDataString(query) + ":"
           + Uri.EscapeDataString(origin ?? "") + ":"
           + (scoped && !string.IsNullOrEmpty(origin) ? Here : Everywhere) + ":"
           + (matchCase ? Cased : AnyCase);

    public static bool IsSearch(string? path)
        => path is not null && path.StartsWith(SearchPrefix, StringComparison.Ordinal);

    public static string QueryOf(string path) => Part(path, 0);

    /// <summary>The folder the search was started from, whether or not it is
    /// currently the scope. Null when it was started somewhere that is not a
    /// folder — This PC, the bin, either Recent listing.</summary>
    public static string? OriginOf(string path)
        => Part(path, 1) is { Length: > 0 } origin ? origin : null;

    /// <summary>
    /// Whether the search is narrowed to the folder it started from.
    ///
    /// **A place that is not a folder cannot be one**, however the path came to
    /// say otherwise — the box that writes these is one route, and a session
    /// file edited by hand is the other. Answered here rather than at each
    /// reader, so the tick box, the label and the query the backend is handed
    /// cannot disagree about it.
    /// </summary>
    public static bool IsScoped(string path)
        => Part(path, 2) == Here && OriginOf(path) is { } origin && !IsVirtual(origin);

    /// <summary>
    /// What the backend is actually asked to search; null is "everywhere",
    /// which is what ISearchProvider already documents.
    ///
    /// **"This folder only" over This PC searched for a folder called
    /// "vaktari:computer".** That rule is a fact about what a search path can
    /// mean, so it lives with the path rather than with the tick box.
    /// </summary>
    public static string? ScopeOf(string path) => IsScoped(path) ? OriginOf(path) : null;

    /// <summary>
    /// Whether the capitals in the question are part of it.
    ///
    /// **Absent means no**, which is what carries a three-field path written by
    /// an older build: it has no case field, so it means what it has always
    /// meant. Read here rather than at the checkbox for the same reason
    /// <see cref="IsScoped"/> is: the box, the query handed to the backend and
    /// a session file edited by hand all have to get one answer.
    /// </summary>
    public static bool MatchesCase(string path) => Part(path, 3) == Cased;

    /// <summary>
    /// Whether two places are the same place, for the pane's "you are already
    /// here" rule.
    ///
    /// **<c>PathRules.Same</c> is OrdinalIgnoreCase on Windows, and a search
    /// path carries a question rather than a filename.** With the case box
    /// ticked "readme" and "README" are two questions with two different
    /// answers — and the pane would not go from one to the other:
    /// <c>NavigateAsync</c>'s already-here guard read the two paths as one and
    /// returned without loading anything, so Enter did nothing at all. Measured
    /// here as a red test on Windows, where <c>PathRules.Comparison</c> is
    /// OrdinalIgnoreCase; on Linux it is Ordinal and the guard already told
    /// them apart.
    ///
    /// Ordinal only for a search, because that rule is right for a folder:
    /// C:\Users and C:\users ARE one directory, and comparing them ordinally
    /// reloaded the listing and pushed a Back that went nowhere.
    ///
    /// **The ORIGIN field is not immune to that, and it is worth being exact
    /// about why it does not bite.** Percent-escaping leaves letters alone —
    /// measured here, C:\Users\Me\Docs and c:\users\me\docs escape to two
    /// different strings — so two searches asking one question of one folder
    /// reached by its two spellings do compare as two places under this rule.
    /// Nothing in the pane can produce that pair: all three roads that build a
    /// search path (<c>PaneViewModel</c>'s two box setters and
    /// <c>RunSearch</c>) rebuild it from the origin of the path the pane is
    /// already on, and Back and Forward only pop paths the pane itself pushed.
    /// </summary>
    public static bool SamePlace(string? a, string? b)
        => IsSearch(a) || IsSearch(b)
            ? string.Equals(a, b, StringComparison.Ordinal)
            : PathRules.Same(a, b);

    /// <summary>
    /// One field of a search path.
    ///
    /// **Malformed returns empty rather than throwing.** These strings go into
    /// the session file and come back at startup; a hand-edited or truncated
    /// one must give an empty search, not stop the window opening.
    ///
    /// **Three OR four, because the case field arrived after the other three.**
    /// A tab left open on a search by an older build is in session.json with
    /// three, and a parser that demanded four would reopen it as an empty
    /// search. The missing field reads as "" and so as
    /// <see cref="AnyCase"/> — the behaviour those paths were written under.
    /// </summary>
    private static string Part(string path, int index)
    {
        if (!IsSearch(path)) return "";

        var parts = path[SearchPrefix.Length..].Split(':');

        if (parts.Length is not (3 or 4)) return "";

        if (index >= parts.Length) return "";

        return index == 2 ? parts[2] : Uri.UnescapeDataString(parts[index]);
    }

    /// <summary>Any listing that is not a directory.</summary>
    public static bool IsVirtual(string? path)
        => IsRecent(path) || path == Trash || path == Computer || IsSearch(path);

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

        // **Above the fallback, and computed rather than constant.** A search
        // path carries its own query, so this is the one label that is not a
        // fixed string — and the final arm is a catch-all, so it would
        // otherwise swallow every search and title the tab "Recent locations".
        _ when IsSearch(path) => $"Search: {QueryOf(path)}",

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
