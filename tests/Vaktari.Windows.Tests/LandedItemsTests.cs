using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a finished copy or move says it actually put in the destination.
///
/// **A paste left its arrivals unselected because nothing could name them.** A
/// handle carried <see cref="IOperationHandle.Paths"/> — the sources and the
/// destination, which is what was SENT — and the pane's only way to guess an
/// arrival was destination plus source name. That guess is wrong the moment a
/// conflict is answered Keep both, which renames the arriving file, and wrong
/// again when an item is skipped or fails and never arrives at all. So after
/// copying twenty files into a folder you had to find them yourself.
///
/// Real files and a real engine, like the rest of these tests: a handle built
/// by hand would prove only that the property exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LandedItemsTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    /// <summary>The everyday case: two files into an empty folder, and the two
    /// paths a pane would select.</summary>
    [WindowsFact]
    public async Task A_copy_reports_where_each_named_item_landed()
    {
        using var tree = new TempTree();

        var one = tree.Write("src/one.txt", "1");
        var two = tree.Write("src/two.txt", "2");
        var destination = tree.Dir("dst");

        var handle = new WindowsFileOperations()
            .Copy([one, two], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(
            [tree.At("dst", "one.txt"), tree.At("dst", "two.txt")],
            handle.Landed.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The finding's own reason this is not one line in the UI: Keep both
    /// renames what arrives, so the name that lands is not the name that was
    /// sent and only the engine ever sees it.
    /// </summary>
    [WindowsFact]
    public async Task A_kept_both_arrival_is_reported_under_the_name_it_arrived_with()
    {
        using var tree = new TempTree();

        var source = tree.Write("src/report.txt", "new");
        tree.Write("dst/report.txt", "old");

        var handle = new WindowsFileOperations()
            .Copy([source], tree.At("dst"), Always(ConflictResolution.KeepBoth));

        await handle.Completion;

        Assert.Equal(tree.At("dst", "report (2).txt"), Assert.Single(handle.Landed));
    }

    /// <summary>
    /// **Only what arrived.** One of two files is held open with no sharing, so
    /// the copy of it fails while its neighbour goes through. Naming the
    /// casualty as well would point the pane's selection — and the next Delete
    /// with it — at whatever already occupied that path.
    /// </summary>
    [WindowsFact]
    public async Task A_partial_failure_reports_only_the_item_that_arrived()
    {
        using var tree = new TempTree();

        var fine = tree.Write("src/fine.txt", "ok");
        var locked = tree.Write("src/locked.txt", "held");
        var destination = tree.Dir("dst");

        using var hold = new FileStream(
            locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var handle = new WindowsFileOperations()
            .Copy([fine, locked], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        // The batch finished and named the casualty, which is what makes the
        // single landing below mean "one of two" rather than "one was sent".
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Single(handle.Problems);

        Assert.Equal(tree.At("dst", "fine.txt"), Assert.Single(handle.Landed));
    }

    /// <summary>
    /// **The spelling that lands is the folder's, not the source's, and the
    /// report carries the source's.** Copying report.txt over an existing
    /// Report.TXT writes through the directory entry that is already there and
    /// leaves it named Report.TXT, while the engine reports the path it was
    /// asked to write — dst\report.txt. Ordinal called those two files two, and
    /// the pane matching arrivals to rows that way left the arrival unselected.
    ///
    /// So the contract between an engine and a listing is not string equality
    /// but <see cref="PathRules.Comparer"/>, which is what
    /// PaneViewModel.Reselect now uses. Pinned by mutating
    /// <see cref="PathRules.Comparison"/> to StringComparison.Ordinal.
    /// </summary>
    [WindowsFact]
    public async Task An_overwrite_reports_the_row_the_listing_shows()
    {
        using var tree = new TempTree();

        var source = tree.Write("src/report.txt", "new");
        tree.Write("dst/Report.TXT", "old");
        var destination = tree.At("dst");

        var handle = new WindowsFileOperations()
            .Copy([source], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        var rows = new List<string>();

        await foreach (var batch in new WindowsFileSystemProvider()
                           .EnumerateAsync(destination, new ListingOptions(), default))
            rows.AddRange(batch.Select(e => e.FullPath));

        var landed = new HashSet<string>(handle.Landed, PathRules.Comparer);

        Assert.Equal(rows, rows.Where(landed.Contains).ToList());
    }

    /// <summary>
    /// **What the placement of the report costs, written down.** The call sits
    /// past the item loop and inside the try, on the same control-flow line as
    /// the undo — so a run cancelled at a clash reports nothing at all, even
    /// though the files ahead of the clash are on disk. The pane's answer to
    /// nothing is to leave the selection alone, which is survivable; remembering
    /// it here is what makes moving the call a deliberate act rather than an
    /// accident.
    ///
    /// Pinned by adding a per-item <c>handle.Arrived([target]);</c> beside the
    /// <c>landings.Add</c> in the loop, which reports the first file.
    /// </summary>
    [WindowsFact]
    public async Task A_cancelled_copy_reports_nothing()
    {
        using var tree = new TempTree();

        var fine = tree.Write("src/fine.txt", "ok");
        var clash = tree.Write("src/clash.txt", "new");
        tree.Write("dst/clash.txt", "old");
        var destination = tree.At("dst");

        var handle = new WindowsFileOperations()
            .Copy([fine, clash], destination, Always(ConflictResolution.Cancel));

        await handle.Completion;

        // The first file really did arrive, which is what makes the empty
        // report below a consequence and not a tautology.
        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.True(File.Exists(tree.At("dst", "fine.txt")));

        Assert.Empty(handle.Landed);
    }

    /// <summary>
    /// A skip arrives nowhere, and the file sitting at that name is somebody
    /// else's — the one the person just chose to leave alone.
    /// </summary>
    [WindowsFact]
    public async Task A_skipped_item_lands_nothing()
    {
        using var tree = new TempTree();

        var source = tree.Write("src/report.txt", "new");
        tree.Write("dst/report.txt", "old");

        var handle = new WindowsFileOperations()
            .Copy([source], tree.At("dst"), Always(ConflictResolution.Skip));

        await handle.Completion;

        Assert.Empty(handle.Landed);
    }

    /// <summary>A move lands at the destination the same way a copy does — the
    /// pane showing the destination has new rows either way.</summary>
    [WindowsFact]
    public async Task A_move_reports_the_destination_it_landed_at()
    {
        using var tree = new TempTree();

        var source = tree.Write("src/one.txt", "1");
        var destination = tree.Dir("dst");

        var handle = new WindowsFileOperations()
            .Move([source], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(tree.At("dst", "one.txt"), Assert.Single(handle.Landed));
    }

    /// <summary>
    /// A GUARD, and it says so: no one-line change to this fix can make it
    /// fail, because a delete never reaches the line that reports landings at
    /// all. It is here to say out loud what "empty" means downstream — the pane
    /// keeps the selection it had — so that a later engine which starts
    /// reporting from the delete loop breaks a test rather than the selection.
    ///
    /// Through Delete rather than Trash so a green run leaves nothing in the
    /// developer's own bin.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_lands_nothing()
    {
        using var tree = new TempTree();

        var handle = new WindowsFileOperations().Delete([tree.Write("doomed.txt")]);

        await handle.Completion;

        Assert.Empty(handle.Landed);
    }
}
