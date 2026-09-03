using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Which terminal "Open terminal here" opens.
///
/// **Plasma has had a terminal preference forever and Vaktari read none of
/// it.** A KDE user who set theirs to Alacritty still got Konsole, because
/// Konsole is simply first in the built-in list — the one desktop with a
/// setting for this was the one whose setting was ignored.
///
/// And the list itself was short enough to matter: Ptyxis ships as GNOME's
/// terminal on recent Fedora, Terminator and Tilix are what people install when
/// they want splits, Ghostty is spreading, and x-terminal-emulator is the
/// Debian link that answers when nothing else is installed under its own name.
/// Missing all of them, Vaktari fell through to xterm on machines with a
/// perfectly good terminal on them.
/// </summary>
public sealed class DesktopTerminalTests : IDisposable
{
    private readonly string _config = Path.Combine(
        Path.GetTempPath(), "vaktari-kdeglobals-" + Guid.NewGuid().ToString("N")[..8]);

    public DesktopTerminalTests()
    {
        Directory.CreateDirectory(_config);

        LinuxLauncher.ConfigHomeOverride = () => _config;
    }

    public void Dispose()
    {
        LinuxLauncher.ConfigHomeOverride = null;

        try { Directory.Delete(_config, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private void KdeGlobals(string contents)
        => File.WriteAllText(Path.Combine(_config, "kdeglobals"), contents);

    [Fact]
    public void The_desktops_own_choice_is_read()
    {
        KdeGlobals("[General]\nTerminalApplication=alacritty\n");

        Assert.Equal("alacritty", LinuxLauncher.DesktopTerminal());
    }

    /// <summary>
    /// **Only the [General] group.** The key appears in others — a
    /// per-application override among them — so taking the first match anywhere
    /// in the file reads somebody else's setting and opens the wrong program.
    /// </summary>
    [Fact]
    public void A_setting_in_another_group_is_not_this_one()
    {
        KdeGlobals("""
            [KFileDialog Settings]
            TerminalApplication=not-this-one

            [General]
            TerminalApplication=kitty
            """);

        Assert.Equal("kitty", LinuxLauncher.DesktopTerminal());
    }

    /// <summary>And a key outside every group is not it either.</summary>
    [Fact]
    public void A_setting_above_the_first_group_is_ignored()
    {
        KdeGlobals("TerminalApplication=stray\n[General]\nColorScheme=Breeze\n");

        Assert.Null(LinuxLauncher.DesktopTerminal());
    }

    /// <summary>
    /// Plasma 6 records the desktop entry's name where Plasma 5 recorded a
    /// command, and the two differ by exactly the suffix — which is not part of
    /// any executable's name.
    /// </summary>
    [Fact]
    public void An_entry_name_is_trimmed_back_to_the_command()
    {
        KdeGlobals("[General]\nTerminalApplication=org.gnome.Ptyxis.desktop\n");

        Assert.Equal("org.gnome.Ptyxis", LinuxLauncher.DesktopTerminal());
    }

    [Theory]
    [InlineData("[General]\nTerminalApplication=\n")]
    [InlineData("[General]\nTerminalApplication\n")]
    [InlineData("[General]\nColorScheme=Breeze\n")]
    [InlineData("")]
    public void Nothing_set_is_nothing_read(string contents)
    {
        KdeGlobals(contents);

        Assert.Null(LinuxLauncher.DesktopTerminal());
    }

    /// <summary>A machine with no kdeglobals at all — every desktop but one —
    /// answers nothing rather than throwing.</summary>
    [Fact]
    public void A_machine_with_no_such_file_answers_nothing()
        => Assert.Null(LinuxLauncher.DesktopTerminal());

    /// <summary>
    /// **And the detection really consults it, after $TERMINAL and before the
    /// list.** Read from the source, which is the honest ceiling: the only
    /// caller resolves a name against the real PATH, and both the variable and
    /// the PATH are process-global — the kind of thing that has already broken
    /// one test class in this repository by leaking into another.
    ///
    /// The order is the claim being pinned. $TERMINAL is the more explicit
    /// choice and the one a person sets per session, so it wins; a desktop
    /// preference beats a list of guesses, so it comes before the loop.
    /// </summary>
    [Fact]
    public void The_detection_asks_the_desktop_between_the_variable_and_the_list()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxLauncher.cs");

        var variable = source.IndexOf("\"TERMINAL\"", StringComparison.Ordinal);
        var desktop = source.IndexOf("DesktopTerminal() is", StringComparison.Ordinal);
        var loop = source.IndexOf("foreach (var (id, name, exe, args, run) in Known)",
                                  StringComparison.Ordinal);

        Assert.True(variable > 0, "the TERMINAL variable is no longer read");
        Assert.True(desktop > variable, "the desktop's setting is consulted before $TERMINAL");
        Assert.True(loop > desktop, "the built-in list is tried before the desktop's setting");

        // And only when the variable found nothing: an explicit $TERMINAL would
        // otherwise be joined by a second option rather than answered with.
        var gate = source.IndexOf("found.Count == 0", variable, StringComparison.Ordinal);

        Assert.True(gate > 0 && gate < desktop,
                    "the desktop's setting is read whether or not $TERMINAL already answered");
    }

    // ---- and the list it falls back to --------------------------------------

    /// <summary>
    /// The terminals a desktop is likely to have. Named here so that removing
    /// one from the table fails a test rather than quietly costing somebody
    /// their terminal.
    /// </summary>
    [Theory]
    [InlineData("konsole")]
    [InlineData("gnome-terminal")]
    [InlineData("ptyxis")]
    [InlineData("ghostty")]
    [InlineData("terminator")]
    [InlineData("tilix")]
    [InlineData("alacritty")]
    [InlineData("kitty")]
    [InlineData("wezterm")]
    [InlineData("foot")]
    [InlineData("xfce4-terminal")]
    [InlineData("mate-terminal")]
    [InlineData("lxterminal")]
    [InlineData("qterminal")]
    [InlineData("x-terminal-emulator")]
    [InlineData("xterm")]
    public void The_known_list_holds_this_terminal(string exe)
        => Assert.Contains(
            $"\"{exe}\"", RepoSource.Read("src", "Vaktari.Linux", "LinuxLauncher.cs"));

    /// <summary>
    /// **xterm stays last, and the alternatives link second to last.** The list
    /// is a preference order — the first one present is what opens — so an
    /// entry that answers on almost every machine, placed early, would take the
    /// gesture away from every terminal below it.
    /// </summary>
    [Fact]
    public void The_last_resorts_are_last()
    {
        var source = RepoSource.Read("src", "Vaktari.Linux", "LinuxLauncher.cs");

        var link = source.IndexOf("(\"x-terminal-emulator\"", StringComparison.Ordinal);
        var xterm = source.IndexOf("(\"xterm\",", StringComparison.Ordinal);
        var konsole = source.IndexOf("(\"konsole\",", StringComparison.Ordinal);

        Assert.True(konsole > 0 && link > konsole, "the alternatives link is above a real terminal");
        Assert.True(xterm > link, "xterm is above the alternatives link");
    }
}
