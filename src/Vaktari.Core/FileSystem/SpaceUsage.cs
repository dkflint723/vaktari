namespace Vaktari.Core.FileSystem;

/// <summary>
/// What a folder holds, and what could not be read while finding out.
///
/// **The unreadable count is the honest half.** A walk that skips a folder it
/// cannot open — which every walk here does, because one denied folder must
/// cost that folder and not the measurement — returns a total that is short by
/// however much was behind it. Reporting the number is what lets a listing say
/// "12.4 GiB, and two folders could not be read" instead of a figure that
/// looks exact and is not.
/// </summary>
public readonly record struct Usage(long Bytes, int Files, int Folders, int Unreadable)
{
    /// <summary>The three numbers a progress report carries, for the callers
    /// that already speak <see cref="SizeProgress"/>.</summary>
    public SizeProgress Counted => new(Bytes, Files, Folders);
}

/// <summary>
/// One row of "what is using the space here": a child of the folder being
/// looked at, counted with everything underneath it.
///
/// **A row counts the child itself, whatever the child is.** A file's row is
/// one file of its own length; a folder's row is that folder plus its whole
/// tree; a link's row is one entry of no size. Counting the folder itself is
/// what makes the rows of a listing add up to <see cref="SpaceUsage.Measure"/>
/// of the folder they came from — without it a footer summing the rows
/// reported fewer folders than the progress line that fed the same pane.
///
/// <paramref name="IsDirectory"/> is false for a link to a directory, which is
/// the answer <see cref="SafeWalk.Found"/> gives for the same entry.
/// </summary>
public readonly record struct UsageRow(string Path, bool IsDirectory, bool IsLink, Usage Usage);

/// <summary>
/// The rows of "what is using the space here", and the folder's own total.
///
/// **The total is not the caller's to add up.** Summing the rows reaches the
/// same figure whenever there are rows, but a folder that could not be listed
/// at all has none to sum, and that is exactly the case a listing must not
/// show as an empty folder.
/// </summary>
public readonly record struct UsageListing(IReadOnlyList<UsageRow> Rows, Usage Total);

/// <summary>
/// Recursive sizes, on the one walk that does not follow links out of the tree.
///
/// **The summing a setting has been waiting for.** `FolderSizeMode.ContentSize`
/// has been in the settings model since it was written, and the settings
/// dialog cannot reach it: the metadata providers only count entries, and the
/// view model says so in a comment — "needs recursive summing in the metadata
/// provider, which does not exist". This is that summing, in Core, where the
/// Size column and a listing of what is using the space can both reach it.
///
/// **A link is one entry, and never a folder.** <see cref="SafeWalk"/> reports
/// every link with <c>IsDirectory</c> false, so a link to a directory is
/// counted here as a file of no size. Both platform
/// <see cref="IPropertiesProvider.MeasureAsync"/> walks count that same link as
/// a folder — Windows tests the Directory attribute, and .NET's Unix
/// <c>FileSystemEntry.IsDirectory</c> stats through the link — so a figure from
/// one and a figure from the other are not interchangeable, and this does not
/// replace them. Those answer "how big is this one thing" for a dialog; this
/// answers it for every child of a folder at once, and counts what it could not
/// read, which neither of those does.
///
/// Progress is reported on the thread doing the walking, in order. A caller
/// that hands in an <c>IProgress</c> marshalling to another thread owns what
/// happens after that.
/// </summary>
public static class SpaceUsage
{
    /// <summary>
    /// Entries between progress reports. **A report per entry floods whatever
    /// is listening and is invisible anyway** — the Windows measure settled on
    /// 256 for the same reason. The Linux one uses 500, so matching either is a
    /// choice rather than consistency; this follows the smaller.
    /// </summary>
    private const int ReportEvery = 256;

