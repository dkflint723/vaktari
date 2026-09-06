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
    /// How many handlers the static event is holding.
    ///
    /// Read through the compiler-generated backing field, because an event
    /// exposes no other way to ask — and asking is the point: the leak this
    /// guards against is invisible from outside.
    /// </summary>
    private static int Listeners()
    {
        var field = typeof(IconLoader).GetField(
            nameof(IconLoader.SourceChanged),
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.GetField);

        Assert.NotNull(field);

        return ((EventHandler?)field!.GetValue(null))?.GetInvocationList().Length ?? 0;
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

        var before = files.Reads;

        shell.RefreshPaneListings();
        Settle();

        Assert.Equal(tabs, files.Reads - before);
    }

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
    /// </summary>
    [AvaloniaFact]
    public void A_window_subscribes_while_it_is_open_and_lets_go_when_it_closes()
    {
        UseSearch(PaneViewModel.Search);

        var before = Listeners();

        var window = _window = new MainWindow();

        window.Show();
        Settle();

        Assert.Equal(before + 1, Listeners());

        window.Close();
        _window = null;

        // **Closed does not fire on the turn Close() returns**, measured here:
        // asserting straight after the call saw the handler still attached.
        // Waited for rather than counted in turns, which is the rule every
        // load-dependent wait in this suite has had to learn.
        for (var i = 0; i < 200 && Listeners() != before; i++) { Settle(); Thread.Sleep(5); }

        Assert.Equal(before, Listeners());
    }
}
