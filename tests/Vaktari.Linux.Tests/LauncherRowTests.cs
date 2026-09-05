using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A .desktop file listed as a ROW, rather than offered as a menu entry.
///
/// **A launcher listed as "org.kde.konsole.desktop" beside a generic grey
/// page.** Everything else in DesktopEntries answers "what can open this?" and
/// builds its rows out of the database by id; nothing answered "what is THIS
/// file". So the two keys that say what a launcher is called and what it looks
/// like — Name, which was already read for the menu, and Icon, which was read
/// by nothing at all — never reached the listing. A folder of launchers is a
/// real folder people open: the desktop itself, ~/.local/share/applications,
/// and /usr/share/applications where the KDE applications all file their
/// entries under reverse-DNS names.
/// </summary>
public sealed class LauncherRowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-launcher-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly string? _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly Func<string, string?>? _launcherName = FileKind.LauncherName;
    private readonly Func<string, bool>? _executable = DesktopEntries.ExecutableOverride;
    private readonly Func<string, bool, string>? _sniff = DesktopEntries.SniffOverride;
    private readonly int _maxLaunchers = DesktopEntries.MaxLaunchers;

    /// <summary>
    /// XDG_DATA_HOME points at the temp root, so <c>&lt;root&gt;/applications</c>
    /// is a real application directory as far as DesktopEntries is concerned —
    /// which is the fact the trust rule turns on, and the one no agent has by
    /// default.
    /// </summary>
    public LauncherRowTests()
    {
        Directory.CreateDirectory(Installed);
        Directory.CreateDirectory(Downloads);

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHome);
        FileKind.LauncherName = _launcherName;
        DesktopEntries.ExecutableOverride = _executable;
        DesktopEntries.SniffOverride = _sniff;
        DesktopEntries.MaxLaunchers = _maxLaunchers;

        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Installed => Path.Combine(_root, "applications");
    private string Downloads => Path.Combine(_root, "downloads");

    /// <summary>
    /// One .desktop file. The caller says where, because where it is IS the
    /// question in half of these.
    /// </summary>
    private static string Entry(string directory, string file, params string[] lines)
    {
        var path = Path.Combine(directory, file);

        File.WriteAllLines(path, ["[Desktop Entry]", "Type=Application", .. lines]);

        return path;
    }

    // ---- the key nothing read ----------------------------------------------

    [Fact]
    public void The_icon_key_is_read()
        => Assert.Equal("org.kde.konsole", DesktopEntries.ReadEntry(
            Entry(Installed, "a.desktop", "Name=Konsole", "Icon=org.kde.konsole")).Icon);

    [Fact]
    public void An_entry_with_no_icon_says_nothing()
        => Assert.Equal("", DesktopEntries.ReadEntry(
            Entry(Installed, "b.desktop", "Name=Konsole")).Icon);

    /// <summary>
    /// **Icon[de]= is a different key and must not match this prefix.** Written
    /// FIRST on purpose: an entry whose translations precede the plain key is
    /// how a prefix match would show itself, and every entry in this file that
    /// reads a key takes the first line that matches.
    /// </summary>
    [Fact]
    public void A_translated_icon_is_not_the_icon()
        => Assert.Equal("firefox", DesktopEntries.ReadEntry(
            Entry(Installed, "c.desktop", "Icon[de]=fuchs", "Name=Firefox", "Icon=firefox")).Icon);

    /// <summary>First wins, like Name and Exec beside it: an entry that repeats
    /// a key is one the spec says to read top-down.</summary>
    [Fact]
    public void An_entry_that_repeats_the_icon_key_takes_the_first()
        => Assert.Equal("firefox", DesktopEntries.ReadEntry(
            Entry(Installed, "twice.desktop", "Name=Firefox", "Icon=firefox", "Icon=fuchs")).Icon);

    /// <summary>Later [Desktop Action] groups are alternate launches, not the
    /// application — the same rule Name, Exec and Terminal already follow.</summary>
    [Fact]
    public void A_later_group_does_not_name_the_icon()
        => Assert.Equal("", DesktopEntries.ReadEntry(Entry(
            Installed, "d.desktop", "Name=Firefox",
            "[Desktop Action new-window]", "Icon=firefox-private")).Icon);

    // ---- whose words may be repeated ---------------------------------------

    /// <summary>
    /// Pure, and given both facts rather than reading them, so the states that
    /// matter can all be arranged — including the two an agent's machine
    /// cannot produce.
    /// </summary>
    [Fact]
    public void An_installed_launcher_is_believed()
        => Assert.True(DesktopEntries.Trusted(
            "/usr/share/applications/org.kde.konsole.desktop",
            ["/usr/share/applications"], executable: false));

    [Fact]
    public void A_launcher_somebody_marked_runnable_is_believed()
        => Assert.True(DesktopEntries.Trusted(
            "/home/me/Desktop/steam.desktop", ["/usr/share/applications"], executable: true));

    /// <summary>
    /// **The whole reason this is gated.** A .desktop file chooses its own Name
    /// and Icon, so one that arrives by download or by mail can call itself
    /// "Invoice" and wear a PDF icon while its Exec runs something else. Vaktari
    /// repeats those words only for a file somebody put in an application
    /// directory or marked runnable; anything else keeps its file name, which is
    /// the honest answer for a file nobody has vouched for.
    /// </summary>
    [Fact]
    public void A_downloaded_launcher_is_not_believed()
        => Assert.False(DesktopEntries.Trusted(
            "/home/me/Downloads/Invoice.desktop",
            ["/usr/share/applications"], executable: false));

    /// <summary>
    /// The prefix has to end at a separator, or "applications" claims
    /// "applications-backup" — the trap PathRules.Contains exists for, and the
    /// reason this is not a StartsWith.
    /// </summary>
    [Fact]
    public void A_directory_merely_named_like_one_is_not_an_application_directory()
        => Assert.False(DesktopEntries.Trusted(
            "/home/me/.local/share/applications-backup/x.desktop",
            ["/home/me/.local/share/applications"], executable: false));

    // ---- what the row gets --------------------------------------------------

    [Fact]
    public void An_installed_launcher_hands_over_its_name_and_icon()
    {
        var path = Entry(Installed, "org.kde.konsole.desktop",
                         "Name=Konsole", "Icon=org.kde.konsole", "Exec=konsole");

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(path));
    }

    [Fact]
    public void A_downloaded_launcher_hands_over_nothing()
    {
        var path = Entry(Downloads, "Invoice.desktop", "Name=Invoice", "Icon=application-pdf");

        Assert.Equal(("", ""), DesktopEntries.Launcher(path));
    }

    /// <summary>
    /// **The extension is the gate, and it is asked about every row in every
    /// listing.** Without it this is a real open-read-close per file per bind
    /// on a folder that is mostly not launchers, which is the cost the row
    /// pipeline exists to avoid.
    ///
    /// The file really does hold a desktop entry, so a gate that let it through
    /// would answer with the entry's own words rather than with the same empty
    /// pair an unreadable file gives.
    /// </summary>
    [Fact]
    public void An_ordinary_file_is_not_a_launcher()
    {
        var path = Path.Combine(Installed, "notes.txt");

        File.WriteAllLines(path, ["[Desktop Entry]", "Name=Konsole", "Icon=org.kde.konsole"]);

        Assert.Equal(("", ""), DesktopEntries.Launcher(path));
    }

    /// <summary>
    /// **A real open-read-close per row per bind is what the cache is for.**
    /// The name converter asks once per visible row every time the list
    /// rebinds, and the icon loader asks again from the pool. Proved by taking
    /// the file away: an answer that still arrives is one that was not re-read.
    /// </summary>
    [Fact]
    public void A_launcher_is_read_once()
    {
        var path = Entry(Installed, "cached.desktop", "Name=Konsole", "Icon=org.kde.konsole");

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(path));

        File.Delete(path);

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(path));
    }

    /// <summary>
    /// **Bounded rather than merely finite**, the same treatment IconLoader's
    /// two caches get: the key is a PATH, so a folder of ten thousand launchers
    /// would otherwise leave ten thousand pairs behind it for the life of the
    /// process.
    ///
    /// Taking the file away is how "was it read again?" is asked, the same way
    /// <see cref="A_launcher_is_read_once"/> asks it in the other direction —
    /// there it proves an answer survived, here that it did not.
    /// </summary>
    [Fact]
    public void What_is_remembered_is_bounded()
    {
        DesktopEntries.MaxLaunchers = 1;

        var first = Entry(Installed, "one.desktop", "Name=Konsole", "Icon=org.kde.konsole");

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(first));

        DesktopEntries.Launcher(Entry(Installed, "two.desktop", "Name=Kate", "Icon=org.kde.kate"));
        DesktopEntries.Launcher(Entry(Installed, "three.desktop", "Name=Ark", "Icon=org.kde.ark"));

        File.Delete(first);

        Assert.Equal(("", ""), DesktopEntries.Launcher(first));
    }

    /// <summary>
    /// **The bin lists a row under the path the file will come BACK to**, which
    /// by definition is not there while the row is on screen — so opening the
    /// bin asked about every trashed launcher, and an answer remembered from
    /// that ask was empty forever. Restoring one put the file back and left the
    /// row showing "org.kde.konsole.desktop" beside a grey page until the
    /// application was restarted.
    ///
    /// The positive direction beside this one only pins that a HIT is kept.
    /// Nothing asked what a miss leaves behind.
    /// </summary>
    [Fact]
    public void A_launcher_that_is_not_there_yet_is_not_remembered_as_nothing()
    {
        var path = Path.Combine(Installed, "org.kde.konsole.desktop");

        Assert.Equal(("", ""), DesktopEntries.Launcher(path));

        Entry(Installed, "org.kde.konsole.desktop", "Name=Konsole", "Icon=org.kde.konsole");

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(path));
    }

    /// <summary>
    /// **And a refusal is a fact about the file now, not forever.** Vaktari has
    /// its own permissions editor, so the execute bit that decides this can be
    /// ticked from the properties dialog while the folder is showing. A
    /// remembered refusal meant doing that changed nothing on the row.
    ///
    /// The bit itself is the machine fact the seam exists for: this suite runs
    /// on Windows, where GetUnixFileMode throws and there is no chmod.
    /// </summary>
    [Fact]
    public void A_launcher_marked_runnable_afterwards_is_asked_again()
    {
        var path = Entry(Downloads, "hand-written.desktop", "Name=Konsole", "Icon=org.kde.konsole");

        DesktopEntries.ExecutableOverride = _ => false;

        Assert.Equal(("", ""), DesktopEntries.Launcher(path));

        DesktopEntries.ExecutableOverride = _ => true;

        Assert.Equal(("Konsole", "org.kde.konsole"), DesktopEntries.Launcher(path));
    }

    // ---- the icon the theme is asked for ------------------------------------

    /// <summary>
    /// **Icon=firefox.png is common and the theme index is keyed without the
    /// extension**, so the value as written matched nothing and the launcher
    /// kept the grey page. Verbatim first regardless, because the theme may
    /// genuinely ship a file called firefox.png.png — and because the strip has
    /// to be wrong-proof, which the next test is about.
    /// </summary>
    [Fact]
    public void An_icon_written_with_its_extension_is_also_offered_without_one()
    {
        var path = Entry(Installed, "firefox.desktop", "Name=Firefox", "Icon=firefox.png");

        Assert.Equal(
            ["firefox.png", "firefox", "application-x-desktop", "application-x-executable"],
            XdgIconNaming.LauncherIcons(path));
    }

    /// <summary>
    /// **A dot in an icon name is not automatically an extension.**
    /// org.kde.konsole is the convention half of KDE follows, and
    /// Path.GetExtension calls its last component ".konsole" — asking a theme
    /// for "org.kde" would be asking for the wrong picture, if any.
    /// </summary>
    [Fact]
    public void A_reverse_dns_icon_name_keeps_every_part()
    {
        var path = Entry(Installed, "konsole.desktop", "Name=Konsole", "Icon=org.kde.konsole");

        Assert.Equal(
            ["org.kde.konsole", "application-x-desktop", "application-x-executable"],
            XdgIconNaming.LauncherIcons(path));
    }

    /// <summary>
    /// **The extension is matched the way the index it feeds is keyed.**
    /// FreedesktopIconTheme indexes theme files under OrdinalIgnoreCase, on the
    /// file name with its extension removed — so "firefox" is a key that
    /// exists. The first version of the strip pattern-matched ".png" ordinally,
    /// so an entry that spells it Icon=firefox.PNG offered only the name with
    /// the extension still on it, matched nothing, and kept the generic page
    /// this whole change is about.
    /// </summary>
    [Fact]
    public void An_icon_written_with_a_shouted_extension_is_stripped_too()
    {
        var path = Entry(Installed, "firefox-shouted.desktop", "Name=Firefox", "Icon=Firefox.PNG");

        Assert.Equal(
            ["Firefox.PNG", "Firefox", "application-x-desktop", "application-x-executable"],
            XdgIconNaming.LauncherIcons(path));
    }

    /// <summary>
    /// **Path.GetExtension(".png") answers ".png", not "".** So a degenerate
    /// Icon=.png stripped to the empty string and handed the theme a name it
    /// could only fail on, ahead of the two fallbacks that would have worked.
    /// </summary>
    [Fact]
    public void An_icon_that_is_nothing_but_an_extension_keeps_it()
    {
        var path = Entry(Installed, "degenerate.desktop", "Name=Odd", "Icon=.png");

        Assert.Equal(
            [".png", "application-x-desktop", "application-x-executable"],
            XdgIconNaming.LauncherIcons(path));
    }

    /// <summary>An entry with no Icon= has no opinion, and the row falls back
    /// to the mime answer it had before — which is what empty means here.</summary>
    [Fact]
    public void A_launcher_with_no_icon_offers_no_names()
        => Assert.Empty(XdgIconNaming.LauncherIcons(
            Entry(Installed, "plain.desktop", "Name=Konsole")));

    /// <summary>
    /// The naming seam the icon loader actually calls, not just the helper
    /// behind it — and it has to come FIRST.
    ///
    /// **The mime database has to be given something to say, or order is not
    /// what this observes.** There is no shared-mime-info and no xdg-mime on a
    /// Windows agent, so the branch this one has to beat answers with an empty
    /// list here; a test that only asked for [0] would then redden on a moved
    /// line with IndexOutOfRange and on a deleted one identically, saying
    /// nothing about which came first. The sniff seam supplies the answer the
    /// desktop would have given — application/x-desktop, which is what EVERY
    /// launcher sniffs as, and the reason the mime route is useless here — so
    /// the whole list is the observation and the wrong order names it.
    /// </summary>
    [Fact]
    public void The_naming_asks_for_the_launchers_own_icon_first()
    {
        DesktopEntries.SniffOverride = (_, _) => "application/x-desktop";

        var path = Entry(Installed, "steam.desktop", "Name=Steam", "Icon=steam");

        Assert.Equal(
            ["steam", "application-x-desktop", "application-x-executable"],
            new XdgIconNaming().NamesFor(path));
    }

    /// <summary>The mime answer this beats, so the list above is a CHOICE
    /// between two real answers rather than the only one on offer.</summary>
    [Fact]
    public void Without_an_icon_of_its_own_a_launcher_falls_to_the_mime_answer()
    {
        DesktopEntries.SniffOverride = (_, _) => "application/x-desktop";

        var path = Entry(Installed, "steam-plain.desktop", "Name=Steam");

        Assert.Equal("application-x-desktop", new XdgIconNaming().NamesFor(path)[0]);
    }

    // ---- and the name the row shows -----------------------------------------

    /// <summary>Wired the way LinuxPlatform wires it, so what is proved here is
    /// the path the application takes rather than a stub.</summary>
    private void AdoptTheRealReader()
        => FileKind.LauncherName = path => DesktopEntries.Launcher(path).Name;

    private static FileEntry Row(string path)
        => new(Path.GetFileName(path), path, 0, DateTimeOffset.UnixEpoch, EntryFlags.None);

    [Fact]
    public void A_launcher_lists_under_the_name_it_gives_itself()
    {
        AdoptTheRealReader();

        var path = Entry(Installed, "org.kde.dolphin.desktop", "Name=Dolphin", "Icon=system-file-manager");

        Assert.Equal("Dolphin", FileKind.DisplayName(Row(path)));
    }

    /// <summary>
    /// **DisplayName cannot return empty**, which the shortcut arm above states
    /// as a rule and the seam could have broken: a trusted .desktop file with
    /// no Name= at all hands back "", and a row drawn from that is a blank
    /// where a file used to be.
    /// </summary>
    [Fact]
    public void A_launcher_that_names_itself_nothing_keeps_its_file_name()
    {
        AdoptTheRealReader();

        var path = Entry(Installed, "nameless.desktop", "Icon=org.kde.konsole");

        Assert.Equal("nameless.desktop", FileKind.DisplayName(Row(path)));
    }

    [Fact]
    public void A_downloaded_launcher_keeps_its_file_name()
    {
        AdoptTheRealReader();

        var path = Entry(Downloads, "Invoice.desktop", "Name=Invoice", "Icon=application-pdf");

        Assert.Equal("Invoice.desktop", FileKind.DisplayName(Row(path)));
    }

    /// <summary>
    /// **This runs once per visible row per bind, for every row.** A listing is
    /// overwhelmingly not launchers, so the extension has to be the gate and the
    /// seam has to stay unasked — a file read per row while scrolling is the one
    /// cost this codebase does not pay.
    /// </summary>
    [Fact]
    public void An_ordinary_row_never_asks()
    {
        var asked = 0;

        FileKind.LauncherName = _ => { asked++; return "Konsole"; };

        Assert.Equal("notes.txt", FileKind.DisplayName(Row(Path.Combine(Installed, "notes.txt"))));
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// A GUARD, and it cannot fail on a platform that sets the seam: with no
    /// reader adopted — which is every build but the Linux one — a .desktop file
    /// is an opaque file from another operating system and keeps its whole name.
    /// </summary>
    [Fact]
    public void With_no_reader_a_launcher_is_just_a_file()
    {
        FileKind.LauncherName = null;

        var path = Entry(Installed, "org.kde.kate.desktop", "Name=Kate", "Icon=kate");

        Assert.Equal("org.kde.kate.desktop", FileKind.DisplayName(Row(path)));
    }

    /// <summary>
    /// The wiring, which no unit can be asked about: Core holds the seam and
    /// cannot reference this assembly, so the one place a platform is chosen is
    /// the one place it can be filled in.
    /// </summary>
    [Fact]
    public void The_linux_platform_adopts_the_reader()
        => Assert.Contains(
            "FileKind.LauncherName = path => DesktopEntries.Launcher(path).Name;",
            RepoSource.Read("src", "Vaktari.Linux", "LinuxPlatform.cs"),
            StringComparison.Ordinal);
}
