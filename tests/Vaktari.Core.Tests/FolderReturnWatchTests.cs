using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Waiting for a folder that is not there to come back (batch-0.11.2c QA:
/// a pane left saying "that folder is not there any more" after Undo put its
/// folder back). The provider here is a fake that records which folders are
/// watched and lets the test say what each one heard, so every step is the
/// test's own and nothing waits on a timer — except the one test about a
/// folder above that cannot be watched, which is read on a short one. The
/// folders themselves are real temporary ones, because whether the folder is
/// back is asked of the disk.
/// </summary>
public sealed class FolderReturnWatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-return").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string At(params string[] names) => Path.Combine([_root, .. names]);

    /// <summary>Which folders are watched, and the callback each was given.</summary>
    private sealed class Watches : IFileSystemProvider
    {
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Live = [];

        public bool Refuse { get; set; }

        /// <summary>How many watches have been opened, ever.</summary>
        public int Opened { get; private set; }

        /// <summary>Run while a watch is being set up, before it is handed
        /// back — the moment in which a name can arrive unheard.</summary>
        public Action<string>? WhileWatching { get; set; }

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            if (Refuse) throw new IOException("no watcher can be started here");

            Opened++;

            WhileWatching?.Invoke(path);

            var entry = (path, onChange);
            lock (Live) Live.Add(entry);
            return new Off(() => { lock (Live) Live.Remove(entry); });
        }

        public string[] Paths
        {
            get { lock (Live) return Live.Select(l => l.Path).ToArray(); }
        }

        /// <summary>Tells whoever watches <paramref name="folder"/> that
        /// <paramref name="change"/> happened.</summary>
        public void Say(string folder, FileSystemChange change)
        {
            Action<FileSystemChange>[] heard;
            lock (Live) heard = Live.Where(l => l.Path == folder).Select(l => l.Heard).ToArray();
            foreach (var h in heard) h(change);
        }

        private sealed class Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);

        public string? GetParent(string path) => Path.GetDirectoryName(path);

        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    [Fact]
    public void A_folder_already_back_is_said_at_once_and_nothing_is_watched()
    {
        var x = Directory.CreateDirectory(At("x")).FullName;
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);

        Assert.Equal(1, said);
        Assert.Empty(fs.Paths);
        Assert.Null(wait.Watching);
    }

    [Fact]
    public void A_missing_folder_is_waited_for_from_the_folder_above_and_said_once()
    {
        var x = At("x");
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);

        Assert.Equal([_root], fs.Paths);
        Assert.Equal(_root, wait.Watching);

        // Something else arriving brings nothing back.
        Directory.CreateDirectory(At("other"));
        fs.Say(_root, new FileSystemChange(ChangeKind.Added, At("other")));
        Assert.Equal(0, said);

        Directory.CreateDirectory(x);
        fs.Say(_root, new FileSystemChange(ChangeKind.Renamed, x, At("x-away")));

        Assert.Equal(1, said);
        Assert.Empty(fs.Paths);

        // Once said, never again.
        fs.Say(_root, new FileSystemChange(ChangeKind.Added, x));
        Assert.Equal(1, said);
    }

    /// <summary>
    /// **Only the name on the way down is looked into** (batch-0.11.2c QA,
    /// round 3: a busy folder above, ten or a hundred waits, two disk checks
    /// each per arrival on the watcher's one thread, and the kernel's queue
    /// overflowed). The folder is made without a word; an arrival of anything
    /// else asks nothing, so the wait does not notice — the arrival of the
    /// folder's own name does.
    /// </summary>
    [Fact]
    public void Only_the_name_on_the_way_down_is_looked_into()
    {
        var x = At("x");
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);
        var checks = wait.Checks;

        Directory.CreateDirectory(x);
        fs.Say(_root, new FileSystemChange(ChangeKind.Added, At("download.part")));
        fs.Say(_root, new FileSystemChange(ChangeKind.Renamed, At("download.zip"), At("download.part")));

        Assert.Equal(0, said);
        Assert.Equal(checks, wait.Checks);

        fs.Say(_root, new FileSystemChange(ChangeKind.Added, x));

        Assert.Equal(1, said);
    }

    /// <summary>
    /// **The folder above going is watched afresh, even at the same path**
    /// (batch-0.11.2c QA, round 3). Deleted and made again before its going
    /// is read, it is the same path and a new folder; the old watch is on the
    /// old one, and hears nothing again.
    /// </summary>
    [Fact]
    public void The_folder_above_gone_and_made_again_is_watched_afresh()
    {
        var p = Directory.CreateDirectory(At("p")).FullName;
        var x = At("p", "x");
        var fs = new Watches();

        using var wait = new FolderReturnWatch(fs, x, () => { });
        var first = fs.Live.Single().Heard;

        Directory.Delete(p);
        Directory.CreateDirectory(p);
        first(new FileSystemChange(ChangeKind.Gone, p));

        var now = fs.Live.Single();

        Assert.Equal(p, now.Path);
        Assert.Equal(2, fs.Opened);
    }

    /// <summary>
    /// **A folder on the way down made while the watch above is set up is
    /// looked for once the watch is there.** Its arrival came before anything
    /// was listening, so without that second look the wait sat above it,
    /// hearing nothing of what was made inside (measured over inotify: the
    /// folder above deleted and made again at once, 16 rounds in 20 missed).
    /// </summary>
    [Fact]
    public void A_folder_on_the_way_down_made_while_watching_is_found()
    {
        var p = At("p");
        var x = At("p", "x");
        var fs = new Watches();

        fs.WhileWatching = path =>
        {
            if (path == _root) Directory.CreateDirectory(p);
        };

        using var wait = new FolderReturnWatch(fs, x, () => { });

        Assert.Equal(p, wait.Watching);
        Assert.Equal([p], fs.Paths);
    }

    /// <summary>A caller that knows the disk changed — the places list moved —
    /// asks again, and a folder back without a word is found.</summary>
    [Fact]
    public void Asked_again_it_finds_a_folder_that_came_back_unheard()
    {
        var x = At("x");
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);

        Directory.CreateDirectory(x);
        Assert.Equal(0, said);

        wait.Recheck();

        Assert.Equal(1, said);
        Assert.Empty(fs.Paths);
    }

    [Fact]
    public void A_file_written_or_removed_above_does_not_ask_again()
    {
        var x = At("x");
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);

        // Made without a word, then only changes that bring no folder back.
        Directory.CreateDirectory(x);
        fs.Say(_root, new FileSystemChange(ChangeKind.Changed, At("log.txt")));
        fs.Say(_root, new FileSystemChange(ChangeKind.Removed, At("old.txt")));

        Assert.Equal(0, said);

        fs.Say(_root, new FileSystemChange(ChangeKind.Lost, _root));

        Assert.Equal(1, said);
    }

    [Fact]
    public void With_the_folder_above_gone_too_it_waits_higher_and_comes_down_again()
    {
        var p = At("p");
        var x = At("p", "x");
        var fs = new Watches();
        var said = 0;

        using var wait = new FolderReturnWatch(fs, x, () => said++);

        Assert.Equal([_root], fs.Paths);

        Directory.CreateDirectory(p);
        fs.Say(_root, new FileSystemChange(ChangeKind.Added, p));

        Assert.Equal([p], fs.Paths);
        Assert.Equal(0, said);

        Directory.CreateDirectory(x);
        fs.Say(p, new FileSystemChange(ChangeKind.Added, x));

        Assert.Equal(1, said);
        Assert.Empty(fs.Paths);
    }

    [Fact]
    public void The_folder_watched_going_moves_the_wait_up()
    {
        var p = Directory.CreateDirectory(At("p")).FullName;
        var x = At("p", "x");
        var fs = new Watches();

        using var wait = new FolderReturnWatch(fs, x, () => { });

        Assert.Equal([p], fs.Paths);

        Directory.Delete(p);
        fs.Say(p, new FileSystemChange(ChangeKind.Gone, p));

        Assert.Equal([_root], fs.Paths);
        Assert.Equal(_root, wait.Watching);
    }

    [Fact]
    public void Disposed_it_says_nothing_and_watches_nothing()
    {
        var x = At("x");
        var fs = new Watches();
        var said = 0;

        var wait = new FolderReturnWatch(fs, x, () => said++);
        var heard = fs.Live.Single().Heard;

        wait.Dispose();

        Assert.Empty(fs.Paths);

        Directory.CreateDirectory(x);
        heard(new FileSystemChange(ChangeKind.Added, x));

        Assert.Equal(0, said);
    }

    [Fact]
    public async Task A_folder_above_that_cannot_be_watched_is_read_on_a_timer()
    {
        var x = At("x");
        var fs = new Watches { Refuse = true };
        using var back = new SemaphoreSlim(0);

        using var wait = new FolderReturnWatch(fs, x, () => back.Release(), TimeSpan.FromMilliseconds(50));

        Assert.Equal(_root, wait.Watching);

        Directory.CreateDirectory(x);

        Assert.True(await back.WaitAsync(TimeSpan.FromSeconds(30)), "the timer never noticed the folder back");
    }
}
