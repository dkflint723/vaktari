using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Going to where a row lives, from the listings whose rows live elsewhere.
///
/// **Recent showed the folder each row came from and offered no way to go
/// there.** The parent-path column is on in a search and in both Recent
/// listings for one reason — the rows are gathered from the whole machine — and
/// only the search carried Open file location. Recent's single addition to the
/// menu was Forget, so a Recent listing could name a place and not take you to
/// it.
/// </summary>
public sealed class GoToLocationTests : OwnedViewModels
{
    private static readonly DateTimeOffset When = DateTimeOffset.UnixEpoch;

    private static string In(string name) => Path.Combine(Path.GetTempPath(), name);

    /// <summary>Serves one folder's rows and nothing else.</summary>
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

    private PaneViewModel Pane(IFileSystemProvider fs)
        => Own(new PaneViewModel(fs) { ViewportWidth = 1400 });

    [AvaloniaTheory]
    [InlineData(VirtualPaths.Files, true)]
    [InlineData(VirtualPaths.Locations, true)]
    [InlineData(VirtualPaths.Trash, false)]
    [InlineData(VirtualPaths.Computer, false)]
    public void The_entry_is_offered_where_the_rows_came_from_elsewhere(string path, bool offered)
    {
        var pane = Pane(new Listing(Path.GetTempPath()));

        pane.CurrentPath = path;

        Assert.Equal(offered, pane.CanGoToLocation);
    }

    /// <summary>The half it already had, and the folder it must never be
    /// offered in.</summary>
    [AvaloniaFact]
    public void A_search_still_offers_it_and_an_ordinary_folder_does_not()
    {
        var pane = Pane(new Listing(Path.GetTempPath()));

        pane.CurrentPath = VirtualPaths.Search("report", null, false);
        Assert.True(pane.CanGoToLocation);

        pane.CurrentPath = Path.GetTempPath();
        Assert.False(pane.CanGoToLocation);
    }

    /// <summary>
    /// The row is bound to the property, so the property has to be announced
    /// when the path moves — a change raised for IsSearchListing is not a
    /// change raised for this one.
    /// </summary>
    [AvaloniaFact]
    public async Task Arriving_in_Recent_announces_it()
    {
        var pane = Pane(new Listing(Path.GetTempPath()));

        await pane.NavigateAsync(Path.GetTempPath());

        var announced = new List<string>();
        pane.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? "");

        await pane.NavigateAsync(VirtualPaths.Files);
        await WaitUntil(() => announced.Contains(nameof(PaneViewModel.CanGoToLocation)));

        Assert.Contains(nameof(PaneViewModel.CanGoToLocation), announced);
    }

    /// <summary>
    /// The case the whole finding is about: a Recent FILES row.
    ///
    /// **Recent Files showed the folder each row came from and could not go
    /// there.** No unique mutation kills this one — for a file the two routes
    /// land in the same folder on the same target — so it is a regression guard
    /// on the advertised behaviour rather than a pin on a branch.
    /// </summary>
    [AvaloniaFact]
    public async Task A_recent_file_can_be_shown_where_it_lives()
    {
        var file = new FileEntry("notes.txt", In("notes.txt"), 4, When, EntryFlags.None);

        var pane = Pane(new Listing(Path.GetTempPath(), file));

        pane.CurrentPath = VirtualPaths.Files;
        pane.SelectedEntry = file;

        await pane.GoToLocationCommand.ExecuteAsync(null);

        Assert.True(PathRules.Same(Path.GetTempPath(), pane.CurrentPath));
    }

    /// <summary>
    /// **Every row in Recent Locations is a folder, and revealing a folder
    /// enters it** — which is what double-clicking the row already does. Shown
    /// in its parent, the entry answers the question it is named for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_recent_folder_is_shown_in_its_parent_rather_than_entered()
    {
        var folder = new FileEntry("projects", In("projects"), 0, When, EntryFlags.Directory);

        var pane = Pane(new Listing(Path.GetTempPath(), folder));

        pane.CurrentPath = VirtualPaths.Locations;
        pane.SelectedEntry = folder;

        await pane.GoToLocationCommand.ExecuteAsync(null);

        Assert.True(PathRules.Same(Path.GetTempPath(), pane.CurrentPath));
        Assert.Equal(folder.FullPath, pane.SelectedEntry?.FullPath);
        Assert.Contains(pane.SelectedEntry!.Value, (IEnumerable<FileEntry>)pane.Entries);
    }

    /// <summary>
    /// A drive root is a place you visit, so Recent Locations collects them —
    /// and it has no parent directory, so the entry would have done nothing at
    /// all on it. Up already answers this: the top of a drive is not the top of
    /// the machine.
    /// </summary>
    [AvaloniaFact]
    public async Task A_recent_drive_root_goes_to_This_PC_rather_than_nowhere()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var drive = new FileEntry(root, root, 0, When, EntryFlags.Directory);

        // The drives provider is a static this test does not want a real answer
        // from: This PC then lists nothing, which is all this assertion needs
        // and is the same on every machine.
        var before = PaneViewModel.Places;
        PaneViewModel.Places = null;

        try
        {
            var pane = Pane(new Listing(Path.GetTempPath()));

            pane.CurrentPath = VirtualPaths.Locations;
            pane.SelectedEntry = drive;

            await pane.GoToLocationCommand.ExecuteAsync(null);

            Assert.Equal(VirtualPaths.Computer, pane.CurrentPath);
        }
        finally { PaneViewModel.Places = before; }
    }

    /// <summary>
    /// And the search listing's answer is untouched: a folder you asked a
    /// search to take you to is somewhere you want to BE.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_found_by_a_search_is_still_entered()
    {
        var folder = new FileEntry("projects", In("projects"), 0, When, EntryFlags.Directory);

        var pane = Pane(new Listing(In("projects")));

        pane.CurrentPath = VirtualPaths.Search("projects", null, false);
        pane.SelectedEntry = folder;

        await pane.GoToLocationCommand.ExecuteAsync(null);

        Assert.True(PathRules.Same(In("projects"), pane.CurrentPath));
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), "the pane never got there");
    }
}
