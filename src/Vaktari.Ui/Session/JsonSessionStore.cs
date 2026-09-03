using System.Text.Json;
using Avalonia.Threading;
using Vaktari.Core.Session;

namespace Vaktari.Ui.Session;

/// <summary>
/// Crash-safe session storage. Three rules, each of which exists because
/// breaking it is how file managers end up "randomly forgetting":
///
///   1. Save continuously, not on exit. Saving in a shutdown handler loses
///      everything to a crash, a force-kill, or an update reboot.
///   2. Write atomically. A truncated file fails to parse on next launch,
///      which the user experiences as amnesia rather than as corruption.
///   3. Never let a bad session file prevent startup. Any load failure
///      returns null and the app opens empty.
/// </summary>
public sealed class JsonSessionStore : ISessionStore, IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SessionState? _pending;
    private bool _disposed;

    public JsonSessionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "session.json");
        _tempPath = _path + ".tmp";
        _backupPath = _path + ".bak";

        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Somewhere else to keep all of it, for a test.
    ///
    /// **Every headless test that built a MainWindow wrote the developer's own
    /// state.** The constructor makes eight stores out of this one directory —
    /// the session, the settings, the folder views, the recents, the drive
    /// links, the icon index and the platform's own — and closing the window
    /// flushes them. So running the suite overwrote the open tabs, the window
    /// geometry and the back stack of whoever ran it: one back stack held
    /// eighty entries named after temp folders a rename test had visited, and a
    /// test that left a tab in the bin made the bin the folder the application
    /// opened on next launch — which then failed two unrelated tests, because
    /// renaming is refused there.
    ///
    /// A property rather than an environment variable because there is nothing
    /// to read on Windows: StateRoot honours XDG_STATE_HOME on Linux, and
    /// GetFolderPath(LocalApplicationData) does not consult the environment at
    /// all. One seam that works the same on both platforms is worth more than
    /// two that do not.
    ///
    /// Set once for the whole test assembly from a module initializer, so that
    /// no test has to remember — the class that did the damage was the one of
    /// the four building a window that did not share the common base.
    ///
    /// A factory rather than a path, because "somewhere else" is not one place:
    /// a window flushes its session on close and the NEXT window restores it,
    /// so a single directory for the whole run leaves the tests poisoning each
    /// other exactly as they poisoned the developer — one that ended on the bin
    /// made the bin the folder the next test's window opened on. Asked per
    /// store rather than per run, the test side can answer with a directory of
    /// its own for each test class.
    /// </summary>
    internal static Func<string>? DirectoryOverride { get; set; }

    /// <summary>~/.local/state/vaktari on Linux, %LOCALAPPDATA%\vaktari on Windows —
    /// or <see cref="DirectoryOverride"/> when a test has set one.</summary>
    public static string DefaultDirectory()
    {
        // Before the adoptions below, deliberately: those MOVE a directory when
        // they find one, and a test directory has no heimdall or rove beside it
        // to find. Asking anyway would be two stat calls per store to answer a
        // question about a machine this is not running as.
        if (DirectoryOverride is { } ask)
        {
            var elsewhere = ask();

            Directory.CreateDirectory(elsewhere);
            return elsewhere;
        }

        var directory = Path.Combine(StateRoot(), "vaktari");

        // **Two renames now, and they are tried newest first.** ROVE became
        // Heimdall, and Heimdall became Vaktari once it turned out how many
        // other projects already had that name. Adopt rather than start empty:
        // losing every tab, pinned place, folder view and window position to a
        // change of name would be a poor trade for a new name.
        //
        // Order matters and the guard makes it safe. Adopt moves only when the
        // destination does not exist, so the first one that finds something
        // wins and the second becomes a no-op — a machine carrying both an old
        // heimdall and a much older rove directory keeps the newer state, which
        // is the one somebody has actually been using.
        Vaktari.Core.PreviousName.Adopt(directory, Path.Combine(StateRoot(), "heimdall"));
        Vaktari.Core.PreviousName.Adopt(directory, Path.Combine(StateRoot(), "rove"));

        return directory;
    }

    private static string StateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

            if (string.IsNullOrWhiteSpace(stateHome))
                stateHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "state");

            return stateHome;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }


    /// <summary>
    /// Synchronous by design. The window needs its geometry before it is shown,
    /// and an async load means restoring size and position after first paint —
    /// a visible jump on every launch. The file is a few kilobytes.
    /// </summary>
    public SessionState? Load()
    {
        var state = TryLoad(_path) ?? TryLoad(_backupPath);
        return state?.Version == SessionState.CurrentVersion ? state : null;
    }

    private static SessionState? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SessionJsonContext.Default.SessionState);
        }
        catch
        {
            // Corrupt, truncated, unreadable — all the same answer.
            return null;
        }
    }

    public void NotifyChanged(SessionState state)
    {
        if (_disposed) return;

        _pending = state with { SavedAt = DateTimeOffset.UtcNow };
        _timer.Stop();
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        // async void: anything escaping here terminates the process. WriteAsync
        // catches its own failures, but taking the write lock can still throw
        // if disposal races this tick.
        try
        {
            _timer.Stop();
            await WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] session write failed: {ex.Message}");
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        _timer.Stop();
        await WriteAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        var state = _pending;
        if (state is null || _disposed) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stream = File.Create(_tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, state, SessionJsonContext.Default.SessionState, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(_path))
                File.Copy(_path, _backupPath, overwrite: true);

            // Rename is atomic on both ext4/btrfs and NTFS, so a crash mid-save
            // leaves either the old file or the new one, never a half-written one.
            File.Move(_tempPath, _path, overwrite: true);
        }
        catch
        {
            // Losing a session write is not worth interrupting the user over.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Async because the write lock must be drained before the semaphore is
    /// disposed — tearing it down mid-write would throw into the swallowing
    /// catch above and silently lose the final save.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _timer.Stop();
        _timer.Tick -= OnTick;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        _disposed = true;
        _writeLock.Release();
        _writeLock.Dispose();
    }
}
