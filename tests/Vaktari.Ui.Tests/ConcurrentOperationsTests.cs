using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the transfer bar says when there is more than one transfer.
///
/// **It said nothing, and showed one of them.** Nothing serialises these — the
/// shell keeps a LIST of running handles precisely because a second operation
/// used to take the first's slot — and the bar follows the newest, so the
/// filename, the percentage, the speed, Pause and Cancel all belong to one
/// handle while another goes on writing with no line of its own. The one number
/// that would have said so already existed for the close-confirmation question
/// and was never asked during the transfer.
///
/// These tests adopt two handles into one shell, which is the situation the
/// list's own comment describes: a paste still running when a delete starts.
/// </summary>
public sealed class ConcurrentOperationsTests : OwnedViewModels
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
    /// Begun rather than freshly constructed: a Queued handle counts as live
    /// too, so a test that never began one would pass against a count that read
    /// the wrong state.
    /// </summary>
    private static OperationHandle Running(ShellViewModel shell)
    {
        var handle = new OperationHandle();

        shell.ActiveTab!.Adopt(handle);
        handle.Begin(itemsTotal: 1, totalBytes: 1024);

        return handle;
    }

    /// <summary>
    /// Ends every handle and lets the shell's completion continuations land.
    ///
    /// **Every test here must do this.** A running operation leaves the shell's
    /// rate DispatcherTimer ticking — it is only stopped once the list is empty
    /// — and a tick arriving after a headless test session has ended lands on a
    /// dispatcher that has moved on, which is the fault OwnedViewModels exists
    /// for. The drain is a loop rather than one RunJobs because completion goes
    /// through a pool continuation before it posts.
    /// </summary>
    private static async Task DrainAsync(ShellViewModel shell, params OperationHandle[] handles)
    {
        foreach (var handle in handles)
            if (InFlight.Unfinished(handle.State)) handle.Complete();

        // Wall-clock rather than a number of turns, for the reason
        // OperationProgressTests.A_running_copy_drives_the_bar records: the
        // completion goes through a pool continuation before it posts, and
        // fifty immediate yields did not outlast one on a loaded CI runner.
        for (var i = 0; i < 400 && shell.ActiveOperation is not null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The ordinary case, and the reason the count is not "1 running": the bar
    /// IS the one operation, and a badge on screen for every single copy is not
    /// read by the time it means something.
    /// </summary>
    [AvaloniaFact]
    public async Task One_transfer_gets_no_count()
    {
        var shell = Shell();

        var only = Running(shell);

        Assert.Equal("", shell.ConcurrentOperations);

        await DrainAsync(shell, only);
    }

    /// <summary>**The whole finding.** Two are going and the bar shows one.</summary>
    [AvaloniaFact]
    public async Task A_second_transfer_says_how_many_are_running()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        Assert.Equal("2 running", shell.ConcurrentOperations);

        // And the bar is showing the newest of them, which is what the count is
        // there to disclose.
        Assert.Same(second, shell.ActiveOperation);

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// **And the count has to TELL the view.** Everything else here reads the
    /// property directly and the markup tests read an attribute, so a plain
    /// auto-property satisfies the lot of them — and raises nothing, leaving
    /// the bar blank for the life of the window, which is verbatim the fault
    /// this finding was. OperationBarTests pins the same thing for the
    /// properties beside this one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_transfer_tells_the_view()
    {
        var shell = Shell();
        var raised = new List<string?>();

        var first = Running(shell);

        // Subscribed after the first, so the notification below can only have
        // come from the second. The first writes "" over "" and raises
        // nothing at all: with the handler attached ahead of it instead,
        // ConcurrentOperations was absent from this list until the second
        // transfer started.
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var second = Running(shell);

        Assert.Contains(nameof(ShellViewModel.ConcurrentOperations), raised);

        await DrainAsync(shell, first, second);
    }

    /// <summary>Three is three, so the count is a count and not a flag.</summary>
    [AvaloniaFact]
    public async Task The_count_is_the_number_running()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);
        var third = Running(shell);

        Assert.Equal("3 running", shell.ConcurrentOperations);

        await DrainAsync(shell, first, second, third);
    }

    /// <summary>
    /// And it goes away again. Without this the count would be written once and
    /// left on the bar for the rest of the session, saying two while one runs.
    /// </summary>
    [AvaloniaFact]
    public async Task Finishing_one_of_two_takes_the_count_away()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        Assert.Equal("2 running", shell.ConcurrentOperations);

        second.Complete();

        // Wall-clock rather than a number of turns, for the reason
        // OperationProgressTests.A_running_copy_drives_the_bar records: the
        // completion goes through a pool continuation before it posts, and
        // fifty immediate yields did not outlast one on a loaded CI runner.
        for (var i = 0; i < 400 && shell.ConcurrentOperations.Length > 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Equal("", shell.ConcurrentOperations);

        // The survivor is what the bar follows now.
        Assert.Same(first, shell.ActiveOperation);

        await DrainAsync(shell, first, second);
    }

    /// <summary>
    /// **A finished handle stays in the list for a moment**, because the shell
    /// drops it on a continuation posted to the UI thread — the list's own
    /// comment says so and this measures it: nothing here pumps the dispatcher
    /// between the completion and the third adoption, and the completed handle
    /// is still in the list when the count is taken.
    ///
    /// So the count tests state, exactly as the close-confirmation question
    /// does. Taking the list's Count instead reads "3 running" with two going.
    /// </summary>
    [AvaloniaFact]
    public async Task A_handle_that_has_finished_is_not_counted_while_it_lingers()
    {
        var shell = Shell();

        var first = Running(shell);
        var second = Running(shell);

        // Completed, and deliberately not drained: the continuation that takes
        // it out of the list cannot run until something pumps the dispatcher.
        second.Complete();

        Assert.Equal(OperationState.Completed, second.State);

        var third = Running(shell);

        Assert.Equal("2 running", shell.ConcurrentOperations);

        await DrainAsync(shell, first, second, third);
    }

    /// <summary>
    /// **A run can end three ways and only one of them is Completed.** The test
    /// above lingers a completed handle, which a hand-rolled "not Completed"
    /// gets right by accident; a copy that threw and a copy that was cancelled
    /// linger in the list exactly the same way, and counting either of them
    /// would put "4 running" on the bar with two going.
    ///
    /// Same no-pump shape as above: nothing here lets the continuations that
    /// take these two out of the list run before the count is read.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failed_or_cancelled_operation_is_not_counted_while_it_lingers()
    {
        var shell = Shell();

        var first = Running(shell);

        var failed = Running(shell);
        failed.Failed(new IOException("boom"));
        Assert.Equal(OperationState.Failed, failed.State);

        var cancelled = Running(shell);
        cancelled.Cancelled();
        Assert.Equal(OperationState.Cancelled, cancelled.State);

        var last = Running(shell);

        Assert.Equal("2 running", shell.ConcurrentOperations);

        await DrainAsync(shell, first, failed, cancelled, last);
    }

    // ---- the bar shows it --------------------------------------------------

    private static XElement OperationBar()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
                    .Descendants(Avalonia + "Border")
                    .Single(e => (string?)e.Attribute("IsVisible") == "{Binding ShowOperationBar}");

    private static XElement Count()
        => Assert.Single(
            OperationBar().Descendants(Avalonia + "TextBlock"),
            e => (string?)e.Attribute("Text") == "{Binding ConcurrentOperations}");

    /// <summary>
    /// The count reaches the window. Everything above is view-model state, and
    /// a view model nothing binds to is the fault this finding was.
    /// </summary>
    [Fact]
    public void The_bar_shows_the_count()
    {
        var count = Count();

        // Docked right, like the progress bar and the speed. Not for room —
        // this one sizes to its text and cannot trim — but for its PLACE: a
        // DockPanel child with no Dock takes the default, which is Left, and
        // laid out that way the count arrives at the far left of the bar, in
        // front of the status line it is there to qualify.
        Assert.Equal("Right", (string?)count.Attribute("DockPanel.Dock"));
    }

    /// <summary>
    /// "2 running" says how many there are and not which one is on the line, so
    /// the answer to that is a hover away rather than nowhere.
    /// </summary>
    [Fact]
    public void The_count_says_which_of_them_the_line_belongs_to()
    {
        Assert.Equal("This line follows the newest of them",
                     (string?)Count().Attribute("ToolTip.Tip"));

        // At the same wait as the status line's own tip, which is the other
        // thing on this bar a hover can ask about: one bar, one hover speed.
        var status = Assert.Single(
            OperationBar().Descendants(Avalonia + "TextBlock"),
            e => (string?)e.Attribute("Text") == "{Binding OperationStatus}");

        Assert.Equal((string?)status.Attribute("ToolTip.ShowDelay"),
                     (string?)Count().Attribute("ToolTip.ShowDelay"));
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
