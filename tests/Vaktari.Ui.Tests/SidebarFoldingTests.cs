using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Folding a sidebar section away.
///
/// **A full sidebar was a scrolling sidebar.** Places, Devices, Shares,
/// Network, Remote, Sharing and Recent do not fit above the fold on a laptop,
/// so reaching the bottom of the list meant scrolling past four sections that
/// were never going to be clicked — and there was no way to put one away.
/// Explorer and Dolphin both fold a heading, and both remember it.
/// </summary>
public sealed class SidebarFoldingTests : OwnedViewModels
{
    private ShellViewModel Shell(ISessionStore? store = null)
        => Own(new ShellViewModel(new Inert(), store: store, places: new ThreeGroups()));

    private static PlaceGroupViewModel Group(ShellViewModel shell, string label)
        => shell.Sidebar.Groups.First(g => g.Label == label);

    /// <summary>The whole finding, from the group's side.</summary>
    [AvaloniaFact]
    public async Task A_group_can_be_folded_and_says_so()
    {
        var shell = Shell();
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        var places = Group(shell, "PLACES");

        Assert.False(places.IsCollapsed);

        places.IsCollapsed = true;

        Assert.True(shell.Sidebar.IsCollapsed("PLACES"));
        Assert.Contains("PLACES", shell.Sidebar.CollapsedSections);
    }

    /// <summary>
    /// **And it is still folded after the list is rebuilt.** Plug in a stick
    /// and every group object is thrown away and made again from the desktop's
    /// list, so a fold remembered on the group would last until the next
    /// reload — which is to say, until the next time anything happens.
    /// </summary>
    [AvaloniaFact]
    public async Task It_survives_the_rebuild_that_a_new_drive_causes()
    {
        var shell = Shell();
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        Group(shell, "DEVICES").IsCollapsed = true;

        await shell.Sidebar.ReloadAsync();

        Assert.True(Group(shell, "DEVICES").IsCollapsed);
        Assert.False(Group(shell, "PLACES").IsCollapsed);
    }

    /// <summary>
    /// **The sidebar's own four sections cannot fold a provider's group.**
    /// Folding is one set of strings matched without case, and a places
    /// provider is entitled to call a group NETWORK — the fake here does, and
    /// so does the one in ComputerListingTests. A bare "network" key would put
    /// that group away when somebody folded the discovery section.
    /// </summary>
    [AvaloniaFact]
    public async Task Folding_the_network_section_leaves_a_group_called_network_alone()
    {
        var shell = Shell();
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        shell.Sidebar.IsNetworkCollapsed = true;

        // **After a rebuild, which is when a colliding key would show.** A
        // group reads its fold from the sidebar when it is built, so a group
        // already on screen would not change under a bare "network" — it would
        // come back folded the next time a drive was plugged in, which is the
        // worst possible moment to notice.
        await shell.Sidebar.ReloadAsync();

        Assert.True(shell.Sidebar.IsNetworkCollapsed);
        Assert.False(Group(shell, "NETWORK").IsCollapsed,
                     "folding the discovery section also folded a group the provider calls NETWORK");
    }

    /// <summary>
    /// A provider that capitalises a label differently between two runs still
    /// finds its fold. The label is the key, and a label is data.
    /// </summary>
    [AvaloniaFact]
    public async Task A_label_matches_however_it_is_capitalised()
    {
        var shell = Shell();
        shell.Start(null, Path.GetTempPath());

        await shell.Sidebar.ReloadAsync();

        shell.Sidebar.RestoreCollapsed(["places"]);

        await shell.Sidebar.ReloadAsync();

        Assert.True(Group(shell, "PLACES").IsCollapsed);
    }

    /// <summary>The four written into the markup fold on their own.</summary>
    [AvaloniaFact]
    public void Each_of_the_four_fixed_sections_folds_by_itself()
    {
        var shell = Shell();
        shell.Start(null, Path.GetTempPath());

        shell.Sidebar.IsRemoteCollapsed = true;

        Assert.True(shell.Sidebar.IsRemoteCollapsed);
        Assert.False(shell.Sidebar.IsNetworkCollapsed);
        Assert.False(shell.Sidebar.IsSharingCollapsed);
        Assert.False(shell.Sidebar.IsRecentCollapsed);
    }

    /// <summary>
    /// Folding is written to the session, or it is a setting that lasts until
    /// the window closes.
    /// </summary>
    [AvaloniaFact]
    public void Folding_a_section_saves_the_session()
    {
        var store = new Listening();
        var shell = Shell(store);

        shell.Start(null, Path.GetTempPath());

        var before = store.Heard;

        shell.Sidebar.IsRecentCollapsed = true;

        Assert.True(store.Heard > before, "nothing asked for the session to be saved");

        Assert.Contains(
            SidebarSections.Recent,
            store.Last!.Windows[0].CollapsedSections);
    }

    /// <summary>And a restored session comes up folded.</summary>
    [AvaloniaFact]
    public void A_restored_session_comes_up_folded()
    {
        var shell = Shell();

        shell.Start(new SessionState
        {
            Windows = [new WindowSession { CollapsedSections = [SidebarSections.Sharing] }],
        });

        Assert.True(shell.Sidebar.IsSharingCollapsed);
        Assert.False(shell.Sidebar.IsRecentCollapsed);
    }

    /// <summary>
    /// **A session written before folding existed has no such key, and an
    /// absent key arrives as null.** Deserialization does not run property
    /// initializers here — the note beside the per-layout scales records the
    /// same thing — so the `?? []` at the restore site is the difference
    /// between starting and a NullReferenceException on the first launch after
    /// an upgrade, for everybody.
    /// </summary>
    [AvaloniaFact]
    public void A_session_that_never_heard_of_folding_starts_without_throwing()
    {
        var shell = Shell();

        shell.Start(new SessionState
        {
            Windows = [new WindowSession { CollapsedSections = null! }],
        });

        Assert.False(shell.Sidebar.IsRecentCollapsed);
        Assert.Empty(shell.Sidebar.CollapsedSections);
    }

    private sealed class Listening : ISessionStore
    {
        public int Heard { get; private set; }
        public SessionState? Last { get; private set; }

        public SessionState? Load() => null;

        public void NotifyChanged(SessionState state)
        {
            Heard++;
            Last = state;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Three groups, one of them labelled NETWORK — which is the collision the
    /// prefixed keys exist to prevent, and it is in the repository already.
    /// </summary>
    private sealed class ThreeGroups : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        private static Place At(string label, PlaceKind kind)
            => new()
            {
                Id = label,
                Label = label,
                Path = Path.GetTempPath(),
                Kind = kind,
                Icon = "folder",
            };

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("PLACES", [At("Home", PlaceKind.UserFolder)]),
                new PlaceGroup("DEVICES", [At("Disk", PlaceKind.Device)]),
                new PlaceGroup("NETWORK", [At("Share", PlaceKind.Network)]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.Ejected("gone"));

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
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
