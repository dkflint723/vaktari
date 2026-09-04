using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The offer to go again on what an operation could not do.
///
/// **The batch never stopped to ask, so the asking happens afterwards.** Skip
/// is what the batch already did and cancel has been on the bar all along;
/// retry is the third verb, and the one that needs the person to go and DO
/// something first — which is exactly why it cannot be a modal in the middle of
/// five thousand items.
/// </summary>
public sealed class RetryOfferTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static RetryOffer Offer(int count, Action? onAgain = null)
        => new(count, () =>
        {
            onAgain?.Invoke();

            return new OperationHandle();
        });

    [AvaloniaFact]
    public void With_nothing_left_behind_there_is_nothing_to_offer()
    {
        var shell = new ShellViewModel(new Nothing());

        Assert.Null(shell.Retryable);
        Assert.False(shell.CanRetryOperation);
        Assert.Equal("Retry", shell.RetryLabel);
    }

    /// <summary>
    /// The count on the button is what pressing it will ATTEMPT — not how many
    /// problems were reported, which for one unreadable folder is every planned
    /// descendant.
    /// </summary>
    [AvaloniaFact]
    public void The_button_says_how_many_it_will_go_again_on()
    {
        var shell = new ShellViewModel(new Nothing()) { Retryable = Offer(3) };

        Assert.True(shell.CanRetryOperation);
        Assert.Equal("Retry 3", shell.RetryLabel);
    }

    /// <summary>
    /// **An offer must not outlive its sentence.** It is set and cleared where
    /// the bar's message is, so one left standing cannot reappear underneath
    /// the NEXT operation's failure, attached to work nobody is looking at.
    /// </summary>
    [AvaloniaFact]
    public void Dismissing_the_message_dismisses_the_offer_with_it()
    {
        var shell = new ShellViewModel(new Nothing())
        {
            OperationStatus = "one item was left behind",
            Retryable = Offer(1),
        };

        shell.DismissOperationStatusCommand.Execute(null);

        Assert.Equal("", shell.OperationStatus);
        Assert.Null(shell.Retryable);
        Assert.False(shell.CanRetryOperation);
    }

    /// <summary>
    /// Pressing it takes the offer first. The new operation writes its own line
    /// to the bar, and an offer still standing while that runs is an offer for
    /// work already being redone.
    /// </summary>
    [AvaloniaFact]
    public void Pressing_it_takes_the_offer_and_starts_the_work()
    {
        var started = 0;
        var shell = new ShellViewModel(new Nothing());

        shell.Start(null, Path.GetTempPath());

        shell.OperationStatus = "one item was left behind";
        shell.Retryable = Offer(1, () => started++);

        shell.RetryOperationCommand.Execute(null);

        Assert.Equal(1, started);
        Assert.Null(shell.Retryable);
        Assert.Equal("", shell.OperationStatus);
    }

    /// <summary>And pressing it with nothing offered does nothing at all.</summary>
    [AvaloniaFact]
    public void Pressing_it_with_nothing_offered_starts_nothing()
    {
        var shell = new ShellViewModel(new Nothing());

        shell.RetryOperationCommand.Execute(null);

        Assert.Null(shell.Retryable);
    }

    /// <summary>
    /// The button carries the count and hides when there is nothing to go again
    /// on — absent rather than present and doing nothing.
    /// </summary>
    [Fact]
    public void The_bar_shows_it_only_when_there_is_something_to_retry()
    {
        var button = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "Button")
            .Single(b => (string?)b.Attribute("Command") == "{Binding RetryOperationCommand}");

        Assert.Equal("{Binding CanRetryOperation}", (string?)button.Attribute("IsVisible"));
        Assert.Equal("{Binding RetryLabel}", (string?)button.Attribute("Content"));
    }

    /// <summary>
    /// The offer is taken from the handle in the same place the bar's message
    /// is written, which is what stops it outliving that message.
    /// </summary>
    [Fact]
    public void The_offer_is_read_where_the_message_is_written()
    {
        var source = RepoSource.Ui("ViewModels", "ShellViewModel.cs");

        var taken = source.IndexOf("Retryable = handle.Retry;", StringComparison.Ordinal);
        var described = source.IndexOf(
            "OperationStatus = DescribeProblems(handle.Problems);", StringComparison.Ordinal);

        Assert.True(taken > 0, "nothing reads the offer off a finished operation");
        Assert.True(taken < described,
                    "the offer is set after the message it belongs to, so a branch that "
                    + "writes no message can leave it standing");
    }

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
}