    /// <summary>
    /// Everything under <paramref name="root"/>, counted once.
    ///
    /// A link found on the way is counted where it stands and never followed,
    /// so a folder holding a link to a home directory reports its own size
    /// rather than the home directory's, and one pointing at an ancestor
    /// finishes.
    ///
    /// **The root handed in is the exception, and deliberately so.**
    /// <see cref="SafeWalk.Descend"/> pushes its root without testing it — as
    /// <see cref="Archives"/> also states for the row a person picked — so
    /// measuring a link measures what it points at. Asking about a folder is
    /// asking about the folder it opens.
    /// </summary>
    public static Usage Measure(string root, IProgress<SizeProgress>? progress, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        var folders = 0;
        var unreadable = 0;
        var sinceReport = 0;
        var reported = false;

        foreach (var found in SafeWalk.Descend(root, ct, _ => unreadable++))
        {
            if (found.IsDirectory) folders++;
            else
            {
                files++;
                bytes += found.Length;
            }

            if (++sinceReport < ReportEvery) continue;

            sinceReport = 0;
            reported = true;
            progress?.Report(new SizeProgress(bytes, files, folders));
        }

        var total = new Usage(bytes, files, folders, unreadable);

        // The throttle may have just reported this very figure, on a folder
        // whose entries happened to land on a multiple of ReportEvery; and on
        // an empty folder it reported nothing at all. Either way a caller gets
        // exactly one report of the total: **a listener that switched to
        // "measuring…" has to be told when it is over.**
        if (sinceReport != 0 || !reported) progress?.Report(total.Counted);

        return total;
    }

    /// <summary>
    /// One row per child of <paramref name="folder"/>, and what the folder
    /// comes to.
    ///
    /// **Rows come back in the order the folder lists them**, not biggest
    /// first: a pane sorts its own rows, and a listing that arrived pre-sorted
    /// would fight the column the person clicked.
    ///
    /// A child that is a file is its own length and nothing more — no walk, no
    /// stat, because the listing already read it.
    ///
    /// A folder that cannot be listed gives no rows and a total saying one
    /// thing could not be read, which is what <see cref="Measure"/> answers for
    /// the same path. A folder that is not there answers the same way, for the
    /// same reason: neither yielded a listing, and a caller wanting to tell
    /// them apart has <c>Directory.Exists</c>.
    /// </summary>
    public static UsageListing Underneath(
        string folder, IProgress<SizeProgress>? progress, CancellationToken ct)
    {
        // Before the listing, not only between the children: an already
        // cancelled call on an empty folder reached the return and handed back
        // a listing, where Measure on the same token threw.
        ct.ThrowIfCancellationRequested();

        var rows = new List<UsageRow>();

        // Everything so far, so the progress a caller sees climbs across the
        // whole folder rather than restarting at each child. It ends as the
        // folder's total, because every row is added to it.
        var done = new Usage();

        IEnumerable<FileSystemInfo> children;

        try
        {
            children = new DirectoryInfo(folder).EnumerateFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var nothing = new Usage(0, 0, 0, 1);

            progress?.Report(nothing.Counted);

            return new UsageListing(rows, nothing);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            var link = (child.Attributes & FileAttributes.ReparsePoint) != 0;
            UsageRow row;

            if (child is DirectoryInfo && !link)
            {
                var inside = Measure(child.FullName, new Relative(progress, done), ct);

                row = new UsageRow(
                    child.FullName,
                    IsDirectory: true,
                    IsLink: false,
                    inside with { Folders = inside.Folders + 1 });
            }
            else
            {
                // A link is counted where it stands, as the walk counts one:
                // following it is how a measurement of this folder becomes a
                // measurement of somewhere else. FileInfo.Length follows one,
                // so the length is taken only when this is not a link.
                var length = link || child is not FileInfo file ? 0 : file.Length;

                row = new UsageRow(
                    child.FullName,
                    IsDirectory: false,
                    IsLink: link,
                    new Usage(length, Files: 1, Folders: 0, Unreadable: 0));
            }

            rows.Add(row);

            done = new Usage(
                done.Bytes + row.Usage.Bytes,
                done.Files + row.Usage.Files,
                done.Folders + row.Usage.Folders,
                done.Unreadable + row.Usage.Unreadable);

            progress?.Report(done.Counted);
        }

        // Nothing in the loop reported, because the loop did not run.
        if (rows.Count == 0) progress?.Report(done.Counted);

        return new UsageListing(rows, done);
    }

    /// <summary>
    /// Adds what is already counted to what a child's own measure reports, so
    /// the caller sees one climbing total instead of a figure that drops back
    /// to zero at every child.
    /// </summary>
    private sealed class Relative(IProgress<SizeProgress>? outer, Usage sofar) : IProgress<SizeProgress>
    {
        public void Report(SizeProgress value)
            => outer?.Report(new SizeProgress(
                sofar.Bytes + value.Bytes,
                sofar.Files + value.Files,
                sofar.Folders + value.Folders));
    }
}
