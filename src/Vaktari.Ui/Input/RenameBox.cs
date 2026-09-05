using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Input;

/// <summary>
/// Puts the keyboard in the edit box that has just appeared on a row, and
/// selects the part of the name that gets typed over.
///
/// **A row's edit box has no field to call Focus() on.** It lives inside the
/// three listing DataTemplates, so there is no generated member for the code
/// behind to reach — the same reason
/// <see cref="Vaktari.Ui.FocusBehavior"/> exists for the per-pane controls.
/// FocusBehavior itself is not enough here: it selects the WHOLE text, and a
/// rename that selects the extension is the bug
/// <see cref="RenameSelection"/> was written for.
/// </summary>
public static class RenameBox
{
    /// <summary>
    /// True on the one row being renamed. Bound to the same comparison the box
    /// is made visible by, so becoming visible and taking the keyboard are one
    /// event rather than two that can disagree.
    /// </summary>
    public static readonly AttachedProperty<bool> EditingProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Editing", typeof(RenameBox));

    public static void SetEditing(TextBox box, bool value) => box.SetValue(EditingProperty, value);

    public static bool GetEditing(TextBox box) => box.GetValue(EditingProperty);

    static RenameBox()
    {
        EditingProperty.Changed.AddClassHandler<TextBox>((box, args) =>
        {
            if (args.NewValue is not true) return;

            // Posted, as FocusBehavior posts: the flag goes true while the row
            // is still being laid out, and Focus() on a control that is not yet
            // realized is a no-op that reports nothing.
            //
            // Input priority, which is what FocusBehavior.FocusWhen posts at
            // for the same job — and it is not decoration. Measured by moving
            // this one line to Background and running the rename and F6 tests
            // six times: `Tab_commits_the_name_and_opens_the_next_one` failed
            // twice, and four unmutated runs of the same set failed none. A
            // step closes one box and opens the next in a single pass, and at
            // Background this post lands behind the work that pass leaves
            // behind. The failure is intermittent, so this is a tendency the
            // suite can show rather than a line one test always pins.
            Dispatcher.UIThread.Post(() =>
            {
                // The listings virtualize, so a container can be recycled onto
                // a different row between the post and the run.
                //
                // A GUARD, and no mutation can redden it: a box whose Editing
                // is false is a box whose IsVisible is false — both are the
                // same comparison — and Avalonia refuses to focus an invisible
                // control. Measured: Focus() on a hidden rename box returns
                // false and leaves the keyboard on the listing. So this saves
                // nothing observable, and states the intent instead.
                if (!GetEditing(box)) return;

                box.Focus();

                // **Explorer selects the name and not the extension.** Read off
                // the box rather than the row: the box already holds the name
                // the rename started from, and the row's entry is a record the
                // next refresh replaces.
                box.SelectionStart = 0;
                box.SelectionEnd = RenameSelection.LengthFor(
                    box.Text, box.DataContext is FileEntry entry && entry.IsDirectory);
            }, DispatcherPriority.Input);
        });
    }
}
