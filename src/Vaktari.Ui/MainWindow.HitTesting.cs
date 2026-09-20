using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// What is under the pointer, asked one way.
///
/// Every one of these walks UP the visual tree from whatever was physically
/// hit — a glyph, a TextBlock, the padding inside a row — because the thing an
/// event reports is almost never the thing a gesture means. Written with
/// <c>GetVisualParent</c> rather than an items-control API whose shape varies
/// between Avalonia versions.
///
/// **They are together because three different gestures ask the same
/// questions.** The press, the drop and the rubber band each need to know which
/// pane, which row, which place, which crumb — and when two of them worked it
/// out separately they disagreed: drag-over and drop mapped the bin's row
/// differently, so the cursor said no-drop and the branch in OnDrop that
/// handled it was unreachable code that read like a working feature. One
/// answer, read by everyone, is the arrangement in which that cannot happen
/// again.
///
/// The <c>*Class</c> constants live here too. They are the names the markup
/// puts on a control and the only thing these walks have to match on, so a
/// rename that changes one and not the other is a hit test that silently stops
/// finding anything.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The list a press landed in, but ONLY if it landed on empty space rather
    /// than on a row. Null for a press on a row, or outside any list.
    ///
    /// The upward walk is the same one `FocusListIfEmptySpace` needed, and for
    /// the same reason: a press on templated content has no logical path back to
    /// the list, so the visual tree is the only route.
    /// </summary>
    internal static ListBox? ListForEmptySpace(object? source)
    {
        ListBoxItem? row = null;

        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            // Content, not background: the name and the icon.
            // Grabbing one of these means "take this file", so a band must not
            // start here or dragging a file out would become impossible.
            if (row is null && visual is TextBlock or Image or Avalonia.Controls.Shapes.Path)
                return null;

            // **The scrollbar lives INSIDE the list**, so the walk used to
            // reach the ListBox from it and call it empty space: pressing the
            // scrollbar cleared the selection, and dragging the thumb drew a
            // rubber band down the side of the listing while the view scrolled
            // under it. Scrolling is not a selection gesture in any file
            // manager.
            if (visual is Avalonia.Controls.Primitives.ScrollBar
                       or Avalonia.Controls.Primitives.Thumb
                       or RepeatButton)
                return null;

            // **And the group heading lives inside a ROW**, so the walk reached
            // the list from it and called it empty space. Measured on a real
            // window with two files already picked out: a press in the middle
            // of the "MD (1)" heading arrived with e.Source =
            // Avalonia.Controls.Presenters.ContentPresenter — so the content
            // refusal above never saw it — this walk answered with the ListBox,
            // the selection went from "a.txt,b.txt" to empty ON THE PRESS, and
            // _bandList came back armed.
            //
            // Which is worse than losing the selection for an instant, and that
            // was measured too: press, move 40px, release, and the selection
            // ended EMPTY — the pointer had left the button, so the click never
            // completed and the band the heading names was never taken. The
            // same three steps with this refusal in place leave "a.txt,b.txt"
            // untouched throughout.
            //
            // Refused here rather than in the two handlers, because one refusal
            // covers both the clearing and the arming: they read the same
            // answer. This is the rule the box and the triangle already keep —
            // a widget drawn inside a row is not the row's background.
            if (visual is Control head && head.Classes.Contains(GroupHeadingClass))
                return null;

            if (visual is ListBoxItem hit) { row = hit; continue; }

            if (visual is not ListBox found) continue;

            // Not every list can hold a multiple selection — the column strip is
            // SelectionMode="None", and a band there would draw a rectangle that
            // selects nothing.
            if (!found.SelectionMode.HasFlag(SelectionMode.Multiple)) return null;

            // Nothing under the pointer but the list itself: always a band.
            if (row is null) return found;

            // **A row that is already selected drags from anywhere on it.**
            // Building a selection and then reaching for it destroyed the
            // selection instead: the gaps around the Size and Date text are
            // row background, so pressing one started a band and cleared
            // everything — and those gaps are most of the width of both
            // columns, because the text is short. Explorer drags the whole row.
            //
            // Only when selected, because that is what makes the two readings
            // of the same pixel unambiguous: on something picked out it means
            // "take these", and on something not it means "start again here".
            if (row.IsSelected) return null;

            // Otherwise a band, but only where the row spans the list, because
            // then there is no empty space beside it and the background is the
            // only place left to start one. A tile leaves gaps of its own, and
            // stealing its background would make dragging a file out of the
            // grid needlessly fiddly.
            return row.Bounds.Width >= found.Bounds.Width * 0.9 ? found : null;
        }

        return null;
    }

    /// <summary>
    /// The class the three row templates put on a selection box, and the only
    /// thing tying the markup to the handler that gives it meaning.
    /// </summary>
    internal const string SelectionBoxClass = "pick";

    /// <summary>
    /// The class the three row templates put on the box a name is typed in.
    /// The style that sizes it to a row is keyed off this, and so is every test
    /// that has to find an editor which exists only inside a DataTemplate.
    /// </summary>
    internal const string RenameBoxClass = "rename";

    /// <summary>
    /// The class each listing wears while one click opens. Bound rather than
    /// set, so it follows the preference, the desktop and the path; the three
    /// styles that draw the affordance — a hand over the row, an underline
    /// under the name being pointed at, and the I-beam the open rename box
    /// keeps — are keyed off it.
    /// </summary>
    internal const string SingleClickClass = "singleclick";

    /// <summary>
    /// The class the three row templates put on the cell that draws the name,
    /// which is the one thing in a row that can be underlined the way a target
    /// you are about to open is underlined everywhere else.
    /// </summary>
    internal const string RowNameClass = "filename";

    /// <summary>
    /// The selection box a press landed on, or null for a press anywhere else.
    ///
    /// **The box cannot be a CheckBox, so nothing toggles unless this is
    /// asked.** A press inside a row is claimed on the window's tunnel, which
    /// runs before the ListBox does — and that is not an optimisation, it is
    /// the only place the press can be taken: by the time the ListBox has seen
    /// it, a five-file selection is already one file, which is measured and
    /// exactly what the boxes exist to avoid. Handling it there also stops a
    /// CheckBox's own class handler from ever running, which is why the box in
    /// the markup is a drawn Border rather than a control.
    ///
    /// Stops at the ListBoxItem, so the walk ends inside the row it started in
    /// and cannot wander up into some other list's box.
    /// </summary>
    internal static Control? SelectionBoxAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control box && box.Classes.Contains(SelectionBoxClass)) return box;
        }

        return null;
    }

    /// <summary>
    /// The class the details row template puts on its expand triangle, and the
    /// only thing tying that markup to the handler that gives it meaning.
    /// </summary>
    internal const string ExpanderClass = "twist";

    /// <summary>
    /// The heading drawn over the first row of a group. Marked so
    /// <see cref="EntryAt"/> can tell it from the row it is drawn inside — see
    /// there for why that difference matters.
    /// </summary>
    internal const string GroupHeadingClass = "groupheading";

    /// <summary>
    /// The expand triangle a press landed on, or null for a press anywhere
    /// else.
    ///
    /// Same walk and the same reason as <see cref="SelectionBoxAt"/>: the press
    /// has to be claimed on the window's tunnel, before the ListBox reads it as
    /// a click on the row and collapses a multiple selection down to the one
    /// under the pointer. Stops at the ListBoxItem so the walk ends inside the
    /// row it started in.
    ///
    /// That stop has NO KILLING MUTATION, and it was looked for: turning it
    /// into a stop at the ListBox left the whole Vaktari.Ui.Tests project
    /// green, because nothing above a row in this window carries the class and
    /// the walk therefore runs out either way. It stays because the pane it
    /// would otherwise walk out of is the one whose triangle the press belongs
    /// to — the same bound SelectionBoxAt draws.
    /// </summary>
    internal static Control? ExpanderAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control cell && cell.Classes.Contains(ExpanderClass)) return cell;
        }

        return null;
    }

    /// <summary>The list a press landed in, row or background alike.</summary>
    private static ListBox? ListingAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBox list) return list;
        }

        return null;
    }

    /// <summary>Walks up from whatever was hit to the pane that owns it.</summary>
    private static PaneViewModel? PaneAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneViewModel pane }) return pane;
        }

        return null;
    }

    /// <summary>
    /// The tab under the pointer, or null if the pointer is not on the strip.
    ///
    /// **Not PaneAt.** A tab and a listing both carry a PaneViewModel as their
    /// data context — that is the whole point of the tab strip — so telling
    /// them apart has to be done by container, or a middle-click in the listing
    /// would close the tab it was aimed into.
    /// </summary>
    private static PaneViewModel? TabAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Primitives.TabStripItem { DataContext: PaneViewModel pane })
                return pane;
        }

        return null;
    }

    /// <summary>
    /// The group whose tab strip a gesture landed on, and only where the strip
    /// is BLANK — not on a tab, the "+", a chevron or the scrollbar.
    ///
    /// **The empty half of the strip answered nothing.** Explorer, Dolphin and
    /// every browser open a tab when it is double-clicked, and here the gesture
    /// fell through to the row walk, found no file and stopped — with the "+"
    /// itself scrolled out of reach behind a dozen tabs, which is exactly when
    /// the blank space is aimed at.
    ///
    /// Keyed on the strip's own ScrollViewer rather than the tab bar behind it,
    /// so the layout buttons docked to its right — and the gaps between them —
    /// keep meaning nothing. Button covers the "+", a tab's ✕ and both overflow
    /// chevrons in one test, because RepeatButton and ToggleButton both derive
    /// from it; the scrollbar and its thumb are refused for the reason
    /// <see cref="ListForEmptySpace"/> refuses them, which is that scrolling is
    /// not an opening gesture.
    /// </summary>
    private static PaneGroupViewModel? TabStripEmptySpaceAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Avalonia.Controls.Primitives.TabStripItem
                       or Button
                       or Avalonia.Controls.Primitives.ScrollBar
                       or Avalonia.Controls.Primitives.Thumb)
                return null;

            if (visual is ScrollViewer { DataContext: PaneGroupViewModel group } strip
                && strip.Classes.Contains("tab-space"))
                return group;
        }

        return null;
    }

    /// <summary>Walks up from whatever was hit to the group that owns it.</summary>
    private static PaneGroupViewModel? GroupAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PaneGroupViewModel group }) return group;
        }

        return null;
    }

    /// <summary>
    /// Which pane a navigation button moves, for a press that landed anywhere.
    ///
    /// **The side buttons navigated a tab nobody could see.** A tab header
    /// carries its own pane as its data context — that is how the strip is
    /// bound, and it is what lets a middle click close the tab under the
    /// pointer — so walking up from the press answered with the tab that was
    /// pointed AT rather than the listing on screen. Pressing back while aiming
    /// at the third tab's label rewound the third tab: the visible listing did
    /// not move, nothing said anything, and the only trace was a title quietly
    /// changing on a folder that was not open. Every browser drives the page
    /// you are looking at, whichever piece of chrome the pointer is over.
    ///
    /// The strip belongs to its group, so the group's own active tab is the
    /// answer — in a split, pointing at one side's tabs still navigates that
    /// side, which is the pane-under-the-pointer rule the rest of this handler
    /// follows rather than an exception to it. It also fixes the strip's
    /// background and its + button, which reached no pane at all and fell
    /// through to the OTHER side's active tab.
    /// </summary>
    private static PaneViewModel? NavigationTargetAt(object? source)
    {
        var pane = TabAt(source) is null ? PaneAt(source) : null;

        return pane ?? GroupAt(source)?.ActiveTab;
    }

    /// <summary>
    /// The sidebar place under the pointer, if a drop should go into it.
    ///
    /// Explorer takes a drop on the tree and on Quick access, and dragging a
    /// file onto Downloads or a drive is how a good deal of filing gets done.
    /// The sidebar accepted nothing, so the drag died over it with no cursor
    /// and no explanation.
    ///
    /// Only where the place is a real folder: a share that is not mounted has
    /// nowhere to put anything, and its own row already says so.
    ///
    /// This PC is not one of them, though it is drawn inside Home's row and
    /// carries Home's place — see <see cref="ComputerRowAt"/>. Without that
    /// line, a folder released on This PC went into the home folder.
    /// </summary>
    private static string? PlaceAt(object? source)
    {
        if (ComputerRowAt(source)) return null;

        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel { IsAvailable: true } place }
                && place.Path.Length > 0
                && !VirtualPaths.IsVirtual(place.Path))
                return place.Path;
        }

        return null;
    }

    /// <summary>
    /// Whether the pointer is over the bin's row.
    ///
    /// Separate from <see cref="PlaceAt"/>, which deliberately refuses a
    /// virtual path because those are not folders anything can be copied into.
    /// The bin is the one virtual place that IS a destination — for exactly one
    /// verb.
    /// </summary>
    private static bool TrashRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel place }
                && PathRules.Same(place.Path, VirtualPaths.Trash))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the pointer is on the sidebar's own GROUND — the blank strip
    /// under the sections, the gaps between them, a section heading — rather
    /// than on any of its rows.
    ///
    /// **Dropping a folder there was refused.** AllowDrop was on the place row
    /// and nowhere else, so the drag died over everything else in the panel;
    /// both references pin a folder dropped on the navigation pane, and here it
    /// was the one way of adding a place that did not exist.
    ///
    /// **Every row keeps what it does, including refusing.** The first draft of
    /// this answered true for anything in the panel that was not a place row.
    /// Measured on the real templated tree, that made the panel's ground of
    /// Scan, Share, "Connect to a server…" and both Recent rows, and the
    /// remote-mount, discovered-server and share rows are the same shape — so a
    /// folder released on a remote mount, a row a pointer is aimed at BECAUSE
    /// it looks like somewhere a folder goes, would have been pinned rather
    /// than refused. A refusal turning into a different action is worse than
    /// the refusal; those rows keep theirs until they are given one of their
    /// own.
    ///
    /// The two lines below are the whole rule. Every row and every action in
    /// this panel is a <see cref="Button"/> — a place row, This PC, a remote
    /// mount, a discovered server, Scan, Share, Connect, both Recent rows —
    /// while the ground is panels and text; a section heading is the one button
    /// that is part of the ground, and it says so with its style class. The
    /// second line is for the rows that are NOT buttons: a served share and a
    /// shared link are Borders in the markup, and would fall through the first.
    /// </summary>
    private static bool SidebarAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is not Control control) continue;

            if (control is Button && !control.Classes.Contains("section")) return false;

            if (control.DataContext is PlaceItemViewModel or RemoteMount or DiscoveredService
                or Core.Sharing.ShareSession or Core.Sharing.DriveLink) return false;

            if (control.Name == "SidebarPanel") return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the pointer is on the This PC row.
    ///
    /// **Asked over the whole chain rather than as a step inside
    /// <see cref="PlaceAt"/>**, and that is forced: DataContext inherits, so
    /// every control inside the Home row's template — This PC's own label and
    /// icon included — answers `DataContext is PlaceItemViewModel` with Home. A
    /// walk that stopped at the first such control would decide "this is the
    /// Home row" one or two levels below the name and never reach it. Measured:
    /// with the panel armed and without this, a folder released on This PC was
    /// copied into the home folder.
    ///
    /// The row is drawn there because a DataTemplate cannot ask where it sits,
    /// and only the first place row knows it leads the sidebar.
    /// </summary>
    private static bool ComputerRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { Name: "ComputerRow" }) return true;
        }

        return false;
    }

    /// <summary>
    /// Everything a drag needs to know about what is under the pointer.
    ///
    /// **Drag-over and drop used to work this out separately, and disagreed.**
    /// OnDrop had mapped the bin's row to Trash; OnDragOver never considered
    /// it, so the cursor showed no-drop and the toolkit — which delivers a drop
    /// only where the drag-over said yes — never delivered one. The branch in
    /// OnDrop was unreachable code that read like a working feature. One
    /// answer, read by both handlers, is the only arrangement in which they
    /// cannot drift apart again.
    /// </summary>
    private readonly record struct DropTarget(
        bool IsBin, bool IsSidebar, string? Place, string? Crumb, string? Folder, PaneViewModel? Pane)
    {
        /// <summary>Somewhere a drop could land. False refuses the drag.</summary>
        public bool Exists =>
            IsBin || IsSidebar || Place is not null || Crumb is not null || Pane is not null;

        /// <summary>
        /// The folder a drop goes into. Empty for the bin, which is a verb
        /// rather than a place to put things.
        ///
        /// A crumb outranks the pane below it and is outranked by a sidebar
        /// place, which is the order the pointer is actually over them in.
        /// </summary>
        public string Destination => Explicit ?? Pane?.CurrentPath ?? "";

        /// <summary>
        /// A folder the pointer is over in its own right — a place, a crumb or
        /// a folder row — as opposed to falling back to the folder being
        /// listed. The right-button drop menu needs the difference: it offers
        /// to put things INTO what you pointed at, and pointing at nothing in
        /// particular is not the same as pointing at the current folder.
        /// </summary>
        public string? Explicit => Place ?? Crumb ?? Folder;
    }

    private static DropTarget TargetAt(object? source) => new(
        TrashRowAt(source), SidebarAt(source), PlaceAt(source), CrumbAt(source),
        FolderRowAt(source), PaneAt(source));

    /// <summary>
    /// The breadcrumb segment under the pointer.
    ///
    /// **Dragging onto an ancestor did nothing**, though it is the shortest way
    /// to move something up two levels and both Explorer and Dolphin take it.
    /// The crumbs sit above the listing, so a drag over them found the pane and
    /// offered the pane's own folder — the drop went where the file already
    /// was, which is a no-op that looks like a bug.
    /// </summary>
    private static string? CrumbAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PathSegment crumb }
                && crumb.FullPath.Length > 0
                && !VirtualPaths.IsVirtual(crumb.FullPath))
                return crumb.FullPath;
        }

        return null;
    }

    /// <summary>
    /// The sidebar row under the pointer, whatever it names.
    ///
    /// **Not <see cref="PlaceAt"/>, which answers a DROP.** That one refuses a
    /// virtual path, because the bin and the two recent listings are not
    /// folders anything can be copied into, and it refuses a share that is not
    /// mounted. Opening is the other question: the row's own menu offers "Open
    /// in new tab" on every row it draws, and a gesture that answers fewer rows
    /// than the menu beside it reads as broken rather than as careful.
    /// </summary>
    private static string? PlaceRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PlaceItemViewModel { Path.Length: > 0 } place })
                return place.Path;
        }

        return null;
    }

    /// <summary>
    /// The crumb under the pointer, virtual ones included — This PC is somewhere
    /// you can be even though it is nowhere a file can land, which is the only
    /// reason <see cref="CrumbAt"/> refuses it.
    /// </summary>
    private static string? CrumbRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: PathSegment { FullPath.Length: > 0 } crumb })
                return crumb.FullPath;
        }

        return null;
    }

    /// <summary>
    /// The folder a press asks for in a NEW tab, or null when it asks for
    /// nothing of the kind.
    ///
    /// **A sidebar place and a breadcrumb answered neither gesture.** Middle
    /// click reached only the tab strip and a folder row, and a Button ignores
    /// a middle press altogether — so middle-clicking Documents in the sidebar,
    /// or an ancestor in the path bar, did nothing at all, while the row's own
    /// right-click menu offered "Open in new tab" and the F1 sheet advertised
    /// the middle button. Both references open a tab from both.
    ///
    /// Ctrl+click is offered on the sidebar and the crumbs only. In the listing
    /// that gesture already means "add this row to the selection", and taking
    /// it would break the most-used modifier in the application to add a second
    /// way of doing something the middle button already does.
    ///
    /// Place, then crumb, then folder row — the same order
    /// <c>DropTarget.Explicit</c> reads them in, and the order the pointer is
    /// physically over them in.
    /// </summary>
    private static string? NewTabTarget(
        object? source, PointerUpdateKind kind, KeyModifiers modifiers)
    {
        var middle = kind is PointerUpdateKind.MiddleButtonPressed;

        // Ctrl+middle is the pane-scale reset and keeps its own meaning. The
        // handler returns on it long before this; saying so here is what lets
        // the rule be read and tested on its own.
        if (middle && modifiers.HasFlag(KeyModifiers.Control)) return null;

        var ctrlLeft = kind is PointerUpdateKind.LeftButtonPressed
                       && modifiers == KeyModifiers.Control;

        if (!middle && !ctrlLeft) return null;

        if (PlaceRowAt(source) is { } place) return place;
        if (CrumbRowAt(source) is { } crumb) return crumb;

        // The listing keeps Ctrl+click for extending a selection.
        return middle ? FolderRowAt(source) : null;
    }

    /// <summary>The folder row under the pointer, if the drop should go into it
    /// rather than into the directory being listed.</summary>
    private static string? FolderRowAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: FileEntry { IsDirectory: true } entry })
                return entry.FullPath;
        }

        return null;
    }

    private static bool IsInside(Visual child, Visual parent)
    {
        for (var visual = child; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, parent)) return true;

        return false;
    }

    /// <summary>
    /// The listing the user is actually looking at: visible, showing the active
    /// tab, and able to hold more than one selection.
    ///
    /// Three layout lists exist per pane and all stay alive when hidden, so
    /// identity alone is not enough — `IsVisible` is what distinguishes them,
    /// and it is bound to the view mode.
    /// </summary>
    private ListBox? ActiveListing()
    {
        foreach (var list in Lists(this))
            if (list.IsVisible
                && ReferenceEquals(list.DataContext, _shell.ActiveTab)
                && list.SelectionMode.HasFlag(SelectionMode.Multiple))
                return list;

        return null;
    }

    private static IEnumerable<ListBox> Lists(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ListBox list) yield return list;

            foreach (var nested in Lists(child)) yield return nested;
        }
    }

    /// <summary>
    /// Every ListBoxItem beneath a control.
    ///
    /// Written with `GetVisualChildren` — the sibling of the `GetVisualParent`
    /// this file already relies on — rather than an items-control API whose shape
    /// varies between Avalonia versions.
    /// </summary>
    private static IEnumerable<ListBoxItem> Rows(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ListBoxItem row) yield return row;

            foreach (var nested in Rows(child)) yield return nested;
        }
    }

    /// <summary>The listing under the pointer, whatever part of a row it is
    /// over.</summary>
    private static ListBox? ListAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBox list) return list;
        }

        return null;
    }

    private static ScrollViewer? Scroller(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ScrollViewer found) return found;

            if (Scroller(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>
    /// The row's entry, found by walking UP from whatever was physically under
    /// the pointer.
    ///
    /// `e.Source` is the innermost visual, and the row template is several
    /// controls deep — a click on the filename lands on an `AccessText` whose
    /// DataContext is a `string`, not the `FileEntry`. Testing the source
    /// directly therefore only worked when the pointer happened to hit a
    /// control that carried the entry, which is why opening a folder could take
    /// four clicks: you were hunting for the right pixel. Double-click made it
    /// worse because it needs two qualifying hits in a row, not one.
    ///
    /// The same upward walk already exists in OnPointerPressedAnywhere for
    /// PaneGroupViewModel; this is that pattern, not a new idea.
    /// </summary>
    private static FileEntry? EntryAt(object? source)
    {
        // VISUAL tree, not Control.Parent.
        //
        // Parent is the LOGICAL parent, and a control generated inside a
        // template has no logical path back to the row that owns it — the
        // diagnostic showed `source=AccessText entry=NONE` even after the walk
        // was added, because AccessText lives inside the template and its
        // logical chain simply ends. The visual tree always connects, which is
        // why hit-testing questions belong there.

        // **A group heading is drawn INSIDE the row it stands over, and every
        // control in it inherits that row's FileEntry as its DataContext** — so
        // the walk below answered with the row for anything that landed on the
        // heading, and each caller took the one for the other. Measured on this
        // walk: a Button with the heading's class and a FileEntry DataContext
        // came back as that entry, exactly as a cell of the row does.
        //
        // Three call sites, all reachable with a single press: the left drag
        // arm is `EntryAt(e.Source) is not null`, so a twitch while pressing a
        // heading armed a drag of the row underneath it and a drop moved a real
        // file; and OnTapped and OnDoubleTapped open whatever this hands them,
        // so clicking a heading opened that row — on the first click, in
        // single-click mode.
        //
        // A separate walk rather than a test inside the loop below, and that is
        // not tidiness: the press lands on the presenter INSIDE the heading,
        // which carries the inherited FileEntry and not the class, so a check
        // in the loop would have answered with the row before ever reaching the
        // heading itself. Same shape and the same reason as ExpanderAt.
        if (GroupHeadingAt(source) is not null) return null;

        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: FileEntry entry }) return entry;
        }

        return null;
    }

    /// <summary>
    /// The group heading a press or a tap landed inside, or null for anything
    /// else.
    ///
    /// Stops at the ListBoxItem for the reason <see cref="ExpanderAt"/> gives:
    /// the walk ends inside the row it started in. That stop has NO KILLING
    /// MUTATION, exactly as ExpanderAt's does and for the same reason — nothing
    /// above a row carries the class, so the walk runs out either way — and it
    /// is here because a press on any other part of any row would otherwise
    /// climb to the window before answering null.
    /// </summary>
    internal static Control? GroupHeadingAt(object? source)
    {
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem) return null;

            if (visual is Control head && head.Classes.Contains(GroupHeadingClass)) return head;
        }

        return null;
    }

    /// <summary>
    /// True when the press landed inside the box a name is being typed in.
    ///
    /// **Two clicks in the editor opened the file.** <see cref="EntryAt"/>
    /// walks up to the first FileEntry DataContext and the rename box carries
    /// the row's own, so placing a caret and then double-clicking to select a
    /// word was an activation gesture: measured on this window, two clicks in
    /// the box renaming a folder called "adir" navigated into adir and left the
    /// rename pointing at what had become the current folder. With the
    /// single-click preference on, one click to place the caret was enough.
    /// </summary>
    private static bool InRenameBox(object? source)
    {
        // Visual tree, for the reason EntryAt gives: the press lands on the
        // TextPresenter inside the box's own template, which has no logical
        // path back out of it.
        for (var visual = source as Visual; visual is not null;
             visual = visual.GetVisualParent())
        {
            if (visual is TextBox box && box.Classes.Contains(RenameBoxClass)) return true;
        }

        return false;
    }
}
