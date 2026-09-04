using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>Grouping the listing, and the header each run gets.</summary>
public sealed partial class PaneViewModel
{
    // ---- grouping ---------------------------------------------------------

    [ObservableProperty] private GroupMode _groupBy = GroupMode.None;

    /// <summary>
    /// The grouping the LISTING is actually built with, which is the chosen one
    /// only in the details view.
    ///
    /// **A grouping chosen in Details went on reordering grid and compact.** The
    /// menu that sets it is hidden outside Details, because the heading is part
    /// of the details row template and the other two lay out fixed-size cells in
    /// a wrap panel — but hiding the menu only stopped it being CHANGED there.
    /// The ordering was never gated, so a folder grouped by date came up as tiles
    /// in band order, with nothing to say what the runs were and no row left to
    /// clear it with: the menu that had done it was no longer on screen.
    ///
    /// Ignored rather than cleared, so the bands are still there when you come
    /// back — GroupBy stays what you set it to, and the menu still shows it
    /// chosen.
    /// </summary>
    private GroupMode EffectiveGroupBy
        => View == ViewMode.Details ? GroupBy : GroupMode.None;

    public bool IsGroupedByName => GroupBy == GroupMode.Name;

    public bool IsGroupedBySize => GroupBy == GroupMode.Size;

    public bool IsGroupedByModified => GroupBy == GroupMode.Modified;

    public bool IsGroupedByKind => GroupBy == GroupMode.Kind;

    public bool IsUngrouped => GroupBy == GroupMode.None;

    partial void OnGroupByChanged(GroupMode value)
    {
        OnPropertyChanged(nameof(IsUngrouped));
        OnPropertyChanged(nameof(IsGroupedByName));
        OnPropertyChanged(nameof(IsGroupedBySize));
        OnPropertyChanged(nameof(IsGroupedByModified));
        OnPropertyChanged(nameof(IsGroupedByKind));

        if (!_suppressReload) ApplyFilter();
    
        RememberFolderView();
    }

    public string? HeaderFor(string fullPath)
        => _groupHeaders.TryGetValue(fullPath, out var label) ? label : null;
}
