using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A search worth keeping, kept as a place.
///
/// **A question worth keeping had nowhere to be kept.** The magnifier's
/// history holds twelve and forgets the rest, so a search somebody runs every
/// week was retyped every week or fished out of a list shortening under it —
/// and Ctrl+D, the gesture that keeps a folder, refused a search with the bin
/// and This PC as "a view rather than a folder". A search is a view, but the
/// one view the panes can open again from its path alone, which is exactly
/// what a place is. These pin the gesture, the row it makes, and the two
/// routes that offer it.
/// </summary>
public sealed class SavedSearchTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaktari-saved-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        return dir;
    }

    private ShellViewModel Shell(string folder, Recorder places)
    {
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        shell.Start(null, folder);

        return shell;
    }

    // ---- the gesture ---------------------------------------------------------------

    /// <summary>
    /// Ctrl+D in a search listing saves the search — the whole path, so the
    /// folder it was confined to and its capitals go with it — under the same
    /// words the history rows use, and says so in the same line a folder pin
    /// speaks in.
    /// </summary>
    [AvaloniaFact]
    public async Task Ctrl_d_on_a_search_saves_it_under_its_question()
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);
            var search = VirtualPaths.Search("report", dir, scoped: true);

            shell.ActiveTab!.CurrentPath = search;

            await shell.PinCurrentCommand.ExecuteAsync(null);

            var name = PaneViewModel.SearchStepName(search);

            Assert.Equal([(search, name)], places.Pinned);
            Assert.Contains("report", name, StringComparison.Ordinal);
            Assert.Equal($"saved the search {name} to places", shell.ActiveTab.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The second press finds the row and says so, the way a folder's does.</summary>
    [AvaloniaFact]
    public async Task A_search_already_in_places_is_not_saved_again()
    {
        var dir = Temp();
        var search = VirtualPaths.Search("report", dir, scoped: true);
        var places = new Recorder(search);

        try
        {
            var shell = Shell(dir, places);

            await shell.Sidebar.ReloadAsync();

            shell.ActiveTab!.CurrentPath = search;

            await shell.PinCurrentCommand.ExecuteAsync(null);

            Assert.Empty(places.Pinned);
            Assert.EndsWith("is already in places", shell.ActiveTab.Status, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the row -------------------------------------------------------------------

    /// <summary>
    /// **The providers asked Directory.Exists to decide whether a pin could be
    /// opened, and drew every pin as a bookmark.** A saved search is always
    /// openable — the folder it looks under is asked when it runs — and draws
    /// the magnifier that made it. The rule is Core's, since both providers
    /// sit below the Ui that decides what a search path looks like.
    /// </summary>
    [Fact]
    public void A_saved_search_is_always_available_and_draws_the_magnifier()
    {
        var search = VirtualPaths.Search("report", null, scoped: false);
        var gone = Path.Combine(Path.GetTempPath(), "vaktari-gone-" + Guid.NewGuid().ToString("N"));

        Assert.True(PinnedPlaces.IsSearch(search));
        Assert.False(PinnedPlaces.IsSearch(VirtualPaths.Trash));

        Assert.True(PinnedPlaces.IsAvailable(search));
        Assert.False(PinnedPlaces.IsAvailable(gone));

        Assert.Equal("search", PinnedPlaces.Icon(search));
        Assert.Equal("bookmark", PinnedPlaces.Icon(gone));
    }

    /// <summary>
    /// The row's tooltip is where the search looks — its path is the scheme's
    /// own punctuation and reads as nothing — and a folder's is its path, as
    /// it always was.
    /// </summary>
    [Fact]
    public void The_row_says_where_a_saved_search_looks()
    {
        var dir = Temp();

        try
        {
            var search = VirtualPaths.Search("report", dir, scoped: true);

            var row = new PlaceItemViewModel(new Place
            {
                Id = "pin:" + search, Label = "report", Path = search,
                Kind = PlaceKind.Bookmark, Icon = "search", IsUserPinned = true,
            });

            Assert.Equal(dir, row.Where);

            var folder = new PlaceItemViewModel(new Place
            {
                Id = "pin:" + dir, Label = "a folder", Path = dir,
                Kind = PlaceKind.Bookmark, Icon = "bookmark", IsUserPinned = true,
            });

            Assert.Equal(dir, folder.Where);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Clicking the row asks the question again: the pane goes to
    /// the search path, which is what runs a search.</summary>
    [AvaloniaFact]
    public async Task Clicking_the_row_asks_the_question_again()
    {
        UseSearch(null);

        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);
            var search = VirtualPaths.Search("report", dir, scoped: true);

            var row = new PlaceItemViewModel(new Place
            {
                Id = "pin:" + search, Label = "report", Path = search,
                Kind = PlaceKind.Bookmark, Icon = "search", IsUserPinned = true,
            });

            await shell.OpenPlaceCommand.ExecuteAsync(row);

            Assert.Equal(search, shell.ActiveTab!.CurrentPath);
            Assert.True(shell.ActiveTab.IsSearchListing);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the routes ----------------------------------------------------------------

    /// <summary>
    /// The listing menu's slot reads "save this search" in a search and "add
    /// this folder" in a folder — never both, never neither — and the band
    /// above the results has a button for it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_and_the_band_offer_to_save_a_search()
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);

            Assert.True(shell.ShowAddCurrentToPlaces);
            Assert.False(shell.ShowSaveSearchToPlaces);

            shell.ActiveTab!.CurrentPath = VirtualPaths.Search("report", dir, scoped: true);

            Assert.False(shell.ShowAddCurrentToPlaces);
            Assert.True(shell.ShowSaveSearchToPlaces);

            var doc = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

            var row = doc.Descendants(Avalonia + "MenuItem")
                         .Single(m => (string?)m.Attribute("Header") == "Save this search to places");

            Assert.Contains("PinCurrentCommand", (string?)row.Attribute("Command"), StringComparison.Ordinal);
            Assert.Contains("ShowSaveSearchToPlaces", (string?)row.Attribute("IsVisible"), StringComparison.Ordinal);

            var button = doc.Descendants(Avalonia + "Button")
                            .Single(b => (string?)b.Attribute("Content") == "Save search");

            Assert.Contains("PinCurrentCommand", (string?)button.Attribute("Command"), StringComparison.Ordinal);
            Assert.Equal("SearchBand", (string?)button.Parent?.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        await Task.CompletedTask;
    }

    // ---- fakes ---------------------------------------------------------------------

    /// <summary>Places that remember what was pinned and under what name, and
    /// show whatever they were seeded with.</summary>
    private sealed class Recorder(params string[] shown) : IPlacesProvider
    {
        public List<(string Path, string? Label)> Pinned { get; } = [];

        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
                shown.Length == 0
                    ? []
                    : [new PlaceGroup("places", [.. shown.Select(path => new Place
                    {
                        Id = "row:" + path, Label = "a row", Path = path,
                        Kind = PlaceKind.Bookmark, Icon = PinnedPlaces.Icon(path),
                    })])]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
        {
            Pinned.Add((path, label));
            return ValueTask.CompletedTask;
        }

        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
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
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
