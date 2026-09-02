using Vaktari.Core.Session;

namespace Vaktari.Ui.Input;

/// <summary>
/// Which way a column sorts the FIRST time it is clicked.
///
/// **Every heading started ascending, and two of them read backwards.**
/// Clicking "modified" brought the oldest file to the top — so the download
/// that had just finished, which is the reason anybody clicks that heading at
/// all, landed at the very bottom of the folder and took a second click to
/// find. Size did the same with the zero-byte files.
///
/// Explorer settles this per column rather than globally: its property schema
/// gives every date and every size a default sort direction of descending, and
/// Finder agrees, so "sort by modified" has meant newest-first everywhere
/// people learned it. Name and type stay ascending, because A to Z is what
/// those two mean.
///
/// Only about the first click. Clicking the same heading again still reverses
/// whatever is there, which is the other half of the same convention.
/// </summary>
public static class SortDefaults
{
    public static bool DescendingFirst(SortField field)
        => field is SortField.Size or SortField.Modified;
}
