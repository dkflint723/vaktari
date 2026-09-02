using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Building the "Open with" menu.
///
/// **Every right-click spawned an xdg-mime process**, even on a file whose
/// extension the desktop's own glob database already answers. The row icons
/// have read that database since they were written; this menu never did.
///
/// **And the menu disappeared, intermittently.** The sniff shared the row
/// icons' four-permit budget and asked for a permit without waiting, so
/// whenever a listing was busy loading icons it got nothing — and nothing meant
/// an empty option list, which meant the submenu was not drawn at all. A menu
/// that is sometimes missing is worse than one that is always slow.
///
/// The interactive callers have a budget of their own now. Waiting on the same
/// semaphore would not have fixed it: permits are handed out unordered, so
/// behind a queue of background spawns a bounded wait times out just as often.
/// </summary>
public sealed class OpenWithMenuTests : IDisposable
{
    private readonly Func<string, bool, string>? _before = DesktopEntries.SniffOverride;

    public void Dispose()
    {
        DesktopEntries.SniffOverride = _before;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The glob database answers an ordinary extension, so nothing is spawned:
    /// reading a text file the desktop installed is free, and spawning is not.
    ///
    /// Reads the desktop's own glob database, so it runs where there is one.
    /// Asserted rather than skipped when the answer is missing: a machine with
    /// no shared-mime-info should fail this loudly, not pass it quietly.
    /// </summary>
    [PosixTheory]
    [InlineData("/tmp/notes.txt")]
    [InlineData("/tmp/photo.png")]
    [InlineData("/tmp/report.pdf")]
    [InlineData("/tmp/archive.tar.gz")]
    public void A_named_file_never_reaches_the_sniff(string path)
    {
        Assert.False(
            string.IsNullOrEmpty(SharedMimeInfo.ForPath(path)),
            $"the glob database has no answer for {path}; is shared-mime-info installed?");

        var sniffed = 0;

        DesktopEntries.SniffOverride = (_, _) => { sniffed++; return "text/plain"; };

        DesktopEntries.ForFile(path);

        Assert.Equal(0, sniffed);
    }

    /// <summary>
    /// What globs cannot answer still reaches the sniff — a file with no
    /// extension is exactly what the sniff is for.
    /// </summary>
    [Fact]
    public void A_file_with_no_extension_still_asks()
    {
        var sniffed = 0;

        DesktopEntries.SniffOverride = (_, _) => { sniffed++; return "text/plain"; };

        DesktopEntries.ForFile("/tmp/README");

        Assert.Equal(1, sniffed);
    }

    /// <summary>
    /// **And it waits, because somebody is looking at the menu.** Asking
    /// without waiting is what made the submenu vanish whenever a listing was
    /// loading icons.
    /// </summary>
    [Fact]
    public void The_menu_asks_against_the_interactive_budget()
    {
        bool? waited = null;

        DesktopEntries.SniffOverride = (_, w) => { waited = w; return "text/plain"; };

        DesktopEntries.ForFile("/tmp/README");

        Assert.True(waited, "the menu asked the background budget and can come back empty");
    }

    /// <summary>
    /// The row icons must NOT wait: a thread blocked there is a pool thread out
    /// of circulation, which is the failure that budget exists to prevent.
    /// </summary>
    [Fact]
    public void A_row_icon_still_never_waits()
    {
        bool? waited = null;

        DesktopEntries.SniffOverride = (_, w) => { waited = w; return "text/plain"; };

        DesktopEntries.QueryMimeType("/tmp/README");

        Assert.False(waited, "a row icon waits, and parks a pool thread doing it");
    }

    /// <summary>
    /// The two budgets are separate objects. Sharing one is the whole bug, and
    /// merely waiting on the shared one would not have fixed it.
    /// </summary>
    [Fact]
    public void The_interactive_budget_is_not_the_background_one()
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var source = File.ReadAllText(
            Path.Combine(here!, "src", "Vaktari.Linux", "DesktopEntries.cs"));

        Assert.Contains("SemaphoreSlim AskedFor = new(2, 2);", source);
        Assert.Contains("var budget = waiting ? AskedFor : Sniffs;", source);

        // And the WAIT follows the same flag. The override above short-circuits
        // before this line, so no behaviour test can see it — and a background
        // caller that blocks here parks a pool thread, which is the failure the
        // original budget exists to prevent.
        Assert.Contains("budget.Wait(waiting ? InteractiveWaitMs : 0)", source);
    }
}
