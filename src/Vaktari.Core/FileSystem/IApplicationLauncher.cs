namespace Vaktari.Core.FileSystem;

/// <summary>One application that can open a given file.</summary>
public sealed record LaunchOption(string Name, string Id, string? Icon = null)
{
    /// <summary>
    /// The "pick something else" row rather than an installed application.
    ///
    /// A member of the same list because the menu is driven by ItemsSource:
    /// a static sibling cannot be added beside bound items, and a chooser that
    /// sits anywhere other than the bottom of the list it belongs to is a
    /// chooser nobody finds.
    /// </summary>
    public bool IsChooser { get; init; }
}

/// <summary>
/// One terminal this machine can actually open, as offered in the menu.
///
/// **A record rather than a hardcoded chain**, because there was one: the
/// launcher tried Windows Terminal, then PowerShell, then cmd, took the first
/// that started, and gave the user no say. On a machine with Warp, or WSL, or
/// a Git Bash the person actually lives in, "open a terminal here" opened the
/// wrong one and there was nowhere to say so.
///
/// <param name="Id">Stable across launches — it is what the preference stores,
/// so it must not be the display name, which is the sort of thing that gets
/// tidied up later.</param>
/// <param name="Name">What the menu shows: the terminal's own name, as its
/// installer spells it.</param>
/// <param name="Command">The executable to run.</param>
/// <param name="Arguments">Its arguments, with <c>{dir}</c> where the folder
/// goes. A terminal that takes the folder as its working directory instead
/// leaves this empty — see <see cref="UsesWorkingDirectory"/>.</param>
/// </summary>
public sealed record TerminalOption(
    string Id,
    string Name,
    string Command,
    IReadOnlyList<string> Arguments)
{
    /// <summary>
    /// How this terminal is told to run a command rather than a shell.
    ///
    /// **A .desktop entry with Terminal=true was launched with no console.**
    /// vim, nano and htop all ship such entries and all register against
    /// text/plain, so they appeared in "Open with" for any text file — and
    /// spawning their Exec directly gave them no tty. The process started,
    /// Process.Start returned non-null so the launch reported success, and
    /// nothing ever appeared: vim exits at once off a tty, htop lingers
    /// invisibly.
    ///
    /// "-e" is the near-universal spelling and so the default for a terminal we
    /// do not recognise. The exceptions are why this is a list rather than a
    /// flag: gnome-terminal and xfce4-terminal both take a single STRING after
    /// -e, so passing an argv there would run only the first word, and kitty
    /// takes the command positionally with no flag at all.
    /// </summary>
    public IReadOnlyList<string> RunArguments { get; init; } = ["-e"];

    /// <summary>
    /// True when the folder is passed by starting the process IN it rather
    /// than as an argument. Both are needed: cmd and PowerShell have no
    /// "start here" flag and inherit the working directory, while Windows
    /// Terminal ignores the inherited one and needs `-d`.
    /// </summary>
    public bool UsesWorkingDirectory => Arguments.Count == 0;

    /// <summary>The user's chosen default, marked so the menu can show which
    /// one F4 will open.</summary>
    public bool IsPreferred { get; init; }
}

/// <summary>
/// Handing a file to whatever the desktop thinks should open it. Deliberately
/// tiny and platform-agnostic: the desktop database and xdg-open on Linux,
/// ShellExecute and the shell's own handler list on Windows.
/// </summary>
public interface IApplicationLauncher
{
    /// <summary>
    /// Open with the user's default application for the type.
    ///
    /// **This returned void, and every caller had to pretend the launch
    /// worked.** Both launchers caught what went wrong and dropped it —
    /// WindowsLauncher through <c>Quiet.Swallowed("launcher", ex)</c>, which
    /// prints only under VAKTARI_QUIET_DEBUG, LinuxLauncher through a bare
    /// catch — so double-clicking a row whose file had been deleted since the
    /// listing was drawn did nothing whatsoever: no window, no message, no
    /// clue that anything had been attempted.
    /// </summary>
    /// <returns>Null when the desktop accepted the request, and the failure
    /// otherwise, for <see cref="Failures.Describe"/> to put into words. Null
    /// is not a promise the application opened: the request is handed to the
    /// shell, and what it does after that is out of reach.</returns>
    Exception? Open(string path);

    /// <summary>Open the preferred terminal with its working directory set to
    /// this folder. What F4 does.</summary>
    void OpenTerminal(string directory);

    /// <summary>
    /// Open one specific terminal here, from the menu.
    ///
    /// Default implementation so a platform that has not been taught to
    /// enumerate terminals still honours the call rather than doing nothing:
    /// with an empty <see cref="Terminals"/> the menu never offers a choice,
    /// and this can only be reached with a stale one.
    /// </summary>
    void OpenTerminal(string directory, TerminalOption terminal) => OpenTerminal(directory);

    /// <summary>
    /// Whether this desktop has a way to run something with administrator
    /// rights that a file manager should be using.
    ///
    /// **This used to answer "false is the honest answer nearly everywhere",
    /// and gave the reason: the freedesktop world has pkexec and sudo, which
    /// are a policy question rather than a menu entry, and a file manager
    /// quietly deciding to elevate on a Linux desktop is not a decision it
    /// should be making.** The second half of that still holds and is why
    /// nothing here elevates quietly. The first half was wrong about pkexec,
    /// and the cost of being wrong was that Linux had neither of the two
    /// entries Windows has.
    ///
    /// pkexec decides nothing. It hands the request to polkit, which puts up
    /// the SYSTEM's own authentication dialog, refuses on its own rules, and
    /// answers to the machine's policy rather than to us — which is the same
    /// arrangement as the runas verb handing a request to Windows' consent
    /// dialog. sudo really would have been a policy question, because sudoers
    /// is one and there is no dialog; pkexec is not, and the two were being
    /// treated as one thing.
    ///
    /// False where the machine has no pkexec, so the rows disappear rather than
    /// being offered and failing.
    /// </summary>
    bool CanElevate => false;

