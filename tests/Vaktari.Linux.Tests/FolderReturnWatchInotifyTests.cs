using System.Collections.Concurrent;
using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// FolderReturnWatch over the real watcher (batch-0.11.2c QA, round 3). Its own
/// tests drive a fake provider, step by step, which pins what it decides; these
/// ask what the kernel actually says on the way back, and whether the wait
/// holds up when the shared inotify reader and the pool both call into it at
/// once — it takes its lock on whatever thread noticed, and watches and lets go
/// of watches while holding it.
/// </summary>
public sealed class FolderReturnWatchInotifyTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-return-ino").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string At(params string[] names) => Path.Combine([_root, .. names]);

    private static bool Within(Func<bool> done, TimeSpan? ceiling = null)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            if (clock.Elapsed > (ceiling ?? Ceiling)) return false;
            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>
    /// **Every way a folder comes back by name is heard where the wait is.**
    /// Made again, renamed back within the folder above, moved in from
    /// elsewhere, and — two levels missing — the folder above made first and
    /// the folder after it. Each says so once, through the real watcher.
    /// </summary>
    [PosixTheory]
    [InlineData("made")]
    [InlineData("renamed-in")]
    [InlineData("moved-in")]
    [InlineData("two-levels")]
    public void Each_route_back_is_heard_through_the_real_watcher(string route)
    {
        var fs = new LinuxFileSystemProvider();
        var target = route == "two-levels" ? At("up", "down") : At("w");
        var told = 0;

        if (route == "renamed-in") Directory.CreateDirectory(At("elsewhere-name"));
        if (route == "moved-in") Directory.CreateDirectory(At("..", Path.GetFileName(_root) + "-out", "w"));

        using var wait = new FolderReturnWatch(fs, target, () => Interlocked.Increment(ref told));

        Assert.Equal(_root, wait.Watching);

        switch (route)
        {
            case "made":
                Directory.CreateDirectory(target);
                break;
            case "renamed-in":
                Directory.Move(At("elsewhere-name"), target);
                break;
            case "moved-in":
                Directory.Move(At("..", Path.GetFileName(_root) + "-out", "w"), target);
                Directory.Delete(At("..", Path.GetFileName(_root) + "-out"));
                break;
            case "two-levels":
                Directory.CreateDirectory(At("up"));
                Assert.True(Within(() => wait.Watching == At("up")), $"the wait did not move down to the folder made above; watching {wait.Watching}");
                Directory.CreateDirectory(target);
                break;
        }

        Assert.True(Within(() => Volatile.Read(ref told) > 0), $"{route}: the folder came back and nothing said so; watching {wait.Watching}");

        Thread.Sleep(100);

        Assert.Equal(1, told);
        Assert.Null(wait.Watching);
    }

    /// <summary>
    /// **The folder watched, deleted and made again before its going is read**
    /// (batch-0.11.2c QA, round 3; their repro, adopted). The wait sits on
    /// <c>a</c> for <c>a/b</c>; <c>a</c> is deleted and made again at once, so
    /// by the time the reader delivers Gone the nearest folder above is
    /// <c>a</c> again — the same path, a new folder, and the old watch dead
    /// with the old one. Then <c>a/b</c> is made, and must be heard. Missed 20
    /// rounds in 20 before the wait watched afresh on Gone.
    /// </summary>
    [PosixFact]
    public void The_folder_watched_deleted_and_made_again_at_once_still_hears_the_return()
    {
        var fs = new LinuxFileSystemProvider();
        var missed = new List<int>();

        for (var round = 0; round < 20; round++)
        {
            var a = At($"a{round}");
            var b = Path.Combine(a, "b");
            Directory.CreateDirectory(a);

            var told = 0;
            using var wait = new FolderReturnWatch(fs, b, () => Interlocked.Increment(ref told));

            Assert.Equal(a, wait.Watching);

            Directory.Delete(a);
            Directory.CreateDirectory(a);

            // Let the reader deliver the Gone of the old a.
            Thread.Sleep(50);

            Directory.CreateDirectory(b);

            if (!Within(() => Volatile.Read(ref told) > 0, TimeSpan.FromSeconds(2))) missed.Add(round);
        }

        Assert.True(missed.Count == 0, $"{missed.Count} of 20 rounds never heard a/b come back after a was deleted and made again: rounds {string.Join(",", missed)}");
    }

    /// <summary>
    /// **Waits under a busy folder cost it nothing.** A hundred panes waiting
    /// for folders under one parent, and twenty thousand files arriving there
    /// beside them: an arrival is only looked into when it is the name on the
    /// way down. Before, each wait asked the disk twice per arrival on the
    /// watcher's one thread, and twenty thousand arrivals overflowed the
    /// kernel's queue (16,384), which tells every watch in the process it has
    /// lost track (batch-0.11.2c QA, round 3). An independent watch on the
    /// same folder hears every file, nothing overflows, and the waits asked
    /// the disk no more than their first look did.
    /// </summary>
    [PosixFact]
    public void Waits_under_a_busy_folder_neither_slow_the_reader_nor_overflow_it()
    {
        const int Waits = 100;
        const int Files = 20_000;

        var fs = new LinuxFileSystemProvider();
        var busy = Directory.CreateDirectory(At("busy")).FullName;
        var waits = Enumerable.Range(0, Waits)
            .Select(i => new FolderReturnWatch(fs, Path.Combine(busy, $"missing{i}", "x"), () => { }))
            .ToList();

        var heard = 0;
        var lost = 0;

        using var listener = fs.Watch(busy, c =>
        {
            if (c.Kind == ChangeKind.Added) Interlocked.Increment(ref heard);
            if (c.Kind is ChangeKind.Lost or ChangeKind.Gone) Interlocked.Increment(ref lost);
        });

        try
        {
            var before = waits.Sum(w => w.Checks);

            for (var i = 0; i < Files; i++) File.Create(Path.Combine(busy, $"f{i:D5}")).Dispose();

            Assert.True(Within(() => Volatile.Read(ref heard) >= Files, TimeSpan.FromSeconds(60)),
                $"the independent watch heard {heard} of {Files} files");

            Assert.Equal(0, Volatile.Read(ref lost));
            Assert.Equal(before, waits.Sum(w => w.Checks));

            // The cost itself, counted rather than timed. Whether the reader
            // keeps up is a race between it and the loop above, and it sat on
            // the edge: on the tree before batch-0.11.2e round 6's fix this
            // failed alone 11 runs in 12 whatever the JIT was told, failed in
            // one full Linux suite and passed in another (QA saw it pass in
            // the suite every time). The count does not race: no file here is
            // any wait's next step, so none may reach the full path compare.
            Assert.Equal(0, waits.Sum(w => w.FullCompares));
        }
        finally
        {
            foreach (var wait in waits) wait.Dispose();
        }
    }

    /// <summary>
    /// A provider that hands out real watches, refuses some at random — so the
    /// wait falls back to a PollingWatch on a short timer, whose ticks arrive
    /// from the pool — and counts what is live.
    /// </summary>
    private sealed class Mixed(int seed) : IFileSystemProvider
    {
        private readonly LinuxFileSystemProvider _real = new();
        private readonly Random _random = new(seed);
        public int Live;

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            bool refuse;
            lock (_random) refuse = _random.Next(3) == 0;

            if (refuse) throw new IOException("refused, so the wait polls");

            var inner = _real.Watch(path, onChange);
            Interlocked.Increment(ref Live);
            return new Counted(this, inner);
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct)
            => _real.EnumerateAsync(path, options, ct);

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => _real.GetEntryAsync(path, ct);

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => _real.IsReachableAsync(path, timeout, ct);

        public string Combine(string basePath, string name) => _real.Combine(basePath, name);

        public string? GetParent(string path) => _real.GetParent(path);

        public bool IsCaseSensitive => _real.IsCaseSensitive;

        private sealed class Counted(Mixed owner, IDisposable inner) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 1) return;
                Interlocked.Decrement(ref owner.Live);
                inner.Dispose();
            }
        }
    }

    /// <summary>
    /// **Many waits, the reader and the pool all at once, and nothing locks up
    /// or says twice.** Forty-eight waits on twelve missing folders under three
    /// parents. For three seconds: files are made and removed in the parents
    /// (each an arrival the reader hands every wait), the parents are deleted
    /// and made again (each a Gone that moves waits up, which watches and lets
    /// go of watches under the wait's lock, from the reader thread), the
    /// missing folders come and go, and waits are disposed and made anew from
    /// other threads — with a third of the watches refused so that polling
    /// ticks arrive from the pool as well. Every round of the churn has to
    /// finish within the ceiling (a deadlock would hold it), no wait may say
    /// it is back more than once, and once all are disposed no watch is left.
    /// </summary>
    [PosixFact]
    public void Many_waits_under_the_reader_and_the_pool_neither_lock_up_nor_say_twice()
    {
        var fs = new Mixed(7);
        var parents = Enumerable.Range(0, 3).Select(i => At($"p{i}")).ToArray();
        var targets = parents.SelectMany(p => Enumerable.Range(0, 4).Select(j => Path.Combine(p, $"t{j}"))).ToArray();

        foreach (var p in parents) Directory.CreateDirectory(p);

        var told = new ConcurrentDictionary<FolderReturnWatch, int>();
        var waits = new FolderReturnWatch?[48];
        var gate = new object();

        FolderReturnWatch Make(int i)
        {
            FolderReturnWatch? made = null;
            made = new FolderReturnWatch(fs, targets[i % targets.Length], () =>
            {
                // Said from inside the constructor when the folder is back
                // already: made is still null then, and that is one telling.
                if (made is { } w) told.AddOrUpdate(w, 1, (_, n) => n + 1);
            }, TimeSpan.FromMilliseconds(5));
            told.TryAdd(made, 0);
            return made;
        }

        for (var i = 0; i < waits.Length; i++) waits[i] = Make(i);

        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var progress = new int[4];
        var errors = new ConcurrentQueue<Exception>();

        var threads = new List<Thread>();

        void Loop(int id, Action<Random> step)
        {
            var thread = new Thread(() =>
        {
            var random = new Random(id);
            try
            {
                while (DateTime.UtcNow < stop)
                {
                    step(random);
                    Interlocked.Increment(ref progress[id]);
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
        }) { IsBackground = true, Name = $"churn {id}" };
            threads.Add(thread);
            thread.Start();
        }

        Loop(0, r =>
        {
            var file = Path.Combine(parents[r.Next(3)], $"f{r.Next(50)}");
            try { File.WriteAllText(file, ""); File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });

        Loop(1, r =>
        {
            var p = parents[r.Next(3)];
            try { Directory.Delete(p, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(r.Next(3));
            try { Directory.CreateDirectory(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(5);
        });

        Loop(2, r =>
        {
            var t = targets[r.Next(targets.Length)];
            try { Directory.CreateDirectory(t); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(1);
            try { Directory.Delete(t, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });

        Loop(3, r =>
        {
            var i = r.Next(waits.Length);
            FolderReturnWatch? old;
            lock (gate) { old = waits[i]; waits[i] = null; }
            old?.Dispose();
            var made = Make(i);
            lock (gate) waits[i] = made;
            Thread.Sleep(1);
        });

        // Every churn thread has to come out of its last round: one blocked on
        // a wait's lock (the thread that disposes and makes waits) would not.
        foreach (var thread in threads)
            Assert.True(thread.Join(stop - DateTime.UtcNow + Ceiling), $"{thread.Name} never finished its last round: something is holding a wait's lock");

        var last = progress.ToArray();

        Assert.Empty(errors);
        Assert.All(last, n => Assert.True(n > 0, "a churn thread made no round at all"));

        // Disposing every wait has to finish too: a Dispose blocked on a
        // callback holding the lock would hang here.
        var disposed = Task.Run(() =>
        {
            lock (gate)
                foreach (var w in waits) w?.Dispose();
            foreach (var w in told.Keys) w.Dispose();
        });

        Assert.True(disposed.Wait(Ceiling), "disposing the waits did not finish: something is holding the lock");

        var twice = told.Where(kv => kv.Value > 1).ToList();
        Assert.True(twice.Count == 0, $"{twice.Count} waits said the folder was back more than once (most {twice.Select(t => t.Value).DefaultIfEmpty().Max()})");

        Assert.True(Within(() => Volatile.Read(ref fs.Live) == 0), $"{fs.Live} watches still live after every wait was disposed");
    }
}
