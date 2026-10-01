using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>Run alone: the starvation test lowers the pool's minimum for the
/// whole process while it runs.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolSensitive
{
    public const string Name = "thread pool sensitive";
}

/// <summary>
/// **Waits on a dead share starved the thread pool** (batch-0.11.2e QA, round
/// 7). Every look a wait made on its timer, or for the places list, ran on a
/// pool thread, and on a dead share a look takes 21 to 42 s — longer than the
/// 30 s between looks, so back to back. With the pool's minimum at two (a
/// two-core machine), eight such waits cut a healthy wait to 11 of 30 looks
/// and kept unrelated pool work 2 s late; thirty-two stopped both. The pool
/// is where the window's folder loads run, and the same starvation failed two
/// of these tests on CI (run 36887240896). The looks that can wait on the disk
/// now run on threads of their own, and a wait whose looks are slow looks
/// less often.
/// </summary>
[Collection(ThreadPoolSensitive.Name)]
public sealed class FolderReturnWatchPoolTests : IDisposable
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pool").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Provider : IFileSystemProvider
    {
        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Off();

        private sealed class Off : IDisposable
        {
            public void Dispose()
            {
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
    /// QA's probe, as a test: thirty-two waits on a share that takes 2 s to
    /// say no, looking every 500 ms, beside a healthy wait looking every
    /// 100 ms, with the pool's minimum at two. The healthy wait keeps its
    /// looks, unrelated pool work runs at once, and no dead look ever ran on a
    /// pool thread.
    /// </summary>
    [PoolWorkersFact]
    public void Waits_on_a_dead_share_leave_the_pool_and_a_healthy_wait_alone()
    {
        const int Dead = 32;

        var dead = Path.Combine(_root, "dead-share");
        var onThePool = 0;

        // One slow answer a look — the wait's own folder — so that after the
        // first look the dead waits' looks come from their timers, which is
        // where the pool was used.
        bool Slow(string path)
        {
            if (!path.StartsWith(dead, StringComparison.Ordinal) || Path.GetFileName(path) != "x") return false;

            if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref onThePool);
            Thread.Sleep(2000);
            return false;
        }

        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        var waits = new List<FolderReturnWatch>();

        try
        {
            ThreadPool.SetMinThreads(2, minIo);

            for (var i = 0; i < Dead; i++)
                waits.Add(FolderReturnWatch.Start(new Provider(), Path.Combine(dead, $"w{i}", "x"), () => { }, null, Slow, TimeSpan.FromMilliseconds(500)));

            // Past every dead wait's first look (2 s), into its timer's.
            Thread.Sleep(3000);

            var healthy = new FolderReturnWatch(new Provider(), Path.Combine(_root, "gone", "x"), () => { }, null, _ => false, TimeSpan.FromMilliseconds(100));
            waits.Add(healthy);

            var before = healthy.Checks;
            var late = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                var queued = Stopwatch.StartNew();
                using var ran = new ManualResetEventSlim();
                var took = TimeSpan.Zero;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    took = queued.Elapsed;
                    ran.Set();
                });

                if (!ran.Wait(TimeSpan.FromSeconds(3))) took = TimeSpan.FromSeconds(3);
                if (took > late) late = took;

                Thread.Sleep(100);
            }

            var looks = healthy.Checks - before;

            Assert.Equal(0, Volatile.Read(ref onThePool));
            Assert.True(looks >= 10, $"the healthy wait looked {looks} times in 3 s at 100 ms");
            Assert.True(late < TimeSpan.FromSeconds(1), $"unrelated pool work waited {late.TotalMilliseconds:F0} ms");
        }
        finally
        {
            foreach (var wait in waits) wait.Dispose();
            ThreadPool.SetMinThreads(minWorkers, minIo);
        }
    }

    /// <summary>
    /// **A wait made by Start returns before the disk is asked.** The pane
    /// makes its wait from the window's side; the first look, which on a dead
    /// share takes most of a minute, is the wait's own to run.
    /// </summary>
    [Fact]
    public void Start_returns_before_the_first_look_and_the_look_still_finds_the_folder()
    {
        var target = Path.Combine(_root, "x");
        using var gate = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        var told = 0;
        FolderReturnWatch? wait = null;

        bool Held(string path)
        {
            asked.Set();
            gate.Wait();
            return Directory.Exists(path);
        }

        var starting = OwnThread.Run(() => wait = FolderReturnWatch.Start(new Provider(), target, () => Interlocked.Increment(ref told), null, Held, Timeout.InfiniteTimeSpan));

        try
        {
            Assert.True(starting.Finished(Prompt), "Start waited on the disk");
            Assert.True(asked.Wait(Prompt), "the first look never asked the disk");

            Directory.CreateDirectory(target);
        }
        finally
        {
            gate.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref told) == 1, Prompt), "the first look never said the folder was back");
        wait!.Dispose();
    }

    /// <summary>
    /// **Asked again for the places list, it returns at once and looks on a
    /// thread of its own.** The provider raises PlacesChanged on its own
    /// thread; the look behind it can wait on a dead share.
    /// </summary>
    [PoolWorkersFact]
    public void A_recheck_on_its_own_thread_returns_at_once_and_looks_off_the_pool()
    {
        var target = Path.Combine(_root, "x");
        using var gate = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        var hold = false;
        var pooled = true;

        bool Held(string path)
        {
            if (Volatile.Read(ref hold))
            {
                Volatile.Write(ref hold, false);
                pooled = Thread.CurrentThread.IsThreadPoolThread;
                asked.Set();
                gate.Wait();
            }

            return Directory.Exists(path);
        }

        using var wait = new FolderReturnWatch(new Provider(), target, () => { }, null, Held, Timeout.InfiniteTimeSpan);

        Volatile.Write(ref hold, true);

        try
        {
            // From a pool thread, as a provider's event can be.
            using var returned = new ManualResetEventSlim();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                wait.RecheckOnItsOwnThread();
                returned.Set();
            });

            Assert.True(returned.Wait(Prompt), "the recheck waited on the disk");
            Assert.True(asked.Wait(Prompt), "the recheck never asked the disk");
            Assert.False(pooled, "the recheck's look ran on a pool thread");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// **A wait whose looks are slow looks less often, and back to its pace
    /// once the share is.** Each look here takes 300 ms against an interval
    /// of 100 ms; the interval grows, and returns to 100 ms when the disk
    /// answers at once again with the folder above there to watch.
    /// </summary>
    [PoolWorkersFact]
    public void Slow_looks_stretch_the_interval_and_quick_ones_restore_it()
    {
        var above = Path.Combine(_root, "slow");
        var target = Path.Combine(above, "x");
        var slow = 1;

        bool Exists(string path)
        {
            if (Volatile.Read(ref slow) == 1)
            {
                if (path == target) Thread.Sleep(300);
                return false;
            }

            return path == above;
        }

        var interval = TimeSpan.FromMilliseconds(100);
        using var wait = new FolderReturnWatch(new Provider(), target, () => { }, null, Exists, interval);

        Assert.True(SpinWait.SpinUntil(() => wait.Interval > interval, Prompt), "slow looks never stretched the interval");
        Assert.True(wait.Interval <= interval * 8, $"stretched past eight times: {wait.Interval}");

        Volatile.Write(ref slow, 0);

        Assert.True(SpinWait.SpinUntil(() => wait.Interval == interval, Prompt), $"quick looks never brought it back: {wait.Interval}");
    }

    /// <summary>
    /// **A quick look that finds nothing to watch does not undo the back-off**
    /// (batch-0.11.2e QA, round 8). On a real dead share the Windows network
    /// client remembers for about 30 s that the server did not answer, so the
    /// look after a 42 s one answers in no time; counted as quick, it put the
    /// interval back every other look, and it never passed twice the interval
    /// given. Here looks alternate, slow and then instant with nothing there,
    /// as they did on the share: the interval still climbs to eight times. And
    /// once a quick look watches a folder above — the share is back — it
    /// returns to the interval given.
    /// </summary>
    [PoolWorkersFact]
    public void Quick_looks_that_find_nothing_do_not_undo_the_back_off()
    {
        var above = Path.Combine(_root, "share");
        var target = Path.Combine(above, "x");
        var asked = 0;
        var back = 0;

        bool Exists(string path)
        {
            if (Volatile.Read(ref back) == 1) return path == above;

            // Every other look is slow, as the network client's memory of
            // the dead server lapses and is renewed.
            if (path == target && Interlocked.Increment(ref asked) % 2 == 1) Thread.Sleep(300);
            return false;
        }

        var interval = TimeSpan.FromMilliseconds(50);
        using var wait = new FolderReturnWatch(new Provider(), target, () => { }, null, Exists, interval);

        Assert.True(SpinWait.SpinUntil(() => wait.Interval == interval * 8, Prompt), $"the quick looks between kept undoing it: {wait.Interval}");

        // It stays there while the share stays dead.
        var at = Volatile.Read(ref asked);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) >= at + 4, Prompt), "the looks stopped");
        Assert.Equal(interval * 8, wait.Interval);

        Volatile.Write(ref back, 1);

        Assert.True(SpinWait.SpinUntil(() => wait.Interval == interval, Prompt), $"a quick look with a watch open never brought it back: {wait.Interval}");
        Assert.Equal(above, wait.Watching);
    }
}
