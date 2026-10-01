using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The process's inotify instance (Inotify) under the cases its own tests
/// left out (batch-0.11.2c QA): watches made and let go from many threads at
/// once and from inside callbacks, a rename whose two halves are read apart,
/// renames in two folders at once, a deleted folder's watch let go after a
/// new one took its name, an overflow whose folder has gone, and a reader
/// that dies.
///
/// Every folder here is a temporary one. This assembly runs its classes one
/// at a time (Parallelism.cs), so the descriptors and threads counted are
/// this class's own doing.
/// </summary>
public sealed class InotifyAttackTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-inotify-qa").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // ---- the process's own account ---------------------------------------------

    /// <summary>Open descriptors whose link reads <paramref name="target"/>.</summary>
    private static int Descriptors(string target)
    {
        var count = 0;

        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == target) count++;
            }
            catch (IOException)
            {
            }
        }

        return count;
    }

    private const string InotifyLink = "anon_inode:inotify";
    private const string EventFdLink = "anon_inode:[eventfd]";

    /// <summary>Threads whose name the kernel holds as the reader's — the
    /// first fifteen bytes of "Vaktari inotify", which is all of it.</summary>
    private static int Readers()
    {
        var count = 0;

        foreach (var task in Directory.GetDirectories("/proc/self/task"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(task, "comm")).TrimEnd('\n') == "Vaktari inotify") count++;
            }
            catch (IOException)
            {
            }
        }

        return count;
    }

    private static bool Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();

        while (!done())
        {
            if (clock.Elapsed > Ceiling) return false;
            Thread.Sleep(10);
        }

        return true;
    }

    // ---- libc, for the reader made to fail and the reused number ----------------

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int from, int to);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] descriptors);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_init1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_add_watch(int descriptor, byte[] path, uint mask);

    private static byte[] Native(string path) => Encoding.UTF8.GetBytes(path + "\0");

    private const int ReadOnlyDirectory = 0x10000 | 0x80000; // O_DIRECTORY | O_CLOEXEC

    /// <summary>What a watch said, in order, with a wait for the next.</summary>
    private sealed class Heard
    {
        private readonly List<FileSystemChange> _all = [];

        public void Add(FileSystemChange change)
        {
            lock (_all)
            {
                _all.Add(change);
                Monitor.PulseAll(_all);
            }
        }

        public List<FileSystemChange> All
        {
            get { lock (_all) return [.. _all]; }
        }

        public bool WaitFor(Func<FileSystemChange, bool> wanted)
        {
            var clock = Stopwatch.StartNew();

            lock (_all)
            {
                while (!_all.Any(wanted))
                {
                    var left = Ceiling - clock.Elapsed;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_all, left);
                }

                return true;
            }
        }

        public override string ToString()
            => string.Join(" | ", All.Take(40).Select(c => $"{c.Kind} {c.Path} {c.OldPath}"));
    }

    private static Func<FileSystemChange, bool> Is(ChangeKind kind, string path, string? old = null)
        => c => c.Kind == kind && c.Path == path && c.OldPath == old;

    // ---- shutdown and reopen, from everywhere at once -------------------------------

    /// <summary>
    /// **Made and let go from sixteen threads at once, and from inside the
    /// callbacks** — a callback that disposes its own watch, one that watches
    /// another folder and lets it go again from the reader thread, a folder
    /// deleted and made again under it all — and afterwards nothing is left:
    /// no inotify descriptor, no eventfd, no reader thread, no shared
    /// instance. The instance closes and opens again hundreds of times here,
    /// so a reader that missed its wake-up, a descriptor closed twice or never,
    /// or a watch added to an instance already closing would show as a count
    /// that does not come back.
    /// </summary>
    [PosixFact]
    public void Watches_made_and_let_go_from_everywhere_at_once_leave_nothing_open()
    {
        // Anything a provider made by an earlier class still holds is part of
        // the baseline: counted, and compared with, not assumed away.
        var sharedBefore = Inotify.Shared is not null;
        var instancesBefore = Descriptors(InotifyLink);
        var eventFdsBefore = Descriptors(EventFdLink);
        var readersBefore = Readers();

        var dirs = Enumerable.Range(0, 8).Select(i => Dir($"h{i}")).ToArray();
        var churned = dirs[7];
        var failures = new List<Exception>();
        var refused = 0;
        var heard = 0;
        var peak = 0;
        using var stop = new CancellationTokenSource();

        // Something to hear: files written, and one folder deleted and made
        // again, so Gone and IN_IGNORED arrive while watches come and go.
        var writer = new Thread(() =>
        {
            var n = 0;

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(Path.Combine(dirs[n % 7], $"f{n % 13}"), "x");

                    if (n % 50 == 0)
                    {
                        Directory.Delete(churned, recursive: true);
                        Directory.CreateDirectory(churned);
                    }
                }
                catch (IOException)
                {
                }

                n++;
            }
        });

        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var now = Descriptors(InotifyLink);
                if (now > Volatile.Read(ref peak)) Volatile.Write(ref peak, now);
                Thread.Sleep(2);
            }
        });

        writer.Start();
        sampler.Start();

        var workers = Enumerable.Range(0, 16).Select(t => new Thread(() =>
        {
            var random = new Random(t);

            for (var i = 0; i < 1500; i++)
            {
                var dir = dirs[random.Next(dirs.Length)];
                var shape = random.Next(4);
                IDisposable? watch = null;
                var told = 0;

                try
                {
                    watch = Inotify.Watch(dir, change =>
                    {
                        Interlocked.Increment(ref heard);

                        if (Interlocked.Exchange(ref told, 1) != 0) return;

                        switch (shape)
                        {
                            // Lets itself go from the reader thread.
                            case 1:
                                Volatile.Read(ref watch)?.Dispose();
                                break;

                            // Watches somewhere else and lets that go, on the
                            // reader thread of an instance that may be closing.
                            case 2:
                                try
                                {
                                    Inotify.Watch(dirs[(i + 1) % 7], _ => { }).Dispose();
                                }
                                catch (Exception e) when (e is IOException or ObjectDisposedException)
                                {
                                    lock (failures) failures.Add(e);
                                }

                                break;
                        }
                    });

                    try
                    {
                        if (random.Next(2) == 0) File.WriteAllText(Path.Combine(dir, $"w{t}"), "w");
                    }
                    catch (IOException)
                    {
                        // The churned folder, gone again.
                    }
                    if (random.Next(3) == 0) Thread.Yield();
                    else if (random.Next(8) == 0) Thread.Sleep(1);
                }
                catch (DirectoryNotFoundException)
                {
                    // The churned folder, caught between its deletion and its
                    // making again.
                    Interlocked.Increment(ref refused);
                }
                catch (Exception e)
                {
                    lock (failures) failures.Add(e);
                }
                finally
                {
                    watch?.Dispose();
                    if (shape == 3) watch?.Dispose();
                }
            }
        })).ToList();

        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "a worker hung");

        stop.Cancel();
        writer.Join();
        sampler.Join();

        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f => f.ToString())));
        Assert.True(heard > 0, "nothing was heard: the hammer measured nothing");

        var settled = Until(() => Descriptors(InotifyLink) == instancesBefore
                                  && Descriptors(EventFdLink) == eventFdsBefore
                                  && Readers() == readersBefore);

        Assert.True(settled,
            $"left open: inotify {Descriptors(InotifyLink)} (was {instancesBefore}), "
            + $"eventfd {Descriptors(EventFdLink)} (was {eventFdsBefore}), "
            + $"readers {Readers()} (was {readersBefore}); peak inotify {peak}, refused {refused}");

        if (!sharedBefore) Assert.Null(Inotify.Shared);

        Console.WriteLine($"[qa] hammer: heard {heard}, refused {refused}, peak inotify descriptors {peak}");
    }

    // ---- a rename read in two pieces ---------------------------------------------------

    /// <summary>
    /// **A rename whose two halves land in different reads is still one
    /// rename.** The reader is held in a callback while the queue fills with
    /// folders made, each event 32 bytes (a name under 16 bytes is padded to
    /// 16), so that a 64 KiB read ends exactly on the move's first half
    /// (2047 before it), on its second (2046), or just short of both (2048).
    /// </summary>
    [PosixTheory]
    [InlineData(2046)]
    [InlineData(2047)]
    [InlineData(2048)]
    public void A_rename_split_across_two_reads_is_one_rename(int before)
    {
        var hold = Dir("hold");
        var dir = Dir("w");
        var from = Path.Combine(dir, "r0");
        var to = Path.Combine(dir, "r1");
        File.WriteAllText(from, "r");

        var heard = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        var holding = instance.Add(hold, _ =>
        {
            if (entered.IsSet) return;

            entered.Set();
            release.Wait(Ceiling);
        });

        var watch = instance.Add(dir, heard.Add);

        try
        {
            Directory.CreateDirectory(Path.Combine(hold, "h"));
            Assert.True(entered.Wait(Ceiling), "the reader never called back");

            for (var i = 0; i < before; i++) Directory.CreateDirectory(Path.Combine(dir, $"d{i:D4}"));

            File.Move(from, to);

            release.Set();

            Assert.True(heard.WaitFor(Is(ChangeKind.Renamed, to, from)), "no rename: " + heard);
            Thread.Sleep(300);

            var about = heard.All.Where(c => c.Path == from || c.Path == to || c.OldPath == from).ToList();
            Assert.True(about.Count == 1, "the rename was heard as: " + string.Join(" | ", about.Select(c => $"{c.Kind} {c.Path} {c.OldPath}")));
            Assert.Equal(before + 1, heard.All.Count);
        }
        finally
        {
            release.Set();
            watch.Dispose();
            holding.Dispose();
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    /// <summary>
    /// **Renames in two folders at once, and moves between them**, from three
    /// threads: what each folder's watch reports, replayed onto what it held
    /// at the start, is what it holds at the end. One queue now carries both
    /// folders, so the halves of two renames can interleave — they used to be
    /// in two instances — and a half let go early must still leave the
    /// listing right.
    /// </summary>
    [PosixFact]
    public void Renames_in_two_folders_at_once_replay_to_what_is_there()
    {
        var a = Dir("a");
        var b = Dir("b");

        for (var i = 0; i < 40; i++)
        {
            File.WriteAllText(Path.Combine(a, $"a{i}-0"), "");
            File.WriteAllText(Path.Combine(b, $"b{i}-0"), "");
        }

        File.WriteAllText(Path.Combine(a, "shuttle"), "");

        var heardA = new Heard();
        var heardB = new Heard();
        var instance = Inotify.Open();

        var startA = Directory.GetFileSystemEntries(a).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var startB = Directory.GetFileSystemEntries(b).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        using (instance.Add(a, heardA.Add))
        using (instance.Add(b, heardB.Add))
        {
            void Rename(string dir, string prefix)
            {
                for (var round = 0; round < 60; round++)
                    for (var i = 0; i < 40; i++)
                        File.Move(Path.Combine(dir, $"{prefix}{i}-{round}"), Path.Combine(dir, $"{prefix}{i}-{round + 1}"));
            }

            var one = new Thread(() => Rename(a, "a"));
            var two = new Thread(() => Rename(b, "b"));
            var shuttle = new Thread(() =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    File.Move(Path.Combine(a, "shuttle"), Path.Combine(b, "shuttle"));
                    File.Move(Path.Combine(b, "shuttle"), Path.Combine(a, "shuttle"));
                }
            });

            one.Start();
            two.Start();
            shuttle.Start();
            one.Join();
            two.Join();
            shuttle.Join();

            File.WriteAllText(Path.Combine(a, "end"), "");
            File.WriteAllText(Path.Combine(b, "end"), "");
            Assert.True(heardA.WaitFor(Is(ChangeKind.Added, Path.Combine(a, "end"))), "a: " + heardA);
            Assert.True(heardB.WaitFor(Is(ChangeKind.Added, Path.Combine(b, "end"))), "b: " + heardB);
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");

        static HashSet<string> Replay(HashSet<string> start, List<FileSystemChange> changes)
        {
            var now = new HashSet<string>(start, StringComparer.Ordinal);

            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case ChangeKind.Added:
                        now.Add(Path.GetFileName(change.Path));
                        break;
                    case ChangeKind.Removed:
                        now.Remove(Path.GetFileName(change.Path));
                        break;
                    case ChangeKind.Renamed:
                        now.Remove(Path.GetFileName(change.OldPath!));
                        now.Add(Path.GetFileName(change.Path));
                        break;
                }
            }

            return now;
        }

        Assert.DoesNotContain(heardA.All, c => c.Kind is ChangeKind.Lost or ChangeKind.Gone);
        Assert.DoesNotContain(heardB.All, c => c.Kind is ChangeKind.Lost or ChangeKind.Gone);

        var endA = Directory.GetFileSystemEntries(a).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var endB = Directory.GetFileSystemEntries(b).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(endA.Order(StringComparer.Ordinal), Replay(startA!, heardA.All).Order(StringComparer.Ordinal));
        Assert.Equal(endB.Order(StringComparer.Ordinal), Replay(startB!, heardB.All).Order(StringComparer.Ordinal));
    }

    // ---- a deleted folder's watch let go late ---------------------------------------------

    /// <summary>
    /// **The old watch let go before its IN_IGNORED is read**, with a new
    /// folder at the same name already watched. The reader is held while the
    /// folder is deleted, made again and watched again, and the old watch
    /// disposed — its kernel watch already dropped, its IN_IGNORED still
    /// queued. The new watch must go on hearing, the kernel must hold exactly
    /// the watches that are wanted, and the old one hears nothing at all.
    /// </summary>
    [PosixFact]
    public void A_deleted_folder_let_go_before_its_ignored_is_read_leaves_the_new_watch_alone()
    {
        var hold = Dir("hold");
        var dir = Dir("w");
        var old = new Heard();
        var anew = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        var holding = instance.Add(hold, _ =>
        {
            if (entered.IsSet) return;

            entered.Set();
            release.Wait(Ceiling);
        });

        try
        {
            var gone = instance.Add(dir, old.Add);

            Directory.CreateDirectory(Path.Combine(hold, "h"));
            Assert.True(entered.Wait(Ceiling), "the reader never called back");

            Directory.Delete(dir);
            Directory.CreateDirectory(dir);

            using var again = instance.Add(dir, anew.Add);

            gone.Dispose();
            release.Set();

            File.WriteAllText(Path.Combine(dir, "x"), "x");
            Assert.True(anew.WaitFor(Is(ChangeKind.Added, Path.Combine(dir, "x"))), "the new watch: " + anew);

            Thread.Sleep(300);

            Assert.Empty(old.All);
            Assert.DoesNotContain(anew.All, c => c.Kind is ChangeKind.Gone or ChangeKind.Lost);
            Assert.Equal(2, instance.Folders);
            Assert.Equal(2, InotifyCount.Watches(instance.Descriptor));
        }
        finally
        {
            release.Set();
            holding.Dispose();
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    // ---- overflow with the folder gone -------------------------------------------------------

    /// <summary>
    /// **An overflow whose folder has meanwhile been deleted is Gone, not
    /// Lost** — the deletion's own DELETE_SELF and IN_IGNORED are among what
    /// the full queue dropped, so the overflow is the only word there is.
    /// A folder still there hears Lost from the same overflow.
    /// </summary>
    [PosixFact]
    public void An_overflow_whose_folder_was_deleted_meanwhile_is_gone()
    {
        var queued = int.Parse(File.ReadAllText("/proc/sys/fs/inotify/max_queued_events").Trim());

        Assert.True(queued <= 1_000_000, $"max_queued_events is {queued}; overflowing it would take too long");

        var hold = Dir("hold");
        var dir = Dir("w");
        var heardHold = new Heard();
        var heard = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        using (instance.Add(hold, change =>
               {
                   if (!entered.IsSet)
                   {
                       entered.Set();
                       release.Wait(Ceiling * 6);
                   }

                   heardHold.Add(change);
               }))
        using (instance.Add(dir, heard.Add))
        {
            Directory.CreateDirectory(Path.Combine(hold, "h"));
            Assert.True(entered.Wait(Ceiling), "the reader never called back");

            for (var i = 0; i < queued + 64; i++) File.Create(Path.Combine(dir, $"f{i:D7}")).Dispose();

            Directory.Delete(dir, recursive: true);

            release.Set();

            Assert.True(heard.WaitFor(c => c.Kind == ChangeKind.Gone && c.Path == dir), "the deleted folder: " + heard.All.Count + " heard, last " + heard.All.LastOrDefault());
            Assert.True(heardHold.WaitFor(Is(ChangeKind.Lost, hold)), "the folder still there: " + heardHold);
            Assert.DoesNotContain(heard.All, c => c.Kind == ChangeKind.Lost);
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    // ---- the reader dies --------------------------------------------------------------------------

    /// <summary>
    /// **A reader that dies tells every listener Lost, and the next watch
    /// opens a new instance that hears.** The instance's descriptor is
    /// replaced underneath it by one the reader cannot read — a folder
    /// (read answers EISDIR) or the write end of a pipe with no reader
    /// (poll answers POLLERR) — and one event on the old instance wakes it.
    /// Afterwards the old instance's number is taken by an inotify instance
    /// of the test's own whose first watch has the same number the dead
    /// listener had: letting the dead listener go must not reach it.
    /// </summary>
    [PosixTheory]
    [InlineData("folder")]
    [InlineData("pipe")]
    public void A_reader_that_dies_tells_every_listener_lost_and_the_next_watch_hears(string how)
    {
        var one = Dir("one");
        var two = Dir("two");
        var heardOne = new Heard();
        var heardTwo = new Heard();
        var instancesBefore = Descriptors(InotifyLink);

        var instance = Inotify.Open();
        var first = instance.Add(one, heardOne.Add);
        var second = instance.Add(two, heardTwo.Add);
        var number = instance.Descriptor;

        int replacement;
        int[] ends = [-1, -1];

        if (how == "folder")
        {
            replacement = open(Native(_root), ReadOnlyDirectory);
        }
        else
        {
            Assert.Equal(0, pipe(ends));
            replacement = ends[1];
            close(ends[0]);
        }

        Assert.True(replacement >= 0, "open: " + Marshal.GetLastPInvokeError());
        Assert.Equal(number, dup2(replacement, number));
        close(replacement);

        // Wakes the poll, which still holds the old instance.
        File.WriteAllText(Path.Combine(one, "wake"), "w");

        Assert.True(heardOne.WaitFor(Is(ChangeKind.Lost, one)), "one: " + heardOne);
        Assert.True(heardTwo.WaitFor(Is(ChangeKind.Lost, two)), "two: " + heardTwo);
        Assert.True(instance.Exited.WaitOne(Ceiling), "the dead reader did not reach its end");

        // The old number, now an inotify instance of the test's own with a
        // watch numbered 1 — the number the first dead listener held.
        var own = inotify_init1(0x80000);
        Assert.True(own >= 0);

        // The lowest free number is often the one just let go.
        if (own != number)
        {
            Assert.Equal(number, dup2(own, number));
            close(own);
        }

        var spare = Dir("spare");

        try
        {
            var wd = inotify_add_watch(number, Native(spare), Inotify.Mask);
            Assert.True(wd == 1, $"own watch {wd}, errno {Marshal.GetLastPInvokeError()}");

            first.Dispose();
            second.Dispose();

            Assert.Equal(1, InotifyCount.Watches(number));
        }
        finally
        {
            close(number);
        }

        // And watching goes on, through a new shared instance.
        var heardAgain = new Heard();

        using (var again = FolderWatch.Open(one, heardAgain.Add))
        {
            File.WriteAllText(Path.Combine(one, "after"), "a");
            Assert.True(heardAgain.WaitFor(Is(ChangeKind.Added, Path.Combine(one, "after"))), "after: " + heardAgain);
        }

        Assert.True(Until(() => Descriptors(InotifyLink) <= instancesBefore), $"inotify descriptors {Descriptors(InotifyLink)}, was {instancesBefore}");
    }

    /// <summary>
    /// **The shared instance, dead, is not handed out again.** A watch made
    /// through Watch after the shared reader died opens a new instance rather
    /// than adding to the dead one.
    /// </summary>
    [PosixFact]
    public void A_dead_shared_instance_is_not_handed_out_again()
    {
        var one = Dir("one");
        var heard = new Heard();

        var first = Inotify.Watch(one, heard.Add);
        var dead = Inotify.Shared!;
        var number = dead.Descriptor;

        try
        {
            var folder = open(Native(_root), ReadOnlyDirectory);
            Assert.True(folder >= 0);
            Assert.Equal(number, dup2(folder, number));
            close(folder);

            File.WriteAllText(Path.Combine(one, "wake"), "w");

            Assert.True(heard.WaitFor(Is(ChangeKind.Lost, one)), "lost: " + heard);
            Assert.True(dead.Exited.WaitOne(Ceiling), "the dead reader did not reach its end");

            var again = new Heard();

            using (Inotify.Watch(one, again.Add))
            {
                Assert.NotSame(dead, Inotify.Shared);

                File.WriteAllText(Path.Combine(one, "after"), "a");
                Assert.True(again.WaitFor(Is(ChangeKind.Added, Path.Combine(one, "after"))), "after: " + again);
            }
        }
        finally
        {
            first.Dispose();
        }
    }

    // ---- the KDE config folder's watch, the branches its tests reach by chance --------------

    /// <summary>
    /// **The folder above going too, after the watch has moved up to it**:
    /// the watch lapses. KdeThemeWatcherTests deletes both at once, which
    /// reaches either this branch or the one where the folder above is
    /// already gone, depending on how far the recursive delete has got;
    /// here each step waits for the last.
    /// </summary>
    [PosixFact]
    public void The_folder_above_going_after_the_watch_moved_up_lapses_it()
    {
        var above = Dir("above");
        var config = Directory.CreateDirectory(Path.Combine(above, "config")).FullName;
        var changed = 0;

        using var watch = ConfigFolderWatch.Start(config, () => Interlocked.Increment(ref changed));
        Assert.NotNull(watch);
        Assert.True(watch.OnFolder);

        Directory.Delete(config);
        Assert.True(Until(() => !watch.OnFolder), "the config folder going was not heard");
        Assert.False(watch.Lapsed, "lapsed with the folder above still there");

        Directory.Delete(above);
        Assert.True(Until(() => watch.Lapsed), "the folder above going was not heard");

        // Lapsed means nothing is held: the folder coming back is not heard.
        Directory.CreateDirectory(config);
        Thread.Sleep(300);
        Assert.False(watch.OnFolder);
    }

    /// <summary>
    /// **The config folder renamed away and back** — the watch follows
    /// neither the name nor the folder, so it waits above; the name coming
    /// back is heard as a rename there, and the file is said to have changed.
    /// </summary>
    [PosixFact]
    public void A_config_folder_renamed_away_and_back_is_watched_again()
    {
        var above = Dir("above");
        var config = Directory.CreateDirectory(Path.Combine(above, "config")).FullName;
        var changed = 0;

        using var watch = ConfigFolderWatch.Start(config, () => Interlocked.Increment(ref changed));
        Assert.NotNull(watch);

        Directory.Move(config, config + ".bak");
        Assert.True(Until(() => !watch.OnFolder), "the rename away was not heard");

        var before = Volatile.Read(ref changed);
        Directory.Move(config + ".bak", config);
        Assert.True(Until(() => watch.OnFolder), "the rename back was not heard");
        // Said just after the watch is back, outside its lock: waited for, not read once.
        Assert.True(Until(() => Volatile.Read(ref changed) > before), "the file was not said to have changed on the way back");

        var now = Volatile.Read(ref changed);
        File.WriteAllText(Path.Combine(config, "kdeglobals"), "[KDE]");
        Assert.True(Until(() => Volatile.Read(ref changed) > now), "a write after coming back was not heard");
    }
}
