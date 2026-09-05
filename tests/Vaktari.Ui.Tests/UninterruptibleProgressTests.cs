using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The bar during an operation that reports no sizes and cannot be stopped.
///
/// **A five-item trash read "0/5  0 B/0 B" from beginning to end.** Both engines
/// open a trash and a permanent delete with <c>totalBytes: 0</c> — nothing has
/// measured the files and nothing is going to — and the bar dropped the byte
/// figures only when the ITEM count was one or less. So the one operation with
/// no bytes to report was the one that reported them, twice, and both were zero
/// the whole way through.
///
/// **And both buttons under it did nothing.** Windows recycles a whole batch
/// through a single synchronous SHFileOperation: there is no loop between items
/// to await the pause gate in, and the shell reads no cancellation token.
/// Pressing Pause set a flag nothing would look at; pressing Cancel cancelled a
/// token nobody was passing. A button that accepts a press and does nothing
/// reads as the application being broken.
/// </summary>
public sealed class UninterruptibleProgressTests : OwnedViewModels
{
    private ShellViewModel Shell() => Own(new ShellViewModel(new Nothing()));

    /// <summary>Lists nothing. These are about the bar, not about a folder.
    /// </summary>
    private sealed class Nothing : IFileSystemProvider
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

    /// <summary>Adopts a handle the way a real operation arrives, and drains
    /// the dispatcher so the bar has read it.</summary>
    private static void Adopt(ShellViewModel shell, OperationHandle handle)
    {
        shell.Start(null, Path.GetTempPath());
        shell.ActiveTab!.Adopt(handle);

        Dispatcher.UIThread.RunJobs();
    }

    // ---- what the line says ------------------------------------------------

    /// <summary>The finding itself.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_batch_that_reports_no_sizes_shows_no_sizes()
    {
        var shell = Shell();
        var handle = new OperationHandle();

        Adopt(shell, handle);

        handle.Begin(itemsTotal: 5, totalBytes: 0);
        handle.ItemStarted("holiday.jpg");

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("0/5  holiday.jpg", shell.OperationStatus);
        Assert.DoesNotContain("0 B", shell.OperationStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trash names nothing: one SHFileOperation covers the batch, so there
    /// is no item to be "on". The count is the whole of what can be said, and
    /// the line must not trail off into the spaces where a name would go.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_batch_that_names_no_item_does_not_trail_off()
    {
        var shell = Shell();
        var handle = new OperationHandle();

        Adopt(shell, handle);

        handle.Begin(itemsTotal: 5, totalBytes: 0);
        handle.ItemFinished();

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("1/5", shell.OperationStatus);
    }

    /// <summary>One item and no sizes is still just the name — the case the old
    /// condition covered, and the reason it was written that way.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void One_item_and_no_sizes_is_still_just_the_name()
    {
        var shell = Shell();
        var handle = new OperationHandle();

        Adopt(shell, handle);

        handle.Begin(itemsTotal: 1, totalBytes: 0);
        handle.ItemStarted("holiday.jpg");

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("holiday.jpg", shell.OperationStatus);
    }

    /// <summary>And a copy, which does know its sizes, still reports both.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_copy_that_knows_its_sizes_still_reports_them()
    {
        var shell = Shell();
        var handle = new OperationHandle();

        Adopt(shell, handle);

        handle.Begin(itemsTotal: 2, totalBytes: 2048);
        handle.ItemStarted("one.txt");
        handle.BytesCopied(1024);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("0/2  1 KiB/2 KiB  one.txt", shell.OperationStatus);
    }

    // ---- the two buttons ---------------------------------------------------

    /// <summary>
    /// **The whole point of the flags.** A handle that says it cannot be
    /// stopped leaves both buttons refusing the press, which is how a bound
    /// Button greys itself out.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void An_operation_that_cannot_be_stopped_offers_neither_button()
    {
        var shell = Shell();

        Adopt(shell, new OperationHandle { CanPause = false, CanCancel = false });

        Assert.False(shell.CancelOperationCommand.CanExecute(null));
        Assert.False(shell.PauseOperationCommand.CanExecute(null));
    }

    /// <summary>Every engine written here can, so the default is yes.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void An_ordinary_copy_offers_both()
    {
        var shell = Shell();

        Adopt(shell, new OperationHandle());

        Assert.True(shell.CancelOperationCommand.CanExecute(null));
        Assert.True(shell.PauseOperationCommand.CanExecute(null));
    }

    /// <summary>
    /// **CanExecute is cached until something says otherwise.** A command asked
    /// once while nothing was running would answer "no" for the rest of the
    /// session, so the bar has to re-ask when the handle it is showing changes
    /// — including the change TO one, which is the one that matters.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void The_buttons_are_re_asked_when_the_bar_changes_hands()
    {
        var shell = Shell();

        shell.Start(null, Path.GetTempPath());

        Assert.False(shell.CancelOperationCommand.CanExecute(null));

        var asked = 0;

        shell.CancelOperationCommand.CanExecuteChanged += (_, _) => asked++;

        shell.ActiveTab!.Adopt(new OperationHandle());

        Dispatcher.UIThread.RunJobs();

        Assert.True(asked > 0, "the button was never re-asked, so it stays greyed out");
        Assert.True(shell.CancelOperationCommand.CanExecute(null));
    }

    /// <summary>Nothing running is nothing to stop, which is the same answer
    /// the bar's own visibility gives.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Nothing_running_offers_neither()
    {
        var shell = Shell();

        shell.Start(null, Path.GetTempPath());

        Assert.False(shell.CancelOperationCommand.CanExecute(null));
        Assert.False(shell.PauseOperationCommand.CanExecute(null));
    }
}
