using System.ComponentModel;
using Vaktari.Core.FileSystem;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One side of the window: its own tab strip, its own active tab.
///
/// Splitting the window is two of these rather than a special case, which is
/// why PaneViewModel was built to own its path, history, sort and selection
/// from the beginning. The session schema has stored Panes as a list since v1
/// for the same reason.
/// </summary>
public sealed partial class PaneGroupViewModel : ObservableObject
{
    private readonly Func<PaneViewModel> _createPane;

    /// <summary>
    /// Raised when the active tab's location changes, or a different tab
    /// becomes active. The shell uses it to keep the sidebar's highlight on the
    /// place being viewed — this group has no idea whether it is the active
    /// side, and should not learn.
    /// </summary>
    public event EventHandler? LocationChanged;

    public PaneGroupViewModel(Func<PaneViewModel> createPane) => _createPane = createPane;

    public ObservableCollection<PaneViewModel> Tabs { get; } = new();

    [ObservableProperty] private PaneViewModel? _activeTab;

    /// <summary>Which side has focus. Drives the accent on the active side and
    /// decides where new tabs and pasted files land.</summary>
    [ObservableProperty] private bool _isActiveGroup;

    /// <summary>
    /// Whether this side carries the controls that belong to the WINDOW rather
    /// than to a pane: the details panel toggle, the split toggle, and the view
    /// options menu.
    ///
    /// **They are one set of controls, and a split drew two of them.** Split
    /// view is window state; the options menu opens window settings; the
    /// details panel is per-side but is the odd one out in a group of three,
    /// and two identical rows of icons in one toolbar reads as duplication
    /// rather than as per-side choice. The layout buttons stay on both sides
    /// because those genuinely differ per pane.
    ///
    /// True when there is no split, and on the RIGHT side when there is —
    /// rightmost is where a window's own controls sit.
    /// </summary>
    [ObservableProperty] private bool _showsWindowControls = true;

    partial void OnActiveTabChanged(PaneViewModel? oldValue, PaneViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnTabChanged;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnTabChanged;
        }

        RefreshInfo();
        LocationChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- details panel ----------------------------------------------------

    /// <summary>
    /// One panel per split side, not one per window.
    ///
    /// The panel describes what you have selected, and each side has its own
    /// selection — a single shared panel would keep swapping content as focus
    /// moved between halves, which is the opposite of useful when you are
    /// comparing two folders. That comparison is the reason a split exists.
    /// </summary>
    public InfoPanelViewModel Info { get; private set; } = new(null);

    [ObservableProperty] private bool _isInfoVisible;

    [ObservableProperty] private double _infoWidth = 280;

    /// <summary>
    /// Narrowest listing worth keeping once the panel has taken its share.
    /// Below this the names truncate to two characters and the listing stops
    /// being a listing — which is the state that prompted this.
    /// </summary>
    private const double MinimumListing = 420;

    /// <summary>Below this the preview and the field labels start colliding, so
    /// there is nothing left to read.</summary>
    private const double NarrowestInfo = 220;

