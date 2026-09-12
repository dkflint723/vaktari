using Vaktari.Core.FileSystem;

namespace Vaktari.Ui;

/// <summary>
/// What is using the space in one folder: a row per child, carrying what that
/// child comes to with everything underneath it.
///
/// **The measuring is Core's and the shape is the pane's.** Every listing source
/// here yields batches of <see cref="FileEntry"/>, so sorting, filtering, the
/// three layouts and the whole selection machinery run unchanged and know
/// nothing about where the rows came from. A child's recursive total arrives in
/// <see cref="FileEntry.Length"/> with <see cref="EntryFlags.Measured"/> beside
/// it, which is what tells the size cell, the sort, the bands and the status bar
/// that this folder's length is real.
///
/// Shaped as the same <see cref="IAsyncEnumerable{T}"/> the filesystem provider
/// returns, like <see cref="ComputerListing"/> beside it.
/// </summary>
public static class SpaceListing
{
    /// <summary>
    /// One batch, because <see cref="SpaceUsage.Underneath"/> answers all at
    /// once. The walk runs on the pool: it is every tree under every child, and
    /// on a home directory that is seconds rather than milliseconds.
    ///
    /// Progress is raised on the walking thread, so a caller binding anything to
    /// it owns the marshalling — the same rule the pane already follows for its
    /// own flush loop.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
        string folder,
        bool includeHidden,
        IProgress<SizeProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var listing = await Task.Run(
            () => SpaceUsage.Underneath(folder, progress, ct), ct).ConfigureAwait(false);

        yield return Build(listing, includeHidden);
    }

    /// <summary>
    /// The rows, from the measurement.
    ///
    /// Separated from the enumeration so it can be tested without walking a real
    /// tree — what is interesting is which rows survive and what they carry, not
    /// the plumbing.
    ///
    /// **Hidden children are dropped here, as the search listing drops them.**
    /// Every real provider filters through its own predicate and the pane
    /// expects them already gone; nothing downstream re-checks it, so a listing
    /// that skipped this would show dotfiles beside a folder hiding them and
    /// leave Ctrl+H doing nothing. The folder's TOTAL still counts them, because
    /// a total that changed with a view setting would be a claim about the disk
    /// that the disk does not make.
    /// </summary>
    internal static List<FileEntry> Build(UsageListing listing, bool includeHidden)
    {
        var rows = new List<FileEntry>(listing.Rows.Count);

        foreach (var row in listing.Rows)
        {
            if (row.IsConcealed && !includeHidden) continue;

            var flags = EntryFlags.None;

            if (row.IsDirectory) flags |= EntryFlags.Directory;
            if (row.IsLink) flags |= EntryFlags.Symlink;
            if (row.IsConcealed) flags |= EntryFlags.Hidden;

            // **Measured on a folder and on nothing else.** A file's length is
            // its own and needs no saying; the flag is what makes a FOLDER's
            // length real to the size cell, the sort, the bands and the status
            // bar, each of which is right to ignore a directory's length
            // everywhere else.
            if (row.IsDirectory) flags |= EntryFlags.Measured;

            // The timestamps are left at default deliberately: a measurement
            // carries no write time, and default is below the Unix epoch, which
            // the date converter already renders as an empty cell — the same
            // answer This PC's drives and the Recent rows give.
            rows.Add(new FileEntry(
                System.IO.Path.GetFileName(row.Path),
                row.Path,
                row.Usage.Bytes,
                default,
                flags));
        }

        return rows;
    }
}
