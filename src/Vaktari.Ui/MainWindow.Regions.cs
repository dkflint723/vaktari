using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Vaktari.Ui;

/// <summary>
/// Which part of the window has the keyboard, and how F6 moves between them.
///
/// The sidebar, the address bar and the listing are the three regions; this
/// works out which one the focus is in, puts it in another, and walks the
/// sidebar's own stops once it is there. Every caller is the keymap — nothing
/// else in the window asks any of it.
///
/// **The rules themselves are not here.** Which region follows which lives in
/// FocusCycle, and what counts as a stop lives in SidebarWalk, both under
/// Input/ with their own tests. What is here is the part that has to touch
/// real controls: finding what is focused, and focusing something else.
/// </summary>
public partial class MainWindow
{
    private bool SidebarShowing => _shell?.Sidebar.IsPanelVisible == true;

    /// <summary>
    /// Which region the keyboard is in.
    ///
    /// The sidebar is asked FIRST and the listing last, because the sidebar
    /// contains a list of its own — testing for a ListBox before ruling the
    /// sidebar out would call a place row part of the listing.
    ///
    /// **Anything else is Elsewhere, not the listing.** A toolbar button, the
    /// tab strip, a crumb, or nothing at all at startup are all real states,
    /// and calling them the listing would make F6 move ON from them — which is
    /// exactly the rescue the old handler existed to provide.
    /// </summary>
    private Input.KeyboardRegion CurrentRegion()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused)
            return Input.KeyboardRegion.Elsewhere;

        if (this.FindControl<Border>("SidebarPanel") is { } sidebar
            && IsInside(focused, sidebar))
            return Input.KeyboardRegion.Sidebar;

        // **Asked of the pane, not found by name.** The address bar lives inside
        // a per-pane template and has no generated field, so FindControl on the
        // window does not reach it — and a region test that silently answers
        // "somewhere else" would make the sidebar unreachable by keyboard while
        // every other step still worked.
        //
        // A focused text box while the bar is open IS that bar: it closes the
        // moment the keyboard leaves it, so no other box can hold focus at the
        // same time.
        if (focused is TextBox && _shell?.ActiveTab?.IsPathEditing == true)
            return Input.KeyboardRegion.Location;

        return focused.FindAncestorOfType<ListBox>(includeSelf: true) is not null
            ? Input.KeyboardRegion.Listing
            : Input.KeyboardRegion.Elsewhere;
    }

    private void GoToRegion(Input.KeyboardRegion region)
    {
        switch (region)
        {
            case Input.KeyboardRegion.Location:
                _shell?.ActiveTab?.BeginEditPath();
                break;

            case Input.KeyboardRegion.Sidebar:
                // **Closed first, then queued BEHIND what closing sets off.**
                // The address bar's own lost-focus rule shuts it the moment
                // the sidebar takes the keyboard, and shutting it posts "put
                // the keyboard back in the listing" — a deliberate behaviour
                // for every other way the bar closes, and one that would land
                // after this and undo it. Same priority, posted second, so it
                // runs second: the listing is focused and then the sidebar row
                // takes it, which is the order asked for.
                _shell?.ActiveTab?.RevertPathText();

                Dispatcher.UIThread.Post(
                    () => FirstSidebarRow()?.Focus(NavigationMethod.Directional),
                    DispatcherPriority.Background);
                break;

            default:
                // Closed first, or the box's own lost-focus command fires as
                // the listing takes the keyboard and lands on whatever is
                // active by then.
                if (_shell?.ActiveTab is { IsPathEditing: true } editing)
                    editing.RevertPathText();

                ActiveListing()?.Focus();
                break;
        }
    }

    /// <summary>
    /// Where F6 puts the keyboard when it reaches the sidebar.
    ///
    /// **A section heading is a Button.** Folding made every heading a
    /// ToggleButton, which derives from Button, so the first visible one in
    /// the panel became PLACES rather than Home — and F6 landed on a control
    /// whose Space bar folds the list you were trying to reach. The rule is
    /// still "the first row", and a heading is not a row.
    ///
    /// Written with a body rather than as an expression so the rule can be
    /// pinned: RepoSource.Body ends a method at a closing brace at class
    /// indentation, and an expression-bodied member has none — it would have
    /// returned this declaration plus the whole of the next method.
    /// </summary>
    private Control? FirstSidebarRow()
    {
        if (this.FindControl<Border>("SidebarPanel") is not { } sidebar) return null;

        return sidebar.GetVisualDescendants().OfType<Button>()
                      .FirstOrDefault(b => b.IsVisible
                                           && b is not Avalonia.Controls.Primitives.ToggleButton);
    }

    /// <summary>
    /// Everything the arrow keys stop on in the sidebar, top to bottom.
    ///
    /// **RepeatButton and ToggleButton both derive from Button, and the panel's
    /// content sits in a ScrollViewer.** A scrollbar's PART_LineUpButton is a
    /// Button, so an unfiltered walk would let End put the keyboard on a scroll
    /// arrow — the same refusal ListForEmptySpace and TabStripEmptySpaceAt
    /// already make, for the same reason. Derived rather than trusted: whether
    /// the theme happens to mark those arrows unfocusable is a template detail,
    /// and this rule must not depend on one.
    ///
    /// A section HEADING is a stop, unlike F6's landing. It is the only way to
    /// unfold a section from the keyboard, and a folded section whose heading
    /// cannot be reached is a section the keyboard can never open again.
    /// </summary>
    private List<Control> SidebarStops()
    {
        if (this.FindControl<Border>("SidebarPanel") is not { } sidebar) return [];

        return [.. sidebar.GetVisualDescendants()
                          .OfType<Button>()
                          .Where(b => b is not RepeatButton)
                          .Where(b => b.Focusable && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)];
    }

    /// <summary>
    /// Moves the keyboard through the sidebar.
    ///
    /// Focused as a DIRECTIONAL move rather than a plain one, so the row draws
    /// its focus ring: Avalonia sets :focus-visible for keyboard navigation and
    /// leaves it off for a click, and a keyboard walk with no visible cursor is
    /// a walk you cannot follow.
    /// </summary>
    private void MoveInSidebar(Input.SidebarStep step)
    {
        var stops = SidebarStops();

        var from = FocusManager?.GetFocusedElement() is Visual focused
            ? stops.FindIndex(s => ReferenceEquals(s, focused))
            : -1;

        var landing = Input.SidebarWalk.Landing(stops.Count, from, step);

        if (landing < 0) return;

        stops[landing].Focus(NavigationMethod.Directional);
    }
}
