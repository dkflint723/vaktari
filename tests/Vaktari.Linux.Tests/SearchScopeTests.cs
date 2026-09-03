using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// "This folder only", when Baloo is the one answering.
///
/// **The scope was a bare string prefix, so searching one folder searched its
/// neighbours too.** Baloo indexes the whole home and the scope is applied to
/// its output; scoped to /home/u/proj it also let through /home/u/projects and
/// /home/u/proj-old. Worse, the walking fallback scopes by starting the walk at
/// the folder, so it never had this fault — the same search gave different
/// answers depending on whether Baloo happened to be running.
/// </summary>
public sealed class SearchScopeTests
{
    private const string Scope = "/home/u/proj";

    [Theory]
    [InlineData("/home/u/proj/notes.md")]
    [InlineData("/home/u/proj/deep/inside/a.txt")]
    public void A_file_inside_the_folder_is_in_scope(string path)
        => Assert.True(LinuxSearchProvider.InScope(Scope, path));

    /// <summary>The folder itself, which is a legitimate answer for a query
    /// matching its name.</summary>
    [Fact]
    public void And_so_is_the_folder_itself()
        => Assert.True(LinuxSearchProvider.InScope(Scope, Scope));

    /// <summary>
    /// The whole finding. Every one of these shares the scope's text as a
    /// prefix and none of them is inside it, so a rule that compares text
    /// rather than path segments lets all three through.
    /// </summary>
    [Theory]
    [InlineData("/home/u/projects/notes.md")]
    [InlineData("/home/u/proj-old/notes.md")]
    [InlineData("/home/u/projectile")]
    public void A_neighbour_that_merely_starts_the_same_way_is_not(string path)
        => Assert.False(LinuxSearchProvider.InScope(Scope, path));

    [Theory]
    [InlineData("/home/v/proj/notes.md")]
    [InlineData("/etc/passwd")]
    public void Nor_is_anything_elsewhere(string path)
        => Assert.False(LinuxSearchProvider.InScope(Scope, path));

    /// <summary>No scope means everywhere, which is what "Everywhere" asks
    /// for — the filter has to pass everything rather than nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_scope_lets_everything_through(string? scope)
    {
        Assert.True(LinuxSearchProvider.InScope(scope, "/etc/passwd"));
        Assert.True(LinuxSearchProvider.InScope(scope, "/home/u/proj/notes.md"));
    }

    /// <summary>A trailing separator is the same folder, and the rule must not
    /// start refusing its own contents because of one.</summary>
    [Fact]
    public void A_trailing_separator_names_the_same_folder()
        => Assert.True(LinuxSearchProvider.InScope("/home/u/proj/", "/home/u/proj/notes.md"));

    /// <summary>The root is a scope like any other, and everything is under
    /// it — the case where "ends at a separator" and "starts with" agree.
    /// </summary>
    [Fact]
    public void The_root_contains_everything()
        => Assert.True(LinuxSearchProvider.InScope("/", "/etc/passwd"));

    /// <summary>
    /// And that the indexed path asks it. The rule is pure and the loop that
    /// uses it drives a subprocess, so nothing else here can tell a correct
    /// rule that is consulted from a correct rule that is not.
    /// </summary>
    [Fact]
    public void The_indexed_walk_asks_the_rule_about_the_query_s_own_scope()
        => Assert.Contains(
            "if (!InScope(query.ScopePath, path)) continue;",
            RepoSource.Read("src", "Vaktari.Linux", "LinuxSearchProvider.cs"));
}
