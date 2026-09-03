using Avalonia.Headless.XUnit;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Searching from a listing that is not a folder.
///
/// **"This folder only" over This PC searched for a folder called
/// "vaktari:computer".** The box was ticked by default and the scope was the
/// pane's raw path, which for a virtual listing is an internal scheme rather
/// than a directory.
///
/// On Windows the walk pushes it as a root, the read throws, the per-directory
/// catch swallows it, and the panel reports no results — a definite negative
/// about the whole machine. On Linux the enumerator throws before yielding
/// anything and the panel says the folder is not there any more, about a place
/// you are standing in. Explorer searches every drive from This PC.
///
/// The rule now lives on the PATH rather than on a tick box, so the box, the
/// label and the query handed to the backend cannot disagree about it — and a
/// session file edited by hand is answered by the same clause.
/// </summary>
public sealed class SearchScopeTests : OwnedViewModels
{
    private static readonly string Folder = Path.GetTempPath();

    [Theory]
    [InlineData("vaktari:computer")]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:recent-files")]
    [InlineData("vaktari:recent-locations")]
    public void A_listing_that_is_not_a_folder_scopes_to_nothing(string origin)
    {
        // Built as though the box HAD been ticked, which is what a hand-edited
        // session file can also say.
        var path = VirtualPaths.Search("report", origin, scoped: true);

        Assert.False(VirtualPaths.IsScoped(path));
        Assert.Null(VirtualPaths.ScopeOf(path));
    }

    /// <summary>Not a blanket ignore: a real folder still scopes, which is what
    /// the box is for.</summary>
    [Fact]
    public void A_real_folder_still_scopes_to_itself()
        => Assert.Equal(Folder, VirtualPaths.ScopeOf(
            VirtualPaths.Search("report", Folder, scoped: true)));

    [Fact]
    public void Unticked_still_means_everywhere()
        => Assert.Null(VirtualPaths.ScopeOf(
            VirtualPaths.Search("report", Folder, scoped: false)));

    // ---- and the box says what it is doing ----------------------------------

    /// <summary>
    /// Awaited, never blocked on. A headless test runs ON the dispatcher, and
    /// the load posts its finishing work back to it — so a GetResult here waits
    /// for a callback that cannot run until the wait ends.
    /// </summary>
    private async Task<PaneViewModel> Searching(string origin)
    {
        UseSearch(null);

        var pane = Own(new PaneViewModel(new Silent()));

        await pane.NavigateAsync(VirtualPaths.Search("report", origin, scoped: true));

        return pane;
    }

    /// <summary>
    /// A box that still reads "This folder only" while being ignored claims a
    /// scope the search does not have. The label carries the truth instead.
    /// </summary>
    [AvaloniaFact]
    public async Task Over_this_pc_the_box_says_it_is_searching_everywhere()
    {
        var pane = await Searching(VirtualPaths.Computer);

        Assert.False(pane.CanScopeSearch);
        Assert.False(pane.SearchScopedHere);
        Assert.Contains("everywhere", pane.SearchScopeLabel);
        Assert.DoesNotContain("Only in", pane.SearchScopeLabel);
    }

    /// <summary>And it names the listing, rather than printing the internal
    /// scheme at somebody.</summary>
    [AvaloniaFact]
    public async Task The_label_names_the_listing_rather_than_its_scheme()
        => Assert.DoesNotContain(
            "vaktari:", (await Searching(VirtualPaths.Trash)).SearchScopeLabel);

    [AvaloniaFact]
    public async Task In_a_real_folder_the_box_is_its_ordinary_self()
    {
        var pane = await Searching(Folder);

        Assert.True(pane.CanScopeSearch);
        Assert.True(pane.SearchScopedHere);
    }

    private sealed class Silent : Vaktari.Core.FileSystem.IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<Vaktari.Core.FileSystem.FileEntry>> EnumerateAsync(
            string path, Vaktari.Core.FileSystem.ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<Vaktari.Core.FileSystem.FileEntry?> GetEntryAsync(
            string path, CancellationToken ct)
            => ValueTask.FromResult<Vaktari.Core.FileSystem.FileEntry?>(null);

        public IDisposable Watch(
            string path, Action<Vaktari.Core.FileSystem.FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
