using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// Dragging files out of this window, and everything a drop into it goes
/// through: the payload, the ghost under the pointer, the answer given while
/// the pointer is still moving, and what actually happens when it is let go.
///
/// **Drag-over and drop have to agree, and this is the file where that is
/// checkable.** The toolkit delivers a drop only where the drag-over said yes,
/// so a branch in OnDrop with no matching branch in OnDragOver is unreachable
/// code that reads like a working feature — which is exactly what the bin's row
/// was until both were made to read one DropTarget. Keeping the two handlers,
/// the intent they compute and the effect they report in one place is what
/// stops them drifting apart again.
///
/// Not the tab strip and not the sidebar's pinned rows. Those are reordering
/// within the window — no payload, no toolkit drag, nothing leaves — and they
/// share only <c>_dragOrigin</c>, which belongs to the press that sets it
/// rather than to any of the three.
/// </summary>
public partial class MainWindow
{
    private PaneViewModel? _dragSource;
    private bool _dragging;

    /// <summary>
    /// What was selected at the moment the button went down.
    ///
    /// **Because pressing collapses the selection before the drag starts.**
    /// Select five files, press on one of them and drag: the press has already
    /// reduced the selection to that row by the time BeginDragAsync reads it,
    /// so four files were silently left behind. The right-button drag carried
    /// all five, because Avalonia treats a right press differently — which is
    /// what made it look like a listing quirk rather than a drag one.
    ///
    /// **Read on the way out as well as on the way in.** Feeding the payload
    /// from this fixed what the drag CARRIED and not what the window SHOWED:
    /// the collapsed selection stayed collapsed, so a drag of three that was
    /// cancelled with Escape, and one the bin refused before it started, both
    /// ended with one row picked out. BeginDragAsync's finally hands the list
    /// back to the pane, from inside the try that now holds the refusals too.
    /// </summary>
    private List<string>? _dragSelection;

    // The press that began the gesture, held until the move threshold is
    // crossed.
    //
    // This looks like retaining event args past their handler, and it is — but
    // DragDrop.DoDragDropAsync takes PointerPressedEventArgs specifically, not
    // the PointerEventArgs the move handler receives, so a drag cannot be
    // started from the move without it. Starting from the press instead would
    // mean no movement threshold, and every click on a row would begin a drag.
    // The alternative is worse than the constraint.
    private PointerPressedEventArgs? _dragTrigger;

    /// <summary>
    /// True while a drag that started inside Vaktari is in flight. Dragging within
    /// a file manager conventionally means move; dragging in from another
    /// application means copy. Ctrl and Shift override either way.
    /// </summary>
    private bool _internalDrag;

    /// <summary>
    /// True while a drag started by THIS APPLICATION is in flight, in whichever
    /// of its windows began it.
    ///
    /// **A drag between two Vaktari windows lost its label at the boundary.**
    /// <see cref="_internalDrag"/> above belongs to one window, and a window is
    /// not what "our own drag" means to the ghost: measured with two headless
    /// MainWindows, the first holding the drag, the second reported its ghost
    /// invisible with an empty label while the first had already put its own
    /// away on the leave — so for the whole crossing there was nothing on
    /// screen, which is the gesture this feature exists for. Vaktari opens
    /// windows on purpose: OpenNewWindow builds one per invocation and a
    /// restored session brings back several.
    ///
    /// Separate from <see cref="_internalDrag"/> rather than replacing it,
    /// because that field also decides what a DROP does — the move-versus-copy
    /// default and the right-drag menu — and whether a drop arriving from
    /// another window of this application should count as internal there is a
    /// different question, unmeasured, and not this one.
    ///
    /// Static, so a test that sets it must put it back; DragGhostTests does.
    /// </summary>
    private static bool _dragBegunInThisApplication;

