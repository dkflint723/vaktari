using Avalonia.Controls;
using Avalonia.Input;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Dragging a details column wider or narrower.
///
/// The whole code-behind of one gesture: which column, from the Thumb's Tag,
/// and how far — and then, when the drag ends, commit. Both are reached only
/// from the markup, from the grips on four column headings, and neither is
/// called from any C# in the repository.
///
/// **A column width is not this window's layout, which is why it is not filed
/// with the things that are.** It is an application-wide preference that
/// outlives the window: the shell scales the dragged pixels by the pane's own
/// zoom, writes the result into the settings, and the commit at the end of the
/// drag is what gets it saved. Every pane in every window then draws at the
/// new width. The panel width beside it, by contrast, belongs to one window
/// for as long as one panel is open.
///
/// The pane's own zoom is why the shell does the arithmetic rather than this:
/// the grip reports pixels on screen, the stored width is kept at 100%, and in
/// a split at two zooms the other side's scale would move the column a
/// different distance from the pointer.
///
/// The save is wired in the constructor, on the shell's ColumnWidthsChanged,
/// and stays there with the rest of that block.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Widens or narrows a details column as the grip on its heading is
    /// dragged. The same plumbing as the details-panel handle and the sidebar
    /// one, both of which stayed in MainWindow.axaml.cs — which column, from
    /// the Thumb's Tag, and how far — with the arithmetic in the shell.
    ///
    /// **The pane's own zoom goes with it.** The grip reports pixels on
    /// screen and the width is kept at 100%, and it is THIS pane's scale the
    /// column under the pointer was drawn at: in a split at two zooms, the
    /// other side's would move the column a different distance from the
    /// pointer. From the DataContext rather than a name for the same reason
    /// the details-panel handle gives: in split view there are two of these,
    /// and a name would find one.
    /// </summary>
    private void OnColumnGripDragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Control { DataContext: PaneViewModel pane, Tag: string column })
            _shell.ResizeColumn(Enum.Parse<DetailsColumn>(column), e.Vector.X, pane.FontScale);
    }

    private void OnColumnGripDragCompleted(object? sender, VectorEventArgs e)
        => _shell.CommitColumnWidths();
}
