namespace Vaktari.Core.Search;

/// <summary>
/// The searches that have been run, newest first.
///
/// **Nothing anywhere recorded what had been searched for.** A question was
/// typed, answered and then gone: the only copy of it was the pane's own path,
/// so it survived exactly as long as that tab stayed on the results. Retyping
/// it was the whole of "search for that again", and a question refined three
/// times — narrowed to a folder, then told to mind its capitals — had to be
/// rebuilt from memory one control at a time.
///
/// **An entry is a search PATH, not the words that were typed.** A search is
/// four fields — query, origin, scope, case — and the path is the one string
/// that carries all four, so replaying it puts back the question that was
/// asked rather than a vaguer one that merely shares its words. It is also
/// what the pane navigates to, so a history row is a place rather than an
/// instruction to reassemble one.
///
/// Compared ORDINALLY, which is the rule <c>VirtualPaths.SamePlace</c> already
/// applies to a search path: with the case box ticked "readme" and "README"
/// are two questions with two different answers, so folding them together here
/// would lose one of them.
///
/// Shaped after <see cref="Vaktari.Core.FileSystem.IRecentStore"/> — the same
/// switch to stop it and the same one action to empty it — because they are
/// the same kind of thing: a list of what somebody has been doing, which must
/// be stoppable and clearable or it is a log rather than a tool.
/// </summary>
public interface ISearchHistory
{
    /// <summary>
    /// Records a search, or moves one already held to the top.
    ///
    /// "Already held" is the same QUESTION rather than the same string: the
    /// origin rides in the path even when the search is unscoped, and nothing
    /// reads it then, so one question asked from two folders is one entry. The
    /// newest asking is the one kept, origin and all.
    /// </summary>
    void Record(string path);

    /// <summary>Newest first. Fewer than <paramref name="count"/> when little
    /// has been searched for yet.</summary>
    IReadOnlyList<string> Recent(int count);

    /// <summary>How many searches are held, which is what the settings dialog
    /// reports before it offers to clear them.</summary>
    int Count { get; }

    /// <summary>
    /// Drops everything, and answers how many that was.
    ///
    /// One action rather than one row at a time: a list of the questions
    /// somebody has asked their own machine is exactly the kind of thing that
    /// has to be emptiable in a single gesture.
    /// </summary>
    int ForgetAll();

    /// <summary>
    /// Raised when the list changes, so every menu built out of it reads it
    /// again.
    ///
    /// **The store is one per application and the menus are one per pane, so a
    /// clear announced to only one of them left the others drawing searches
    /// that were gone — and running one of those rows RE-RECORDED the search
    /// that had just been forgotten**, because opening a history row is a
    /// navigation to a search path like any other.
    ///
    /// MEASURED with two shells over one store, the announcement removed:
    /// after the clear the second pane had been told 0 times, the rows it had
    /// already been handed still numbered 1, and executing that row took the
    /// store from 0 entries back to 1. The rows PROPERTY answered 0 throughout,
    /// which is why this is an announcement rather than a cache: it is a menu
    /// already on screen that goes on holding what it last read.
    ///
    /// Declared here rather than left to the one implementation, for the
    /// reason <see cref="Vaktari.Core.FileSystem.IRecentStore.Changed"/> is:
    /// a reader holds the interface, and a notice it cannot subscribe to is a
    /// notice it does not get.
    /// </summary>
    event EventHandler? Changed;
}
