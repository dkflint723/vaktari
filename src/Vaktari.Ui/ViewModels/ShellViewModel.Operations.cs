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

    // ---- the bar it all reports through -----------------------------
    //
    // **Split from the list above, in another file.** The bar carried the
    // status, the percentage and the retry offer while the list carried what
    // was running — and _concurrentOperations sat on one side of that line
    // with RefreshConcurrentOperations on the other. One concern, one file.

    [ObservableProperty] private string _operationStatus = "";
    [ObservableProperty] private IOperationHandle? _activeOperation;

    /// <summary>
    /// Whether the transfer bar is on screen.
    ///
    /// **The bar used to follow ActiveOperation alone, so every failure was
    /// written and hidden in the same instant.** The completion handler sets
    /// the message — "failed: …", or which files were left behind — and then,
    /// six lines later, clears ActiveOperation because nothing is running any
    /// more. The bar went with it. The comment above that message says "a
    /// failure stays on screen"; it could not.
    ///
    /// So: visible while something is running, and visible while there is
    /// something to say.
    /// </summary>
    public bool ShowOperationBar => ActiveOperation is not null || OperationStatus.Length > 0;

    /// <summary>True only in the after-the-fact state, where there is a message
    /// but nothing left to pause or cancel.</summary>
    public bool OperationFinished => ActiveOperation is null && OperationStatus.Length > 0;

    /// <summary>
    /// How far along, 0 to 1, for the bar.
    ///
    /// **There was no bar.** The line counted items and bytes — "34/1200
    /// 1.2 GiB/4.9 GiB" — which is the one thing a person can work out by
    /// looking at it twice, and reading two fractions in a monospace line is
    /// not how anybody judges "nearly done".
    /// </summary>
    [ObservableProperty] private double _operationPercent;

    /// <summary>
    /// Whether there is a fraction worth drawing.
    ///
    /// A trash and a delete report a count and no bytes at all, so their bar
    /// would sit at zero for the whole run and then vanish — which is what a
    /// hung operation looks like. Item counts fill it instead, and where there
    /// is neither the bar stays away rather than lying.
    /// </summary>
    [ObservableProperty] private bool _hasOperationProgress;

    /// <summary>
    /// "4.2 MiB/s · about 2 min left", or as much of it as can be said.
    ///
    /// Separate from the status line because it answers a different question:
    /// that one says what is happening, this one says how long it will go on.
    /// Empty rather than absent when there is nothing to say — a speed that
    /// appears and disappears as a copy crosses a slow patch is worse than one
    /// that is simply not there yet.
    /// </summary>
    [ObservableProperty] private string _operationRate = "";

    /// <summary>
    /// "2 running" while more than one operation is going at once, and empty
    /// while there is one or none.
    ///
    /// **The bar shows one operation and never said which of how many.**
    /// Nothing serialises these — <see cref="_running"/> is a list, and its own
    /// comment records why: a second operation used to take the first's slot.
    /// The bar follows the newest, so the line, the percentage, the speed,
    /// Pause and Cancel all belong to one handle while another goes on writing
    /// with nothing on screen for it at all. Measured here by
    /// ConcurrentOperationsTests, which adopts two handles into one shell,
    /// finds ActiveOperation on the newer of them, and reads the bar.
    ///
    /// The count leads to the rows beside it: every engine now records what
    /// KIND of work its handle is doing, which is what a per-operation sentence
    /// was missing, and <see cref="RunningOperations"/> is the list this count
    /// counts.
    ///
    /// Empty rather than "1 running" for the ordinary case: the bar is already
    /// the one operation, and a badge that is on screen for every single copy
    /// stops being read by the time it matters.
    /// </summary>
    [ObservableProperty] private string _concurrentOperations = "";

    /// <summary>
    /// A row per running operation: what each is doing, and a cancel that
    /// reaches that one.
    ///
    /// **The count said there were others and named none of them.** The bar
    /// follows the newest handle, so the line, the fraction, the speed, Pause
    /// and Cancel all belong to that one — and CancelOperation is
    /// <c>ActiveOperation?.Cancel()</c>, so the only way to stop the copy
    /// underneath was to wait for the one on top to finish. Paste a large
    /// folder, start a second one, and the first is unreachable for as long as
    /// the second runs.
    ///
    /// Rebuilt in <see cref="RefreshConcurrentOperations"/>, off the same
    /// state-filtered list the count is taken from, so a row and the number
    /// beside it can never disagree — and a handle that has finished but is
    /// still lingering in <see cref="_running"/> gets neither.
    ///
    /// A whole new list rather than a mutated ObservableCollection: it is
    /// rebuilt only when an operation starts or ends, it holds a handful of
    /// rows, and a row carries nothing worth preserving across a rebuild.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<RunningOperationRow> _runningOperations = [];

    /// <summary>
    /// Whether the list is worth offering, on the same threshold as the count:
    /// with one operation running the bar IS that operation, and its own Cancel
    /// is already the per-operation cancel.
    ///
    /// **The threshold was decided with the 2-to-1 transition in front of it,
    /// measured on a headless window with the flyout open.** When one of two
    /// ends, this goes false and the button disappears — but the popup already
    /// open stays open, showing the survivor's row with its Cancel still live,
    /// and the completion continuation below has by then pointed
    /// <see cref="ActiveOperation"/> at that same survivor, so the bar's own
    /// Cancel reaches it too. What goes is a second way IN, not the only way
    /// out of anything.
    ///
    /// Against <c>&gt; 0</c>, which would hold the button still across that
    /// moment: it also puts an Operations button on the bar for every single
    /// copy — measured at 143px on this machine, taken off a status line that
    /// had 310px left at a 1000-wide window — to open a list of one row saying
    /// what the bar beside it is already saying.
    /// </summary>
    public bool CanShowRunningOperations => RunningOperations.Count > 1;

    partial void OnRunningOperationsChanged(IReadOnlyList<RunningOperationRow> value)
        => OnPropertyChanged(nameof(CanShowRunningOperations));

    /// <summary>
    /// The offer to go again on what an operation could not do, or null.
    ///
    /// **Set and cleared in the same place the bar's message is**, which is the
    /// whole of keeping it honest: an offer that outlived its sentence would
    /// reappear underneath the NEXT operation's failure, attached to work
    /// nobody was looking at. Every branch that writes OperationStatus decides
    /// this too.
    /// </summary>
    [ObservableProperty] private Core.FileSystem.RetryOffer? _retryable;

    /// <summary>The pane to hang the retry's progress on, so it reports where
    /// the original did.</summary>
    private PaneViewModel? _retryPane;

    public bool CanRetryOperation => Retryable is not null;

    /// <summary>
    /// The count is what the button will ATTEMPT, which is not the number of
    /// problems: a folder that could not be created reports every one of its
    /// planned descendants, and "Retry 431" for one unreadable folder says
    /// nothing about what pressing it does.
    ///
    /// **This read "retry 431", and the dialogs beside it read "Cancel".** The
    /// transfer bar was written in the lower-case chrome voice the column
    /// headings used, so one row of the window disagreed with every other about
    /// how a button is spelled. Sentence case is the rule now, everywhere;
    /// LabelCasingTests holds it.
    /// </summary>
    public string RetryLabel => Retryable is { } offer ? $"Retry {offer.Count}" : "Retry";

    /// <summary>
    /// Whether going again with administrator rights would mean anything.
    ///
    /// Both halves are needed and neither implies the other: the engine says
    /// whether any of the failures were about permission at all, and the
    /// launcher says whether this machine has a route to ask for rights — a
    /// desktop with no pkexec has none, and a button offering one would be a
    /// button that fails.
    /// </summary>
    public bool CanRetryAsAdministrator
        => Retryable?.AsAdministrator is not null && _launcher is { CanElevate: true };

    /// <summary>
    /// Its own count, and it can be smaller than the plain retry's: a batch
    /// that lost one file to a program holding it open and three to a protected
    /// folder reads "Retry 4" beside "Retry 3 as administrator". Both numbers
    /// are true, and the words are what tell them apart.
    /// </summary>
    public string RetryAsAdministratorLabel
        => Retryable?.AsAdministrator is { } request
            ? $"Retry {request.Sources.Count} as administrator"
            : "Retry as administrator";

    partial void OnRetryableChanged(Core.FileSystem.RetryOffer? value)
    {
        OnPropertyChanged(nameof(CanRetryOperation));
        OnPropertyChanged(nameof(RetryLabel));
        OnPropertyChanged(nameof(CanRetryAsAdministrator));
        OnPropertyChanged(nameof(RetryAsAdministratorLabel));
    }

    /// <summary>
    /// Everything the last finished operation left behind, or empty.
    ///
    /// **Set and cleared in the same three places the bar's message is**, for
    /// exactly the reason the retry offer is: a list that outlived its sentence
    /// would still be one click away underneath the NEXT operation's message,
    /// listing files nobody is looking at any more.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ProblemRow> _operationProblems = [];

    public bool CanShowProblems => OperationProblems.Count > 0;

    partial void OnOperationProblemsChanged(IReadOnlyList<ProblemRow> value)
        => OnPropertyChanged(nameof(CanShowProblems));

    /// <summary>
    /// Goes again on the failures, as an ordinary operation: it gets the bar,
    /// the progress, the pause and the cancel that any other one does, and it
    /// can itself leave something behind and offer another retry.
    /// </summary>
    [RelayCommand]
    private void RetryOperation()
    {
        if (Retryable is not { } offer) return;

        // Taken first. The new operation writes its own line to the bar, and an
        // offer still standing while that runs is an offer for work already
        // being redone.
        Retryable = null;
        OperationStatus = "";

        // The list belongs to the sentence being replaced. The new operation
        // writes its own, including its own list if it leaves anything behind.
        OperationProblems = [];

        (_retryPane ?? ActiveTab)?.Adopt(offer.Again());
    }

    /// <summary>
    /// The same failures, handed to a second copy of this program that the
    /// SYSTEM has agreed to start with administrator rights.
    ///
    /// **Vaktari holds no rights of its own here either.** The consent dialog
    /// is Windows' or polkit's, this process stays exactly as privileged as it
    /// was, and a refusal ends with nothing having happened — the same
    /// arrangement as "Run as administrator" on a file.
    ///
    /// The offer is taken first, as the plain retry does, so an offer still
    /// standing cannot be pressed twice while the first run is being consented
    /// to. The sentence is replaced rather than cleared: the consent dialog can
    /// sit on screen for as long as somebody takes to read it, and an empty bar
    /// underneath it says the retry did nothing.
    /// </summary>
    [RelayCommand]
    private void RetryAsAdministrator()
    {
        if (Retryable?.AsAdministrator is not { } request
            || _launcher is not { CanElevate: true } launcher) return;

        Retryable = null;

        // The list belongs to the sentence being replaced, exactly as it does
        // for the plain retry.
        OperationProblems = [];

        OperationStatus = "waiting for administrator…";

        (_retryPane ?? ActiveTab)?.Adopt(
            Core.FileSystem.ElevatedRun.Start(launcher, request));
    }

    partial void OnActiveOperationChanged(IOperationHandle? value)
    {
        NotifyOperationBar();

        // An operation that has just ENDED may have been a trash or a restore,
        // and the bin's glyph follows what it holds. One directory entry, and
        // this is the single point every file operation passes through — the
        // alternative is remembering to call it at each of the four sites that
        // change the bin, which is the kind of list that grows a fifth.
        if (value is null)
        {
            Sidebar.RefreshBinState();

            // **And the drives' free space, which that operation has just
            // moved.** The sidebar rebuilds on device arrivals and pins, none
            // of which a copy is, so a drive row and its tooltip reported the
            // space free before the copy started until something unrelated
            // happened to plug in. Same funnel and the same argument as the bin
            // above it: a copy, a move, a delete and a restore all change this
            // and all pass through here.
            //
            // Not awaited: this is a property-changed hook, and the figure
            // arriving a stat later is the point — nothing downstream of the
            // operation bar is waiting on it.
            _ = Sidebar.RefreshCapacityAsync();
        }
    }
    partial void OnOperationStatusChanged(string value) => NotifyOperationBar();

    private void NotifyOperationBar()
    {
        OnPropertyChanged(nameof(ShowOperationBar));
        OnPropertyChanged(nameof(OperationFinished));

        // The two buttons answer from the handle, so they have to be re-asked
        // when the handle changes -- including to null, where both go false.
        CancelOperationCommand.NotifyCanExecuteChanged();
        PauseOperationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Puts the last message away. Needed because the bar now outlives the
    /// operation: without it a failure would sit there until the next copy.
    /// </summary>
    [RelayCommand]
    private void DismissOperationStatus()
    {
        OperationStatus = "";

        // Dismissing the sentence dismisses the offer attached to it.
        Retryable = null;

        // And the list of what it was about.
        OperationProblems = [];
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation() => ActiveOperation?.Cancel();

    /// <summary>
    /// **A button that does nothing reads as the application being broken.**
    /// Windows recycles a whole batch through one blocking SHFileOperation, so
    /// there is nothing to cancel from out here; the handle says so and the
    /// button greys out rather than accepting a press that goes nowhere.
    /// </summary>
    private bool CanCancelOperation() => ActiveOperation?.CanCancel ?? false;

    /// <inheritdoc cref="CanCancelOperation"/>
    private bool CanPauseOperation() => ActiveOperation?.CanPause ?? false;

    /// <summary>
    /// Pauses or resumes the running operation.
    ///
    /// **Pause was fully implemented and unreachable.** OperationHandle has a
    /// real gate, and BOTH engines await it between items and inside the byte
    /// loop — so the machinery for stopping a large copy mid-flight has always
    /// worked and nothing in the application could ask for it. The interface's
    /// own comment justifies handles existing on the grounds that "pause and
    /// reorder cannot be retrofitted onto a Task", which was true and was the
    /// reason a feature nobody could use had been paid for in full.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPauseOperation))]
    private void PauseOperation()
    {
        if (ActiveOperation is not { } operation) return;

        if (operation.State == OperationState.Paused) operation.Resume();
        else operation.Pause();

        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>
    /// One button, two words — the state it is in decides which.
    ///
    /// **Both were lower case while every dialog button was not.** Sentence
    /// case is the one rule now; LabelCasingTests holds it.
    /// </summary>
    public string PauseLabel
        => ActiveOperation?.State == OperationState.Paused ? "Resume" : "Pause";
}
