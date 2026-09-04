using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Which results a capped walk spends its budget on.
///
/// **The walk was depth-first, and the cap turned a preference into a bug.**
/// The frontier was a Stack, so a search of C:\ committed the whole ten
/// thousand to whichever branch was popped first and stopped there — with
/// nothing said, so a truncated answer read as a complete one. Measured over
/// every fixed drive on a real machine, "e" capped at ten thousand returned
/// zero rows from the home folder and spent 8,624 of them inside one game's
/// asset tree. Ordering is the half tested here; the sentence that admits the
/// truncation is in the UI.
///
/// The tree is two branches, each with a hit at the top and a hit underneath
/// it, so what is asserted is DEPTH rather than which sibling NTFS hands back
/// first. Sibling order is the filesystem's business and nothing here may
/// depend on it.
/// </summary>
[SupportedOSPlatform("windows")]
public class SearchDepthTests
{
    private static async Task<List<FileEntry>> Search(string scope, string text, int max = 1000)
    {
        var query = new SearchQuery { Text = text, ScopePath = scope, MaxResults = max };

        // A bound rather than an assertion, as in SearchLinkTests: a mistake
        // here should report as a failed test, not as a run that never returns.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<FileEntry>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry);

        return found;
    }

    private static TempTree TwoDeepBranches()
    {
        var tree = new TempTree();

        tree.Write("a/hit-near-a.txt");
        tree.Write("a/down/hit-far-a.txt");
        tree.Write("b/hit-near-b.txt");
        tree.Write("b/down/hit-far-b.txt");

        return tree;
    }

    [WindowsFact]
    public async Task Everything_near_the_top_comes_back_before_anything_underneath_it()
    {
        using var tree = TwoDeepBranches();

        var found = await Search(tree.Root, "hit");

        Assert.Equal(4, found.Count);

        Assert.Equal(
            ["hit-near-a.txt", "hit-near-b.txt"],
            found.Take(2).Select(e => e.Name).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// And why it matters: with the budget gone, what is left is the shallow
    /// half of the tree rather than one branch of it.
    /// </summary>
    [WindowsFact]
    public async Task A_capped_walk_keeps_the_shallow_hits_rather_than_one_deep_branch()
    {
        using var tree = TwoDeepBranches();

        var found = await Search(tree.Root, "hit", max: 2);

        Assert.Equal(
            ["hit-near-a.txt", "hit-near-b.txt"],
            found.Select(e => e.Name).Order(StringComparer.Ordinal).ToList());
    }
}
