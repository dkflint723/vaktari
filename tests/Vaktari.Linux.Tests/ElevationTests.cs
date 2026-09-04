using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// "Run as administrator" and the admin terminal, on Linux.
///
/// **Neither existed here, and the interface said why: pkexec and sudo were "a
/// policy question rather than a menu entry", and a file manager quietly
/// deciding to elevate on a Linux desktop was not making a decision that was
/// its to make.** Windows had both entries; Linux had a no-op. That reasoning
/// held for sudo — sudoers is a policy question and there is no dialog — and
/// not for pkexec, which decides nothing at all: it hands the request to
/// polkit, which puts up the system's OWN authentication dialog and answers to
/// the machine's policy. That is the same arrangement as the runas verb handing
/// a request to Windows' consent dialog, and the two were being treated as one
/// thing.
///
/// What is pinned here is the shape of the argv, because the argv is the whole
/// of what can be wrong: a flag in the wrong place runs the wrong program, or
/// opens the person's shell where it meant to open root's, and a spawn that
/// succeeds proves only that a terminal opened.
/// </summary>
public sealed class ElevationTests
{
    /// <summary>What a launcher was told to start, in order.</summary>
    private sealed record Started(string Directory, string[] Argv);

    private sealed class Watched
    {
        public List<Started> Spawns { get; } = [];

        /// <summary>Which of the programs offered will actually start. Empty
        /// means every one refuses, which is the state a machine cannot be
        /// asked to produce.</summary>
        public HashSet<string> Working { get; } = [];

        public bool Spawn(string directory, IReadOnlyList<string> argv)
        {
            Spawns.Add(new Started(directory, [.. argv]));

            return Working.Contains(argv[0]);
        }
    }

    private static (LinuxLauncher Launcher, Watched Watched) Machine(
        string? pkexec, params TerminalOption[] terminals)
    {
        var watched = new Watched();
        var launcher = new LinuxLauncher();

        launcher.UsePkexec(pkexec);
        launcher.UseTerminals(terminals);
        launcher.SpawnOverride = watched.Spawn;

        foreach (var terminal in terminals) watched.Working.Add(terminal.Command);

        return (launcher, watched);
    }

    private static TerminalOption Konsole =>
        new("konsole", "Konsole", "/usr/bin/konsole", ["--workdir", "{dir}"]);

