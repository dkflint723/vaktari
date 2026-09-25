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
        bool caseSensitive = false, ContentSkips? skipped = null, bool readsConcealed = true)
    {
        var query = new SearchQuery
        {
            Text = text,
            ScopePath = scope,
            MatchContent = contents,
            CaseSensitive = caseSensitive,
            Skipped = skipped,
            ReadsConcealed = readsConcealed,
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

    /// <summary>
    /// **A hidden file is not opened when hidden files are not shown.** Its
    /// row would be dropped by the pane after the walk had read it, so reading
    /// it was a cost with nothing to show — and a whole AppData of them.
    /// Asked of a file whose name cannot answer, so only the read is at stake.
    /// </summary>
    [WindowsFact]
    public async Task A_hidden_file_is_read_only_when_hidden_files_are_shown()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var hidden = tree.Write("tree/notes.txt", "remember the milk");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.Equal(["notes.txt"], await Walk(scope, "milk", contents: true, readsConcealed: true));
        Assert.Empty(await Walk(scope, "milk", contents: true, readsConcealed: false));

        // And its NAME still answers either way; the pane decides whether the
        // row is shown, as it always has.
        Assert.Equal(["notes.txt"], await Walk(scope, "notes", contents: true, readsConcealed: false));
    }

    /// <summary>
    /// **A log a program still has open is read.** NTFS updates the size in a
    /// file's directory entry when a handle closes, so the walk lists a file
    /// that is being written as 0 bytes — and a zero length used to mean "never
    /// open it". Checked first that the listing really does say 0 here, since
    /// the test proves nothing on a filesystem that reports the live size.
    /// </summary>
    [WindowsFact]
    public async Task A_file_still_being_written_is_read()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var log = Path.Combine(scope, "app.log");

        using var writer = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("remember the milk"u8);
        writer.Flush();

        Assert.True(new DirectoryInfo(scope).EnumerateFiles().Single().Length == 0,
            "the listing already reports the live size, so this proves nothing");

        Assert.Equal(["app.log"], await Walk(scope, "milk", contents: true));
    }

    /// <summary>
    /// **Each entry is judged by its own contents**, including the names the
    /// Win32 path rules would rewrite: "report." opens as its neighbour
    /// "report", and "nul" as the NUL device. Made through the extended prefix,
    /// the only way to make them, and removed the same way — TempTree's own
    /// cleanup would be rewritten too.
    /// </summary>
    [WindowsFact]
    public async Task A_name_windows_would_rewrite_is_read_as_itself()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var dotted = @"\\?\" + Path.Combine(scope, "report.");
        var device = @"\\?\" + Path.Combine(scope, "nul");

        tree.Write("tree/report", "plain report holds banana");
        File.WriteAllText(dotted, "the dotted one holds cherry");
        File.WriteAllText(device, "remember the milk");

        try
        {
            Assert.Equal(["report"], await Walk(scope, "banana", contents: true));
            Assert.Equal(["report."], await Walk(scope, "cherry", contents: true));
            Assert.Equal(["nul"], await Walk(scope, "milk", contents: true));
        }
        finally
        {
            File.Delete(dotted);
            File.Delete(device);
        }
    }

    [WindowsFact]
    public void The_extended_prefix_is_added_once_and_keeps_a_share()
    {
        Assert.Equal(@"\\?\C:\x\report.", WindowsSearchProvider.Extended(@"C:\x\report."));
        Assert.Equal(@"\\?\UNC\server\share\a.txt", WindowsSearchProvider.Extended(@"\\server\share\a.txt"));
        Assert.Equal(@"\\?\C:\x\a.txt", WindowsSearchProvider.Extended(@"\\?\C:\x\a.txt"));
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
    /// set without a sync client, so it is the one the whole walk is asked
    /// about; the tests below pin the mask and the exposed read.
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
    /// provider can set — OnlineOnlyFilesTests registers one of its own to put
    /// the second on a real placeholder. What is pinned here is that the rule
    /// reads all three "not on this disk" bits.
    /// </summary>
    [WindowsTheory]
    [InlineData(0x0000_1000)]
    [InlineData(0x0004_0000)]
    [InlineData(0x0040_0000)]
    public void Each_of_the_three_not_here_bits_counts_as_online(int attribute)
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");

        Assert.NotEqual(0, (int)(Placeholders.HeldOnline & (FileAttributes)attribute));

        // And the one a test CAN set is read by the exposed read itself, not
        // only listed in the mask.
        if (attribute == 0x0000_1000)
        {
            var held = tree.Write("tree/synced.txt", "x");
            File.SetAttributes(held, File.GetAttributes(held) | FileAttributes.Offline);

            Assert.Contains("synced.txt", Placeholders.HeldOnlineIn(scope));
        }
    }

    /// <summary>
    /// **The question is asked with placeholders exposed, and only for as long
    /// as it takes.** This process sees a sync client's placeholders disguised
    /// as ordinary files, so a read in its own mode could not tell an
    /// online-only file from one on the disk. The mode is read from inside the
    /// exposed read, and again after it, on the same thread.
    /// </summary>
    [WindowsFact]
    public void The_online_question_is_asked_with_placeholders_exposed()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        var before = Placeholders.RtlQueryThreadPlaceholderCompatibilityMode();
        sbyte during = -1;

        Placeholders.WhileExposed = dir =>
        {
            if (dir == scope) during = Placeholders.RtlQueryThreadPlaceholderCompatibilityMode();
        };

        try
        {
            Placeholders.HeldOnlineIn(scope);
        }
        finally
        {
            Placeholders.WhileExposed = null;
        }

        Assert.Equal(2, during);
        Assert.Equal(before, Placeholders.RtlQueryThreadPlaceholderCompatibilityMode());
    }

    /// <summary>
    /// **The one-file question is asked exposed too, and puts the mode back.**
    /// Nothing else can prove it: OnlineOnlyFilesTests found the test host
    /// seeing a real placeholder's online bits in its own mode, so every
    /// assertion made there holds with the expose step deleted. Here, beside
    /// the folder read's own test, because the seam is one static both share.
    /// </summary>
    [WindowsFact]
    public void The_one_file_question_is_asked_with_placeholders_exposed()
    {
        using var tree = new TempTree();

        var file = tree.Write("tree/notes.txt", "remember the milk");
        var before = Placeholders.RtlQueryThreadPlaceholderCompatibilityMode();
        sbyte during = -1;

        Assert.True(before != 2, "the thread was exposed already, so 2 inside would prove nothing");

        Placeholders.WhileExposed = path =>
        {
            if (path == file) during = Placeholders.RtlQueryThreadPlaceholderCompatibilityMode();
        };

        try
        {
            Assert.False(Placeholders.IsHeldOnline(file));
        }
        finally
        {
            Placeholders.WhileExposed = null;
        }

        Assert.Equal(2, during);
        Assert.Equal(before, Placeholders.RtlQueryThreadPlaceholderCompatibilityMode());
    }

    /// <summary>
    /// **A content walk asks, and a name walk does not.** The walk's own rows
    /// cannot say which files are online, so a content search that did not ask
    /// would open every placeholder it met; and a name search that did would
    /// pay a second read of every folder for nothing.
    /// </summary>
    [WindowsFact]
    public async Task A_content_walk_asks_which_files_are_online_and_a_name_walk_does_not()
    {
        using var tree = new TempTree();

        var scope = tree.Dir("tree");
        tree.Write("tree/notes.txt", "remember the milk");

        var asked = 0;
        Placeholders.WhileExposed = dir => { if (dir == scope) Interlocked.Increment(ref asked); };

        try
        {
            await Walk(scope, "milk", contents: false);
            Assert.Equal(0, asked);

            await Walk(scope, "milk", contents: true);
            Assert.Equal(1, asked);
        }
        finally
        {
            Placeholders.WhileExposed = null;
        }
    }

    /// <summary>
    /// The rule itself, against a real file that DOES hold the text — so false
    /// can only mean it was not read — and the refusal is counted.
    /// </summary>
    [WindowsFact]
    public void A_file_held_online_is_refused_and_counted()
    {
        using var tree = new TempTree();

        var path = tree.Write("synced.txt", "remember the milk");
        var entry = new FileEntry("synced.txt", path, new FileInfo(path).Length,
                                  DateTimeOffset.UnixEpoch, EntryFlags.None);
        var skipped = new ContentSkips();
        var query = new SearchQuery { Text = "milk", MatchContent = true, Skipped = skipped };

        Assert.True(WindowsSearchProvider.Contains(entry, heldOnline: false, query, CancellationToken.None));
        Assert.Equal(0, skipped.Online);

        Assert.False(WindowsSearchProvider.Contains(entry, heldOnline: true, query, CancellationToken.None));
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

        Assert.False(WindowsSearchProvider.Contains(link, heldOnline: false, query, CancellationToken.None));
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
