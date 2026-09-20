using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The copies, moves and deletes still running, as a row apiece.
///
/// **An operation outlives the pane that started it**, which is why these live
/// on the shell: closing the tab you copied from must not cancel the copy, and
/// the row has to keep reporting somewhere. What is here is the list, what it
/// says about itself, and the two ways it ends — finished, or cancelled by
/// somebody watching it take too long.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- operations ----------------------------------------------------

    /// <summary>
    /// Everything currently running, newest last.
    ///
    /// **One slot was not enough, and the second operation took it.** Nothing
    /// serialises these: each Copy, Move or Trash starts its own handle
    /// immediately, the conflict prompt is an inline bar rather than a modal so
    /// the window stays usable, and every pane and tab reports into the same
    /// shell. Paste a large folder and then press Delete on something else: the
    /// trash finishes in milliseconds, its completion cleared ActiveOperation
    /// without checking whether it still owned it, and the transfer bar vanished
    /// while the copy was still running - taking Cancel with it, since
    /// CancelOperation only ever reached ActiveOperation.
    /// </summary>
    private readonly List<IOperationHandle> _running = [];

    /// <summary>This window's own transfers, for the family: the eject veto
    /// asks every window rather than only the one being clicked in.</summary>
    internal IReadOnlyList<IOperationHandle> Running => _running;

    /// <summary>
    /// Everything running anywhere, when this shell belongs to a window that
    /// has a family. Null for a shell on its own, which then answers with its
    /// own list — so every existing view-model test is unchanged.
    /// </summary>
    internal Func<IEnumerable<IOperationHandle>>? AllRunning { get; set; }

    /// <summary>
    /// What this window would take with it if it closed now, or null when it
    /// would take nothing.
    ///
    /// **Closing a window used to kill its transfer and nobody noticed**,
    /// because the process was ending too. With a second window the process
    /// carries on and the handle goes on writing with no bar showing it, no
    /// Cancel reaching it and nobody told when it fails.
    ///
    /// Reuses InFlight.Unfinished rather than testing state a second way: its
    /// own comment records that a finished handle lingers in this list for a
    /// moment, and two spellings of "still owes something" is one too many.
    ///
    /// The tab count travels in the same sentence when the tabs question would
    /// also have applied, because only one of the two is ever asked.
    /// </summary>
    internal string? RunningDescription()
    {
        var live = _running.Count(h => Core.FileSystem.InFlight.Unfinished(h.State));

        if (live == 0) return null;

        var tabs = Left.Tabs.Count + (Right?.Tabs.Count ?? 0);

        var transfers = live == 1 ? "A transfer is" : $"{live} transfers are";

        return tabs > 1
            ? $"{transfers} still running, and {tabs} tabs are open. Close anyway?"
            : $"{transfers} still running. Close anyway?";
    }

    /// <summary>
    /// Re-reads how many operations are live, for the bar's count and for the
    /// rows behind it.
    ///
    /// **One filtered list feeds both**, and that is the point of doing it
    /// here: a count taken one way and rows taken another would eventually
    /// disagree, and the disagreement would be on screen — "2 running" over
    /// three rows, one of them offering a Cancel for work that had already
    /// finished.
    ///
    /// Counted through InFlight.Unfinished rather than off _running.Count, for
    /// the reason InFlight.On's own comment gives and that this one measured: a
    /// finished handle stays in the list until the continuation posted to the
    /// UI thread takes it out, so a third operation starting in that window
    /// counted the finished one too — _running.Count put "3 running" on the bar
    /// with two going, and "4 running" with two going and two ended. And the
    /// shared helper rather than a hand-rolled state test, because a run ends
    /// three ways and only one of them is Completed. Both cases are in
    /// ConcurrentOperationsTests.
    ///
    /// Called where the list changes, both sides: the shell learns an
    /// operation is over from its Completion continuation, which is the same
    /// place the list shrinks. IOperationHandle.StateChanged exists now, and
    /// the rows follow it for their own pause word — but the LIST still comes
    /// from here, because a state change is not the moment a handle leaves
    /// <see cref="_running"/>, and a row built off the one would disagree with
    /// the count taken off the other.
    ///
    /// The rows being replaced are disposed first: each one holds a
    /// subscription on its handle, and a row nothing shows any more would go
    /// on following a label for as long as that handle lived.
    /// </summary>
    private void RefreshConcurrentOperations()
    {
        var live = _running.Where(h => Core.FileSystem.InFlight.Unfinished(h.State)).ToList();

        ConcurrentOperations = live.Count > 1 ? $"{live.Count} running" : "";

        foreach (var row in RunningOperations) row.Dispose();

        RunningOperations = [.. live.Select(h => new RunningOperationRow(h))];
    }

    /// <summary>
    /// Cancels everything this window started. Called when the question above
    /// is answered yes: leaving them running is the silent loss it exists to
    /// prevent.
    /// </summary>
    internal void CancelAllOperations()
    {
        // Over a copy, because Cancel drives a continuation that removes the
        // handle from this very list.
        foreach (var handle in _running.ToList()) handle.Cancel();
    }

    private Avalonia.Threading.DispatcherTimer? _rateTimer;

    /// <summary>
    /// Overridable so a test can drive the clock instead of waiting on it. The
    /// whole point of the rate is that it ages out after a few seconds, and a
    /// test that has to sit through those seconds is slow and flaky at once.
    /// </summary>
    internal Func<TransferRate> NewRate { get; set; } = () => new TransferRate();

    /// <summary>
    /// Re-reads the rate once a second while something is running.
    ///
    /// The rate answers null once its newest reading has aged out, and nothing
    /// else would ever ask it again: a stalled copy fires no progress at all,
    /// so without this the last number stays on the bar for as long as the
    /// operation is stuck.
    /// </summary>
    private void StartRateTicking(IOperationHandle handle, Action tick)
    {
        _rateTimer?.Stop();

        _rateTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _rateTimer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(ActiveOperation, handle)) return;

            tick();
        };

        _rateTimer.Start();
    }

    /// <summary>
    /// Puts one reading on the bar: how far along, and how long it has left.
    ///
    /// Bytes where there are bytes, items where there are not — a trash and a
    /// delete report a count and no bytes at all, and a bar sitting at zero for
    /// the whole run then vanishing is what a hung operation looks like.
    /// </summary>
    internal void ShowProgress(TransferRate rate, OperationProgress p)
    {
        var speed = rate.BytesPerSecond;

        HasOperationProgress = p.BytesTotal > 0 || p.ItemsTotal > 1;

        OperationPercent = p.BytesTotal > 0
            ? Math.Clamp((double)p.BytesDone / p.BytesTotal, 0, 1)
            : p.ItemsTotal > 0 ? Math.Clamp((double)p.ItemsDone / p.ItemsTotal, 0, 1) : 0;

        var parts = new List<string>(2);

        if (speed is { } bytesPerSecond) parts.Add($"{ByteSize.Format((long)bytesPerSecond)}/s");

        if (TransferRate.Remaining(p.BytesDone, p.BytesTotal, speed) is { } left)
            parts.Add(TransferRate.Describe(left));

        OperationRate = string.Join(" · ", parts);
    }

    private void OnOperationStarted(object? sender, IOperationHandle handle)
    {
        _running.Add(handle);
        ActiveOperation = handle;

        RefreshConcurrentOperations();

        // One rate per operation, because a rate carried across two of them
        // measures the gap between them as a slow patch in whichever is
        // running now.
        var rate = NewRate();
        var last = default(OperationProgress);

        handle.Progressed += (_, p) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Only the operation the bar is showing may write to it, or a
                // quick background one overwrites the foreground one's numbers.
                if (!ReferenceEquals(ActiveOperation, handle)) return;

                // **"0/5  0 B/0 B" is what a five-item trash read.** The
                // bytes were dropped only when the item count was also one or
                // less, so a batch operation that reports no sizes -- which is
                // every trash and every permanent delete, both of which open
                // with totalBytes: 0 -- drew two figures that were zero at the
                // start, zero in the middle and zero at the end. The count is
                // the only real number there, and now it is the only one shown.
                OperationStatus = (p.BytesTotal == 0
                    ? p.ItemsTotal <= 1
                        ? p.CurrentItem ?? ""
                        : $"{p.ItemsDone}/{p.ItemsTotal}  {p.CurrentItem}"
                    : $"{p.ItemsDone}/{p.ItemsTotal}  {ByteSize.Format(p.BytesDone)}/{ByteSize.Format(p.BytesTotal)}  {p.CurrentItem}")
                    .TrimEnd();

                rate.Observe(p.BytesDone);

                last = p;

                ShowProgress(rate, p);
            });

        // **A stall has to be able to age the speed out**, and nothing else can
        // do it: the engine reports on every buffer and every item, so a copy
        // stuck inside one file reports nothing at all — and without a tick the
        // bar would go on claiming a speed while a drive that has given up
        // moves nothing.
        StartRateTicking(handle, () => ShowProgress(rate, last));

        _ = handle.Completion.ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _running.Remove(handle);

                RefreshConcurrentOperations();

                // A failure stays on screen; only success clears silently.
                // Travels with the message below, in every branch: an offer
                // that outlived its sentence would reappear under the next
                // operation's failure.
                Retryable = handle.Retry;
                _retryPane = ActiveTab;

                // Taken alongside the offer rather than inside the branch that
                // writes the sentence, and for the same reason: a clean run has
                // to CLEAR this, or the previous failure's list stays reachable
                // under the next operation's message. A failed operation that
                // also recorded item problems still gets its list — the two are
                // not exclusive, and "failed: …" says nothing about which files.
                OperationProblems = ListProblems(handle.Problems);

                // Taken whatever the branch below, so no afterword outlives the
                // operation it was written for.
                var afterword = _afterwords.Remove(handle, out var says) ? says() : null;

                if (handle.State == OperationState.Failed && handle.Error is { } error)
                {
                    // Described the way the rest of the application describes a
                    // failure, rather than handing back a .NET exception message.
                    OperationStatus = "failed: " + Core.FileSystem.Failures.Describe(error, "finish that");
                }
                else if (handle.Problems.Count > 0)
                {
                    // **A batch that finished with items left behind must say
                    // so.** The rest of the files really did arrive, so this is
                    // not a failure — but clearing the line would report a clean
                    // run, and the whole point of carrying on past a locked file
                    // is that the person learns which ones were skipped.
                    OperationStatus = DescribeProblems(handle.Problems);
                }
                else if (afterword is not null)
                {
                    OperationStatus = afterword;
                }
                else if (ReferenceEquals(ActiveOperation, handle))
                {
                    OperationStatus = "";
                }

                // The bar follows whatever is still going, and only empties when
                // nothing is.
                if (ReferenceEquals(ActiveOperation, handle))
                    ActiveOperation = _running.Count > 0 ? _running[^1] : null;

                if (_running.Count == 0)
                {
                    HasOperationProgress = false;
                    OperationRate = "";
                    OperationPercent = 0;

                    _rateTimer?.Stop();
                    _rateTimer = null;
                }
            }), TaskScheduler.Default);
    }

    /// <summary>
    /// Lets go of every pane, and with them every file watcher and every timer
    /// the panes are holding.
    ///
    /// **Each pane keeps a watcher, two timers and a cancellation source**, and
    /// nothing had a way to release the lot at once — the window tears down
    /// per-group in two places and a test could not do it at all. A pane left
    /// running after its owner has gone still ticks, and the tick lands on a
    /// dispatcher that has moved on.
    ///
    /// And, since windows started closing while the application carries on, the
    /// subscriptions to the two sources that OUTLIVE a window. That half was
    /// missing and it was measured: after Dispose(), CutMarks.Mark still set
    /// this shell's CutPaths.
    /// </summary>
    public void Dispose()
    {
        // The rate tick outlives nothing: it holds this shell and would go on
        // firing into a window that has closed.
        _rateTimer?.Stop();
        _rateTimer = null;

        // **The two process-wide sources, and this is the half that was
        // missing.** CutMarks is static and the sharing provider is one object
        // for every window, so a shell that never unsubscribes stays reachable
        // from them for the life of the process — and its handlers went on
        // running after the panes below had been torn down.
        CutMarks.Changed -= _onCutMarks;

        if (_onSharingChanged is not null && _sharing is not null)
            _sharing.Changed -= _onSharingChanged;

        Sidebar.Dispose();

        Left?.DisposeAll();
        Right?.DisposeAll();
    }
}