    /// <summary>
    /// A file to elevate, and the folder holding it.
    ///
    /// Built with Path.Combine rather than written out, because the suite is
    /// mostly run on Windows and the launcher asks Path.GetDirectoryName — a
    /// literal "/opt/vendor/install.sh" would come back with backslashes there
    /// and the test would be pinning the separator instead of the rule.
    /// </summary>
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "vendor");

    private static readonly string Installer = Path.Combine(Folder, "install.sh");

    // ---- whether it is offered at all ---------------------------------------

    [Fact]
    public void A_machine_with_pkexec_can_elevate()
        => Assert.True(Machine("/usr/bin/pkexec").Launcher.CanElevate);

    /// <summary>
    /// **Without pkexec the answer has to be no**, so the two rows vanish from
    /// the menu rather than appearing and failing. A machine with polkit
    /// uninstalled, or a container, is an ordinary machine.
    /// </summary>
    [Fact]
    public void A_machine_without_pkexec_cannot()
        => Assert.False(Machine(null).Launcher.CanElevate);

    /// <summary>
    /// And no file is offered one either. Linux only, because the assertion has
    /// to be made against a file the mode read would otherwise SAY YES to —
    /// anywhere else GetUnixFileMode throws, the catch answers no, and the test
    /// would pass without the rule being there at all.
    /// </summary>
    [PosixFact]
    public void Without_pkexec_no_file_is_offered_an_elevated_run()
        => WithRunnableFile(file => Assert.False(Machine(null).Launcher.CanElevateFile(file)));

    /// <summary>
    /// **Any of the three execute bits, not the owner's.** The binaries this
    /// verb exists for are the root-owned ones — 0755, owner root — so asking
    /// whether the OWNER may run it answers for root rather than for the person
    /// at the keyboard, and says yes to every such file whether or not anybody
    /// else could run it.
    /// </summary>
    [Theory]
    [InlineData(UnixFileMode.UserExecute, true)]
    [InlineData(UnixFileMode.GroupExecute, true)]
    [InlineData(UnixFileMode.OtherExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, false)]
    [InlineData(UnixFileMode.None, false)]
    public void The_execute_bit_is_the_whole_of_the_question(UnixFileMode mode, bool runnable)
        => Assert.Equal(runnable, LinuxLauncher.Runnable(mode));

    /// <summary>
    /// And the mode really is read off the file. Linux only, because
    /// GetUnixFileMode throws everywhere else — a body guard here would report
    /// a pass on a machine where nothing ran.
    /// </summary>
    [PosixFact]
    public void A_file_on_disk_is_offered_only_once_it_can_be_run()
    {
        var (launcher, _) = Machine("/usr/bin/pkexec");

        WithRunnableFile(file =>
        {
            Assert.True(launcher.CanElevateFile(file));

            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.False(launcher.CanElevateFile(file));
        });
    }

    /// <summary>A real file with a real execute bit, cleaned up afterwards.
    /// Only ever called from a PosixFact — SetUnixFileMode throws
    /// everywhere else.</summary>
    private static void WithRunnableFile(Action<string> body)
    {
        var file = Path.Combine(
            Path.GetTempPath(), "vaktari-elevate-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            File.WriteAllText(file, "#!/bin/sh\necho hello\n");
            File.SetUnixFileMode(file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            body(file);
        }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { /* a temp file is not worth failing over */ }
        }
    }

    /// <summary>A folder is the thing most likely to be selected, and pkexec has
    /// nothing to do with one.</summary>
    [PosixFact]
    public void A_folder_is_never_offered_an_elevated_run()
        => Assert.False(Machine("/usr/bin/pkexec").Launcher.CanElevateFile(Path.GetTempPath()));

    // ---- the admin terminal --------------------------------------------------

    /// <summary>
    /// **The terminal is ours and the shell inside it is root's, not the other
    /// way round.** "pkexec konsole" is the obvious spelling and it does not
    /// work: pkexec unsets DISPLAY and XAUTHORITY — its own documented refusal
    /// to launch X11 applications — so a terminal started that way has no
    /// display to open on. Running pkexec inside an ordinary terminal also
    /// leaves the window itself unprivileged.
    /// </summary>
    [Fact]
    public void An_admin_terminal_runs_pkexec_inside_the_terminal()
    {
        var (launcher, watched) = Machine("/usr/bin/pkexec", Konsole);

        launcher.OpenElevatedTerminal("/srv/work");

        var started = Assert.Single(watched.Spawns);

        Assert.Equal("/srv/work", started.Directory);
        Assert.Equal(["/usr/bin/konsole", "-e", "/usr/bin/pkexec"], started.Argv[..3]);

        // The fourth and last is the shell, whose choice is pinned separately —
        // it is read from $SHELL, which belongs to whoever ran the suite.
        Assert.Equal(4, started.Argv.Length);
    }

    /// <summary>
    /// **The terminal's working-directory flags are not mixed into the run.**
    /// The folder arrives as the directory the process is started in, which
    /// every terminal honours; concatenating the two argument lists produces
    /// nonsense on at least one of them — WezTerm opens with
    /// ["start", "--cwd", dir] and runs with ["start", "--"], so a joined argv
    /// says "start" twice and WezTerm refuses it.
    /// </summary>
    [Fact]
    public void The_folder_is_where_the_process_starts_not_an_argument()
    {
        var wezterm = new TerminalOption(
            "wezterm", "WezTerm", "wezterm", ["start", "--cwd", "{dir}"])
        {
            RunArguments = ["start", "--"],
        };

        var (launcher, watched) = Machine("/usr/bin/pkexec", wezterm);

        launcher.OpenElevatedTerminal("/srv/work");

        var started = Assert.Single(watched.Spawns);

        Assert.Equal(["wezterm", "start", "--", "/usr/bin/pkexec"], started.Argv[..4]);
        Assert.Equal("/srv/work", started.Directory);
        Assert.DoesNotContain("/srv/work", started.Argv);
    }

    /// <summary>
    /// The shell an elevated terminal opens: the person's own where $SHELL says
    /// so, and /bin/sh otherwise — the one path POSIX guarantees. /bin/bash is
    /// missing on Alpine and on a good many containers, so it is not a safe
    /// fallback.
    /// </summary>
    [Theory]
    [InlineData("/usr/bin/fish", "/usr/bin/fish")]
    [InlineData("/bin/zsh", "/bin/zsh")]
    [InlineData("", "/bin/sh")]
    [InlineData("   ", "/bin/sh")]
    [InlineData(null, "/bin/sh")]
    public void The_elevated_shell_is_the_persons_own_or_the_guaranteed_one(
        string? shell, string expected)
        => Assert.Equal(expected, LinuxLauncher.ShellFor(shell));

    /// <summary>
    /// A terminal that will not start is followed by the next, and then by the
    /// one that needs no detection — the same walk the unelevated F4 does. It
    /// must not ask the same refusing terminal twice, which is how that pair
    /// once recursed until the stack ran out.
    /// </summary>
    [Fact]
    public void A_terminal_that_refuses_is_followed_by_the_rest()
    {
        var broken = new TerminalOption("broken", "Broken", "vaktari-no-such-terminal", []);

        var (launcher, watched) = Machine("/usr/bin/pkexec", broken, Konsole);

        // Only Konsole answers; the first candidate is the broken one.
        watched.Working.Remove("vaktari-no-such-terminal");

        launcher.OpenElevatedTerminal("/srv/work");

        Assert.Equal(
            ["vaktari-no-such-terminal", "/usr/bin/konsole"],
            watched.Spawns.Select(s => s.Argv[0]));
    }

    /// <summary>
    /// **And it is not asked twice.** The list is cached for the life of the
    /// process, so a terminal that refuses refuses every time; asking the same
    /// one again is how the unelevated pair used to recurse until the stack ran
    /// out, and here it would simply waste the one attempt that had a chance.
    /// </summary>
    [Fact]
    public void The_named_terminal_is_not_offered_twice()
    {
        var (launcher, watched) = Machine("/usr/bin/pkexec", Konsole);

        // Nothing at all will start, so every candidate is tried in turn.
        watched.Working.Clear();

        launcher.OpenElevatedTerminal("/srv/work", Konsole);

        Assert.Equal(
            ["/usr/bin/konsole", "xterm"],
            watched.Spawns.Select(s => s.Argv[0]));
    }

    /// <summary>
    /// Nothing detected at all is the ordinary state on a minimal machine, and
    /// xterm still needs no detection to be worth trying.
    /// </summary>
    [Fact]
    public void With_no_terminal_detected_xterm_is_still_tried()
    {
        var (launcher, watched) = Machine("/usr/bin/pkexec");

        launcher.OpenElevatedTerminal("/srv/work");

        var started = Assert.Single(watched.Spawns);

        Assert.Equal(["xterm", "-e", "/usr/bin/pkexec"], started.Argv[..3]);
    }

    /// <summary>
    /// **And on a machine with no pkexec nothing is started at all.** The menu
    /// already hides both rows there, but a command that trusts its own entry's
    /// visibility is one keyboard binding away from spawning a terminal that
    /// prints "pkexec: command not found".
    /// </summary>
    [Fact]
    public void Without_pkexec_the_admin_terminal_starts_nothing()
    {
        var (launcher, watched) = Machine(null, Konsole);

        launcher.OpenElevatedTerminal("/srv/work");

        Assert.Empty(watched.Spawns);
    }

    // ---- running one file ----------------------------------------------------

    /// <summary>
    /// **In a terminal, because pkexec unsets DISPLAY and XAUTHORITY.** What it
    /// starts has no window, and without a console it has nowhere to print its
    /// output and nowhere to say why it stopped — the same fault this repository
    /// already fixed for Terminal=true desktop entries, where a spawned vim
    /// exited instantly off a tty and the launch still reported success.
    /// </summary>
    [Fact]
    public void Running_a_file_elevated_runs_it_in_a_terminal_under_pkexec()
    {
        var (launcher, watched) = Machine("/usr/bin/pkexec", Konsole);

        launcher.OpenElevated(Installer);

        var started = Assert.Single(watched.Spawns);

        Assert.Equal(
            ["/usr/bin/konsole", "-e", "/usr/bin/pkexec", Installer],
            started.Argv);

        // Its own folder, so a script that reads a file sitting beside it finds
        // it — the same rule the Windows launcher was given.
        Assert.Equal(Folder, started.Directory);
    }

    /// <summary>
    /// **The file verb walks the same candidates the terminal one does.** A
    /// terminal that refuses is not a reason to give up on the next: with only
    /// the admin terminal asking, this walk could be cut to its first candidate
    /// and nothing here would have noticed.
    /// </summary>
    [Fact]
    public void A_refusing_terminal_does_not_stop_an_elevated_run()
    {
        var broken = new TerminalOption("broken", "Broken", "vaktari-no-such-terminal", []);

        var (launcher, watched) = Machine("/usr/bin/pkexec", broken, Konsole);

        watched.Working.Remove("vaktari-no-such-terminal");

        launcher.OpenElevated(Installer);

        Assert.Equal(
            ["vaktari-no-such-terminal", "/usr/bin/konsole"],
            watched.Spawns.Select(s => s.Argv[0]));
    }

    /// <summary>
    /// No terminal on the machine would start, and the elevation still happens:
    /// polkit's authentication agent is a session service and has nothing to do
    /// with our terminal. Only the program's own output has nowhere to go.
    /// </summary>
    [Fact]
    public void With_nothing_that_will_start_pkexec_is_run_on_its_own()
    {
        var (launcher, watched) = Machine("/usr/bin/pkexec");

        // xterm is offered and refuses, like everything else here.
        launcher.OpenElevated(Installer);

        Assert.Equal(
            ["/usr/bin/pkexec", Installer],
            watched.Spawns[^1].Argv);
    }

    [Fact]
    public void Without_pkexec_running_a_file_elevated_starts_nothing()
    {
        var (launcher, watched) = Machine(null, Konsole);

        launcher.OpenElevated(Installer);

        Assert.Empty(watched.Spawns);
    }
}
