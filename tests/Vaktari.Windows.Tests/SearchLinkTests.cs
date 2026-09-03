using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a name search does when it meets a link.
///
/// **A junction was removed from the results, not merely from the recursion.**
/// The walk set `AttributesToSkip = System | ReparsePoint`, and that setting
/// does two things at once: it stopped the walk descending through a junction,
/// which is right — a profile is full of legacy junctions pointing back at
/// their own ancestors and a walk that follows one does not finish — and it
/// also stopped the junction ever being returned as a row, which is not. A
/// folder link somebody made and named was the one name Windows search could
/// never find, while the same query on Linux listed it: `LinuxSearchProvider`
/// walks with `AttributesToSkip = 0` and refuses the descent separately, in a
/// `ShouldRecursePredicate`.
///
/// Junctions rather than symbolic links, for the reason
/// <see cref="TempTree.Junction"/> gives: `mklink /J` needs no elevation and
/// `Directory.CreateSymbolicLink` does, so a symlink here would fail on a
/// machine in its default configuration. Both carry FileAttributes.ReparsePoint
/// and the walk reads nothing finer than that.
/// </summary>
[SupportedOSPlatform("windows")]
public class SearchLinkTests
{
    private static async Task<List<FileEntry>> Search(string scope, string text, int max = 1000)
    {
        var query = new SearchQuery { Text = text, ScopePath = scope, MaxResults = max };

        // A bound rather than an assertion: the failure one of these tests
        // pins is a walk that recurses into itself, and the result cap ends
        // that long before this does. This is only so a mistake in the cap
        // reports as a failed test rather than as a run that never returns.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<FileEntry>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry);

        return found;
    }

    /// <summary>
    /// The row the fix is about: a junction matched by name comes back, and
    /// comes back carrying the flag the listing draws its link emblem from,
    /// rather than looking like an ordinary folder.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_is_listed_as_a_result_by_its_own_name()
    {
        using var tree = new TempTree();

        // Somewhere for the link to point that is not inside the scope. What
        // is in it does not matter: nothing here asserts on it, and nothing
        // in it can match the query.
        var outside = tree.Dir("outside");
        var scope = tree.Dir("tree");

        tree.Write("tree/real.txt");

        var junction = tree.Junction("tree/link", outside);

        var found = await Search(scope, "link");

        var entry = Assert.Single(found);

        Assert.Equal(junction, entry.FullPath);
        Assert.True(entry.IsSymlink, "the junction was listed, but not as a link");
    }

    /// <summary>
    /// The half the skip was there for, which has to survive the fix. A
    /// junction pointing at its own parent is the shape that never terminates,
    /// and without a guard the result cap is the only thing that stops it —
    /// by accident, and after handing back the same file five times under five
    /// different paths.
    /// </summary>
    [WindowsFact]
    public async Task The_walk_does_not_descend_through_a_junction_into_its_own_parent()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");

        tree.Write("tree/real.txt");
        tree.Junction("tree/loop", scope);

        // Capped low so a walk that does descend fails this as a handful of
        // extra rows, in a second, rather than as a hung run.
        var found = await Search(scope, "real", max: 5);

        var entry = Assert.Single(found);

        Assert.Equal(tree.At("tree", "real.txt"), entry.FullPath);
    }

    /// <summary>
    /// The other half of the attribute, deliberately kept. Dropping ReparsePoint
    /// from the skip is about links; System is a different question, and a walk
    /// that started answering with pagefile.sys and System Volume Information on
    /// every whole-machine query would be a second change wearing the first
    /// one's clothes.
    /// </summary>
    [WindowsFact]
    public async Task A_system_file_is_still_left_out()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var ordinary = tree.Write("tree/report.txt");
        var system = tree.Write("tree/report-system.txt");

        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var found = await Search(scope, "report");

        var entry = Assert.Single(found);

        Assert.Equal(ordinary, entry.FullPath);
    }
}
