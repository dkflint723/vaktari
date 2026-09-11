using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Test classes that build a shell or a pane inherit this and hand what they
/// make to <see cref="Own{T}"/>.
///
/// **A pane that has loaded keeps a file watcher, two dispatcher timers and a
/// background task that shells out to git.** None of it stops on its own, and
/// a headless test session ends the moment the test does — so a tick or a
/// continuation arriving a moment later touches a dispatcher that has moved on.
/// It surfaces as "the calling thread cannot access this object because a
/// different thread owns it" in the CLEANUP of whatever test happened to run
/// next, which is why the reported victim was never the cause and changed from
/// run to run.
///
/// The rate rises with the number of tests that navigate, which is what made it
/// look like a mystery for so long: it was rare when few did, and reproduced
/// three times in six once there were more.
/// </summary>
public abstract class OwnedViewModels : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    protected T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    public virtual void Dispose()
    {
        foreach (var thing in _owned)
        {
            // One failing teardown must not hide the others, or the next leak
            // is invisible again.
            try { thing.Dispose(); }
            catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        }

        _owned.Clear();

        if (_searchBorrowed) Vaktari.Ui.ViewModels.PaneViewModel.Search = _searchBefore;

        if (_historyBorrowed) Vaktari.Ui.ViewModels.PaneViewModel.Searches = _historyBefore;
    }

    /// <summary>
    /// Pumps until the sidebar has a place row on screen, or gives up after
    /// five seconds and says what the sidebar held instead.
    ///
    /// **The rows arrive from the thread pool after the window has opened.**
    /// The shell starts the places import fire-and-forget, the import reads
    /// the mount table and the user's folders on the pool, and the rows are
    /// posted back when it is done — so a test that pumps twice and looks is
    /// looking at a moment, not at the sidebar. On this desktop and under WSL
    /// the moment was always late enough; measured on the two-core Ubuntu
    /// runner the first time the Ui suite ran there, six tests looked before
    /// the import had finished and found no Home row, no This PC row and
    /// nothing for F6 to land on. A bounded wait on the condition is what a
    /// test about the rows has to make, on every machine.
    ///
    /// **A place row is a Button whose DataContext is a place, and the first
    /// version of this wait did not say so.** It waited for any visible
    /// Button that was not a section heading — and the sidebar holds several
    /// that are there before the import has produced anything: Scan, Share,
    /// the "+" on the remotes section. One of those satisfied it at once, so
    /// it waited for nothing, and the same six tests failed on the same
    /// runner with it in place, none of them with this method's message.
    /// The message now carries the sidebar's own account — the groups it
    /// holds, whether a rebuild is still running, and the exception if one
    /// died — because the runner is the only machine that can measure this,
    /// and "no rows" was all it had said.
    /// </summary>
    protected static void SidebarReady(Window window)
    {
        var panel = window.FindControl<Border>("SidebarPanel");

        Assert.NotNull(panel);

        static bool HasRow(Border panel)
            => panel.GetVisualDescendants().OfType<Button>()
                    .Any(b => b.IsVisible && b.DataContext is Vaktari.Ui.ViewModels.PlaceItemViewModel);

        for (var i = 0; i < 500 && !HasRow(panel!); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        if (HasRow(panel!)) return;

        var sidebar = (window.DataContext as Vaktari.Ui.ViewModels.ShellViewModel)?.Sidebar;

        var groups = sidebar is null
            ? "no shell on the window"
            : string.Join(", ", sidebar.Groups.Select(g => $"{g.Label}: {g.Places.Count} rows"));

        Assert.Fail(
            "the sidebar showed no place row within five seconds — "
            + $"groups [{groups}]; rebuilding: {sidebar?.IsReloading}; "
            + $"import error: {sidebar?.LastImportError?.ToString() ?? "none"}; "
            + $"rebuild error: {sidebar?.LastReloadError?.ToString() ?? "none"}");
    }

    private Vaktari.Core.Search.ISearchProvider? _searchBefore;
    private bool _searchBorrowed;

    /// <summary>
    /// Lends this test the pane's search backend, and gives it back afterwards.
    ///
    /// **A test that navigates to a search path and does NOT set this runs a
    /// real one.** The backend is a static, the way every provider the pane
    /// reaches is, and one test class in this assembly builds a real MainWindow
    /// — which assigns the platform's own search provider to it. Any later
    /// class that then loads a search listing performs a genuine recursive walk
    /// of the machine: slow, and answering with whatever files happen to be
    /// there.
    ///
    /// Null is the honest default for a test that only cares about the shape of
    /// a search listing rather than its contents.
    /// </summary>
    protected void UseSearch(Vaktari.Core.Search.ISearchProvider? backend)
    {
        if (!_searchBorrowed)
        {
            _searchBefore = Vaktari.Ui.ViewModels.PaneViewModel.Search;
            _searchBorrowed = true;
        }

        Vaktari.Ui.ViewModels.PaneViewModel.Search = backend;
    }

    private Vaktari.Core.Search.ISearchHistory? _historyBefore;
    private bool _historyBorrowed;

    /// <summary>
    /// Lends this test the pane's search history, and gives it back afterwards.
    ///
    /// **A static like the backend above, and the one a test is most likely to
    /// leave behind.** Every navigation to a search path writes to whatever is
    /// in it, so a class that left its own fake here would collect the searches
    /// of every later class in the assembly, and a class that ran after the one
    /// building a real MainWindow would write into the SHIPPED store that
    /// window installed.
    ///
    /// **Which is cross-CLASS leakage and not a write to the user's own file,
    /// and the difference was measured rather than assumed.** TestState's
    /// module initializer points JsonSessionStore.DirectoryOverride at a
    /// per-test-class directory under GetTempPath, and WindowServices builds
    /// the shipped store as
    /// <c>new JsonSearchHistory(JsonSessionStore.DefaultDirectory())</c> — so
    /// the searches.json a headless MainWindow writes lands in that directory,
    /// which is what
    /// <c>TestStateIsolationTests.Every_store_in_this_run_is_pointed_somewhere_disposable</c>
    /// already asserts. What this helper guards is one class reading another
    /// class's list, which is enough on its own: these tests count rows.
    /// </summary>
    protected void UseSearchHistory(Vaktari.Core.Search.ISearchHistory? history)
    {
        if (!_historyBorrowed)
        {
            _historyBefore = Vaktari.Ui.ViewModels.PaneViewModel.Searches;
            _historyBorrowed = true;
        }

        Vaktari.Ui.ViewModels.PaneViewModel.Searches = history;
    }
}
