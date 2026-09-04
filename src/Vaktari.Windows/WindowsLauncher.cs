using System.Diagnostics;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// Opening things the way the shell would. Markedly simpler than the Linux side,
/// which parses .desktop files and the mime database by hand: here the shell
/// already knows, and <c>UseShellExecute</c> asks it.
/// </summary>
public sealed class WindowsLauncher : IApplicationLauncher
{
    /// <summary>
    /// Windows has its own chooser, so this offers that rather than a
    /// home-made one.
    ///
    /// SHOpenWithDialog is the dialog the shell shows for "Open with > Choose
    /// another app": it lists what is installed, offers "Look for another app
    /// on this PC" to browse, and can make the choice permanent. Reproducing
    /// any of that would be worse in every respect, and would not update the
    /// association the rest of the system reads.
    /// </summary>
    public bool CanChooseApplication => true;

    public bool ChooseApplication(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        var shown = false;

        // The shell wants an STA, exactly as IAssocHandler.Invoke and the
        // property sheet do. Joined, unlike the property sheet: this dialog is
        // modal and returns when it closes, so there is nothing to keep alive
        // afterwards and the caller wants to know whether it ran.
        var thread = new Thread(() => shown = ShowOnThisThread(path)) { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return shown;
    }

    private static bool ShowOnThisThread(string path)
    {
        var info = new Native.OpenAsInfo
        {
            FileName = path,
            ClassName = null,

            // EXEC opens the file once something is chosen — without it the
            // dialog sets the association and does nothing, which reads as the
            // menu entry having failed.
            //
            // ALLOW_REGISTRATION is what puts "Always use this app" on it. That
            // is the whole point of choosing: a chooser that forgets is a
            // one-shot launcher.
            Flags = Native.OpenAsFlags.Exec | Native.OpenAsFlags.AllowRegistration,
        };

        var hr = Native.SHOpenWithDialog(IntPtr.Zero, ref info);

        // The user pressing Cancel comes back as ERROR_CANCELLED, which is not
        // a failure worth reporting anywhere.
        if (hr == 0 || hr == unchecked((int)0x800704C7)) return true;

        Console.Error.WriteLine($"[vaktari] open-with chooser refused: 0x{hr:X8}");
        return false;
    }

    /// <summary>
    /// The folder a started program should run in — its own.
    ///
    /// Empty when the path has no folder, which leaves the inherited one:
    /// better than pointing a process at a directory that does not exist.
    /// </summary>
    private static string WorkingDirectoryFor(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException
                                    or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>
    /// ERROR_FILE_NOT_FOUND, as ShellExecute reports it.
    ///
    /// Measured on Windows 11: starting a path that is no longer there raises
    /// Win32Exception with NativeErrorCode 2 and HResult 0x80004005 — E_FAIL,
    /// which carries no code at all, so the NativeErrorCode is the only thing
    /// worth matching on.
    /// </summary>
    private const int FileNotFound = 2;

    /// <summary>
    /// ERROR_CANCELLED. <c>OpenElevated</c> below already treats this as an
    /// answer rather than an error, and the same holds here: a plain
    /// double-click can raise the consent dialog on its own, because the shell
    /// elevates a program whose manifest asks it to. Saying no is a decision,
    /// and a status line under it would be arguing with the person.
    /// </summary>
    private const int Cancelled = 1223;

    /// <summary>
    /// What a refused launch should be reported as.
    ///
    /// Pure and separate, the way RecycleRefusal is, and for the same reason:
    /// these codes are the whole of what can be wrong here, and neither of the
    /// two below can be produced from a test by asking the shell politely —
    /// ERROR_CANCELLED needs somebody to press No on a dialog.
    /// </summary>
    internal static Exception? Refusal(Exception failure, string path) => failure switch
    {
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == Cancelled => null,

        // The TYPE, not the message, exactly as RecycleRefusal does it:
        // Failures.Describe already matches FileNotFoundException and supplies
        // the sentence used everywhere else in the application. Win32's own
        // message is no use in a status bar — measured, it reads "An error
        // occurred trying to start process 'C:\…\notes.txt' with working
        // directory 'C:\…'. The system cannot find the file specified.",
        // which names the path twice and talks about processes to somebody who
        // double-clicked a row.
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == FileNotFound
            => new FileNotFoundException(w.Message, path, w),

        _ => failure,
    };

    /// <summary>
    /// Hands the file to the shell and says whether the shell took it.
    ///
    /// **The failure used to be swallowed here.** Every exception went to
    /// Quiet.Swallowed, which prints nothing unless VAKTARI_QUIET_DEBUG is
    /// set — so a double-click on a file that had been deleted since the
    /// listing was drawn produced no window and no message. Quiet is for
    /// failures nobody needs to know about, and its own summary says so; this
    /// is not one.
    ///
    /// Measured on Windows 11, so the caller knows what it will and will not
    /// hear about. A path that is gone raises Win32Exception with
    /// NativeErrorCode 2. A file with an extension nothing is registered for
    /// raises nothing at all: the shell puts up its own "How do you want to
    /// open this file?" chooser, and when that was dismissed Process.Start
    /// returned null. So the unopenable file does not reach the status bar,
    /// and should not — the shell is already asking the user about it.
    /// </summary>
    public Exception? Open(string path)
    {
        try
        {
            // UseShellExecute is what makes this ShellExecute rather than
            // CreateProcess — without it, opening a .txt tries to execute it.
            //
            // **The working directory is the file's own folder**, which this
            // was the only launcher in the class not to set: OpenElevated,
            // Elevate and Start all do. Without it a started program inherits
            // Vaktari's working directory, so a portable .exe or a .bat that
            // reads a file sitting beside it fails — and the failure looks like
            // the program being broken rather than how it was started.
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = WorkingDirectoryFor(path),
            })?.Dispose();

            return null;
        }
        catch (Exception ex)
        {
            return Refusal(ex, path);
        }
    }

