using System.Diagnostics;
using Vaktari.Core.Search;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Finding a file by what is in it, on Linux: the walk that runs when Baloo is
/// absent, and Baloo itself when the box is left clear.
///
/// **The walk could match names and nothing else**, so on a machine without an
/// indexer there was no way to find a file by its contents at all, and on one
/// with Baloo there was no way NOT to — baloosearch answers from names and
/// contents together, and nothing narrowed it. The box has to mean the same
/// thing on both.
///
/// The walk's tests are plain facts: FileSystemEnumerable runs anywhere, and
/// the rules under test are the walk's own. Links, FIFOs and a stand-in
/// baloosearch need a real POSIX system and say so.
/// </summary>
public sealed class SearchContentTests : IDisposable
{
    private readonly Func<string?>? _before = LinuxSearchProvider.BalooOverride;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-content-" + Guid.NewGuid().ToString("N"));

    public SearchContentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        LinuxSearchProvider.BalooOverride = _before;

        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private async Task<List<string>> Search(
        string text, bool contents, bool caseSensitive = false, ContentSkips? skipped = null,
        string? scope = null, CancellationToken ct = default)
    {
        var query = new SearchQuery
        {
            Text = text,
            ScopePath = scope ?? _root,
            MatchContent = contents,
            CaseSensitive = caseSensitive,
            Skipped = skipped,
            MaxResults = 50,
        };

        var found = new List<string>();

        await foreach (var entry in new LinuxSearchProvider().SearchAsync(query, ct))
            found.Add(entry.Name);

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void NoBaloo() => LinuxSearchProvider.BalooOverride = () => null;

    // ---- the walk -----------------------------------------------------------

    /// <summary>
    /// Both ways round. It was true only with Baloo, because the walk could
    /// not look inside anything.
    /// </summary>
    [Fact]
    public void The_box_is_offered_with_or_without_an_index()
    {
        NoBaloo();
        Assert.True(new LinuxSearchProvider().SupportsContentSearch);

        LinuxSearchProvider.BalooOverride = () => "/usr/bin/baloosearch6";
        Assert.True(new LinuxSearchProvider().SupportsContentSearch);
    }

    [Fact]
    public async Task The_walk_finds_a_file_by_its_contents_only_when_asked()
    {
        NoBaloo();
        Write("notes.txt", "remember the milk");

        Assert.Empty(await Search("milk", contents: false));
        Assert.Equal(["notes.txt"], await Search("milk", contents: true));
    }

    /// <summary>Name OR contents, so ticking the box only ever adds rows.</summary>
    [Fact]
    public async Task A_name_still_answers_when_contents_are_asked_for_too()
    {
        NoBaloo();
        Write("milk.txt", "nothing relevant");
        Write("sub/notes.txt", "remember the milk");
        Write("other.txt", "nothing at all");

        Assert.Equal(["milk.txt", "notes.txt"], await Search("milk", contents: true));
    }

    /// <summary>Asked of a file whose name cannot answer, so only the content arm is under test.</summary>
    [Fact]
    public async Task Match_case_reaches_the_contents()
    {
        NoBaloo();
        Write("animals.txt", "A Zebra Crossing");

        Assert.Equal(["animals.txt"], await Search("zebra", contents: true));
        Assert.Empty(await Search("zebra", contents: true, caseSensitive: true));
        Assert.Equal(["animals.txt"], await Search("Zebra", contents: true, caseSensitive: true));
    }

    /// <summary>A pattern is a question about names, whatever the box says.</summary>
    [Fact]
    public async Task A_pattern_matches_names_only_whatever_the_box_says()
    {
        NoBaloo();
        Write("notes.md", "files like *.txt go here");
        Write("list.txt", "nothing");

        Assert.Equal(["list.txt"], await Search("*.txt", contents: true));
    }

    /// <summary>
    /// Counted on the way past. Extended rather than written, which leaves a
    /// sparse file on Linux; the text is in the first bytes, so a walk that
    /// ignored the limit would find it.
    /// </summary>
    [Fact]
    public async Task A_file_over_the_limit_is_skipped_and_counted()
    {
        NoBaloo();
        var big = Write("huge.log", "remember the milk");

        using (var stream = new FileStream(big, FileMode.Open, FileAccess.Write))
            stream.SetLength(ContentMatcher.MaxBytes + 1);

        var skipped = new ContentSkips();

        Assert.Empty(await Search("milk", contents: true, skipped: skipped));
        Assert.Equal(1, skipped.TooLarge);
    }

    /// <summary>
    /// **A link is matched by its name and not read through.** The file it
    /// points at is outside the folder being searched and holds the text, so a
    /// walk that followed links into contents would answer with the link.
    /// </summary>
    [PosixFact]
    public async Task A_link_is_not_read_through()
    {
        NoBaloo();
        var outside = Write("elsewhere/notes.txt", "remember the milk");
        var scope = Directory.CreateDirectory(Path.Combine(_root, "searched")).FullName;

        File.CreateSymbolicLink(Path.Combine(scope, "pointer"), outside);

        Assert.Empty(await Search("milk", contents: true, scope: scope));
    }

    /// <summary>
    /// **Nothing in the tree can hang the search.** Opening a FIFO for reading
    /// blocks inside open() until something writes, where no Stop reaches. A
    /// FIFO reports a length of zero and is never opened; a LINK to one
    /// reports the link's own length, which is why links are not read through.
    ///
    /// Bounded rather than awaited: a regression here is a search that never
    /// returns, and it has to fail as a test rather than hang the run. If it
    /// does hang, the FIFO is opened for writing on the way out, which is what
    /// lets the stuck open() return.
    /// </summary>
    [PosixFact]
    public async Task A_fifo_does_not_hang_the_search_directly_or_through_a_link()
    {
        NoBaloo();
        var fifo = Path.Combine(_root, "pipe");

        using (var mkfifo = Process.Start("mkfifo", [fifo]))
            await mkfifo.WaitForExitAsync();

        Assert.True(File.Exists(fifo), "mkfifo made nothing, so this proves nothing");

        File.CreateSymbolicLink(Path.Combine(_root, "via-link"), fifo);
        Write("notes.txt", "remember the milk");

        var search = Search("milk", contents: true);

        var finished = await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(20))) == search;