    /// <summary>
    /// Runs a file as administrator. The system asks for consent; this never
    /// obtains rights of its own.
    /// </summary>
    void OpenElevated(string path) { }

    /// <summary>Opens a terminal here, elevated.</summary>
    void OpenElevatedTerminal(string directory, TerminalOption? terminal = null) { }

    /// <summary>
    /// Starts THIS program again with these arguments, elevated, and waits for
    /// it to finish.
    ///
    /// **This program and no other**, which is the whole reason it is not
    /// <see cref="OpenElevated"/> with a path. That one hands the system a file
    /// the person pointed at and forgets about it; this one is how the file
    /// manager gets work done with rights it does not have, so what is started
    /// has to be the binary already running and the arguments have to be an
    /// argv rather than anything a shell would look at.
    ///
    /// Waits, unlike every other launch here, because the answer matters: the
    /// exit code is the only thing an elevated file operation can say back
    /// through Windows' consent verb, which forbids redirecting its output.
    ///
    /// Null means it never ran at all — declined at the system's prompt, or no
    /// elevation route on this machine. A decline is an answer and not a
    /// failure, which is why it is not an exception and not a code.
    ///
    /// False-by-default in step with <see cref="CanElevate"/>: a platform that
    /// cannot elevate answers null and the offer is never made.
    /// </summary>
    ValueTask<int?> RunSelfElevatedAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
        => ValueTask.FromResult<int?>(null);

    /// <summary>
    /// Whether starting THIS file elevated would do anything.
    ///
    /// **The question is the platform's, and it was being answered in the view
    /// model** — by a list of Windows file extensions, which is the right
    /// answer on Windows and no answer at all on a desktop where an executable
    /// usually has no extension at all. Left there, "Run as administrator"
    /// could never have appeared on Linux however loudly <see cref="CanElevate"/>
    /// said yes.
    ///
    /// False by default, in step with <see cref="CanElevate"/>: a desktop with
    /// no elevation route has no files it could elevate.
    /// </summary>
    bool CanElevateFile(string path) => false;

    /// <summary>
    /// The terminals installed on this machine, preferred first. Empty where
    /// the platform cannot tell, which leaves the menu with a single entry and
    /// the old fall-through behaviour behind it.
    ///
    /// **Detected once and cached by the implementation.** This is read while
    /// building a context menu, on the UI thread, and probing the disk for a
    /// dozen executables there is exactly the sort of thing that makes a menu
    /// feel slow to open.
    /// </summary>
    IReadOnlyList<TerminalOption> Terminals => [];

    /// <summary>
    /// Every application registered as able to open this file, default first.
    /// Empty if the desktop provides no way to enumerate them.
    /// </summary>
    IReadOnlyList<LaunchOption> GetOpenWithOptions(string path);

    /// <summary>Open with one specific application from GetOpenWithOptions.</summary>
    void OpenWith(string path, LaunchOption option);

    /// <summary>
    /// Whether the menu should offer a way to pick an application that is not
    /// in the list.
    ///
    /// **This described one of the two ways there are, and so answered for one
    /// platform.** It said "Windows has its own dialog", which is true, and
    /// defaulted off for everyone else — so Linux, which has no such dialog to
    /// hand off to, could never say yes however many applications it could
    /// enumerate. A file whose type nothing claims got an "Open with" submenu
    /// with nothing in it and no way out of it.
    ///
    /// Either route counts: the system's own chooser, shown by
    /// <see cref="ChooseApplication"/>, or a list of everything installed in
    /// <see cref="AllApplications"/> for a chooser Vaktari draws itself.
    ///
    /// Still defaulted off, and still for the original reason: a platform with
    /// neither shows no entry, rather than an entry that does nothing.
    /// </summary>
    bool CanChooseApplication => false;

    /// <summary>
    /// Shows the SYSTEM'S chooser. False when there is none to show, which is
    /// the answer a platform gives when <see cref="AllApplications"/> is how it
    /// means to be asked instead.
    /// </summary>
    bool ChooseApplication(string path) => false;

    /// <summary>
    /// Every application installed, for a chooser Vaktari draws ITSELF.
    ///
    /// Empty where <see cref="ChooseApplication"/> shows the platform's own,
    /// which is the better answer wherever there is one: Windows' dialog
    /// browses for an executable and writes the association the rest of the
    /// system reads, and neither is reproducible from here.
    ///
    /// Not filtered by what the file is. That is what
    /// <see cref="GetOpenWithOptions"/> already answers, and this is the way
    /// out of it — a list narrowed by the same rule would offer the same
    /// nothing on the type that has nobody registered against it.
    ///
    /// **Detected once and cached by the implementation**, for the same reason
    /// <see cref="Terminals"/> is: on a desktop this means reading every
    /// .desktop file the machine has, and doing that per right-click is what
    /// makes a menu feel slow to open.
    /// </summary>
    IReadOnlyList<LaunchOption> AllApplications => [];
}
