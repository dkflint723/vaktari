namespace Vaktari.Core.Places;

/// <summary>
/// Notices volumes arriving and leaving, so a stick plugged in appears on its
/// own rather than whenever something else happens to rebuild the sidebar.
///
/// **A poll, deliberately, and not because the event-driven route was too much
/// work.** Both platforms offer one, and both fail INVISIBLY when they fail:
/// Avalonia's <c>AddWndProcHookCallback</c> returns void and quietly does
/// nothing when the top level is not a Win32 one, and <c>inotify</c> on
/// /proc/mounts hands back a valid watch descriptor that then never fires,
/// because procfs content is generated at read time. A mechanism that reports
/// success and does nothing is the worst failure available — the feature would
/// look shipped and be absent.
///
/// So the timer is the floor, always present, and a native notification can
/// only ever make it FASTER by calling <see cref="Nudge"/>. Degrading to
/// "slower" beats degrading to "gone".
///
/// The cost is a measured 34µs per tick on Windows — a 0.003% duty cycle at one
/// second — because <see cref="snapshot"/> is required to be cheap and, above
/// all, non-blocking. See the platform snapshots for what they must never ask.
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
            try
            {
                var nudged = await Wait(Interval, ct).ConfigureAwait(false);

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

            Observe(now);
        }
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
