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
    }
}
