using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The folder tree where it lives: rooted at the sidebar's own places, shown
/// only when asked for, and following the pane only while anyone can see it.
///
/// The tree's own behaviour is <see cref="FolderTreeTests"/>' business. What is
/// here is the wiring — which is where the two halves can disagree.
/// </summary>
public sealed class SidebarFolderTreeTests
{
    private static string P(params string[] parts)
        => OperatingSystem.IsWindows()
            ? @"C:\" + string.Join('\\', parts)
            : "/" + string.Join('/', parts);

    private static Place At(string path, string label) => new()
    {
        Id = label,
        Label = label,
        Path = path,
        Kind = PlaceKind.UserFolder,
        Icon = "folder",
    };

    private sealed class Places(params Place[] places) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string path) => null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([new("PLACES", places)]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>Answers nothing, and counts what it was asked.</summary>
    private sealed class Quiet : IFileSystemProvider
    {
        public List<string> Read { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Read.Add(path);
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// **Every test that calls this needs the headless dispatcher**, so they
    /// are AvaloniaFacts: ReloadAsync publishes its groups through
    /// <c>Dispatcher.UIThread.InvokeAsync</c>, and awaiting that with nothing
    /// pumping is a deadlock rather than a slow test — measured, as a run that
    /// never finished.
    /// </summary>
    private static async Task<SidebarViewModel> Loaded(IPlacesProvider places, IFileSystemProvider fs)
    {
        var sidebar = new SidebarViewModel(places, fs: fs);

        await sidebar.ReloadAsync();

        return sidebar;
    }

    /// <summary>A sidebar with no filesystem behind it — every test fake, and
    /// the two other construction sites — has no tree and no section.</summary>
    [Fact]
    public void Without_a_filesystem_there_is_no_tree_at_all()
    {
        var sidebar = new SidebarViewModel(new Places());

        Assert.Null(sidebar.Tree);
        Assert.False(sidebar.ShowFolderTree);
    }

    [AvaloniaFact]
    public async Task The_roots_are_the_places()
    {
        var fs = new Quiet();

        var sidebar = await Loaded(new Places(At(P("home"), "Home"), At(P("work"), "Work")), fs);

        Assert.Equal(["Home", "Work"], sidebar.Tree!.Roots.Select(r => r.Label));

        // And nothing was read to find that out.
        Assert.Empty(fs.Read);
    }

    /// <summary>
    /// **A root has to be somewhere a filesystem can be asked about.** The bin
    /// and both Recent listings are places, and rooting a tree at one would
    /// offer a triangle onto a path no provider can enumerate.
    /// </summary>
    [AvaloniaFact]
    public async Task A_place_that_is_not_a_folder_is_not_a_root()
    {
        var sidebar = await Loaded(
            new Places(
                At(P("home"), "Home"),
                At(VirtualPaths.Trash, "Bin"),
                At(VirtualPaths.Files, "Recent files")),
            new Quiet());

        Assert.Equal(["Home"], sidebar.Tree!.Roots.Select(r => r.Label));
    }

    /// <summary>
    /// **Revealing reads a folder per level, so it waits until anyone can see
    /// it.** A folded section that still walked the tree on every navigation
    /// would be work for nobody.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folded_section_does_not_follow_the_pane()
    {
        var fs = new Quiet();

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = true;

        sidebar.SetCurrentPath(P("home", "docs"));

        Assert.Empty(fs.Read);
    }

    [AvaloniaFact]
    public async Task An_open_section_follows_the_pane()
    {
        var fs = new Quiet();

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = false;

        sidebar.SetCurrentPath(P("home", "docs"));

        // Home is opened looking for docs. It answers nothing here, so the walk
        // stops there — which is the point: one level, not a search.
        Assert.Equal([P("home")], fs.Read);
    }

    /// <summary>
    /// **An unmounted volume is given an empty Path on purpose** — GoToPlace
    /// relies on exactly that to refuse the click — so without a guard it
    /// becomes a root whose enumeration is of "", which is the working
    /// directory rather than anywhere the person can see.
    /// </summary>
    [AvaloniaFact]
    public async Task A_volume_that_is_not_mounted_is_not_a_root()
    {
        var sidebar = await Loaded(
            new Places(At(P("home"), "Home"), At("", "Backup drive")),
            new Quiet());

        Assert.Equal(["Home"], sidebar.Tree!.Roots.Select(r => r.Label));
    }

    /// <summary>Rebuilding the places — plugging a stick in — rebuilds the
    /// roots rather than adding to them.</summary>
    [AvaloniaFact]
    public async Task Reloading_the_places_rebuilds_the_roots()
    {
        var places = new Places(At(P("home"), "Home"));

        var sidebar = await Loaded(places, new Quiet());

        Assert.Single(sidebar.Tree!.Roots);

        await sidebar.ReloadAsync();

        Assert.Single(sidebar.Tree!.Roots);
    }
}
