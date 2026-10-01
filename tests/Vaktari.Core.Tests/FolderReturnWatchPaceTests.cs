using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The slow look's pace, and the public Start, at the three lines that could be
/// taken out with every test still green (batch-0.11.2e QA, round 8): the
/// timer actually moved to the stretched interval, the stretch capped at eight
/// times the base, and the public Start — the one the pane uses — leaving the
/// first look to its own thread.
/// </summary>
[Collection(ThreadPoolSensitive.Name)]
public sealed class FolderReturnWatchPaceTests : IDisposable
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-pace").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Watches that hear nothing; one may be held on a gate.</summary>
    private sealed class Provider : IFileSystemProvider
    {
        public volatile bool Hold;
        public readonly ManualResetEventSlim Gate = new();

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (Hold) Gate.Wait();
            return new Off();
        }

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
    /// **Stretched, the timer asks less often; and never past eight times.**
    /// Looks of 1.2 s against a base of 100 ms stretch the interval to its
    /// cap, 800 ms, and no further (taking out the cap went on to 1.6 s). Then looks of 50 ms — under the interval, so it holds, and over
    /// a quarter of the base, so it is not restored — must come about once per
    /// 800 ms: a stretch that only changed the number reported, with the
    /// timer left at 100 ms, made a look every 100 ms instead (both were green
    /// before this test).
    /// </summary>
    [PoolWorkersFact]
    public void A_stretched_interval_is_what_the_timer_keeps_and_it_stops_at_eight_times()
    {
        var target = Path.Combine(_root, "slow", "x");
        var pause = 1200;
        var looks = 0;

        bool Exists(string path)
        {
            if (path != target) return false;

            Interlocked.Increment(ref looks);
            Thread.Sleep(Volatile.Read(ref pause));
            return false;
        }

        var interval = TimeSpan.FromMilliseconds(100);
        using var wait = new FolderReturnWatch(new Provider(), target, () => { }, null, Exists, interval);

        Assert.True(SpinWait.SpinUntil(() => wait.Interval == interval * 8, Prompt), $"slow looks never stretched it to eight times: {wait.Interval}");

        // Several more slow looks at the cap: it stays there.
        var at = Volatile.Read(ref looks);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) >= at + 6, Prompt), "the looks stopped");
        Assert.Equal(interval * 8, wait.Interval);

        // Looks of 50 ms: neither stretched nor restored; now count them.
        Volatile.Write(ref pause, 50);
        Thread.Sleep(2000);
        Assert.Equal(interval * 8, wait.Interval);

        var before = Volatile.Read(ref looks);
        Thread.Sleep(4000);
        var asked = Volatile.Read(ref looks) - before;

        // About five in 4 s at 800 ms; a timer left at 100 ms made about forty.
        Assert.InRange(asked, 1, 15);
    }

    /// <summary>
    /// **The public Start returns before the first look.** It is the one the
    /// pane calls, on the window's thread; with its first look held on the
    /// disk, Start must still return at once (taking it out of the own-thread
    /// path left every test green, since the tests used the internal Start).
    /// </summary>
    [Fact]
    public void The_public_Start_returns_while_its_first_look_is_held()
    {
        var fs = new Provider { Hold = true };
        FolderReturnWatch? wait = null;

        var started = OwnThread.Run(() => wait = FolderReturnWatch.Start(fs, Path.Combine(_root, "x"), () => { }));

        try
        {
            Assert.True(started.Finished(TimeSpan.FromSeconds(5)), "Start waited for its first look");
        }
        finally
        {
            fs.Gate.Set();
            Assert.True(started.Finished(Prompt), "Start never returned, even with the look let go");
            wait?.Dispose();
        }
    }
}
