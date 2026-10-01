using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The two edges of FolderReturnWatch its own tests left open (batch-0.11.2c
/// QA, round 3: each line could be taken out with every test still green).
///
/// A folder made between the look and the watch is never heard arriving, so
/// the wait looks once more after the watch is there; and a change already on
/// its way when the wait ended must not say the folder is back a second time.
/// Both are driven through a fake provider that does what the kernel can: make
/// the folder while a watch is being set up, and hand a callback a change after
/// the watch it came from was let go.
/// </summary>
public sealed class FolderReturnWatchEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-return-edge").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Provider : IFileSystemProvider
    {
        /// <summary>Run while a watch is being set up, before it is handed back.</summary>
        public Action<string>? WhileWatching { get; set; }

        /// <summary>Every callback ever given, live or not.</summary>
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        public int Live;

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            Given.Add((path, onChange));
            WhileWatching?.Invoke(path);
            Live++;
            return new Off(this);
        }

        private sealed class Off(Provider owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                owner.Live--;
            }
        }

        public IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(string path, ListingOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);

        public string? GetParent(string path) => Path.GetDirectoryName(path);

        public bool IsCaseSensitive => !OperatingSystem.IsWindows();
    }

    /// <summary>
    /// **Made while the watch above was being set up, and said all the same.**
    /// The folder was not there when the wait looked, and is there before the
    /// watch can hear anything — so no arrival will ever be heard, and only the
    /// second look finds it.
    /// </summary>
    [Fact]
    public void A_folder_made_while_the_watch_is_set_up_is_still_said()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider { WhileWatching = _ => Directory.CreateDirectory(target) };
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++);

        Assert.Equal(1, told);
        Assert.Null(wait.Watching);
        Assert.Equal(0, fs.Live);
    }

    /// <summary>
    /// **Said once, even with a change still on its way.** The folder comes
    /// back and is said; a second arrival that was already being delivered
    /// through the watch the wait has just let go — the reader had read it,
    /// or a poll had started — reaches the wait afterwards, and must not say
    /// it again.
    /// </summary>
    [Fact]
    public void A_change_still_on_its_way_after_the_return_does_not_say_it_twice()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++);

        var (watched, heard) = Assert.Single(fs.Given);
        Assert.Equal(_root, watched);

        Directory.CreateDirectory(target);
        heard(new FileSystemChange(ChangeKind.Added, target));

        Assert.Equal(1, told);
        Assert.Equal(0, fs.Live);

        // Late: through the callback of the watch already let go.
        heard(new FileSystemChange(ChangeKind.Added, Path.Combine(_root, "y")));
        heard(new FileSystemChange(ChangeKind.Lost, _root));

        Assert.Equal(1, told);
        Assert.Equal(0, fs.Live);
    }

    /// <summary>
    /// **Asked again, it watches the folder above afresh, though the path is
    /// the same** (batch-0.11.2c QA, round 4: Recheck asking without force
    /// left every test green). A drive mounted again over the folder watched
    /// is a new folder at the same path, and the old watch is on the one the
    /// mount now covers: it hears nothing made on the drive. The folder is
    /// still not back, so nothing is said — but the watch is a new one, and
    /// the old one is let go.
    /// </summary>
    [Fact]
    public void Asked_again_with_nothing_back_it_watches_the_same_folder_afresh()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++);

        var (first, _) = Assert.Single(fs.Given);
        Assert.Equal(_root, first);

        wait.Recheck();

        Assert.Equal(0, told);
        Assert.Equal(2, fs.Given.Count);
        Assert.Equal(_root, fs.Given[1].Path);
        Assert.Equal(1, fs.Live);
        Assert.Equal(_root, wait.Watching);

        // And the new watch is the one that hears the folder come back.
        Directory.CreateDirectory(target);
        fs.Given[1].Heard(new FileSystemChange(ChangeKind.Added, target));

        Assert.Equal(1, told);
        Assert.Equal(0, fs.Live);
    }
}
