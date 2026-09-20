using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The windows this one opens, and what has to be re-read when settings
/// change.
///
/// **The shell owns no windows**, which is why almost every member here is an
/// event rather than a call: the view opens the dialog and hands back what was
/// chosen. Settings arrive the same way and reach the panes by being applied
/// to each in turn, because a preference is not a property anybody binds — it
/// is a file that has changed underneath the whole window.
/// </summary>
public sealed partial class ShellViewModel
{
    public void OnSettingsChanged()
    {
        // The tile and cell metrics are computed from the pane's scale AND the
        // global spacing settings, but only the scale raises a notification.
        // Without this, a spacing change would reach only the panes that
        // happened to rescale afterwards — which is the trap the old
        // application-level filter was trying to avoid, solved at the right end.
        foreach (var group in new[] { Left, Right })
            if (group is not null)
                foreach (var tab in group.Tabs)
                {
                    tab.RefreshScale();
                    tab.RefreshDecorations();

                    // Same shape as the two above and the same reason: the
                    // selection boxes are read from the live settings on every
                    // binding evaluation, and nothing re-evaluates a binding on
                    // a row that is already realized unless it is told to.
                    tab.RefreshSelectionBoxes();
                }

        // Which pointer a row wears is the same kind of question and reaches
        // the same way — see RefreshActivation, which the desktop's own change
        // also goes through.
        RefreshActivation();

        // The narrow-panel behaviour changes whether the toggle may be pressed,
        // and that is computed rather than stored — so it has to be re-raised or
        // a greyed button stays greyed until the next resize.
        foreach (var group in new[] { Left, Right })
            group?.RefreshInfoFit();

        // The flyout's size boxes read the PANE's points and pixels through
        // this shell, so a pane notification does not reach them. The interface
        // text size is what makes that matter: it moves the size a pane draws
        // at without touching the pane's own zoom, so without this the box goes
        // on quoting a size nothing on screen is any more.
        NotifyTargetSizes();

        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(ShowFreeSpace));

        // Free space now prints on the drive rows rather than in the status bar,
        // and those rows are separate objects — raising it here does not reach
        // them, so each one is told directly.
        foreach (var group in Sidebar.Groups)
            foreach (var item in group.Places)
                item.RaiseCapacityVisibilityChanged();

        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(CanShowProperties));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowSortByInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewWindowInMenu));
        OnPropertyChanged(nameof(ShowAddToPlacesInMenu));
        OnPropertyChanged(nameof(ShowAddSelectionToPlaces));
        OnPropertyChanged(nameof(ShowAddCurrentToPlaces));
        OnPropertyChanged(nameof(ShowCopyLocationInMenu));

        // Left and Right, not a Groups collection — this view model has no such
        // thing, and inventing one for a loop would be the tail wagging the dog.
        foreach (var group in new[] { Left, Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs)
                tab.RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// Tells every open pane to re-ask whether one click opens.
    ///
    /// **The desktop can change its mind while the window is open.** A Plasma
    /// scheme change re-reads the palette and rewrites
    /// <see cref="PaneViewModel.SystemSingleClick"/>, and that is a plain
    /// static with nothing watching it — so a listing already on screen went on
    /// showing the pointer it was built with. Separate from
    /// <see cref="OnSettingsChanged"/> because that path is a SAVE, and the
    /// desktop change is not one.
    /// </summary>
    public void RefreshActivation()
    {
        foreach (var group in new[] { Left, Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs) tab.RefreshActivation();
        }
    }

    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raised once the defaults have been changed and are already in force, so
    /// the window can write them out through the settings store it owns. The
    /// shell has no store of its own, and leaves what it cannot do itself by an
    /// event, the way it does for every dialog it needs opened.
    /// </summary>
    public event EventHandler<Core.Settings.SettingsState>? DefaultViewChanged;

    /// <summary>
    /// "Use this view for all folders".
    ///
    /// **A layout was a property of a tab and of nothing else.** Choosing the
    /// large grid was a per-tab click; the session brings back the tabs that
    /// were open, and the per-folder store answers for folders that have
    /// already been given a layout — so between them they remember where you
    /// have been and nothing at all about where you are going. There was no way
    /// to say "this is how I want folders to look", which is the question both
    /// reference file managers answer: Explorer with "Apply to Folders",
    /// Dolphin with a common display style for all folders.
    ///
    /// Two halves, and the second is what makes the first true: the pane's view
    /// becomes the default, and every folder that had been given an override is
    /// forgotten. Without the forget, "all folders" would mean "all folders
    /// except the ones you have already touched" — and those are precisely the
    /// ones somebody asking this question has been touching.
    ///
    /// The tabs already open are left alone. This is about the folders you go
    /// to next, not about rearranging what is on screen while you look at it.
    /// </summary>
    [RelayCommand]
    private void UseThisViewEverywhere()
    {
        if (ActiveTab is not { } pane) return;

        var settings = Settings.AppSettings.Current;

        var next = settings with
        {
            Views = settings.Views with
            {
                DefaultView = pane.View,
                DefaultSort = pane.Sort,
                DefaultSortDescending = pane.SortDescending,
                DefaultGroupBy = pane.GroupBy,
            },
        };

        // Applied before it is saved, so the next tab opened in this window
        // gets it without a restart: a pane reads these when it is constructed.
        Settings.AppSettings.Apply(next);

        var forgotten = PaneViewModel.FolderViews?.ForgetAll() ?? 0;

        // **Nothing on screen moved when it was pressed.** The tabs already
        // open are deliberately left alone, the layout controls were taken out
        // of this flyout, and the whole effect is on folders opened later — so
        // the button had no visible result at all, while quietly emptying the
        // per-folder store. The status line is where this codebase says what an
        // action you cannot see has done, and the other route to the same
        // ForgetAll — the settings dialog's "Forget remembered views" — counts
        // what it is about to drop, so this counts what it dropped.
        pane.Status = forgotten switch
        {
            0 => $"folders will open as {LayoutName(pane.View)} from now on",
            1 => $"folders will open as {LayoutName(pane.View)} from now on — one folder's own view forgotten",
            _ => $"folders will open as {LayoutName(pane.View)} from now on — {forgotten:N0} folders' own views forgotten",
        };

        DefaultViewChanged?.Invoke(this, next);
    }

    /// <summary>The names the toolbar chip and the shortcut sheet already give
    /// the three layouts, so the status line calls a layout what the button
    /// that sets it is called rather than what its enum member is spelled.
    /// </summary>
    private static string LayoutName(ViewMode view) => view switch
    {
        ViewMode.Grid => "a large grid",
        ViewMode.Compact => "a small grid",
        _ => "a list",
    };

    /// <summary>Raised so the window can show the shortcut list; a view model
    /// has no business owning a window.</summary>
    public event EventHandler? ShortcutsRequested;

    [RelayCommand]
    private void ShowShortcuts() => ShortcutsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised so the window can show the tour — the same shape as
    /// the sheet above, for the same reason.</summary>
    public event EventHandler? TourRequested;

    [RelayCommand]
    private void ShowTour() => TourRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised so the window can open the command palette — the same
    /// shape as the two above. The window runs what was picked, because the
    /// pick is only known once the palette has closed.</summary>
    public event EventHandler? PaletteRequested;

    [RelayCommand]
    private void ShowPalette() => PaletteRequested?.Invoke(this, EventArgs.Empty);
}
