using System.Diagnostics;

namespace Vaktari.Core.Sharing;

/// <summary>One way to try to install copyparty, and what to call it while it runs.</summary>
/// <param name="File">The executable to run.</param>
/// <param name="Args">Its arguments, unquoted — they go through ArgumentList.</param>
/// <param name="Describe">
/// What the user sees: "pipx install copyparty". A command they could have typed
/// themselves, so a failed install leaves them somewhere to start.
/// </param>
public sealed record InstallAttempt(string File, string[] Args, string Describe);

/// <summary>
/// Where copyparty is, and how to get it — the two parts of running it that
/// genuinely differ per operating system.
///
/// **This is the whole reason CopypartyShare is not itself platform code.**
/// Writing the config, choosing a free port, launching the process, tracking
/// sessions and stopping them again are identical everywhere; what differs is
/// that Linux looks along a colon-separated PATH for `python3` and installs with
/// pipx or pip, while Windows looks along a semicolon-separated one for
/// `python.exe` and has a standalone .exe to find as well. Splitting it here
/// keeps one copy of the part that matters and a short subclass per platform.
/// </summary>
public abstract class CopypartyBackend
{
    /// <summary>
    /// The command to run and any arguments that must come before ours, or
    /// null when copyparty is not installed.
    ///
    /// The prefix exists because copyparty is as often a Python module as an
    /// executable: `("python3", ["-m", "copyparty"])` and `("copyparty", [])`
    /// both run it, and the caller should not have to know which it got.
    /// </summary>
    public abstract (string? Command, string[] Prefix) Locate();

    /// <summary>
    /// What to try, in the order a careful person would — least disturbing to
    /// the system first. An empty list means nothing here can install it.
    /// </summary>
    public abstract IReadOnlyList<InstallAttempt> InstallAttempts();

    /// <summary>Shown when copyparty is missing, ending in something to type.</summary>
    public abstract string NotInstalledHint { get; }

    /// <summary>When nothing on this system could install it.</summary>
    public abstract string NoInstallerHint { get; }

    /// <summary>
    /// When an install reported success but <see cref="Locate"/> still finds
    /// nothing — almost always a PATH that does not include where it landed.
    /// </summary>
    public abstract string InstalledButNotFoundHint { get; }

    /// <summary>When every attempt failed.</summary>
    public abstract string InstallFailedHint { get; }

    /// <summary>
    /// Whether the NEXT attempt exists specifically for this failure, rather
    /// than being another guess.
    ///
    /// It decides one thing: whether to say "failed, trying another way". Every
    /// attempt is tried either way. Linux uses it to recognise PEP 668's
    /// externally-managed-environment, which is exactly what its
    /// --break-system-packages attempt is for — and stays quiet in that case,
    /// because the next attempt is a targeted answer rather than the generic
    /// flailing that message describes.
    ///
    /// False by default: most platforms have no failure-specific attempt.
    /// </summary>
    public virtual bool NextAttemptAddresses(string output) => false;

    /// <summary>
    /// Something the person starting a share should hear once, from the
    /// platform — Windows knows whether the network is one it marks public.
    /// Null when there is nothing to say, which is the default, and Linux.
    /// </summary>
    public virtual string? StartWarning() => null;

    /// <summary>
    /// The first executable of this name on PATH, or null.
    ///
    /// Here rather than in each subclass because the only real difference is
    /// the separator and the extensions, and both come from the runtime.
    /// </summary>
    protected static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        // PATHEXT on Windows, so "python" finds python.exe; a single empty
        // extension elsewhere, so the name is tried as written.
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate) && !IsAppExecutionAlias(candidate))
                        return candidate;
                }
                catch
                {
                    // Unreadable or malformed PATH entry; keep looking.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// **A Windows machine with no Python still has python.exe on PATH.**
    ///
    /// Windows ships App Execution Aliases in
    /// %LOCALAPPDATA%\Microsoft\WindowsApps for python, python3 and others.
    /// They are zero-byte reparse points that File.Exists reports as files, and
    /// running one opens the Microsoft Store at the page for the thing it
    /// stands in for. So without this, on a stock machine: Locate() finds
    /// "python3", runs it to test whether copyparty is importable, and the
    /// Microsoft Store opens — at startup, because the sharing provider is
    /// constructed with the platform. The user launched a file manager and got
    /// a shop.
    ///
    /// Zero length is the test. A real executable is never zero bytes, and the
    /// reparse-point attribute alone would also catch ordinary symlinks, which
    /// are perfectly good interpreters and are how several version managers put
    /// one on PATH.
    /// </summary>
    private static bool IsAppExecutionAlias(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var file = new FileInfo(path);
            return file.Length == 0 && (file.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // If it cannot be inspected it cannot be trusted to be run.
            return true;
        }
    }

    /// <summary>
    /// Runs a command for its exit code alone — "is the module importable"
    /// without caring what it printed.
    ///
    /// Both pipes drain CONCURRENTLY. Draining stdout to completion and only
    /// then stderr deadlocks the moment the child fills the stderr buffer while
    /// we are still blocked on stdout, and the WaitForExit timeout never
    /// applies because ReadToEnd has no timeout of its own.
    /// </summary>
    protected static int Run(string file, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,

                // Windows would otherwise allocate a console for the child and
                // show its window, on every startup, while probing for the
                // module. Nothing on Linux.
                CreateNoWindow = true,
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null) return -1;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { Quiet.Swallowed("sharing", ex); }
                return -1;
            }

            // Exit closes the pipes, so these are already completing.
            Task.WaitAll(new Task[] { stdout, stderr }, 5_000);

            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
