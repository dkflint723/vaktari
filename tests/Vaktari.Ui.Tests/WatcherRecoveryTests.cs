using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What happens when live updates stop being reliable.
///
/// **A watcher that falls behind says nothing at all.** The kernel buffer is
/// fixed, and an extraction, a build or a large download in the folder on
/// screen overruns it — after which events are simply dropped. Nothing
/// subscribed the Error event, so the listing went quietly out of date with no
/// way back but F5, and a folder deleted underneath left the pane sitting on
/// rows for a place that no longer existed.
///
/// And every event did O(n) work on the UI thread — recomputing the look-alike
/// set over the whole listing, measured at 28.9 ms a pass — so the same burst
/// of events that broke the watcher also froze the window.
/// </summary>
public sealed class WatcherRecoveryTests : OwnedViewModels
{
    private static FileEntry Entry(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    /// <summary>Dropped events mean "read it again": what is on screen may be
    /// wrong in either direction, and only a fresh listing settles it.</summary>
    [AvaloniaFact]
    public async Task Dropped_events_reload_the_listing()
    {
        var fs = new Watched();
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        var listings = fs.Listings;

        fs.Raise(new FileSystemChange(ChangeKind.Lost, pane.CurrentPath));
        Dispatcher.UIThread.RunJobs();

        Assert.True(fs.Listings > listings, "the listing was never re-read");
    }

    /// <summary>
    /// The folder itself going away is the other half. The rows describe a
    /// place that is not there, and reloading is what says so — through the
    /// same failure path as any other unreadable folder.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_disappears_is_re_read_rather_than_left_standing()
    {
        var fs = new Watched();
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        var listings = fs.Listings;

        fs.Raise(new FileSystemChange(ChangeKind.Gone, pane.CurrentPath));
        Dispatcher.UIThread.RunJobs();

        Assert.True(fs.Listings > listings, "the pane kept its phantom rows");
    }

    /// <summary>
    /// **The path on Lost and Gone is the watched FOLDER, not a child**, so the
    /// direct-children guard that every other event goes through would have
    /// discarded both. They are handled before it, and this is the test that
    /// says so.
    /// </summary>
    [AvaloniaFact]
    public async Task They_survive_the_direct_children_guard()
    {
        var fs = new Watched();
        var pane = Own(new PaneViewModel(fs, null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        var listings = fs.Listings;

        // A child event of the same shape is NOT a reload.
        fs.Raise(new FileSystemChange(ChangeKind.Changed,
                                      Path.Combine(pane.CurrentPath, "ordinary.txt")));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(listings, fs.Listings);
    }

    private sealed class Watched : IFileSystemProvider
    {
        private Action<FileSystemChange>? _onChange;

        public int Listings { get; private set; }

        public void Raise(FileSystemChange change) => _onChange?.Invoke(change);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Listings++;
            await Task.CompletedTask;
            yield return [Entry("a.txt"), Entry("b.txt")];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(Entry(Path.GetFileName(path)));

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _onChange = onChange;
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
