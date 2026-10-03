using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Core's half of the folder hand-over: the engines' shared lease
/// (<see cref="FolderMoves"/>), the path arithmetic that carries a tab after
/// its folder, and the two watches that can still be reading the disk when
/// they are let go of (rename-notes, review finding 3).
/// </summary>
public sealed class FolderHandoverCoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-handover-" + Guid.NewGuid().ToString("N")[..12]);

    public FolderHandoverCoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp dir is not worth failing over */ }
    }

    private string In(params string[] parts) => Path.Combine([_root, .. parts]);

    // ---- the lease ------------------------------------------------------------

    /// <summary>What a hand-over was told, in order.</summary>
    private sealed class Recording : IFolderHandover
    {
        public List<string> Said { get; } = [];

        public ValueTask<IFolderLease> ReleaseAsync(IReadOnlyList<string> folders, CancellationToken ct)
        {
            Said.Add("release " + string.Join(",", folders.Select(Path.GetFileName)));
            return ValueTask.FromResult<IFolderLease>(new Lease(this));
        }

        public void Followed(string from, string to)
            => Said.Add($"followed {Path.GetFileName(from)}>{Path.GetFileName(to)}");

        private sealed class Lease(Recording owner) : IFolderLease
        {
            public void Moved(string from, string to)
                => owner.Said.Add($"moved {Path.GetFileName(from)}>{Path.GetFileName(to)}");

            public void Gone(string folder) => owner.Said.Add($"gone {Path.GetFileName(folder)}");

            public ValueTask DisposeAsync()
            {
                owner.Said.Add("end");
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Let go of first, followed after, and ended.</summary>
    [Fact]
    public async Task A_folder_that_moved_is_followed_and_the_hold_ended()
    {
        var from = Directory.CreateDirectory(In("one")).FullName;
        var to = In("two");
        var handover = new Recording();

        await FolderMoves.RunAsync(handover, [(from, to)], () =>
        {
            Directory.Move(from, to);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(["release one", "moved one>two", "end"], handover.Said);
    }

    /// <summary>
    /// **A failed move is told neither**, so whatever let go is put back where
    /// it was, and the failure still reaches the caller.
    /// </summary>
    [Fact]
    public async Task A_folder_that_did_not_move_is_put_back_as_it_was()
    {
        var from = Directory.CreateDirectory(In("one")).FullName;
        var handover = new Recording();

        await Assert.ThrowsAsync<IOException>(async () =>
            await FolderMoves.RunAsync(handover, [(from, In("two"))],
                () => throw new IOException("refused"), CancellationToken.None));

        Assert.Equal(["release one", "end"], handover.Said);
    }

    /// <summary>A folder sent to the bin is gone — asked of the disk, not
    /// read from the call, which for the shell answers for a whole batch.</summary>
    [Fact]
    public async Task A_binned_folder_is_gone_only_when_it_is()
    {
        var binned = Directory.CreateDirectory(In("binned")).FullName;
        var kept = Directory.CreateDirectory(In("kept")).FullName;
        var handover = new Recording();

        await FolderMoves.RunAsync(handover, [(binned, null), (kept, null)], () =>
        {
            Directory.Delete(binned);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(["release binned,kept", "gone binned", "end"], handover.Said);
    }

    /// <summary>No hand-over is exactly what there was before one: the work,
    /// and nothing else.</summary>
    [Fact]
    public async Task Without_a_handover_the_work_runs_alone()
    {
        var ran = false;

        await FolderMoves.RunAsync(null, [(In("x"), In("y"))], () =>
        {
            ran = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.True(ran);
    }

    /// <summary>
    /// A case-only rename is the same folder by both names on a
    /// case-insensitive disk, so the disk cannot say it moved; the call's own
    /// success does.
    /// </summary>
    [WindowsFact]
    public async Task A_case_only_rename_is_followed_when_it_worked()
    {
        var from = Directory.CreateDirectory(In("photos")).FullName;
        var to = In("Photos");
        var handover = new Recording();

        await FolderMoves.RunAsync(handover, [(from, to)], () => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Contains("moved photos>Photos", handover.Said);
    }

    // ---- the path arithmetic --------------------------------------------------

    /// <summary>
    /// **Renaming "one" must not touch "onetwo".** A prefix is not a parent:
    /// the comparison ends at a separator (PathRules.Contains), or a tab in a
    /// folder that merely starts with the same letters would be carried
    /// somewhere that does not exist.
    /// </summary>
    [Fact]
    public void A_sibling_whose_name_starts_the_same_is_left_alone()
    {
        var one = In("one");

        Assert.Null(PathRules.Rebase(In("onetwo"), one, In("three")));
        Assert.Null(PathRules.Rebase(In("onetwo", "deep"), one, In("three")));
    }

    [Fact]
    public void The_folder_and_everything_under_it_follow()
    {
        var one = In("one");
        var three = In("three");

        Assert.Equal(three, PathRules.Rebase(one, one, three));
        Assert.Equal(Path.Combine(three, "a", "b.txt"), PathRules.Rebase(In("one", "a", "b.txt"), one, three));
    }

    // ---- watches still reading when they are let go of ------------------------

    /// <summary>
    /// **Disposing a polled watch does not stop a read already under way.**
    /// A folder renamed straight after its watch was let go of could still
    /// meet that read's handle. A read is held mid-way here on its own report,
    /// which happens inside it: idle is not said until it has finished.
    /// </summary>
    [Fact]
    public async Task A_polled_watch_is_not_idle_until_its_read_has_finished()
    {
        var folder = Directory.CreateDirectory(In("polled")).FullName;
        using var inRead = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var watch = new PollingWatch(folder, _ =>
        {
            inRead.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }, Timeout.InfiniteTimeSpan);

        File.WriteAllText(Path.Combine(folder, "new.txt"), "x");

        var reading = OwnThread.Run(watch.Tick);

        Assert.True(inRead.Wait(TimeSpan.FromSeconds(30)), "the read never reported what it found");

        watch.Dispose();

        Assert.False(await watch.WhenIdleAsync(TimeSpan.FromMilliseconds(200)),
                     "the watch said it was idle with a read still inside the folder");

        release.Set();

        Assert.True(reading.Finished(TimeSpan.FromSeconds(30)));
        Assert.True(await watch.WhenIdleAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>A watch that opens on the folder above, held mid-open.</summary>
    private sealed class Gated(ManualResetEventSlim opening, ManualResetEventSlim release) : IFileSystemProvider
    {
        public int Disposed;

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            opening.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new Off(this);
        }

        private sealed class Off(Gated owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner.Disposed);
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>
    /// The same for a wait on a folder that is not there: its look opens a
    /// watch on the nearest folder above, which for a tab inside a folder
    /// being renamed is inside it too. Not idle until the look is over — and
    /// what it opened is let go of, because the wait was disposed.
    /// </summary>
    [Fact]
    public async Task A_wait_for_a_folder_is_not_idle_until_its_look_has_finished()
    {
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fs = new Gated(opening, release);

        var wait = FolderReturnWatch.Start(fs, In("gone", "x"), () => { });

        Assert.True(opening.Wait(TimeSpan.FromSeconds(30)), "the wait never looked for a folder above");

        wait.Dispose();

        Assert.False(await wait.WhenIdleAsync(TimeSpan.FromMilliseconds(200)),
                     "the wait said it was idle with a look still opening a watch");

        release.Set();

        Assert.True(await wait.WhenIdleAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, Volatile.Read(ref fs.Disposed));
    }
}
