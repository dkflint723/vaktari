using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Vcs;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A folder whose watcher cannot start.
///
/// **The pane went on listing and stopped following the folder, and said
/// nothing.** StartWatching and StartWatchingRepository swallowed whatever
/// Watch threw. On Linux, Watch throws once the user's inotify instances are
/// used up — each watcher is one, and fs.inotify.max_user_instances is 128 by
/// default. Measured 2026-09-11 in WSL Fedora: a full Ui run peaked at 120
/// instances in use, and when it tipped over, a pane silently had no watcher
/// and a test waiting for a file to appear in that folder timed out. Measured
/// again 2026-09-13 on .NET 10.0.11 in WSL Fedora 44: five watchers in one
/// process held five instances, and once the user's instances were used up,
/// starting another threw an IOException naming that limit — in a process
/// already running a watcher as much as in one that was not. A person with
/// enough tabs and windows open meets the same ceiling, and the only sign is a
/// folder that stops updating.
///
/// The folder here is real, made by this class and removed with it, because
/// what is under test is a file arriving in it — something no fake listing can
/// stand in for. The poll is shortened through <see cref="PaneViewModel"/>'s
/// PollInterval, and the test about a repository hands the pane a version
/// control of its own; both are put back afterwards.
/// </summary>
public sealed class UnwatchableFolderTests : OwnedViewModels
{
    private const string Notice = "checked every few seconds";

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-unwatchable").FullName;

    private readonly TimeSpan? _pollBefore = PaneViewModel.PollInterval;

    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    public UnwatchableFolderTests()
    {
        // Short enough that a wait sees many reads rather than racing one.
        PaneViewModel.PollInterval = TimeSpan.FromMilliseconds(50);
    }

