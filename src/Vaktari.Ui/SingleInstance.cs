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

    /// <summary>
    /// Paths sent by a later launch, already on the UI thread, exactly as they
    /// were sent. Empty when the launch named none — which is still a request,
    /// to bring the running window forward.
    /// </summary>
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

    /// <summary>
    /// What tells one copy's lock and socket from another's: nothing for an
    /// installed copy, so nothing about it changes, and for a portable one a
    /// digest of where its state lives.
    ///
    /// **Without it a portable copy and the installed copy shared one lock.**
    /// The second to start handed its folder to the first and exited — and
    /// a copy somebody carried in on a stick and started on purpose is the
    /// copy that was meant to open the folder. The full path, so the same
    /// copy started the same way gets the same name every time; the lock
    /// still lives in the per-user runtime folder, because the stick may be
    /// read-only and the socket has to be somewhere a socket can be.
    /// </summary>
    internal static string Suffix
        => Session.JsonSessionStore.PortableRoot is { } portable
            ? "-" + Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(Path.GetFullPath(portable))))[..8]
            : "";

    internal static string LockPath => Path.Combine(RuntimeDirectory, $"vaktari{Suffix}.lock");
    internal static string SocketPath => Path.Combine(RuntimeDirectory, $"vaktari{Suffix}.sock");

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

    /// <summary>
    /// The most one launch may hand over. Far past any real selection —
    /// ten thousand paths of a hundred bytes each — and small enough that a
    /// client which never stops sending cannot grow the running window without
    /// bound.
    /// </summary>
    internal const int MaxHandoverBytes = 1024 * 1024;

    /// <summary>
    /// How long one launch has to finish sending. TryForward writes and closes
    /// at once, so this only ever runs out on a client that connected and then
    /// stalled — and without it that client would hold the one accept loop,
    /// and with it every later handover, for as long as it stayed connected.
    /// </summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(5);

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { } listener)
        {
            try
            {
                using var client = await listener.AcceptAsync(ct).ConfigureAwait(false);

                if (await ReceiveAllAsync(client, ct).ConfigureAwait(false) is not { } message)
                    continue;

                // **Split, never trimmed.** This split with TrimEntries, and a
                // space at either end of a name is part of the name on Linux:
                // a folder called "old " handed over opened "old" if there was
                // one — the wrong folder, confidently — and nothing if not.
                // Program sends exactly what it was given, one per line.
                //
                // **And an empty message is still delivered.** A launch with no
                // folder sends nothing at all, and this used to stop there on
                // "nothing read" and again on "no paths", so PathsReceived never
                // fired and the window it was asked to raise stayed where it
                // was — while the launch printed "raising the existing window".
                // The handler opens no tabs for an empty list and raises the
                // window, which is what starting Vaktari again means.
                var paths = message.Length == 0
                    ? []
                    : Encoding.UTF8.GetString(message)
                              .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // Raised on the UI thread: handlers open tabs and activate the
                // window, neither of which is safe from a socket thread.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => PathsReceived?.Invoke(this, paths));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
    /// Everything one launch sent, or null when it sent more than
    /// <see cref="MaxHandoverBytes"/>.
    ///
    /// **One read of 8 KiB was taken as the whole message.** A handover of
    /// about eighty paths or more arrived cut off mid-name: measured at 120,
    /// the last one delivered was "C:\Users\someone\D", which failed
    /// Directory.Exists and was dropped without a word — and a cut that happens
    /// to land on a folder that exists opens the wrong one. The sender closes
    /// when it is done, so a read of zero is the end, and nothing is decoded
    /// before it: a UTF-8 character split across two reads would otherwise
    /// decode as two replacement characters.
    ///
    /// Over the cap the message is refused whole rather than cut, because a cut
    /// is the fault being fixed.
    /// </summary>
    private static async Task<byte[]?> ReceiveAllAsync(Socket client, CancellationToken ct)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stalled.CancelAfter(HandoverTimeout);

        using var message = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await client.ReceiveAsync(buffer, SocketFlags.None, stalled.Token)
                                   .ConfigureAwait(false);

            if (read <= 0) return message.ToArray();

            if (message.Length + read > MaxHandoverBytes)
            {
                Console.Error.WriteLine(
                    $"[vaktari] instance channel: a handover over {MaxHandoverBytes} bytes was refused");

                return null;
            }

            message.Write(buffer, 0, read);
        }
    }

    /// <summary>What became of a handover.</summary>
    public enum Handover
    {
        /// <summary>The running copy has the paths.</summary>
        Handed,

        /// <summary>Nothing answered; the caller should start normally rather
        /// than vanish.</summary>
        NoAnswer,

        /// <summary>More than <see cref="MaxHandoverBytes"/>: never sent, and
        /// the running copy would have refused it whole.</summary>
        TooLarge,
    }

    /// <summary>
    /// Hands paths to the running instance.
    ///
    /// **A handover over the cap opened a second window.** The running copy
    /// refuses one whole and closes on it; the launch, still blocked sending
    /// a megabyte into a socket buffer a fifth that size, got a reset, read
    /// it as "nothing answered" and started a copy of its own on the shared
    /// session file — the outcome single-instance exists to prevent. The size
    /// is known before connecting, so it is measured here and said as what it
    /// is, and Program stops rather than opening anything.
    /// </summary>
    public static Handover TryForward(string[] paths)
    {
        var message = Encoding.UTF8.GetBytes(string.Join('\n', paths));

        if (message.Length > MaxHandoverBytes) return Handover.TooLarge;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);

            // Short: the whole point is to return before the calling
            // application notices it launched anything.
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(message);

            // Said, not left to Dispose: the running copy reads until the
            // stream ends, and this is the end. Its own catch, because the
            // paths are already sent — failing here must not report "nobody
            // answered" and open a second window for a folder already handed
            // over; Dispose closes the stream either way.
            try { socket.Shutdown(SocketShutdown.Send); }
            catch (Exception ex) { Quiet.Swallowed("instance", ex); }

            return Handover.Handed;
        }
        catch
        {
            return Handover.NoAnswer;
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
