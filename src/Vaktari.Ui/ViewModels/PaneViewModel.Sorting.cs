using CommunityToolkit.Mvvm.Input;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>Sort order, and the glyphs the column headers show for it.</summary>
public sealed partial class PaneViewModel
{
    // ---- sorting -------------------------------------------------------

    /// <summary>
    /// Click a column heading to sort by it; click again to reverse. The sort
    /// state was implemented, persisted per tab and completely unreachable —
    /// there was no control anywhere that set it.
    /// </summary>
    [RelayCommand]
    public void SortBy(string? field)
    {
        var target = field switch
        {
            "size" => SortField.Size,
            "modified" => SortField.Modified,
            "kind" => SortField.Kind,
            _ => SortField.Name,
        };

        if (Sort == target) SortDescending = !SortDescending;
        else { Sort = target; SortDescending = false; }

        NotifySortGlyphs();
    }

    private string Glyph(SortField field)
        => Sort != field ? "" : SortDescending ? " \u25BE" : " \u25B4";

    public string NameSortGlyph => Glyph(SortField.Name);

    public string SizeSortGlyph => Glyph(SortField.Size);

    public string ModifiedSortGlyph => Glyph(SortField.Modified);

    /// <summary>The arrow over the type column's heading. It had no heading to
    /// sit on until there was a type column.</summary>
    public string KindSortGlyph => Glyph(SortField.Kind);

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedBySize));
        OnPropertyChanged(nameof(IsSortedByModified));
        OnPropertyChanged(nameof(IsSortedByKind));

        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
        OnPropertyChanged(nameof(KindSortGlyph));
    }

    // ---- sorting, reachable from the menu as well as the headers ----------

    public bool IsSortedByName => Sort == SortField.Name;

    public bool IsSortedBySize => Sort == SortField.Size;

    public bool IsSortedByModified => Sort == SortField.Modified;

    public bool IsSortedByKind => Sort == SortField.Kind;

    /// <summary>Sorting by type was implemented from the start and had no way
    /// to be reached — there is no type column to click.</summary>
    [RelayCommand]
    private void SortByKind()
    {
        if (Sort == SortField.Kind) SortDescending = !SortDescending;
        else { Sort = SortField.Kind; SortDescending = false; }
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    private int Compare(FileEntry a, FileEntry b)
    {
        // The group is the PRIMARY key. Without this, grouping by size while
        // sorted by name interleaves the bands and every group holds one file.
        //
        // **Ahead of directories-first, which is why every band used to appear
        // twice.** Folders-first ran first, so the order was [every folder, by
        // band][every file, by band] — and the header is emitted wherever the
        // label changes, so "Today" came up once over the folders and again
        // over the files. Explorer and Dolphin both put a folder and a file
        // modified today under one "Today".
        //
        // Grouping by Size or Kind is unaffected: both give folders a band of
        // their own ("Folders"), so they still sort ahead of everything else by
        // the group key itself rather than by the tie-break below.
        if (GroupBy != GroupMode.None)
        {
            var group = Grouping.CompareGroups(a, b, GroupBy, _groupNow);

            // **Descending flips the bands as well as the rows inside them.**
            // It reversed the rows and left the band order alone, so a
            // descending listing read Today, Yesterday, This week downwards
            // while the files inside each ran the other way — two directions in
            // one list. Explorer turns both over together.
            if (group != 0) return SortDescending ? -group : group;
        }

        // Directories first, always — the convention every file manager follows
        // and users notice immediately when it's missing. Inside a band when
        // there is one, which is where it belongs.
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        // Span comparison rather than Extension.ToString(): sorting 200k entries
        // by kind would otherwise allocate a string per comparison, millions of
        // them for one sort.
        var result = Sort switch
        {
            SortField.Size     => a.Length.CompareTo(b.Length),
            SortField.Modified => a.LastWriteTime.CompareTo(b.LastWriteTime),
            SortField.Kind     => a.Extension.CompareTo(b.Extension, StringComparison.OrdinalIgnoreCase),
            _                  => 0,
        };

        // Natural order, so file2 comes before file10. Ordinal comparison is
        // right for bytes and wrong for names people chose — but it is now a
        // preference, because Dolphin makes it one and some people genuinely
        // want the alphabetical order their shell gives them.
        if (result == 0)
        {
            var general = Settings.AppSettings.Current.General;

            result = general.NaturalSorting
                ? NaturalOrder.Compare(a.Name, b.Name)
                : string.Compare(a.Name, b.Name, general.CaseSensitiveSorting
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase);
        }

        return SortDescending ? -result : result;
    }
}
