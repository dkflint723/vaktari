using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Recursive sizes: what a folder comes to, what each child of it comes to,
/// and what could not be read on the way.
///
/// **A total that skips a folder silently is a figure that looks exact and is
/// not.** Every walk here steps over a folder it cannot open, because one
/// denied folder must cost that folder rather than the measurement; counting
/// them is what lets a listing say so.
/// </summary>
public sealed class SpaceUsageTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-usage").FullName;

    public void Dispose()
    {
        // Only this class's own folder — and a folder one test below left
        // unreadable is opened again first, or the delete cannot remove it.
        if (OperatingSystem.IsLinux()) Reopen();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    /// <summary>
    /// Its own method, attributed and called behind the matching
    /// <see cref="OperatingSystem"/> check: the platform analyser reads the
    /// attribute, not a <c>[PosixFact]</c>'s runtime skip, and the Windows
    /// build never compiles this far enough to say so.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void Reopen()
    {
        // IgnoreInaccessible, because the default enumeration recurses INTO the
        // folder this is here to reopen and throws out of Dispose when it
        // cannot. That it works today rests on the queue happening to yield a
        // folder before opening it, which is an ordering, not a guarantee.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var folder in Directory.EnumerateDirectories(_root, "*", options))
        {
            try
            {
                File.SetUnixFileMode(
                    folder,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception) { /* it may have been readable all along */ }
        }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());

        Directory.CreateDirectory(path);

        return path;
    }

    private string File_(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }

    private static UsageRow Row(UsageListing listing, string name)
        => Assert.Single(listing.Rows, r => Path.GetFileName(r.Path) == name);

    // ---- one folder --------------------------------------------------------

    [Fact]
    public void A_folder_comes_to_what_is_under_it()
    {
        File_("top.bin", 100);
        File_(Path.Combine("inner", "deep.bin"), 250);
        File_(Path.Combine("inner", "deeper", "deepest.bin"), 4);

        var usage = SpaceUsage.Measure(_root, progress: null, CancellationToken.None);

        Assert.Equal(354, usage.Bytes);
        Assert.Equal(3, usage.Files);
        Assert.Equal(2, usage.Folders);
        Assert.Equal(0, usage.Unreadable);
    }

    [Fact]
    public void An_empty_folder_comes_to_nothing_rather_than_failing()
    {
        var usage = SpaceUsage.Measure(Dir("empty"), progress: null, CancellationToken.None);

        Assert.Equal(new Usage(0, 0, 0, 0), usage);
    }

    /// <summary>
    /// **The rule the walk exists for.** A folder holding a link to somewhere
    /// large is its own size, not that of what the link points at — and a link
    /// to an ancestor would never finish being measured.
    /// </summary>
    [PosixFact]
    public void A_link_is_counted_where_it_stands_and_never_followed()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-usage-outside").FullName;

        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(outside, "huge.raw"), new byte[9_000]);

            File_("own.bin", 10);
            Directory.CreateSymbolicLink(Path.Combine(_root, "shortcut"), outside);

            var usage = SpaceUsage.Measure(_root, progress: null, CancellationToken.None);

            Assert.Equal(10, usage.Bytes);

            // A link is one entry, counted as a file rather than a folder,
            // which is how SafeWalk reports it. Both platform MeasureAsync
            // walks count the same link as a folder instead.
            Assert.Equal(2, usage.Files);
            Assert.Equal(0, usage.Folders);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (Exception) { /* not worth failing over */ }
        }
    }

    /// <summary>
    /// **The root handed in is followed, unlike every link under it.** The walk
    /// pushes its root without testing it, so asking what a link comes to asks
    /// about the folder it opens — the same decision <c>Archives</c> states for
    /// the row a person picked. Pinned because the rule above reads as though
    /// it covered this one too.
    /// </summary>
    [PosixFact]
    public void A_link_handed_in_as_the_root_is_measured_through()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-usage-outside").FullName;

        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(outside, "huge.raw"), new byte[9_000]);

            var shortcut = Path.Combine(_root, "shortcut");

            Directory.CreateSymbolicLink(shortcut, outside);

            var usage = SpaceUsage.Measure(shortcut, progress: null, CancellationToken.None);

            Assert.Equal(9_000, usage.Bytes);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (Exception) { /* not worth failing over */ }
        }
    }

    /// <summary>A folder that cannot be opened is stepped over, and said.</summary>
    [PosixFact, SupportedOSPlatform("linux")]
    public void A_folder_it_could_not_read_is_counted_and_said()
    {
        // Root reads a mode-000 directory anyway, so the premise does not hold
        // there and the test would fail on something it cannot control. CI runs
        // the Core suite as an ordinary user; a container or a WSL distro
        // without a default user does not.
        if (Environment.IsPrivilegedProcess) return;

        File_("seen.bin", 7);

        var closed = Dir("closed");
        System.IO.File.WriteAllBytes(Path.Combine(closed, "unseen.bin"), new byte[5_000]);
        System.IO.File.SetUnixFileMode(closed, UnixFileMode.None);

        var usage = SpaceUsage.Measure(_root, progress: null, CancellationToken.None);

        Assert.Equal(7, usage.Bytes);
        Assert.Equal(1, usage.Unreadable);
    }

    /// <summary>
    /// Progress arrives while the walk runs, and the last of it is the total.
    ///
    /// The sink here is synchronous, so the order below is the order the walk
    /// reported in. The application hands in a <c>Progress&lt;SizeProgress&gt;</c>,
    /// which posts each report to a captured context; what arrives after that
    /// boundary is the context's business, not this code's.
    /// </summary>
    [Fact]
    public void The_progress_climbs_and_ends_at_the_total()
    {
        for (var i = 0; i < 600; i++) File_(Path.Combine("many", $"f{i}.bin"), 1);

        var seen = new List<SizeProgress>();

        var usage = SpaceUsage.Measure(
            _root, new Progress(seen.Add), CancellationToken.None);

        // **More than one report, or it is not progress.** A single report at
        // the end is exactly what no throttle at all produces, and the
        // assertion below cannot tell the two apart: measured, with the
        // throttle set past any real folder, this test stayed green.
        Assert.True(seen.Count > 1, $"only {seen.Count} report(s): nothing arrived while the walk ran");
        Assert.Equal(usage.Counted, seen[^1]);

        // GUARD, not a test. `bytes` is a running sum of lengths, reported in
        // place, so this sequence cannot fall however the counting is broken.
        // It is here to catch a report built from something other than the
        // running total.
        Assert.True(seen.Select(s => s.Bytes).SequenceEqual(seen.Select(s => s.Bytes).Order()),
                    "a report went backwards, so it was not one climbing total");
    }

    /// <summary>
    /// **The end of the walk is reported once, not twice.** A folder whose
    /// entries land exactly on the throttle has already been reported in full
    /// by it, and a second identical report is a listener told twice that it is
    /// finished.
    /// </summary>
    [Fact]
    public void A_folder_that_lands_on_the_throttle_is_not_reported_twice()
    {
        for (var i = 0; i < 256; i++) File_($"f{i}.bin", 1);

        var seen = new List<SizeProgress>();

        var usage = SpaceUsage.Measure(_root, new Progress(seen.Add), CancellationToken.None);

        Assert.Equal(usage.Counted, Assert.Single(seen));
    }

    /// <summary>
    /// Cancelling throws rather than handing back a short total.
    ///
    /// **The check is the walk's, not a second one here.** A check inside this
    /// loop was removed and this test stayed green — measured — because
    /// <see cref="SafeWalk.Descend"/> already checks before every entry it
    /// yields. <see cref="SpaceUsage.Underneath"/> is the one that needs its
    /// own, and has its own tests below.
    /// </summary>
    [Fact]
    public void Cancelling_stops_it()
    {
        for (var i = 0; i < 500; i++) File_(Path.Combine("many", $"f{i}.bin"), 1);

        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SpaceUsage.Measure(_root, progress: null, cancelled.Token));
    }

    // ---- what is using the space here --------------------------------------

    [Fact]
    public void Each_child_carries_itself_and_everything_underneath_it()
    {
        File_("loose.bin", 3);
        File_(Path.Combine("big", "a.bin"), 1_000);
        File_(Path.Combine("big", "deeper", "b.bin"), 500);
        Dir("empty");

        var listing = SpaceUsage.Underneath(_root, progress: null, CancellationToken.None);

        Assert.Equal(3, Row(listing, "loose.bin").Usage.Bytes);
        Assert.False(Row(listing, "loose.bin").IsDirectory);

        var big = Row(listing, "big");

        Assert.True(big.IsDirectory);
        Assert.Equal(1_500, big.Usage.Bytes);
        Assert.Equal(2, big.Usage.Files);

        // Itself and the one under it. A row that left itself out made the
        // rows of a listing add up to fewer folders than the same folder's
        // own total.
        Assert.Equal(2, big.Usage.Folders);

        Assert.Equal(1, Row(listing, "empty").Usage.Folders);
        Assert.Equal(0, Row(listing, "empty").Usage.Bytes);
    }

    /// <summary>
    /// **The rows and the total are one measurement, not two.** A footer
    /// summing the rows and a progress line reading the total sit in the same
    /// pane; when a folder row left itself out of its own count they disagreed
    /// about how many folders a tree held.
    /// </summary>
    [Fact]
    public void The_rows_add_up_to_what_the_folder_comes_to()
    {
        File_("loose.bin", 3);
        File_(Path.Combine("big", "a.bin"), 1_000);
        File_(Path.Combine("big", "deeper", "b.bin"), 500);
        Dir("empty");

        var listing = SpaceUsage.Underneath(_root, progress: null, CancellationToken.None);

        Assert.Equal(SpaceUsage.Measure(_root, progress: null, CancellationToken.None), listing.Total);

        Assert.Equal(listing.Total.Bytes, listing.Rows.Sum(r => r.Usage.Bytes));
        Assert.Equal(listing.Total.Files, listing.Rows.Sum(r => r.Usage.Files));
        Assert.Equal(listing.Total.Folders, listing.Rows.Sum(r => r.Usage.Folders));
    }

    /// <summary>
    /// The rows are the folder's own order, not biggest first: a pane sorts its
    /// own rows, and a listing that arrived sorted would fight the column the
    /// person clicked.
    ///
    /// **Twenty files, because two could not tell the difference.** With one
    /// small file and one large one this assertion passed even with the rows
    /// sorted biggest-first — measured — since the folder's own order happened
    /// to agree. The sizes rise and fall against the names as well, so neither
    /// direction of sort can match the order a folder lists them in by chance.
    /// </summary>
    [Fact]
    public void The_rows_are_not_sorted_for_the_caller()
    {
        for (var i = 0; i < 20; i++) File_($"f{i}.bin", 1 + (i * 7 % 20 * 37));

        var listing = SpaceUsage.Underneath(_root, progress: null, CancellationToken.None);

        Assert.Equal(
            Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName),
            listing.Rows.Select(r => Path.GetFileName(r.Path)));
    }

    /// <summary>A child that is a link is its own row and is never walked into,
    /// so the folder beside it cannot be measured twice.</summary>
    [PosixFact]
    public void A_child_that_is_a_link_is_not_walked_into()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-usage-outside").FullName;

        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(outside, "huge.raw"), new byte[9_000]);
            Directory.CreateSymbolicLink(Path.Combine(_root, "shortcut"), outside);

            var row = Row(
                SpaceUsage.Underneath(_root, progress: null, CancellationToken.None), "shortcut");

            Assert.True(row.IsLink);
            Assert.Equal(0, row.Usage.Bytes);

            // Not a folder, though it opens one: the same answer SafeWalk gives
            // for it. A row saying IsDirectory beside a count of one file was
            // two descriptions of one entry that disagreed.
            Assert.False(row.IsDirectory);
            Assert.Equal(1, row.Usage.Files);
            Assert.Equal(0, row.Usage.Folders);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (Exception) { /* not worth failing over */ }
        }
    }

    /// <summary>
    /// **A link to a file is zero here, not the file's size.**
    /// <c>FileInfo.Length</c> follows a symbolic link, so without the link
    /// check a row for a link to a 9 KiB file came back at 9 KiB — measured —
    /// and a folder holding links to a media library would have been counted
    /// as though it held the library. A link to a *folder* cannot catch this:
    /// it is not a <c>FileInfo</c>, so it reads as zero either way.
    /// </summary>
    [PosixFact]
    public void A_child_that_links_to_a_file_is_zero_rather_than_the_files_size()
    {
        var outside = Directory.CreateTempSubdirectory("vaktari-usage-outside").FullName;

        try
        {
            var big = Path.Combine(outside, "huge.raw");

            System.IO.File.WriteAllBytes(big, new byte[9_000]);
            System.IO.File.CreateSymbolicLink(Path.Combine(_root, "shortcut.raw"), big);

            var row = Row(
                SpaceUsage.Underneath(_root, progress: null, CancellationToken.None), "shortcut.raw");

            Assert.True(row.IsLink);
            Assert.Equal(0, row.Usage.Bytes);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (Exception) { /* not worth failing over */ }
        }
    }

    /// <summary>
    /// **A row says whether the platform conceals it.** The attributes are read
    /// for the link check either way, so carrying the bit costs nothing — and a
    /// listing that had to ask again would stat every child a second time to
    /// decide whether to show it.
    /// </summary>
    [PosixFact]
    public void A_hidden_child_says_so()
    {
        File_(".secret", 5);
        File_("plain.bin", 5);

        var listing = SpaceUsage.Underneath(_root, progress: null, CancellationToken.None);

        Assert.True(Row(listing, ".secret").IsConcealed);
        Assert.False(Row(listing, "plain.bin").IsConcealed);
    }

    /// <summary>The same rule where hidden is an attribute rather than a leading
    /// dot.</summary>
    [WindowsFact]
    public void A_hidden_child_says_so_on_windows()
    {
        var secret = File_("secret.bin", 5);

        File_("plain.bin", 5);
        System.IO.File.SetAttributes(secret, FileAttributes.Hidden);

        var listing = SpaceUsage.Underneath(_root, progress: null, CancellationToken.None);

        Assert.True(Row(listing, "secret.bin").IsConcealed);
        Assert.False(Row(listing, "plain.bin").IsConcealed);
    }

    /// <summary>
    /// **No rows, and a total that says why.** An empty list alone reads as an
    /// empty folder, which is the one thing a listing must not show for a
    /// folder nobody was allowed to open.
    /// </summary>
    [Fact]
    public void A_folder_that_cannot_be_listed_gives_no_rows_and_says_so()
    {
        var missing = Path.Combine(_root, "not-there");

        var listing = SpaceUsage.Underneath(missing, progress: null, CancellationToken.None);

        Assert.Empty(listing.Rows);
        Assert.Equal(1, listing.Total.Unreadable);

        // The same answer the other entry point gives for the same path.
        Assert.Equal(SpaceUsage.Measure(missing, progress: null, CancellationToken.None), listing.Total);
    }

    /// <summary>
    /// Cancelling part-way through stops the walk across the children.
    ///
    /// **Cancelled before the call, this proved nothing.** The check at the top
    /// of the listing threw before the loop was reached, so discarding the
    /// check inside the loop left the test green — measured. The token is
    /// cancelled from the first child's own progress report instead, which is
    /// where a person pressing Escape during a long measurement puts it: after
    /// the listing began. Nothing else in that loop would stop —
    /// <c>EnumerateFileSystemInfos</c> never looks at the token, and a folder
    /// of plain files never reaches <see cref="SpaceUsage.Measure"/>, whose
    /// walk has a check of its own.
    /// </summary>
    [Fact]
    public void Cancelling_part_way_stops_the_walk_across_the_children()
    {
        for (var i = 0; i < 20; i++) File_($"f{i}.bin", 1);

        using var cancelled = new CancellationTokenSource();

        var stopAfterTheFirst = new Progress(_ => cancelled.Cancel());

        Assert.Throws<OperationCanceledException>(
            () => SpaceUsage.Underneath(_root, stopAfterTheFirst, cancelled.Token));
    }

    /// <summary>
    /// **A folder with nothing in it must stop too.** With the only check
    /// inside the loop, a cancelled call on an empty folder ran to the end and
    /// handed back a listing, where the same token threw everywhere else.
    /// </summary>
    [Fact]
    public void Cancelling_stops_it_even_where_there_is_nothing_to_walk()
    {
        var empty = Dir("empty");

        using var cancelled = new CancellationTokenSource();

        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SpaceUsage.Underneath(empty, progress: null, cancelled.Token));
    }

    /// <summary>
    /// **A listener is told when an empty folder is finished.** Every report in
    /// the loop belongs to a child, so a folder with no children reported
    /// nothing at all and a pane that had switched to "measuring…" stayed
    /// there.
    /// </summary>
    [Fact]
    public void An_empty_folder_still_reports_that_it_is_finished()
    {
        var seen = new List<SizeProgress>();

        var listing = SpaceUsage.Underneath(Dir("empty"), new Progress(seen.Add), CancellationToken.None);

        Assert.Equal(listing.Total.Counted, Assert.Single(seen));
    }

    /// <summary>Keeps every report, so a test can say what arrived and in what
    /// order.</summary>
    private sealed class Progress(Action<SizeProgress> onReport) : IProgress<SizeProgress>
    {
        public void Report(SizeProgress value) => onReport(value);
    }
}
