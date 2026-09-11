using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Adding a place says what it did.
///
/// **Ctrl+D was silent, and Ctrl+D is what Explorer deletes with.** Somebody
/// arriving from there presses it over a selected file: the folder they are
/// standing in goes into places.json, the panel rebuilds from the provider's
/// own event a moment later, and nothing on screen at the moment of the press
/// changes at all — so the gesture that did something they did not ask for
/// looked exactly like the gesture that did nothing, and neither looked like
/// the delete they wanted. The menu row behind the same two commands was
/// silent for the same reason.
///
/// The drop onto the panel already reported; this is the same line, from the
/// other two routes in — including its measured refusal to claim a pin for a
/// folder the panel is already showing, which the provider drops while
/// building the list it renders.
///
/// The commands are awaited rather than pumped for: they became async so that
/// the report is made after the write returns, and <c>ExecuteAsync</c> hands
/// back that same task, so nothing here waits on a proxy for what it asserts.
/// </summary>
public sealed class PinSaysWhatItDidTests : OwnedViewModels
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaktari-pin-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        return dir;
    }

    /// <summary>A shell whose one tab is standing in <paramref name="folder"/>,
    /// with places that remember what they were told to pin.</summary>
    private ShellViewModel Shell(string folder, Recorder places)
    {
        var shell = Own(new ShellViewModel(new Inert(), places: places));

        shell.Start(null, folder);

        Assert.Equal(folder, shell.ActiveTab!.CurrentPath);

        return shell;
    }

    [AvaloniaFact]
    public async Task Ctrl_d_says_which_folder_it_pinned()
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);

            await shell.PinCurrentCommand.ExecuteAsync(null);

            Assert.Equal([dir], places.Pinned);
            Assert.Equal($"pinned {Path.GetFileName(dir)} to places", shell.ActiveTab!.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **A folder the panel already shows was written to places.json and never
    /// drawn.** The provider drops a pin whose path is already one of the
    /// built-in places while BUILDING the list it renders, so pressing Ctrl+D
    /// in Downloads — the likeliest press there is — wrote an entry that no row
    /// ever stood for and that no "Remove from places" could reach. Once, not
    /// once per press: MEASURED against a real WindowsPlacesProvider, four
    /// presses and a relaunch left places.json holding that one entry, because
    /// PinAsync refuses a path its in-memory list already has. That is why the
    /// rendered rows are what "already" is asked of — the file's answer is
    /// "there is no such row" the first time and unreadable afterwards, since
    /// PinAsync hands nothing back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_the_panel_already_shows_is_not_pinned_again()
    {
        var dir = Temp();
        var places = new Recorder(dir);

        try
        {
            var shell = Shell(dir, places);

            await shell.Sidebar.ReloadAsync();

            await shell.PinCurrentCommand.ExecuteAsync(null);

            Assert.Empty(places.Pinned);
            Assert.Equal($"{Path.GetFileName(dir)} is already in places", shell.ActiveTab!.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// In the bin or This PC there is no folder to pin — the gesture has been
    /// guarded there since it pinned "vaktari:trash", and the guard was as
    /// silent as the pin it replaced. A search is no longer in this list: it
    /// is the one view that is a place, and SavedSearchTests has it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(VirtualPaths.Trash)]
    [InlineData(VirtualPaths.Computer)]
    public async Task A_listing_that_is_not_a_folder_says_why_nothing_was_pinned(string listing)
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);

            shell.ActiveTab!.CurrentPath = listing;

            await shell.PinCurrentCommand.ExecuteAsync(null);

            Assert.Empty(places.Pinned);
            Assert.Equal("only a folder or a search can be a place", shell.ActiveTab.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The menu's own row, which was the other caller of the bare
    /// <c>Sidebar.PinAsync</c> and reported nothing either. It pins the
    /// SELECTED folder, so the line has to name that one rather than the folder
    /// the pane is standing in.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_row_says_it_too_and_names_the_selection()
    {
        var dir = Temp();
        var places = new Recorder();
        var selected = Path.Combine(dir, "reports");

        try
        {
            var shell = Shell(dir, places);

            shell.ActiveTab!.SelectedEntry = new FileEntry(
                "reports", selected, 0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

            await shell.AddSelectionToPlacesCommand.ExecuteAsync(null);

            Assert.Equal([selected], places.Pinned);
            Assert.Equal("pinned reports to places", shell.ActiveTab.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **A bin row's path is where the folder USED to be.**
    /// <c>RecentListing.GatherTrash</c> builds every row as
    /// <c>new FileEntry(name, item.OriginalPath, …)</c> with the directory flag
    /// copied from the deleted item, so a deleted folder in the bin is a
    /// selected directory carrying a real-looking path that nothing occupies —
    /// and the selection row offered "Add to places" over it, pinned that path,
    /// and now would have said "pinned reports to places" about a row that can
    /// never be opened. The bin is the only listing that does this: Recent's
    /// <c>Build</c> returns null for a path that is neither a directory nor a
    /// file, and a search listing's rows are folders that are really there,
    /// which is why the answer is the bin rather than "the folder must exist".
    ///
    /// So the selection is not read there at all and the fallback answers, the
    /// same refusal the scheme gets below.
    /// </summary>
    [AvaloniaFact]
    public async Task A_deleted_folder_in_the_bin_is_not_a_place()
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);

            shell.ActiveTab!.CurrentPath = VirtualPaths.Trash;

            // Where it was deleted from — a path, correctly shaped, that no
            // longer names anything.
            shell.ActiveTab.SelectedEntry = new FileEntry(
                "reports", Path.Combine(dir, "gone", "reports"), 0,
                DateTimeOffset.UnixEpoch, EntryFlags.Directory);

            await shell.AddSelectionToPlacesCommand.ExecuteAsync(null);

            Assert.Empty(places.Pinned);
            Assert.Equal("only a folder or a search can be a place", shell.ActiveTab.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The menu command pins the CURRENT folder when nothing is selected, and
    /// in a listing that is a view rather than a folder that is the internal
    /// scheme: <c>vaktari:trash</c> is not a path, and a row holding it could
    /// never be opened and had to be taken out of places.json by hand — the
    /// fault Ctrl+D was guarded against, in the other command that shares its
    /// fallback.
    ///
    /// Nobody meets it through the menu: the row binding this hides where no
    /// folder is selected, hides in the bin, and the current-folder row beside
    /// it hides in any virtual listing — three gates that cover it between
    /// them. This is the command keeping its own promise rather than trusting
    /// that they always will, and it is the same guard the bin case above
    /// reaches for real once the selection is set aside there.
    /// </summary>
    [AvaloniaFact]
    public async Task The_menu_command_pins_no_scheme_where_the_listing_is_a_view()
    {
        var dir = Temp();
        var places = new Recorder();

        try
        {
            var shell = Shell(dir, places);

            shell.ActiveTab!.CurrentPath = VirtualPaths.Trash;
            shell.ActiveTab.SelectedEntry = null;

            await shell.AddSelectionToPlacesCommand.ExecuteAsync(null);

            Assert.Empty(places.Pinned);
            Assert.Equal("only a folder or a search can be a place", shell.ActiveTab.Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- fakes ---------------------------------------------------------------

    /// <summary>Places that remember what was pinned, and show whatever they
    /// were seeded with.</summary>
    private sealed class Recorder(params string[] shown) : IPlacesProvider
    {
        public List<string> Pinned { get; } = [];

        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
                shown.Length == 0
                    ? []
                    : [new PlaceGroup("places", [.. shown.Select(path => new Place
                    {
                        Id = "row:" + path, Label = "a row", Path = path,
                        Kind = PlaceKind.UserFolder, Icon = "folder",
                    })])]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
        {
            Pinned.Add(path);
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
