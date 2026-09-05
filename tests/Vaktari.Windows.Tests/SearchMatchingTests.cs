using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a search query means, which has to be the same sentence on both
/// systems.
///
/// **Windows had only the substring half.** `LinuxSearchProvider` treats a
/// query containing <c>*</c> or <c>?</c> as a pattern and anything else as a
/// substring; the Windows walk matched `entry.Name.Contains(text)` and nothing
/// else. So `*.cs` found every C# file on Linux and nothing whatsoever on
/// Windows — no filename contains those three characters in that order — and
/// the failure looked exactly like an empty result set rather than like an
/// unsupported syntax. A glob is the one search syntax a person tries without
/// being told it exists.
/// </summary>
[SupportedOSPlatform("windows")]
public class SearchMatchingTests
{
    private static bool Match(string name, string query, bool caseSensitive = false)
        => WindowsSearchProvider.Matches(
            name,
            query,
            glob: query.Contains('*') || query.Contains('?'),
            comparison: caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            caseSensitive: caseSensitive);

    [WindowsTheory]
    [InlineData("Program.cs", "*.cs", true)]
    [InlineData("Program.csproj", "*.cs", false)]
    [InlineData("notes.txt", "*.cs", false)]
    [InlineData("WindowsSearchProvider.cs", "*Search*", true)]
    [InlineData("WindowsSearchProvider.cs", "*Search*.cs", true)]
    [InlineData("note1.txt", "note?.txt", true)]
    [InlineData("note12.txt", "note?.txt", false)]
    public void A_pattern_is_matched_as_a_pattern(string name, string query, bool expected)
        => Assert.Equal(expected, Match(name, query));

    /// <summary>
    /// The half that already worked, kept so adding globs cannot quietly turn
    /// every plain word into a pattern that matches only whole names.
    /// </summary>
    [WindowsTheory]
    [InlineData("WindowsSearchProvider.cs", "Search", true)]
    [InlineData("WindowsSearchProvider.cs", "search", true)]
    [InlineData("notes.txt", "note", true)]
    [InlineData("notes.txt", "zzz", false)]
    public void A_plain_word_is_matched_as_a_substring(string name, string query, bool expected)
        => Assert.Equal(expected, Match(name, query));

    /// <summary>
    /// Case follows the query's own setting in both modes, rather than the
    /// pattern arm quietly ignoring it.
    /// </summary>
    [WindowsTheory]
    [InlineData("Program.CS", "*.cs", false, true)]
    [InlineData("Program.CS", "*.cs", true, false)]
    [InlineData("Program.cs", "*.cs", true, true)]
    [InlineData("Notes.txt", "notes", false, true)]
    [InlineData("Notes.txt", "notes", true, false)]
    public void Case_sensitivity_applies_to_both_arms(
        string name, string query, bool caseSensitive, bool expected)
        => Assert.Equal(expected, Match(name, query, caseSensitive));

    /// <summary>
    /// A bare <c>*</c> is a legitimate "everything here" query, and the pattern
    /// arm has to answer it rather than falling through to a substring test for
    /// a literal asterisk.
    /// </summary>
    [WindowsTheory]
    [InlineData("anything.txt")]
    [InlineData("a")]
    public void A_bare_star_matches_everything(string name)
        => Assert.True(Match(name, "*"));

    /// <summary>
    /// The link the search band's box now depends on: the flag on the QUERY,
    /// not the argument to <see cref="Match"/>, is what the walk branches on.
    ///
    /// Everything above calls the matcher directly, so all of it stays green if
    /// <c>Walk</c> stops passing <c>query.CaseSensitive</c> down and hard-codes
    /// the insensitive comparison — which is exactly the behaviour the
    /// application had until something started setting the field.
    ///
    /// One name, asked for twice: NTFS is case-insensitive about names, so
    /// "Report.txt" and "report.txt" cannot both exist in a folder and the
    /// difference has to be made by the query.
    /// </summary>
    [WindowsFact]
    public async Task The_query_flag_reaches_the_walk_rather_than_only_the_matcher()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var report = tree.Write("tree/Report.txt");

        Assert.Equal(report, Assert.Single(await Walk(scope, "report", false)).FullPath);

        Assert.Empty(await Walk(scope, "report", true));

        // And the capital spelling still comes back, so the sensitive walk is
        // matching rather than merely failing.
        Assert.Equal(report, Assert.Single(await Walk(scope, "Report", true)).FullPath);

        // Both arms, because Walk reads the flag twice: once for the
        // comparison above and once for the pattern's ignoreCase. A glob is the
        // only way to reach the second read.
        Assert.Empty(await Walk(scope, "*.TXT", true));
        Assert.Equal(report, Assert.Single(await Walk(scope, "*.txt", true)).FullPath);
    }

    private static async Task<List<Vaktari.Core.FileSystem.FileEntry>> Walk(
        string scope, string text, bool caseSensitive)
    {
        var query = new Vaktari.Core.Search.SearchQuery
        {
            Text = text,
            ScopePath = scope,
            CaseSensitive = caseSensitive,
            MaxResults = 50,
        };

        // A bound rather than an assertion: a mistake here should report as a
        // failed test rather than as a run that never returns.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<Vaktari.Core.FileSystem.FileEntry>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry);

        return found;
    }

    /// <summary>
    /// The claim the band reads before it draws the box. False here would hide
    /// a control over a backend that has honoured the flag since it was
    /// written.
    /// </summary>
    [WindowsFact]
    public void The_walk_says_it_honours_the_flag()
        => Assert.True(new WindowsSearchProvider().SupportsCaseSensitivity);
}
