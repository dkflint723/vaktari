using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Search;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One search that was run, as a menu row.
///
/// <paramref name="Name"/> is what the row says, <paramref name="Where"/> its
/// tooltip, and <paramref name="Path"/> the search path both were computed
/// from — kept so the row can be identified in a test without re-deriving the
/// words it shows.
/// </summary>
public sealed record SearchStep(string Path, string Name, string Where, ICommand Open);

/// <summary>
/// What has been searched for, and how to ask it again.
///
/// **Nothing recorded it.** A question was typed, answered, and gone the moment
/// the tab moved on: the only copy of it was the pane's own path. Asking it
/// again meant retyping it, and a question that had been refined — narrowed to
/// a folder, then told to mind its capitals — had to be rebuilt one control at
/// a time from memory.
///
/// **Shaped exactly like the navigation history next door**, which is not
/// tidiness: <c>BackSteps</c> and <c>ForwardSteps</c> already hang a list of
/// places off a toolbar button's right-click, with the row carrying its own
/// command rather than reaching out through <c>$parent[Window]</c> because a
/// flyout is its own popup root. A second, different way of doing the same
/// thing three centimetres away would be the thing to explain.
///
/// **On the magnifier's right-click rather than inside the open field, and
/// that is a measured constraint rather than a taste.** The field's TextBox
/// carries <c>FocusBehavior.LostFocusCommand</c> bound to
/// <c>CloseSearchIfEmpty</c>, so any control inside the box that takes focus
/// when clicked collapses the box out from under itself — the same wound
/// <c>FocusBehavior.OnLostFocus</c> already had to special-case for the
/// address bar's own context menu. The collapsed button is not focus-sensitive
/// at all, and right-click there is what Back and Forward already teach.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// How many searches the menu shows. The same depth the navigation menus
    /// use, for the same reason: deep enough to cover the walk that makes this
    /// worth having, short enough that the flyout does not become a scrolling
    /// list of its own.
    /// </summary>
    private const int SearchHistoryDepth = HistoryDepth;

    /// <summary>
    /// The searches that have been run. Static for the same reason as every
    /// other provider on this type: panes are created by the shell, not
    /// injected here. Null is an empty menu, never a crash.
    /// </summary>
    public static ISearchHistory? Searches { get; set; }

    /// <summary>
    /// The history, when the setting says to keep one.
    ///
    /// Read here rather than at the call site, so a second recording road
    /// cannot be added that forgets to ask — which is exactly how the recent
    /// lists came to have five call sites and no setting between them.
    /// </summary>
    private static ISearchHistory? RecordingSearches
        => Settings.AppSettings.Current.General.ForgetSearches ? null : Searches;

    /// <summary>
    /// What a history row says: the question, the folder it was confined to
    /// when it was confined to one, and whether the capitals were part of it.
    ///
    /// **Every field the search reads is in the row, and the case one was
    /// left out.** A search path carries four fields and the backend is handed
    /// three of them, so "report" here and "report" there and "report" minding
    /// its capitals are three entries — and the case box is an ORDINARY way to
    /// reach the pair: its setter navigates to a new search path, which is
    /// recorded, so refining one question through the box always left two rows
    /// behind. With the case out of the row those two read identically AND
    /// tooltipped identically, which is the exact failure the scope is in the
    /// row to avoid, reached by one click.
    ///
    /// Two spaces before the aside, the way "Search files  (ctrl+f)" spells
    /// the same idea on the button this hangs from; a comma between the two
    /// asides when a search has both. "Matching case" rather than any other
    /// phrasing because "Match case" is what the box that sets it says, and a
    /// history row naming a control differently is a second name to learn.
    /// </summary>
    public static string SearchStepName(string path)
    {
        var scope = VirtualPaths.ScopeOf(path);
        var name = VirtualPaths.QueryOf(path);

        if (scope is not null) name += $"  in {PathRules.LeafName(scope)}";

        if (VirtualPaths.MatchesCase(path))
            name += scope is null ? "  matching case" : ", matching case";

        return name;
    }

    /// <summary>
    /// Where the row will look, as the tooltip.
    ///
    /// The full folder for a scoped search, because the leaf in the row above
    /// is ambiguous between four folders called Docs — the same question the
    /// listing's parent-path column exists to answer. Otherwise the provider's
    /// own phrase, because **"everywhere" was not true on either platform**:
    /// Windows walks the drives on this machine and Linux the home folder and
    /// what is mounted, and the provider is what decides those roots.
    /// </summary>
    public static string SearchStepWhere(string path)
        => VirtualPaths.ScopeOf(path)
           ?? $"Searching {Search?.Everywhere ?? "everywhere"}";

    /// <summary>
    /// The rows behind the magnifier, newest first.
    ///
    /// Rebuilt on each read rather than held in a collection: the store is the
    /// single copy, and a cached list is a second one to keep in step.
    /// </summary>
    public IReadOnlyList<SearchStep> SearchSteps
    {
        get
        {
            if (Searches is null) return [];

            var steps = new List<SearchStep>();

            foreach (var path in Searches.Recent(SearchHistoryDepth))
            {
                steps.Add(new SearchStep(
                    path,
                    SearchStepName(path),
                    SearchStepWhere(path),
                    new RelayCommand(() => _ = NavigateAsync(path))));
            }

            return steps;
        }
    }

    /// <summary>Whether there is any history at all, which is what decides
    /// whether the button admits to having a menu.</summary>
    public bool HasSearchSteps => Searches is { Count: > 0 };

    /// <summary>
    /// The magnifier's tooltip, which grows a second half once there is
    /// something behind the right-click.
    ///
    /// **A gesture is only worth advertising when it leads somewhere.** Back
    /// and Forward advertise neither, and their menus are empty on a fresh
    /// tab; this says so only when it is true, so the hint cannot send anybody
    /// to a menu with nothing in it.
    /// </summary>
    public string SearchTip => HasSearchSteps
        ? Vaktari.Ui.Input.Keymap.Current.Labelled("Search files", "Search") + "  —  right-click for recent searches"
        : Vaktari.Ui.Input.Keymap.Current.Labelled("Search files", "Search");

    /// <summary>
    /// Records a search, when the setting allows one.
    ///
    /// **Called BEFORE the load, which is the opposite of the recent-folder
    /// line beside it and deliberate.** That one waits for <c>IsLoaded</c>
    /// because a folder that could not be read is not somewhere the user went;
    /// a search that found nothing is still a question that was asked, and is
    /// the one most worth asking again once the files it was looking for
    /// exist.
    ///
    /// **On the caller's thread, before any await, and that is what makes the
    /// notification it triggers safe.** Every road that reaches
    /// <c>NavigateAsync</c> with a search path is a UI-thread command — the
    /// scope box's setter, the case box's setter, <c>RunSearch</c>, and a row
    /// of this very menu — and this runs synchronously in the part of
    /// NavigateAsync that precedes its first suspension, so no pool thread
    /// ever raises a property change into a binding.
    ///
    /// **It does not refresh the menu itself, and that is the fix rather than
    /// an omission.** The store announces its own changes and every pane in
    /// every window listens, so one road serves the pane that searched and the
    /// three that did not. Refreshing here as well would rebuild this pane's
    /// menu twice for one search and still leave the others stale on a clear.
    /// </summary>
    private void RecordSearch(string path)
    {
        if (RecordingSearches is not { } history) return;

        history.Record(path);
    }

    /// <summary>
    /// The menu is rebuilt from the store, on the store's own
    /// <see cref="ISearchHistory.Changed"/>.
    ///
    /// **Hung on the event rather than called after each gesture, because the
    /// gestures are in the wrong windows.** The store is one per application
    /// and this menu is one per pane: a search run in window A and a clear
    /// pressed in window A's settings dialog both have to reach window B's
    /// magnifier, and B's shell hears about neither.
    ///
    /// One caller, and deliberately: the road in is the event, so a gesture
    /// that changed the store without announcing it would be visible as a menu
    /// that did not move rather than papered over by a second call here.
    /// </summary>
    private void RefreshSearchHistory()
    {
        OnPropertyChanged(nameof(SearchSteps));
        OnPropertyChanged(nameof(HasSearchSteps));
        OnPropertyChanged(nameof(SearchTip));
    }
}
