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

    /// <summary>
    /// How many pixels narrower than its chosen width the name is drawn right
    /// now, because the pane is narrower than the one its width was chosen
    /// in. Worked out by the heading (DetailsColumns.Give) and read by the
    /// heading and every row, so they cannot disagree. Not saved: it belongs
    /// to the pane's width at this moment.
    /// </summary>
    [ObservableProperty] private double _nameGive;

    /// <summary>True while a column's edge is being dragged in this tab, when
    /// <see cref="NameGive"/> is held where it was so the edge follows the
    /// pointer.</summary>
    [ObservableProperty] private bool _isResizingColumns;

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

    // ---- when the columns do not fit -------------------------------------------
    //
    // **The columns scroll sideways once nothing else will make them fit.** The
    // room after the last column goes first, then the name gives way down to its
    // floor (NameGive); only a row that is still too wide after both scrolls,
    // headings and rows together. Worked out once, on the heading, like the give
    // (DetailsColumns.OnHeadingLaidOut), and handed out from here so the two
    // scrollers take their content width from the same number.

    /// <summary>True while the columns are wider than the visible row even
    /// with the name at its floor, which is when the headings and the rows
    /// scroll sideways. Not saved: it belongs to the pane's width now.</summary>
    [ObservableProperty] private bool _columnsOverflow;

    /// <summary>
    /// How wide the row is drawn while <see cref="ColumnsOverflow"/>, margins
    /// included, in pixels as drawn — or NaN, "as wide as the pane", while the
    /// columns fit. **One number for both scrollers**: the heading's panel and
    /// the rows' panel both take it, because two widths that round apart by a
    /// device pixel leave the heading a pixel short at the far right (measured:
    /// 0.8 at 125% device scaling and 125% zoom).
    /// </summary>
    [ObservableProperty] private double _detailsRowWidth = double.NaN;

    /// <summary>
    /// Asked of whatever draws this tab's headings: fit one column to its
    /// widest entry, or every column drawn when the column is null. A view
    /// model cannot measure text, so the heading answers it — see ColumnScroll.
    /// </summary>
    public event EventHandler<Core.Settings.DetailsColumn?>? ColumnFitRequested;

    /// <summary>Fits one column, as a double-click on its edge does.</summary>
    public void FitColumn(Core.Settings.DetailsColumn column) => ColumnFitRequested?.Invoke(this, column);

    /// <summary>
    /// Every column drawn, fitted to its widest entry. **Only in the List
    /// layout**: the other two draw no headings, so there is nothing to fit and
    /// nothing laid out to measure the visible row against.
    ///
    /// Greyed by CanExecute in the menus; refused here as well, because a
    /// command run by name — the palette — is executed without being asked
    /// first, and a fit run against a hidden heading measures a row of nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDetailsView))]
    private void SizeAllColumnsToFit()
    {
        if (!IsDetailsView) return;

        ColumnFitRequested?.Invoke(this, null);
    }

    /// <summary>
    /// Columns given widths of their own at 100%, all in one assignment, so a
    /// fit of every column is one change to the tab and one rewrite of its
    /// metrics.
    ///
    /// **The name keeps where it is unless it is one of them**, for the reason
    /// the drag gives it a width of its own on the first move: a name that
    /// fills would take up every pixel the fitted columns gained or gave back,
    /// and the fit would visibly move the name instead. A name that is one of
    /// them is chosen in the visible row, as a dragged one is, so it is not
    /// given away again the moment it is set.
    /// </summary>
    public void ChooseColumnWidths(
        IReadOnlyDictionary<Core.Settings.DetailsColumn, double> widths, double drawnName, double visibleRow)
    {
        var next = ColumnWidths;

        if (NameFills || widths.ContainsKey(Core.Settings.DetailsColumn.Name))
            next = next with { Name = drawnName, Span = visibleRow };

        foreach (var (column, width) in widths)
            next = next.WithWidth(column, PaneScale.Clamp(column, Math.Round(width, 1)));

        ColumnWidths = next;
    }
}
