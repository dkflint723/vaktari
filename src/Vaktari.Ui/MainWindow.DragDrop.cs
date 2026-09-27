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
    /// It drives the shared edge-scroll timer, which the rubber band drives
    /// too — safely, because a band and a file drag cannot both be in progress:
    /// the band needs a left press on empty space, and by the time a drag is
    /// running that press has been spent on the drag. The fields still carry a
    /// _band prefix from when the band was its only caller.
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

    // ---- the drag being answered --------------------------------------------

    /// <summary>
    /// The drag this window is answering, as the object Avalonia hands every
    /// enter, over and leave of it.
    ///
    /// **One object per visit, and that is what makes it the drag's identity.**
    /// Avalonia's Windows backend wraps the source's data object once when the
    /// drag enters the window and hands that same wrapper to every event until
    /// the drag leaves or drops — and the drop itself gets a FRESH wrapper,
    /// which is why the drop reads what the drag-overs learned instead of
    /// asking this. A different object here is a different drag, and
    /// <see cref="Join"/> starts again.
    /// </summary>
    private IDataTransfer? _session;

    /// <summary>
    /// The drag was seen to carry files with no location on disk — what
    /// Explorer's zip view and 7-Zip hand over for an archive's contents: a
    /// descriptor and a stream per file, and no paths.
    ///
    /// **Asked once and remembered, because asking can fail.** The question is
    /// a call into the source process, made on every drag-over, and one that
    /// failed turned that tick's answer into None. Windows drops only where
    /// the LAST answer before the release was not None, so one bad moment at
    /// the end of the gesture took the whole drag away — "sometimes it works,
    /// sometimes nothing happens". A yes is a fact about the drag, not about
    /// the tick, so it is kept for as long as the drag is.
    ///
    /// **Kept across a leave.** It is forgotten when another drag's first
    /// event arrives (<see cref="Join"/>) or at the drop, and never on a
    /// leave, for the reason <see cref="OnDragLeave"/> gives: most leaves are
    /// the pointer crossing a part of this window that takes no drop, and
    /// forgetting there put the fault straight back.
    /// </summary>
    private bool _sessionCarriesVirtual;

    /// <summary>The failure that decided the latest answer, if one did. A
    /// drag that ends on a refusal because of THIS is worth a line in the
    /// log; one that ends over somewhere that refuses is not — and nor is one
    /// that met a failure earlier and got past it.</summary>
    private string? _tickFault;

    /// <summary>At most one error line per drag: a handler that throws does so
    /// twenty times a second.</summary>
    private bool _sessionErrorLogged;

    /// <summary>
    /// What the last drag-over told the user, and the element it told it about.
    ///
    /// **The drop is delivered to the element the last drag-over was over —
    /// not to whatever is under the pointer when the button comes up.**
    /// Avalonia raises it on the target it remembered from that drag-over,
    /// which is up to one polling interval old. In between, the edge scroll
    /// may have moved the listing, and a virtualized list recycles the row
    /// that was under the pointer for another item: the ring was on "Photos",
    /// and the element the drop arrives on now says "Tax 2025". Reading the
    /// folder again from that element put the files where the user never
    /// pointed. So the drop takes what the drag-over promised when it arrives
    /// on the same element, and works it out afresh only when it does not.
    /// </summary>
    private object? _promisedSource;

    private DropTarget? _promised;

    private DropTarget? Promised(object? source)
        => source is not null && ReferenceEquals(source, _promisedSource) ? _promised : null;

    private void Join(IDataTransfer data)
    {
        if (ReferenceEquals(data, _session)) return;

        EndSession();
        _session = data;
    }

    private void EndSession()
    {
        _session = null;
        _sessionCarriesVirtual = false;
        _tickFault = null;
        _sessionErrorLogged = false;
        _promisedSource = null;
        _promised = null;
    }

    /// <summary>Remembers the failure that is deciding this answer.</summary>
    private void Fault(string what) => _tickFault = what;

    /// <summary>
    /// An exception caught at a handler's edge: remembered as a fault, and
    /// logged as an error with its stack — the first time in each drag.
    /// </summary>
    private void Failed(string doing, Exception ex)
    {
        var what = $"{doing} failed: {Describe(ex)}";

        Fault(what);

        if (_sessionErrorLogged) return;

        _sessionErrorLogged = true;

        Console.Error.WriteLine("[vaktari] drop: " + what);
        Vaktari.Core.Diagnostics.Log.Error("drop", what + Environment.NewLine + ex);
    }

    /// <summary>
    /// **A drag that failed and a drag that was refused looked the same, and
    /// neither said anything.** A drop out of an archive that ended with
    /// nothing, because asking the source a question failed at the wrong
    /// moment, left no trace anywhere: the cursor had simply said no. This is
    /// the one line that tells the two apart.
    ///
    /// Given the failure that ended it — the one that decided the last answer,
    /// or the drop's own — and silent without one: a drag that met a failure
    /// earlier and got past it was not refused because of it.
    ///
    /// **Once per visit of a drag to this window, and that needs no flag.**
    /// It is called only where a visit ends: the drop, or the leave that
    /// Windows sends in place of one (see <see cref="OnDragLeave"/>) — never
    /// at the leaves raised for crossing the window, which a first version
    /// logged at and then started counting again after. A visit ends once,
    /// and a drag that comes back arrives as a new data object.
    /// </summary>
    private void TellRefused(string? fault)
    {
        if (fault is null) return;

        DropWarn("a drag ended with nothing taken, because " + fault);
    }

    /// <summary>
    /// Whether the drag carries files that have no location on disk — asked
    /// of the source until it says yes, then remembered for the drag. See
    /// <see cref="_sessionCarriesVirtual"/>.
    ///
    /// **Never a drag that carries paths.** A descriptor is not an archive:
    /// Explorer offers FileGroupDescriptorW and FileContents beside CF_HDROP
    /// for an ORDINARY file on disk. Asked only whether a descriptor was
    /// there, a Shift-drag of a file onto its own folder — refused as "already
    /// here" by the path rules — was answered Copy, and the drop pasted the
    /// file back beside itself as if it had come out of an archive. The paths
    /// decide; the descriptor is only for a drag that has none.
    /// </summary>
    private bool CarriesVirtualFiles(IDataTransfer data, IReadOnlyList<string> offered)
    {
        if (offered.Count > 0) return false;

        if (_sessionCarriesVirtual) return true;

        if (_virtualDrop is not { } drop) return false;

        if (drop.Offers(data, out var failure)) return _sessionCarriesVirtual = true;

        if (failure is not null) Fault("asking whether the drag carries files from an archive: " + failure);

        return false;
    }

    /// <summary>
    /// A read of the drag that may fail — every one of them is a call into
    /// another process — answered with <paramref name="otherwise"/> and a
    /// remembered fault when it does, rather than an exception that takes the
    /// rest of the answer with it. A drop out of an archive has no paths to
    /// read, so a failure here must still leave the question of its archive
    /// files to be asked.
    /// </summary>
    private T Guarded<T>(string doing, Func<T> read, T otherwise)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Fault($"{doing} failed: {Describe(ex)}");
            return otherwise;
        }
    }

    /// <summary>The local paths the drag carries, or none when they could not
    /// be read.</summary>
    private IReadOnlyList<string> OfferedPaths(DragEventArgs e)
        => Guarded("reading the paths the drag carries",
                   () => Input.DroppedFileReader.Offered(e.DataTransfer), []);

    /// <summary>The type, the HRESULT and the message: a COM failure's message
    /// is often a generic sentence, and the number is what identifies it.</summary>
    private static string Describe(Exception ex)
        => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}";

    /// <summary>A line about a drop, to stderr — seen from a console run and
    /// nowhere else.</summary>
    private static void DropSay(string line) => Console.Error.WriteLine("[vaktari] drop: " + line);

    /// <summary>
    /// **A drop that went wrong, to the log as well.** Every drop diagnostic
    /// wrote to stderr alone, and the shipped build is a windowed process that
    /// has none — so the lines written to explain a lost drop were written
    /// nowhere anybody could read. The log keeps warnings and redacts paths.
    /// </summary>
    private static void DropWarn(string line)
    {
        DropSay(line);
        Vaktari.Core.Diagnostics.Log.Warn("drop", line);
    }

    /// <summary>
    /// The effect to answer, given what the source allows.
    ///
    /// **A Shift or Alt drag from most applications showed no-drop and
    /// dropped nothing.** OLE hands the target the source's allowed effects
    /// in the same argument it reads the answer back from, masks the answer
    /// with them, and skips the drop when what is left is None. 7-Zip and
    /// most programs allow Copy alone, so a Move (Shift, or the same-drive
    /// default of an internal drag) or a Link (Alt) came back as nothing at
    /// all. Now the wanted effect stands when it is allowed, and otherwise
    /// falls back to Copy when that is — the one answer every file source
    /// gives.
    ///
    /// <paramref name="masked"/> is whether the platform means the incoming
    /// effect as a mask. On Windows it does. X11 hands over the ONE action the
    /// source proposes, and the target is free to answer another — so there
    /// the incoming effect is not a limit, and reading it as one would turn
    /// every same-drive move from a file manager into a copy.
    ///
    /// An empty mask is no mask: no source allows nothing, and OLE would
    /// discard any answer to one anyway. Events a test builds by hand arrive
    /// with none.
    /// </summary>
    internal static DragDropEffects Permitted(DragDropEffects wanted, DragDropEffects allowed, bool masked)
    {
        if (!masked || allowed == DragDropEffects.None || (wanted & allowed) == wanted) return wanted;

        return allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Whether this platform's drop target hands over the source's
    /// allowed effects as a mask. See <see cref="Permitted"/>.</summary>
    private static bool EffectsAreAMask => OperatingSystem.IsWindows();

    /// <summary>
    /// What a drop reports for a move Vaktari carries out itself — a move into
    /// a folder, or files sent to the bin.
    ///
    /// **None on Windows, Move everywhere else, and each is that platform's
    /// "done".** The shell calls a move the target performs an optimized move
    /// and asks for anything but Move back, because Move is half of how a
    /// source learns it should delete the originals. X11 reads the answer the
    /// other way: XdndFinished carries "accepted" only when the action is not
    /// None, so a None there tells GTK or Qt the drop was REJECTED, and GTK
    /// plays its failure animation over a move that worked.
    /// </summary>
    internal static DragDropEffects MovedByUs(bool windows)
        => windows ? DragDropEffects.None : DragDropEffects.Move;

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
            ? OfferedPaths(e)
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

    /// <summary>
    /// Answers the drag-over and the drag-enter, which is the same question.
    ///
    /// **Nothing thrown in here may leave it.** An exception out of a drag
    /// handler goes up through Avalonia into its COM layer, which turns it
    /// into E_FAIL for Windows and writes nothing anywhere. On the enter that
    /// is worse than one lost answer: a failed DragEnter tells Windows this
    /// window is no drop target, and it stops asking until the pointer leaves
    /// the window. So the whole answer is inside one try, a failure answers
    /// None for this tick only, and is logged — once per drag, because this
    /// runs twenty times a second.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        // What the source allows, before anything below overwrites it: the
        // toolkit starts the answer at it. See Permitted.
        var allowed = e.DragEffects;

        Join(e.DataTransfer);
        _tickFault = null;

        try
        {
            // Before every branch below, refusals included: what a drag is
            // carrying does not stop being carried over a target that will not
            // take it, and a label that blinked out over the wrong folder would
            // read as the drag itself having ended.
            ShowDragGhost(e);

            // The sidebar first: a place row has no pane above it, so asking
            // for one would refuse the drop before the place was ever
            // considered.
            var spot = TargetAt(e.Source);

            // What this answer promises, kept for the drop — see _promised.
            _promisedSource = e.Source;
            _promised = spot;

            // Before the refusal below: a tab strip is not a pane and not a
            // place, so a drag resting on it would be refused and never counted
            // as a hover.
            HoverTab(TabAt(e.Source));

            if (!spot.Exists)
            {
                e.DragEffects = DragDropEffects.None;
                HighlightDropTarget(null);
                StopDragScroll();
                return;
            }

            // **The bin is a verb, not a folder**, so it is answered here
            // rather than falling through to the destination rules below —
            // which would ask what it costs to copy into "vaktari:trash" and
            // refuse.
            //
            // Move, because that is what dropping on the bin does to the
            // original, and it is the effect Explorer's own cursor shows over
            // the Recycle Bin.
            if (spot.IsBin)
            {
                e.DragEffects = OfferedPaths(e).Count > 0
                    ? DragDropEffects.Move
                    : DragDropEffects.None;

                HighlightDropTarget(null, place: VirtualPaths.Trash);
                StopDragScroll();
                return;
            }

            // **The sidebar's own ground pins**, and like the bin it is
            // answered before the destination rules, which name no folder for
            // it: measured by letting a drag over the blank strip fall through
            // to them, the cursor came back Copy with a destination of "" — so
            // the toolkit would deliver the drop and the copy path would be
            // handed nowhere to put it. What the cursor says here is
            // PinPlan.Effect's rule instead, and the Link it answers is what
            // tells the two apart.
            if (spot.IsSidebar)
            {
                e.DragEffects = Input.PinnableDrop.For(OfferedPaths(e)).Effect;

                HighlightDropTarget(null);
                StopDragScroll();
                return;
            }

            // Near an edge of the listing, keep it moving — checked on every
            // drag-over rather than only where the drop would be accepted,
            // because scrolling is how you REACH somewhere that would accept
            // it.
            DragScroll(e);

            var place = spot.Place;
            var pane = spot.Pane;
            var destination = spot.Destination;

            // A virtual listing is a view, not a folder, so its background has
            // nowhere to put anything. The paste path refuses it too, but that
            // refusal arrives as a line of status text after the drop; the
            // cursor can say it beforehand, which is when it is still useful.
            // Read from `destination` rather than the pane, so a real folder
            // ROW inside Recent still takes a drop.
            if (VirtualPaths.IsVirtual(destination))
            {
                e.DragEffects = DragDropEffects.None;
                HighlightDropTarget(null);
                return;
            }

            // Refuse a drop that would achieve nothing, so the cursor says so
            // before the click rather than a duplicate appearing after it.
            // **The effect first, from the raw paths, then what the drop
            // means.** Copying keeps a file dropped into its own folder — that
            // is Explorer's duplicate gesture — while moving discards it as a
            // no-op, so the filtering cannot be decided before the intent is.
            //
            // **Not Copy: everything that is not a Move.** The reader is told
            // copy-or-move and only a MOVE has a reason to strip a path already
            // living in the destination. Asking it `== Copy` put Link on the
            // move side, so Alt+drag onto the folder a file is already in —
            // Explorer's way of putting "X - Shortcut" beside the original —
            // was filtered down to nothing and answered None. Measured: OnDrop
            // asks the same reader `!move` and made the shortcut, so the drop
            // handler was already right and unreachable, because a real OLE
            // drag obeys the cursor. See
            // Alt_onto_the_folder_the_file_already_lives_in_still_makes_a_shortcut.
            var offered = OfferedPaths(e);
            var effect = Permitted(EffectFor(e.KeyModifiers, offered, destination), allowed, EffectsAreAMask);

            var takeable = Guarded(
                "reading what the drag carries",
                () => Input.DroppedFileReader.Read(e.DataTransfer, destination, effect != DragDropEffects.Move),
                Input.DroppedFiles.Nothing).Any;

            // Files with no location on disk — an archive's contents — have no
            // paths, so nothing above sees them; but they can be had, and the
            // cursor has to say so before the button is released rather than
            // after.
            if (!takeable && CarriesVirtualFiles(e.DataTransfer, offered))
            {
                // Copy: there is no original to take away. A move out of an
                // archive is not a thing the archive would survive.
                e.DragEffects = Permitted(DragDropEffects.Copy, allowed, EffectsAreAMask);
                HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place, spot.Tree);
                return;
            }

            if (!takeable)
            {
                e.DragEffects = DragDropEffects.None;
                HighlightDropTarget(null);
                return;
            }

            e.DragEffects = effect;

            // A place is its own target; highlighting a pane for it would
            // point at the wrong half of the window. The row and the place are
            // marked whichever it is, so the ring is on the thing the files
            // will go into.
            HighlightDropTarget(place is null ? pane : null, spot.Folder, spot.Place, spot.Tree);
        }
        catch (Exception ex)
        {
            e.DragEffects = DragDropEffects.None;
            Failed("answering a drag", ex);
        }
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
    /// place. Null when the drop carries none — including a drop that carries
    /// paths, which is never one; see <see cref="CarriesVirtualFiles"/> — and
    /// otherwise how many came out, which is 0 when none could be had.
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
    ///
    /// **Asked again at the drop, and the drag-over's yes outranks the second
    /// answer.** The drop arrives with a data object Avalonia has only just
    /// wrapped, so asking it anything is another round trip into the source
    /// process — and a drag the cursor had already accepted as an archive's
    /// was lost, with nothing said, whenever that one question failed.
    /// <paramref name="known"/> is the drag-over's yes; with it, Take is simply
    /// tried, and Take says loudly why when it truly cannot.
    /// </summary>
    private int? TakeVirtual(
        IDataTransfer data, IReadOnlyList<string> offered, PaneViewModel pane, string destination, bool known)
    {
        if (_virtualDrop is not { } virtualDrop || offered.Count > 0) return null;

        if (!known && !virtualDrop.Offers(data, out var failure))
        {
            if (failure is not null) Fault("asking whether the drop carries files from an archive: " + failure);
            return null;
        }

        pane.Status = "taking the files out of the archive…";

        IReadOnlyList<string> taken;

        try
        {
            taken = virtualDrop.Take(data);
        }
        catch (Exception ex)
        {
            DropWarn($"taking files out of an archive failed: {Describe(ex)}");
            pane.Status = $"could not take those out of the archive: {ex.Message}";
            return 0;
        }

        if (taken.Count == 0)
        {
            pane.Status = "nothing came out of that archive";
            return 0;
        }

        pane.PasteIntoFolder(destination, taken, move: true);

        return taken.Count;
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
    ///
    /// **The two answers that mean trouble go to the log too.** This wrote to
    /// stderr alone, which a windowed build does not have, so "already gone"
    /// — the very line it exists for — was never seen outside a console run.
    /// </summary>
    private static void ReportDroppedPaths(IReadOnlyList<string> paths)
    {
        DropSay($"{paths.Count} path(s) handed over");

        for (var i = 0; i < paths.Count && i < 8; i++)
        {
            var path = paths[i];

            if (File.Exists(path))
            {
                DropSay($"  file present · {path}");
                continue;
            }

            if (!Directory.Exists(path))
            {
                DropWarn($"  ALREADY GONE · {path}");
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
                DropWarn($"  folder unreadable · {path}");
                continue;
            }

            DropSay($"  folder with {files} file(s), {bytes} bytes · {path}");
        }
    }

    /// <summary>
    /// Puts the drag's marks away.
    ///
    /// **A leave is almost never the end of the drag, and forgets nothing.**
    /// Avalonia's DragDropDevice.DragOver raises a leave on the old element
    /// whenever the element under the pointer changes: followed by an enter
    /// when the new one takes drops, and by NOTHING when it does not — the
    /// toolbar, the address and status bars, the sidebar's resize handle
    /// between the listing and the tree. A first version forgot the drag's
    /// yes about archive files one dispatcher turn after a leave that no
    /// drag-over followed; measured through Avalonia's own OleDropTarget, one
    /// pass over the sidebar handle turned the next answer from Copy into
    /// None and asked the source again — the very fault it was written for.
    /// What the drag has learned is forgotten by <see cref="Join"/>, when the
    /// next drag's first event brings a different data object, and by the
    /// drop.
    ///
    /// **Only the real end is told apart, for the log.** Windows ends a visit
    /// without a drop by calling IDropTarget::DragLeave, which OleDropTarget
    /// passes on with no effects at all; every leave raised for an element
    /// change carries the source's allowed effects, which no source leaves
    /// empty. A real end whose last answer was decided by a failure is the
    /// drag the maintainer saw vanish, and says why — once per drag.
    /// </summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        try
        {
            HighlightDropTarget(null);
            HideDragGhost();
            StopDragScroll();
            HoverTab(null);
        }
        catch (Exception ex)
        {
            // NO KILLING MUTATION, and none was found: nothing these four call
            // throws on any state a test can reach. It is here for
            // OnDragOver's reason, which a leave shares — whatever does throw
            // in a drag handler vanishes into the COM layer unsaid.
            Failed("ending a drag", ex);
        }

        if (e.DragEffects == DragDropEffects.None) TellRefused(_tickFault);
    }

    /// <summary>
    /// Marks what a drop would land in: the pane, the folder row inside it, the
    /// sidebar place, and the row of the folder tree.
    ///
    /// **Only the pane was ever marked, and the pane is the one thing that was
    /// never in doubt.** What a drag could not tell you is whether releasing
    /// puts the files into the folder under the pointer or into the folder
    /// being listed — different places, and finding out meant releasing and
    /// looking. All of them are cleared together, because a stale ring on a
    /// row you have moved off is worse than none.
    ///
    /// The tree row is marked by path and only in the tree, so a folder that
    /// is both a pinned place and a tree row lights where the pointer is and
    /// not in both sections at once.
    /// </summary>
    private void HighlightDropTarget(
        PaneViewModel? pane, string? row = null, string? place = null, string? tree = null)
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

        if (_shell.Sidebar.Tree is { } folders)
            foreach (var node in folders.Rows)
                node.IsDropTarget = tree is not null && PathRules.Same(node.Path, tree);
    }

    /// <summary>
    /// Takes the drop.
    ///
    /// **Every way out says what was done, as the effect handed back to the
    /// source.** This set none at all, so Avalonia returned the effect it was
    /// given — everything the source allowed, Copy and Move for Explorer's zip
    /// view, whatever had happened. A source deletes its originals only when
    /// that answer is Move AND the target has also set the performed effect
    /// on the data object to Move, which Vaktari never did — so the originals
    /// were never at risk, but the answer was not true, and a source is
    /// entitled to act on it. None is set first and each path that does
    /// something replaces it with what it did: Copy for a copy and for files
    /// taken out of an archive, Link for shortcuts and pins, and for a move
    /// Vaktari performs itself whatever <see cref="MovedByUs"/> says this
    /// platform calls a finished move.
    ///
    /// **What it does is what the source allows**, by <see cref="Permitted"/>
    /// — the same rule the drag-over answered with, so the drop does what the
    /// cursor said.
    ///
    /// **Nothing thrown in here may leave it**, for OnDragOver's reason: the
    /// COM layer swallows it, and the drop simply did not happen. It is logged,
    /// and said on the status line, because a drop that does nothing is
    /// indistinguishable from one that missed.
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        // What the source allows arrives as the starting answer; read before
        // it is replaced with what was done.
        var allowed = e.DragEffects;

        e.DragEffects = DragDropEffects.None;

        // Read before anything else can end the drag: what the drag-overs
        // learned about it is what this drop is about to rely on.
        var knownVirtual = _sessionCarriesVirtual;
        var promised = Promised(e.Source);

        _tickFault = null;

        PaneViewModel? reporter = null;

        try
        {
            HighlightDropTarget(null);
            HideDragGhost();
            StopDragScroll();
            HoverTab(null);

            // **The bin takes drops.** Its row is AllowDrop with a comment
            // about taking them "the way the tree and Quick access do in
            // Explorer", and nothing ever mapped one to IFileOperations.Trash —
            // PlaceAt refuses a virtual path, and the bin's path is the virtual
            // vaktari:trash, so the drop landed nowhere and looked like the row
            // was simply dead.
            var spot = promised ?? TargetAt(e.Source);

            reporter = spot.Pane ?? _shell.ActiveTab;

            if (spot.IsBin && _shell.ActiveTab is { } binPane)
            {
                // The bin sends the originals away itself: a move Vaktari
                // performs, reported as one.
                var offered = OfferedPaths(e);

                if (offered.Count > 0)
                {
                    // A drive the bin turned away was not moved: None, which
                    // the handler set first. See PaneViewModel.TrashPaths.
                    if (!binPane.TrashPaths(offered)) return;

                    e.DragEffects = MovedByUs(OperatingSystem.IsWindows());
                    _virtualDrop?.MovedByTarget(e.DataTransfer);
                }
                else
                {
                    Refused(binPane, "");
                }

                return;
            }

            // **A folder dropped on the panel becomes a place.** Not awaited:
            // the pin writes a file, and a drag must not hold the UI thread
            // while it does. The shell reports what happened on the active
            // tab's status line, including the files it left alone.
            if (spot.IsSidebar)
            {
                var offered = OfferedPaths(e);

                _ = _shell.PinDroppedAsync(offered);

                // A pin is a link to the folder: the cursor's own answer, and
                // nothing the source should act on.
                e.DragEffects = Input.PinnableDrop.For(offered).Effect;
                return;
            }

            // A sidebar place and a breadcrumb are destinations in their own
            // right, and neither is guaranteed to have a pane above it to ask
            // about — so the active tab stands in as the pane that reports what
            // happened.
            var pane = spot.Pane ?? (spot.Exists ? _shell.ActiveTab : null);

            if (pane is null) return;

            // Dropping onto a folder row means into that folder, not into the
            // directory being listed — that is what the pointer was over.
            var target = spot.Explicit;
            var destination = target ?? pane.CurrentPath;

            var offeredPaths = OfferedPaths(e);

            var effect = Permitted(EffectFor(e.KeyModifiers, offeredPaths, destination), allowed, EffectsAreAMask);

            // Nothing the source allows is anything this drop can do. The
            // drag-over said so and Windows would not have delivered it; a
            // platform that does is answered the same way.
            if (effect == DragDropEffects.None) return;

            var move = effect == DragDropEffects.Move;

            var dropped = Guarded(
                "reading what the drop carries",
                () => Input.DroppedFileReader.Read(e.DataTransfer, destination, !move),
                Input.DroppedFiles.Nothing);

            if (!dropped.Any)
            {
                // **The contents of an archive, which have no paths until
                // asked for.** This is what dragging out of 7-Zip carries, and
                // looking only for paths saw an empty drop — so the drag did
                // nothing at all and read as the application being unreliable.
                if (TakeVirtual(e.DataTransfer, offeredPaths, pane, destination, knownVirtual) is { } taken)
                {
                    // Copy: the archive keeps what it had.
                    if (taken > 0) e.DragEffects = DragDropEffects.Copy;
                    else TellRefused(_tickFault);
                    return;
                }

                // Said rather than silently ignored: a drop that does nothing
                // is indistinguishable from one that missed the pane.
                Refused(pane, dropped.Refusal);
                return;
            }

            var paths = dropped.Paths;

            ReportDroppedPaths(paths);

            // **Before this handler returns, because after it returns the
            // files may not exist.** Dragging out of an archive hands over a
            // path into the archiver's own temporary folder, and the archiver
            // deletes it the moment the drop is over — measured at 541 files
            // and 8,985,809 bytes present at the drop, and the whole folder
            // gone by the time the copy ran. See DropStaging.
            var rescue = Input.DropStaging.Rescue(
                paths, Path.GetTempPath(), Path.Combine(DropStagingRoot(), Guid.NewGuid().ToString("N")[..12]));

            if (rescue.Rescued)
            {
                // The two figures separate a healthy rescue from a degraded
                // one: a move is a rename and costs nothing, while copied bytes
                // are time this thread spent frozen — so a rescue that had to
                // copy is the one that goes to the log.
                var line = "rescued from the temporary folder before the source "
                           + $"could clear it — {rescue.Moved} moved"
                           + (rescue.CopiedBytes > 0 ? $", {rescue.CopiedBytes} bytes copied" : "");

                if (rescue.CopiedBytes > 0) DropWarn(line);
                else DropSay(line);

                paths = rescue.Paths;

                // Moved out of our own staging folder rather than copied, so
                // nothing is left behind. What the user asked of the ORIGINAL
                // is already satisfied: the archive still holds it either way.
                move = true;
            }

            // Shortcuts, before anything else can spend work: nothing is
            // copied or moved for a link, and the rescue above never fires for
            // one because internal drags are not volatile. EffectFor answers
            // Link only where there is a shortcut maker.
            if (effect == DragDropEffects.Link && _shortcuts is { } shortcuts)
            {
                if (CreateShortcuts(shortcuts, pane, paths, destination) > 0)
                    e.DragEffects = DragDropEffects.Link;

                return;
            }

            // **A right-drag executes nothing.** The whole point of dragging
            // with the right button is that the drop ASKS — Explorer's oldest
            // answer to "did I just move that or copy it". The menu holds
            // everything needed to carry on; letting the drop fall through
            // would decide for the user the one time they explicitly asked to
            // be consulted. Nothing has been done yet, so the effect stays
            // None — and the source is this window, which does not read it.
            if (_internalDrag && _rightDragInFlight)
            {
                ShowRightDropMenu(pane, target, destination, paths, defaultsToMove: move);
                return;
            }

            // A refused paste started nothing, so the source is told None:
            // a Move for a drive the guard turned away would tell an X11
            // source to delete what it dragged. See PaneViewModel.PasteInto.
            if (!Paste(pane, target, paths, move)) return;

            // Asked of the effect, not of `move`: a rescue moves our own
            // staged copies, which to the source was a copy of its files.
            if (effect == DragDropEffects.Move)
            {
                e.DragEffects = MovedByUs(OperatingSystem.IsWindows());
                _virtualDrop?.MovedByTarget(e.DataTransfer);
            }
            else
            {
                e.DragEffects = DragDropEffects.Copy;
            }
        }
        catch (Exception ex)
        {
            e.DragEffects = DragDropEffects.None;
            Failed("taking a drop", ex);

            if ((reporter ?? _shell.ActiveTab) is { } said)
                said.Status = "that drop failed: " + Vaktari.Core.FileSystem.Failures.Describe(ex, "take that drop");
        }
        finally
        {
            EndSession();
        }
    }

    /// <summary>
    /// Says a drop took nothing, and why: the reader's own reason when it has
    /// one, and otherwise the failure that stopped THIS drop being read — not
    /// one the drag met earlier and got past.
    /// </summary>
    private void Refused(PaneViewModel pane, string refusal)
    {
        if (refusal.Length > 0)
        {
            pane.Status = refusal;
            return;
        }

        if (_tickFault is { } fault)
        {
            pane.Status = "could not read that drop: " + fault;
            TellRefused(fault);
        }
    }

    /// <summary>The one place a drop's files actually go somewhere, so the
    /// direct path and the right-drag menu cannot drift apart.</summary>
    /// <returns>Whether a copy or move was started.</returns>
    private static bool Paste(
        ViewModels.PaneViewModel pane, string? target, IReadOnlyList<string> paths, bool move)
        => target is not null
            ? pane.PasteIntoFolder(target, paths.ToList(), move)
            : pane.PasteInto(paths.ToList(), move);

    /// <summary>
    /// Makes a shortcut per dropped item, and says what happened — including
    /// the refusal for files that live in somebody's temporary folder, where a
    /// shortcut would dangle the moment its owner tidies up.
    ///
    /// How many were made, which is what the drop reports back as Link — or
    /// as nothing, when every one was skipped or refused.
    /// </summary>
    private int CreateShortcuts(
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
                return made;
            }
        }

        pane.Status = doomed > 0
            ? $"created {made} shortcut(s) — {doomed} skipped: files inside an archive have no lasting place to point at"
            : $"created {made} shortcut(s)";

        return made;
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
