using Avalonia.Headless.XUnit;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Searching from a listing that is not a folder.
///
/// **"This folder only" over This PC searched for a folder called
/// "vaktari:computer".** The box is ticked by default and the scope was the
/// pane's raw path, which for a virtual listing is an internal scheme rather
/// than a directory.
///
/// On Windows the walk pushes it as a root, the read throws, the per-directory
/// catch swallows it, and the panel reports no results — a definite negative
/// about the whole machine. On Linux the enumerator throws before yielding
/// anything and the panel says the folder is not there any more, about a place
/// you are standing in. Explorer searches every drive from This PC.
/// </summary>
public sealed class SearchScopeTests
{
    private static readonly string Folder = Path.GetTempPath();

    [AvaloniaTheory]
    [InlineData("vaktari:computer")]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:recent-files")]
    [InlineData("vaktari:recent-locations")]
    public void A_listing_that_is_not_a_folder_scopes_to_nothing(string path)
        => Assert.Null(SearchViewModel.ScopeFor(path, scopeToCurrentFolder: true));

    /// <summary>Not a blanket ignore: a real folder still scopes, which is what
    /// the box is for.</summary>
    [AvaloniaFact]
    public void A_real_folder_still_scopes_to_itself()
        => Assert.Equal(Folder, SearchViewModel.ScopeFor(Folder, scopeToCurrentFolder: true));

    [AvaloniaFact]
    public void Unticked_still_means_everywhere()
        => Assert.Null(SearchViewModel.ScopeFor(Folder, scopeToCurrentFolder: false));

    // ---- and the box says what it is doing ----------------------------------

    private static SearchViewModel Searching(string path)
        => new(null, () => path);

    /// <summary>
    /// A box that still reads "This folder only" while being ignored claims a
    /// scope the search does not have. The label carries the truth instead.
    /// </summary>
    [AvaloniaFact]
    public void Over_this_pc_the_box_says_it_is_searching_everywhere()
    {
        var search = Searching("vaktari:computer");

        Assert.False(search.CanScopeToCurrentFolder);
        Assert.Contains("everywhere", search.ScopeLabel);
        Assert.DoesNotContain("This folder only", search.ScopeLabel);
    }

    /// <summary>And it names the listing, rather than printing the internal
    /// scheme at somebody.</summary>
    [AvaloniaFact]
    public void The_label_names_the_listing_rather_than_its_scheme()
        => Assert.DoesNotContain("vaktari:", Searching("vaktari:trash").ScopeLabel);

    [AvaloniaFact]
    public void In_a_real_folder_the_box_is_its_ordinary_self()
    {
        var search = Searching(Folder);

        Assert.True(search.CanScopeToCurrentFolder);
        Assert.Equal("This folder only", search.ScopeLabel);
    }

    /// <summary>
    /// The markup reads both, or the rule is right and the box goes on lying.
    /// </summary>
    [AvaloniaFact]
    public void The_box_is_bound_to_the_label_and_the_gate()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        var at = markup.IndexOf("Search.ScopeToCurrentFolder", StringComparison.Ordinal);
        Assert.True(at > 0, "the scope box is not bound where this test looks for it");

        var element = markup[markup.LastIndexOf("<CheckBox", at, StringComparison.Ordinal)..at];

        Assert.Contains("Search.ScopeLabel", element);
        Assert.Contains("Search.CanScopeToCurrentFolder", element);
        Assert.DoesNotContain("Content=\"This folder only\"", element);
    }
}