    /// <summary>The pane first, so its watch stops reading before the folder
    /// goes; then only the folder this class made.</summary>
    public override void Dispose()
    {
        PaneViewModel.PollInterval = _pollBefore;

        base.Dispose();

        PaneViewModel.Vcs = _vcsBefore;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private async Task<PaneViewModel> Opened(bool watchable)
    {
        var pane = Own(new PaneViewModel(new RealFolder(watchable)) { ViewportWidth = 1400 });

        await pane.NavigateAsync(_root);
        await Until(() => pane.IsLoaded, "the folder never finished loading");

        return pane;
    }

    /// <summary>
    /// **A file arriving in a folder that cannot be watched still appears.**
    /// Nothing in this test refreshes the pane — no F5, no navigation — so the
    /// only thing that can put the new row on screen is a watcher, or whatever
    /// stands in for one when a watcher cannot be had.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_arriving_in_a_folder_that_cannot_be_watched_still_appears()
    {
        File.WriteAllText(Path.Combine(_root, "already.txt"), "here");

        var pane = await Opened(watchable: false);

        Assert.Single(pane.Entries);

        File.WriteAllText(Path.Combine(_root, "arrived.txt"), "new");

        await Until(
            () => pane.Entries.Any(e => e.Name == "arrived.txt"),
            "a file arrived in the folder and the pane never noticed");
    }

    /// <summary>
    /// **And the person is told.** A folder read every few seconds lags behind
    /// one that is watched, and a lag with no reason given reads as the
    /// application being slow; the silence was half of the fault.
    /// </summary>
    [AvaloniaFact]
    public async Task The_pane_says_the_folder_is_checked_rather_than_watched()
    {
        var pane = await Opened(watchable: false);

        await Until(
            () => (pane.Status ?? "").Contains(Notice, StringComparison.Ordinal),
            $"the pane said nothing about the folder not being watched; it said \"{pane.Status}\"");
    }

    /// <summary>
    /// **And it went on being told only until the poll found something.** Every
    /// change the watch reports ends in the settle tick, which rewrites the
    /// status with the count — and for an unfiltered folder that was nothing at
    /// all, so the first file to arrive took the notice with it while the lag
    /// it explains stayed.
    ///
    /// The settle tick raises ListingSettled on the line after it writes the
    /// status, so a settle counted after the file arrived is that write done.
    /// </summary>
    [AvaloniaFact]
    public async Task The_notice_outlives_the_first_change_the_poll_finds()
    {
        var pane = await Opened(watchable: false);

        await Until(
            () => (pane.Status ?? "").Contains(Notice, StringComparison.Ordinal),
            "GUARD: the load never gave the notice in the first place");

        var settled = 0;
        pane.ListingSettled += (_, _) => settled++;

        File.WriteAllText(Path.Combine(_root, "arrived.txt"), "new");

        await Until(
            () => pane.Entries.Any(e => e.Name == "arrived.txt") && settled > 0,
            "the poll never reported the file, or the listing never settled after it");

        Assert.Contains(Notice, pane.Status ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD, not a test of the change, and it says so: a folder whose watcher
    /// starts is not given the notice. It passes before the change as well —
    /// it is here so the notice cannot become something every folder says.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_can_be_watched_is_not_given_the_notice()
    {
        var pane = await Opened(watchable: true);

        Assert.DoesNotContain(Notice, pane.Status ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **The repository's marks follow it as well.** A commit rewrites
    /// .git/index and nothing the listing shows, which is why the metadata
    /// folder has a watcher of its own — and when that one could not start
    /// either, a commit made in a terminal never reached the marks.
    ///
    /// Only the index is written here, into a file already there, so the
    /// folder's own poll, which sees .git as one unchanged row, has nothing to
    /// report. The one thing that can ask for the status again is whatever
    /// follows .git.
    /// </summary>
    [AvaloniaFact]
    public async Task A_commit_in_a_repository_that_cannot_be_watched_still_reaches_the_marks()
    {
        var index = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName, "index");
        File.WriteAllText(index, "before");

        var vcs = new CountingVcs(_root);
        PaneViewModel.Vcs = vcs;

        var pane = await Opened(watchable: false);

        // Set on the dispatcher job that starts following .git, on the line
        // after it — so once this is true, whatever follows .git has already
        // read the index as it was.
        await Until(() => pane.IsRepository, "the pane never took the folder for a repository");

        var asked = vcs.Asked;

        File.WriteAllText(index, "after a commit, and longer");

        await Until(
            () => vcs.Asked > asked,
            "the index changed and the pane never asked for the repository's status again");
    }

    private static async Task Until(Func<bool> condition, string because, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.True(condition(), because);
    }

    /// <summary>
    /// A real folder, read as it is, whose watcher either refuses to start —
    /// the shape the pane meets when the platform has no watchers left to give
    /// — or starts and reports nothing.
    ///
    /// <see cref="GetEntryAsync"/> answers from the disk because that is how an
    /// arrival becomes a row: a reported path goes through Queue and Drain to
    /// StatAndApplyAsync, which asks this for the entry. A fake that answered
    /// null there would leave the row off screen for a reason that has nothing
    /// to do with watching.
    /// </summary>
    private sealed class RealFolder(bool watchable) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return Directory.EnumerateFileSystemEntries(path).Select(Entry).ToList();
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(Path.Exists(path) ? Entry(path) : null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
            => watchable ? new Idle() : throw new IOException("no watcher can be started for this folder");

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private static FileEntry Entry(string path)
        {
            var directory = Directory.Exists(path);
            var info = new FileInfo(path);

            return new FileEntry(
                Path.GetFileName(path),
                path,
                directory ? 0 : info.Length,
                info.LastWriteTimeUtc,
                directory ? EntryFlags.Directory : EntryFlags.None);
        }

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// A repository at <paramref name="root"/> with nothing to mark, counting
    /// how often it is asked. The count is all a test reads from it: a question
    /// asked after the index changed is the marks being refreshed.
    /// </summary>
    private sealed class CountingVcs(string root) : IVersionControl
    {
        private int _asked;

        /// <summary>Read on the test's thread, raised on the pool.</summary>
        public int Asked => Volatile.Read(ref _asked);

        public string Name => "counting";

        public bool IsAvailable => true;

        public string? FindRoot(string folder) => root;

        public Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);

            return Task.FromResult<VcsSnapshot?>(
                new VcsSnapshot(root, new Dictionary<string, VcsState>()));
        }
    }
}
