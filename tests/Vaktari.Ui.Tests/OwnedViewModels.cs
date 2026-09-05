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
