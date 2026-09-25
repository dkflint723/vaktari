using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// How long the Size column believes what it was told about a folder.
///
/// **For the whole session.** The answers were kept under the path alone, and
/// nothing but the count eviction ever removed one — so with the default
/// setting, the item count, a folder that gained or lost files went on reading
/// its first count through F5, through the watcher and through every operation
/// that changed it. A folder that could not be read kept its em dash for the
/// session the same way.
///
/// The fill is driven for real here: a TextBlock given a row, a provider that
/// counts how often it is asked, and the cell's own text read back. The
/// provider answers "asked N times", so a figure that did not move is a figure
/// that came out of the cache.
/// </summary>
public sealed class FolderSizeCacheTests : OwnedViewModels
{
    private readonly IFileMetadataProvider? _providerBefore = RowMetadata.Provider;

    // Apply publishes to a static every class reads, and these classes run in
    // sequence, so a leak lands on somebody else's assertion.
    private readonly SettingsState _settingsBefore = AppSettings.Current;

    private readonly Asked _asked = new();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-size-cache-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderSizeCacheTests()
    {
        Directory.CreateDirectory(_root);

        RowMetadata.Provider = _asked;
        Folders(FolderSizeMode.ItemCount);
    }

    public override void Dispose()
    {
        base.Dispose();

        RowMetadata.Provider = _providerBefore;
        AppSettings.Apply(_settingsBefore);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private static void Folders(FolderSizeMode mode)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with
            {
                Details = AppSettings.Current.Views.Details with { FolderSize = mode },
            },
        });

    private static readonly DateTimeOffset Then = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private FileEntry Row(string relative, DateTimeOffset? modified = null)
        => new(Path.GetFileName(relative), Path.Combine(_root, relative), 0,
               modified ?? Then, EntryFlags.Directory);

    /// <summary>Counts every question and answers with the count, so the
    /// text on the cell says which question it came from.</summary>
    private sealed class Asked : IFileMetadataProvider
    {
        private int _count;

        public bool CanDescribe(string path, bool isDirectory) => true;

        public ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct)
            => ValueTask.FromResult<string?>($"asked {Interlocked.Increment(ref _count)}");

        public ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    /// <summary>
    /// What a fresh cell shows for this row once its fill has finished.
    ///
    /// Waited on the cache rather than on the text, because one of the answers
    /// under test is to leave the em dash standing — which is also what the
    /// cell says before the fill has run.
    /// </summary>
    private static async Task<string?> Shown(FileEntry row)
    {
        var fill = AppSettings.Current.Views.Details.FolderSize == FolderSizeMode.ContentSize
            ? RowMetadata.SizeFill.Measure
            : RowMetadata.SizeFill.Count;

        var key = RowMetadata.CacheKey(row, fill);
        var cell = new TextBlock();

        RowMetadata.SetSize(cell, row);

        for (var i = 0; i < 400 && !RowMetadata.Holds(key); i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(RowMetadata.Holds(key), $"the fill for {row.FullPath} never finished");

        // The answer is painted from a dispatcher job queued after it is kept.
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(1);
            Dispatcher.UIThread.RunJobs();
        }

        return cell.Text;
    }

    // ---- the key ------------------------------------------------------------

    /// <summary>
    /// A GUARD for the two below: asked twice about a folder that has not
    /// changed, the cell answers from the cache. Without it, "asked again" in
    /// the tests that follow could be a cache that no longer holds anything.
    /// </summary>
    [AvaloniaFact]
    public async Task An_unchanged_folder_is_counted_once()
    {
        var row = Row("same");

        Assert.Equal("asked 1", await Shown(row));
        Assert.Equal("asked 1", await Shown(row));
    }

    /// <summary>
    /// **The finding, for the default setting.** Adding or removing something
    /// moves the folder's modified time, and the row the listing hands the
    /// cell after a refresh or a watcher event carries the new one. Keyed on
    /// the path alone, that row found the first answer waiting and never
    /// asked.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_changed_since_it_was_counted_is_counted_again()
    {
        var before = Row("changing");

        Assert.Equal("asked 1", await Shown(before));

        var after = before with { LastWriteTime = Then.AddSeconds(1) };

        Assert.Equal("asked 2", await Shown(after));
    }

    // ---- forgetting ---------------------------------------------------------

    [AvaloniaFact]
    public async Task Forgetting_a_folder_drops_what_was_counted_under_it()
    {
        var row = Row(Path.Combine("a", "b"));

        Assert.Equal("asked 1", await Shown(row));

        RowMetadata.Forget(Path.Combine(_root, "a"));

        Assert.Equal("asked 2", await Shown(row));
    }

    /// <summary>
    /// A GUARD: "under" is at a separator, and PathRules.Contains already
    /// holds that rule and its own tests. Here so a hand-rolled StartsWith
    /// replacing it would be caught at this door as well.
    /// </summary>
    [AvaloniaFact]
    public async Task Forgetting_a_folder_leaves_one_that_only_starts_with_its_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ab"));

        var row = Row("ab");

        Assert.Equal("asked 1", await Shown(row));

        RowMetadata.Forget(Path.Combine(_root, "a"));

        Assert.Equal("asked 1", await Shown(row));
    }

    /// <summary>
    /// **A total several folders up is the one answer a changed time cannot
    /// reach.** A file arriving in a/deep moves deep's modified time and not
    /// a's, so the row for "a" is the same row it was and its old total was
    /// served however often the listing was refreshed.
    /// </summary>
    [AvaloniaFact]
    public async Task Forgetting_a_path_drops_the_totals_that_include_it()
    {
        Folders(FolderSizeMode.ContentSize);

        var deep = Directory.CreateDirectory(Path.Combine(_root, "a", "deep")).FullName;
        File.WriteAllBytes(Path.Combine(deep, "one.bin"), new byte[10]);

        var row = Row("a");

        Assert.Equal(ByteSize.Format(10), await Shown(row));

        File.WriteAllBytes(Path.Combine(deep, "two.bin"), new byte[10]);

        // What the finding describes: the same row, the same stale total.
        Assert.Equal(ByteSize.Format(10), await Shown(row));

        RowMetadata.Forget(deep);

        Assert.Equal(ByteSize.Format(20), await Shown(row));
    }

    // ---- and who forgets ----------------------------------------------------

    /// <summary>
    /// **F5 re-read the folder and left every folder's size as it was.** The
    /// row it hands back is the same row — nothing in the folder moved its
    /// time — so only forgetting can make the cell ask again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_forgets_the_sizes_it_showed()
    {
        var fs = new Listing(_root, Row("sub"));
        var pane = Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);

        Assert.Equal("asked 1", await Shown(pane.Entries.Single()));

        await pane.RefreshAsync();

        Assert.Equal("asked 2", await Shown(pane.Entries.Single()));
    }

    /// <summary>
    /// **Nor only where the pane is.** A Copy to lands in a folder this pane is
    /// not showing, and its refresh forgets only the folder it is showing — so
    /// the destination's answers have to go when the operation finishes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_finished_operation_forgets_the_sizes_where_it_landed()
    {
        var fs = new Listing(_root);
        var pane = Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);

        var elsewhere = Path.Combine(_root + "-elsewhere", "dest");
        var inside = new FileEntry("inner", Path.Combine(elsewhere, "inner"), 0, Then, EntryFlags.Directory);

        Assert.Equal("asked 1", await Shown(inside));

        var copy = new OperationHandle
        {
            Paths = [Path.Combine(_root, "source.txt"), elsewhere],
            Kind = OperationKind.Copy,
        };

        pane.Adopt(copy);
        copy.Begin(1, 0);
        copy.Complete();

        string? now = null;

        for (var i = 0; i < 100 && now != "asked 2"; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();

            now = await Shown(inside);
        }

        Assert.Equal("asked 2", now);
    }

    /// <summary>
    /// **A finished operation froze the window once per file it touched.** Its
    /// paths are every source and the destination, and each was forgotten on
    /// its own — a pass over the whole cache per path, on the UI thread, so a
    /// select-all delete in a folder of twenty thousand files was twenty
    /// thousand passes before the listing could come back. The answers come out
    /// the same either way; the number of passes is the whole of the cost.
    ///
    /// The tab is closed before the operation finishes, so the refresh that
    /// forgets the folder on screen does not run and the one pass counted is
    /// the operation's own.
    /// </summary>
    [AvaloniaFact]
    public async Task A_finished_operation_forgets_all_its_paths_in_one_pass()
    {
        var fs = new Listing(_root);
        var pane = Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);

        var delete = new OperationHandle
        {
            Paths = [.. Enumerable.Range(0, 500).Select(i => Path.Combine(_root, $"file{i}.txt"))],
            Kind = OperationKind.Delete,
        };

        pane.Adopt(delete);
        pane.Dispose();

        var before = RowMetadata.Passes;

        delete.Begin(1, 0);
        delete.Complete();

        for (var i = 0; i < 400 && RowMetadata.Passes == before; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        // Anything still to come would come from the same dispatcher job.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 1, RowMetadata.Passes);
    }

    /// <summary>
    /// A GUARD on the single pass: every path it is given is forgotten, not
    /// only the first or the last — a set built wrong would pass the count
    /// above and forget almost nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Forgetting_several_paths_forgets_under_each_of_them()
    {
        var first = Row(Path.Combine("one", "inner"));
        var second = Row(Path.Combine("two", "inner"));
        var neither = Row(Path.Combine("three", "inner"));

        Assert.Equal("asked 1", await Shown(first));
        Assert.Equal("asked 2", await Shown(second));
        Assert.Equal("asked 3", await Shown(neither));

        RowMetadata.Forget([Path.Combine(_root, "one"), Path.Combine(_root, "two")]);

        Assert.Equal("asked 4", await Shown(first));
        Assert.Equal("asked 5", await Shown(second));
        Assert.Equal("asked 3", await Shown(neither));
    }

    // ---- an answer still on its way ----------------------------------------

    /// <summary>Asks nothing of the disk and answers only when told to, so a
    /// test can forget a folder while its answer is still being fetched.</summary>
    private sealed class Held : IFileMetadataProvider
    {
        private readonly TaskCompletionSource<string?> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Asked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Answer(string text) => _answer.TrySetResult(text);

        public bool CanDescribe(string path, bool isDirectory) => true;

        public async ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct)
        {
            Asked.TrySetResult();

            return await _answer.Task.ConfigureAwait(false);
        }

        public ValueTask<string?> DescribeAccessAsync(string path, bool isDirectory, CancellationToken ct)
            => ValueTask.FromResult<string?>(null);
    }

    /// <summary>
    /// **An answer still being fetched when its folder was forgotten was kept
    /// anyway.** Forget drops what the cache holds, and an answer in flight is
    /// not held yet: a copy into a/b/c that finished while the Size column was
    /// walking "a" forgot nothing, and the walk then kept a total taken before
    /// the copy under a key nothing would ever move. The count and the details
    /// line fetch the same way, and both are driven here; the answer is still
    /// painted, and only the keeping is refused.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_in_flight_when_its_folder_is_forgotten_is_not_kept(bool sizeCell)
    {
        var held = new Held();
        RowMetadata.Provider = held;

        var row = Row(Path.Combine("a", "b"));
        var cell = new TextBlock();

        if (sizeCell) RowMetadata.SetSize(cell, row);
        else RowMetadata.SetEntry(cell, row);

        for (var i = 0; i < 400 && !held.Asked.Task.IsCompleted; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(held.Asked.Task.IsCompleted, "GUARD: the fill never asked, so nothing was in flight");

        RowMetadata.Forget(Path.Combine(_root, "a"));

        held.Answer("counted before the change");

        for (var i = 0; i < 400 && cell.Text != "counted before the change"; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("counted before the change", cell.Text);
        Assert.False(
            RowMetadata.Holds(RowMetadata.CacheKey(row, RowMetadata.SizeFill.Count)),
            "an answer fetched across a Forget was kept");
    }

    // ---- a folder that cannot be read ---------------------------------------

    /// <summary>
    /// **"0 B" for a folder nothing could be read from.** Measuring one it
    /// cannot open answers an empty total with one unreadable folder, and the
    /// cell printed the empty total, where the doc on the measurement promises
    /// the em dash a failed count leaves.
    ///
    /// Windows: a deny on listing the folder is something the person running
    /// the test can place and take off again without elevation.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    [SupportedOSPlatform("windows")]
    public async Task A_folder_that_cannot_be_opened_keeps_the_dash()
    {
        Folders(FolderSizeMode.ContentSize);

        var locked = new DirectoryInfo(Path.Combine(_root, "locked"));
        locked.Create();
        File.WriteAllBytes(Path.Combine(locked.FullName, "inside.bin"), new byte[10]);

        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

        var security = locked.GetAccessControl();
        security.AddAccessRule(rule);
        locked.SetAccessControl(security);

        try
        {
            Assert.Equal("—", await Shown(Row("locked")));
        }
        finally
        {
            security = locked.GetAccessControl();
            security.RemoveAccessRule(rule);
            locked.SetAccessControl(security);
        }
    }

    /// <summary>One folder whose listing is whatever rows it was given, every
    /// time it is read.</summary>
    private sealed class Listing(string root, params FileEntry[] rows) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return PathRules.Same(path, root) ? rows : [];
        }

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
