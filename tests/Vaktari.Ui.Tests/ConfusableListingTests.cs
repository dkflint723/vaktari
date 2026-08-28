using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The look-alike set has to reach a LISTING, not merely exist.
///
/// **The mark shipped in 0.9.7 and never rendered once.** ConfusableNames was
/// right, its unit tests passed, the binding was right — and the assignment
/// that connects them lived only in ApplyFilter, which an ordinary navigation
/// never runs: a plain folder load takes ResortInPlace. So the set stayed at
/// its empty initial value on every normal path, and the feature was invisible
/// from the day it shipped until an eye-test five releases later put two
/// colliding names on a real screen and saw nothing.
///
/// The unit tests could not have caught it, because they tested the set. This
/// one asks the pane, after the navigation a user actually performs.
/// </summary>
public sealed class ConfusableListingTests
{
    /// <summary>A provider that yields exactly the entries it is given.</summary>
    private sealed class Canned(IReadOnlyList<FileEntry> entries) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return entries;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static FileEntry Entry(string folder, string name) =>
        new(name, Path.Combine(folder, name), 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    [AvaloniaFact]
    public async Task An_ordinary_navigation_populates_the_lookalike_set()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-confusable-listing");

        var shell = new ShellViewModel(new Canned(
        [
            Entry(folder, "Ember Setup 0.1.0.exe"),
            Entry(folder, "Ember Setup 0.1.0 .exe"),
            Entry(folder, "report.pdf"),
        ]));

        shell.Start(null, folder);

        var pane = shell.ActiveTab!;

        // The listing loads asynchronously; give the dispatcher its turns until
        // the entries have landed, bounded so a hang fails rather than spins.
        for (var i = 0; i < 200 && pane.Entries.Count < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(3, pane.Entries.Count);

        // No filter was ever typed — this is the path that stayed empty.
        Assert.Contains(Path.Combine(folder, "Ember Setup 0.1.0.exe"), pane.Confusable);
        Assert.Contains(Path.Combine(folder, "Ember Setup 0.1.0 .exe"), pane.Confusable);
        Assert.DoesNotContain(Path.Combine(folder, "report.pdf"), pane.Confusable);
    }
}
