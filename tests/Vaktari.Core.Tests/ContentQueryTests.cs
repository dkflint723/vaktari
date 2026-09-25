using System.Text;
using Vaktari.Core.Search;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The two rules both walks share about a search of contents: when files are
/// opened at all, and which refusals get counted.
///
/// Shared because each walk would otherwise carry its own copy, and the band
/// needs the first one too — it says "every folder and text file is read" only
/// when that is what the walk is doing.
/// </summary>
public sealed class ContentQueryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-content-query").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("*.txt", true)]
    [InlineData("note?.md", true)]
    [InlineData("report", false)]
    [InlineData("two words", false)]
    public void A_star_or_a_question_mark_makes_a_pattern(string text, bool pattern)
        => Assert.Equal(pattern, new SearchQuery { Text = text }.IsPattern);

    [Fact]
    public void Contents_are_read_when_asked_for() =>
        Assert.True(new SearchQuery { Text = "milk", MatchContent = true }.ReadsContents);

    [Fact]
    public void And_not_when_they_are_not() =>
        Assert.False(new SearchQuery { Text = "milk" }.ReadsContents);

    /// <summary>
    /// **A pattern is a question about names**, whatever the box says. Nobody
    /// typing "*.txt" means "files with an asterisk in them", and reading every
    /// file in reach to look for one is the most expensive possible way to
    /// answer a question nobody asked.
    /// </summary>
    [Fact]
    public void A_pattern_never_reads_contents() =>
        Assert.False(new SearchQuery { Text = "*.txt", MatchContent = true }.ReadsContents);

    // ---- Answers: the walks' one call -------------------------------------

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    private static bool Answers(SearchQuery query, string path)
        => ContentMatcher.Answers(query, path, new FileInfo(path).Length, CancellationToken.None);

    [Fact]
    public void A_file_holding_the_text_answers()
    {
        var path = Write("notes.txt", "remember the milk");

        Assert.True(Answers(new SearchQuery { Text = "milk", MatchContent = true }, path));
    }

    /// <summary>
    /// The query's own case flag reaches the read — Answers takes a query
    /// rather than a flag, so this is the one place it could be dropped.
    /// </summary>
    [Fact]
    public void The_query_s_case_flag_reaches_the_read()
    {
        var path = Write("zoo.txt", "A Zebra Crossing");

        Assert.True(Answers(new SearchQuery { Text = "zebra", MatchContent = true }, path));
        Assert.False(Answers(new SearchQuery { Text = "zebra", MatchContent = true, CaseSensitive = true }, path));
    }

    /// <summary>
    /// **A refusal the search has to own up to is counted where it happens.**
    /// A file over the limit is not opened — asserted with a path that does not
    /// exist, as in ContentMatcherTests — and the tally the pane reads goes up.
    /// </summary>
    [Fact]
    public void A_file_over_the_limit_is_counted_as_skipped()
    {
        var skipped = new ContentSkips();
        var query = new SearchQuery { Text = "milk", MatchContent = true, Skipped = skipped };

        var answered = ContentMatcher.Answers(
            query, Path.Combine(_root, "nowhere.log"), ContentMatcher.MaxBytes + 1, CancellationToken.None);

        Assert.False(answered);
        Assert.Equal(1, skipped.TooLarge);
        Assert.Equal(0, skipped.Online);
    }

    /// <summary>
    /// And only that refusal. A binary file and an unreadable one are left out
    /// of the count on purpose — see ContentSkips — so a search over a folder
    /// of images does not report hundreds of files "not read".
    /// </summary>
    [Fact]
    public void A_binary_or_unreadable_file_is_not_counted()
    {
        var skipped = new ContentSkips();
        var query = new SearchQuery { Text = "milk", MatchContent = true, Skipped = skipped };

        var binary = Path.Combine(_root, "image.bin");
        File.WriteAllBytes(binary, [0x89, 0x50, 0x00, 0x00, (byte)'m', (byte)'i', (byte)'l', (byte)'k']);

        Assert.False(Answers(query, binary));
        Assert.False(ContentMatcher.Answers(query, Path.Combine(_root, "gone.txt"), 10, CancellationToken.None));

        Assert.Equal(0, skipped.TooLarge);
    }

    /// <summary>Nobody asking for the count is the ordinary case, and must not throw.</summary>
    [Fact]
    public void With_nobody_counting_a_refusal_is_still_only_a_refusal()
    {
        var query = new SearchQuery { Text = "milk", MatchContent = true };

        Assert.False(ContentMatcher.Answers(
            query, Path.Combine(_root, "nowhere.log"), ContentMatcher.MaxBytes + 1, CancellationToken.None));
    }
}
