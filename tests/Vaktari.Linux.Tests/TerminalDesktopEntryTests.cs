using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// A .desktop entry that says it needs a console.
///
/// **Nothing read Terminal=.** vim, nano and htop all ship such entries and all
/// register against text/plain, so they appeared in "Open with" for any text
/// file — and their Exec was spawned directly with no tty. The process started,
/// Process.Start returned non-null so the launch reported success, and nothing
/// ever appeared on screen: vim exits at once off a tty, htop lingers
/// invisibly. A menu row that can only do nothing.
/// </summary>
public sealed class TerminalDesktopEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-desktop-" + Guid.NewGuid().ToString("N")[..8]);

    public TerminalDesktopEntryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string Entry(string name, params string[] extra)
    {
        var path = Path.Combine(_root, name + ".desktop");

        File.WriteAllLines(path, ["[Desktop Entry]", "Type=Application",
                                  "Name=" + name, "Exec=" + name + " %f", .. extra]);

        return path;
    }

    [Fact]
    public void An_entry_that_needs_a_console_says_so()
        => Assert.True(DesktopEntries.ReadEntry(Entry("vim", "Terminal=true")).Terminal);

    [Fact]
    public void An_ordinary_application_does_not()
        => Assert.False(DesktopEntries.ReadEntry(Entry("gedit")).Terminal);

    /// <summary>Terminal=false is the common spelling and must not be read as a
    /// prefix match on "Terminal=".</summary>
    [Fact]
    public void An_entry_that_says_false_is_not_a_console_application()
        => Assert.False(DesktopEntries.ReadEntry(Entry("gedit", "Terminal=false")).Terminal);

    /// <summary>The key is case-insensitive in practice, as the neighbouring
    /// NoDisplay and Hidden reads already assume.</summary>
    [Fact]
    public void The_value_is_read_however_it_is_cased()
        => Assert.True(DesktopEntries.ReadEntry(Entry("htop", "Terminal=True")).Terminal);

    /// <summary>
    /// Later [Desktop Action] groups are alternate launches, not the
    /// application — a Terminal=true there says nothing about the main entry.
    /// </summary>
    [Fact]
    public void A_later_group_does_not_speak_for_the_application()
        => Assert.False(DesktopEntries.ReadEntry(
            Entry("gedit", "[Desktop Action new-window]", "Terminal=true")).Terminal);

    // ---- and how it is actually run -----------------------------------------

    private static TerminalOption Terminal(string command, params string[] run)
        => new("t", "T", command, []) { RunArguments = run };

    [Fact]
    public void The_command_is_wrapped_in_the_terminal()
        => Assert.Equal(
            ["/usr/bin/konsole", "-e", "vim", "/tmp/notes.txt"],
            DesktopEntries.InTerminal(
                Terminal("/usr/bin/konsole", "-e"), ["vim", "/tmp/notes.txt"]));

    /// <summary>
    /// **Not all of them spell it "-e".** gnome-terminal deprecated its -e and
    /// takes a single string after it, so an argv there would run only the
    /// first word; kitty takes the command positionally with no flag at all.
    /// </summary>
    [Theory]
    [InlineData("gnome-terminal", "--")]
    [InlineData("xfce4-terminal", "-x")]
    public void Each_terminal_uses_its_own_spelling(string command, string flag)
        => Assert.Equal(
            [command, flag, "vim", "/tmp/a.txt"],
            DesktopEntries.InTerminal(Terminal(command, flag), ["vim", "/tmp/a.txt"]));

    [Fact]
    public void A_terminal_that_takes_the_command_positionally_gets_no_flag()
        => Assert.Equal(
            ["kitty", "vim", "/tmp/a.txt"],
            DesktopEntries.InTerminal(Terminal("kitty"), ["vim", "/tmp/a.txt"]));

    /// <summary>The default for one we do not recognise, which is what
    /// $TERMINAL pointing at something unknown produces.</summary>
    [Fact]
    public void An_unrecognised_terminal_gets_the_near_universal_flag()
        => Assert.Equal(["-e"], new TerminalOption("t", "T", "whatever", []).RunArguments);

    /// <summary>
    /// **With no terminal, launching must refuse rather than report success.**
    /// Reporting success is the whole bug: Process.Start returned non-null and
    /// nothing appeared.
    ///
    /// Driven through the real Launch against a desktop file this test installs
    /// where the lookup looks, because a Launch that returns false merely
    /// because the id was not found proves nothing at all.
    /// </summary>
    [PosixFact]
    public void With_no_terminal_a_console_application_refuses_to_launch()
    {
        var applications = Path.Combine(_root, "applications");
        Directory.CreateDirectory(applications);

        // **/bin/true, not a command that fails.** A command that cannot start
        // returns false either way, so the test could not tell "refused because
        // there is no terminal" from "tried and failed" — which is exactly the
        // distinction the fix is about.
        File.WriteAllLines(
            Path.Combine(applications, "vaktari-fake-vim.desktop"),
            ["[Desktop Entry]", "Type=Application", "Name=Fake vim",
             "Exec=/bin/true %f", "Terminal=true"]);

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var terminals = DesktopEntries.Terminals;

        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _root);

            DesktopEntries.Terminals = [];

            Assert.False(
                DesktopEntries.Launch("vaktari-fake-vim.desktop", "/tmp/a.txt"),
                "launched a console application with no console, and reported success");

            // And with one, it goes: the refusal above is the guard, not the
            // whole path being dead.
            DesktopEntries.Terminals = [Terminal("/bin/true", "-e")];

            Assert.True(DesktopEntries.Launch("vaktari-fake-vim.desktop", "/tmp/a.txt"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            DesktopEntries.Terminals = terminals;
        }
    }

    /// <summary>
    /// **The table is where this is easy to get wrong**, and no test that
    /// builds its own TerminalOption can see it. gnome-terminal deprecated its
    /// -e and takes a single string after it, so an argv there runs only the
    /// first word — silently, which is the same failure this whole finding is
    /// about.
    /// </summary>
    [Theory]
    [InlineData("gnome-terminal", "[\"--\"]")]
    [InlineData("xfce4-terminal", "[\"-x\"]")]
    [InlineData("kitty", "[]")]
    public void The_terminals_that_do_not_spell_it_dash_e_are_recorded(string exe, string run)
    {
        var here = AppContext.BaseDirectory;

        while (here is not null && !File.Exists(Path.Combine(here, "Vaktari.slnx")))
            here = Path.GetDirectoryName(here);

        var line = File.ReadAllLines(
                Path.Combine(here!, "src", "Vaktari.Linux", "LinuxLauncher.cs"))
            .First(l => l.Contains($"(\"{exe}\",", StringComparison.Ordinal));

        Assert.EndsWith(run + "),", line.TrimEnd());
    }
}
