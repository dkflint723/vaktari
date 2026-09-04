using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The apartment thread the shell menu runs on, tested with jobs of our own.
///
/// **Why the jobs are ours rather than the shell's.** The fault is what happens
/// when the shell is SLOW, and there is no way to make the real shell slow on
/// demand — this machine answers in about a tenth of a second. A job that blocks
/// until the test releases it is a slow machine, deterministically. That trick
/// is not exclusive to this file: ShellContextMenuTests plays it through
/// ForAsync's build parameter, and ShellMenuBindingTests plays it through the
/// view model's provider seam, each pinning the fault at its own layer. What
/// this file adds is the apartment thread's own behaviour — STA, one thread,
/// held open, closed exactly once — which the other two cannot see.
///
/// **Every wait here is bounded.** A regression in the consuming loop leaves an
/// await with nobody to complete it, and an unbounded one turns a red test into
/// a CI job that reports nothing at all — reported: `Take(1)` on that loop hung
/// the whole project run past 600 s rather than failing it, and deleting the
/// TrySetCanceled in RunAsync hung it too. With the bounds below both were
/// measured going red instead: `Take(1)` reddens five of these inside eighty
/// seconds, and the deleted TrySetCanceled reddens one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StaWorkerTests
{
    /// <summary>Long enough that a loaded agent never trips it, short enough
    /// that a regression is a red test inside the minute.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// **A PIN for the fault, at this layer.** A build that ran long used to be
    /// given up on: the caller waited four seconds, then carried on with an
    /// empty answer that could not be told apart from the shell having nothing
    /// to offer. Nothing waits now, so there is nothing to give up — however
    /// long the job takes, its answer is the one that comes back.
    ///
    /// **A quarter of a second, not four.** No test can afford to outlast an
    /// arbitrary deadline, so what is pinned here is that there is none: the job
    /// is held while the caller goes round its own loop, and a deadline shorter
    /// than that hold turns the assertion below red. Measured: reinstating one
    /// of a millisecond fails this on "the job was given up on". The four-second
    /// constant that actually shipped is pinned where it lived, in
    /// ShellContextMenuTests, which is the only place a five-second test earns
    /// its wall clock.
    /// </summary>
    [WindowsFact]
    public async Task An_answer_that_takes_its_time_is_still_delivered()
    {
        using var worker = new StaWorker("vaktari-test");
        using var held = new ManualResetEventSlim();

        var answer = worker.RunAsync(() =>
        {
            held.Wait();
            return 42;
        });

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        // Asking cost the caller nothing — it came back here with the job still
        // running, which is what makes waiting indefinitely affordable — and
        // nothing has decided the job is never coming back.
        Assert.False(answer.IsCompleted, "the job was given up on while it ran");

        held.Set();

        Assert.Equal(42, await answer.WaitAsync(Patience));
    }

    /// <summary>
    /// A GUARD, not a pin: the deleted code was STA too, and every test here was
    /// measured passing under the mutation that puts the deadline back. What it
    /// holds up is the constraint the whole design rests on — IContextMenu is
    /// apartment-bound, so the thread that built a menu is the only one that may
    /// invoke from it, and the shell refuses an MTA thread with nothing in the
    /// HRESULT to say why.
    /// </summary>
    [WindowsFact]
    public async Task Every_job_runs_on_the_same_apartment_thread()
    {
        using var worker = new StaWorker("vaktari-test");

        var apartment = await worker
            .RunAsync(() => Thread.CurrentThread.GetApartmentState()).WaitAsync(Patience);
        var first = await worker
            .RunAsync(() => Environment.CurrentManagedThreadId).WaitAsync(Patience);
        var second = await worker
            .RunAsync(() => Environment.CurrentManagedThreadId).WaitAsync(Patience);

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal(first, second);
        Assert.NotEqual(Environment.CurrentManagedThreadId, first);
    }

    /// <summary>
    /// A GUARD: the deleted code held its thread open for exactly this reason
    /// and this cannot go red for the deadline. It holds up the reason the
    /// thread outlives the build — invoking an entry happens long after the menu
    /// was read and has to happen on the same apartment.
    /// </summary>
    [WindowsFact]
    public async Task Work_posted_after_the_answer_runs_on_that_same_thread()
    {
        using var worker = new StaWorker("vaktari-test");
        using var ran = new ManualResetEventSlim();

        var built = await worker
            .RunAsync(() => Environment.CurrentManagedThreadId).WaitAsync(Patience);

        var invoked = 0;
        Assert.True(worker.Post(() =>
        {
            invoked = Environment.CurrentManagedThreadId;
            ran.Set();
        }));

        Assert.True(ran.Wait(Patience), "posted work never ran");
        Assert.Equal(built, invoked);
    }

    /// <summary>
    /// Pins new behaviour rather than the bug: the old code called _work.Add
    /// outside any try, so a click landing after the close would have thrown.
    /// A click and a close are a race the user can genuinely run, so work
    /// arriving after the thread has been told to end is refused rather than
    /// thrown — and a value asked for then is cancelled rather than left
    /// pending, because a task that can never complete is a caller awaiting
    /// forever.
    ///
    /// The bound on the throw assertion is not decoration. WaitAsync raises
    /// TimeoutException, which is not an OperationCanceledException, so a
    /// regression that leaves the task pending fails this line instead of
    /// hanging on it. Reported without the bound: deleting the TrySetCanceled
    /// this pins hung the run instead of reddening it. Measured with it:
    /// the same deletion reddens this test at twenty seconds.
    /// </summary>
    [WindowsFact]
    public async Task Work_arriving_after_disposal_is_refused_rather_than_thrown()
    {
        var worker = new StaWorker("vaktari-test");
        worker.Dispose();

        Assert.False(worker.Post(() => { }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.RunAsync(() => 1).WaitAsync(Patience));
    }

    /// <summary>
    /// A GUARD: the deleted code ended its thread the same way, by completing
    /// the same queue. It holds up that each menu's thread goes away with it —
    /// a worker that outlived its menu would be a thread leaked per right-click.
    /// </summary>
    [WindowsFact]
    public async Task Disposing_ends_the_thread()
    {
        var worker = new StaWorker("vaktari-test");

        var thread = await worker.RunAsync(() => Thread.CurrentThread).WaitAsync(Patience);

        worker.Dispose();

        Assert.True(
            SpinWait.SpinUntil(() => !thread.IsAlive, Patience),
            "the apartment thread outlived the worker");
    }

    /// <summary>
    /// A GUARD for the deadline — nothing about it was broken — but not an
    /// unpinnable one: it holds up the assumption
    /// <see cref="ShellContextMenu.Dispose"/> rests on, that closing the worker
    /// lets what is already queued run first, and a consuming loop that stops
    /// early reddens it. Measured: `.Take(1)` on that loop fails this along
    /// with three others. Freeing a menu handle from the wrong apartment, or
    /// not at all, is the kind of wrong that shows up as a crash somewhere else
    /// entirely.
    /// </summary>
    [WindowsFact]
    public void Work_already_queued_still_runs_before_the_thread_ends()
    {
        var worker = new StaWorker("vaktari-test");

        using var holding = new ManualResetEventSlim();
        using var ran = new ManualResetEventSlim();

        worker.Post(holding.Wait);
        worker.Post(ran.Set);

        worker.Dispose();

        holding.Set();

        Assert.True(ran.Wait(Patience),
            "work queued before the close was dropped");
    }

    /// <summary>
    /// Pins new behaviour rather than the bug: the deleted code swallowed a
    /// failed build into an empty Entries, which is the conflation this whole
    /// change is about, one layer down. Other people's code runs on this thread,
    /// and a handler that throws has to reach the caller as a failure rather
    /// than as an answer, because "it said nothing" and "it could not be asked"
    /// are different menus.
    ///
    /// Both awaits are bounded. The second is the one that mattered: a worker
    /// that has stopped consuming leaves it waiting forever, and measured, that
    /// hung the whole project run rather than failing it.
    /// </summary>
    [WindowsFact]
    public async Task A_job_that_throws_faults_its_task()
    {
        using var worker = new StaWorker("vaktari-test");

        var boom = worker.RunAsync<int>(() => throw new InvalidOperationException("handler"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => boom.WaitAsync(Patience));

        Assert.Equal("handler", failure.Message);

        // And the thread is still there for the next job.
        Assert.Equal(7, await worker.RunAsync(() => 7).WaitAsync(Patience));
    }
}
