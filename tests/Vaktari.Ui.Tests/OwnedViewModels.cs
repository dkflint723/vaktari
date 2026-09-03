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
}