    /// <summary>
    /// Moves the details panel's edge by a drag of its handle.
    ///
    /// **Here rather than in the drag handler**, because the upper bound is
    /// <see cref="MinimumListing"/> — past it <see cref="CanShowInfo"/> goes
    /// false and the panel vanishes mid-drag, which is a fault and not a
    /// resize. Writing that rule out a second time in the code-behind is how
    /// the two come to disagree.
    ///
    /// Negated: the panel is docked to the right, so dragging its handle left
    /// is a negative X and has to make the panel wider.
    ///
    /// A window too narrow to hold both refuses the drag rather than clamping
    /// to something it cannot show.
    /// </summary>
    public void ResizeInfoBy(double dx)
    {
        // Before the first measure there is no width to reason about. 560 is
        // the same ceiling the clamp lands on at a comfortable window size.
        var widest = GroupWidth > 0
            ? GroupWidth - MinimumListing * (ActiveTab?.TextScale ?? 1.0)
            : 560;

        if (widest < NarrowestInfo) return;

        var wanted = Math.Clamp(InfoWidth - dx, NarrowestInfo, widest);
        if (wanted == InfoWidth) return;

        InfoWidth = wanted;

        // Or the drag is forgotten at the next launch. The session is written
        // on a dirty flag, and every other persisted change raises this — the
        // width was simply a value nothing had ever been able to change before.
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when something persisted about this group's layout moves,
    /// so the shell can mark the session dirty.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>This side's full width, panel included. Fed by the view.</summary>
    [ObservableProperty] private double _groupWidth;

    /// <summary>
    /// Whether this side is wide enough to spare the panel's width.
    ///
    /// **Measured from the GROUP, not derived from the listing.** The earlier
    /// version added the panel's width back when `IsInfoVisible` was set — but
    /// that is the WISH, not whether the panel is laid out, so with the wish set
    /// and the panel suppressed it double-counted and reported room that was not
    /// there. Measuring the group is not circular and cannot oscillate.
    /// </summary>
    public bool CanShowInfo
    {
        get
        {
            // Before the first measure, allow it. Refusing on no information
            // would grey the toggle out at startup for no reason.
            if (GroupWidth <= 0) return true;

            var scale = ActiveTab?.TextScale ?? 1.0;

            return GroupWidth - InfoWidth >= MinimumListing * scale;
        }
    }

    /// <summary>
    /// What the markup binds. The user's choice AND the room to honour it —
    /// `IsInfoVisible` is left untouched when the window shrinks, so widening it
    /// again brings the panel back rather than making them press F11 twice.
    /// </summary>
    public bool IsInfoUsable => IsInfoVisible && CanShowInfo;

    /// <summary>
    /// Whether the toggle may be pressed. Under
    /// <see cref="NarrowPanelBehaviour.GrowWindow"/> it stays enabled even when
    /// the panel does not currently fit, because pressing it is what makes room
    /// — disabling it there would be the control lying about what it does.
    /// </summary>
    public bool IsInfoToggleEnabled =>
        CanShowInfo
        || Settings.AppSettings.Current.Views.NarrowDetailsPanel
               == NarrowPanelBehaviour.GrowWindow;

    /// <summary>
    /// Asks the window to widen by this many pixels. The GROUP cannot resize a
    /// window and should not learn how — the shell forwards it, exactly as it
    /// does for the properties dialog and the trash prompt.
    /// </summary>
    public event EventHandler<double>? GrowRequested;

    /// <summary>
    /// Raised when a panel that HAD been given extra room is closed again, so the
    /// window can hand it back.
    ///
    /// Only ever raised by a side that actually asked — closing a panel that fit
    /// all along gives nothing back, because nothing was taken.
    /// </summary>
    public event EventHandler? ReleaseRequested;

    /// <summary>
    /// Whether this side's current panel is only on screen because the window was
    /// grown for it. Cleared on release, so a second open-and-close does not hand
    /// back width twice.
    /// </summary>
    public bool GrewForPanel { get; private set; }

    /// <summary>
    /// Everything the panel appearing implies, in ONE place.
    ///
    /// It lives here rather than in `ToggleInfo` because the toolbar button and
    /// the flyout checkbox bind `IsChecked` straight to this property and never
    /// call the command — so logic in the command ran on F11 only, which is
    /// exactly why "widen the window" appeared not to work.
    /// </summary>
    partial void OnIsInfoVisibleChanged(bool value)
    {
        NotifyInfoFit();

        if (!value)
        {
            // Closing the panel cancels any request that was still waiting for a
            // measurement, or it would fire the moment the width arrived and grow
            // the window for a panel nobody wants any more.
            _roomRequestPending = false;

            if (!GrewForPanel)
            {
                // Not an error: this side's panel fitted all along, so it took no
                // width and has none to give back.
                return;
            }

            GrewForPanel = false;

            // The setting is checked here rather than in the window, so the
            // decision sits beside the one that caused the growth.
            // Read as "unless told to keep it", so an absent key means give it
            // back — see KeepWidthAfterPanelClose for why the polarity matters.
            if (Settings.AppSettings.Current.Views.KeepWidthAfterPanelClose)
            {
                PanelDebug("[vaktari] panel: keeping the extra width — "
                    + "\"shrink back when the panel closes\" is off");
                return;
            }

            ReleaseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Also moved here from the command, and it fixes a second thing: opening
        // the panel from the button used to leave its contents stale until the
        // next selection change.
        RefreshInfo();

        if (_restoringLayout) return;

        // A group that has just appeared — the right-hand side of a fresh split —
        // has not been measured yet, and `CanShowInfo` answers "yes, there is
        // room" on no information rather than greying the toggle out at startup.
        // Asking now would compute a shortfall from a width of zero, so the
        // request WAITS for the first measure instead.
        //
        // This is what made it take several presses: press one showed the panel,
        // the measure then hid it, press two turned the wish off, press three
        // finally ran with a real width.
        if (GroupWidth <= 0)
        {
            _roomRequestPending = true;
            return;
        }

        AskForRoomIfNeeded();
    }

    /// <summary>
    /// Set when the panel was asked for before this side had been measured.
    /// **One-shot on purpose:** re-asking on every resize would fight someone
    /// deliberately narrowing the window with the panel open, growing it back
    /// under their hands.
    /// </summary>
    private bool _roomRequestPending;

    /// <summary>
    /// The panel-sizing trace, off unless VAKTARI_PANEL_DEBUG=1.
    ///
    /// Every other ungated `[vaktari]` line in this project reports a FAILURE,
    /// which must always be visible. These report success on every grow and close,
    /// so they are chatter once the feature works — but they earned their keep
    /// across four rounds of debugging, so they are gated rather than deleted.
    /// </summary>
    internal static void PanelDebug(string message)
    {
        if (Environment.GetEnvironmentVariable("VAKTARI_PANEL_DEBUG") == "1")
            Console.Error.WriteLine(message);
    }

    partial void OnInfoWidthChanged(double value) => NotifyInfoFit();
    partial void OnGroupWidthChanged(double value)
    {
        // **A width dragged wide in a big window used to strand the panel.**
        // The drag clamps against the group width AT THE TIME, and nothing
        // re-clamped it afterwards — so widening the panel on a large display
        // and later opening the window smaller left CanShowInfo false, the
        // panel gone, and its handle gone with it. Getting it back meant
        // finding a window as wide as the one it was dragged in.
        //
        // The panel now gives room back as the window narrows, down to the
        // floor below which there is nothing left to read. Under that the old
        // behaviour still applies: it hides, and returns when there is room.
        if (value > 0)
        {
            var widest = value - MinimumListing * (ActiveTab?.TextScale ?? 1.0);

            if (widest >= NarrowestInfo && InfoWidth > widest) InfoWidth = widest;
        }

        NotifyInfoFit();

        if (!_roomRequestPending || value <= 0) return;

        _roomRequestPending = false;

        if (IsInfoVisible) AskForRoomIfNeeded();
    }

    /// <summary>
    /// True only while a session restore is assigning layout state, so a restored
    /// window does not resize itself on startup because a preference says the
    /// panel was open.
    /// </summary>
    private bool _restoringLayout;

    private void AskForRoomIfNeeded()
    {
        if (CanShowInfo) return;

        if (Settings.AppSettings.Current.Views.NarrowDetailsPanel
            != NarrowPanelBehaviour.GrowWindow) return;

        // Shortfall, not a fixed step: a constant would overshoot on a nearly
        // wide-enough window and fail to help on a very narrow one.
        var needed = MinimumListing * (ActiveTab?.TextScale ?? 1.0) + InfoWidth;

        if (needed <= GroupWidth) return;

        // Recorded BEFORE the request, so a release later knows this side is the
        // one that took the width.
        GrewForPanel = true;

        GrowRequested?.Invoke(this, needed - GroupWidth);
    }

    /// <summary>Public so a settings save can re-evaluate the toggle.</summary>
    public void RefreshInfoFit() => NotifyInfoFit();

    private void NotifyInfoFit()
    {
        OnPropertyChanged(nameof(CanShowInfo));
        OnPropertyChanged(nameof(IsInfoUsable));
        OnPropertyChanged(nameof(IsInfoToggleEnabled));
    }

    private IPropertiesProvider? _properties;

    public void UseProperties(IPropertiesProvider? properties)
    {
        _properties = properties;
        Info = new InfoPanelViewModel(properties);
        OnPropertyChanged(nameof(Info));
        RefreshInfo();
    }

    /// <summary>
    /// Just the flip. Refreshing and asking for room happen in
    /// `OnIsInfoVisibleChanged`, so the button, the checkbox and F11 all behave
    /// identically.
    /// </summary>
    [RelayCommand]
    private void ToggleInfo() => IsInfoVisible = !IsInfoVisible;

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.SelectedEntry)
                           or nameof(PaneViewModel.Summary))
            RefreshInfo();

