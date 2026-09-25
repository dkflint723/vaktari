using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.Thumbnails;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What happens to rows already on screen when the icons change source.
///
/// **Dropping the icon caches repaints nothing.** RowIcon resolves an icon when
/// its entry is set and never again, so a theme read in the background swapped
/// in, IconLoader.Invalidate emptied the dictionaries, and the folder being
/// looked at went on showing the icons it opened with until the next
/// navigation. IconThemeInstall's own comment promises "icons change once
/// shortly after this window opens", and for the rows that were already open it
/// was not true.
///
/// **The obvious test for this does not work, and it took a probe to find
/// out.** Writing a file into the watched folder and waiting for the row to
/// appear passes whether or not anything is announced, because the pane's own
/// filesystem watcher puts it there — measured, by deleting the announcement
/// and watching the test go on passing. So the listing is counted at the
/// provider instead, where nothing else can supply the answer.
/// </summary>
public sealed class IconSourceRepaintTests : OwnedViewModels
{
    /// <summary>Answers nothing, and says how many times it was asked.</summary>
    private sealed class Counting : IFileSystemProvider
    {
        public int Reads;

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private MainWindow? _window;

    public override void Dispose()
    {
        // A shown window flushes the session on close, and one left open is
        // torn down later on whatever thread xunit is on.
        _window?.Close();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// The handlers the static event is holding.
    ///
    /// Read through the compiler-generated backing field, because an event
    /// exposes no other way to ask — and asking is the point: the leak this
    /// guards against is invisible from outside.
    /// </summary>
    private static Delegate[] Handlers()
    {
        var field = typeof(IconLoader).GetField(
            nameof(IconLoader.SourceChanged),
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.GetField);

        Assert.NotNull(field);

        return ((EventHandler?)field!.GetValue(null))?.GetInvocationList() ?? [];
    }

    /// <summary>
    /// The one handler <paramref name="after"/> holds that
    /// <paramref name="before"/> did not: the subscription the window just
    /// made, told apart from every other window's by identity rather than by
    /// arithmetic.
    /// </summary>
    private static Delegate Added(Delegate[] before, Delegate[] after)
    {
        var added = after.Except(before).ToArray();

        Assert.Single(added);

        return added[0];
    }

    // ---- the announcement reaches every pane -------------------------------

    /// <summary>
    /// Counted at the provider, where a watcher cannot supply the answer, and
    /// across a SPLIT so a loop that only walked the left half would show.
    /// </summary>
    [AvaloniaFact]
    public void Every_pane_in_the_window_is_re_listed()
    {
        var files = new Counting();
        var shell = Own(new ShellViewModel(files));

        // A bare shell starts with no tabs at all — measured, when this asked
        // for a split and got left=0, right=1.
        shell.Left.AddTab(Path.GetTempPath());

        if (!shell.IsSplit) shell.ToggleSplit();
        Settle();

        var tabs = shell.Left.Tabs.Count + (shell.Right?.Tabs.Count ?? 0);

        // Both halves, so a loop that walked only Left would show here.
        Assert.True(shell.IsSplit, "the split is what puts a pane on the right");
        Assert.True(tabs >= 2, $"expected a pane each side, saw {tabs}");

        // **Waited on, not pumped once.** A load now waits a moment for its
        // folder's watch to open before its first read, so one pump of the
        // dispatcher no longer finishes it — measured on the CI agents, where
        // this read 0 re-lists of 2. First every pane's own first listing, so
        // the count below is only the re-lists; then the re-lists themselves.
        Until(() => shell.Left.Tabs.Concat(shell.Right?.Tabs ?? []).All(t => t.IsLoaded));

        var before = files.Reads;

        shell.RefreshPaneListings();
        Until(() => files.Reads - before >= tabs);

        Assert.Equal(tabs, files.Reads - before);
    }

    /// <summary>Pumps the dispatcher until <paramref name="done"/>, for at most ten seconds.</summary>
    private static void Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
            Settle();
        }
    }

