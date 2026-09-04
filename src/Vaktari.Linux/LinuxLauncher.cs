using System.Diagnostics;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

public sealed class LinuxLauncher : IApplicationLauncher
{
    /// <summary>
    /// The desktop's own opener, with its refusal handed back rather than
    /// dropped.
    ///
    /// **This went through a bare catch that returned nothing.** A machine
    /// with no xdg-open on PATH — a bare container, a session put together by
    /// hand — opened nothing and said nothing, because the only caller had a
    /// void to read.
    ///
    /// What this CANNOT see is a file that has vanished: xdg-open is a
    /// program, so it starts, and whatever it makes of the path it was given
    /// it makes after this has returned. Only the spawn itself is visible from
    /// here.
    /// </summary>
    public Exception? Open(string path) => SpawnFailure(_opener, path);

    private string _opener = "xdg-open";

    /// <summary>What Open will run. Readable so the default, and the stand-in
    /// that replaces it, are both pinned from a machine that has neither.
    /// </summary>
    internal string Opener => _opener;

    /// <summary>
    /// Stands in for the desktop's opener, in the same shape as
    /// <see cref="UseTerminals"/>.
    ///
    /// The state that matters is the opener refusing to start, and it cannot
    /// be produced on a machine that has xdg-open — while calling the real one
    /// in a test opens whatever it was handed. A name that is not a program
    /// produces the refusal on any agent, including the Windows one this
    /// assembly's tests also run on.
    /// </summary>
    internal void UseOpener(string program) => _opener = program;

    public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        => DesktopEntries.ForFile(path);

    public void OpenWith(string path, LaunchOption option)
    {
        // Fall back to the default handler rather than doing nothing if the
        // desktop file has gone missing since the menu was built.
        if (!DesktopEntries.Launch(option.Id, path))
            Open(path);
    }

    /// <summary>
    /// The terminals on PATH, the user's choice first — the same list the old
    /// fall-through chain walked, now offered rather than guessed at.
    ///
    /// $TERMINAL still comes first when it is set: it is the desktop's own way
    /// of saying which terminal to use, and a setting inside one application
    /// has no business overruling it.
    /// </summary>
    /// Detection only: the user's preference is applied above this, because
    /// settings live in the UI assembly and this one sits underneath it.
    public IReadOnlyList<TerminalOption> Terminals => _terminals ??= Detect();

    private IReadOnlyList<TerminalOption>? _terminals;

    /// <summary>
    /// Stands in for detection.
    ///
    /// The state that mattered — a detected terminal that refuses to start —
    /// cannot be produced on a real machine on request, and it is the only
    /// state in which OpenTerminal used to recurse into itself until the stack
    /// ran out. A list of names that are not programs reproduces it exactly.
    /// </summary>
    internal void UseTerminals(IReadOnlyList<TerminalOption> terminals)
        => _terminals = terminals;

