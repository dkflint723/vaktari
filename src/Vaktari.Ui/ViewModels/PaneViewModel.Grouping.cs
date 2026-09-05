using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// A band's heading: the name of the band, and how many of the folder's rows
/// are in it.
///
/// **The heading was the label and nothing else.** RecomputeGroups stored
/// `Grouping.Label(...)` straight into the map the rows read, so "This month"
/// or "TXT" stood over a run with no way to learn how long the run was short
/// of counting the rows — and the run is exactly the thing a heading is there
/// to describe. A run can also be longer than the window, so counting them is
/// not something the eye can do.
///
/// Label and Count stay separate rather than the drawn string being stored:
/// the Label is the band's identity, which is what decides where a run ends
/// and what the tests about duplicate bands are about, and <see cref="Text"/>
/// is only how it is spelled on screen.
/// </summary>
public sealed record GroupHeader(string Label, int Count)
{
    /// <summary>What the heading draws. N0 because the status bar's counts are
    /// grouped the same way, and a band in a large folder runs to five
    /// digits.</summary>
    public string Text => $"{Label} ({Count:N0})";
}

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

    public GroupHeader? HeaderFor(string fullPath)
        => _groupHeaders.TryGetValue(fullPath, out var header) ? header : null;

    /// <summary>
    /// Picks every row under one heading, given the path of the row the
    /// heading sits on.
    ///
    /// **The heading was a label and nothing more.** It named a run of rows and
    /// gave no way to take that run: selecting "This month" meant clicking its
    /// first row and shift-clicking its last, which needs both ends on screen
    /// at once — and a band is exactly as long as it wants to be.
    ///
    /// The run is read out of <c>Entries</c> rather than out of the rows on
    /// screen, and the two differ once a folder is open in place: the spliced
    /// listing carries that folder's children between its parent and the next
    /// row, and those children belong to their own folder, not to this band —
    /// nothing gives them a heading either (HeaderFor answers null for them).
    /// It is also the rule SelectWholeFolder states for Ctrl+A: everything
    /// downstream of a selection is written for one that lives in ONE folder.
    ///
    /// The length comes from the header's own Count, so the number the heading
    /// shows and the rows this takes cannot disagree.
    /// </summary>
    [RelayCommand]
    public void SelectGroup(string? firstPath)
    {
        if (firstPath is null || !_groupHeaders.TryGetValue(firstPath, out var header)) return;

        var start = -1;

        for (var i = 0; i < Entries.Count; i++)
        {
            if (!string.Equals(Entries[i].FullPath, firstPath, StringComparison.Ordinal)) continue;

            start = i;
            break;
        }

        if (start < 0) return;

        var selection = SelectedEntries;

        selection.Clear();

        for (var i = start; i < Entries.Count && i < start + header.Count; i++)
            selection.Add(Entries[i]);

        // The focused row too, so the keyboard carries on from the band that
        // was just picked rather than from wherever it happened to be — the
        // same thing Reselect does after a rebuild.
        if (selection.Count > 0) SelectedEntry = selection[0];
    }
}
