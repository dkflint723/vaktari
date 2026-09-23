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
        string? scope = null, CancellationToken ct = default, bool readsConcealed = true)
    {
        var query = new SearchQuery
        {
            Text = text,
            ScopePath = scope ?? _root,
            MatchContent = contents,
            CaseSensitive = caseSensitive,
            Skipped = skipped,
            ReadsConcealed = readsConcealed,
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

    /// <summary>
    /// A dotfile is not opened when hidden files are not shown: the pane drops
    /// its row after the walk has read it. Its name still answers either way.
    /// </summary>
    [Fact]
    public async Task A_hidden_file_is_read_only_when_hidden_files_are_shown()
    {
        NoBaloo();
        Write(".notes", "remember the milk");

        Assert.Equal([".notes"], await Search("milk", contents: true, readsConcealed: true));
        Assert.Empty(await Search("milk", contents: true, readsConcealed: false));
        Assert.Equal([".notes"], await Search("notes", contents: true, readsConcealed: false));
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
    /// does hang, the FIFO is opened on the way out, which is what lets the
    /// stuck open() return — read AND write, because a write-only open of a
    /// FIFO itself blocks until a reader arrives, and if the search stalled
    /// somewhere else there is no reader and the cleanup would hang the run
    /// instead. Opened both ways a FIFO never blocks on Linux (fifo(7)).
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
            using (new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite)) { }
        }

        Assert.True(finished, "a content search blocked opening a FIFO");
        Assert.Equal(["notes.txt"], await search);
    }

    // ---- where files are not opened ----------------------------------------

    private readonly Func<IEnumerable<string>>? _mounts = LinuxSearchProvider.MountLines;

    /// <summary>
    /// **A cloud or network mount the walk walked down into is not read, and is
    /// counted.** Reading through an rclone mount fetches every file from the
    /// cloud, which is what the Windows walk refuses to do to a sync client's
    /// placeholders. Stood in for through the mount-table seam, so this runs
    /// wherever the walk does.
    /// </summary>
    [Fact]
    public async Task A_cloud_mount_inside_the_search_is_not_read()
    {
        NoBaloo();
        var nas = Directory.CreateDirectory(Path.Combine(_root, "gdrive")).FullName;
        Write("gdrive/notes.txt", "remember the milk");
        Write("local.txt", "remember the milk");

        LinuxSearchProvider.MountLines = () => [$"gdrive: {nas} fuse.rclone rw 0 0"];

        try
        {
            var skipped = new ContentSkips();

            Assert.Equal(["local.txt"], await Search("milk", contents: true, skipped: skipped));
            Assert.Equal(1, skipped.Online);

            // And a search scoped TO the mount reads it: somebody went there.
            Assert.Equal(["notes.txt"], await Search("milk", contents: true, scope: nas));
        }
        finally
        {
            LinuxSearchProvider.MountLines = _mounts;
        }
    }

    /// <summary>
    /// **A kernel filesystem is never read, and not counted.** sysfs reports
    /// 4096 bytes for a file that holds one line, so the zero-length rule does
    /// not keep it out, and reading some of those files does something to the
    /// machine. Nobody searching for words is missing them.
    /// </summary>
    [Fact]
    public async Task A_kernel_filesystem_is_not_read()
    {
        NoBaloo();
        var sys = Directory.CreateDirectory(Path.Combine(_root, "sys")).FullName;
        Write("sys/mtu", "65536");

        LinuxSearchProvider.MountLines = () => [$"sysfs {sys} sysfs rw 0 0"];

        try
        {
            var skipped = new ContentSkips();

            Assert.Empty(await Search("65536", contents: true, skipped: skipped));
            Assert.Equal(0, skipped.Online);
        }
        finally
        {
            LinuxSearchProvider.MountLines = _mounts;
        }
    }

    /// <summary>
    /// The same against the real /sys, with the machine's own mount table:
    /// the loopback device's MTU is read here, by the test, and the search is
    /// asked for it.
    /// </summary>
    [PosixFact]
    public async Task The_real_sysfs_is_not_read()
    {
        NoBaloo();
        const string lo = "/sys/class/net/lo";

        Assert.True(File.Exists(lo + "/mtu"), "no loopback device, so this proves nothing");

        var mtu = File.ReadAllText(lo + "/mtu").Trim();

        Assert.DoesNotContain("mtu", await Search(mtu, contents: true, scope: lo));
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
    /// **When Baloo has nothing, the walk says it is walking, as it starts.**
    /// The band decided before Baloo answered that an index was answering; this
    /// is how it learns otherwise while the walk still runs.
    /// </summary>
    [PosixFact]
    public async Task A_walk_behind_an_index_that_had_nothing_says_so()
    {
        var mentions = Write("notes.txt", "remember the milk");

        var walked = 0;

        async Task Ask(string script)
        {
            LinuxSearchProvider.BalooOverride = () => script;

            var query = new SearchQuery
            {
                Text = "milk", ScopePath = _root, MatchContent = true, MaxResults = 50,
                WalkingInstead = () => walked++,
            };

            await foreach (var _ in new LinuxSearchProvider().SearchAsync(query, CancellationToken.None)) { }
        }

        await Ask(Baloo(mentions));
        Assert.Equal(0, walked);

        await Ask(Baloo());
        Assert.Equal(1, walked);
    }

    /// <summary>
    /// **An index that answered with nothing but contents has been built.**
    /// The walk behind Baloo runs when the index says nothing at all — which a
    /// switched-off indexer and a real "no matches" both do, alike — so the
    /// question has to be asked before the narrowing, or an index that did
    /// answer would be walked past as well. Here the walk WOULD find a file by
    /// name, and must not be reached.
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
