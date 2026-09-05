using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The Linux twin of the Windows landed-items tests, because a pane asks the
/// engine the same question on both platforms and would otherwise get an
/// answer on only one.
///
/// **A paste left its arrivals unselected because nothing could name them.** A
/// handle carried <see cref="IOperationHandle.Paths"/> — the sources and the
/// destination, which is what was SENT — and destination plus source name is
/// the arrival's name only when nothing was renamed on the way in. Keep both
/// renames it; a skip and a failure mean nothing arrived under it at all.
///
/// Like <see cref="FolderCopyTests"/> these run on any platform: the code under
/// test is path arithmetic and ordinary file I/O.
/// </summary>
public sealed class LandedItemsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-landed-" + Guid.NewGuid().ToString("N")[..8]);

    public LandedItemsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Dir(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Write(string relative, string content = "x")
    {
        var path = At(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The everyday case: two files into an empty folder, and the two
    /// paths a pane would select.</summary>
    [Fact]
    public async Task A_copy_reports_where_each_named_item_landed()
    {
        var one = Write("src/one.txt");
        var two = Write("src/two.txt");
        var destination = Dir("dst");

        var handle = new LinuxFileOperations()
            .Copy([one, two], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(
            [At("dst", "one.txt"), At("dst", "two.txt")],
            handle.Landed.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The finding's own reason this is not one line in the UI: Keep both
    /// renames what arrives, so the name that lands is not the name that was
    /// sent and only the engine ever sees it.
    /// </summary>
    [Fact]
    public async Task A_kept_both_arrival_is_reported_under_the_name_it_arrived_with()
    {
        var source = Write("src/report.txt", "new");
        Write("dst/report.txt", "old");

        var handle = new LinuxFileOperations()
            .Copy([source], At("dst"), Always(ConflictResolution.KeepBoth));

        await handle.Completion;

        Assert.Equal(At("dst", "report (1).txt"), Assert.Single(handle.Landed));
    }

    /// <summary>
    /// **Only what arrived.** A folder cannot be made where a file of that name
    /// already sits, so that root fails while its neighbour goes through — a
    /// failure both platforms produce, unlike a lock. Naming the casualty as
    /// well would point the pane's selection, and the next Delete with it, at
    /// the file that was already there.
    /// </summary>
    [Fact]
    public async Task A_partial_failure_reports_only_the_item_that_arrived()
    {
        var fine = Write("src/fine.txt");
        var folder = Dir("src", "blocked");
        var destination = Dir("dst");

        // The obstacle: a FILE where the folder wants to arrive.
        Write("dst/blocked");

        var handle = new LinuxFileOperations()
            .Copy([fine, folder], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        // The batch finished and named the casualty, which is what makes the
        // single landing below mean "one of two" rather than "one was sent".
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.NotEmpty(handle.Problems);

        Assert.Equal(At("dst", "fine.txt"), Assert.Single(handle.Landed));
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
    [Fact]
    public async Task A_cancelled_copy_reports_nothing()
    {
        var fine = Write("src/fine.txt");
        var clash = Write("src/clash.txt", "new");
        Write("dst/clash.txt", "old");
        var destination = At("dst");

        var handle = new LinuxFileOperations()
            .Copy([fine, clash], destination, Always(ConflictResolution.Cancel));

        await handle.Completion;

        // The first file really did arrive, which is what makes the empty
        // report below a consequence and not a tautology.
        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.True(File.Exists(At("dst", "fine.txt")));

        Assert.Empty(handle.Landed);
    }

    /// <summary>
    /// A skip arrives nowhere, and the file sitting at that name is somebody
    /// else's — the one the person just chose to leave alone.
    /// </summary>
    [Fact]
    public async Task A_skipped_item_lands_nothing()
    {
        var source = Write("src/report.txt", "new");
        Write("dst/report.txt", "old");

        var handle = new LinuxFileOperations()
            .Copy([source], At("dst"), Always(ConflictResolution.Skip));

        await handle.Completion;

        Assert.Empty(handle.Landed);
    }

    /// <summary>A move lands at the destination the same way a copy does — the
    /// pane showing the destination has new rows either way.</summary>
    [Fact]
    public async Task A_move_reports_the_destination_it_landed_at()
    {
        var source = Write("src/one.txt");
        var destination = Dir("dst");

        var handle = new LinuxFileOperations()
            .Move([source], destination, Always(ConflictResolution.Overwrite));

        await handle.Completion;

        Assert.Equal(At("dst", "one.txt"), Assert.Single(handle.Landed));
    }

    /// <summary>
    /// A GUARD, and it says so: no one-line change to this fix can make it
    /// fail, because a delete never reaches the line that reports landings at
    /// all. It is here to say out loud what "empty" means downstream — the pane
    /// keeps the selection it had — so that a later engine which starts
    /// reporting from the delete loop breaks a test rather than the selection.
    /// </summary>
    [Fact]
    public async Task A_delete_lands_nothing()
    {
        var handle = new LinuxFileOperations().Delete([Write("doomed.txt")]);

        await handle.Completion;

        Assert.Empty(handle.Landed);
    }
}
