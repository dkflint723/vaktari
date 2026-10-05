namespace Vaktari.Core.Places;

/// <summary>
/// Notices volumes arriving and leaving, so a stick plugged in appears on its
/// own rather than whenever something else happens to rebuild the sidebar.
///
/// **The system says when; the timer is only the floor.** Both platforms'
/// native routes can fail invisibly — Avalonia's <c>AddWndProcHookCallback</c>
/// returns void and does nothing on a top level that is not Win32, and
/// <c>inotify</c> on /proc/mounts hands back a watch that never fires — and a
/// mechanism that reports success and does nothing is the worst failure
/// available. So a native source can only ever make a look FASTER, by calling
/// <see cref="Nudge"/>, and the timer stays. What changed is its period: one
/// second when no native source is running, <see cref="FallbackInterval"/>
/// once one has said it is (<see cref="UseNativeSource"/>).
///
/// **The one-second floor was not the 34 µs a tick this comment used to
/// claim.** The look itself is that cheap; the wake around it is not.
/// Measured over 120 s of an idle process: 234 ms of CPU with the watch
/// running against 0 without (a cycle counter put it at 216 ms against 6), so
/// about 2 ms a wake, every second, forever. The platform sources (a hidden
/// window that hears WM_DEVICECHANGE on Windows, a poll() on
/// /proc/self/mountinfo on Linux) cost nothing until something changes.
///
/// **A fallback look that finds a change no nudge announced is logged, once.**
/// That is the one sign a native source has gone quiet — the failure this
/// class was written to survive — and without it a dead source would look
/// exactly like a slow machine.
///
/// <see cref="snapshot"/> is required to be cheap and, above all,
/// non-blocking. See the platform snapshots for what they must never ask.
///
/// Nothing here knows what a drive is. The whole platform surface is one
/// <see cref="Func{TResult}"/> returning a string, which is what lets the
/// decision below be tested with no hardware, no timer and no operating system.
/// </summary>
public sealed class DeviceWatch(Func<string> snapshot) : IDisposable
{
    /// <summary>Raised when the set of volumes differs from the last look.
    ///
    /// **On a background thread.** Handlers marshal to their own thread if they
    /// need one; the sidebar does exactly that.</summary>
    public event EventHandler? Changed;

    /// <summary>How long between looks when nothing nudges. One second puts the
    /// mean at half of that, which is inside the window where a result still
    /// feels caused by the thing the person just did.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long between looks once a native source is running. Thirty
    /// seconds: a native route that misses an event, or one Windows never
    /// broadcasts (a drive letter mapped by another program), still shows up
    /// inside half a minute, at a thirtieth of the wakes.
    /// </summary>
    public TimeSpan FallbackInterval { get; init; } = TimeSpan.FromSeconds(30);

    private volatile bool _native;

    /// <summary>
    /// Says whether a native source is now nudging this watch. True stretches
    /// the floor to <see cref="FallbackInterval"/>; false — a source that
    /// could not start, or stopped — puts it back to <see cref="Interval"/>.
    /// </summary>
    public void UseNativeSource(bool live) => _native = live;

    /// <summary>The wait the loop asks for next: the floor in force now.</summary>
    public TimeSpan CurrentInterval => _native ? FallbackInterval : Interval;

    /// <summary>Whether a fallback look has found a change no nudge
    /// announced — said once in the log. For the tests.</summary>
    internal bool MissedByNative { get; private set; }

    /// <summary>How long to keep draining nudges before looking.
    ///
    /// **Only ever applied after a nudge, never on the timer path.** One device
    /// can broadcast once per partition, and looking four times is three
    /// rebuilds nobody asked for. Paying this on the timer path instead would
    /// add a quarter second to every arrival to hide a state — a row present
    /// but dimmed for a moment — that is already the correct rendering of a
    /// volume that has not finished mounting.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Stands in for the wait, so a test drives the loop by hand
    /// rather than by sleeping. True means "nudged", false means "timed out" —
    /// the same two answers the real wait gives.</summary>
    internal Func<TimeSpan, CancellationToken, Task<bool>>? WaitOverride { get; init; }

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();

    private string? _last;
    private bool _started;
    private volatile bool _disposed;

    /// <summary>
    /// Begins watching. Returns before the first look, so a caller on the UI
    /// thread is never made to wait for one.
    ///
    /// Separate from the constructor because tests construct providers freely,
    /// and a background loop started by construction is a leak with a
    /// heartbeat.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed) return;

        _started = true;

        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>
    /// Asks for a look now — what a native device notification calls when one
    /// is wired up.
    ///
    /// **Never throws, whatever the state.** This is called from inside a
    /// window procedure, where an escaping exception does not fail the feature,
    /// it ends the process.
    /// </summary>
    public void Nudge()
    {
        try
        {
            if (!_disposed) _wake.Release();
        }
        catch (Exception ex)
        {
            // Disposed underneath us, or the count is somehow saturated. Either
            // way a missed nudge costs one interval of latency and nothing else.
            Quiet.Swallowed("places", ex);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool nudged;

            try
            {
                nudged = await Wait(CurrentInterval, ct).ConfigureAwait(false);

                // Drain the burst: one device arriving can nudge once per
                // partition, and each extra look is a whole sidebar rebuild.
                if (nudged) while (await Wait(Settle, ct).ConfigureAwait(false)) { }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested) return;

            string now;

            try
            {
                now = snapshot();
            }
            catch (Exception ex)
            {
                // One unreadable look must not end detection for the run: the
                // mount table can be mid-write, and a drive can vanish between
                // being listed and being asked about.
                Quiet.Swallowed("places", ex);
                continue;
            }

            if (Observe(now) && !nudged) NoticeMissed();
        }
    }

    /// <summary>
    /// A change the timer found and no native source announced. Logged the
    /// first time only, and only while a native source claims to be running:
    /// with none, every change is found this way and that is the design.
    /// </summary>
    internal void NoticeMissed()
    {
        if (!_native || MissedByNative) return;

        MissedByNative = true;

        Diagnostics.Log.Warn("places",
            "a device change was found by the fallback look, not announced by the system — the native device notification may not be working");
    }

    private Task<bool> Wait(TimeSpan delay, CancellationToken ct)
        => WaitOverride?.Invoke(delay, ct) ?? _wake.WaitAsync(delay, ct);

    /// <summary>
    /// The entire decision, and pure: report only when this look differs from
    /// the last one.
    ///
    /// The first look establishes the baseline and announces nothing — at
    /// startup the sidebar has just been built from the same volumes, and a
    /// rebuild would be work to redraw what is already on screen.
    /// </summary>
    internal bool Observe(string now)
    {
        if (_last is null)
        {
            _last = now;
            return false;
        }

        if (string.Equals(now, _last, StringComparison.Ordinal)) return false;

        _last = now;

        // Disposed between the look and the report: nobody is listening any
        // more, and raising here would rebuild a sidebar that is going away.
        if (_disposed) return false;

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            _stop.Cancel();
            _stop.Dispose();
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
        }
    }
}
