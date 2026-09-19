using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// What a scan of one folder came to: how many sets of copies there are, how
/// many files stand in them, what deleting all but one of each would give
/// back, and how much of the tree could not be read.
///
/// **The figure counts the rows and nothing else.** Its neighbour
/// <see cref="Usage"/> deliberately totals hidden files a listing is not
/// showing, because "how big is this folder" is a claim about the disk and the
/// disk does not care which files a view setting hides. This is the opposite
/// question — "what do I get back by deleting what is in front of me" — and an
/// answer that counted copies the person cannot see would be a promise the
/// visible rows cannot keep.
/// </summary>
public readonly record struct Copies(int Sets, int Files, long Reclaimable, int Unreadable);

/// <summary>
/// The rows of a duplicates listing, what they come to, and which of them are
/// the spare ones.
/// </summary>
/// <param name="Extras">Every copy but one of each set. **This is the whole
/// safety property of the listing**: the one route that selects in bulk leaves
/// a member of every set unselected, so selecting everything it offers and
/// pressing Delete can never take the last copy of anything.</param>
internal readonly record struct CopyRows(
    List<FileEntry> Rows, Copies Summary, List<string> Extras);

/// <summary>
/// Every file under one folder that is a copy of another, as somewhere you can
/// be.
///
/// **Every copy is a row, not just the spare ones.** A listing that showed
/// only what it had decided to throw away would be safe by construction and
/// would also make the decision for the person: which of two copies to keep
/// depends on where each one lives and when it was written, and that is a
/// judgement nobody else can make. So all of them are shown, the date column
/// is filled in for once, and the bulk route is a SELECTION — see
/// <see cref="CopyRows.Extras"/> — rather than a filtered listing.
///
/// The measuring is <see cref="DuplicateFinder"/>'s and the shape is the
/// pane's, as <see cref="SpaceListing"/> beside it: every listing source here
/// yields batches of <see cref="FileEntry"/>, so sorting, filtering, the three
/// layouts and the selection machinery run unchanged.
/// </summary>
public static class DuplicateListing
{
    /// <summary>
    /// One batch, because <see cref="DuplicateFinder.Find"/> answers all at
    /// once. The scan runs on the pool: it walks every folder below the one
    /// asked about and reads every file that shares a length with another, so
    /// on a home directory it is seconds and can be minutes.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        string folder,
        bool includeHidden,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        Action<Copies>? onScanned = null,
        Action<IReadOnlyList<string>>? onExtras = null)
    {
        var report = await Task.Run(
            () => DuplicateFinder.Find(folder, progress: null, ct), ct).ConfigureAwait(false);

        var built = Build(report, includeHidden);

        // Both handed back the way SpaceListing hands back its total and
        // SearchListing its cap: noticed here on the pool, published by the
        // caller on the dispatcher, because a property the band binds to
        // cannot be raised from a walking thread.
        onScanned?.Invoke(built.Summary);
        onExtras?.Invoke(built.Extras);

        yield return built.Rows;
    }

    /// <summary>
    /// The rows, from the report.
    ///
    /// Separated from the enumeration so it can be tested without a tree to
    /// scan — what is interesting is which rows survive, what they carry and
    /// which of them are spare, not the plumbing.
    ///
    /// **A set is shown whole or not at all.** Hidden files are dropped here as
    /// every other listing drops them, and a set left with one visible member
    /// is then dropped too: a lone row labelled a copy, with nothing on screen
    /// it is a copy OF, invites deleting the only one you can see.
    /// </summary>
    internal static CopyRows Build(DuplicateReport report, bool includeHidden)
    {
        var rows = new List<FileEntry>();
        var extras = new List<string>();
        var sets = 0;
        long reclaimable = 0;

        foreach (var set in report.Sets)
        {
            var shown = new List<FileEntry>();

            foreach (var path in set.Paths)
            {
                // **One look per row, for two facts.** The scan carried paths
                // and a length and nothing else, and both the date and whether
                // the file is hidden are wanted here — the same argument
                // UsageRow makes for carrying IsConcealed out of attributes it
                // had already read. The "no follow-up stat per entry" rule the
                // enumeration lives by is about a folder of 200,000 files;
                // these rows are the handful that turned out to be copies, and
                // every one of them has just been read from end to end.
                var (when, concealed, there) = Look(path);

                if (!there) continue;

                if (concealed && !includeHidden) continue;

                shown.Add(new FileEntry(
                    System.IO.Path.GetFileName(path),
                    path,
                    set.Length,
                    when,
                    concealed ? EntryFlags.Hidden : EntryFlags.None));
            }

            if (shown.Count < 2) continue;

            // **By path, so the same copy is kept every time the scan is run.**
            // The order carries no claim about which file came first: nothing on
            // disk records that, a copy usually carries the original's dates,
            // and the listing shows every member precisely so the choice can be
            // made by hand instead.
            shown.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

            sets++;
            reclaimable += set.Length * (shown.Count - 1);

            rows.AddRange(shown);

            for (var i = 1; i < shown.Count; i++) extras.Add(shown[i].FullPath);
        }

        return new CopyRows(rows, new Copies(sets, rows.Count, reclaimable, report.Unreadable), extras);
    }

    /// <summary>
    /// When the file was written and whether it is concealed, or
    /// <c>There: false</c> for one that has gone between the scan and the
    /// listing — which is not an error, just a file somebody deleted while the
    /// scan ran, and it stops being a copy of anything the moment it does.
    /// </summary>
    private static (DateTimeOffset When, bool Concealed, bool There) Look(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists) return (default, false, false);

            return (info.LastWriteTimeUtc,
                (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Readable enough to be compared and not to be asked about, which
            // is odd but not worth losing the row over: it is shown with no
            // date, as This PC's drives and the Recent rows are.
            return (default, false, true);
        }
    }
}
