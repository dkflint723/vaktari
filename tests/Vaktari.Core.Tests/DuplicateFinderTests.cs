using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Files with identical contents, under one folder.
///
/// **Same bytes, and nothing else.** Every test here builds files whose names
/// and timestamps disagree with their contents, in both directions, because
/// those are the two things a duplicate is NOT about — and the comparison
/// feature's own <c>FileSameness</c>, which judges size and a two-second
/// tolerance, would get several of them wrong.
/// </summary>
public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-duplicates").FullName;

    public void Dispose()
    {
        // Only this class's own folder — and a file one test below left
        // unreadable is opened again first, or the delete cannot remove it.
        if (OperatingSystem.IsLinux()) Reopen();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    /// <summary>Its own method behind the matching check, which is the shape
    /// the platform analyser reads — a <c>[PosixFact]</c>'s runtime skip is
    /// invisible to it.</summary>
    [SupportedOSPlatform("linux")]
    private void Reopen()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var file in Directory.EnumerateFiles(_root, "*", options))
        {
            try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception) { /* it may have been readable all along */ }
        }
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);

        return path;
    }

    /// <summary>A file of a given size whose bytes are its own, so two of them
    /// share a length and nothing else.</summary>
    private string Fill(string relative, int bytes, byte with)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var buffer = new byte[bytes];

        Array.Fill(buffer, with);
        File.WriteAllBytes(path, buffer);

        return path;
    }

    private DuplicateReport Find()
        => DuplicateFinder.Find(_root, progress: null, CancellationToken.None);

    private static IReadOnlyList<string> Names(DuplicateSet set)
        => set.Paths.Select(Path.GetFileName).Order().ToList()!;

    // ---- what a duplicate is -------------------------------------------------

    /// <summary>
    /// **Different names, different dates, same bytes.** The names disagree and
    /// the timestamps are set a year apart, because neither has anything to do
    /// with it.
    /// </summary>
    [Fact]
    public void Two_files_of_one_content_are_a_set()
    {
        var first = Write("invoice.txt", "the same thing");
        var second = Write(Path.Combine("elsewhere", "copy-of-something.dat"), "the same thing");

        File.SetLastWriteTimeUtc(first, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var set = Assert.Single(Find().Sets);

        Assert.Equal(["copy-of-something.dat", "invoice.txt"], Names(set));
        Assert.Equal(14, set.Length);
    }

    [Fact]
    public void Three_of_one_content_are_one_set_of_three()
    {
        Write("a.txt", "again and again");
        Write("b.txt", "again and again");
        Write("c.txt", "again and again");

        Assert.Equal(3, Assert.Single(Find().Sets).Paths.Count);
    }

    /// <summary>
    /// **One name over two contents is not a duplicate.** The compare
    /// feature's rule — same size, within two seconds — would call these one
    /// file, which is why it is not the rule here.
    /// </summary>
    [Fact]
    public void Two_files_of_one_name_and_length_that_differ_are_not()
    {
        var here = Write(Path.Combine("one", "report.txt"), "aaaaaaaaaa");
        var there = Write(Path.Combine("two", "report.txt"), "bbbbbbbbbb");

        var when = new DateTime(2024, 3, 3, 12, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(here, when);
        File.SetLastWriteTimeUtc(there, when);

        Assert.Empty(Find().Sets);
    }

    /// <summary>A length nothing else shares is never opened, and never a
    /// set.</summary>
    [Fact]
    public void A_file_of_its_own_length_is_left_alone()
    {
        Write("alone.txt", "unique length here");
        Write("other.txt", "a different length entirely, by some way");

        Assert.Empty(Find().Sets);
    }

    /// <summary>
    /// **Files that begin alike and end differently are separate sets.** The
    /// head block puts them in one bucket, and only the byte-for-byte
    /// confirmation afterwards takes them apart again.
    /// </summary>
    [Fact]
    public void Files_that_share_a_beginning_are_still_told_apart()
    {
        var shared = new string('x', 70_000);

        Write("one.bin", shared + "AAAA");
        Write("two.bin", shared + "AAAA");
        Write("three.bin", shared + "BBBB");
        Write("four.bin", shared + "BBBB");

        var sets = Find().Sets.OrderBy(s => Names(s)[0], StringComparer.Ordinal).ToList();

        Assert.Equal(2, sets.Count);
        Assert.Equal(["one.bin", "two.bin"], Names(sets[1]));
        Assert.Equal(["four.bin", "three.bin"], Names(sets[0]));
    }

    /// <summary>
    /// **A file left alone after the comparison is not a set of one.** Three
    /// files share a beginning and only two of them share an ending; the third
    /// is a copy of nothing, and reporting it would be reporting a duplicate
    /// that does not exist.
    /// </summary>
    [Fact]
    public void A_file_alone_after_the_comparison_is_not_reported()
    {
        var shared = new string('x', 70_000);

        Write("one.bin", shared + "AAAA");
        Write("two.bin", shared + "AAAA");
        Write("odd.bin", shared + "ZZZZ");

        Assert.Equal(["one.bin", "two.bin"], Names(Assert.Single(Find().Sets)));
    }

    /// <summary>
    /// **Empty files are left out.** Every one of them matches every other, so
    /// counting them turns the answer into a list of every placeholder on the
    /// disk rather than the copies somebody went looking for.
    /// </summary>
    [Fact]
    public void Empty_files_are_not_copies_of_each_other()
    {
        Write("nothing.txt", "");
        Write("also-nothing.txt", "");

        Assert.Empty(Find().Sets);
    }

    // ---- what it will not do -------------------------------------------------

    /// <summary>
    /// **A link is never a copy of what it points at.** Its length is zero as
    /// well, so the same rule that drops empty files drops it — and following
    /// one is how a scan offers somebody the original as a copy of itself.
    /// </summary>
    [PosixFact]
    public void A_link_is_not_a_copy_of_its_target()
    {
        var real = Write("real.txt", "the actual contents");

        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.txt"), real);

        Assert.Empty(Find().Sets);
    }

    /// <summary>A file it could not open is counted, not guessed into a
    /// set.</summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public void A_file_it_could_not_read_is_counted()
    {
        // Root reads a mode-000 file anyway, so the premise does not hold there
        // and the test would fail on something it cannot control.
        if (Environment.IsPrivilegedProcess) return;

        Write("readable.txt", "the same thing");
        Write("also-readable.txt", "the same thing");

        var closed = Write("closed.txt", "the same thing");

        File.SetUnixFileMode(closed, UnixFileMode.None);

        var report = Find();

        Assert.Equal(1, report.Unreadable);
        Assert.Equal(2, Assert.Single(report.Sets).Paths.Count);
    }

    // ---- running it ----------------------------------------------------------

    [Fact]
    public void Cancelling_stops_it()
    {
        for (var i = 0; i < 50; i++) Fill($"f{i}.bin", 4_096, 7);

        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DuplicateFinder.Find(_root, progress: null, cancelled.Token));
    }

    /// <summary>
    /// Cancelling part-way stops the reading.
    ///
    /// **Cancelled before the call, the test above proves nothing about the
    /// reading.** The walk's own check throws before a single file is opened,
    /// so discarding the check in the loop that reads left it green —
    /// measured. The token is cancelled from the first file's progress report
    /// instead, which is where somebody pressing Stop would put it: after the
    /// walk, during the part that takes the time.
    /// </summary>
    [Fact]
    public void Cancelling_part_way_stops_the_reading()
    {
        for (var i = 0; i < 20; i++) Fill($"f{i}.bin", 4_096, 7);

        using var cancelled = new CancellationTokenSource();

        var stopAfterTheFirst = new Progress(_ => cancelled.Cancel());

        Assert.Throws<OperationCanceledException>(
            () => DuplicateFinder.Find(_root, stopAfterTheFirst, cancelled.Token));
    }

    /// <summary>
    /// Progress counts the files it actually opens, not every file walked —
    /// **the walk is the cheap half**, and a count that climbed through
    /// thousands of files nothing would ever read would say nothing about how
    /// long this is going to take.
    /// </summary>
    [Fact]
    public void The_progress_counts_what_it_opens()
    {
        Write("a.txt", "shared contents");
        Write("b.txt", "shared contents");
        Write("alone.txt", "a length of its own entirely");

        var seen = new List<int>();

        DuplicateFinder.Find(_root, new Progress(seen.Add), CancellationToken.None);

        Assert.Equal([1, 2], seen);
    }

    private sealed class Progress(Action<int> onReport) : IProgress<int>
    {
        public void Report(int value) => onReport(value);
    }
}
