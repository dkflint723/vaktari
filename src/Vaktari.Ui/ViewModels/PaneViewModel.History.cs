using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>One place in the navigation history, as a menu row.</summary>
public sealed record HistoryStep(int Depth, string FullPath, string Name, ICommand Open);

/// <summary>One recently visited folder, as a row in the address bar's own
/// menu. No depth: this is a list of places, not a count of presses.</summary>
public sealed record RecentPlace(string FullPath, string Name, ICommand Open);

/// <summary>
/// Where Back and Forward can take you.
///
/// **They exposed one step each, out of a history the pane had kept all
/// along** — and had been writing to the session file since tabs became
/// restorable. Nothing anywhere read the stacks' contents, so a pane ten
/// folders deep could only be walked back out one press at a time, with no way
/// to see where the next press went. Both references put that list on the
/// button.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// How many rows a history menu shows. Deep enough to cover the walk that
    /// makes this worth having, short enough that the flyout does not become a
    /// scrolling list of its own.
    /// </summary>
    private const int HistoryDepth = 12;

    /// <summary>
    /// What a path is called in a menu: a virtual listing by its own name, and
    /// anything else by its leaf — LeafName hands a root back as itself, so a
    /// drive reads "C:\" rather than blank.
    /// </summary>
    public static string PlaceName(string path)
        => VirtualPaths.IsVirtual(path) ? VirtualPaths.Label(path) : PathRules.LeafName(path);

    /// <summary>
    /// A navigation stack as menu rows, nearest first.
    ///
    /// Static and pure — the caller hands over the command for a given depth —
    /// so what the menu will say, and where each row goes, can be read without
    /// a pane behind it.
    /// </summary>
    public static IReadOnlyList<HistoryStep> StepsFor(
        IEnumerable<string> stack, Func<int, ICommand> open)
    {
        var steps = new List<HistoryStep>();
        var depth = 0;

        foreach (var path in stack)
        {
            if (depth == HistoryDepth) break;

            depth++;
            steps.Add(new HistoryStep(depth, path, PlaceName(path), open(depth)));
        }

        return steps;
    }

    /// <summary>
    /// The rows behind the Back button, nearest first.
    ///
    /// A Stack enumerates top-first, which is exactly the order wanted: the
    /// place one press away comes first.
    /// </summary>
    public IReadOnlyList<HistoryStep> BackSteps
        => StepsFor(_back, depth => new RelayCommand(() => _ = GoBackAsync(depth)));

    public IReadOnlyList<HistoryStep> ForwardSteps
        => StepsFor(_forward, depth => new RelayCommand(() => _ = GoForwardAsync(depth)));

    public bool HasBackSteps => _back.Count > 0;
    public bool HasForwardSteps => _forward.Count > 0;

    /// <summary>
    /// Goes back several steps at once, keeping both stacks as a single press
    /// repeated would have left them — everything stepped over lands on the
    /// forward stack, in order, so Forward walks back through it.
    /// </summary>
    public async Task GoBackAsync(int depth)
    {
        if (depth < 1) return;

        string? target = null;

        for (var i = 0; i < depth && _back.Count > 0; i++)
        {
            if (target is not null) _forward.Push(target);
            else _forward.Push(CurrentPath);

            target = _back.Pop();
        }

        if (target is null) return;

        await LoadAsync(target).ConfigureAwait(false);
    }

    public async Task GoForwardAsync(int depth)
    {
        if (depth < 1) return;

        string? target = null;

        for (var i = 0; i < depth && _forward.Count > 0; i++)
        {
            if (target is not null) _back.Push(target);
            else _back.Push(CurrentPath);

            target = _forward.Pop();
        }

        if (target is null) return;

        await LoadAsync(target).ConfigureAwait(false);
    }

    /// <summary>The menus are rebuilt from the stacks, which change on every
    /// navigation.</summary>
    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(BackSteps));
        OnPropertyChanged(nameof(ForwardSteps));
        OnPropertyChanged(nameof(HasBackSteps));
        OnPropertyChanged(nameof(HasForwardSteps));
    }

    // ---- and where you have been, whichever tab you were in ----------------

    /// <summary>How many folders the address bar's own menu lists. The same
    /// twelve as the two chevrons beside it, for the same reason.</summary>
    private const int RecentPlaceRows = 12;

    /// <summary>
    /// Where you have been lately — across tabs, across windows, and across
    /// runs.
    ///
    /// **Back and Forward were the only history the window offered, and both
    /// are per-tab and per-walk.** A folder reached in another tab, or before
    /// the last restart, was in neither; and going back and then somewhere else
    /// discards the forward stack, so a place you left five minutes ago could
    /// become unreachable except by typing it again. The recent-folder store
    /// has recorded every user-initiated navigation since it was written, and
    /// the only thing that ever read it was the sidebar's virtual listing.
    ///
    /// Through <c>Recording</c> rather than <c>Recents</c>: the switch that
    /// stops folders being recorded has to stop them being offered too, or
    /// turning it off leaves a menu of everywhere you went before you turned it
    /// off, on the most visible control in the window.
    ///
    /// **Nothing here asks the filesystem whether a remembered folder is still
    /// there.** The listing that shows the same store drops entries that have
    /// gone, and it can: it is already off the UI thread. This is read while
    /// building a menu, and a <c>Directory.Exists</c> on a disconnected share
    /// blocks for as long as the network takes to say no — which is exactly the
    /// stall <c>ReachablePath</c> exists to avoid. A folder that has gone
    /// navigates to the pane's ordinary "not there" message.
    /// </summary>
    public IReadOnlyList<RecentPlace> RecentPlaces
        => (Recording?.Recent(RecentKind.Folder, RecentPlaceRows) ?? [])
            .Select(entry => new RecentPlace(
                entry.Path,
                PlaceName(entry.Path),
                new RelayCommand(() => Detached(NavigateAsync(entry.Path), "navigate"))))
            .ToList();

    /// <summary>Nothing remembered is no button, rather than a button that
    /// opens onto an empty menu.</summary>
    public bool HasRecentPlaces => RecentPlaces.Count > 0;

    /// <summary>
    /// Announced from the store's own <c>Changed</c> and from
    /// <c>AppSettings.Changed</c>, rather than from <c>NotifyHistory</c> beside
    /// the two chevrons.
    ///
    /// **NotifyNavigationState runs at the START of a load and the recording
    /// happens after it finishes**, so a notice raised there describes the
    /// store as it was one navigation ago — the folder you had just walked into
    /// was never in the menu. Back, forward and refresh are deliberately not
    /// recorded, so they have nothing to announce.
    ///
    /// **And the two things that empty this menu never touch the load at all.**
    /// Turning "remember recent places" off changes the answer without writing
    /// to the store, and clearing the store — which the settings save does on
    /// its way past — changes it without any navigation. Hung on the events, a
    /// notice arrives for all three.
    ///
    /// Hopped to the UI thread when it is not already on it: recording happens
    /// after an await that may have resumed on a pool thread, and a property
    /// notice raised from there reaches a bound menu on the wrong one. Straight
    /// through when it is, so a settings save announces in the same turn it is
    /// applied.
    /// </summary>
    private void NotifyRecentPlaces()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(NotifyRecentPlaces);
            return;
        }

        OnPropertyChanged(nameof(RecentPlaces));
        OnPropertyChanged(nameof(HasRecentPlaces));
    }
}
