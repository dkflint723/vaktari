using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The list behind the transfer bar's count, and the cancel on each of its
/// rows.
///
/// **"2 running" said how many there were and named none of them, and there was
/// exactly one Cancel in the whole application.** The bar follows the NEWEST
/// handle — the shell keeps a list precisely because a second operation used to
/// take the first's slot — and <c>CancelOperation</c> is
/// <c>ActiveOperation?.Cancel()</c>. So paste a large folder, start a second
/// transfer, and the first became unreachable: no line, no fraction, no speed
/// and no way to stop it short of waiting for the one on top to finish.
///
/// Rank 95 shipped the count, the bar and the speed. This is the rest of the
/// same plan: somewhere to see them as a list, each row naming what it is
/// doing, each with a cancel that reaches THAT operation.
///
/// These adopt handles into one shell, which is the situation the shell's own
/// list comment describes: a paste still running when a delete starts.
/// </summary>
public sealed class RunningOperationsListTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    /// <summary>
    /// A handle the shell is tracking and that is genuinely under way.
    ///
    /// Begun rather than freshly constructed, for the reason
    /// ConcurrentOperationsTests records: a Queued handle counts as live too, so
    /// a test that never began one would pass against a filter that read the
    /// wrong state.
    /// </summary>
    private static OperationHandle Running(
        ShellViewModel shell,
        OperationKind kind = OperationKind.Other,
        IReadOnlyList<string>? paths = null,
        bool canCancel = true,
        bool canPause = true)
    {
        var handle = new OperationHandle
        {
            Kind = kind,
            Paths = paths ?? [],
            CanCancel = canCancel,
            CanPause = canPause,
        };

        shell.ActiveTab!.Adopt(handle);
        handle.Begin(itemsTotal: 1, totalBytes: 1024);

        return handle;
    }

    /// <summary>
    /// Ends every handle and lets the shell's completion continuations land.
    ///
    /// **Every test here must do this**, exactly as in ConcurrentOperationsTests:
    /// a running operation leaves the shell's rate DispatcherTimer ticking — it
    /// is only stopped once the list is empty — and a tick arriving after a
    /// headless session has ended lands on a dispatcher that has moved on.
    ///
    /// Wall-clock rather than a number of turns, because the completion goes
    /// through a pool continuation before it posts.
    /// </summary>
    private static async Task DrainAsync(ShellViewModel shell, params OperationHandle[] handles)
    {
        foreach (var handle in handles)
            if (InFlight.Unfinished(handle.State)) handle.Complete();

        for (var i = 0; i < 400 && shell.ActiveOperation is not null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    // ---- the list ----------------------------------------------------------

    /// <summary>**The whole finding.** Two are going and there is a row for
    /// each, not one bar for whichever started last.</summary>
    [AvaloniaFact]
    public async Task Every_running_operation_gets_a_row()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        Assert.Equal(2, shell.RunningOperations.Count);

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// And the way in appears only when there is more than one, on the same
    /// threshold as the count beside it: with one operation running the bar IS
    /// that operation, and its own Cancel is already the per-operation cancel.
    /// </summary>
    [AvaloniaFact]
    public async Task One_operation_offers_no_list()
    {
        var shell = Shell();

        var only = Running(shell);

        Assert.False(shell.CanShowRunningOperations);

        var second = Running(shell);

        Assert.True(shell.CanShowRunningOperations);

        await DrainAsync(shell, only, second);
    }

    /// <summary>
    /// **And the list has to TELL the view.** Everything else here reads the
    /// properties directly, so a computed getter that nothing raises would
    /// satisfy the lot of them and leave the button hidden for the life of the
    /// window — which is the shape of the fault this finding was.
    /// </summary>
    [AvaloniaFact]
    public async Task The_list_tells_the_view()
    {
        var shell = Shell();
        var raised = new List<string?>();

        var first = Running(shell);

        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var second = Running(shell);

        Assert.Contains(nameof(ShellViewModel.RunningOperations), raised);
        Assert.Contains(nameof(ShellViewModel.CanShowRunningOperations), raised);

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// **A finished handle stays in the shell's list for a moment**, because it
    /// is dropped on a continuation posted to the UI thread. A row for it would
    /// offer a Cancel for work that is already over, and the count beside the
    /// rows — which tests state — would disagree with them.
    ///
    /// Same no-pump shape as the count's own test: nothing here lets the
    /// continuation run before the rows are read.
    /// </summary>
    [AvaloniaFact]
    public async Task A_finished_operation_gets_no_row_while_it_lingers()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        second.Complete();
        Assert.Equal(OperationState.Completed, second.State);

        var third = Running(shell);

        Assert.Equal(2, shell.RunningOperations.Count);
        Assert.Equal("2 running", shell.ConcurrentOperations);

        await DrainAsync(shell, first, second, third);
    }

    /// <summary>
    /// **The moment the threshold turns**, which neither of the two static
    /// states above reaches: two are running, one ends, and the button that
    /// opens the list goes away with it.
    ///
    /// What this asserts is that nothing goes with the button. The survivor
    /// still has a row and the row still has a cancel, and the completion
    /// continuation has moved <c>ActiveOperation</c> onto that survivor — so
    /// the bar's own Cancel reaches it. The list stops being offered because
    /// the bar has become the operation again, not because the operation
    /// stopped being reachable.
    ///
    /// The NEWEST of the two is the one that ends, because that is the handle
    /// the bar was following and therefore the case where ActiveOperation has
    /// to move at all.
    ///
    /// Waits on the list itself under a wall-clock ceiling, not on a number of
    /// dispatcher turns: the completion goes through a pool continuation before
    /// it posts. The count is 2 before and 1 after, so no transition can
    /// satisfy the wait early.
    /// </summary>
    [AvaloniaFact]
    public async Task When_one_of_two_ends_the_bar_takes_over_the_survivor()
    {
        var shell = Shell();

        var survivor = Running(shell);
        var ending = Running(shell);

        Assert.Same(ending, shell.ActiveOperation);
        Assert.True(shell.CanShowRunningOperations);

        ending.Complete();

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (shell.RunningOperations.Count > 1 && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Single(shell.RunningOperations);
        Assert.False(shell.CanShowRunningOperations);
        Assert.Equal("", shell.ConcurrentOperations);

        // The row that is left is the survivor's, and it can still be stopped.
        Assert.True(shell.RunningOperations[0].CanCancel);

        // And the bar has taken that same operation over, which is what makes
        // the button's going a way IN disappearing rather than the only way out.
        Assert.Same(survivor, shell.ActiveOperation);

        shell.CancelOperationCommand.Execute(null);

        Assert.True(survivor.Token.IsCancellationRequested,
                    "the bar's cancel did not reach the operation it took over");

        await DrainAsync(shell, survivor, ending);
    }

    /// <summary>
    /// And the rows go away with the operations, or the flyout would offer a
    /// cancel for a copy that finished half an hour ago.
    /// </summary>
    [AvaloniaFact]
    public async Task Finishing_everything_empties_the_list()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        await DrainAsync(shell, first, second);

        Assert.Empty(shell.RunningOperations);
    }

    // ---- the cancel on the row ---------------------------------------------

    /// <summary>
    /// **The point of the rows.** The bar is following the newest, so the first
    /// operation is the one that had no way to be stopped; its row's cancel
    /// reaches it and leaves the other alone.
    ///
    /// Asserted on the token rather than on State, because that is what Cancel
    /// actually does: <see cref="OperationHandle.Cancel"/> opens the pause gate
    /// and cancels the source, and the state does not become Cancelled until
    /// the ENGINE notices and says so. There is no engine behind a hand-built
    /// handle, so a test that waited for Cancelled would wait for ever.
    /// </summary>
    [AvaloniaFact]
    public async Task A_rows_cancel_reaches_its_own_operation()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        Assert.Same(second, shell.ActiveOperation);

        var row = shell.RunningOperations[0];
        row.CancelCommand.Execute(null);

        Assert.True(first.Token.IsCancellationRequested,
                    "the row's cancel did not reach the operation it belongs to");
        Assert.False(second.Token.IsCancellationRequested,
                     "it reached the one the bar happens to be following instead");

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// A row asks its own handle whether a cancel means anything.
    ///
    /// **A Windows recycle is one blocking SHFileOperation**: no loop between
    /// items to read a token in, and nothing the shell passes a token to. A
    /// Cancel on that row would accept the press and do nothing, which reads as
    /// the application being broken rather than as the operation being
    /// uninterruptible — which is the fault the per-handle flag was added for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_that_cannot_be_cancelled_says_so()
    {
        var shell = Shell();

        var stoppable = Running(shell);
        var recycle = Running(shell, canCancel: false);

        Assert.True(shell.RunningOperations[0].CanCancel);
        Assert.False(shell.RunningOperations[1].CanCancel);

        await DrainAsync(shell, stoppable, recycle);
    }

    // ---- the pause on the row ----------------------------------------------

    /// <summary>
    /// **Pause was the bar's alone, and the bar follows the newest.** The first
    /// operation is the one that had no way to be paused; its row's button
    /// reaches it, leaves the other alone, and says which word comes next.
    /// </summary>
    [AvaloniaFact]
    public async Task A_rows_pause_reaches_its_own_operation_and_says_so()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        Assert.Same(second, shell.ActiveOperation);

        var row = shell.RunningOperations[0];

        Assert.True(row.CanPause);
        Assert.Equal("Pause", row.PauseLabel);

        row.PauseCommand.Execute(null);

        Assert.Equal(OperationState.Paused, first.State);
        Assert.Equal(OperationState.Running, second.State);
        Assert.True(row.IsPaused);
        Assert.Equal("Resume", row.PauseLabel);

        row.PauseCommand.Execute(null);

        Assert.Equal(OperationState.Running, first.State);
        Assert.Equal("Pause", row.PauseLabel);

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// The word follows the OPERATION, not the button. Paused from the bar —
    /// or by anything else holding the handle — the row says Resume, and it
    /// tells the view so; a computed getter nothing raised would leave the
    /// button reading Pause over a paused operation.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_follows_a_pause_made_elsewhere()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        // The bar's own Pause reaches the newest handle, which is the second.
        var row = shell.RunningOperations[1];
        var told = new List<string?>();

        row.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        shell.PauseOperationCommand.Execute(null);

        Assert.Equal(OperationState.Paused, second.State);
        Assert.Equal("Resume", row.PauseLabel);
        Assert.Contains(nameof(RunningOperationRow.PauseLabel), told);
        Assert.Equal("Pause", shell.RunningOperations[0].PauseLabel);

        await DrainAsync(shell, first, second);
    }

    /// <summary>The same question the cancel asks, of the same handle: a
    /// blocking recycle has no gate to wait at.</summary>
    [AvaloniaFact]
    public async Task A_row_that_cannot_be_paused_says_so()
    {
        var shell = Shell();

        var pausable = Running(shell);
        var recycle = Running(shell, canPause: false);

        Assert.True(shell.RunningOperations[0].CanPause);
        Assert.False(shell.RunningOperations[1].CanPause);

        await DrainAsync(shell, pausable, recycle);
    }

    /// <summary>
    /// The list is rebuilt whenever an operation starts or ends, so a row is
    /// replaced often. A replaced row lets go of its handle; one that did not
    /// would go on following a label nothing shows for as long as the handle
    /// lived, and nothing would say so.
    /// </summary>
    [AvaloniaFact]
    public async Task A_replaced_row_stops_following_its_operation()
    {
        var shell = Shell();

        var first = Running(shell);
        var stale = shell.RunningOperations[0];

        var second = Running(shell);

        Assert.NotSame(stale, shell.RunningOperations[0]);

        first.Pause();

        Assert.False(stale.IsPaused, "a row no longer shown went on following its handle");
        Assert.True(shell.RunningOperations[0].IsPaused);

        first.Resume();

        await DrainAsync(shell, first, second);
    }

    // ---- what a row says ---------------------------------------------------

    /// <summary>
    /// The sentence reaches the row from the handle, which is the join this
    /// finding needed: the kind is the engine's, the paths are the engine's,
    /// and the words are the window's.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_names_what_its_operation_is_doing()
    {
        var shell = Shell();

        var copy = Running(
            shell, OperationKind.Copy,
            [Path.Combine("src", "one.txt"), Path.Combine("src", "two.txt"),
             Path.Combine("D:", "Photos")]);

        Assert.Equal("Copying 2 items to Photos", shell.RunningOperations[0].Description);

        await DrainAsync(shell, copy);
    }

    /// <summary>
    /// One source is named outright and several are counted — the same choice
    /// the problems list under "Details" makes, and for the same reason: a row
    /// in a popup has no room for a path.
    /// </summary>
    [Fact]
    public void A_copy_names_its_destination()
    {
        Assert.Equal(
            "Copying one.txt to Photos",
            RunningOperationRow.Describe(
                OperationKind.Copy,
                [Path.Combine("src", "one.txt"), Path.Combine("D:", "Photos")]));

        Assert.Equal(
            "Copying 3 items to Photos",
            RunningOperationRow.Describe(
                OperationKind.Copy,
                ["a", "b", "c", Path.Combine("D:", "Photos")]));
    }

    /// <summary>A move is the other half of the same shape, and the verb is the
    /// only thing that separates the two rows on screen.</summary>
    [Fact]
    public void A_move_says_move()
    {
        Assert.Equal(
            "Moving one.txt to Archive",
            RunningOperationRow.Describe(
                OperationKind.Move,
                [Path.Combine("src", "one.txt"), Path.Combine("D:", "Archive")]));
    }

    /// <summary>
    /// A trash and a delete carry sources only — there is no second place — so
    /// every path they hold is counted, and neither sentence ends in a
    /// destination.
    ///
    /// The two are worth telling apart on a row above all: one is recoverable
    /// and the other is not.
    /// </summary>
    [Fact]
    public void Binning_and_destroying_read_differently()
    {
        Assert.Equal(
            "Moving 2 items to the bin",
            RunningOperationRow.Describe(OperationKind.Trash, ["a", "b"]));

        Assert.Equal(
            "Deleting notes.txt",
            RunningOperationRow.Describe(
                OperationKind.Delete, [Path.Combine("D:", "notes.txt")]));
    }

    /// <summary>
    /// A handle from something that is not one of the file engines still gets a
    /// row — it is holding the drive and is worth cancelling — and it is not
    /// given a verb it did not earn.
    /// </summary>
    [Fact]
    public void An_operation_of_no_particular_kind_is_not_invented_a_verb()
        => Assert.Equal("Working", RunningOperationRow.Describe(OperationKind.Other, ["a"]));

    /// <summary>
    /// **A copy with no paths at all is legal**, and it used to be the shape
    /// that threw: the destination is the LAST path, and indexing an empty list
    /// while drawing a flyout takes the window with it.
    ///
    /// Nothing in the application builds one — IOperationHandle.Paths says
    /// empty means "nowhere in particular", and it is the tests that take it up
    /// on that. The guard is against the shape the type allows.
    /// </summary>
    [Fact]
    public void A_transfer_with_no_paths_still_has_a_sentence()
    {
        Assert.Equal("Copying", RunningOperationRow.Describe(OperationKind.Copy, []));
        Assert.Equal("Moving to the bin", RunningOperationRow.Describe(OperationKind.Trash, []));

        // And a destination with nothing in front of it names the destination
        // rather than counting to zero: one path is the destination, so the
        // sources are what is left, which is none of them.
        Assert.Equal(
            "Copying to Photos",
            RunningOperationRow.Describe(OperationKind.Copy, [Path.Combine("D:", "Photos")]));
    }

    /// <summary>
    /// A destination handed in with a trailing separator names its folder, not
    /// nothing at all; a drive root, which has no leaf to find, keeps the path
    /// it came with.
    /// </summary>
    [Fact]
    public void A_folder_with_a_trailing_separator_is_still_named()
    {
        Assert.Equal(
            "Copying one.txt to Photos",
            RunningOperationRow.Describe(
                OperationKind.Copy,
                ["one.txt", Path.Combine("D:", "Photos") + Path.DirectorySeparatorChar]));

        // This platform's own root — D:\ here, / there — since what a root is
        // is the platform's to say.
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Equal(
            $"Copying one.txt to {root}",
            RunningOperationRow.Describe(OperationKind.Copy, ["one.txt", root]));
    }

    // ---- the bar shows it --------------------------------------------------

    private static XElement OperationBar()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
                    .Descendants(Avalonia + "Border")
                    .Single(e => (string?)e.Attribute("IsVisible") == "{Binding ShowOperationBar}");

    private static XElement ListButton()
        => Assert.Single(
            OperationBar().Descendants(Avalonia + "Button"),
            e => (string?)e.Attribute("IsVisible") == "{Binding CanShowRunningOperations}");

    /// <summary>
    /// The way in reaches the window. Everything above is view-model state, and
    /// a view model nothing binds to is the fault this finding was.
    /// </summary>
    [Fact]
    public void The_bar_offers_the_list()
    {
        var button = ListButton();

        Assert.Equal("Operations", (string?)button.Attribute("Content"));

        // Docked, like every other button on this bar: a horizontal run
        // measured against infinite width laid them out past the right edge of
        // the window, and this one is the only route to a per-operation cancel.
        Assert.Equal("Right", (string?)button.Attribute("DockPanel.Dock"));

        // And no Command: it is the button's own popup, so it takes no verb —
        // which is what keeps the bar's asserted order of commands, pinned by
        // OperationBarTests, reading exactly as it did.
        Assert.Null(button.Attribute("Command"));
    }

    /// <summary>
    /// The popup opens UPWARD, and it aligns LEFT. This bar is docked to the
    /// bottom of the window, so a Flyout's default Bottom placement is always
    /// constrained, and Avalonia's default PlacementConstraintAdjustment is All:
    /// it would be flipped, slid or resized back into view by the constraint
    /// solver rather than by anybody's decision.
    ///
    /// **The whole string, not its first three letters.** Which EDGE the list
    /// grows from is the half of this decision that can be wrong without
    /// anything looking broken until the window is narrow: measured on a
    /// headless window with two operations running, the room to the right of
    /// this button's left edge is 553px at 600, 800, 1000 and 1920 wide — a
    /// constant, because everything docked to its right sizes to itself —
    /// while the room to the left of its right edge is the window width less
    /// 410, which is 190px at 600. The list is 320 wide and this window's
    /// MinWidth is 560, so TopEdgeAlignedRight would be constrained on
    /// anything narrower than 730.
    ///
    /// The sibling flyout on this same bar — the problems list under
    /// "Details" — pins its own placement as a whole string, and to the
    /// opposite alignment, because it is declared fourth from the right of the
    /// docked run rather than ninth.
    /// </summary>
    [Fact]
    public void The_list_opens_upward_from_its_left_edge()
    {
        var flyout = ListButton().Descendants(Avalonia + "Flyout").Single();

        Assert.Equal("TopEdgeAlignedLeft", (string?)flyout.Attribute("Placement"));
    }

    /// <summary>
    /// One row per running operation, from the list the shell keeps rather than
    /// from the one handle the bar follows.
    ///
    /// The width is here because the placement above is argued from it: 320 is
    /// the number the room on either side of the button was measured against.
    /// </summary>
    [Fact]
    public void The_list_is_bound_to_every_running_operation()
    {
        var items = ListButton().Descendants(Avalonia + "ItemsControl").Single();

        Assert.Equal("{Binding RunningOperations}", (string?)items.Attribute("ItemsSource"));
        Assert.Equal("320", (string?)items.Attribute("Width"));
    }

    /// <summary>
    /// **The two halves of the finding, in the markup.** Each row says what its
    /// operation is doing, and carries a cancel bound to the ROW's command —
    /// not to the shell's, which reaches ActiveOperation and nothing else.
    ///
    /// The cancel's own LABEL is asserted here rather than left to
    /// LabelCasingTests, which checks the casing of labels that exist and would
    /// not notice this one going: it is the only per-operation cancel the
    /// application has, and a button with no content is a button nobody can
    /// name.
    /// </summary>
    [Fact]
    public void Every_row_names_its_operation_and_can_stop_it()
    {
        var row = ListButton().Descendants(Avalonia + "DataTemplate").Single();

        var text = row.Descendants(Avalonia + "TextBlock").Single();
        Assert.Equal("{Binding Description}", (string?)text.Attribute("Text"));

        var cancel = row.Descendants(Avalonia + "Button")
                        .Single(b => (string?)b.Attribute("Content") == "Cancel");
        Assert.Equal("{Binding CancelCommand}", (string?)cancel.Attribute("Command"));

        // Docked, so the sentence beside it ellipsizes instead of pushing the
        // cancel out of the popup — the fault the bar's own buttons had, and
        // the same fix.
        Assert.Equal("Right", (string?)cancel.Attribute("DockPanel.Dock"));

        // And it is hidden where it would do nothing: a Windows recycle is one
        // blocking call that reads no token.
        Assert.Equal("{Binding CanCancel}", (string?)cancel.Attribute("IsVisible"));
    }

    /// <summary>
    /// The pause on the row, bound to the ROW's command and the ROW's word:
    /// the bar's pause reaches the newest handle only, and a button whose
    /// label did not follow the operation would read Pause over a paused copy.
    /// </summary>
    [Fact]
    public void Every_row_can_pause_its_operation_and_the_word_follows_it()
    {
        var row = ListButton().Descendants(Avalonia + "DataTemplate").Single();

        var pause = row.Descendants(Avalonia + "Button")
                       .Single(b => (string?)b.Attribute("Command") == "{Binding PauseCommand}");

        Assert.Equal("{Binding PauseLabel}", (string?)pause.Attribute("Content"));
        Assert.Equal("{Binding CanPause}", (string?)pause.Attribute("IsVisible"));
        Assert.Equal("Right", (string?)pause.Attribute("DockPanel.Dock"));
    }

    /// <summary>
    /// The sentence is the fill child, so it takes what the cancel left — and
    /// it trims rather than growing the row, with the whole of it still
    /// readable. The same three assertions
    /// <c>OperationBarTests.The_status_line_takes_what_is_left_and_trims</c>
    /// makes of the bar's own line, for the same reason: a row that wrapped
    /// would push the rows under it down the popup.
    ///
    /// Centred, because LastChildFill stretches this to the cancel button's
    /// height and a stretched TextBlock draws its text at the top of that
    /// space.
    /// </summary>
    [Fact]
    public void A_rows_sentence_takes_what_is_left_and_trims()
    {
        var row = ListButton().Descendants(Avalonia + "DataTemplate").Single();
        var text = row.Descendants(Avalonia + "TextBlock").Single();

        // No Dock, so it fills. The cancel beside it is measured first.
        Assert.Null(text.Attribute("DockPanel.Dock"));

        Assert.Equal("CharacterEllipsis", (string?)text.Attribute("TextTrimming"));
        Assert.Null(text.Attribute("TextWrapping"));
        Assert.Equal("{Binding Description}", (string?)text.Attribute("ToolTip.Tip"));
        Assert.Equal("Center", (string?)text.Attribute("VerticalAlignment"));
    }

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
