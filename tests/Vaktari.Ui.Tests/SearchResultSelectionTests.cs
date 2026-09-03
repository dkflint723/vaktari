using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Choosing a search result.
///
/// **It selected the search backend's own entry, which is not the row.**
/// FileEntry is a record struct with structural equality over all five members,
/// the listings bind SelectedItem to SelectedEntry, and a ListBox resolves
/// SelectedItem by equality against the rows it holds. So the hit had to match
/// the row in size, timestamp AND flags as well as path — and it routinely did
/// not: the Linux search provider sets Directory and Hidden and nothing else,
/// while the file system provider also sets Symlink and ReadOnly. Choosing any
/// read-only file or symlink put you in the right folder with no row lit and
/// the selection empty, which reads as the search having failed after taking
/// you somewhere.
///
/// **And a hidden hit had no row to select at all.** Both backends return
/// hidden and system files; the listing excludes them while "show hidden files"
/// is off, so there was nothing there to find.
/// </summary>
public sealed class SearchResultSelectionTests : OwnedViewModels
{
    /// <summary>
    /// Stamps ReadOnly on its files, as the real providers do and as the search
    /// backends do not — which is the whole discrepancy under test.
    /// </summary>
    private sealed class Listing(string folder, params FileEntry[] rows) : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            if (!PathRules.Same(path, folder)) { yield break; }

            yield return [.. rows.Where(r => options.IncludeHidden || !r.IsConcealed)];
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

    private static readonly DateTimeOffset When = DateTimeOffset.UnixEpoch;

    private static string In(string name) => Path.Combine(Path.GetTempPath(), name);

    /// <summary>
    /// The same file as the listing's row in every way the eye can see, and
    /// different in the flags — exactly what the Linux search backend hands
    /// over for a read-only file.
    /// </summary>
    private static FileEntry AsSearchSawIt(FileEntry row)
        => new(row.Name, row.FullPath, row.Length, row.LastWriteTime, EntryFlags.None);

    [AvaloniaFact]
    public async Task A_chosen_result_lights_a_row_the_listing_really_has()
    {
        var row = new FileEntry("report.txt", In("report.txt"), 12, When, EntryFlags.ReadOnly);

        var pane = Own(new PaneViewModel(new Listing(Path.GetTempPath(), row))
        {
            ViewportWidth = 1400,
        });

        await pane.RevealAsync(AsSearchSawIt(row));

        Assert.Equal(row.FullPath, pane.SelectedEntry?.FullPath);

        // The load-bearing assertion, and the reason this is not vacuous:
        // asserting the path alone PASSES against the old code, because the old
        // code assigned the hit and the hit does carry the right path. The pane
        // looked selected while the listing showed nothing highlighted.
        Assert.Contains(pane.SelectedEntry!.Value, (IEnumerable<FileEntry>)pane.Entries);
    }

    [AvaloniaFact]
    public async Task It_goes_to_the_folder_the_result_is_in()
    {
        var row = new FileEntry("report.txt", In("report.txt"), 12, When, EntryFlags.ReadOnly);

        var pane = Own(new PaneViewModel(new Listing(Path.GetTempPath(), row))
        {
            ViewportWidth = 1400,
        });

        await pane.RevealAsync(AsSearchSawIt(row));

        Assert.True(PathRules.Same(Path.GetTempPath(), pane.CurrentPath));
    }

    /// <summary>
    /// A hidden hit is returned by both backends and excluded by the listing,
    /// so there was no row at all. Landing in a folder that provably cannot
    /// contain what you asked for is the worse surprise.
    /// </summary>
    [AvaloniaFact]
    public async Task A_hidden_result_turns_hidden_files_on_so_there_is_a_row()
    {
        var row = new FileEntry(".config", In(".config"), 3, When, EntryFlags.Hidden);

        var pane = Own(new PaneViewModel(new Listing(Path.GetTempPath(), row))
        {
            ViewportWidth = 1400,
        });

        Assert.False(pane.ShowHidden);

        await pane.RevealAsync(row);

        Assert.True(pane.ShowHidden, "the result is hidden and hidden files were left off");
        Assert.Equal(row.FullPath, pane.SelectedEntry?.FullPath);
        Assert.Contains(pane.SelectedEntry!.Value, (IEnumerable<FileEntry>)pane.Entries);
    }

    /// <summary>Not a blanket switch-on: an ordinary result leaves the setting
    /// where the user put it.</summary>
    [AvaloniaFact]
    public async Task An_ordinary_result_leaves_the_hidden_setting_alone()
    {
        var row = new FileEntry("report.txt", In("report.txt"), 12, When, EntryFlags.ReadOnly);

        var pane = Own(new PaneViewModel(new Listing(Path.GetTempPath(), row))
        {
            ViewportWidth = 1400,
        });

        await pane.RevealAsync(AsSearchSawIt(row));

        Assert.False(pane.ShowHidden);
    }

    /// <summary>
    /// An index can be stale. Saying so beats going somewhere and lighting
    /// nothing, which is indistinguishable from the bug this fixes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_result_that_has_since_gone_says_so()
    {
        var pane = Own(new PaneViewModel(new Listing(Path.GetTempPath()))
        {
            ViewportWidth = 1400,
        });

        await pane.RevealAsync(
            new FileEntry("stale.txt", In("stale.txt"), 1, When, EntryFlags.None));

        Assert.Contains("stale.txt", pane.Status);
        Assert.Null(pane.SelectedEntry);
    }

    /// <summary>
    /// The half no view-model test can see: the shell has to ASK. It used to
    /// assemble the whole behaviour in its own event lambda, which is how it
    /// came to select something the listing had never heard of — and every test
    /// above would pass with that lambda back in place.
    /// </summary>
    [AvaloniaFact]
    public void The_shell_asks_the_pane_to_reveal_it()
    {
        var shell = RepoSource.Ui("ViewModels", "ShellViewModel.cs").Split('\n');

        var handler = shell.FirstOrDefault(
            l => l.Contains("Sidebar.Search.ResultChosen", StringComparison.Ordinal));

        Assert.NotNull(handler);
        Assert.Contains("RevealAsync", handler);

        // Not assembling the behaviour itself, which is how it came to select
        // something the listing had never heard of.
        Assert.DoesNotContain("SelectedEntry", handler);
    }

    [Theory]
    [InlineData(EntryFlags.Hidden, false, true)]
    [InlineData(EntryFlags.System, false, true)]
    [InlineData(EntryFlags.Hidden, true, false)]
    [InlineData(EntryFlags.None, false, false)]
    [InlineData(EntryFlags.ReadOnly, false, false)]
    public void The_listing_hides_what_the_platform_calls_hidden_and_system(
        EntryFlags flags, bool showHidden, bool needs)
        => Assert.Equal(needs, PaneViewModel.NeedsHiddenShown(
            new FileEntry("x", In("x"), 1, When, flags), showHidden));
}
