using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A pane showing the files under a folder that are copies of each other.
///
/// Like <see cref="UsageListingTests"/>, this is a virtual listing that reads a
/// REAL tree, so it cannot be driven by a fake provider: the path carries the
/// folder and the scan walks it. The folders below are this class's own.
/// </summary>
public sealed class DuplicatesPaneTests : OwnedViewModels
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-dup-pane").FullName;

    public override void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Content rather than a length, since what makes two files copies
    /// here is the bytes.</summary>
    private void File_(string relative, string content)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    private async Task<PaneViewModel> Scanning(string folder)
    {
        var pane = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

        await pane.NavigateAsync(VirtualPaths.Duplicates(folder));

        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (!pane.IsLoaded && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(pane.IsLoaded, "the scan never finished");

        return pane;
    }

    [AvaloniaFact]
    public async Task Both_copies_arrive_as_rows_and_the_odd_one_out_does_not()
    {
        File_("a.txt", "same");
        File_(Path.Combine("deeper", "b.txt"), "same");
        File_("alone.txt", "different, and of another length");

        var pane = await Scanning(_root);

        Assert.Equal(["a.txt", "b.txt"], pane.Entries.Select(e => e.Name).Order());
    }

    /// <summary>
    /// **The whole point of the listing, at the pane.** The command selects the
    /// spare copies, and a member of the set is left unselected — so the
    /// obvious next keystroke cannot take the last copy.
    /// </summary>
    [AvaloniaFact]
    public async Task Selecting_the_spare_copies_leaves_one_of_each_set()
    {
        File_("a.txt", "same");
        File_("b.txt", "same");
        File_("c.txt", "same");

        var pane = await Scanning(_root);

        pane.SelectExtraCopiesCommand.Execute(null);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, pane.SelectedEntries.Count);

        // Three rows are shown; one of them is not offered.
        Assert.Equal(3, pane.Entries.Count);

        var kept = pane.Entries.Select(e => e.FullPath)
            .Except(pane.SelectedEntries.Select(e => e.FullPath));

        Assert.Single(kept);
    }

    /// <summary>
    /// A scan that found nothing selects nothing — rather than throwing, or
    /// selecting the rows of whatever the pane showed before.
    /// </summary>
    [AvaloniaFact]
    public async Task Selecting_the_spare_copies_of_nothing_selects_nothing()
    {
        File_("a.txt", "one");
        File_("b.txt", "another thing entirely");

        var pane = await Scanning(_root);

        pane.SelectExtraCopiesCommand.Execute(null);

        Dispatcher.UIThread.RunJobs();

        Assert.Empty(pane.Entries);
        Assert.Empty(pane.SelectedEntries);
    }

    /// <summary>
    /// **The spare copies of the PREVIOUS folder must not survive the move.**
    /// They are paths a command acts on, so a list left over is a command that
    /// selects rows this listing does not have — and because
    /// <c>ReselectPaths</c> does nothing when it can see none of them, a stale
    /// list shows up as selecting NOTHING where it should select this folder's
    /// spare copy.
    /// </summary>
    [AvaloniaFact]
    public async Task Scanning_a_second_folder_forgets_the_first_folders_spares()
    {
        File_(Path.Combine("one", "a.txt"), "same");
        File_(Path.Combine("one", "b.txt"), "same");
        File_(Path.Combine("two", "c.txt"), "a different pair");
        File_(Path.Combine("two", "d.txt"), "a different pair");

        var pane = await Scanning(Path.Combine(_root, "one"));

        pane.SelectExtraCopiesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            Path.Combine(_root, "one", "b.txt"),
            Assert.Single(pane.SelectedEntries).FullPath);

        // **Waited on the rows of the SECOND folder, not on IsLoaded.** It is
        // still true from the first scan at the moment the wait begins, so a
        // loop on it falls straight through and measures nothing — which is
        // exactly what this test did until it was made to fail.
        await pane.NavigateAsync(VirtualPaths.Duplicates(Path.Combine(_root, "two")));

        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (pane.Entries.Count != 2 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal(["c.txt", "d.txt"], pane.Entries.Select(e => e.Name).Order());

        pane.SelectExtraCopiesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            Path.Combine(_root, "two", "d.txt"),
            Assert.Single(pane.SelectedEntries).FullPath);
    }

    [AvaloniaFact]
    public async Task The_band_says_what_can_be_freed()
    {
        File_("a.txt", "0123456789");
        File_("b.txt", "0123456789");

        var pane = await Scanning(_root);

        Assert.True(pane.IsDuplicatesListing);

        // Two copies, one set, and one of the two can go.
        Assert.Contains("2 copies in 1 set", pane.DuplicatesLine);
        Assert.Contains("can be freed", pane.DuplicatesLine);
    }

    [AvaloniaFact]
    public async Task A_folder_with_no_copies_says_so_rather_than_that_it_is_empty()
    {
        File_("a.txt", "one");
        File_("b.txt", "another thing entirely");

        var pane = await Scanning(_root);

        Assert.Equal("nothing here is a copy of anything else", pane.DuplicatesLine);
        Assert.Equal("nothing here is a copy of anything else", pane.EmptyText);
    }

    /// <summary>
    /// It is a view rather than a folder, so everything gated on
    /// <c>IsRealFolder</c> refuses it — and the way back out is the row that
    /// goes to where a file lives, because Up cannot leave a virtual listing.
    /// </summary>
    [AvaloniaFact]
    public async Task It_is_a_view_rather_than_a_folder()
    {
        File_("a.txt", "same");
        File_("b.txt", "same");

        var pane = await Scanning(_root);

        Assert.False(pane.IsRealFolder);
        Assert.True(pane.CanGoToLocation);

        // And asking for a duplicates listing OF one is refused rather than
        // scanning a path that names no tree.
        await pane.ShowDuplicatesCommand.ExecuteAsync(null);

        Assert.Equal(VirtualPaths.Duplicates(_root), pane.CurrentPath);
    }

    /// <summary>
    /// **Every duplicates listing is remembered under one key.** The path
    /// carries the folder that was scanned, so keyed as it is spelled, a person
    /// who scanned fifty folders would leave fifty records in a store kept for
    /// the life of the profile and reachable again by none of them. An ordinary
    /// folder still gets its own, which is the half saying this did not
    /// collapse the store itself.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_duplicates_listing_is_remembered_under_one_key()
    {
        File_(Path.Combine("one", "a.txt"), "same");
        File_(Path.Combine("one", "b.txt"), "same");
        File_(Path.Combine("two", "c.txt"), "a different pair");
        File_(Path.Combine("two", "d.txt"), "a different pair");

        var store = new Recording();
        var storeBefore = PaneViewModel.FolderViews;
        var settingsBefore = Vaktari.Ui.Settings.AppSettings.Current;

        PaneViewModel.FolderViews = store;

        Vaktari.Ui.Settings.AppSettings.Apply(
            settingsBefore with
            {
                General = settingsBefore.General with { RememberViewPerFolder = true },
            });

        try
        {
            (await Scanning(Path.Combine(_root, "one"))).RememberFolderView();
            (await Scanning(Path.Combine(_root, "two"))).RememberFolderView();

            Assert.Equal([VirtualPaths.DuplicatesViewKey], store.Written.Distinct());

            var plain = Own(new PaneViewModel(new Silent()) { ViewportWidth = 1400 });

            await plain.NavigateAsync(_root);
            plain.RememberFolderView();

            Assert.Contains(_root, store.Written);
        }
        finally
        {
            PaneViewModel.FolderViews = storeBefore;
            Vaktari.Ui.Settings.AppSettings.Apply(settingsBefore);
        }
    }

    /// <summary>Remembers which keys were written, and nothing else.</summary>
    private sealed class Recording : IFolderViewStore
    {
        public List<string> Written { get; } = [];

        public FolderViewState? Read(string path) => null;

        public void Write(string path, FolderViewState state) => Written.Add(path);

        public void Forget(string path) { }

        public int ForgetAll()
        {
            var had = Written.Count;

            Written.Clear();

            return had;
        }

        public int Remembered => Written.Count;
    }

    /// <summary>
    /// The disk is never asked about a virtual path — the scan reads the folder
    /// the path names, and this provider answers nothing so a pane that tried
    /// would show an empty listing rather than a scan.
    /// </summary>
    private sealed class Silent : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