        if (!finished)
        {
            // Unstick the reader so the run can go on, then fail.
            using (new FileStream(fifo, FileMode.Open, FileAccess.Write)) { }
        }

        Assert.True(finished, "a content search blocked opening a FIFO");
        Assert.Equal(["notes.txt"], await search);
    }

    // ---- Baloo, narrowed to names -----------------------------------------

    [Theory]
    [InlineData("report-2024.pdf", "report 2024", true)]
    [InlineData("Report.pdf", "report", true)]
    [InlineData("notes.txt", "report", false)]
    [InlineData("report.pdf", "report 2024", false)]
    public void Every_word_has_to_be_in_the_name(string name, string text, bool expected)
        => Assert.Equal(expected, LinuxSearchProvider.NamedFor(name, text));

    /// <summary>A baloosearch that prints the given paths, as the real one prints its answers.</summary>
    private string Baloo(params string[] answers)
    {
        var script = Path.Combine(_root, "baloosearch-fake");

        File.WriteAllText(script,
            "#!/bin/sh\n" + string.Concat(answers.Select(a => $"echo '{a}'\n")) + "exit 0\n");
        File.SetUnixFileMode(
            script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return script;
    }

    /// <summary>
    /// **With the box clear, Baloo's answers are narrowed to names.** Baloo
    /// answers from contents as well, however it is asked, so a file that only
    /// mentions the word came back as though it were named for it — on this
    /// platform only, and with nothing to turn it off.
    /// </summary>
    [PosixFact]
    public async Task Baloo_is_narrowed_to_names_unless_contents_are_asked_for()
    {
        var named = Write("milk-list.txt", "eggs");
        var mentions = Write("notes.txt", "remember the milk");

        var script = Baloo(named, mentions);
        LinuxSearchProvider.BalooOverride = () => script;

        Assert.Equal(["milk-list.txt"], await Search("milk", contents: false));
        Assert.Equal(["milk-list.txt", "notes.txt"], await Search("milk", contents: true));
    }

    /// <summary>
    /// **An index that answered with nothing but contents has been built.**
    /// The walk behind Baloo runs when the index says nothing at all, which is
    /// how a switched-off indexer is told from a real "no matches" — so the
    /// question has to be asked before the narrowing. Here the walk WOULD find
    /// a file by name, and must not be reached.
    /// </summary>
    [PosixFact]
    public async Task Narrowing_every_answer_away_is_not_mistaken_for_no_index()
    {
        var mentions = Write("notes.txt", "remember the milk");
        Write("milk-unindexed.txt", "eggs");

        var script = Baloo(mentions);
        LinuxSearchProvider.BalooOverride = () => script;

        Assert.Empty(await Search("milk", contents: false));
    }
}
