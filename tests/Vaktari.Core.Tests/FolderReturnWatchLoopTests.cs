using System.Runtime.CompilerServices;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The check loop and the retry timer of FolderReturnWatch, at the four lines
/// that could be taken out with every test still green (batch-0.11.2d QA,
/// round 5): a forced ask kept when a plain one arrives behind it, nothing
/// said by a check that finds the folder back after the wait was disposed,
/// the retry timer let go with the wait, and one timer however often the wait
/// is refused.
/// </summary>
public sealed class FolderReturnWatchLoopTests : IDisposable
{
    /// <summary>How long a step that should be at once may take before the
    /// test calls it stuck. Generous: a pass is at once, and only a step that
    /// never comes uses it up (CI's pool was busy for seconds; see OwnThread).</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-loop").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Watches that hear only what the test says.</summary>
    private sealed class Provider : IFileSystemProvider
    {
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            lock (Given) Given.Add((path, onChange));
            return new Off();
        }

        private sealed class Off : IDisposable
        {
            public void Dispose() { }
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>The real disk, except that the answer for <c>held</c> is read
    /// and then kept back on a gate, once, while <see cref="Hold"/> is set.</summary>
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

    /// <summary>
    /// **A forced ask is not lost to a plain one behind it.** A check is
    /// running, held on the disk; the folder watched goes and comes back
    /// meanwhile (a Gone, which asks to watch it afresh), and then a name
    /// arrives (a plain ask). Once the running check is done, the one it runs
    /// next must still watch the folder afresh: a plain ask overwriting the
    /// forced one kept the watch on the folder that went, which hears nothing.
    /// </summary>
    [Fact]
    public void A_forced_ask_survives_a_plain_one_asked_while_a_check_runs()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var target = Path.Combine(a, "b");
        var fs = new Provider();
        var disk = new SlowDisk(target);

        using var wait = new FolderReturnWatch(fs, target, () => { }, null, disk.Exists, TimeSpan.FromHours(1));

        var (first, heard) = Assert.Single(fs.Given);
        Assert.Equal(a, first);

        // A check that will be held: an arrival of the name on the way down.
        disk.Hold = true;
        var running = OwnThread.Run(() => heard(new FileSystemChange(ChangeKind.Added, target)));
        Assert.True(disk.Held.Wait(Prompt), "the check never reached the disk");

        // While it is held: the folder watched goes (forced), then a name arrives (plain).
        heard(new FileSystemChange(ChangeKind.Gone, a));
        heard(new FileSystemChange(ChangeKind.Added, target));

        disk.Gate.Set();
        Assert.True(running.Finished(Prompt), "the held check never finished");

        lock (fs.Given)
        {
            Assert.Equal(2, fs.Given.Count);
            Assert.Equal(a, fs.Given[1].Path);
        }
    }

    /// <summary>
    /// **Disposed while a check finds the folder back, it says nothing.** The
    /// check has read that the folder is there and is held before it says so;
    /// the wait is disposed meanwhile — the pane has moved on — and when the
    /// check goes on, the return is nobody's to hear.
    /// </summary>
    [Fact]
    public void Disposed_while_a_check_finds_the_folder_back_it_says_nothing()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var disk = new SlowDisk(target);
        var told = 0;

        var wait = new FolderReturnWatch(fs, target, () => Interlocked.Increment(ref told), null, disk.Exists, TimeSpan.FromHours(1));

        Directory.CreateDirectory(target);
        disk.Hold = true;
        var running = OwnThread.Run(wait.Recheck);
        Assert.True(disk.Held.Wait(Prompt), "the check never reached the disk");

        wait.Dispose();
        disk.Gate.Set();
        Assert.True(running.Finished(Prompt), "the held check never finished");

        Assert.Equal(0, told);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference DisposedWithATimer(Func<string, bool> exists)
    {
        var wait = new FolderReturnWatch(new Provider(), Path.Combine(_root, "gone", "x"), () => { }, null, exists, TimeSpan.FromHours(1));

        Assert.Null(wait.Watching);
        wait.Dispose();

        return new WeakReference(wait);
    }

    /// <summary>
    /// **The retry timer goes with the wait.** Nothing above is there, so the
    /// wait asks again on a timer; once disposed, nothing may keep it — a timer
    /// left running holds the wait, and fires into it, for the life of the
    /// process, once for every pane that ever waited on a drive that was not
    /// there.
    /// </summary>
    [Fact]
    public void Disposed_with_nothing_above_its_retry_timer_does_not_keep_it()
    {
        var weak = DisposedWithATimer(_ => false);

        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive, "the disposed wait is still held, by its retry timer");
    }

    /// <summary>
    /// **One timer, however often it is refused.** With nothing above, each
    /// check finds nothing to watch; asked again twenty times, the wait must
    /// still ask on one timer, not on one more for every ask.
    /// </summary>
    [Fact]
    public void Asked_again_and_again_with_nothing_above_it_keeps_one_timer()
    {
        var looks = 0;
        using var wait = new FolderReturnWatch(new Provider(), Path.Combine(_root, "gone", "x"), () => { }, null,
            _ => { Interlocked.Increment(ref looks); return false; }, TimeSpan.FromMilliseconds(100));

        for (var i = 0; i < 20; i++) wait.Recheck();

        // The timer ticks at all: waited for, not given a second — its ticks
        // come from the pool, and CI's pool was busy for seconds at a time
        // (run 36887240896: none in a second). See OwnThread.
        var asked = wait.Checks;
        Assert.True(SpinWait.SpinUntil(() => wait.Checks >= asked + 3, Prompt), "the retry timer never ticked");

        // How many: one timer at 100 ms is about twenty checks in two seconds,
        // twenty timers about four hundred. A pool that is late only delays
        // ticks — a tick that waited runs once, and ticks that pile up run
        // into the check already running — so a slow pool cannot push one
        // timer past the bound.
        var before = wait.Checks;
        Thread.Sleep(2000);
        var timed = wait.Checks - before;

        Assert.InRange(timed, 0, 60);

        // And the count itself: timers dropped undisposed stop when the
        // collector finds them, so the looks alone can miss a timer per ask.
        Assert.Equal(1, wait.TimersMade);
    }

    /// <summary>
    /// **The slow look watches afresh, though nothing has changed** (batch-0.11.2e
    /// QA, round 6: the slow look asking without force left every test green).
    /// A watch that has died without a word — opened on a folder in the moment
    /// it was deleted, or on the folder a mount now covers — is on the same
    /// path as the folder above that is there now. Asking without force finds
    /// the same path and keeps the dead watch, so the return is found only by
    /// the next slow look, half a minute late, every time. Here the folder is
    /// still not back: the slow look must still open a new watch on the same
    /// folder, and let the old one go.
    /// </summary>
    [Fact]
    public void The_slow_look_watches_the_same_folder_afresh_with_nothing_changed()
    {
        var fs = new Provider();

        using var wait = new FolderReturnWatch(fs, Path.Combine(_root, "x"), () => { }, null, Directory.Exists, TimeSpan.FromMilliseconds(100));

        lock (fs.Given) Assert.Equal(_root, Assert.Single(fs.Given).Path);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < Prompt)
        {
            lock (fs.Given) if (fs.Given.Count >= 3) break;
            Thread.Sleep(10);
        }

        lock (fs.Given)
        {
            Assert.True(fs.Given.Count >= 3, $"the slow look opened {fs.Given.Count - 1} new watches in {Prompt.TotalSeconds} s");
            Assert.All(fs.Given, g => Assert.Equal(_root, g.Path));
        }

        Assert.Equal(_root, wait.Watching);
    }
}
