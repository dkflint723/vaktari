using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What a waiting folder hears, and what it does when it hears nothing
/// (batch-0.11.2d QA, round 5): a folder made again over SMB arrives as
/// Changed, never Added; a watch opened on a folder in the moment it is
/// deleted can go dead without saying Gone; and with a next step spelled as
/// an 8.3 short name, an arrival is matched by its own short name rather than
/// by asking whether the folder is back.
/// </summary>
public sealed class FolderReturnWatchHearingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-hearing").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Watches that hear only what the test says — or nothing at all.</summary>
    private sealed class Provider : IFileSystemProvider
    {
        public readonly List<(string Path, Action<FileSystemChange> Heard)> Given = [];

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            lock (Given) Given.Add((path, onChange));
            return new Off();
        }

        public Action<FileSystemChange> Last
        {
            get { lock (Given) return Given[^1].Heard; }
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
    /// **A folder made again over a share arrives as Changed** (batch-0.11.2d
    /// QA, round 5: the redirector reported it so, never as Added, and the
    /// wait over UNC missed it in 14 rounds of 20). Changed on the next step
    /// asks; Changed on anything else does not.
    /// </summary>
    [Fact]
    public void A_folder_back_that_arrives_as_changed_is_heard()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, target, () => told++, null, Directory.Exists, TimeSpan.FromHours(1));
        var checks = wait.Checks;

        Directory.CreateDirectory(target);
        fs.Last(new FileSystemChange(ChangeKind.Changed, Path.Combine(_root, "log.txt")));

        Assert.Equal(checks, wait.Checks);
        Assert.Equal(0, told);

        fs.Last(new FileSystemChange(ChangeKind.Changed, target));

        Assert.Equal(1, told);
    }

    /// <summary>
    /// **A watch that never says anything does not leave the wait deaf for
    /// good** (batch-0.11.2d QA, round 5: on Windows a watch opened on a folder
    /// in the moment it is deleted went dead without a Gone, and 1 to 7 of 8
    /// waits under a flickering folder never heard it come back). Every watch
    /// here is dead; the slow look finds the folder all the same.
    /// </summary>
    [PoolWorkersFact]
    public async Task With_a_watch_that_never_says_anything_the_slow_look_finds_it()
    {
        var target = Path.Combine(_root, "x");
        var fs = new Provider();
        using var back = new SemaphoreSlim(0);

        using var wait = new FolderReturnWatch(fs, target, () => back.Release(), null, Directory.Exists, TimeSpan.FromMilliseconds(50));

        Assert.Equal(_root, wait.Watching);

        Directory.CreateDirectory(target);

        Assert.True(await back.WaitAsync(TimeSpan.FromSeconds(30)), "nothing asked again while a dead watch was held");
    }

    /// <summary>
    /// **With a short next step, only its own arrival asks** (batch-0.11.2d
    /// QA, round 5: every arrival asked whether the folder was back, and 100
    /// such waits under a busy folder made 20,000 files take 73 s). An arrival
    /// whose short name is another is a short name read and compared; the
    /// folder's own, under its long name, as Added or Changed, is heard.
    /// </summary>
    [ShortNamesFact]
    public void With_a_short_next_step_other_arrivals_ask_nothing()
    {
        var spelled = Path.Combine(_root, "TARGET~1");
        var made = Path.Combine(_root, "TargetFolderName");

        Directory.CreateDirectory(made);
        Assert.True(Directory.Exists(spelled), "the volume gave the folder another short name");
        Directory.Delete(made);

        var fs = new Provider();
        var told = 0;

        using var wait = new FolderReturnWatch(fs, spelled, () => told++, null, Directory.Exists, TimeSpan.FromHours(1));
        var checks = wait.Checks;

        var other = Path.Combine(_root, "download.part");
        File.WriteAllText(other, "x");
        fs.Last(new FileSystemChange(ChangeKind.Added, other));
        fs.Last(new FileSystemChange(ChangeKind.Changed, other));
        fs.Last(new FileSystemChange(ChangeKind.Added, Path.Combine(_root, "already-gone")));

        Assert.Equal(checks, wait.Checks);

        Directory.CreateDirectory(made);
        fs.Last(new FileSystemChange(ChangeKind.Changed, made));

        Assert.Equal(1, told);
    }
}
