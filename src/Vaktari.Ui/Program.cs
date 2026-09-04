using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.X11;

namespace Vaktari.Ui;

internal sealed class Program
{
    /// <summary>
    /// The build, and the file it is running from.
    ///
    /// **This exists because a stale `~/.local` install shadowed an RPM for
    /// three days** and every diagnosis went looking at the code instead. rpm
    /// answers "what is installed"; only the running process can answer "what is
    /// running", and until now it could not answer either.
    ///
    /// `GetName().Version` rather than reflecting over assembly attributes:
    /// AssemblyName metadata survives trimming and NativeAOT, which is how this
    /// ships.
    /// </summary>
    /// <summary>
    /// `GetName().Version` rather than reflecting over assembly attributes:
    /// AssemblyName metadata survives trimming and NativeAOT, which is how this
    /// ships. Split out from <see cref="Describe"/> so the settings dialog shows
    /// the same number by the same route — two ways of asking the version is
    /// how you end up with a window and a `--version` that disagree.
    /// </summary>
    internal static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>
    /// The file this process is actually running from. **The reason the version
    /// alone is not enough**: a stale `~/.local` install shadowed an RPM for
    /// three days, and both copies reported plausible versions.
    /// </summary>
    internal static string RunningFrom => Environment.ProcessPath ?? "(unknown path)";

    private static string Describe() => $"vaktari {Version}\n{RunningFrom}";

    /// <summary>
    /// Hands the folder and drive classes back to whatever held them.
    ///
    /// **Reuses the application's own Restore rather than reimplementing it in
    /// the installer.** The semantics are not obvious — an empty remembered
    /// value means "nobody had claimed it", which restores differently from
    /// "there was no record at all" — and a second copy of that reasoning in
    /// Inno's Pascal would be the copy that rots.
    ///
    /// Never throws. This runs during an uninstall the user has already
    /// committed to; failing loudly there would leave a half-removed program
    /// and an error box about the registry.
    /// </summary>
    private static string RestoreFileManager()
    {
        try
        {
#if VAKTARI_WINDOWS
            if (!OperatingSystem.IsWindows()) return "not Windows; nothing to undo";

            var platform = new Vaktari.Windows.WindowsPlatform(
                Session.JsonSessionStore.DefaultDirectory());

            if (platform.DefaultFileManager is not { } manager)
                return "this platform registers no folder handler";

            return manager.Restore().Message;
#else
            return "this platform registers no folder handler";
#endif
        }
        catch (Exception ex)
        {
            return $"could not undo the folder handler: {ex.Message}";
        }
    }

    /// <summary>
    /// Null for an ordinary launch; the exit code when this command line was an
    /// elevated file operation and it has now been done.
    ///
    /// Separate from <see cref="Main"/> so it can be run for real in a test —
    /// unelevated, which is exactly as much of this route as a test can prove.
    /// Everything after it, the consent dialog and the rights that follow, is
    /// the system's and cannot be answered by anything automated.
    /// </summary>
    internal static int? ElevatedExitCode(string[] args)
        => Vaktari.Core.FileSystem.ElevatedRequest.Parse(args) is { } request
            ? RunElevatedFileOp(request)
            : null;

