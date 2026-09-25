using System.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Putting the window back the way it was left.
///
/// **A session is written continuously and read once.** Every tab, split and
/// folded section reports itself here as it changes, so the file on disk is
/// always current — and nothing reads it back except a start, which is why a
/// shape that cannot be restored is a fault that only shows up the next
/// morning. Restoring is deliberately forgiving: a path that has gone, or a
/// key written by an older build, gives a usable window rather than none.
/// </summary>
public sealed partial class ShellViewModel
{
    // ---- session -------------------------------------------------------

    /// <summary>
    /// <paramref name="state"/> null means "do not restore" — the caller has
    /// already applied the startup setting and decided the session should be
    /// ignored. <paramref name="openFolder"/> is where to start instead; null
    /// means home, which is what this always did.
    ///
    /// The decision lives in the caller rather than here because the caller is
    /// the only place that holds both stores, and because a view model that
    /// reaches for preferences to decide whether to use its own argument is
    /// harder to reason about than one that is simply told.
    /// </summary>
    public void Start(SessionState? state, string? openFolder = null, int windowIndex = 0)
    {
        if (_started) return;
        _started = true;

        var home = string.IsNullOrWhiteSpace(openFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : openFolder;
        var window = state?.Windows.ElementAtOrDefault(windowIndex);

        if (window is not null)
        {
            Sidebar.Width = window.SidebarWidth;
            Sidebar.Rail = window.Rail;

            // Before InitializeAsync below, so the first list of places arrives
            // already folded the way it was left rather than opening and
            // shutting on screen.
            //
            // `?? []` is load-bearing rather than defensive: a session written
            // before folding existed has no such key, deserialization does not
            // run property initializers, and the absent key arrives as null —
            // so without this the first launch after an upgrade would throw on
            // the foreach inside, for everybody.
            Sidebar.RestoreCollapsed(window.CollapsedSections ?? []);
            SplitRatio = window.SplitRatio;
            FontScale = window.FontScale <= 0 ? 1.0 : window.FontScale;
            IconScale = window.IconScale <= 0 ? 1.0 : window.IconScale;
        }

        _ = Sidebar.InitializeAsync();

        var panes = window?.Panes;

        _restoring = true;
        try
        {
            if (panes is null || panes.Count == 0 || panes[0].Tabs.Count == 0)
            {
                Left.AddTab(home, like: LikeTab);
            }
            else
            {
                Restore(Left, panes[0]);

                if (panes.Count > 1 && panes[1].Tabs.Count > 0)
                {
                    var right = CreateGroup();
                    Restore(right, panes[1]);
                    Right = right;
                }
                else
                {
                    // Split was closed at save time; keep what it was showing
                    // so reopening after a restart lands back in place.
                    _rememberedRight = window?.RememberedRightPane;
                }
            }

            var activeIndex = window?.ActivePaneIndex ?? 0;
            ActiveGroup = activeIndex == 1 && Right is not null ? Right : Left;
        }
        finally
        {
            _restoring = false;
        }

        // Assigned while suppressed, so it never triggered its own load.
        ActiveGroup.ActiveTab?.RefreshIfUnloaded();
    }

    /// <summary>
    /// The tab a window was opened FROM, when this shell belongs to one.
    ///
    /// The same thing Ctrl+T already carries between tabs: hidden files, the
    /// layout, the sort, the grouping and the zoom. Null for a shell that was
    /// not opened from anywhere, which is every launch and every test.
    /// </summary>
    internal PaneViewModel? LikeTab { get; set; }

    private static void Restore(PaneGroupViewModel group, PaneState state)
    {
        group.RestoreFrom(state);

        foreach (var tab in state.Tabs) group.AddRestoredTab(tab);
        group.ActiveTab = group.Tabs[Math.Clamp(state.ActiveTabIndex, 0, group.Tabs.Count - 1)];
    }

    /// <summary>
    /// This window's entry, on its own.
    ///
    /// Split out of ToSessionState so the family can compose one session out of
    /// several windows, and so both routes funnel through the same body — a
    /// field added to one and not the other is how the two would drift.
    /// </summary>
    public WindowSession ToWindowSession()
    {
        var geometry = GeometryProvider?.Invoke() ?? new WindowSession();

        var panes = Right is null
            ? new List<PaneState> { Left.ToPaneState() }
            : [Left.ToPaneState(), Right.ToPaneState()];

        return geometry with
        {
            SidebarWidth = Sidebar.Width,
            Rail = Sidebar.Rail,
            CollapsedSections = Sidebar.CollapsedSections,
            SplitRatio = SplitRatio,
            FontScale = FontScale,
            IconScale = IconScale,
            RememberedRightPane = Right is null ? _rememberedRight : null,
            Panes = panes,
            ActivePaneIndex = ReferenceEquals(ActiveGroup, Right) ? 1 : 0,
        };
    }

    /// <summary>A session holding this window alone — which is what a shell
    /// with no family around it honestly is.</summary>
    public SessionState ToSessionState() => new()
    {
        Version = SessionState.CurrentVersion,
        Windows = [ToWindowSession()],
    };

    /// <summary>
    /// The whole application's session, when this shell belongs to a window
    /// that has a family. Null for a shell on its own, which then writes only
    /// itself — so every existing view-model test is unchanged.
    /// </summary>
    internal Func<SessionState>? WholeSession { get; set; }

    public void NotifyWindowChanged() => MarkDirty();

    private void MarkDirty()
    {
        // Nothing before Start() is worth saving, and property setters fire
        // during construction while Sidebar and the groups are still null.
        if (!_started || _restoring || _store is null) return;

        // **The whole family, not this window.** One window writing only itself
        // over a session that holds three is how the other two would be lost on
        // the next launch.
        _store.NotifyChanged(WholeSession?.Invoke() ?? ToSessionState());
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Beside the switch rather than in it, because ShowHidden is also one
        // of the session's own and a case label can be listed only once. The
        // folder tree follows the active pane's answer — see FollowHidden —
        // and a background tab's toggle is not that.
        if (e.PropertyName == nameof(PaneViewModel.ShowHidden)
            && sender is PaneViewModel pane && ReferenceEquals(pane, ActiveTab))
            Sidebar.FollowHidden(pane.ShowHidden);

        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.CurrentPath):
                // The destination list excludes the folder you are already in,
                // so it changes whenever the pane navigates.
                NotifyTransferTargets();
                MarkDirty();
                break;

            // The flyout's box reads the pane through TargetIconPixels, so the
            // pane saying its pixels changed is not enough on its own.
            case nameof(PaneViewModel.IconPixels):
                NotifyTargetSizes();
                break;

            case nameof(PaneViewModel.Selection):

            // **The Properties row went on being offered after the tab moved
            // into This PC.** These entries are refreshed when the SELECTION
            // changes, and moving away from a folder where nothing was picked
            // changes no selection at all — so the row survived the move and
            // was still there to be chosen. Taken from IsRealFolder rather
            // than CurrentPath because CurrentPath is assigned from
            // LoadListingAsync on a pool thread, while the pane re-raises
            // IsRealFolder from its own hop to the UI thread, which is the
            // only place a bound menu row may hear about it.
            case nameof(PaneViewModel.IsRealFolder):

            // And the focused row on its own. It counts as a selection here,
            // and it does not go through the Selection collections: setting
            // SelectedEntry raises HasSelection and nothing else this switch
            // was listening for.
            case nameof(PaneViewModel.HasSelection):
                NotifySelectionMenu();
                break;

            case nameof(PaneViewModel.Sort):
            case nameof(PaneViewModel.SortDescending):
            case nameof(PaneViewModel.ShowHidden):
            // The column choice is per tab and lives in the session. A property
            // that ToTabState writes but this switch does not list only
            // persists when something else happens to change first.
            case nameof(PaneViewModel.HideSizeColumn):
            case nameof(PaneViewModel.HideModifiedColumn):
            case nameof(PaneViewModel.ShowTypeColumn):
            case nameof(PaneViewModel.ShowCreatedColumn):

            // **Switching a tab to tiles, or grouping it, was saved only by
            // luck.** Both are written by ToTabState and neither was listed, so
            // the change reached the store only when something else marked the
            // session first — a crash or a kill before that came back with the
            // old layout. Listed here rather than trusted to ride on the scale:
            // the scale that changes with the view is equal by default, and an
            // equal assignment raises nothing.
            case nameof(PaneViewModel.View):
            case nameof(PaneViewModel.GroupBy):
                MarkDirty();
                break;

            case nameof(PaneViewModel.Status):
            case nameof(PaneViewModel.Title):
                OnPropertyChanged(nameof(ActiveStatus));
                break;


            case nameof(PaneViewModel.Summary):
                OnPropertyChanged(nameof(ActiveSummary));

                break;
        }
    }
}
