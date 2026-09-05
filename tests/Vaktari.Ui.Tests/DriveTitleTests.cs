using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a tab on a drive root is called.
///
/// **It read "C:".** The title falls back to the path's last segment, which for
/// a root is the root — while the sidebar three inches away called the same
/// drive "Windows (C:)", because building THAT list is where the volume label
/// is read. One machine, two names for one drive, and the useless one in the
/// place you look most.
/// </summary>
public sealed class DriveTitleTests : OwnedViewModels
{
    private static readonly string Root =
        Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath();

    private PaneViewModel Pane(IPlacesProvider? places)
    {
        var before = PaneViewModel.Places;

        PaneViewModel.Places = places;
        _restore.Add(() => PaneViewModel.Places = before);

        return Own(new PaneViewModel(new Silent()));
    }

    private readonly List<Action> _restore = [];

    public override void Dispose()
    {
        foreach (var undo in _restore) undo();

        _restore.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The whole finding.</summary>
    [AvaloniaFact]
    public async Task A_drive_is_titled_the_way_the_sidebar_titles_it()
    {
        var pane = Pane(new Names { [Root] = "Windows (C:)" });

        await pane.NavigateAsync(Root);

        Assert.Equal("Windows (C:)", pane.Title);
    }

    /// <summary>
    /// **A folder is still its own name.** The provider answers for drives and
    /// nothing else, so this must not start renaming ordinary folders after
    /// whatever the sidebar happens to hold.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_is_still_called_what_it_is_called()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-title-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            var pane = Pane(new Names { [Root] = "Windows (C:)" });

            await pane.NavigateAsync(folder);

            Assert.Equal(Path.GetFileName(folder), pane.Title);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    /// <summary>
    /// A provider that has no better name leaves the title exactly as it was,
    /// which is what every one of them did until now.
    /// </summary>
    [AvaloniaFact]
    public async Task A_provider_with_nothing_to_add_changes_nothing()
    {
        var pane = Pane(new Names());

        await pane.NavigateAsync(Root);

        Assert.Equal(PathRules.LeafName(Root), pane.Title);
    }

    /// <summary>And so does no provider at all.</summary>
    [AvaloniaFact]
    public async Task And_so_does_no_provider_at_all()
    {
        var pane = Pane(null);

        await pane.NavigateAsync(Root);

        Assert.Equal(PathRules.LeafName(Root), pane.Title);
    }

    // ---- and the crumb under it ---------------------------------------------

    /// <summary>The crumbs as they read, top to bottom.</summary>
    private static List<string> Crumbs(PaneViewModel pane)
        => [.. pane.Breadcrumbs.Select(c => c.Name)];

    /// <summary>
    /// **The other half of the same finding.** The tab was taught the
    /// sidebar's name for a drive and the crumb directly under it was not, so
    /// one window showed "Windows (C:)" and "C:\" for one drive, three inches
    /// apart — with the raw one in the bar you read to know where you are.
    /// </summary>
    [AvaloniaFact]
    public async Task The_root_crumb_is_named_the_way_the_sidebar_names_it()
    {
        var pane = Pane(new Names { [Root] = "Windows (C:)" });

        await pane.NavigateAsync(Root);

        Assert.Contains("Windows (C:)", Crumbs(pane));
        Assert.DoesNotContain(PathRules.LeafName(Root), Crumbs(pane));
    }

    /// <summary>
    /// **Every crumb asks, not only the first**, so a network share sitting
    /// above the folder you are in gets the sidebar's name for it too.
    ///
    /// The fake here deliberately answers for an ordinary folder, which the
    /// real providers never do — measured: both build that dictionary from the
    /// devices and shares groups only, never from the user's pinned places. So
    /// this separates "asks for every crumb" from "asks and then throws the
    /// answer away", which nothing else here can tell apart. What happens on a
    /// real machine, where the provider has nothing to say about a folder, is
    /// the test below.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_crumb_asks_for_a_better_name()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-crumb-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            var pane = Pane(new Names { [Root] = "Windows (C:)", [folder] = "Named by the sidebar" });

            await pane.NavigateAsync(folder);

            Assert.Contains("Named by the sidebar", Crumbs(pane));
            Assert.DoesNotContain(Path.GetFileName(folder), Crumbs(pane));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    /// <summary>
    /// **And a crumb the provider has nothing to say about is untouched** —
    /// both the root and the folder under it, which is every crumb an ordinary
    /// folder on a real machine produces. This is what all of them did until
    /// now, and the half that must not change.
    /// </summary>
    [AvaloniaFact]
    public async Task A_provider_with_nothing_to_add_leaves_the_crumbs_alone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vaktari-crumb-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            var pane = Pane(new Names());

            await pane.NavigateAsync(folder);

            Assert.Contains(PathRules.LeafName(Root), Crumbs(pane));
            Assert.Contains(Path.GetFileName(folder), Crumbs(pane));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    /// <summary>
    /// Names only what it was told about, so a place list that has not loaded
    /// yet cannot make a tab wait or mis-title one.
    /// </summary>
    private sealed class Names : Dictionary<string, string>, IPlacesProvider
    {
        public Names() : base(PathRules.Comparer) { }

        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string path) => TryGetValue(path, out var name) ? name : null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

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
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