    /// <summary>
    /// Does one file operation with the rights this process was started with,
    /// and answers with a number.
    ///
    /// **The engine, not a second implementation.** Everything about how a copy
    /// behaves — what it does about a collision, which paths it refuses, how it
    /// counts what it could not do — lives in the platform's file operations,
    /// and an elevated copy that answered any of those differently would be a
    /// second file manager with the same name.
    ///
    /// **The engine and nothing else.** Not the platform: building one starts
    /// the places watcher, probes for git and opens the state directory, all as
    /// root, none of which a copy needs. The bin is left null on purpose too —
    /// the elevated side never trashes, only deletes, so there is nothing to
    /// read the Recycle Bin for.
    ///
    /// Never throws. This runs where nobody can see a stack trace, and an
    /// unhandled exception here would exit with a number the caller reads as a
    /// count of files.
    /// </summary>
    private static int RunElevatedFileOp(Vaktari.Core.FileSystem.ElevatedRequest request)
    {
        try
        {
#if VAKTARI_WINDOWS
            if (!OperatingSystem.IsWindows())
                return Vaktari.Core.FileSystem.ElevatedRequest.Refused;

            Vaktari.Core.FileSystem.IFileOperations operations =
                new Vaktari.Windows.WindowsFileOperations();
#else
            if (!OperatingSystem.IsLinux())
                return Vaktari.Core.FileSystem.ElevatedRequest.Refused;

            Vaktari.Core.FileSystem.IFileOperations operations =
                new Vaktari.Linux.LinuxFileOperations();
#endif

            return Vaktari.Core.FileSystem.ElevatedFileOp
                .RunAsync(operations, request)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] elevated file operation: {ex.Message}");

            return Vaktari.Core.FileSystem.ElevatedRequest.Refused;
        }
    }

    /// <summary>
    /// The name the Windows installer watches for, so it can tell that Vaktari
    /// is running before it starts replacing a 28 MB executable underneath it.
    ///
    /// **Must match `AppMutex` in packaging/vaktari.iss**, and a test asserts
    /// that it does — the failure mode otherwise is silent in the worst way: the
    /// installer simply stops noticing, which looks exactly like everything
    /// working until someone upgrades with the app open.
    ///
    /// Session-local rather than `Global\`, deliberately. Creating a global
    /// mutex needs SeCreateGlobalPrivilege, which an ordinary user does not
    /// have, so a per-user install — the default here — could never create one.
    /// Local names are per-SESSION, not per-token, so an elevated setup started
    /// from the same desktop still sees it, which covers the "install for all
    /// users" path too.
    /// </summary>
    public const string InstanceMutexName = "Vaktari.Ui.Running";

    /// <summary>
    /// The switch the uninstaller passes to undo the folder-handler
    /// registration.
    ///
    /// **Named, and asserted against the installer script**, because a rename
    /// on either side breaks nothing visibly: the application still starts, the
    /// installer still builds, and the uninstall still succeeds. It simply stops
    /// undoing the registration, and the first anyone knows is a machine that
    /// cannot open a folder after removing a program.
    /// </summary>
    public const string RestoreFileManagerFlag = "--restore-file-manager";

    /// <summary>
    /// Held for the life of the process. Static so nothing collects it — a
    /// local would be eligible the moment Main stopped using it, and the mutex
    /// would quietly disappear while the window was still open.
    /// </summary>
    private static Mutex? _instanceMutex;

    /// <summary>
    /// **Not single-instance.** The mutex is a flag for the installer to read,
    /// not a lock: a second copy is welcome to start and simply opens the
    /// existing mutex instead. Refusing to launch would be a behaviour change
    /// nobody asked for, smuggled in as packaging.
    /// </summary>
    private static void ClaimInstanceMutex()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName);
        }
        catch (Exception ex)
        {
            // Nothing here is worth failing to start over. The cost of losing it
            // is an installer that cannot detect a running copy — which is
            // exactly where things were before this existed.
            Console.Error.WriteLine($"[vaktari] instance mutex unavailable: {ex.Message}");
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Answered BEFORE anything else — no window, no settings, no theme. It
        // has to work with no display attached, because CI is exactly where a
        // binary that cannot start needs to say so.
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine(Describe());
            return;
        }

        // **Uninstall calls this, and it exists because uninstalling used to
        // leave the machine unable to open a folder.**
        //
        // Becoming the default writes a shell verb pointing at this executable.
        // Removing the executable does not remove the verb, so after an
        // uninstall every double-clicked folder tried to launch a file that was
        // no longer there — and the error names a missing path, not the program
        // that registered it. Windows offers no way back from that except the
        // registry.
        //
        // Before the window, the settings, or the theme: this runs in a
        // headless uninstall with no display and must not depend on any of them.
        if (args.Any(a => a == RestoreFileManagerFlag))
        {
            Console.WriteLine(RestoreFileManager());
            return;
        }

        // **This is the elevated process.** Answered in the same place and for
        // the same reasons as the flag above: it runs with no display attached
        // — pkexec unsets DISPLAY outright — and it must depend on no window,
        // no settings file and no theme. It also must not touch this
        // program's own state directory, which as root would leave files in it
        // that the person can no longer write.
        //
        // Before the instance mutex too: this copy is not a running file
        // manager and has no business being one as far as the installer is
        // concerned.
        if (ElevatedExitCode(args) is { } code)
        {
            Environment.ExitCode = code;
            return;
        }

        // After --version, which must stay free of side effects: it runs in CI
        // and on machines with no display, and claiming a mutex to print a
        // string would be work done for nobody.
        ClaimInstanceMutex();

        // An unhandled exception on a pool thread terminates the process with
        // nothing but a core dump. Logging first turns "it vanished" into
        // something diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[vaktari] FATAL: {e.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[vaktari] unobserved: {e.Exception}");
            e.SetObserved();
        };

        Run(args);
    }

    /// <summary>Folders named on the command line, for the window to open.</summary>
    public static string[] StartupPaths { get; private set; } = [];

    /// <summary>The live channel, so the window can listen for later launches.</summary>
    public static SingleInstance? Instance { get; private set; }

    private static void Run(string[] args)
    {
        var paths = args.Where(a => !a.StartsWith('-')).ToArray();

        var instance = new SingleInstance();

        // Two instances would each restore the same session file and each write
        // back to it, which looks exactly like tabs duplicating themselves —
        // and as the desktop's default file manager, every "open folder" would
        // start another copy.
        if (!instance.TryAcquire())
        {
            // Hand the folders over and get out of the way immediately. A
            // caller that waits for its file manager to exit must not wait on a
            // window that never closes.
            instance.Dispose();

            // **The result decides what is said, and whether we stop here.**
            //
            // This line used to be printed BEFORE the attempt and the attempt's
            // answer thrown away, so a handover that failed reported success
            // and the process exited having opened nothing — indistinguishable,
            // from outside, from working. The bug that made it fail every time
            // lived in SingleInstance.Dispose, one line above this call.
            var handed = SingleInstance.TryForward(paths);

            if (handed)
            {
                // Said out loud. Refusing silently with exit code 0 is
                // indistinguishable from crashing on startup — which cost a
                // diagnostic round trip when the published binary "did nothing"
                // and the real answer was that a copy was already running.
                Console.Error.WriteLine(
                    paths.Length > 0
                        ? $"[vaktari] already running — handed over {paths.Length} path(s)"
                        : "[vaktari] already running — raising the existing window");

                return;
            }

            // Nobody answered: a copy holds the lock but its channel is gone.
            //
            // With folders to show, opening our own window is the lesser fault.
            // Two windows sharing a session file can duplicate tabs, which is
            // why single-instance exists at all — but a file manager that does
            // NOTHING when you double-click a folder is not a file manager, and
            // this is the path the desktop takes for every folder on the
            // machine. A visible extra window can be closed; a launch that
            // vanishes leaves no way to even tell what went wrong.
            //
            // With no folders, there is nothing to show and the existing window
            // is still there, so this stays out of the way as before.
            if (paths.Length == 0)
            {
                Console.Error.WriteLine(
                    "[vaktari] already running, but it did not answer — nothing to open");

                return;
            }

            Console.Error.WriteLine(
                "[vaktari] already running, but it did not answer — "
                + $"opening {paths.Length} path(s) in a window of our own");
        }
        else
        {
            Instance = instance;
        }

        StartupPaths = paths;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()

            // WM_CLASS is how the desktop matches a running window back to its
            // .desktop file. Avalonia's default derives from the assembly name
            // — "Vaktari.Ui" — while the entry is "vaktari.desktop", so the
            // panel could not associate the two and showed a placeholder icon
            // until the window published its own embedded one.
            //
            // Setting it here rather than adding StartupWMClass= to the desktop
            // entry, because there are TWO desktop entries (brand/ and
            // packaging/) and this fixes both, plus any distro package that
            // writes its own.
            .With(new X11PlatformOptions { WmClass = "vaktari" })

            .WithInterFont()
            .LogToTrace();
}