    /// <summary>
    /// What this machine has, in the order a Windows user most likely wants.
    ///
    /// **Detection only — the user's preference is applied above this.**
    /// Settings live in the UI assembly, which references this one and not the
    /// other way round; a launcher reaching for them would invert that.
    ///
    /// Read while a context menu is being built, so the detection behind it is
    /// cached: a dozen file probes on the UI thread is what makes a menu feel
    /// slow to open.
    /// </summary>
    public IReadOnlyList<TerminalOption> Terminals => InstalledTerminals.All();

    /// <summary>
    /// F4, and the plain menu entry: the chosen terminal, or the first one
    /// found if that choice is gone or was never made.
    ///
    /// **Falls back to the old chain when detection finds nothing.** A machine
    /// with a terminal somewhere none of the probes look must still get a
    /// terminal, and "no entries were detected" is not the same fact as "there
    /// is nothing installed".
    /// </summary>
    public void OpenTerminal(string directory)
    {
        // One direction only. The reverse call — the named overload falling
        // back to this one — is what used to recurse; it now walks the rest of
        // the list itself.
        if (Terminals.FirstOrDefault() is { } preferred)
        {
            OpenTerminal(directory, preferred);
            return;
        }

        foreach (var (program, arguments) in new (string, string[])[]
        {
            ("wt.exe", ["-d", directory]),
            ("pwsh.exe", []),
            ("powershell.exe", []),
            ("cmd.exe", []),
        })
        {
            if (Start(program, arguments, directory)) return;
        }
    }

    /// <summary>
    /// Windows has the verb and owns the consent dialog, so this is a real
    /// answer here.
    /// </summary>
    public bool CanElevate => true;

