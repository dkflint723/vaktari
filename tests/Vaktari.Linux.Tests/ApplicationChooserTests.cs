using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The list behind "Choose another app…".
///
/// **This desktop had no such row at all.** The launcher interface offered a
/// chooser only as "the platform shows its own dialog", which Windows does and
/// nothing here does — xdg-open launches the default and has no ask — so a file
/// whose type no application claims produced an "Open with" submenu with
/// nothing in it and no way out of it.
///
/// Scanning is pure and takes its directories, because every state worth
/// pinning is a state of the DATABASE — a hidden entry, a console entry with no
/// terminal to run it in, the same id in two directories — and none of them can
/// be arranged by asking a real machine politely. It is also why these run on
/// the Windows agent this was written on, which has no desktop database at all.
/// </summary>
public sealed class ApplicationChooserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-apps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) { Vaktari.Core.Quiet.Swallowed("test-teardown", e); }

        GC.SuppressFinalize(this);
    }

    /// <summary>One .desktop file, written where the scan will find it.</summary>
    private string Entry(string directory, string name, params string[] lines)
    {
        var folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, name))!);

        var file = Path.Combine(folder, name);
        File.WriteAllLines(file, ["[Desktop Entry]", .. lines]);

        return folder;
    }

    private static readonly TerminalOption ATerminal =
        new("xterm", "xterm", "/usr/bin/xterm", []);

    [Fact]
    public void An_installed_application_is_offered_by_name_and_id()
    {
        var dir = Entry("share", "writer.desktop", "Name=Writer", "Exec=writer %f");

        var found = DesktopEntries.Scan([dir], [ATerminal]);

        var one = Assert.Single(found);

        Assert.Equal("Writer", one.Name);
        Assert.Equal("writer.desktop", one.Id);
    }

    /// <summary>
    /// **The whole point of the chooser.** ForFile answers "what claims this
    /// type", and the file this window exists for is the one nothing claims —
    /// so a list narrowed the same way would offer the same nothing. This entry
    /// declares no MimeType at all and is still in the list.
    /// </summary>
    [Fact]
    public void An_application_that_claims_no_type_is_still_offered()
    {
        var dir = Entry("share", "hexedit.desktop", "Name=Hex editor", "Exec=hexedit %f");

        Assert.Contains(DesktopEntries.Scan([dir], [ATerminal]),
                        o => o.Id == "hexedit.desktop");
    }

    /// <summary>
    /// NoDisplay and Hidden are the database's two ways of saying "not in a
    /// menu". A settings panel or a MIME helper listed among the applications
    /// is a row that opens something nobody asked for.
    /// </summary>
    [Theory]
    [InlineData("NoDisplay=true")]
    [InlineData("Hidden=true")]
    public void An_entry_the_database_hides_is_not_offered(string flag)
    {
        var dir = Entry("share", "helper.desktop", "Name=Helper", "Exec=helper %f", flag);

        Assert.Empty(DesktopEntries.Scan([dir], [ATerminal]));
    }

    /// <summary>
    /// **An entry with no Exec= would have opened the WRONG application.**
    /// Launch refuses it and OpenWith falls back to the default handler, so
    /// such a row does not do nothing — it silently opens whatever the type
    /// already opens with, which reads as the chooser having ignored the
    /// choice.
    /// </summary>
    [Fact]
    public void An_entry_with_no_command_is_not_offered()
    {
        var dir = Entry("share", "link.desktop", "Name=Somewhere", "URL=https://example.invalid");

        Assert.Empty(DesktopEntries.Scan([dir], [ATerminal]));
    }

    /// <summary>
    /// The same rule the per-type list keeps: vim, nano and htop all ship
    /// Terminal=true entries, and spawning one on a machine with no terminal
    /// emulator starts a process with no tty that exits at once or lingers
    /// invisibly.
    /// </summary>
    [Fact]
    public void A_console_entry_is_offered_only_where_something_can_run_it()
    {
        var dir = Entry("share", "vim.desktop", "Name=Vim", "Exec=vim %f", "Terminal=true");

        Assert.Empty(DesktopEntries.Scan([dir], []));
        Assert.Single(DesktopEntries.Scan([dir], [ATerminal]));
    }

    /// <summary>
    /// **The id is the path, not the file name.** An entry under
    /// applications/kde/ is "kde-konsole.desktop" to everything that reads the
    /// database, and that spelling is what FindDesktopFile undoes to open the
    /// file again — so a row reported as "konsole.desktop" is one that could
    /// never be launched.
    /// </summary>
    [Fact]
    public void A_nested_entry_is_offered_under_the_id_the_database_uses()
    {
        var dir = Entry("share", Path.Combine("kde", "konsole.desktop"),
                        "Name=Konsole", "Exec=konsole");

        Assert.Equal("kde-konsole.desktop", Assert.Single(DesktopEntries.Scan([dir], [ATerminal])).Id);
    }

    /// <summary>
    /// A ~/.local/share/applications entry with the same id as a system one is
    /// an override of it, and ApplicationDirs yields the user's copy first.
    /// Taking the system name would show the one the person replaced.
    /// </summary>
    [Fact]
    public void The_user_copy_of_an_id_hides_the_system_one()
    {
        var mine = Entry("home", "editor.desktop", "Name=My editor", "Exec=mine %f");
        var theirs = Entry("system", "editor.desktop", "Name=Stock editor", "Exec=stock %f");

        Assert.Equal("My editor", Assert.Single(DesktopEntries.Scan([mine, theirs], [ATerminal])).Name);
    }

    /// <summary>
    /// By name, because the name is what the window shows and a list of several
    /// hundred is read alphabetically or not at all.
    ///
    /// The ids run the other way, so an order taken from them rather than from
    /// the names is a different sequence and fails here. And the names differ
    /// in case, so an ordinal comparison — which sorts every capital ahead of
    /// every lower-case letter — is a different sequence again.
    /// </summary>
    [Fact]
    public void The_list_is_ordered_by_name()
    {
        var dir = Entry("share", "a.desktop", "Name=Zebra", "Exec=z %f");
        Entry("share", "b.desktop", "Name=apple", "Exec=a %f");
        Entry("share", "c.desktop", "Name=Mango", "Exec=m %f");

        Assert.Equal(["apple", "Mango", "Zebra"],
                     DesktopEntries.Scan([dir], [ATerminal]).Select(o => o.Name));
    }

    /// <summary>
    /// A directory that is not there is skipped rather than thrown from. Two of
    /// the six ApplicationDirs are flatpak's, which exist on no machine without
    /// flatpak installed.
    /// </summary>
    [Fact]
    public void A_directory_that_does_not_exist_costs_nothing()
    {
        var dir = Entry("share", "writer.desktop", "Name=Writer", "Exec=writer %f");

        Assert.Single(DesktopEntries.Scan([Path.Combine(_root, "no-such-place"), dir], [ATerminal]));
    }

    /// <summary>
    /// The launcher's own answer, both ways round. The menu row appears on a
    /// machine with applications and must vanish on one with none — a bare
    /// container, or a desktop database nothing can read — where a row
    /// promising a list would open an empty window.
    /// </summary>
    [Fact]
    public void The_chooser_is_offered_only_where_there_is_something_to_choose()
    {
        var launcher = new LinuxLauncher();

        launcher.UseApplications([]);
        Assert.False(launcher.CanChooseApplication);

        launcher.UseApplications([new LaunchOption("Writer", "writer.desktop")]);
        Assert.True(launcher.CanChooseApplication);
    }

    /// <summary>
    /// And the list the pane is handed is that one. A launcher that answered
    /// CanChooseApplication and then produced nothing would draw the row and
    /// open an empty window behind it.
    /// </summary>
    [Fact]
    public void The_launcher_hands_over_the_applications_it_scanned()
    {
        var launcher = new LinuxLauncher();
        var writer = new LaunchOption("Writer", "writer.desktop");

        launcher.UseApplications([writer]);

        Assert.Equal([writer], launcher.AllApplications);
    }

    /// <summary>
    /// **An id that cannot be found again is worse than no row at all.**
    /// FindDesktopFile resolves a nested entry by turning EVERY dash into a
    /// separator, so an entry whose own file name has one is looked for at a
    /// path that does not exist — measured: kde/google-chrome.desktop scans as
    /// kde-google-chrome.desktop and resolves to kde/google/chrome. Launch
    /// refuses, and OpenWith answers a refusal by opening the DEFAULT
    /// application, so the row reads as the chooser ignoring the choice.
    ///
    /// The two controls are the shapes that DO come back: a nested entry with
    /// no dash of its own, and a top-level one whose name has a dash, which
    /// FindDesktopFile matches directly before it tries the dashed spelling.
    /// </summary>
    [Fact]
    public void An_entry_whose_id_cannot_be_looked_up_again_is_not_offered()
    {
        var dir = Entry("share", Path.Combine("kde", "google-chrome.desktop"),
                        "Name=Chrome", "Exec=chrome %f");

        Entry("share", Path.Combine("kde", "konsole.desktop"), "Name=Konsole", "Exec=konsole");
        Entry("share", "plain-name.desktop", "Name=Plain", "Exec=plain %f");

        Assert.Equal(["kde-konsole.desktop", "plain-name.desktop"],
                     DesktopEntries.Scan([dir], [ATerminal]).Select(o => o.Id));
    }

    /// <summary>
    /// Two applications with the same name are ordered by their ids, so the
    /// list does not depend on the order the walk happened to return.
    ///
    /// The nested entry is walked LAST — top-level files come out before the
    /// walk descends — and sorts FIRST by id, so an order left to the walk is a
    /// different sequence and fails here.
    /// </summary>
    [Fact]
    public void Two_applications_with_one_name_are_ordered_by_id()
    {
        var dir = Entry("share", "zzz.desktop", "Name=Text editor", "Exec=z %f");
        Entry("share", Path.Combine("aaa", "a.desktop"), "Name=Text editor", "Exec=a %f");

        Assert.Equal(["aaa-a.desktop", "zzz.desktop"],
                     DesktopEntries.Scan([dir], [ATerminal]).Select(o => o.Id));
    }

    /// <summary>
    /// **The scan runs once however many ask at once.** AllApplications is read
    /// from a Task.Run started per selection change, so arrowing down a listing
    /// asks again before the first answer exists — and an unlocked ??= is a
    /// cache only for whoever arrives after it has been filled. Measured before
    /// the lock, with a counter inside Scan: sixteen simultaneous readers ran
    /// sixteen full walks of the applications directories.
    ///
    /// The stand-in takes its time on purpose. The race is real but narrow, and
    /// a scan that returns instantly is one every reader may well miss.
    /// </summary>
    [Fact]
    public async Task The_scan_runs_once_however_many_readers_arrive_together()
    {
        var launcher = new LinuxLauncher();
        var scans = 0;

        launcher.UseApplications(() =>
        {
            Interlocked.Increment(ref scans);
            Thread.Sleep(30);
            return [new LaunchOption("Writer", "writer.desktop")];
        });

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => launcher.AllApplications)));

        Assert.Equal(1, scans);
    }
}
