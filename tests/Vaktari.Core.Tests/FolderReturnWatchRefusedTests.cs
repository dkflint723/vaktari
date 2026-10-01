using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **A folder above that goes between the look and the watch left the wait
/// watching nothing** (batch-0.11.2c QA, round 4). NearestAbove found it, it
/// was deleted before the watch was set up, the watch was refused (and so was
/// the PollingWatch fallback, which reads it first), and the wait ended with
/// nothing watched — and the second look ran only while something was. Nothing
/// ever asked again: the folder came back and the pane stayed on its error
/// until F5. Measured over the real watchers with rm -rf a; mkdir -p a/b while
/// a wait sat on a/b/c: 47 rounds in 50 on Linux, 20 in 20 over UNC on
/// Windows. The first test is QA's own deterministic repro.
///
/// And where every folder above is refused, up to the root — a drive or a
/// share that does not answer — the wait watches nothing, and asks again on a
/// slow timer, or when asked.
/// </summary>
public sealed class FolderReturnWatchRefusedTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-refused").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Deletes a folder at the moment it is asked to watch it, once, and refuses.</summary>
    private sealed class Vanishing(string vanishes) : IFileSystemProvider
    {
        private bool _gone;
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Live = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (!_gone && path == vanishes)
            {
                _gone = true;
                Directory.Delete(path, recursive: true);
                throw new DirectoryNotFoundException(path);
            }

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

            var entry = (path, onChange);
            Live.Add(entry);
            return new Off(() => Live.Remove(entry));
        }

        private sealed class Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>Refuses every watch while <see cref="Refuse"/> is set.</summary>
    private sealed class Refusing : IFileSystemProvider
    {
        public volatile bool Refuse = true;
        public int Live;

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (Refuse) throw new IOException("no watcher can be started here");

            Interlocked.Increment(ref Live);
            return new Off(this);
        }

        private sealed class Off(Refusing owner) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner.Live);
            }
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    [Fact]
    public void A_folder_above_gone_before_its_watch_is_set_up_moves_the_wait_further_up()
    {
        var a = Path.Combine(_root, "a");
        var target = Path.Combine(a, "b");
        Directory.CreateDirectory(a);

        var fs = new Vanishing(a);
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++);

        Assert.Equal(_root, wait.Watching);

        Directory.CreateDirectory(a);
        var (_, heard) = Assert.Single(fs.Live);
        heard(new FileSystemChange(ChangeKind.Added, a));

        Directory.CreateDirectory(target);
        foreach (var (path, h) in fs.Live.ToList())
            if (path == a) h(new FileSystemChange(ChangeKind.Added, target));

        Assert.Equal(1, told);
    }

    /// <summary>
    /// A disk on which only <c>phantom</c> answers that it is there — and it
    /// is not, so it can be neither watched nor read: refused all the way up.
    /// Once <see cref="Real"/> is set the disk answers for itself.
    /// </summary>
    private sealed class Disk(string phantom)
    {
        public volatile bool Real;

        public bool Exists(string path) => Real ? Directory.Exists(path) : PathRules.Same(path, phantom);
    }

    /// <summary>
    /// **A folder above that is there but refuses is looked past.** Neither
    /// watched nor read — no permission, say — it is found again by the next
    /// look, and the wait goes to the folder above it rather than trying it
    /// again until its tries run out. And it stays there: the next look finds
    /// the refused folder again, which is no reason to look a third time (a
    /// second look comparing with the folder watched, not the one found, spun
    /// in a loop here, so the wait is made off the test's thread and given a
    /// bound).
    /// </summary>
    [Fact]
    public async Task A_folder_above_that_is_there_but_refuses_is_looked_past()
    {
        var phantom = Path.Combine(_root, "phantom");
        var target = Path.Combine(phantom, "x");
        var fs = new Refusing();

        bool Exists(string path) => PathRules.Same(path, phantom) || Directory.Exists(path);

        // Watches refused everywhere; _root, which is real, can still be read
        // on a timer, and phantom cannot.
        var making = Task.Run(() => new FolderReturnWatch(fs, target, () => { }, TimeSpan.FromHours(1), Exists, Timeout.InfiniteTimeSpan));

        Assert.True(await Task.WhenAny(making, Task.Delay(TimeSpan.FromSeconds(30))) == making, "the wait never stopped looking");

        using var wait = await making;
        var checks = wait.Checks;

        Assert.Equal(_root, wait.Watching);
        Assert.InRange(checks, 1, 2);
    }

    [Fact]
    public void Refused_up_to_the_root_it_watches_nothing_and_a_recheck_recovers()
    {
        var phantom = Path.Combine(_root, "phantom");
        var target = Path.Combine(phantom, "x");
        var disk = new Disk(phantom);
        var fs = new Refusing();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++, null, disk.Exists, Timeout.InfiniteTimeSpan);

        Assert.Null(wait.Watching);
        Assert.Equal(0, fs.Live);

        // The share answers again, with the folder back.
        Directory.CreateDirectory(target);
        disk.Real = true;
        fs.Refuse = false;

        Assert.Equal(0, told);

        wait.Recheck();

        Assert.Equal(1, told);
        Assert.Equal(0, fs.Live);
    }

    [PoolWorkersFact]
    public async Task Refused_up_to_the_root_it_asks_again_on_a_timer()
    {
        var phantom = Path.Combine(_root, "phantom");
        var target = Path.Combine(phantom, "x");
        var disk = new Disk(phantom);
        var fs = new Refusing();
        using var back = new SemaphoreSlim(0);

        using var wait = new FolderReturnWatch(fs, target, () => back.Release(), null, disk.Exists, TimeSpan.FromMilliseconds(50));

        Assert.Null(wait.Watching);

        Directory.CreateDirectory(target);
        disk.Real = true;
        fs.Refuse = false;

        Assert.True(await back.WaitAsync(TimeSpan.FromSeconds(30)), "nothing asked again once the share answered");
    }

    /// <summary>
    /// **With nothing above at all** — a drive letter gone, a share root that
    /// does not answer — there is nothing to watch from the very first look,
    /// and the wait asks again on the same timer.
    /// </summary>
    [PoolWorkersFact]
    public async Task With_nothing_above_at_all_it_asks_again_on_a_timer()
    {
        var target = Path.Combine(_root, "phantom", "x");
        var disk = new Disk(Path.Combine(_root, "elsewhere"));
        var fs = new Refusing { Refuse = false };
        using var back = new SemaphoreSlim(0);

        using var wait = new FolderReturnWatch(fs, target, () => back.Release(), null, disk.Exists, TimeSpan.FromMilliseconds(50));

        Assert.Null(wait.Watching);
        Assert.Equal(0, fs.Live);

        Directory.CreateDirectory(target);
        disk.Real = true;

        Assert.True(await back.WaitAsync(TimeSpan.FromSeconds(30)), "nothing asked again once the drive was back");
    }

    /// <summary>
    /// With the parts above answering again but the folder still missing, the
    /// timer's look finds something to watch. The timer goes on looking
    /// (batch-0.11.2d QA, round 5: a watch is not proof of hearing), and each
    /// look lets the watch before it go: one watch open, however many looks.
    /// </summary>
    [PoolWorkersFact]
    public async Task Refused_up_to_the_root_it_watches_again_once_it_can()
    {
        var phantom = Path.Combine(_root, "phantom");
        var target = Path.Combine(phantom, "x");
        var disk = new Disk(phantom);
        var fs = new Refusing();

        using var wait = new FolderReturnWatch(fs, target, () => { }, null, disk.Exists, TimeSpan.FromMilliseconds(50));

        Assert.Null(wait.Watching);

        Directory.CreateDirectory(phantom);
        disk.Real = true;
        fs.Refuse = false;

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (wait.Watching is null && DateTime.UtcNow < until) await Task.Delay(20);

        Assert.Equal(phantom, wait.Watching);

        var checks = wait.Checks;

        Assert.True(SpinWait.SpinUntil(() => wait.Checks > checks, TimeSpan.FromSeconds(30)), "the timer stopped once something was watched");
        Assert.Equal(phantom, wait.Watching);
        // One open, or two for the moment a look swaps them.
        Assert.InRange(fs.Live, 1, 2);
    }
}