    /// <summary>
    /// Scrolls the listing while a DRAG rests near its top or bottom edge.
    ///
    /// **A folder that was off-screen could not be dropped into.** The listing
    /// held still for the whole drag, so reaching anything below the fold meant
    /// abandoning the drag, scrolling, and starting again — and in a folder of
    /// any size that is most of it. Both references scroll at the edges.
    ///
    /// It borrows the rubber band's timer, which is safe because a band and a
    /// file drag cannot both be in progress: the band needs a left press on
    /// empty space, and by the time a drag is running that press has been spent
    /// on the drag.
    /// </summary>
    private void DragScroll(DragEventArgs e)
    {
        if (ListAt(e.Source) is not { } list)
        {
            StopDragScroll();
            return;
        }

        _bandList = list;
        AutoScroll(list, e.GetPosition(BandLayer));
    }

    private void StopDragScroll()
    {
        StopBandScroll();
        _bandList = null;
    }

    private async Task BeginDragAsync(PaneViewModel pane, PointerPressedEventArgs trigger)
    {
        // **Held so the snapshot can be put BACK, not only read.** Feeding the
        // payload from it was half the fix: the press had already collapsed the
        // selection to the one row under the pointer, and nothing restored the
        // rest when the drag was over. Measured with three rows selected and
        // the drag cancelled with Escape — pane.Selection held one entry once
        // the gesture had finished, while the payload had carried three, so
        // two rows were left deselected with nothing to say which.
        var restore = _dragSelection;

        // **Everything below is inside the try so the restore covers the
        // REFUSALS too.** The three guards that follow used to sit above it,
        // and the one that asks CanDragOut is the bin's: measured in a bin
        // listing of three, pressing one row collapsed the selection to it,
        // the drag was refused with "cannot drag out of the Recycle Bin — use
        // Restore", and two rows stayed silently deselected — finding 156
        // verbatim, in the one listing where the drag never starts. They set
        // nothing the finally would wrongly undo: its assignments are the
        // nulls and falses a fresh press writes anyway.
        try
        {
            // The snapshot taken when the button went down wins: by now the
            // press has collapsed a multi-selection to the one row under the
            // pointer.
            var paths = _dragSelection is { Count: > 0 } remembered
                ? remembered
                : pane.Selection.Count > 0
                    ? pane.Selection.Select(x => x.FullPath).ToList()
                    : pane.SelectedEntry is { } one ? [one.FullPath] : [];

            if (paths.Count == 0) return;

            // Asked before a payload is built, so the refusal reaches the status
            // bar instead of the drag reaching a target that cannot say why it
            // failed.
            if (!pane.CanDragOut()) return;

            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

            _dragging = true;
            _internalDrag = true;
            _dragBegunInThisApplication = true;
            _rightDragInFlight = _dragRight;

            // The release that ends this drag is a right-button release, and a
            // right-button release is how context menus open. Armed here,
            // consumed by the tunnelled ContextRequested handler.
            if (_dragRight) _suppressContextMenu = true;

            // DataFormat.File is what other applications actually read; Avalonia
            // serialises it to text/uri-list on X11, the same route the
            // clipboard takes.
            var data = new DataTransfer();

            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);

                if (item is not null) data.Add(DataTransferItem.CreateFile(item));
            }

            if (data.Items.Count == 0) return;

