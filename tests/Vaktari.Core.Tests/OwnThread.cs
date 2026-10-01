using System.Runtime.ExceptionServices;

namespace Vaktari.Core.Tests;

/// <summary>
/// Runs an action on a thread of its own, not the pool's.
///
/// **A test that hands its own steps to the pool measures the pool.** Core's
/// classes run in parallel, and plenty of them hold pool threads on gates and
/// waits; on a four-core CI runner the pool had no thread free for seconds,
/// and a Task.Run that a test waited five seconds for never began
/// (batch-0.11.2e, CI run 36887240896: "the recheck never asked the disk").
/// What such a test proves is about the code it drives, not about when the
/// pool gets round to it, so the test's own steps go on threads of their own.
/// </summary>
internal sealed class OwnThread
{
    private readonly ManualResetEventSlim _done = new();
    private Exception? _failed;

    private OwnThread(Action action)
    {
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                _failed = e;
            }
            finally
            {
                _done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "test step",
        };

        thread.Start();
    }

    public static OwnThread Run(Action action) => new(action);

    /// <summary>Whether the action finished within <paramref name="within"/>;
    /// what it threw is thrown here.</summary>
    public bool Finished(TimeSpan within)
    {
        if (!_done.Wait(within)) return false;

        if (_failed is not null) ExceptionDispatchInfo.Throw(_failed);

        return true;
    }
}
