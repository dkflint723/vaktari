namespace Vaktari.Core;

/// <summary>
/// A store's writes to disk, one at a time and in the order they were asked
/// for, on the thread pool rather than on the thread that asked.
///
/// **Every store's save flushed to the disk on the UI thread.** Forcing the
/// bytes out before the rename is what keeps a power cut from leaving a file
/// full of zeros, and it waits for the device to say the data is down — on a
/// portable copy's stick, a network share or a busy ext4 journal, long enough
/// to freeze the window at the end of a column drag, a pin or a settings OK.
/// The session store had already moved its flush onto the pool for exactly
/// that reason; the others called it wherever they were called from.
///
/// Queued rather than merely started on the pool, because two saves started
/// together would race for the one temp file and could land the older one
/// last. The task a write returns completes when that write is on the disk,
/// which is the moment a synchronous save used to return, so a caller that
/// awaits it knows no less than before; <see cref="Idle"/> is the same promise
/// for everything queued so far, for a reader and for the way out.
/// </summary>
public sealed class WriteBehind
{
    private readonly object _gate = new();
    private Task _last = Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="write"/> on the pool after every write queued before
    /// it. The task completes when it has run, and faults if it threw; the
    /// writes after it run either way.
    /// </summary>
    public Task Enqueue(Action write)
    {
        lock (_gate)
        {
            return _last = _last.ContinueWith(
                _ => write(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Completes once every write queued so far has run, whether or not it
    /// succeeded — never faults, so the way out can await it unguarded.
    /// </summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _last.ContinueWith(
                    static _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