        // The width is measured on the pane, but whether the panel fits is a
        // question about the GROUP, so the group has to hear about resizes.
        if (e.PropertyName is nameof(PaneViewModel.ViewportWidth)
                           or nameof(PaneViewModel.TextScale))
            NotifyInfoFit();

        if (e.PropertyName is nameof(PaneViewModel.CurrentPath))
            LocationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Only while this side's panel is open. Statting files to fill a hidden
    /// panel is work nobody asked for, and selection changes constantly.
    /// </summary>
    public void RefreshInfo()
    {
        if (!IsInfoVisible) return;

        if (ActiveTab is not { } pane) { Info.ShowNothing(); return; }

        var selection = pane.Selection;

        if (selection.Count > 1)
        {
            long total = 0;
            foreach (var entry in selection)
                if (!entry.IsDirectory) total += entry.Length;

            Info.ShowMany(selection.Count, total);
            return;
        }

        if (selection.Count == 1) { _ = Info.ShowAsync(selection[0]); return; }

        if (pane.SelectedEntry is { } single) _ = Info.ShowAsync(single);
        else Info.ShowNothing();
    }

    /// <summary>
    /// The "+" beside this side's tab strip. Lives on the group rather than the
    /// shell so each half opens tabs into itself, regardless of which side has
    /// focus at the time.
    /// </summary>
    [RelayCommand]
    private void NewTabHere()
        => AddTab(ActiveTab?.CurrentPath
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Opens a tab, optionally carrying the current one's view over and
    /// optionally without stealing focus.
    ///
    /// **A new tab used to start from nothing.** Ctrl+T reset hidden files,
    /// the layout, the sort, the grouping and the zoom, so somebody working
    /// with hidden files showing got a tab without them and had to set it all
    /// up again. Explorer and Dolphin both carry the current view across.
    ///
    /// And "open in new tab" — from the menu or a middle-click — jumped to the
    /// new tab, which is the opposite of what the phrase means: you ask for it
    /// in the background precisely so you can carry on where you are.
    /// </summary>
    public PaneViewModel AddTab(string path, PaneViewModel? like = null, bool activate = true)
    {
        var pane = _createPane();

        if (like is not null) pane.AdoptViewOf(like);

        Tabs.Add(pane);

        if (activate) ActiveTab = pane;

        _ = pane.NavigateAsync(path);
        return pane;
    }

    public PaneViewModel AddRestoredTab(TabState tab)
    {
        var pane = _createPane();
        pane.RestoreFrom(tab);
        Tabs.Add(pane);
        return pane;
    }

    public void CloseTab(PaneViewModel? pane)
    {
        pane ??= ActiveTab;
        if (pane is null) return;

        // Never leave a side with zero tabs — an empty column with no way back
        // is a dead end. Closing the last tab of a split side collapses the
        // split instead, which is handled by the shell.
        if (Tabs.Count <= 1) return;

        var index = Tabs.IndexOf(pane);
        var wasActive = ActiveTab == pane;

        // **Remembered before it is torn down.** Closing a tab threw its whole
        // state away — where it was, its history, its view — so Ctrl+Shift+T
        // had nothing to put back, and a tab closed by accident was gone.
        // Bounded, because this is a convenience and not a second session file.
        _closed.Push(pane.ToTabState());

        while (_closed.Count > 10) _closed.Pop();

        Tabs.Remove(pane);
        pane.Dispose();

        if (wasActive || ActiveTab is null)
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    /// <summary>The tabs closed here, most recent first.</summary>
    private readonly Stack<TabState> _closed = new();

    public bool CanReopenTab => _closed.Count > 0;

    /// <summary>
    /// Puts back the last tab closed on this side, where it was and with its
    /// history — Ctrl+Shift+T, which every browser and both references answer
    /// and this did not.
    /// </summary>
    public PaneViewModel? ReopenClosedTab()
    {
        if (_closed.Count == 0) return null;

        var pane = AddRestoredTab(_closed.Pop());

        ActiveTab = pane;
        pane.RefreshIfUnloaded();

        return pane;
    }

    /// <summary>Closes every tab but this one. Explorer and Dolphin both offer
    /// it, and with a dozen tabs open it is the only practical way back.</summary>
    public void CloseOtherTabs(PaneViewModel? keep)
    {
        keep ??= ActiveTab;
        if (keep is null) return;

        foreach (var pane in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
            CloseTab(pane);
    }

    /// <summary>Closes everything to the right of this tab.</summary>
    public void CloseTabsToTheRight(PaneViewModel? from)
    {
        from ??= ActiveTab;
        if (from is null) return;

        var at = Tabs.IndexOf(from);
        if (at < 0) return;

        foreach (var pane in Tabs.Skip(at + 1).ToList()) CloseTab(pane);
    }

    public void Cycle(int delta)
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var i = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    public void SelectTabByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count) ActiveTab = Tabs[index];
    }

    public PaneState ToPaneState() => new()
    {
        Tabs = Tabs.Select(t => t.ToTabState()).ToList(),
        ActiveTabIndex = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab)),
        IsInfoVisible = IsInfoVisible,
        InfoWidth = InfoWidth,
    };

    public void RestoreFrom(PaneState state)
    {
        // Guarded so a restored preference does not make the window resize itself
        // during startup. The panel still appears once the group is measured and
        // found wide enough; it just will not demand to be.
        _restoringLayout = true;
        try
        {
            IsInfoVisible = state.IsInfoVisible;
            InfoWidth = state.InfoWidth > 0 ? state.InfoWidth : 280;
        }
        finally
        {
            _restoringLayout = false;
        }
    }

    public void DisposeAll()
    {
        foreach (var pane in Tabs) pane.Dispose();
        Tabs.Clear();
    }
}
