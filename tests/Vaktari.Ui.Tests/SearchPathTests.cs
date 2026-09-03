using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A search, as somewhere you can be.
///
/// **Results were a popup and nothing else could be done with them** — no
/// multi-select, no drag, no context menu, no columns, no sorting, drawn OVER
/// the listing it was meant to help you act on. Everything else in this
/// application acts on a pane's entries, so the results were the one collection
/// of files it could not do anything to.
///
/// Giving a search a path is the move Recent and This PC already made.
/// </summary>
public sealed class SearchPathTests
{
    [Fact]
    public void A_search_path_carries_its_query()
        => Assert.Equal("report", VirtualPaths.QueryOf(
            VirtualPaths.Search("report", @"C:\Users\me", scoped: true)));

    /// <summary>
    /// **The origin is carried even when the search is unscoped**, and that is
    /// the whole reason there is a separate flag rather than just an empty
    /// scope: without it, ticking "everywhere" throws away the folder you
    /// started from and the box can never be unticked back to it. A one-way
    /// door.
    /// </summary>
    [Fact]
    public void And_the_folder_it_started_from_even_when_it_is_not_the_scope()
    {
        var everywhere = VirtualPaths.Search("report", @"C:\Users\me", scoped: false);

        Assert.Equal(@"C:\Users\me", VirtualPaths.OriginOf(everywhere));
        Assert.False(VirtualPaths.IsScoped(everywhere));
        Assert.Null(VirtualPaths.ScopeOf(everywhere));
    }

    [Fact]
    public void A_scoped_search_asks_the_backend_for_that_folder()
    {
        var here = VirtualPaths.Search("report", @"C:\Users\me", scoped: true);

        Assert.True(VirtualPaths.IsScoped(here));
        Assert.Equal(@"C:\Users\me", VirtualPaths.ScopeOf(here));
    }

    /// <summary>
    /// **"This folder only" over This PC searched for a folder called
    /// "vaktari:computer".** A search started somewhere that is not a folder
    /// has no origin to scope to, so it cannot claim one.
    /// </summary>
    [Fact]
    public void A_search_started_where_there_is_no_folder_is_never_scoped()
    {
        var path = VirtualPaths.Search("report", origin: null, scoped: true);

        Assert.Null(VirtualPaths.OriginOf(path));
        Assert.False(VirtualPaths.IsScoped(path));
        Assert.Null(VirtualPaths.ScopeOf(path));
    }

    /// <summary>
    /// **The escaping is not decoration.** Normalise runs over every path this
    /// pane compares and sits on both sides of every Same; a query is arbitrary
    /// text and can hold a colon, which would break the split, or a separator,
    /// which Normalise would rewrite. Escaping leaves only unreserved
    /// characters, so there is nothing for either to touch.
    /// </summary>
    [Theory]
    [InlineData("a:b")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("trailing/")]
    [InlineData("100%")]
    [InlineData("*.cs")]
    [InlineData("with space")]
    public void A_query_survives_whatever_is_typed_into_it(string query)
    {
        var path = VirtualPaths.Search(query, @"C:\Users\me", scoped: true);

        Assert.Equal(query, VirtualPaths.QueryOf(path));
        Assert.Equal(@"C:\Users\me", VirtualPaths.OriginOf(path));

        // Normalise has nothing to rewrite, which is what keeps every Same
        // comparison in the pane honest.
        Assert.Equal(path, PathRules.Normalise(path));
    }

    /// <summary>And a path with a separator in its origin survives too — that
    /// field is escaped for the same reason.</summary>
    [Fact]
    public void And_so_does_the_folder_it_names()
    {
        var path = VirtualPaths.Search("x", @"C:\Users\me\My Documents\", scoped: true);

        Assert.Equal(@"C:\Users\me\My Documents\", VirtualPaths.OriginOf(path));
        Assert.Equal(path, PathRules.Normalise(path));
    }

    /// <summary>
    /// Two searches that differ only in their query are different places, which
    /// is what stops one replacing the other's tab.
    /// </summary>
    [Fact]
    public void Two_different_questions_are_two_different_places()
    {
        var one = VirtualPaths.Search("report", @"C:\Users\me", scoped: true);
        var two = VirtualPaths.Search("report ", @"C:\Users\me", scoped: true);

        Assert.NotEqual(one, two);
        Assert.False(PathRules.Same(one, two));
    }

    /// <summary>
    /// **Malformed returns empty rather than throwing.** These strings go into
    /// the session file and come back at startup; a hand-edited or truncated
    /// one must give an empty search, not stop the window opening.
    /// </summary>
    [Theory]
    [InlineData("vaktari:search:")]
    [InlineData("vaktari:search:only-one-field")]
    [InlineData("vaktari:search:a:b")]
    [InlineData("vaktari:search:a:b:c:d")]
    public void A_broken_one_is_an_empty_search_rather_than_a_crash(string path)
    {
        Assert.Equal("", VirtualPaths.QueryOf(path));
        Assert.Null(VirtualPaths.OriginOf(path));
        Assert.False(VirtualPaths.IsScoped(path));
    }

    [Fact]
    public void Something_that_is_not_a_search_answers_nothing()
    {
        Assert.False(VirtualPaths.IsSearch(@"C:\Users\me"));
        Assert.False(VirtualPaths.IsSearch(VirtualPaths.Trash));
        Assert.Equal("", VirtualPaths.QueryOf(VirtualPaths.Trash));
    }

    /// <summary>
    /// A search is a listing that is not a directory, which is the one clause
    /// that buys it no watcher, no git subprocess, no free-space probe, no drop
    /// target, one breadcrumb, and no entry in Recent locations — which would
    /// be circular.
    /// </summary>
    [Fact]
    public void A_search_is_a_virtual_listing()
        => Assert.True(VirtualPaths.IsVirtual(
            VirtualPaths.Search("report", @"C:\Users\me", scoped: true)));

    /// <summary>
    /// The tab is titled by its question, not by the scheme. The label's final
    /// arm is a catch-all, so a search reaching it would be titled "Recent
    /// locations".
    /// </summary>
    [Fact]
    public void And_the_tab_is_named_for_the_question()
        => Assert.Equal("Search: report", VirtualPaths.Label(
            VirtualPaths.Search("report", @"C:\Users\me", scoped: true)));

    /// <summary>
    /// One key for every search, so the remembered view is "how I like searches
    /// to look" rather than one record per query anybody ever typed — an
    /// unbounded key in a store that is kept for the life of the profile.
    /// </summary>
    [Fact]
    public void Every_search_remembers_its_view_under_one_key()
    {
        Assert.True(VirtualPaths.IsSearch(VirtualPaths.SearchViewKey));

        Assert.NotEqual(
            VirtualPaths.SearchViewKey,
            VirtualPaths.Search("a", null, false));
    }
}
