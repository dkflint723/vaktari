using System.Diagnostics;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

public sealed class LinuxLauncher : IApplicationLauncher
{
    public void Open(string path) => Spawn("xdg-open", path);

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

    /// <summary>One candidate, the way it asks to be started.</summary>
    private static bool Spawn(string directory, TerminalOption terminal)
    {
        if (terminal.UsesWorkingDirectory) return TrySpawnIn(directory, terminal.Command);

        var args = terminal.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        return TrySpawn(terminal.Command, args);
    }

    private static bool TrySpawnIn(string directory, string exe)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };

            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void Spawn(string exe, params string[] args) => TrySpawn(exe, args);

    private static bool TrySpawn(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            // Detached: the file manager closing must not take the opened
            // application with it.
            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