            // Not disposed — the drag system takes ownership.
            await DragDrop.DoDragDropAsync(
                trigger, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] drag failed: {ex.Message}");
        }
        finally
        {
            _dragging = false;
            _internalDrag = false;
            _dragBegunInThisApplication = false;
            _rightDragInFlight = false;
            _dragRight = false;
            _dragSource = null;
            _dragTrigger = null;
            _dragSelection = null;

            // In the finally rather than after DoDragDropAsync, because the
            // press collapsed the selection before any of these was known: a
            // bin listing that refuses to drag out, a folder whose files the
            // storage provider would not hand over, a payload that came out
            // empty, a platform that threw, and the ordinary drop or cancel.
            // Each of those is a return or a throw from inside the try above.
            // Two were measured with three rows selected and both ended with
            // one row picked out: the cancel, and the bin's refusal.
            if (restore is { Count: > 0 }) pane.ReselectPaths(restore);

            // The drop and the leave both take the ghost away, and neither is
            // guaranteed: a drag released over another application, or
            // abandoned with Escape, ends here and nowhere else. A label left
            // on the glass after the gesture is over is worse than no label.
            //
            // This window's own, and that is enough of them. A SECOND window of
            // ours is drawing a ghost only while the pointer is inside it, so
            // the leave or the drop it is about to be handed is the same route
            // it would use anyway — it is this window, which the pointer has
            // already left, that had nothing else to fall back on.
            HideDragGhost();
        }
    }

    /// <summary>
    /// Takes files a drop offers that are not on disk — dragged straight out of
    /// 7-Zip, or Explorer's own zip view. Null on a platform with no such
    /// notion, which is every one but Windows.
    /// </summary>
    private Vaktari.Core.FileSystem.IVirtualFileDrop? _virtualDrop;

    /// <summary>The platform's shortcut writer, or null where the idea does
    /// not exist — the gestures that use it simply do not offer it then.</summary>
    private Vaktari.Core.FileSystem.IShortcutMaker? _shortcuts;

    /// <summary>
    /// True while the drag in flight was started with the RIGHT button, which
    /// changes what the drop does: nothing, until a menu asks.
    /// </summary>
    private bool _rightDragInFlight;

    /// <summary>Armed at the press that may become a right-drag.</summary>
    private bool _dragRight;

    /// <summary>
    /// Eats the context menu the right-button RELEASE would otherwise open.
    /// A right-drag ends in a release like any right-click, and without this
    /// the source row popped its menu the moment the drop finished — two
    /// gestures answering one another.
    /// </summary>
    private bool _suppressContextMenu;

    /// <summary>
    /// Moves the label that says what the drag is carrying.
    ///
    /// **This APPLICATION's own drags, not this WINDOW's.**
    /// <see cref="DragDrop.DoDragDropAsync"/> takes a trigger, a payload and a
    /// set of effects and has no drag-image parameter, so a drag begun in
    /// Vaktari has nothing following the pointer but the cursor — which is the
    /// gap this fills. A drag arriving from another application brings whatever
    /// that application drew for it, and a second label under the first would
    /// be Vaktari narrating somebody else's gesture.
    ///
    /// Asked of <see cref="_dragBegunInThisApplication"/> and not of
    /// <see cref="_internalDrag"/>, which is per window: measured on the
    /// per-window field, a drag from one Vaktari window into a second lost its
    /// label at the boundary — the receiving window called it foreign and drew
    /// nothing while the source window had already put its own away.
    ///
    /// **Shown before it is measured.** A control that is not visible measures
    /// to an empty size — <c>Layoutable.MeasureCore</c> returns one without
    /// asking the content — so measuring first gave the first ghost of every
    /// drag a width of zero, and the flip at the right-hand edge then placed it
    /// under the pointer instead of clear of it.
    /// </summary>
    private void ShowDragGhost(DragEventArgs e)
    {
        var carried = _dragBegunInThisApplication
            ? Input.DroppedFileReader.Offered(e.DataTransfer)
            : [];

        var label = Input.DragGhost.Label(carried);

        if (label.Length == 0)
        {
            HideDragGhost();
            return;
        }

        DragGhostText.Text = label;
        DragGhostBox.IsVisible = true;

        // Out of band with the layout pass, which runs after this handler has
        // already placed the label — and invalidated first, because the text
        // change marks the TextBlock dirty and leaves the border it sits in
        // measured and valid. **Measuring a valid border hands back the width
        // of the label before this one**, so at the right-hand edge a drag that
        // changed what it said was flipped by the old width and drew clear of
        // nothing. See The_ghost_is_placed_against_the_label_it_draws_now.
        DragGhostBox.InvalidateMeasure();
        DragGhostBox.Measure(Size.Infinity);

        var spot = Input.DragGhost.Spot(
            e.GetPosition(BandLayer), DragGhostBox.DesiredSize, BandLayer.Bounds.Size);

        Canvas.SetLeft(DragGhostBox, spot.X);
        Canvas.SetTop(DragGhostBox, spot.Y);
    }

    private void HideDragGhost() => DragGhostBox.IsVisible = false;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        // Before every branch below, refusals included: what a drag is carrying
        // does not stop being carried over a target that will not take it, and
        // a label that blinked out over the wrong folder would read as the drag
        // itself having ended.
        ShowDragGhost(e);

        // The sidebar first: a place row has no pane above it, so asking for
        // one would refuse the drop before the place was ever considered.
        var spot = TargetAt(e.Source);

        // Before the refusal below: a tab strip is not a pane and not a place,
        // so a drag resting on it would be refused and never counted as a hover.
        HoverTab(TabAt(e.Source));

        if (!spot.Exists)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            StopDragScroll();
            return;
        }

        // **The bin is a verb, not a folder**, so it is answered here rather
        // than falling through to the destination rules below — which would ask
        // what it costs to copy into "vaktari:trash" and refuse.
        //
        // Move, because that is what dropping on the bin does to the original,
        // and it is the effect Explorer's own cursor shows over the Recycle Bin.
        if (spot.IsBin)
        {
            e.DragEffects = Input.DroppedFileReader.Offered(e.DataTransfer).Count > 0
                ? DragDropEffects.Move
                : DragDropEffects.None;

            HighlightDropTarget(null, place: VirtualPaths.Trash);
            StopDragScroll();
            return;
        }

        // **The sidebar's own ground pins**, and like the bin it is answered
        // before the destination rules, which name no folder for it: measured
        // by letting a drag over the blank strip fall through to them, the
        // cursor came back Copy with a destination of "" — so the toolkit would
        // deliver the drop and the copy path would be handed nowhere to put it.
        // What the cursor says here is PinPlan.Effect's rule instead, and the
        // Link it answers is what tells the two apart.
        if (spot.IsSidebar)
        {
            e.DragEffects =
                Input.PinnableDrop.For(Input.DroppedFileReader.Offered(e.DataTransfer)).Effect;

            HighlightDropTarget(null);
            StopDragScroll();
            return;
        }

        // Near an edge of the listing, keep it moving — checked on every
        // drag-over rather than only where the drop would be accepted, because
        // scrolling is how you REACH somewhere that would accept it.
        DragScroll(e);

        var place = spot.Place;
        var pane = spot.Pane;
        var destination = spot.Destination;

        // A virtual listing is a view, not a folder, so its background has
        // nowhere to put anything. The paste path refuses it too, but that
        // refusal arrives as a line of status text after the drop; the cursor
        // can say it beforehand, which is when it is still useful. Read from
        // `destination` rather than the pane, so a real folder ROW inside
        // Recent still takes a drop.
        if (VirtualPaths.IsVirtual(destination))
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        // Refuse a drop that would achieve nothing, so the cursor says so
        // before the click rather than a duplicate appearing after it.
        // **The effect first, from the raw paths, then what the drop means.**
        // Copying keeps a file dropped into its own folder — that is Explorer's
        // duplicate gesture — while moving discards it as a no-op, so the
        // filtering cannot be decided before the intent is.
        //
        // **Not Copy: everything that is not a Move.** The reader is told
        // copy-or-move and only a MOVE has a reason to strip a path already
        // living in the destination. Asking it `== Copy` put Link on the move
        // side, so Alt+drag onto the folder a file is already in — Explorer's
        // way of putting "X - Shortcut" beside the original — was filtered down
        // to nothing and answered None. Measured: OnDrop asks the same reader
        // `!move` and made the shortcut, so the drop handler was already right
        // and unreachable, because a real OLE drag obeys the cursor. See
        // Alt_onto_the_folder_the_file_already_lives_in_still_makes_a_shortcut.
        var offered = Input.DroppedFileReader.Offered(e.DataTransfer);
        var effect = EffectFor(e.KeyModifiers, offered, destination);

        var takeable = Input.DroppedFileReader
            .Read(e.DataTransfer, destination, effect != DragDropEffects.Move).Any;

        // Files that live inside an archive have no paths yet, so nothing above
        // sees them — but they can be had, and the cursor has to say so before
        // the button is released rather than after.
        if (!takeable && _virtualDrop?.Offers(e.DataTransfer) == true)
        {
            // Copy: there is no original to take away. A move out of an archive
            // is not a thing the archive would survive.
            e.DragEffects = DragDropEffects.Copy;
            HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place);
            return;
        }

        if (!takeable)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        e.DragEffects = effect;

        // A place is its own target; highlighting a pane for it would point at
        // the wrong half of the window. The row and the place are marked
        // whichever it is, so the ring is on the thing the files will go into.
        HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place);
    }

    /// <summary>
    /// Copy, move or link. See <see cref="Input.DragEffect"/> for the rule —
    /// which is Windows's, by volume, rather than the one this used to apply.
    ///
    /// **Alt was read nowhere on this path.** Only Control and Shift were
    /// taken off the modifiers, so Alt+drag reached the rule as an unmodified
    /// drag and was answered by volume — a move inside a drive, where Explorer
    /// would have left a shortcut and the original untouched.
    /// </summary>
    private Input.DragIntent IntentFor(
        KeyModifiers modifiers, IReadOnlyList<string> sources, string destination)
    {
        var intent = Input.DragEffect.For(
            modifiers.HasFlag(KeyModifiers.Control),
            modifiers.HasFlag(KeyModifiers.Shift),
            modifiers.HasFlag(KeyModifiers.Alt),
            _internalDrag, sources, destination);

        // A platform with no idea of a shortcut must not advertise one on the
        // cursor and then quietly copy.
        return intent == Input.DragIntent.Link && _shortcuts is null
            ? Input.DragIntent.Copy
            : intent;
    }

    private DragDropEffects EffectFor(
        KeyModifiers modifiers, IReadOnlyList<string> sources, string destination)
        => IntentFor(modifiers, sources, destination) switch
        {
            Input.DragIntent.Move => DragDropEffects.Move,
            Input.DragIntent.Link => DragDropEffects.Link,
            _ => DragDropEffects.Copy,
        };

    /// <summary>
    /// Writes the drop's virtual files somewhere real and moves them into
    /// place. False when there were none, or none could be had.
    ///
    /// **Moved, not copied.** What comes back was written to a temporary folder
    /// for this drop alone and has no original to preserve, so copying would
    /// leave a duplicate behind for nobody.
    ///
    /// **On the thread that received the drop, and that is not a detail.** This
    /// used to run on the thread pool, to keep a large archive from freezing the
    /// window mid-gesture, and it cost the feature: what a drag hands over is a
    /// COM object belonging to the apartment that received it, and reading it
    /// from anywhere else fails every file in the drop. Worse, the pool work
    /// began only after the drop handler had returned, by which time the source
    /// is entitled to have taken the object away — so the same drag that
    /// Offers had just accepted refused everything a moment later.
    ///
    /// That is what "nothing came out of that archive" was: not an empty
    /// archive, but every entry refused, silently, in the wrong apartment.
    /// The window is held for the length of the unpack instead, which is what
    /// the bounds in VirtualFileDrop are for.
    /// </summary>
    private bool TakeVirtual(IDataTransfer data, PaneViewModel pane, string destination)
    {
        if (_virtualDrop is not { } virtualDrop || !virtualDrop.Offers(data)) return false;

        pane.Status = "taking the files out of the archive…";

        IReadOnlyList<string> taken;

        try
        {
            taken = virtualDrop.Take(data);
        }
        catch (Exception ex)
        {
            pane.Status = $"could not take those out of the archive: {ex.Message}";
            return true;
        }

        if (taken.Count == 0)
        {
            pane.Status = "nothing came out of that archive";
            return true;
        }

        pane.PasteIntoFolder(destination, taken, move: true);

        return true;
    }

    /// <summary>
    /// Where rescued drops are staged. Under our own name inside the temporary
    /// directory, so a crash leaves something recognisable rather than litter,
    /// and so the operating system clears it eventually even if we do not.
    /// </summary>
    private static string DropStagingRoot()
        => Path.Combine(Path.GetTempPath(), "Vaktari", "drops");

    /// <summary>
    /// What the drag actually handed over, and whether it was still there when
    /// it did.
    ///
    /// **Written because a failure said "Could not find file" about a path the
    /// drop had just been given.** Dragging out of 7-Zip does not hand over the
    /// archive's contents: it extracts them to a temporary folder of its own
    /// and hands over paths into that. The copy those paths feed runs after the
    /// drop returns, so there are two quite different explanations for a file
    /// that is not there — 7-Zip cleaned up before we read it, or 7-Zip had not
    /// finished writing it when the drop completed — and the fix differs. This
    /// says which, at the one instant that separates them.
    ///
    /// Bounded: a drag of a large tree should not be turned into a long walk by
    /// the thing reporting on it.
    /// </summary>
    private static void ReportDroppedPaths(IReadOnlyList<string> paths)
    {
        Console.Error.WriteLine($"[vaktari] drop: {paths.Count} path(s) handed over");

        for (var i = 0; i < paths.Count && i < 8; i++)
        {
            var path = paths[i];

            if (File.Exists(path))
            {
                Console.Error.WriteLine($"[vaktari] drop:   file present · {path}");
                continue;
            }

            if (!Directory.Exists(path))
            {
                Console.Error.WriteLine($"[vaktari] drop:   ALREADY GONE · {path}");
                continue;
            }

            // A folder is the case that matters: the failure named a file
            // inside one, so the question is whether the contents had been
            // written yet, not whether the folder existed.
            var files = 0;
            var bytes = 0L;

            try
            {
                // Links not followed: a dropped folder holding a link to a huge
                // tree would otherwise be measured as that tree, and one
                // pointing at an ancestor would spin until the two-thousand cap
                // saved it by accident.
                foreach (var inside in Vaktari.Core.FileSystem.SafeWalk.Descend(path))
                {
                    if (inside.IsDirectory || inside.IsLink) continue;

                    files++;
                    try { bytes += new FileInfo(inside.Path).Length; } catch { }
                    if (files >= 2_000) break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[vaktari] drop:   folder unreadable · {path}");
                continue;
            }

            Console.Error.WriteLine(
                $"[vaktari] drop:   folder with {files} file(s), {bytes} bytes · {path}");
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        HideDragGhost();
        StopDragScroll();
        HoverTab(null);
    }

    /// <summary>
    /// Marks what a drop would land in: the pane, the folder row inside it, and
    /// the sidebar place.
    ///
    /// **Only the pane was ever marked, and the pane is the one thing that was
    /// never in doubt.** What a drag could not tell you is whether releasing
    /// puts the files into the folder under the pointer or into the folder
    /// being listed — different places, and finding out meant releasing and
    /// looking. All three are cleared together, because a stale ring on a row
    /// you have moved off is worse than none.
    /// </summary>
    private void HighlightDropTarget(PaneViewModel? pane, string? row = null, string? place = null)
    {
        foreach (var group in new[] { _shell.Left, _shell.Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs)
            {
                var here = ReferenceEquals(tab, pane);

                tab.IsDropTarget = here;
                tab.DropTargetPath = here ? row ?? "" : "";
            }
        }

        foreach (var group in _shell.Sidebar.Groups)
            foreach (var row2 in group.Places)
                row2.IsDropTarget = place is not null && PathRules.Same(row2.Path, place);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        HideDragGhost();
        StopDragScroll();
        HoverTab(null);

        // **The bin takes drops.** Its row is AllowDrop with a comment about
        // taking them "the way the tree and Quick access do in Explorer", and
        // nothing ever mapped one to IFileOperations.Trash — PlaceAt refuses a
        // virtual path, and the bin's path is the virtual vaktari:trash, so the
        // drop landed nowhere and looked like the row was simply dead.
        var spot = TargetAt(e.Source);

        if (spot.IsBin && _shell.ActiveTab is { } binPane)
        {
            var offered = Input.DroppedFileReader.Offered(e.DataTransfer);

            if (offered.Count > 0) binPane.TrashPaths(offered);
            return;
        }

        // **A folder dropped on the panel becomes a place.** Not awaited: the
        // pin writes a file, and a drag must not hold the UI thread while it
        // does. The shell reports what happened on the active tab's status
        // line, including the files it left alone.
        if (spot.IsSidebar)
        {
            _ = _shell.PinDroppedAsync(Input.DroppedFileReader.Offered(e.DataTransfer));
            return;
        }

        // A sidebar place and a breadcrumb are destinations in their own right,
        // and neither is guaranteed to have a pane above it to ask about — so
        // the active tab stands in as the pane that reports what happened.
        var pane = spot.Pane ?? (spot.Exists ? _shell.ActiveTab : null);

        if (pane is null) return;

        // Dropping onto a folder row means into that folder, not into the
        // directory being listed — that is what the pointer was over.
        var target = spot.Explicit;
        var destination = target ?? pane.CurrentPath;

        var intent = IntentFor(
            e.KeyModifiers, Input.DroppedFileReader.Offered(e.DataTransfer), destination);

        var move = intent == Input.DragIntent.Move;

        var dropped = Input.DroppedFileReader.Read(e.DataTransfer, destination, !move);

        if (!dropped.Any)
        {
            // **The contents of an archive, which have no paths until asked
            // for.** This is what dragging out of 7-Zip carries, and looking
            // only for paths saw an empty drop — so the drag did nothing at all
            // and read as the application being unreliable.
            if (TakeVirtual(e.DataTransfer, pane, destination))
            {
                e.Handled = true;
                return;
            }

            // Said rather than silently ignored: a drop that does nothing is
            // indistinguishable from one that missed the pane.
            if (dropped.Refusal.Length > 0) pane.Status = dropped.Refusal;

            e.Handled = true;
            return;
        }

        var paths = dropped.Paths;

        ReportDroppedPaths(paths);

        // **Before this handler returns, because after it returns the files may
        // not exist.** Dragging out of an archive hands over a path into the
        // archiver's own temporary folder, and the archiver deletes it the
        // moment the drop is over — measured at 541 files and 8,985,809 bytes
        // present at the drop, and the whole folder gone by the time the copy
        // ran. See DropStaging.
        var rescue = Input.DropStaging.Rescue(
            paths, Path.GetTempPath(), Path.Combine(DropStagingRoot(), Guid.NewGuid().ToString("N")[..12]));

        if (rescue.Rescued)
        {
            // The two figures separate a healthy rescue from a degraded one: a
            // move is a rename and costs nothing, while copied bytes are time
            // this thread spent frozen — if that number is ever large, the
            // fallback is being hit and the log says so.
            Console.Error.WriteLine(
                $"[vaktari] drop: rescued from the temporary folder before the source "
                + $"could clear it — {rescue.Moved} moved"
                + (rescue.CopiedBytes > 0 ? $", {rescue.CopiedBytes} bytes copied" : ""));

            paths = rescue.Paths;

            // Moved out of our own staging folder rather than copied, so
            // nothing is left behind. What the user asked of the ORIGINAL is
            // already satisfied: the archive still holds it either way.
            move = true;
        }

        // Shortcuts, before anything else can spend work: nothing is copied or
        // moved for a link, and the rescue above never fires for one because
        // internal drags are not volatile.
        if (intent == Input.DragIntent.Link && _shortcuts is { } shortcuts)
        {
            CreateShortcuts(shortcuts, pane, paths, destination);
            e.Handled = true;
            return;
        }

        // **A right-drag executes nothing.** The whole point of dragging with
        // the right button is that the drop ASKS — Explorer's oldest answer to
        // "did I just move that or copy it". The menu holds everything needed
        // to carry on; letting the drop fall through would decide for the user
        // the one time they explicitly asked to be consulted.
        if (_internalDrag && _rightDragInFlight)
        {
            ShowRightDropMenu(pane, target, destination, paths, defaultsToMove: move);
            e.Handled = true;
            return;
        }

        Paste(pane, target, paths, move);

        e.Handled = true;
    }

    /// <summary>The one place a drop's files actually go somewhere, so the
    /// direct path and the right-drag menu cannot drift apart.</summary>
    private static void Paste(
        ViewModels.PaneViewModel pane, string? target, IReadOnlyList<string> paths, bool move)
    {
        if (target is not null)
            pane.PasteIntoFolder(target, paths.ToList(), move);
        else
            pane.PasteInto(paths.ToList(), move);
    }

    /// <summary>
    /// Makes a shortcut per dropped item, and says what happened — including
    /// the refusal for files that live in somebody's temporary folder, where a
    /// shortcut would dangle the moment its owner tidies up.
    /// </summary>
    private void CreateShortcuts(
        Vaktari.Core.FileSystem.IShortcutMaker shortcuts,
        ViewModels.PaneViewModel pane,
        IReadOnlyList<string> paths,
        string destination)
    {
        var made = 0;
        var doomed = 0;

        foreach (var path in paths)
        {
            if (Input.DropStaging.IsVolatile(path, Path.GetTempPath()))
            {
                doomed++;
                continue;
            }

            try
            {
                shortcuts.CreateShortcut(path, destination);
                made++;
            }
            catch (Exception ex)
            {
                pane.Status = Vaktari.Core.FileSystem.Failures.Describe(ex, "make that shortcut");
                return;
            }
        }

        pane.Status = doomed > 0
            ? $"created {made} shortcut(s) — {doomed} skipped: files inside an archive have no lasting place to point at"
            : $"created {made} shortcut(s)";
    }

    /// <summary>
    /// The right-drag's question, asked where the button was released. The
    /// default the plain drop would have taken leads and is emphasised, the
    /// way Explorer bolds its default; closing the menu without choosing is
    /// the cancel.
    /// </summary>
    private void ShowRightDropMenu(
        ViewModels.PaneViewModel pane,
        string? target,
        string destination,
        IReadOnlyList<string> paths,
        bool defaultsToMove)
    {
        var kept = paths.ToList();

        MenuItem Option(string header, bool emphasised, Action run)
        {
            var item = new MenuItem { Header = header };

            if (emphasised) item.FontWeight = Avalonia.Media.FontWeight.Bold;

            item.Click += (_, _) => run();
            return item;
        }

        var menu = new MenuFlyout();

        var moveItem = Option("Move here", defaultsToMove, () => Paste(pane, target, kept, move: true));
        var copyItem = Option("Copy here", !defaultsToMove, () => Paste(pane, target, kept, move: false));

        // Move first when it is the default, copy first otherwise — the
        // emphasised answer is also the nearest one.
        if (defaultsToMove) { menu.Items.Add(moveItem); menu.Items.Add(copyItem); }
        else { menu.Items.Add(copyItem); menu.Items.Add(moveItem); }

        if (_shortcuts is { } shortcuts)
            menu.Items.Add(Option("Create shortcuts here", false,
                () => CreateShortcuts(shortcuts, pane, kept, destination)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Option("Cancel", false, static () => { }));

        // At the pointer, which at this instant is exactly the drop point.
        menu.ShowAt(this, showAtPointer: true);
    }

    // ---- spring-loaded tabs -----------------------------------------------------

    private PaneViewModel? _hoverTab;
    private DispatcherTimer? _hoverSwitch;

    /// <summary>
    /// Switches to a tab the pointer has rested on while dragging.
    ///
    /// **A file could not be dragged into another tab at all.** The only way
    /// across was the split view — open the other side, drag, close it again —
    /// for a move that both references do by hovering. Without the switch the
    /// drop would also be blind: the destination would be a folder you cannot
    /// see, which is not a thing to ask anyone to aim at.
    ///
    /// Six hundred milliseconds: long enough that dragging ACROSS the strip to
    /// reach the listing does not shuffle through every tab on the way, short
    /// enough not to feel stuck.
    /// </summary>
    private void HoverTab(PaneViewModel? tab)
    {
        if (ReferenceEquals(tab, _hoverTab)) return;

        _hoverTab = tab;
        _hoverSwitch?.Stop();
        _hoverSwitch = null;

        if (tab is null) return;

        _hoverSwitch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hoverSwitch.Tick += (_, _) =>
        {
            _hoverSwitch?.Stop();
            _hoverSwitch = null;

            // Read again rather than captured: the pointer may have moved on
            // between the tick being queued and it running.
            if (_hoverTab is not { } want) return;

            foreach (var group in new[] { _shell.Left, _shell.Right })
                if (group is not null && group.Tabs.Contains(want))
                    group.ActiveTab = want;
        };

        _hoverSwitch.Start();
    }
}