    /// <summary>
    /// **Only for things Windows can actually start elevated.** The runas verb
    /// on a .txt does nothing at all — no error, no elevation, no editor — so
    /// offering it for every file would be an entry that silently fails on most
    /// of them. This is the set Explorer itself offers it for.
    ///
    /// **Moved here from PaneViewModel**, where a list of Windows extensions
    /// was deciding this for every platform — and so deciding "no" for Linux,
    /// which has pkexec and, once it had it, still had nothing to offer it for.
    /// </summary>
    public bool CanElevateFile(string path)
        => Startable.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> Startable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".bat", ".cmd", ".ps1", ".com", ".lnk", ".msc", ".vbs", ".reg",
        };

    /// <summary>
    /// Runs a file as administrator, through the shell's own "runas" verb.
    ///
    /// **Vaktari never acquires rights of its own.** The verb asks the SYSTEM
    /// to start a new process elevated, and the system shows its consent dialog
    /// and makes the decision. Nothing here can bypass that, and nothing here
    /// should try — the file manager stays unelevated whatever the answer is.
    ///
    /// Declining raises ERROR_CANCELLED, which is a person saying no rather
    /// than a fault, so it is swallowed like any other cancelled dialog.
    /// </summary>
    public void OpenElevated(string path)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            // ERROR_CANCELLED among them: the consent dialog was declined, which
            // is an answer and not an error.
            Quiet.Swallowed("launcher", ex);
        }
    }

    /// <summary>
    /// A terminal here, elevated — the same consent dialog, and the same
    /// refusal to hold rights of our own.
    /// </summary>
    public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null)
    {
        var chosen = terminal ?? Terminals.FirstOrDefault();

        if (chosen is null)
        {
            // Nothing detected is not the same as nothing installed, and cmd is
            // on every Windows machine there has ever been.
            Elevate("cmd.exe", [], directory);
            return;
        }

        var arguments = chosen.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        Elevate(chosen.Command, arguments, directory);
    }

    private static void Elevate(string program, IReadOnlyList<string> arguments, string directory)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = directory,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var started = Process.Start(info);
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
        }
    }

    /// <summary>
    /// How this program is started again with rights of its own: through the
    /// same consent verb every other elevation here uses, and never through a
    /// shell.
    ///
    /// **ArgumentList rather than a command string.** The runtime writes the
    /// line with Windows' own quoting rules — including doubling the
    /// backslashes before a closing quote, which is what a folder path ending
    /// in a separator would otherwise break — and the process being started
    /// reads it back with the same rules. Building the string by hand is how a
    /// file called <c>a" b</c> becomes two arguments.
    ///
    /// **The running binary, by its own path**, so what the consent dialog is
    /// about is this program and not a name resolved through PATH.
    ///
    /// Separate and internal because the interesting part cannot be run in a
    /// test: <c>Process.Start</c> on this raises the consent dialog, and there
    /// is nobody at a test machine to answer it. What CAN be pinned is the
    /// shape — that consent is asked for, and that the arguments are an argv.
    /// </summary>
    internal static ProcessStartInfo ElevatedSelf(
        string self, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(self)
        {
            UseShellExecute = true,
            Verb = "runas",

            // The executable's own folder, not the folder being looked at. The
            // elevated side accepts only fully-qualified paths, so nothing it
            // does depends on this — which is exactly why it should not be
            // somewhere a caller might think it mattered.
            WorkingDirectory = Path.GetDirectoryName(self) ?? "",
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        return info;
    }

    /// <summary>
    /// Starts it, waits, and reports what it exited with.
    ///
    /// Declining the consent dialog raises ERROR_CANCELLED, which is a person
    /// saying no rather than a fault — the same reading <see cref="OpenElevated"/>
    /// already gives it — so it comes back as null and the bar clears.
    /// </summary>
    public async ValueTask<int?> RunSelfElevatedAsync(
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        // NOT PINNED, and it cannot be from here: Environment.ProcessPath is
        // the running process's own path and nothing in a test can move it.
        // It is documented as null for a process with no executable file
        // behind it — a native host that loaded the runtime itself — and the
        // whole of this method is about starting that file again, so there is
        // nothing to do but decline.
        if (Environment.ProcessPath is not { } self) return null;

        try
        {
            using var started = Process.Start(ElevatedSelf(self, arguments));

            if (started is null) return null;

            await started.WaitForExitAsync(ct).ConfigureAwait(false);

            return started.ExitCode;
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
            return null;
        }
    }

    /// <summary>One named terminal, from the menu.</summary>
    public void OpenTerminal(string directory, TerminalOption terminal)
    {
        var arguments = terminal.Arguments
            .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
            .ToArray();

        // The folder reaches it one way or the other: as an argument where the
        // terminal takes one, and as the working directory where it does not.
        if (Start(terminal.Command, arguments, directory)) return;

        // **Not OpenTerminal(directory), which is where this recursed.** That
        // overload picks Terminals.FirstOrDefault() and calls straight back
        // here; the list is cached for the life of the process, so a preferred
        // terminal that refuses to start produced the same choice every time
        // and the two methods called each other until the stack ran out. A
        // failure to open a terminal took the whole application down with it.
        //
        // The remaining candidates, tried once each, then the chain that needs
        // no detection at all.
        foreach (var other in Terminals)
        {
            if (other.Id == terminal.Id) continue;

            var theirs = other.Arguments
                .Select(a => a.Replace("{dir}", directory, StringComparison.Ordinal))
                .ToArray();

            if (Start(other.Command, theirs, directory)) return;
        }

        foreach (var (program, fallback) in new (string, string[])[]
        {
            ("wt.exe", ["-d", directory]),
            ("pwsh.exe", []),
            ("powershell.exe", []),
            ("cmd.exe", []),
        })
        {
            if (Start(program, fallback, directory)) return;
        }
    }

    private static bool Start(string program, IReadOnlyList<string> arguments, string directory)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                WorkingDirectory = directory,
                UseShellExecute = true,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            if (Process.Start(info) is not { } started) return false;

            started.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            // Win32Exception for "not found" is the expected case here, so it
            // is not worth a diagnostic line per candidate.
            Quiet.Swallowed("launcher", ex);
            return false;
        }
    }

    /// <summary>
    /// The shell's own handler list, which is what Explorer's "Open with"
    /// submenu is built from — so the names match what the user already sees
    /// elsewhere on their machine, default first.
    ///
    /// Was empty, on the grounds that this needed COM and COM under NativeAOT
    /// was the risky combination. The interface does permit empty — "empty if
    /// the desktop provides no way to enumerate them" — but that was never
    /// true here; nobody had tested the assumption. See <see cref="AssocHandlers"/>.
    /// </summary>
    public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path)
        => AssocHandlers.For(path);

    /// <summary>
    /// Hands the file to the chosen handler, and falls back to the shell's
    /// picker if that cannot be done.
    ///
    /// The fallback matters more than it looks: the option was built from a
    /// list that may be a minute old, so the application behind it can have
    /// been uninstalled since the menu opened. Showing the picker is then still
    /// what the user asked for, one dialog further along.
    /// </summary>
    public void OpenWith(string path, LaunchOption option)
    {
        if (!string.IsNullOrEmpty(option.Id) && AssocHandlers.Invoke(path, option.Id)) return;

        try
        {
            var info = new ProcessStartInfo("rundll32.exe") { UseShellExecute = true };
            info.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            info.ArgumentList.Add(path);

            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("launcher", ex);
        }
    }
}
