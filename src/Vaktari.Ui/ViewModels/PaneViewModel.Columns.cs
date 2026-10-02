using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Which columns a pane shows, and which of them fit.
///
/// **Two rules, and they are not the same one.** What the person CHOSE is
/// per-pane and persisted with the tab; what is actually DRAWN also depends on
/// how wide the pane is right now, so a column that is switched on can still
/// be absent from a narrow split. Everything here answers one or the other,
/// and the widths are the one place either is decided.
///
/// Split out of PaneViewModel, which had grown to 5,700 lines with 272 members
/// in it — see roadmap 22. Nothing moved changed: this is the same code in a
/// file whose name says what it is for.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// Set by the view as the pane resizes. Columns drop out in priority order
    /// as space runs out rather than being squeezed or clipped — which is what
    /// makes a narrow split pane still readable.
    /// </summary>
    [ObservableProperty] private double _viewportWidth = 1000;

    // ---- which columns this pane shows ------------------------------------
    //
    // **Per pane, the way sort and grouping are.** A reference listing beside
    // a working one wants different columns, and a choice made on one side of
    // a split must not move the other. Persisted with the tab, restored with
    // it, and phrased so that false is what the pane showed before there was a
    // chooser — an absent session key arrives as default(T).

    [ObservableProperty] private bool _hideSizeColumn;
    [ObservableProperty] private bool _hideModifiedColumn;
    [ObservableProperty] private bool _showTypeColumn;
    [ObservableProperty] private bool _showCreatedColumn;

    // Each also records the folder, because "remember the view for each folder"
    // covers what the view options menu offers and these four are in it.
    // RememberFolderView is inert unless the preference is on, and RestoreFrom
    // holds `_restoringView` while it replays a session, so neither route
    // gives a folder an opinion it never had.
    partial void OnHideSizeColumnChanged(bool value)
    {
        NotifyColumns();
        RememberFolderView();
    }

    partial void OnHideModifiedColumnChanged(bool value)
    {
        NotifyColumns();
        RememberFolderView();
    }

    partial void OnShowTypeColumnChanged(bool value)
    {
        NotifyColumns();
        RememberFolderView();
    }

    partial void OnShowCreatedColumnChanged(bool value)
    {
        NotifyColumns();
        RememberFolderView();
    }

    // The ticks in the chooser. OneWay from these, with the click going through
    // the commands below — the same shape as every other tick in the menus.
    public bool IsSizeColumnShown => !HideSizeColumn;
    public bool IsModifiedColumnShown => !HideModifiedColumn;
    public bool IsTypeColumnShown => ShowTypeColumn;
    public bool IsCreatedColumnShown => ShowCreatedColumn;

    [RelayCommand] private void ToggleSizeColumn() => HideSizeColumn = !HideSizeColumn;
    [RelayCommand] private void ToggleModifiedColumn() => HideModifiedColumn = !HideModifiedColumn;
    [RelayCommand] private void ToggleTypeColumn() => ShowTypeColumn = !ShowTypeColumn;
    [RelayCommand] private void ToggleCreatedColumn() => ShowCreatedColumn = !ShowCreatedColumn;

    // **Two questions, both of which have to say yes.** The width rule was here
    // first and stays: a column that no longer fits is dropped whatever the
    // chooser says, because a chosen column crushing the name is worse than an
    // absent one. The choice is ANDed on top rather than replacing it.
    public bool ShowSize => !HideSizeColumn && ViewportWidth >= 340 * TextScale;

    public bool ShowModified => !HideModifiedColumn && ViewportWidth >= 520 * TextScale;

    /// <summary>
    /// The type column, off until it is asked for.
    ///
    /// **Its own width threshold, and a deliberately generous one.** It sits
    /// between the name and the size, so every pixel it takes comes out of the
    /// name — the only column that stretches. Below this width the name is
    /// already trimming and there is nothing left to give.
    /// </summary>
    public bool ShowType => ShowTypeColumn && ViewportWidth >= 620 * TextScale;

    /// <summary>
    /// When the file was made, off until it is asked for — the same shape as
    /// the type column, and off for the same reason: most listings are read by
    /// modified date and a second date column beside it is noise until somebody
    /// wants it.
    ///
    /// **The highest of the four thresholds the chooser can be held to (340,
    /// 520, 620, this), because it is the last column in the row.** It is the
    /// type column's 620 plus its own 150 (ColCreated at scale 1), which is the
    /// width a pane needs before this one can appear without taking back what
    /// that arithmetic already granted the columns to its left.
    /// </summary>
    public bool ShowCreated => ShowCreatedColumn && ViewportWidth >= 770 * TextScale;
    public bool ShowPermissions => ViewportWidth >= 680 * TextScale;
    public bool ShowMetadata =>
        ViewportWidth >= 840 * TextScale && !IsRecentListing && !IsTrashListing;

    // ---- how wide this tab's columns are -------------------------------------
    //
    // **Per tab, beside the ticks, and for their reason.** The widths were one
    // preference for every pane, so a drag in the left half of a split moved the
    // right half's columns too. They are the tab's now: saved with it, restored
    // with it, reopened with it, and carried to a tab opened from it the way the
    // ticks are (AdoptViewOf).

    /// <summary>
    /// This tab's column widths at 100%. A tab with nothing to copy starts at
    /// what the settings carried when the widths were one preference — see
    /// <see cref="Core.Settings.DetailsViewSettings.StartingWidths"/> — which
    /// is the designed widths on any install that never dragged one.
    /// </summary>
    [ObservableProperty]
    private Core.Settings.ColumnWidths _columnWidths =
        Settings.AppSettings.Current.Views.Details.StartingWidths;

    partial void OnColumnWidthsChanged(Core.Settings.ColumnWidths value)
        => OnPropertyChanged(nameof(NameFills));

    /// <summary>True while the name column takes whatever the others leave,
    /// which is how every tab starts and what a reset goes back to.</summary>
    public bool NameFills => ColumnWidths.Name <= 0;

    /// <summary>
    /// Sets one column to a width at 100%, held to that column's range. The
    /// window's grip works out the width from where the pointer is; this is
    /// only where it is kept.
    /// </summary>
    public void SetColumnWidth(Core.Settings.DetailsColumn column, double width)
        => ColumnWidths = ColumnWidths.WithWidth(column, PaneScale.Clamp(column, Math.Round(width, 1)));

    /// <summary>
    /// Every column of THIS tab back to its designed width, and the name back
    /// to filling what they leave. The other tabs, the other half of a split
    /// and the starting widths in the settings are not touched: the way back
    /// from a drag is as local as the drag was.
    /// </summary>
    [RelayCommand]
    private void ResetColumnWidths() => ColumnWidths = Core.Settings.ColumnWidths.Designed;
}
