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
    /// <summary>Open with the user's default application for the type.</summary>
    void Open(string path);

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
    /// Whether this desktop can offer to pick an application that is not in the
    /// list — Windows has its own "How do you want to open this file?" dialog,
    /// which includes browsing for an executable and remembering the choice.
    ///
    /// Defaulted off so a platform without one shows no entry, rather than an
    /// entry that does nothing.
    /// </summary>
    bool CanChooseApplication => false;

    /// <summary>Shows that chooser. False when it could not be shown.</summary>
    bool ChooseApplication(string path) => false;
}