    /// <summary>
    /// Probed once. This is read while a context menu is built, on the UI
    /// thread, and a PATH walk per candidate is what makes a menu feel slow.
    /// </summary>
    private static IReadOnlyList<TerminalOption> Detect()
    {
        var found = new List<TerminalOption>();

        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } wanted
            && OnPath(wanted) is { } path)
        {
            // **Its own flags if we recognise it, and none if we do not.** This
            // handed every $TERMINAL Konsole's --workdir, so TERMINAL=alacritty
            // produced "alacritty --workdir /path", which alacritty rejects —
            // the one setting whose whole purpose is to honour the user's choice
            // was the one that could not open their terminal. With no arguments
            // the folder still arrives, as the directory the process starts in.
            var known = Known.FirstOrDefault(k =>
                path.EndsWith("/" + k.Exe, StringComparison.Ordinal));

            // Same rule for the run flags: its own if we know it, and the
            // near-universal -e if we do not.
            found.Add(new TerminalOption("terminal-env", wanted, path, known.Args ?? [])
            {
                RunArguments = known.Run ?? ["-e"],
            });
        }

        // **The desktop's own setting, which was read by nothing.** Plasma
        // has had a terminal preference since forever and puts it in
        // kdeglobals; a KDE user who set it to Alacritty still got Konsole,
        // because Konsole is simply first in the list below. $TERMINAL comes
        // first of the two — it is the more explicit choice, and the one a
        // person sets per session — but a desktop preference beats a list of
        // guesses.
        if (found.Count == 0
            && DesktopTerminal() is { Length: > 0 } chosen
            && OnPath(chosen) is { } fromDesktop)
        {
            var known = Known.FirstOrDefault(k =>
                fromDesktop.EndsWith("/" + k.Exe, StringComparison.Ordinal));

            found.Add(new TerminalOption("terminal-desktop", chosen, fromDesktop, known.Args ?? [])
            {
                RunArguments = known.Run ?? ["-e"],
            });
        }

        foreach (var (id, name, exe, args, run) in Known)
        {
            if (found.Any(t => t.Command.EndsWith("/" + exe, StringComparison.Ordinal))) continue;
            if (OnPath(exe) is not { } located) continue;

            found.Add(new TerminalOption(id, name, located, args) { RunArguments = run });
        }

        return found;
    }

    // The last column is how each one is told to RUN something rather than open
    // a shell. Not all "-e": gnome-terminal deprecated its -e and takes a
    // single string, xfce4-terminal's -e is the same shape, and kitty takes the
    // command positionally with no flag at all.
    private static readonly (string Id, string Name, string Exe, string[] Args, string[] Run)[] Known =
    [
        ("konsole",        "Konsole",        "konsole",        ["--workdir", "{dir}"],           ["-e"]),
        ("gnome-terminal", "GNOME Terminal", "gnome-terminal", ["--working-directory", "{dir}"], ["--"]),
        ("alacritty",      "Alacritty",      "alacritty",      ["--working-directory", "{dir}"], ["-e"]),
        ("kitty",          "kitty",          "kitty",          ["--directory", "{dir}"],         []),
        ("wezterm",        "WezTerm",        "wezterm",        ["start", "--cwd", "{dir}"],      ["start", "--"]),
        ("foot",           "foot",           "foot",           ["--working-directory={dir}"],    ["-e"]),
        ("xfce4-terminal", "Xfce Terminal",  "xfce4-terminal", ["--working-directory={dir}"],    ["-x"]),

        // **Everything below was missing, and between them they are the
        // default terminal on a good many desktops.** Ptyxis ships as GNOME's
        // on recent Fedora, Terminator and Tilix are what people install when
        // they want splits, Ghostty is new and spreading quickly, and
        // x-terminal-emulator is the Debian alternatives link that answers when
        // none of the others is installed under its own name. Without them
        // Vaktari fell through to xterm on machines with a perfectly good
        // terminal on them.
        ("ptyxis",         "Ptyxis",         "ptyxis",         ["--working-directory={dir}"],    ["--"]),
        ("ghostty",        "Ghostty",        "ghostty",        ["--working-directory={dir}"],    ["-e"]),
        ("terminator",     "Terminator",     "terminator",     ["--working-directory={dir}"],    ["-x"]),
        ("tilix",          "Tilix",          "tilix",          ["--working-directory={dir}"],    ["-e"]),
        ("mate-terminal",  "MATE Terminal",  "mate-terminal",  ["--working-directory={dir}"],    ["-e"]),
        ("lxterminal",     "LXTerminal",     "lxterminal",     ["--working-directory={dir}"],    ["-e"]),
        ("qterminal",      "QTerminal",      "qterminal",      ["--workdir", "{dir}"],           ["-e"]),

        // Last two, and in this order: the alternatives link is whatever the
        // machine chose, which is a better answer than xterm and a worse one
        // than any terminal named above. It guarantees only -e, and no working
        // directory flag — which costs nothing, because the folder arrives as
        // the directory the process is started in.
        ("x-terminal-emulator", "Terminal",  "x-terminal-emulator", [],                          ["-e"]),
        ("xterm",          "xterm",          "xterm",          [],                               ["-e"]),
    ];

    /// <summary>
    /// The terminal the desktop is configured to use, or null.
    ///
    /// Plasma's, because Plasma is the desktop with a setting for this — GNOME
    /// dropped its equivalent, and the others never had one. Read straight out
    /// of kdeglobals rather than through kreadconfig5, which is a process per
    /// call and is not installed outside KDE.
    ///
    /// A ".desktop" suffix is trimmed: Plasma 6 records the entry name there
    /// where Plasma 5 recorded a command, and the two differ by exactly that.
    /// </summary>
    internal static string? DesktopTerminal()
    {
        try
        {
            var file = Path.Combine(ConfigHome(), "kdeglobals");

            if (!File.Exists(file)) return null;

            var general = false;

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    // Only the [General] group: the key name appears in others
                    // — a per-application override among them — and taking the
                    // first match anywhere reads somebody else's setting.
                    general = line.Equals("[General]", StringComparison.Ordinal);
                    continue;
                }

                if (!general || !line.StartsWith("TerminalApplication", StringComparison.Ordinal))
                    continue;

                var equals = line.IndexOf('=');
                if (equals < 0) continue;

                var value = line[(equals + 1)..].Trim();

                if (value.EndsWith(".desktop", StringComparison.Ordinal))
                    value = value[..^8];

                return value.Length > 0 ? value : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("terminal", e);
        }

        return null;
    }

    /// <summary>Where the desktop keeps its configuration. A seam, because the
    /// suite runs where there is no such directory.</summary>
    internal static Func<string>? ConfigHomeOverride { get; set; }

    private static string ConfigHome()
    {
        if (ConfigHomeOverride is { } given) return given();

        return Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } set
            ? set
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    private static string? OnPath(string exe)
    {
        if (exe.Contains('/')) return File.Exists(exe) ? exe : null;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (dir.Length == 0) continue;

            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public void OpenTerminal(string directory)
    {
        if (Terminals.FirstOrDefault() is { } preferred)
        {
            OpenTerminal(directory, preferred);
            return;
        }

        // Nothing detected. xterm without a working-directory flag still lands
        // in the right place through the shell.
        TrySpawn("xterm", "-e", "cd " + directory + " && $SHELL");
    }

    public void OpenTerminal(string directory, TerminalOption terminal)
    {
        if (Spawn(directory, terminal)) return;

        // **Not OpenTerminal(directory), which is where this recursed.** That
        // overload picks Terminals.FirstOrDefault() and calls straight back
        // here; the list is cached for the life of the process, so a preferred
        // terminal that refuses to start produced the same choice every time
        // and the two methods called each other until the stack ran out — F4
        // took the whole application down with it. WindowsLauncher carries the
        // same note, having been given this fix already; the Linux copy never
        // was, and there was no test project here to notice.
        //
        // The remaining candidates, tried once each, then the one that needs no
        // detection at all.
        foreach (var other in Terminals)
        {
            if (other.Id == terminal.Id) continue;

            if (Spawn(directory, other)) return;
        }

        // xterm without a working-directory flag still lands in the right place
        // through the shell.
        if (TrySpawn("xterm", "-e", "cd " + directory + " && $SHELL")) return;

        // Said out loud. A terminal that never opens and never explains reads
        // as the key doing nothing at all.
        Console.Error.WriteLine(
            $"[vaktari] no terminal would start for {directory} — "
            + $"tried {Terminals.Count} detected and xterm");
    }

    // ---- administrator ------------------------------------------------------

    /// <summary>
    /// Where pkexec is, or null on a machine without it.
    ///
    /// **Probed once**, for the same reason the terminal list is: this is read
    /// while a context menu is being built, on the UI thread, and a PATH walk
    /// per right-click is exactly what makes a menu feel slow to open. A miss
    /// is remembered too — a machine with no pkexec is the one that would
    /// otherwise walk the whole PATH every time.
    /// </summary>
    private string? PkExec
    {
        get
        {
            if (_probedPkExec) return _pkexec;

            _probedPkExec = true;
            return _pkexec = OnPath("pkexec");
        }
    }

    private string? _pkexec;
    private bool _probedPkExec;

    /// <summary>
    /// Stands in for that probe, both ways round.
    ///
    /// Both answers have to be pinned — the menu rows appear on one and must
    /// vanish on the other — and neither can be arranged by asking a machine
    /// politely, least of all the Windows one where most of this is written.
    /// </summary>
    internal void UsePkexec(string? path)
    {
        _pkexec = path;
        _probedPkExec = true;
    }

    /// <summary>
    /// **This answered false, and the interface used to explain why: pkexec and
    /// sudo were "a policy question rather than a menu entry".** Windows had
    /// "Run as administrator" and an admin terminal; Linux had neither, on that
    /// reasoning — which does not survive being written down. sudo really is a
    /// policy question: sudoers is one, and there is no dialog. pkexec decides
    /// nothing. It hands the request to polkit, which shows the system's own
    /// authentication dialog and answers to the machine's policy, exactly as
    /// the runas verb hands a request to Windows' consent dialog. The two were
    /// being treated as one thing, and Linux paid for it.
    ///
    /// False where there is no pkexec, which is what makes the rows disappear
    /// on such a machine rather than appear and fail.
    /// </summary>
    public bool CanElevate => PkExec is not null;

    /// <summary>
    /// Whether pkexec would have anything to run.
    ///
    /// **The execute bit, because on this platform that is the whole of the
    /// question.** The rule this replaces was a list of Windows extensions, and
    /// the file a Linux user most wants this for — a system binary, a build
    /// script, an installer unpacked from a tarball — usually has no extension
    /// at all.
    /// </summary>
    public bool CanElevateFile(string path)
    {
        if (PkExec is null) return false;

        try
        {
            // File.Exists, which is already false for a directory: pkexec has
            // nothing to do with a folder, and a folder is the thing a person
            // is most likely to have selected.
            if (!File.Exists(path)) return false;

            return Runnable(File.GetUnixFileMode(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or PlatformNotSupportedException)
        {
            Vaktari.Core.Quiet.Swallowed("launcher", e);
            return false;
        }
    }

    /// <summary>
    /// **Any of the three bits, not the owner's.** The binaries this verb
    /// exists for are the root-owned ones — 0755, owner root — so asking
    /// whether the OWNER may run it answers for root rather than for the person
    /// at the keyboard. Asking whether ANYONE may run it is the question that
    /// matches what pkexec then does, which is to run it as root.
    /// </summary>
    internal static bool Runnable(UnixFileMode mode)
        => (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute)) != 0;

    /// <summary>
    /// Runs a file with root rights, in a terminal.
    ///
    /// **In a terminal because pkexec unsets DISPLAY and XAUTHORITY**, which is
    /// its own documented refusal to launch X11 applications for you. So the
    /// thing started this way has no window; without a console it has nowhere
    /// to print its output either, and nowhere to say why it stopped — the same
    /// fault this repository already fixed for Terminal=true desktop entries,
    /// where a spawned vim exited instantly off a tty and the launch still
    /// reported success.
    ///
    /// Vaktari never acquires rights of its own: polkit authenticates and
    /// decides, a refusal ends with the program not running, and this process
    /// stays exactly as privileged as it was.
    /// </summary>
    public void OpenElevated(string path)
    {
        if (PkExec is not { } pkexec) return;

        var directory = Path.GetDirectoryName(path) ?? "/";

        foreach (var candidate in Candidates(null))
        {
            if (TrySpawnIn(directory, Elevated(pkexec, candidate, [path]))) return;
        }

        // No terminal on this machine would start, and running it anyway is
        // better than doing nothing: a desktop session with a polkit agent
        // registered still gets its graphical prompt, because that agent is a
        // session service and has nothing to do with our terminal. What is lost
        // is the program's own output, and pkexec's text prompt on a session
        // with no agent at all — which is the whole reason a terminal is tried
        // first.
        //
        // Through Elevated with no terminal rather than spelling the two words
        // out here: written inline, the no-terminal branch of that method would
        // be reachable from nowhere and so pinned by nothing.
        TrySpawnIn(directory, Elevated(pkexec, terminal: null, [path]));
    }

    /// <summary>
    /// A terminal here, running a root shell.
    ///
    /// **The terminal is ours and the shell inside it is root's, not the other
    /// way round.** "pkexec konsole" is the obvious spelling and it does not
    /// work: pkexec unsets DISPLAY and XAUTHORITY, so the terminal has no
    /// display to open on. Running pkexec INSIDE an ordinary terminal also
    /// leaves the window itself unprivileged, which is the arrangement every
    /// Linux desktop asks for.
    /// </summary>
    public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null)
    {
        if (PkExec is not { } pkexec) return;

        var shell = ShellFor(Environment.GetEnvironmentVariable("SHELL"));

        foreach (var candidate in Candidates(terminal))
        {
            if (TrySpawnIn(directory, Elevated(pkexec, candidate, [shell]))) return;
        }

        // Said out loud, like the unelevated one: a terminal that never opens
        // and never explains reads as the entry doing nothing at all.
        Console.Error.WriteLine(
            $"[vaktari] no terminal would start an elevated shell in {directory} — "
            + $"tried {Terminals.Count} detected and xterm");
    }

    /// <summary>
    /// The named terminal first, then the rest, then the one that needs no
    /// detection.
    ///
    /// The same walk OpenTerminal does, and for the same reason: the list is
    /// cached for the life of the process, so a preferred terminal that refuses
    /// to start refuses every time, and asking it twice is how the unelevated
    /// pair used to recurse until the stack ran out.
    /// </summary>
    private IEnumerable<TerminalOption> Candidates(TerminalOption? first)
    {
        if (first is not null) yield return first;

        foreach (var other in Terminals)
        {
            if (first is not null && other.Id == first.Id) continue;

            yield return other;
        }

        yield return LastResort;
    }

    private static readonly TerminalOption LastResort = new("xterm", "xterm", "xterm", []);

    /// <summary>
    /// The argv for running something with root rights, inside a terminal.
    ///
    /// Pure and separate for the same reason DesktopEntries.InTerminal is: the
    /// run flag differs per terminal in ways that cannot be guessed, and the
    /// shape is the whole of what can go wrong.
    ///
    /// **The terminal's working-directory ARGUMENTS are deliberately absent.**
    /// The folder arrives as the directory the process is started in, which
    /// every one of them honours, and concatenating the two lists produces
    /// nonsense on at least one: WezTerm opens with ["start", "--cwd", dir] and
    /// runs with ["start", "--"], so a joined argv says "start" twice.
    /// </summary>
    private static IReadOnlyList<string> Elevated(
        string pkexec, TerminalOption? terminal, IReadOnlyList<string> command)
        => terminal is null
            ? [pkexec, .. command]
            : [terminal.Command, .. terminal.RunArguments, pkexec, .. command];

    /// <summary>
    /// The shell an elevated terminal opens: the person's own where $SHELL says
    /// so, and /bin/sh otherwise — the one path POSIX guarantees, where
    /// /bin/bash is missing on Alpine and on a good many containers.
    ///
    /// Takes the value rather than reading it, because an environment variable
    /// is process-global and a test that sets one has repointed it for every
    /// other test in the assembly.
    /// </summary>
    internal static string ShellFor(string? shell)
        => string.IsNullOrWhiteSpace(shell) ? "/bin/sh" : shell;

    /// <summary>One candidate, the way it asks to be started.</summary>
    private bool Spawn(string directory, TerminalOption terminal)
    {
        if (terminal.UsesWorkingDirectory) return TrySpawnIn(directory, [terminal.Command]);

        var args = terminal.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        return TrySpawn(terminal.Command, args);
    }

    /// <summary>
    /// Stands in for starting a process, recording the argv instead.
    ///
    /// **The elevated verbs are argv arithmetic and nothing else**, and argv is
    /// the whole of what can be wrong about them: a flag in the wrong place
    /// runs the wrong program, or the person's shell rather than root's. None
    /// of that can be seen by calling the real thing — a passing spawn proves a
    /// terminal opened, not what it was told to run — and the machine most of
    /// this is written on has neither pkexec nor a terminal to test with.
    /// </summary>
    internal Func<string, IReadOnlyList<string>, bool>? SpawnOverride { get; set; }

    /// <summary>The process started IN a folder rather than told about it.</summary>
    private bool TrySpawnIn(string directory, IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) return false;

        if (SpawnOverride is { } stand) return stand(directory, argv);

        try
        {
            var info = new ProcessStartInfo(argv[0])
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };

            for (var i = 1; i < argv.Count; i++) info.ArgumentList.Add(argv[i]);

            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether it started, for the callers that only pick the next candidate.
    /// </summary>
    private static bool TrySpawn(string exe, params string[] args)
        => SpawnFailure(exe, args) is null;

    /// <summary>
    /// The same spawn, keeping the exception instead of reducing it to false.
    ///
    /// Two shapes over one body because the terminal chain genuinely wants a
    /// bool — it has another candidate to try and nothing to say — while
    /// <see cref="Open"/> has nowhere left to fall back to and a status bar to
    /// fill.
    /// </summary>
    private static Exception? SpawnFailure(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            // Detached: the file manager closing must not take the opened
            // application with it.
            using var process = Process.Start(info);

            // A null process is what this read as a failure when it answered
            // bool; it now says the same thing with something a caller can
            // show. Not a mechanism anyone here has produced, so the message
            // describes no cause — but it reaches Failures.Describe's
            // IOException arm verbatim, so it has to be a sentence rather than
            // a note to a programmer.
            return process is null
                ? new IOException("the desktop did not start anything")
                : null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
