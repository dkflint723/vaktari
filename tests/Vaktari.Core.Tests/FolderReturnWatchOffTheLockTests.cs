using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **The wait's lock was held across the disk** (batch-0.11.2c QA, round 4).
/// Whether the folder was back, which folder above was there, and opening a
/// watch on it were all asked under the lock that Dispose takes — and Dispose
/// runs on the window's thread, for a navigation, a tab closed, the window
/// closed. Directory.Exists on a server that did not answer took 42 s, and so
/// would the window. Here the disk, or the watch, is held on a gate: Dispose
/// and an arrival heard meanwhile must both return while it is held, and
/// whatever the held check opens afterwards must be let go.
/// </summary>
public sealed class FolderReturnWatchOffTheLockTests : IDisposable
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-offlock").FullName;

    private static async Task<bool> Within(Task task) => await Task.WhenAny(task, Task.Delay(Prompt)) == task;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Counts the watches open; one may be made to wait on a gate.</summary>
    private sealed class Provider : IFileSystemProvider
    {
        public int Live;

        public volatile bool Hold;
        public readonly ManualResetEventSlim Held = new();
        public readonly ManualResetEventSlim Gate = new();

        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (Hold)
            {
                Held.Set();
                Gate.Wait();
            }

            lock (Given) Given.Add((path, onChange));
            Interlocked.Increment(ref Live);
            return new Off(this);
        }

        private sealed class Off(Provider owner) : IDisposable
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

    /// <summary>
    /// The real disk, except that while <see cref="Hold"/> is set the answer
    /// for <c>held</c> is read and then kept back on a gate — a server slow to
    /// answer, whose answer is out of date by the time it arrives.
    /// </summary>
    private sealed class SlowDisk(string held)
    {
        public volatile bool Hold;
        public readonly ManualResetEventSlim Held = new();
        public readonly ManualResetEventSlim Gate = new();

        public bool Exists(string path)
        {
            var answer = Directory.Exists(path);

            if (Hold && PathRules.Same(path, held))
            {
                Hold = false;
                Held.Set();
                Gate.Wait();
            }

            return answer;
        }
    }

    [Fact]
    public async Task Disposed_while_a_watch_is_being_opened_it_returns_at_once_and_the_watch_is_let_go()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var told = 0;

        var wait = new FolderReturnWatch(fs, target, () => Interlocked.Increment(ref told));
        Assert.Equal(1, fs.Live);

        fs.Hold = true;
        var recheck = Task.Run(wait.Recheck);

        try
        {
            Assert.True(fs.Held.Wait(Prompt), "the recheck never reached the watch");

            // An arrival heard on the watcher's own thread meanwhile does not
            // wait for the held check either.
            Directory.CreateDirectory(target);
            var heard = Task.Run(() => fs.Given[0].Heard(new FileSystemChange(ChangeKind.Added, target)));
            Assert.True(await Within(heard), "an arrival waited on the held watch");

            var disposing = Task.Run(wait.Dispose);
            Assert.True(await Within(disposing), "Dispose waited on the held watch");
        }
        finally
        {
            fs.Hold = false;
            fs.Gate.Set();
        }

        Assert.True(await Within(recheck));

        Assert.Equal(0, fs.Live);
        Assert.Null(wait.Watching);
        Assert.Equal(0, told);
    }

    [Fact]
    public async Task Disposed_while_the_disk_is_slow_it_returns_at_once()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var disk = new SlowDisk(target);
        var told = 0;

        var wait = new FolderReturnWatch(fs, target, () => Interlocked.Increment(ref told), null, disk.Exists, Timeout.InfiniteTimeSpan);

        disk.Hold = true;
        var recheck = Task.Run(wait.Recheck);

        try
        {
            Assert.True(disk.Held.Wait(Prompt), "the recheck never asked the disk");

            var disposing = Task.Run(wait.Dispose);
            Assert.True(await Within(disposing), "Dispose waited on the disk");
        }
        finally
        {
            disk.Gate.Set();
        }

        Assert.True(await Within(recheck));

        Assert.Equal(0, fs.Live);
        Assert.Equal(0, told);
    }

    /// <summary>
    /// **An arrival heard while a check is held is not lost.** It is left to
    /// the check already running — here one asked for by an earlier arrival
    /// of the same name, a false start, whose answer was read before the
    /// folder came back and is out of date. With the folder above unchanged
    /// that check has no reason to look twice; it does because a second was
    /// asked for while it ran, and finds the folder.
    /// </summary>
    [Fact]
    public async Task An_arrival_heard_while_the_disk_is_slow_is_looked_at_after()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var disk = new SlowDisk(target);
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => Interlocked.Increment(ref told), null, disk.Exists, Timeout.InfiniteTimeSpan);
        var heard = fs.Given[0].Heard;

        disk.Hold = true;
        var first = Task.Run(() => heard(new FileSystemChange(ChangeKind.Added, target)));

        try
        {
            Assert.True(disk.Held.Wait(Prompt), "the first arrival never asked the disk");

            Directory.CreateDirectory(target);
            var second = Task.Run(() => heard(new FileSystemChange(ChangeKind.Added, target)));
            Assert.True(await Within(second), "an arrival waited on the slow disk");

            Assert.Equal(0, told);
        }
        finally
        {
            disk.Gate.Set();
        }

        Assert.True(await Within(first));

        Assert.Equal(1, told);
        Assert.Equal(0, fs.Live);
    }
}
