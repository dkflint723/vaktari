using System.Net.Sockets;
using System.Text;

using Vaktari.Core;

namespace Vaktari.Ui;

/// <summary>
/// Ensures one Vaktari, and gives later launches a way to hand their paths to
/// it instead of starting a second copy.
///
/// This matters far more once Vaktari is the desktop's default file manager:
/// every "open containing folder" becomes a launch, and without this each one
/// started another full application that then ignored the folder it was asked
/// for. A caller that waits for its handler to finish waits forever.
///
/// The guard is an exclusive file lock, NOT a named Mutex. .NET's named mutexes
/// do not provide cross-process exclusion here — the previous implementation
/// used one and was silently inert, which is why two instances were running.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private FileStream? _lock;
    private Socket? _listener;
    private CancellationTokenSource? _stopping;

    /// <summary>Paths sent by a later launch, already on the UI thread.</summary>
    public event EventHandler<string[]>? PathsReceived;

    /// <summary>
    /// Where the lock and socket live, for tests only.
    ///
    /// **They must not use the real one.** The suite runs while the author's
    /// own copy is open, these files are per-user rather than per-process, and
    /// the first thing this class does with a socket it believes is stale is
    /// delete it — which is precisely the fault being tested. A test that
    /// reproduced the bug against the live path would break the running window
    /// to prove the point.
    /// </summary>
    internal static string? RuntimeDirectoryOverride { get; set; }

    /// <summary>
    /// XDG_RUNTIME_DIR where the session has one, and a folder of the user's
    /// own where it has not. **It was /tmp** where it has not, which every
    /// account on the machine shares — see <see cref="PrivateDirectory"/>
    /// for what that allowed.
    /// </summary>
    private static string RuntimeDirectory => RuntimeDirectoryOverride ?? PrivateDirectory.Runtime();

    internal static string LockPath => Path.Combine(RuntimeDirectory, "vaktari.lock");
    internal static string SocketPath => Path.Combine(RuntimeDirectory, "vaktari.sock");

    /// <summary>
    /// True when this process is the one and only. False means another already
    /// holds the lock and the caller should forward and exit.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _lock = PrivateDirectory.OpenLock(LockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // IOException is the ordinary answer: another copy holds it.
            // UnauthorizedAccessException is a lock that cannot be opened at
            // all, and was measured escaping here — **a read-only lock file
            // crashed the start** rather than falling through to Program,
            // which hands over if anyone answers and opens a window if not.
            return false;
        }

        try
        {
            // A crash leaves the socket file behind; the lock proves nobody is
            // listening on it, so removing it is safe here and only here.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);

            _stopping = new CancellationTokenSource();
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);

            _ = Task.Run(() => AcceptAsync(_stopping.Token));
        }
        catch (Exception ex)
        {
            // Without the listener we are still the only instance; later
            // launches simply cannot hand anything over.
            Console.Error.WriteLine($"[vaktari] instance channel unavailable: {ex.Message}");
        }

        return true;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                using var client = await listener.AcceptAsync(ct).ConfigureAwait(false);

                var buffer = new byte[8192];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, ct)
                                       .ConfigureAwait(false);

                if (read <= 0) continue;

                var paths = Encoding.UTF8.GetString(buffer, 0, read)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (paths.Length == 0) continue;

                // Raised on the UI thread: handlers open tabs and activate the
                // window, neither of which is safe from a socket thread.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => PathsReceived?.Invoke(this, paths));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] instance channel: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Hands paths to the running instance. Returns false if nothing answered,
    /// in which case the caller should start normally rather than vanish.
    /// </summary>
    public static bool TryForward(string[] paths)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);

            // Short: the whole point is to return before the calling
            // application notices it launched anything.
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\n', paths)));

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        // **Only the instance that won the lock owns the socket file.**
        //
        // This deleted it unconditionally, and the process that deletes it is
        // the one that LOST — Program disposes the loser before forwarding, so
        // the first handed-over folder removed the running window's socket and
        // then tried to connect to the path it had just unlinked. It failed, as
        // did every handover afterwards for the life of that window, and the
        // launch exited having opened nothing.
        //
        // Nothing said so. TryForward's result was discarded, the "handed over"
        // line was printed before the attempt, and the failure looked exactly
        // like the intended behaviour: a second copy that starts and quietly
        // gets out of the way. As the desktop's default file manager this is
        // the whole feature — every double-clicked folder is a launch.
        var owner = _lock is not null;

        try { _stopping?.Cancel(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
        try { _listener?.Dispose(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
        try { _lock?.Dispose(); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }

        if (!owner) return;

        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch (Exception ex) { Quiet.Swallowed("instance", ex); }
    }
}