    /// <summary>
    /// **Nothing ever proved that anything asks.** The test above shows a shell
    /// re-lists its panes when it is told to, and calls RefreshPaneListings
    /// itself to say so; the announcement that is supposed to do the telling was
    /// run by no test at all. Measured 2026-09-17 by revert-check: the window's
    /// handler rewritten to post an empty lambda left the whole Ui suite green,
    /// so the chain from IconLoader.AnnounceSourceChanged through the window's
    /// own subscription to the refresh could have been deleted whole and nothing
    /// would have said a word — in the one class named for it.
    ///
    /// Driven from the real announcement through a real window, and counted at a
    /// provider rather than by watching for a row, for the reason the class note
    /// gives. The counting pane is added to the shell's OWN tab list because
    /// RefreshPaneListings walks exactly that collection: a pane held to one side
    /// would prove the provider counts and nothing else.
    /// </summary>
    [AvaloniaFact]
    public async Task An_announcement_re_lists_the_panes_of_an_open_window()
    {
        UseSearch(PaneViewModel.Search);

        var files = new Counting();

        var window = _window = new MainWindow();

        window.Show();
        Settle();

        var pane = Own(new PaneViewModel(files));

        // A refresh re-reads the path the pane is on, so it has to be on one.
        await pane.NavigateAsync(Path.GetTempPath());

        ShellOf(window).Left.Tabs.Add(pane);

        var before = files.Reads;

        IconLoader.AnnounceSourceChanged();

        // The handler POSTS the refresh rather than running it in place, and the
        // refresh it posts is itself a load, so the read is at least two turns
        // away. Waited for rather than counted in turns, the rule every
        // load-dependent wait in this suite has had to learn.
        for (var i = 0; i < 200 && files.Reads == before; i++) { Settle(); Thread.Sleep(5); }

        Assert.True(
            files.Reads > before,
            "the announcement never reached the window's panes: the provider was never read again");
    }

    /// <summary>The window's shell, which it does not hand out.</summary>
    private static ShellViewModel ShellOf(MainWindow window)
        => (ShellViewModel)typeof(MainWindow)
            .GetProperty("Shell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    // ---- and only for as long as the window is open ------------------------

    /// <summary>
    /// **A static event outlives every window**, so one that subscribes has to
    /// let go — or it is held alive by IconLoader and re-lists its disposed
    /// panes on every icon swap for the rest of the session. The theme
    /// subscription beside it documents having had exactly that fault.
    ///
    /// Asserted as a difference rather than an absolute: other windows may be
    /// open in the same run, and this only claims that this one arrives and
    /// leaves.
    ///
    /// **And the difference is in identity, not in the count.** A window an
    /// earlier class closed can still be subscribed when this one starts —
    /// MainWindow's teardown runs past Close(), as the wait below says — and
    /// when it lets go between the reading before and the reading after, the
    /// count stands still while the membership changes completely. Measured in
    /// a full run: before held one handler on MainWindow #30768801 and the
    /// reading after held one on #7466953, which a count read as "the window
    /// never subscribed" and failed. Holding the handler itself is the stricter
    /// claim as well as the steadier one: the window that leaks is the window
    /// this names, so a genuine leak still reddens it.
    /// </summary>
    [AvaloniaFact]
    public void A_window_subscribes_while_it_is_open_and_lets_go_when_it_closes()
    {
        UseSearch(PaneViewModel.Search);

        var before = Handlers();

        var window = _window = new MainWindow();

        window.Show();
        Settle();

        // Subscribed, exactly once: Added asserts there is one new handler.
        var mine = Added(before, Handlers());

        window.Close();
        _window = null;

        // **Closed does not fire on the turn Close() returns**, measured here:
        // asserting straight after the call saw the handler still attached.
        // Waited for rather than counted in turns, which is the rule every
        // load-dependent wait in this suite has had to learn.
        for (var i = 0; i < 200 && Handlers().Contains(mine); i++) { Settle(); Thread.Sleep(5); }

        Assert.DoesNotContain(mine, Handlers());
    }
}
