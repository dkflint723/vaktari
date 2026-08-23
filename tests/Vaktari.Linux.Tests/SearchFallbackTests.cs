using Vaktari.Core.Search;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Search when Baloo is installed but has nothing to say.
///
/// **Having the tool on PATH is not the same as having an index**, and the gap
/// swallowed searches whole. Any desktop with a single KDE application
/// installed pulls baloosearch onto PATH; where the indexer has never run — or
/// the user turned indexing off — a query returns an empty result set and exits
/// cleanly. Nothing on stderr, nothing wrong with the exit code, simply no
/// answers. Vaktari then reported "no results (baloo)": a definite statement
/// about the filesystem, when the truth was that the index does not exist and
/// the walk would have found the file immediately.
///
/// Linux only — the fake stands in for a program, and the walk it falls back to
/// is the same code either way.
/// </summary>
public sealed class SearchFallbackTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-search-" + Guid.NewGuid().ToString("N"));

    public SearchFallbackTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "unrelated.txt"), "x");
    }

    public void Dispose()
    {
        LinuxSearchProvider.BalooOverride = null;

        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A baloosearch that finds nothing and is happy about it.</summary>
    private string SilentBaloo()
    {
        var script = Path.Combine(_root, "baloosearch-silent");

        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return script;
    }

    private async Task<List<string>> Search(string text)
    {
        var found = new List<string>();

        var query = new SearchQuery { Text = text, ScopePath = _root, MaxResults = 50 };

        await foreach (var entry in new LinuxSearchProvider().SearchAsync(query, CancellationToken.None))
            found.Add(Path.GetFileName(entry.FullPath));

        return found;
    }

    /// <summary>
    /// The one that matters: the index says nothing, and the file is found
    /// anyway.
    /// </summary>
    [Fact]
    public async Task An_index_with_nothing_in_it_falls_back_to_the_walk()
    {
        if (!OperatingSystem.IsLinux()) return;

        LinuxSearchProvider.BalooOverride = SilentBaloo();

        Assert.Contains("report.pdf", await Search("report"));
    }

    /// <summary>
    /// And a search that genuinely matches nothing still says nothing — the
    /// fallback must not start inventing results.
    /// </summary>
    [Fact]
    public async Task A_query_that_matches_nothing_still_finds_nothing()
    {
        if (!OperatingSystem.IsLinux()) return;

        LinuxSearchProvider.BalooOverride = SilentBaloo();

        Assert.Empty(await Search("nothinghasthisname"));
    }

    /// <summary>
    /// With no Baloo at all the walk was always used, and still is. This is the
    /// path that was working, and it has to keep working.
    /// </summary>
    [Fact]
    public async Task With_no_baloo_the_walk_is_used_as_before()
    {
        if (!OperatingSystem.IsLinux()) return;

        LinuxSearchProvider.BalooOverride = null;

        var found = await Search("report");

        // Only asserted when the machine has no real Baloo, which is the state
        // this case is about; a box with a working index answers from it.
        if (new LinuxSearchProvider().BackendName == "walk")
            Assert.Contains("report.pdf", found);
    }
}
