using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the transfer bar says about how long it will take.
///
/// **It said nothing.** The line counted items and bytes — "34/1200
/// 1.2 GiB/4.9 GiB" — which is the one thing a person can work out by looking
/// at it twice, and there was no bar, no speed and no estimate. Whether this is
/// a two-minute job or an hour is the question behind "wait for it, or go and
/// do something else".
/// </summary>
public sealed class OperationProgressTests : OwnedViewModels
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    /// <summary>A clock the test drives, so nothing here has to sleep.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);

        public Func<DateTimeOffset> Read => () => Now;
    }

    private ShellViewModel Shell() => Own(new ShellViewModel(new Inert()));

    private static OperationProgress At(long done, long total, int items = 0, int itemsTotal = 0)
        => new()
        {
            BytesDone = done,
            BytesTotal = total,
            ItemsDone = items,
            ItemsTotal = itemsTotal,
            CurrentItem = "one.txt",
        };

    /// <summary>The whole finding: a speed and a time remaining.</summary>
    [AvaloniaFact]
    public void A_copy_says_how_fast_it_is_going_and_how_long_is_left()
    {
        var shell = Shell();
        var clock = new Clock();
        var rate = new TransferRate(TimeSpan.FromSeconds(5), clock.Read);

        // 10 MiB in a second, a quarter of the way through 100 MiB.
        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(10 * 1024 * 1024);

        shell.ShowProgress(rate, At(25 * 1024 * 1024, 100 * 1024 * 1024));

        Assert.True(shell.HasOperationProgress);
        Assert.Equal(0.25, shell.OperationPercent, 3);

        Assert.Equal("10 MiB/s · less than a minute left", shell.OperationRate);
    }

    /// <summary>And the estimate is a guess, phrased as one.</summary>
    [AvaloniaFact]
    public void A_long_copy_says_roughly_how_long()
    {
        var shell = Shell();
        var clock = new Clock();
        var rate = new TransferRate(TimeSpan.FromSeconds(5), clock.Read);

        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(10 * 1024 * 1024);

        // Two and a half gigabytes still to go at ten a second.
        shell.ShowProgress(rate, At(10L * 1024 * 1024, 2510L * 1024 * 1024));

        Assert.Equal("10 MiB/s · about 4 min left", shell.OperationRate);
    }

    /// <summary>
    /// **A trash reports a count and no bytes at all.** Its bar has to fill
    /// from the item count — a bar sitting at zero for the whole run and then
    /// vanishing is what a hung operation looks like — and there is no estimate
    /// to give, because there is nothing to divide.
    /// </summary>
    [AvaloniaFact]
    public void A_delete_fills_from_the_count_and_promises_no_time()
    {
        var shell = Shell();
        var rate = new TransferRate(TimeSpan.FromSeconds(5), () => DateTimeOffset.UnixEpoch);

        shell.ShowProgress(rate, At(0, 0, items: 3, itemsTotal: 4));

        Assert.True(shell.HasOperationProgress);
        Assert.Equal(0.75, shell.OperationPercent, 3);

        Assert.DoesNotContain("left", shell.OperationRate);
    }

    /// <summary>
    /// One file with no size known is neither: no fraction to draw, so no bar
    /// rather than one that sits at zero.
    /// </summary>
    [AvaloniaFact]
    public void With_nothing_to_divide_there_is_no_bar()
    {
        var shell = Shell();
        var rate = new TransferRate(TimeSpan.FromSeconds(5), () => DateTimeOffset.UnixEpoch);

        shell.ShowProgress(rate, At(0, 0, items: 0, itemsTotal: 1));

        Assert.False(shell.HasOperationProgress);
    }

    /// <summary>
    /// **A stall stops claiming a speed.** The engine reports on every buffer
    /// and every item, so a copy stuck inside one file reports nothing — and
    /// the number left on the bar would go on saying 10 MiB/s while a drive
    /// that has given up moves nothing, which reads more alive than the bar did
    /// before any of this existed.
    /// </summary>
    [AvaloniaFact]
    public void A_stalled_copy_stops_claiming_a_speed()
    {
        var shell = Shell();
        var clock = new Clock();
        var rate = new TransferRate(TimeSpan.FromSeconds(5), clock.Read);

        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(10 * 1024 * 1024);

        shell.ShowProgress(rate, At(10 * 1024 * 1024, 100 * 1024 * 1024));

        Assert.Contains("MiB/s", shell.OperationRate);

        // Nothing reported for half a minute, and the bar is asked again.
        clock.Advance(30);

        shell.ShowProgress(rate, At(10 * 1024 * 1024, 100 * 1024 * 1024));

        Assert.Equal("", shell.OperationRate);

        // The bar itself stays where it got to: the work has not been undone,
        // it has stopped.
        Assert.True(shell.HasOperationProgress);
    }

    /// <summary>
    /// **And a real operation drives all of it.** Everything above calls
    /// ShowProgress by hand; this is the half that says the shell asks it at
    /// all — that a running copy's reports reach the rate, and the rate reaches
    /// the bar. Mutating the wiring is invisible to every other test here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_running_copy_drives_the_bar()
    {
        var shell = Shell();
        var clock = new Clock();

        shell.NewRate = () => new TransferRate(TimeSpan.FromSeconds(5), clock.Read);

        shell.Start(null, Path.GetTempPath());

        var handle = new OperationHandle();

        shell.ActiveTab!.Adopt(handle);

        Dispatcher.UIThread.RunJobs();

        handle.Begin(itemsTotal: 2, totalBytes: 100 * 1024 * 1024);
        handle.ItemStarted("one.txt");

        Dispatcher.UIThread.RunJobs();

        clock.Advance(1);

        handle.BytesCopied(10 * 1024 * 1024);

        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.HasOperationProgress);
        Assert.Equal(0.1, shell.OperationPercent, 3);
        Assert.Contains("MiB/s", shell.OperationRate);

        // **Drained rather than dropped.** Completing schedules a pool
        // continuation which then posts to the dispatcher; one RunJobs on the
        // next line usually finds an empty queue, and this test would leave a
        // live rate tick behind for whatever runs next.
        handle.Complete();

        for (var i = 0; i < 50 && shell.ActiveOperation is not null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Assert.Null(shell.ActiveOperation);
    }

    // ---- the bar itself ----------------------------------------------------

    private static XElement OperationBar()
    {
        var markup = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

        return markup.Descendants(Avalonia + "Border")
                     .Single(e => (string?)e.Attribute("IsVisible") == "{Binding ShowOperationBar}");
    }

    /// <summary>There is a bar, and it is bound to how far along it is.</summary>
    [Fact]
    public void The_transfer_bar_has_a_bar()
    {
        var progress = Assert.Single(OperationBar().Descendants(Avalonia + "ProgressBar"));

        Assert.Equal("{Binding OperationPercent}", (string?)progress.Attribute("Value"));
        Assert.Equal("1", (string?)progress.Attribute("Maximum"));
        Assert.Equal("{Binding HasOperationProgress}", (string?)progress.Attribute("IsVisible"));
    }

    /// <summary>
    /// **The bar and the rate are docked, not appended to the line.** The line
    /// ends in a filename that can be 255 characters and is the fill child, so
    /// anything after it would be the first thing to ellipsize away — which is
    /// the fault that put pause and cancel off the edge of the window.
    /// </summary>
    [Fact]
    public void Neither_can_be_pushed_off_the_edge_by_a_long_name()
    {
        var bar = OperationBar();

        var progress = Assert.Single(bar.Descendants(Avalonia + "ProgressBar"));

        var rate = Assert.Single(
            bar.Descendants(Avalonia + "TextBlock"),
            e => (string?)e.Attribute("Text") == "{Binding OperationRate}");

        Assert.Equal("Right", (string?)progress.Attribute("DockPanel.Dock"));
        Assert.Equal("Right", (string?)rate.Attribute("DockPanel.Dock"));
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
