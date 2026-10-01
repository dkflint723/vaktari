using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The process's own inotify instance (Inotify): what each change on disk is
/// reported as, that the folder going is heard, that the instance is one
/// however many folders are watched and is closed when the last watch goes,
/// and that a watch disposed while its events wait hears none of them.
///
/// **What it replaced leaked** (batch-0.11.2b QA): a FileSystemWatcher whose
/// folder was deleted kept its inotify instance open until the process
/// exited, disposed or not. The mapping asserted here is the one that
/// watcher's handlers made, measured against .NET 10.0.11 for each change
/// (vaktari-batch-0.11.2c-notes/fswsem): a create Added, a write or a mode
/// change Changed, a delete Removed, a rename inside the folder Renamed, a
/// move out Removed and a move in Added, nothing for a name further down.
///
/// Every folder here is a temporary one. The instances counted are this
/// process's; this assembly runs its classes one at a time (Parallelism.cs),
/// so nothing else opens one meanwhile. Tests that count use an instance of
/// their own (Inotify.Open) rather than the shared one, which a provider
/// made by an earlier class may be holding.
/// </summary>
public sealed class InotifyWatchTests : IDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    /// <summary>Long enough for anything still in the kernel's queue to have
    /// been read: the reader wakes on the first event.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-inotify").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

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

        public void Clear()
        {
            lock (_all) _all.Clear();
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

        public override string ToString() => string.Join(" | ", All.Select(c => $"{c.Kind} {c.Path} {c.OldPath}"));
    }

    private static IDisposable Watch(string path, Action<FileSystemChange> onChange)
        => new LinuxFileSystemProvider { MountLines = () => [] }.Watch(path, onChange);

    private static Func<FileSystemChange, bool> Is(ChangeKind kind, string path, string? old = null)
        => c => c.Kind == kind && c.Path == path && c.OldPath == old;

    // ---- what each change is reported as -------------------------------------

    [PosixFact]
    public void Each_change_is_reported_as_the_FileSystemWatcher_reported_it()
    {
        var dir = Dir("w");
        var heard = new Heard();
        var file = Path.Combine(dir, "a.txt");

        using var watch = Watch(dir, heard.Add);

        File.WriteAllText(file, "");
        Assert.True(heard.WaitFor(Is(ChangeKind.Added, file)), "a create: " + heard);

        heard.Clear();
        File.AppendAllText(file, "x");
        Assert.True(heard.WaitFor(Is(ChangeKind.Changed, file)), "a write: " + heard);

        heard.Clear();
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.True(heard.WaitFor(Is(ChangeKind.Changed, file)), "a mode change: " + heard);

        heard.Clear();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        Assert.True(heard.WaitFor(Is(ChangeKind.Added, sub)), "a folder made: " + heard);

        // Nothing further down: the watch is on direct children only.
        heard.Clear();
        File.WriteAllText(Path.Combine(sub, "deep.txt"), "x");
        File.Delete(file);
        Assert.True(heard.WaitFor(Is(ChangeKind.Removed, file)), "a delete: " + heard);
        Assert.DoesNotContain(heard.All, c => c.Path.StartsWith(sub + "/", StringComparison.Ordinal));

        heard.Clear();
        Directory.Delete(sub, recursive: true);
        Assert.True(heard.WaitFor(Is(ChangeKind.Removed, sub)), "a folder removed: " + heard);
    }

    /// <summary>The two halves of a rename inside the folder are one
    /// rename, new name first and the old one beside it, and nothing else.</summary>
    [PosixFact]
    public void A_rename_inside_the_folder_is_one_rename()
    {
        var dir = Dir("w");
        var heard = new Heard();
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "a");

        using var watch = Watch(dir, heard.Add);

        File.Move(a, b);

        Assert.True(heard.WaitFor(Is(ChangeKind.Renamed, b, a)), "the rename: " + heard);
        Thread.Sleep(Settle);
        Assert.Single(heard.All);
    }

    /// <summary>A move out of sight is a removal, and a move in from out of
    /// sight an arrival — the move's other half is in a folder not watched.</summary>
    [PosixFact]
    public void A_move_out_is_a_removal_and_a_move_in_an_arrival()
    {
        var dir = Dir("w");
        var outside = Dir("outside");
        var heard = new Heard();
        var inside = Path.Combine(dir, "a.txt");
        File.WriteAllText(inside, "a");

        using var watch = Watch(dir, heard.Add);

        File.Move(inside, Path.Combine(outside, "a.txt"));
        Assert.True(heard.WaitFor(Is(ChangeKind.Removed, inside)), "the move out: " + heard);

        heard.Clear();
        var back = Path.Combine(dir, "back.txt");
        File.Move(Path.Combine(outside, "a.txt"), back);
        Assert.True(heard.WaitFor(Is(ChangeKind.Added, back)), "the move in: " + heard);

        Thread.Sleep(Settle);
        Assert.Single(heard.All);
    }

    /// <summary>A move between two watched folders: the one it left hears a
    /// removal and the one it reached an arrival, each in its own spelling.</summary>
    [PosixFact]
    public void A_move_between_two_watched_folders_is_heard_in_both()
    {
        var one = Dir("one");
        var two = Dir("two");
        var heardOne = new Heard();
        var heardTwo = new Heard();
        var from = Path.Combine(one, "a.txt");
        var to = Path.Combine(two, "b.txt");
        File.WriteAllText(from, "a");

        using var first = Watch(one, heardOne.Add);
        using var second = Watch(two, heardTwo.Add);

        File.Move(from, to);

        Assert.True(heardOne.WaitFor(Is(ChangeKind.Removed, from)), "the folder left: " + heardOne);
        Assert.True(heardTwo.WaitFor(Is(ChangeKind.Added, to)), "the folder reached: " + heardTwo);
    }

    // ---- the folder itself going ---------------------------------------------

    /// <summary>
    /// **The watched folder deleted is Gone**, once — which FileSystemWatcher
    /// never said — after the removals of what was in it.
    /// </summary>
    [PosixFact]
    public void The_watched_folder_deleted_is_gone_once()
    {
        var dir = Dir("w");
        var inside = Path.Combine(dir, "a.txt");
        File.WriteAllText(inside, "a");
        var heard = new Heard();

        using var watch = Watch(dir, heard.Add);

        Directory.Delete(dir, recursive: true);

        Assert.True(heard.WaitFor(Is(ChangeKind.Gone, dir)), "the folder deleted: " + heard);
        Thread.Sleep(Settle);

        var all = heard.All;
        Assert.Single(all, c => c.Kind == ChangeKind.Gone);
        Assert.True(all.FindIndex(new Predicate<FileSystemChange>(Is(ChangeKind.Removed, inside)))
                    < all.FindIndex(new Predicate<FileSystemChange>(Is(ChangeKind.Gone, dir))), heard.ToString());
    }

    /// <summary>
    /// **Moved away is Gone too.** The watch follows the folder, not the
    /// name, so FileSystemWatcher went on reporting what happened in it under
    /// the old name; nothing is heard after Gone.
    /// </summary>
    [PosixFact]
    public void The_watched_folder_moved_away_is_gone()
    {
        var dir = Dir("w");
        var heard = new Heard();

        using var watch = Watch(dir, heard.Add);

        Directory.Move(dir, dir + "-moved");

        Assert.True(heard.WaitFor(Is(ChangeKind.Gone, dir)), "the folder moved: " + heard);

        File.WriteAllText(Path.Combine(dir + "-moved", "m.txt"), "m");
        Thread.Sleep(Settle);

        Assert.Equal([new FileSystemChange(ChangeKind.Gone, dir)], heard.All);
    }

    /// <summary>
    /// A filesystem unmounted from under the watch: Gone. Mounted in a user
    /// and mount namespace of the test's own (unshare -rm, no privilege), and
    /// watched from outside it through /proc/&lt;pid&gt;/root — the kernel
    /// sends IN_UNMOUNT to every watch on it.
    /// </summary>
    [UnshareFact]
    public void A_filesystem_unmounted_under_the_watch_is_gone()
    {
        var point = Dir("mnt");
        var heard = new Heard();

        using var child = Process.Start(new ProcessStartInfo("unshare",
            ["-rm", "sh", "-c", $"mount -t tmpfs t '{point}' && echo mounted && read x && umount '{point}' && echo unmounted && read y"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;

        try
        {
            Assert.Equal("mounted", child.StandardOutput.ReadLine());

            var inside = $"/proc/{child.Id}/root{point}";

            using var watch = Watch(inside, heard.Add);

            File.WriteAllText(Path.Combine(inside, "f"), "f");
            Assert.True(heard.WaitFor(Is(ChangeKind.Added, Path.Combine(inside, "f"))), "a file on the mount: " + heard);
            Assert.False(File.Exists(Path.Combine(point, "f")), "the file landed outside the mount");

            child.StandardInput.WriteLine();
            Assert.Equal("unmounted", child.StandardOutput.ReadLine());

            Assert.True(heard.WaitFor(Is(ChangeKind.Gone, inside)), "the unmount: " + heard);
        }
        finally
        {
            try { child.StandardInput.WriteLine(); child.StandardInput.WriteLine(); } catch (IOException) { }
            child.WaitForExit(5000);
        }
    }

    /// <summary>
    /// **An overflowed queue is Lost**, for a folder that is still there. The
    /// reader is held in a callback while more events are made than the
    /// kernel queues (fs.inotify.max_queued_events), so the kernel drops the
    /// rest and says so.
    /// </summary>
    [PosixFact]
    public void An_overflowed_queue_is_lost()
    {
        var queued = int.Parse(File.ReadAllText("/proc/sys/fs/inotify/max_queued_events").Trim());

        Assert.True(queued <= 1_000_000, $"max_queued_events is {queued}; overflowing it would take too long");

        var dir = Dir("w");
        var heard = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        using (instance.Add(dir, change =>
               {
                   if (!entered.IsSet)
                   {
                       entered.Set();
                       release.Wait(Ceiling * 6);
                   }

                   heard.Add(change);
               }))
        {
            File.WriteAllText(Path.Combine(dir, "first"), "");
            Assert.True(entered.Wait(Ceiling), "the reader never called back");

            for (var i = 0; i < queued + 64; i++) File.Create(Path.Combine(dir, $"f{i:D7}")).Dispose();

            release.Set();

            Assert.True(heard.WaitFor(Is(ChangeKind.Lost, dir)), "no overflow was said: " + heard.All.Count + " heard");
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    // ---- one instance, many watches ---------------------------------------------

    /// <summary>
    /// **One instance however many folders are watched**, each hearing only
    /// its own, and closed — reader stopped, descriptor gone — when the last
    /// watch is disposed.
    /// </summary>
    [PosixFact]
    public void Many_watches_share_one_instance_that_closes_with_the_last()
    {
        const int Folders = 200;

        var before = InotifyCount.Instances();
        var instance = Inotify.Open();
        var dirs = Enumerable.Range(0, Folders).Select(i => Dir($"w{i:D3}")).ToList();
        var heard = dirs.Select(_ => new Heard()).ToList();
        var watches = dirs.Select((d, i) => instance.Add(d, heard[i].Add)).ToList();

        try
        {
            Assert.Equal(before + 1, InotifyCount.Instances());
            Assert.Equal(Folders, instance.Folders);
            Assert.Equal(Folders, InotifyCount.Watches(instance.Descriptor));

            for (var i = 0; i < Folders; i++) File.WriteAllText(Path.Combine(dirs[i], "x"), "x");

            for (var i = 0; i < Folders; i++)
            {
                Assert.True(heard[i].WaitFor(Is(ChangeKind.Added, Path.Combine(dirs[i], "x"))), $"folder {i}: {heard[i]}");
            }

            Thread.Sleep(Settle);

            for (var i = 0; i < Folders; i++)
                Assert.All(heard[i].All, c => Assert.Equal(dirs[i], Path.GetDirectoryName(c.Path)));
        }
        finally
        {
            foreach (var watch in watches) watch.Dispose();
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
        Assert.Equal(before, InotifyCount.Instances());
    }

    /// <summary>
    /// Two watches on one folder — one through a link to it — share the
    /// kernel's watch, each hearing in the spelling it asked with; the watch
    /// stays while either does, and goes with the second.
    /// </summary>
    [PosixFact]
    public void Two_watches_on_one_folder_share_its_watch()
    {
        var dir = Dir("w");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, dir);

        var instance = Inotify.Open();
        var plain = new Heard();
        var linked = new Heard();

        // Holds the instance open, so the watches can be counted after the
        // other two have gone.
        var keep = instance.Add(Dir("keep"), _ => { });

        var first = instance.Add(dir, plain.Add);
        var second = instance.Add(link, linked.Add);

        Assert.Equal(2, InotifyCount.Watches(instance.Descriptor));

        File.WriteAllText(Path.Combine(dir, "a"), "a");
        Assert.True(plain.WaitFor(Is(ChangeKind.Added, Path.Combine(dir, "a"))), plain.ToString());
        Assert.True(linked.WaitFor(Is(ChangeKind.Added, Path.Combine(link, "a"))), linked.ToString());

        first.Dispose();
        first.Dispose();

        Assert.Equal(2, InotifyCount.Watches(instance.Descriptor));

        File.WriteAllText(Path.Combine(dir, "b"), "b");
        Assert.True(linked.WaitFor(Is(ChangeKind.Added, Path.Combine(link, "b"))), "the second, after the first went: " + linked);

        Thread.Sleep(Settle);
        Assert.DoesNotContain(plain.All, c => c.Path.EndsWith("/b", StringComparison.Ordinal));

        // The kernel's watch goes with the last listener on it.
        second.Dispose();

        Assert.Equal(1, InotifyCount.Watches(instance.Descriptor));
        Assert.Equal(1, instance.Folders);

        keep.Dispose();
        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    /// <summary>
    /// A folder deleted and made again, watched again: the old watch's
    /// dispose — its kernel watch long dropped — leaves the new one alone.
    /// </summary>
    [PosixFact]
    public void Disposing_a_watch_whose_folder_went_leaves_the_new_one_alone()
    {
        var dir = Dir("w");
        var instance = Inotify.Open();
        var old = new Heard();
        var anew = new Heard();
        var keep = instance.Add(Dir("keep"), _ => { });

        try
        {
            var gone = instance.Add(dir, old.Add);

            Directory.Delete(dir);
            Assert.True(old.WaitFor(Is(ChangeKind.Gone, dir)), old.ToString());

            Directory.CreateDirectory(dir);

            using var again = instance.Add(dir, anew.Add);

            gone.Dispose();

            File.WriteAllText(Path.Combine(dir, "x"), "x");
            Assert.True(anew.WaitFor(Is(ChangeKind.Added, Path.Combine(dir, "x"))), "the new watch: " + anew);
        }
        finally
        {
            keep.Dispose();
        }

        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    /// <summary>
    /// **A watch disposed while its events wait hears none of them**, and the
    /// reader goes on for the others. The reader is held in another watch's
    /// callback while this one's folder changes; disposed then, it hears
    /// nothing once the reader is let go, and the other watch hears what
    /// came after.
    /// </summary>
    [PosixFact]
    public void A_watch_disposed_while_its_events_wait_hears_none_of_them()
    {
        var holding = Dir("holding");
        var pending = Dir("pending");
        var held = new Heard();
        var late = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        var first = instance.Add(holding, change =>
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(Ceiling);
            }

            held.Add(change);
        });

        var second = instance.Add(pending, late.Add);

        File.WriteAllText(Path.Combine(holding, "hold"), "");
        Assert.True(entered.Wait(Ceiling), "the reader never called back");

        for (var i = 0; i < 50; i++) File.WriteAllText(Path.Combine(pending, $"p{i}"), "p");

        second.Dispose();
        release.Set();

        // Read in order: once this is heard, everything before it has been.
        File.WriteAllText(Path.Combine(holding, "after"), "");
        Assert.True(held.WaitFor(Is(ChangeKind.Added, Path.Combine(holding, "after"))), held.ToString());

        Assert.Empty(late.All);

        first.Dispose();
        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    /// <summary>
    /// **Nor the event already being told.** Two watches on one folder: the
    /// reader is held in the first one's callback for an event it is telling
    /// both, and the second is disposed meanwhile — once the reader is let
    /// go, the second is not told that event. Whom to tell was settled before
    /// the dispose, so only the disposed flag can stop it.
    /// </summary>
    [PosixFact]
    public void A_watch_disposed_while_its_folder_is_being_told_hears_nothing_more()
    {
        var dir = Dir("w");
        var later = new Heard();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var instance = Inotify.Open();

        var first = instance.Add(dir, _ =>
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(Ceiling);
            }
        });

        var second = instance.Add(dir, later.Add);

        File.WriteAllText(Path.Combine(dir, "x"), "");
        Assert.True(entered.Wait(Ceiling), "the reader never called back");

        second.Dispose();
        release.Set();

        Thread.Sleep(Settle);
        Assert.Empty(later.All);

        first.Dispose();
        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    /// <summary>A watch that disposes itself from its own callback — the
    /// last one — closes the instance without waiting on itself.</summary>
    [PosixFact]
    public void A_watch_can_dispose_itself_from_its_callback()
    {
        var dir = Dir("w");
        var instance = Inotify.Open();
        IDisposable? watch = null;
        using var done = new ManualResetEventSlim();

        watch = instance.Add(dir, _ =>
        {
            watch!.Dispose();
            done.Set();
        });

        File.WriteAllText(Path.Combine(dir, "x"), "x");

        Assert.True(done.Wait(Ceiling), "never called back");
        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");
    }

    // ---- refusals ----------------------------------------------------------------

    /// <summary>A folder that is not there, or a file, is refused as
    /// FileSystemWatcher refused it — and an instance opened for it alone is
    /// not left open.</summary>
    [PosixFact]
    public void A_folder_that_is_not_there_is_refused_and_leaves_nothing_open()
    {
        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "f");
        var before = InotifyCount.Instances();

        var instance = Inotify.Open();
        Assert.Throws<DirectoryNotFoundException>(() => instance.Add(Path.Combine(_root, "missing"), _ => { }));
        Assert.True(instance.Exited.WaitOne(Ceiling), "the instance's reader did not stop");

        var again = Inotify.Open();
        Assert.Throws<DirectoryNotFoundException>(() => again.Add(file, _ => { }));
        Assert.True(again.Exited.WaitOne(Ceiling), "the instance's reader did not stop");

        Assert.Equal(before, InotifyCount.Instances());
        Assert.ThrowsAny<IOException>(() => Watch(Path.Combine(_root, "missing"), new Heard().Add));
    }

    /// <summary>
    /// **The ceiling is refused by name**, as an IOException — which the pane
    /// answers by reading the folder on a timer, and passes the message on.
    /// Every instance this user may have is taken here for a moment, and given
    /// back.
    /// </summary>
    [PosixFact]
    public void The_instance_ceiling_is_refused_by_name()
    {
        var taken = new List<IDisposable>();
        var instances = new List<Inotify>();
        IOException? refused = null;

        try
        {
            for (var i = 0; i < 4096 && refused is null; i++)
            {
                try
                {
                    var instance = Inotify.Open();
                    instances.Add(instance);
                    taken.Add(instance.Add(_root, _ => { }));
                }
                catch (IOException e)
                {
                    refused = e;
                }
            }
        }
        finally
        {
            foreach (var watch in taken) watch.Dispose();
        }

        Assert.NotNull(refused);
        Assert.Contains("max_user_instances", refused.Message);
        Assert.All(instances, i => Assert.True(i.Exited.WaitOne(Ceiling)));
    }

    [Fact]
    public void The_watch_ceiling_is_refused_by_name()
    {
        var refused = Inotify.Refused(28, "/x");

        Assert.IsType<IOException>(refused);
        Assert.Contains("max_user_watches", refused.Message);
    }
}

/// <summary>
/// A fact that needs an unprivileged user and mount namespace (unshare -rm),
/// probed by making one at discovery. Skips where that is refused — a
/// container, a kernel with user namespaces off.
/// </summary>
public sealed class UnshareFactAttribute : FactAttribute
{
    private static readonly bool Works = Probe();

    public UnshareFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Needs a real POSIX filesystem; runs on Linux only.";
        else if (!Works)
            Skip = "Needs an unprivileged user and mount namespace; unshare -rm was refused here.";
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            using var p = Process.Start(new ProcessStartInfo("unshare", ["-rm", "true"])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });

            if (p is null) return false;
            if (!p.WaitForExit(5000)) { p.Kill(); return false; }

            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
