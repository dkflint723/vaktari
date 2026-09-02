using Avalonia.Headless.XUnit;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Giving a pinned place a name of your own.
///
/// **Both providers have stored a per-pin label since they were written, and
/// nothing could ever change it.** The import paths already set a custom one —
/// a shortcut's own filename on Windows, an xbel title or a GTK bookmark's
/// trailing label on Linux — so the field was read, written and honoured
/// everywhere except by the person whose sidebar it is. Two folders both called
/// "src" pinned as two rows called "src", and the only way to tell them apart
/// was editing places.json by hand.
/// </summary>
public sealed class PlaceRenameTests
{
    /// <summary>Records the rename rather than performing one, and hands back a
    /// list that reflects it — so a reload shows what a real provider would.</summary>
    private sealed class Pins : IPlacesProvider
    {
        private readonly List<Place> _places;

        public Pins(params (string Path, string Label, bool Pinned)[] rows)
            => _places = [.. rows.Select(r => new Place
            {
                Id = "pin:" + r.Path, Label = r.Label, Path = r.Path,
                Kind = PlaceKind.Bookmark, Icon = "folder", IsUserPinned = r.Pinned,
            })];

        public List<string> Renames { get; } = [];

        public event EventHandler? PlacesChanged { add { } remove { } }

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([new PlaceGroup("places", _places)]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
        {
            Renames.Add($"{id} -> {label}");

            for (var i = 0; i < _places.Count; i++)
                if (_places[i].Id == id)
                    _places[i] = _places[i] with { Label = label };

            return ValueTask.CompletedTask;
        }

        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Ejected("ejected"));
    }

    private static async Task<(SidebarViewModel Sidebar, Pins Provider)> Loaded(Pins pins)
    {
        var sidebar = new SidebarViewModel(pins);

        await sidebar.ReloadAsync();

        return (sidebar, pins);
    }

    [AvaloniaFact]
    public async Task A_pinned_place_can_be_given_a_name()
    {
        var (sidebar, pins) = await Loaded(new Pins(("/work/src", "src", true)));

        await sidebar.RenameAsync("pin:/work/src", "Work source");

        Assert.Equal(["pin:/work/src -> Work source"], pins.Renames);
        Assert.Equal("Work source", sidebar.Groups[0].Places[0].Label);
    }

    /// <summary>The typed text is tidied first: a pasted newline turns one row
    /// into a shape the sidebar cannot draw.</summary>
    [AvaloniaFact]
    public async Task A_pasted_line_break_does_not_reach_the_row()
    {
        var (sidebar, pins) = await Loaded(new Pins(("/work/src", "src", true)));

        await sidebar.RenameAsync("pin:/work/src", "  Work\nsource  ");

        Assert.Equal(["pin:/work/src -> Worksource"], pins.Renames);
    }

    /// <summary>A blank name is a no-op, not a blank row — the sidebar would
    /// then hold something with no way to identify or fix it.</summary>
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_changes_nothing(string typed)
    {
        var (sidebar, pins) = await Loaded(new Pins(("/work/src", "src", true)));

        await sidebar.RenameAsync("pin:/work/src", typed);

        Assert.Empty(pins.Renames);
        Assert.Equal("src", sidebar.Groups[0].Places[0].Label);
    }

    /// <summary>
    /// Only the rows the user made. Home, the drives and the shares are named
    /// by the system, so a caption on one would vanish at the next reload —
    /// which is worse than not offering it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_the_system_named_is_left_alone()
    {
        var pins = new Pins(("/home/flint", "Home", false), ("/work/src", "src", true));

        // The shell's OWN sidebar, or the gate under test would be reading one
        // provider while the assertion read another and the test would pass
        // whatever the gate did.
        var shell = new ShellViewModel(new Nothing(), places: pins);

        try
        {
            await shell.Sidebar.ReloadAsync();

            var rows = shell.Sidebar.Groups[0].Places;

            await shell.RenamePlaceAsync(rows.Single(r => !r.IsUserPinned), "Somewhere else");
            Assert.Empty(pins.Renames);

            // And the same call does work for a row the user made, so the
            // refusal above is the gate and not the plumbing being dead.
            await shell.RenamePlaceAsync(rows.Single(r => r.IsUserPinned), "Work source");
            Assert.Equal(["pin:/work/src -> Work source"], pins.Renames);
        }
        finally
        {
            shell.Dispose();
        }
    }

    /// <summary>And the entry is only drawn on those rows, so the refusal is
    /// never something you have to discover by trying it.</summary>
    [AvaloniaFact]
    public void The_entry_appears_only_on_the_rows_the_user_made()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        var at = markup.IndexOf("RenamePlaceCommand", StringComparison.Ordinal);
        Assert.True(at > 0, "nothing offers to rename a place");

        var element = markup[markup.LastIndexOf("<MenuItem", at, StringComparison.Ordinal)..at];

        Assert.Contains("IsVisible=\"{Binding IsUserPinned}\"", element);
    }

    // ---- the tidying rule, on its own ---------------------------------------

    /// <summary>
    /// A place label is a caption, not a filename. A slash, a colon and "CON"
    /// are all good text for a sidebar row, and refusing them would be refusing
    /// something harmless because it is illegal somewhere else — nothing is
    /// written to disk under this name.
    /// </summary>
    [Theory]
    [InlineData("Work/src", "Work/src")]
    [InlineData("C: drive", "C: drive")]
    [InlineData("CON", "CON")]
    [InlineData("  padded  ", "padded")]
    [InlineData("two\tparts", "twoparts")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void The_label_is_tidied_rather_than_policed(string typed, string kept)
        => Assert.Equal(kept, PlaceNames.Clean(typed));

    private sealed class Nothing : Vaktari.Core.FileSystem.IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<Vaktari.Core.FileSystem.FileEntry>> EnumerateAsync(
            string path, Vaktari.Core.FileSystem.ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<Vaktari.Core.FileSystem.FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<Vaktari.Core.FileSystem.FileEntry?>(null);

        public IDisposable Watch(string path, Action<Vaktari.Core.FileSystem.FileSystemChange> onChange)
            => new Nowt();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nowt : IDisposable { public void Dispose() { } }
    }
}
