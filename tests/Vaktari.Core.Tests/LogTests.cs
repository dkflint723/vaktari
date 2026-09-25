using Vaktari.Core.Diagnostics;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The rolling log and the crash marker.
///
/// **A crash left nothing on disk.** These pin the four promises the log makes
/// in return for existing: it writes where it was pointed and nowhere before
/// that; it hides the directory half of a path unless told not to; it rolls at
/// a megabyte and keeps three; and a crash leaves a marker the next start can
/// take exactly once.
///
/// Serialised on the static log: two classes configuring it at once would
/// each write into the other's folder.
/// </summary>
[Collection("log")]
public sealed class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vaktari-log-" + Guid.NewGuid().ToString("N")[..8]);

    public LogTests() => Log.Configure(_dir, includePaths: false);

    public void Dispose()
    {
        Log.Reset();

        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Live => Path.Combine(_dir, Log.FileName);

    // ---- where it writes ---------------------------------------------------

    [Fact]
    public void A_line_lands_in_the_configured_directory()
    {
        Log.Warn("test", "something happened");

        Assert.True(File.Exists(Live));
        Assert.Contains("warn  test: something happened", File.ReadAllText(Live));
    }

    /// <summary>
    /// **Unconfigured means silent, not "the developer's state directory".**
    /// A test that forgot to configure it must not be able to write into the
    /// real log — and this is the arm every other suite in the repository
    /// relies on without knowing it.
    /// </summary>
    [Fact]
    public void Nothing_is_written_before_it_is_configured()
    {
        Log.Reset();

        Log.Warn("test", "into the void");

        Assert.False(File.Exists(Live));
        Assert.Equal("", Log.Tail(10));
    }

    // ---- what it hides -----------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\somebody\Documents\tax-2025.xlsx", "tax-2025.xlsx", @"C:\Users\somebody\Documents")]
    [InlineData("/home/somebody/Pictures/wedding.jpg", "wedding.jpg", "/home/somebody/Pictures")]
    [InlineData(@"\\nas\share\Photos\family.png", "family.png", @"\\nas\share\Photos")]
    // **A folder with a space in it was cut at the space**, and everything
    // after it was written out whole — so these name the part that leaked
    // rather than the whole directory, which was never there to find.
    [InlineData(@"C:\Users\John Smith\Documents\tax.xlsx", "tax.xlsx", "John Smith")]
    [InlineData(@"C:\Users\John Smith\Documents\tax.xlsx", "tax.xlsx", "Documents")]
    [InlineData("/home/jo/Tax Returns 2025/bank.pdf", "bank.pdf", "Tax Returns")]
    [InlineData(@"C:\Users\John Smith\OneDrive - Contoso\Documents\x.json", "x.json", "Contoso")]
    // **And at an apostrophe**, which Windows allows in an account name.
    [InlineData(@"C:\Users\O'Brien\Documents\x.txt", "x.txt", "Brien")]
    [InlineData(@"C:\Users\O'Brien\Documents\x.txt", "x.txt", "Documents")]
    [InlineData("/home/jo/Rock 'n' Roll/song.mp3", "song.mp3", "Roll")]
    [InlineData("/home/jo/Rock 'n' Roll/song.mp3", "song.mp3", "'n'")]
    public void A_path_keeps_its_leaf_and_loses_its_directory(string path, string leaf, string directory)
    {
        var redacted = Log.Redact("could not read " + path + " at all");

        Assert.Contains(leaf, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, redacted, StringComparison.Ordinal);

        // A hash rather than a blank, so two lines about one folder still
        // match up when somebody is reading the log.
        Assert.Matches("<[0-9a-f]{8}>", redacted);
    }

    [Fact]
    public void Two_paths_on_one_line_are_both_hidden()
    {
        var redacted = Log.Redact(@"moving C:\a\one.txt to D:\b\two.txt");

        Assert.DoesNotContain(@"C:\a", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\b", redacted, StringComparison.Ordinal);
        Assert.Contains("one.txt", redacted, StringComparison.Ordinal);
        Assert.Contains("two.txt", redacted, StringComparison.Ordinal);
    }

    /// <summary>Folders with spaces on both sides of the "to": each path keeps
    /// its own leaf, and neither folder survives.</summary>
    [Fact]
    public void Two_spaced_paths_on_one_line_are_both_hidden()
    {
        var redacted = Log.Redact(@"moving C:\My Files\one.txt to D:\Old Stuff\two.txt");

        Assert.DoesNotContain("My Files", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Stuff", redacted, StringComparison.Ordinal);
        Assert.Contains(@"\one.txt to <", redacted, StringComparison.Ordinal);
        Assert.EndsWith(@"\two.txt", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A quoted path must not run into the next one**, now that a folder may
    /// hold an apostrophe: .NET quotes the paths in its own messages, and
    /// "one.txt' to 'D:" would otherwise read as a folder of the second path,
    /// hashing the first path's leaf and the words between them away.
    /// </summary>
    [Fact]
    public void Two_quoted_paths_on_one_line_each_keep_their_leaf()
    {
        var redacted = Log.Redact(@"cannot move 'C:\a\one.txt' to 'D:\b\two.txt'");

        Assert.DoesNotContain(@"C:\a", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\b", redacted, StringComparison.Ordinal);
        Assert.Contains(@"\one.txt' to '<", redacted, StringComparison.Ordinal);
        Assert.EndsWith(@"\two.txt'", redacted, StringComparison.Ordinal);
    }
    /// <summary>A slash with a space after it is arithmetic, not a root: the
    /// pattern that lets a folder hold a space must not start matching at
    /// one.</summary>
    [Fact]
    public void A_slash_between_spaces_is_not_a_path()
    {
        const string line = "copied 3 / 5 files";

        Assert.Equal(line, Log.Redact(line));
    }

    [Fact]
    public void A_line_with_no_path_is_untouched()
    {
        const string line = "the shell answered E_FAIL after 2s";

        Assert.Equal(line, Log.Redact(line));
    }

    /// <summary>The same switch Quiet honours: on a machine where nobody else
    /// reads the log, whole paths are worth more than hidden ones.</summary>
    [Fact]
    public void Paths_stay_whole_when_asked()
    {
        Log.Configure(_dir, includePaths: true);

        const string line = @"could not read C:\Users\somebody\file.txt";

        Assert.Equal(line, Log.Redact(line));
    }

    // ---- how it rolls -----------------------------------------------------

    [Fact]
    public void It_rolls_at_a_megabyte_and_keeps_three()
    {
        var filler = new string('x', 1000);

        // Past the first roll, then past the second, then past a third: the
        // live file, .1 and .2 should exist and nothing older.
        for (var i = 0; i < 3 * (Log.RollAt / 1000) + 10; i++)
            Log.Warn("fill", filler);

        Assert.True(File.Exists(Live), "no live log");
        Assert.True(File.Exists(Path.Combine(_dir, "vaktari.1.log")), "no first rolled log");
        Assert.True(File.Exists(Path.Combine(_dir, "vaktari.2.log")), "no second rolled log");
        Assert.False(File.Exists(Path.Combine(_dir, "vaktari.3.log")), "kept more than it said");

        // And the live one has just started again rather than carrying on.
        Assert.True(new FileInfo(Live).Length < Log.RollAt, "the live log did not roll");
    }

    // ---- the crash marker -------------------------------------------------

    [Fact]
    public void A_fatal_leaves_a_marker_the_next_start_takes_once()
    {
        Log.Fatal("process", "System.NullReferenceException: at Somewhere\n   at Deeper");

        var marker = Log.TakeCrashMarker();

        Assert.NotNull(marker);
        Assert.Contains("System.NullReferenceException", marker, StringComparison.Ordinal);

        // Once: the second start of the day must not say it again.
        Assert.Null(Log.TakeCrashMarker());
    }

    [Fact]
    public void A_clean_run_leaves_no_marker()
    {
        Log.Warn("test", "an ordinary line");

        Assert.Null(Log.TakeCrashMarker());
    }

    // ---- the tail ---------------------------------------------------------

    [Fact]
    public void The_tail_is_the_last_lines_in_order()
    {
        for (var i = 1; i <= 5; i++) Log.Warn("t", $"line {i}");

        var tail = Log.Tail(2).Split('\n');

        Assert.Equal(2, tail.Length);
        Assert.EndsWith("line 4", tail[0], StringComparison.Ordinal);
        Assert.EndsWith("line 5", tail[1], StringComparison.Ordinal);
    }
}
