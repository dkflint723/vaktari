using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// One background STA thread, held open, with work queued onto it and answers
/// handed back as tasks.
///
/// **Why a thread that stays**, rather than one thread per call the way
/// <see cref="AssocHandlers"/> and <see cref="WindowsLauncher"/> use one: the
/// shell objects this carries are apartment-bound. The thread that built an
/// IContextMenu is the only one that may invoke from it, so it has to be alive
/// between the two — which rules out the create-run-join shape used everywhere
/// else in this assembly.
///
/// **No call here has a deadline, and that is the point.** A deadline is only
/// ever a guess about somebody else's code, and giving up on a slow answer
/// produces the same value as never having asked — which is how a slow machine
/// came to be told the shell offered nothing. Waiting costs nothing here
/// because nobody waits: <see cref="RunAsync{T}"/> returns while the job is
/// still running and the answer arrives when it arrives. What a hung job costs
/// is this one thread, which is the cost the caller already accepted by running
/// other people's code at all.
///
/// **The thread is started on whichever thread asked for the menu**, because
/// the constructor below runs before the first await in
/// <see cref="ShellContextMenu.ForAsync"/> — so for a right-click that is the
/// UI thread. It is a Thread constructor, SetApartmentState and Start, and no
/// handler code runs there; everything after the start is on this thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    internal StaWorker(string name)
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _work.GetConsumingEnumerable())
            {
                // Other people's code runs here. One job's failure is not the
                // next job's business, and it is certainly not the process's.
                try { job(); }
                catch (Exception ex) { Quiet.Swallowed("sta-worker", ex); }
            }
        })
        {
            IsBackground = true,
            Name = name,
        };

        // The shell requires STA. AssocHandlers measured this on the
        // neighbouring interface: the identical call fails from an MTA thread
        // and succeeds from an STA one, with nothing in the HRESULT to say why.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Runs the job on this thread and answers with what it returned.
    ///
    /// **Unbounded on purpose.** See the type comment: the caller is not
    /// blocked, so there is nothing for a deadline to protect and nothing to be
    /// gained by turning a slow answer into no answer.
    ///
    /// A job that throws faults the task rather than being swallowed, because
    /// the caller asked for a value and has to be able to tell "it said
    /// nothing" from "it could not be asked".
    /// </summary>
    internal Task<T> RunAsync<T>(Func<T> job)
    {
        // **Continuations off this thread.** Run inline, whatever the awaiting
        // code does next would happen on the apartment thread and delay the
        // jobs queued behind it — measured: without this flag,
        // Every_job_runs_on_the_same_apartment_thread goes red, because the
        // test itself then continues on the worker.
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = Post(() =>
        {
            try { answer.TrySetResult(job()); }
            catch (Exception ex) { answer.TrySetException(ex); }
        });

        // Never left hanging: a task that can never complete is a caller
        // awaiting forever, which is worse than the failure it is hiding.
        if (!queued) answer.TrySetCanceled();

        return answer.Task;
    }

    /// <summary>
    /// Queues work whose result nobody is waiting for. False when the thread is
    /// already closing.
    ///
    /// **Refused rather than thrown.** Work arrives here from a menu the user
    /// may already have dismissed, and a closed queue is that race, not a
    /// fault.
    /// </summary>
    internal bool Post(Action job)
    {
        try
        {
            _work.Add(job);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Quiet.Swallowed("sta-worker", ex);
            return false;
        }
    }

    /// <summary>
    /// Ends the thread once the work already queued has run, which is what
    /// releases anything those jobs are holding on the apartment that made it.
    ///
    /// **Not joined.** A job that hung owns this thread for the life of the
    /// process, and waiting on it here would move the hang into whoever closed
    /// the menu.
    /// </summary>
    public void Dispose()
    {
        try { _work.CompleteAdding(); }
        catch (ObjectDisposedException ex) { Quiet.Swallowed("sta-worker", ex); }
    }
}
