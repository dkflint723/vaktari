using Vaktari.Core.Search;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Whether this platform's backend claims an index for a given question, which
/// is what decides whether the band above the results explains the wait.
///
/// **The claim used to be IsAvailable, and this provider hardcoded that true.**
/// It had to: false meant "will answer nothing at all", and this one always
/// answers — Baloo when there is an index, a walk when there is not. So the
/// flag the warning hung on could not tell those two apart, and a search on a
/// box with no indexer read exactly like a search on a box with one.
///
/// Asked through the BalooOverride seam, which is a probe rather than a path
/// so that "definitely not installed" can be said at all. These therefore
/// describe a machine rather than the machine they run on, and mean the same
/// thing on the Windows agent and on a Fedora box with a working index.
/// </summary>
public sealed class SearchIndexClaimTests : IDisposable
{
    private readonly Func<string?>? _before = LinuxSearchProvider.BalooOverride;

    public void Dispose()
    {
        LinuxSearchProvider.BalooOverride = _before;
        GC.SuppressFinalize(this);
    }

    private static bool Claims(string text) =>
        new LinuxSearchProvider().AnswersFromIndex(new SearchQuery { Text = text });

    /// <summary>
    /// baloosearch on the machine is the index, and the same answer
    /// SupportsContentSearch has always given: it is the only thing on this
    /// platform that is not a walk.
    /// </summary>
    [Fact]
    public void With_baloo_present_an_index_is_claimed()
    {
        LinuxSearchProvider.BalooOverride = () => "/usr/bin/baloosearch6";

        Assert.True(Claims("report"));
    }

    /// <summary>
    /// And on a machine with no baloosearch anywhere on PATH there is nothing
    /// to claim, so the warning stands.
    /// </summary>
    [Fact]
    public void Without_it_there_is_no_index_to_claim()
    {
        LinuxSearchProvider.BalooOverride = () => null;

        Assert.False(Claims("report"));
    }

    /// <summary>
    /// **A glob has never reached Baloo, and the claim used to say otherwise.**
    /// The index stores words rather than filename patterns, so SearchAsync
    /// routes anything holding * or ? straight to the recursive walk — home
    /// plus every mounted drive. A claim made without reading the query text
    /// therefore suppressed the warning for "*.pdf" on every KDE box with
    /// baloosearch installed: the exact fault the band exists to report, for
    /// the commonest shape of slow search there is.
    /// </summary>
    [Theory]
    [InlineData("*.pdf")]
    [InlineData("report?.txt")]
    public void A_glob_walks_and_says_so_even_with_baloo_installed(string text)
    {
        LinuxSearchProvider.BalooOverride = () => "/usr/bin/baloosearch6";

        Assert.False(Claims(text));
    }

    /// <summary>
    /// **What the band says is what the search does**, because they are one
    /// expression rather than two that agree today. SearchAsync routes on
    /// AnswersFromIndex, so a claim and a route cannot part company the way
    /// they had — the property said "there is an index" for a glob that the
    /// router had always sent to the walk.
    ///
    /// Read off the line SearchWithBalooThenWalkingAsync prints when the index
    /// answers nothing, which is the one place the two branches are
    /// distinguishable from outside: the named binary cannot be started on
    /// either branch, and the fallback IS the walk, so the results alone are
    /// identical whichever way the query went. Both directions are asserted —
    /// the glob must not reach Baloo and the word must — because only the pair
    /// says the router is reading the query at all.
    ///
    /// Console.SetError is process-global and this assembly is deliberately
    /// serial, which is what makes borrowing it safe here.
    /// </summary>
    [Theory]
    [InlineData("*.pdf", false)]
    [InlineData("report", true)]
    public async Task The_router_takes_the_branch_the_band_names(string text, bool viaBaloo)
    {
        var root = Path.Combine(Path.GetTempPath(), "vaktari-glob-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        var stderr = Console.Error;
        var said = new StringWriter();

        try
        {
            File.WriteAllText(Path.Combine(root, "report.pdf"), "x");

            LinuxSearchProvider.BalooOverride = () => Path.Combine(root, "no-such-baloosearch");

            var query = new SearchQuery { Text = text, ScopePath = root, MaxResults = 50 };
            var provider = new LinuxSearchProvider();
            var found = new List<string>();

            Console.SetError(said);

            await foreach (var entry in provider.SearchAsync(query, CancellationToken.None))
                found.Add(Path.GetFileName(entry.FullPath));

            Console.SetError(stderr);

            Assert.Equal(viaBaloo, provider.AnswersFromIndex(query));

            Assert.Equal(
                viaBaloo,
                said.ToString().Contains("baloo returned nothing", StringComparison.Ordinal));

            // Either way the file is found: the point is which route found it,
            // not whether anything did.
            Assert.Contains("report.pdf", found);
        }
        finally
        {
            Console.SetError(stderr);

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
