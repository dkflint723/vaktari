using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The Windows walk finding a file by what is in it.
///
/// **This walk could match names and nothing else**, and said so in the member
/// that owned the question: reading every file without an index "would be
/// indistinguishable from a hang on any real folder". ContentMatcher answers
/// the hang — Stop is honoured before every read — and these pin how the walk
/// uses it: only when asked, only when the name did not already answer, never
/// for a pattern, and never by opening a file whose data is not on this disk.
///
/// Through the real walk over a real tree rather than through the matcher,
/// which has its own tests: what can go wrong here is the wiring, and a
/// matcher test stays green whatever the walk does with it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SearchContentTests
{
    private static async Task<List<string>> Walk(
        string scope, string text, bool contents,
        bool caseSensitive = false, ContentSkips? skipped = null)
    {
        var query = new SearchQuery
        {
            Text = text,
            ScopePath = scope,
            MatchContent = contents,
            CaseSensitive = caseSensitive,
            Skipped = skipped,
            MaxResults = 50,
        };

        // A bound rather than an assertion: a mistake here should report as a
        // failed test rather than as a run that never returns.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var found = new List<string>();

        await foreach (var entry in new WindowsSearchProvider().SearchAsync(query, cts.Token))
            found.Add(entry.Name);

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    [WindowsFact]
    public void The_walk_says_it_can_look_inside_files()
        => Assert.True(new WindowsSearchProvider().SupportsContentSearch);

    /// <summary>
    /// Off unless asked, which is the default the box starts at: a search that
    /// read every file without being told to would be the hang the old comment
    /// was afraid of, imposed on everybody.
    /// </summary>
    [WindowsFact]
    public async Task A_file_is_found_by_its_contents_only_when_asked()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/notes.txt", "remember the milk");

        Assert.Empty(await Walk(scope, "milk", contents: false));
        Assert.Equal(["notes.txt"], await Walk(scope, "milk", contents: true));
    }

    /// <summary>
    /// **Name OR contents**, so ticking the box only ever adds rows. The file
    /// named for the word holds nothing of it, and it is still an answer.
    /// </summary>
    [WindowsFact]
    public async Task A_name_still_answers_when_contents_are_asked_for_too()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/milk.txt", "nothing relevant");
        tree.Write("tree/notes.txt", "remember the milk");
        tree.Write("tree/other.txt", "nothing at all");

        Assert.Equal(["milk.txt", "notes.txt"], await Walk(scope, "milk", contents: true));
    }

    /// <summary>
    /// Deeper than the top folder, because the read is inside the walk's own
    /// frontier loop and a wiring mistake could easily reach only one level.
    /// </summary>
    [WindowsFact]
    public async Task A_file_further_down_is_read_too()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/a/b/deep.txt", "remember the milk");

        Assert.Equal(["deep.txt"], await Walk(scope, "milk", contents: true));
    }

    /// <summary>
    /// Match case reaches the read as well as the name. Asked of a file whose
    /// NAME cannot answer, so only the content arm is under test.
    /// </summary>
    [WindowsFact]
    public async Task Match_case_reaches_the_contents()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/animals.txt", "A Zebra Crossing");

        Assert.Equal(["animals.txt"], await Walk(scope, "zebra", contents: true));
        Assert.Empty(await Walk(scope, "zebra", contents: true, caseSensitive: true));
        Assert.Equal(["animals.txt"], await Walk(scope, "Zebra", contents: true, caseSensitive: true));
    }

    /// <summary>
    /// **A pattern is a question about names.** This file holds the pattern's
    /// own characters, so a walk that read contents for a pattern would find
    /// it; nobody typing "*.txt" is asking for it.
    /// </summary>
    [WindowsFact]
    public async Task A_pattern_matches_names_only_whatever_the_box_says()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/notes.md", "files like *.txt go here");
        tree.Write("tree/list.txt", "nothing");

        Assert.Equal(["list.txt"], await Walk(scope, "*.txt", contents: true));
    }

    /// <summary>
    /// **A file held online is not opened, because opening it downloads it.**
    /// OFFLINE is the one of the three "not on this disk" attributes a test can
    /// set without a sync client; the rule reads all three as one mask, and
    /// the next test pins the other two.
    /// </summary>
    [WindowsFact]
    public async Task A_file_held_online_is_not_read_and_is_counted()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var held = tree.Write("tree/synced.txt", "remember the milk");
        File.SetAttributes(held, File.GetAttributes(held) | FileAttributes.Offline);

        Assert.True(File.GetAttributes(held).HasFlag(FileAttributes.Offline),
            "the test could not mark the file offline, so it proves nothing");

        var skipped = new ContentSkips();

        Assert.Empty(await Walk(scope, "milk", contents: true, skipped: skipped));
        Assert.Equal(1, skipped.Online);
    }

    /// <summary>
    /// RECALL_ON_OPEN and RECALL_ON_DATA_ACCESS, which only a cloud files
    /// provider can set, asked of the rule directly against a real file that
    /// DOES hold the text — so false can only mean it was not read.
    /// </summary>
    [WindowsTheory]
    [InlineData(0x0004_0000)]
    [InlineData(0x0040_0000)]
    public void A_placeholder_is_refused_by_its_attribute(int attribute)
    {
        using var tree = new TempTree();

        var path = tree.Write("synced.txt", "remember the milk");
        var entry = new FileEntry("synced.txt", path, new FileInfo(path).Length,
                                  DateTimeOffset.UnixEpoch, EntryFlags.None);
        var skipped = new ContentSkips();
        var query = new SearchQuery { Text = "milk", MatchContent = true, Skipped = skipped };

        Assert.True(WindowsSearchProvider.Contains(entry, FileAttributes.Normal, query, CancellationToken.None));
        Assert.False(WindowsSearchProvider.Contains(entry, (FileAttributes)attribute, query, CancellationToken.None));
        Assert.Equal(1, skipped.Online);
    }

    /// <summary>
    /// **A link is matched by its name and not read through.** Asked of the
    /// rule with a real file that holds the text, flagged as a link, because
    /// making a symbolic link on Windows needs a privilege a test run may not
    /// have.
    /// </summary>
    [WindowsFact]
    public void A_link_is_not_read_through()
    {
        using var tree = new TempTree();

        var path = tree.Write("pointer.txt", "remember the milk");
        var link = new FileEntry("pointer.txt", path, new FileInfo(path).Length,
                                 DateTimeOffset.UnixEpoch, EntryFlags.Symlink);
        var query = new SearchQuery { Text = "milk", MatchContent = true };

        Assert.False(WindowsSearchProvider.Contains(link, FileAttributes.ReparsePoint, query, CancellationToken.None));
    }

    /// <summary>
    /// **Too large is counted on the way past.** Made at the limit plus one by
    /// extending the file rather than writing it, which NTFS does without
    /// writing sixty-four megabytes; the text is in the first bytes, so a walk
    /// that ignored the limit would find it.
    /// </summary>
    [WindowsFact]
    public async Task A_file_over_the_limit_is_skipped_and_counted()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var big = tree.Write("tree/huge.log", "remember the milk");

        using (var stream = new FileStream(big, FileMode.Open, FileAccess.Write))
            stream.SetLength(ContentMatcher.MaxBytes + 1);

        var skipped = new ContentSkips();

        Assert.Empty(await Walk(scope, "milk", contents: true, skipped: skipped));
        Assert.Equal(1, skipped.TooLarge);
    }
}
