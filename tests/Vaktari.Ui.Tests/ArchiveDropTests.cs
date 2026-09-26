using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.Diagnostics;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Dragging out of a zip open in Explorer, "which sometimes works and
/// sometimes fails" — and when it fails, nothing is dropped and nothing says
/// why.
///
/// **Windows drops only where the LAST drag-over said yes.** Explorer's zip
/// view offers no paths, only an archive's descriptor, so the window's yes
/// rests on one question asked of the source process on every drag-over. One
/// failed answer on the last tick before the release and Windows called
/// DragLeave instead of Drop; an exception anywhere in the handlers was eaten
/// by the COM layer as E_FAIL; the drop reported the source's full allowed
/// effect, Move included, back to it; and every diagnostic went to a stderr the
/// windowed build does not have. Each of those is a test here, against a real
/// headless MainWindow with its archive reader replaced by
/// <see cref="Archive"/>, which answers however the test says.
///
/// **What this cannot stage is Windows itself** — which answer OLE acts on,
/// and when it calls DragLeave rather than Drop. That is read from Avalonia's
/// DragDropDevice and OleDropTarget and from the drag loop, and written down
/// in MainWindow.DragDrop.cs; this drives the handlers the way Avalonia calls
/// them.
/// </summary>
public sealed class ArchiveDropTests : OwnedViewModels
{
    private readonly string _logs = Path.Combine(
        Path.GetTempPath(), "vaktari-archive-drop-log-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<string> _made = [];

    public ArchiveDropTests() => Log.Configure(_logs, includePaths: false);

    public override void Dispose()
    {
        Log.Reset();

        foreach (var dir in _made.Append(_logs)) Delete(dir);

        base.Dispose();
    }

    // ---- nothing thrown leaves a handler -----------------------------------

    /// <summary>
    /// **An exception out of the drag-over used to leave it** — to be turned
    /// into E_FAIL by Avalonia's COM layer with nothing written anywhere, and
    /// on a DragEnter to make Windows stop offering this window the drag until
    /// the pointer left it. It is caught, answered None for that tick, and
    /// logged as an error — ONCE, though the drag-over runs twenty times a
    /// second and throws on every one of them here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_over_that_throws_answers_none_and_is_logged_once()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => throw new InvalidOperationException("the reader broke") };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            var first = Raise(listing, DragDrop.DragOverEvent, data);
            var second = Raise(listing, DragDrop.DragOverEvent, data);

            Assert.Equal(DragDropEffects.None, first.DragEffects);
            Assert.Equal(DragDropEffects.None, second.DragEffects);

            var errors = LogLines().Where(l => l.Contains(" error ", StringComparison.Ordinal)).ToList();

            Assert.True(errors.Count == 1,
                $"expected one error line for the drag, found {errors.Count}: {string.Join(" / ", errors)}");
            Assert.Contains("answering a drag failed", errors[0]);
            Assert.Contains("the reader broke", errors[0]);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The drop's own half: the exception is caught, the drop reports None to
    /// the source, and the pane's status line says the drop failed — a drop
    /// that does nothing is otherwise indistinguishable from one that missed.
    /// In the application's own words for the failure, as every other status
    /// line gives it, rather than the exception's message.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drop_that_throws_says_so_on_the_status_line()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => throw new UnauthorizedAccessException("Access to the path is denied.") };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.Equal("that drop failed: you do not have permission to take that drop", pane.Status);
            Assert.Contains(LogLines(), l => l.Contains(" error ", StringComparison.Ordinal)
                                             && l.Contains("taking a drop failed", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- the drag's yes is kept --------------------------------------------

    /// <summary>
    /// **The fault the maintainer saw.** The source says yes, then fails to
    /// answer — as a busy Explorer does — on every question after. The second
    /// drag-over must still say Copy, because a None there is the answer
    /// Windows takes the drag away on; and the drop must still take the files,
    /// though asking the fresh data object the drop arrives with fails too.
    /// The drop reports Copy: the archive keeps what it had.
    /// </summary>
    [AvaloniaFact]
    public async Task A_yes_about_archive_files_outlives_a_failed_question()
    {
        var into = Folder("into");
        var staged = Staged("inside.txt");

        var archive = new Archive { Taking = () => [staged] };
        archive.Answer = _ => archive.Asked == 1 ? (true, null) : (false, "the source was busy");

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragEnterEvent, data).DragEffects);
            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            // A fresh wrapper, as the real drop brings.
            var drop = Raise(listing, DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(1, archive.Took);
            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);

            await Arrives(Path.Combine(into, "inside.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A yes belongs to one drag.** A second drag, arriving as a different
    /// data object, is asked afresh — and a source that says no is refused,
    /// however recently another drag carried an archive's files.
    /// </summary>
    [AvaloniaFact]
    public async Task A_new_drag_is_asked_again()
    {
        var into = Folder("into");
        var archive = new Archive();
        archive.Answer = _ => archive.Asked == 1 ? (true, null) : (false, null);

        var (window, pane) = await Shown(into, archive);

        try
        {
            var listing = ListingOf(window, pane);

            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, Words()).DragEffects);
            Assert.Equal(DragDropEffects.None, Raise(listing, DragDrop.DragOverEvent, Words()).DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Avalonia raises a leave every time the pointer crosses from one
    /// control to another**, followed at once by an enter — so a leave is not
    /// the end of the drag, and forgetting the yes on it would have put the
    /// fault straight back: the next question fails, and the answer is None.
    /// </summary>
    [AvaloniaFact]
    public async Task Crossing_from_one_control_to_another_keeps_the_yes()
    {
        var into = Folder("into");
        var staged = Staged("kept.txt");

        var archive = new Archive { Taking = () => [staged] };
        archive.Answer = _ => archive.Asked == 1 ? (true, null) : (false, "the source was busy");

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Raise(listing, DragDrop.DragOverEvent, data);
            Raise(listing, DragDrop.DragLeaveEvent, data);
            Raise(listing, DragDrop.DragEnterEvent, data);
            Pump();

            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            Raise(listing, DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(1, archive.Took);

            await Arrives(Path.Combine(into, "kept.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A leave that no enter follows is the pointer crossing a part of the
    /// window that takes no drop** — the toolbar, the status bar, the handle
    /// between the listing and the tree. Avalonia raises a leave there and
    /// nothing after it. Forgetting the yes one dispatcher turn later, as a
    /// first version did, meant the next drag-over asked the source again,
    /// and a failed answer there is the None Windows takes the drag away on.
    /// RealDropTargetTests drives the same crossing through Avalonia's own
    /// drop target.
    /// </summary>
    [AvaloniaFact]
    public async Task Crossing_a_part_that_takes_no_drop_keeps_the_yes()
    {
        var into = Folder("into");
        var archive = new Archive();
        archive.Answer = _ => archive.Asked == 1 ? (true, null) : (false, "the source was busy");

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            // The leave the toolkit raises for a crossing: it carries what the
            // source allows, and no enter follows it.
            Raise(listing, DragDrop.DragLeaveEvent, data);
            Pump();

            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);
            Assert.Equal(1, archive.Asked);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- saying why ---------------------------------------------------------

    /// <summary>
    /// **A drag refused because a question failed now says so, in the log —
    /// once, and only when it has really gone.** The drag-over answered None
    /// because the source did not answer; the pointer crossed parts of the
    /// window that take no drop, twice, which raise leaves of their own; and
    /// then Windows ended the visit with the leave it sends in place of a
    /// drop, which arrives carrying no effects. That last one is the "nothing
    /// happened" the maintainer saw, and it left no trace at all. A first
    /// version wrote the line at the crossings too, and started counting
    /// again after each.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_refused_by_a_failure_says_why_once_when_it_leaves()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => (false, "the source was busy") };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            for (var crossing = 0; crossing < 2; crossing++)
            {
                Assert.Equal(DragDropEffects.None, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

                Raise(listing, DragDrop.DragLeaveEvent, data);
                Pump();
            }

            Assert.DoesNotContain(LogLines(), l => l.Contains("a drag ended", StringComparison.Ordinal));

            Assert.Equal(DragDropEffects.None, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            Raise(listing, DragDrop.DragLeaveEvent, data, allowed: DragDropEffects.None);
            Pump();

            var told = LogLines().Where(l => l.Contains("a drag ended with nothing taken", StringComparison.Ordinal)).ToList();

            Assert.True(told.Count == 1, $"expected one line for the drag, found {told.Count}");
            Assert.Contains(" warn ", told[0]);
            Assert.Contains("the source was busy", told[0]);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A failure the drag got past is not why it ended.** The first question
    /// fails, the next says yes, and the drop then takes nothing — which is
    /// the drop's own business to explain. Logging the drag's FIRST fault
    /// there, as a first version did, blamed a moment that had been survived.
    ///
    /// Two resets keep it so — each drag-over's and the drop's own — and each
    /// hides the other's mutation: only the pair reddens this.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failure_the_drag_got_past_is_not_blamed_for_its_end()
    {
        var into = Folder("into");
        var archive = new Archive();
        archive.Answer = _ => archive.Asked == 1 ? (false, "a moment ago") : (true, null);

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Assert.Equal(DragDropEffects.None, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);
            Assert.Equal(DragDropEffects.Copy, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            Raise(listing, DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(1, archive.Took);
            Assert.DoesNotContain(LogLines(), l => l.Contains("a moment ago", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    // ---- a descriptor is not an archive -----------------------------------

    /// <summary>
    /// **Explorer describes an ordinary file on disk the way it describes a
    /// zip's contents**, with FileGroupDescriptorW and FileContents beside the
    /// path — measured through Avalonia's own drop target in
    /// RealDropTargetTests. A Shift-drag of a file onto the folder it is in is
    /// refused by the path rules as already there; asked only whether a
    /// descriptor came with it, the cursor said Copy instead. The reader here
    /// says yes to everything, which is what Explorer's object says.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_that_carries_paths_is_never_taken_for_an_archive_s()
    {
        var into = Folder("into");
        var file = Path.Combine(into, "plain.txt");
        File.WriteAllText(file, "x");

        var archive = new Archive { Answer = _ => (true, null), Taking = () => [Staged("plain.txt")] };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var over = Raise(ListingOf(window, pane), DragDrop.DragOverEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Shift);

            Assert.Equal(DragDropEffects.None, over.DragEffects);
            Assert.Equal(0, archive.Asked);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The drop's own half, with no drag-over before it to have said no: the
    /// file is not taken out of an "archive" and pasted back beside itself —
    /// which is what the measured drop did, under "taking the files out of
    /// the archive…".
    /// </summary>
    [AvaloniaFact]
    public async Task A_drop_that_carries_paths_is_never_taken_for_an_archive_s()
    {
        var into = Folder("into");
        var file = Path.Combine(into, "plain.txt");
        File.WriteAllText(file, "x");

        var archive = new Archive { Answer = _ => (true, null), Taking = () => [Staged("plain.txt")] };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Shift);

            Assert.Equal(0, archive.Took);
            Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.Equal("that is already here", pane.Status);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- what the source allows ------------------------------------------------

    /// <summary>
    /// **A Shift-drag from a source that allows only Copy showed no-drop and
    /// dropped nothing.** OLE masks the answer with what the source allows
    /// and skips the drop when nothing is left — and 7-Zip, like most
    /// programs, allows Copy alone. The move falls back to the copy the source
    /// allows, and the drop does what the cursor said: copies, and leaves the
    /// original where it was.
    /// </summary>
    [AvaloniaFact]
    public async Task A_move_the_source_does_not_allow_is_a_copy()
    {
        var into = Folder("into");

        // Outside the temporary folder: a drop from inside it is an archiver's
        // scratch copy, which the drop rescues by MOVING it — see DropStaging —
        // and that would take the original for a reason unrelated to this.
        var from = Outside("from");

        var file = Path.Combine(from, "only-copy.txt");
        File.WriteAllText(file, "x");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var data = await Carrying(window, file);
            var listing = ListingOf(window, pane);

            var over = Raise(listing, DragDrop.DragOverEvent, data,
                             allowed: DragDropEffects.Copy, modifiers: KeyModifiers.Shift);

            Assert.Equal(DragDropEffects.Copy, over.DragEffects);

            var drop = Raise(listing, DragDrop.DropEvent, data,
                             allowed: DragDropEffects.Copy, modifiers: KeyModifiers.Shift);

            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);

            await Arrives(Path.Combine(into, "only-copy.txt"));

            Assert.True(File.Exists(file), "a source that allowed only a copy lost its original");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Alt asks for a shortcut; a Copy-only source gets a copy, and
    /// the cursor says so rather than no-drop.</summary>
    [AvaloniaFact]
    public async Task A_link_the_source_does_not_allow_is_a_copy()
    {
        var into = Folder("into");
        var from = Folder("from");

        var file = Path.Combine(from, "no-link.txt");
        File.WriteAllText(file, "x");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var over = Raise(ListingOf(window, pane), DragDrop.DragOverEvent, await Carrying(window, file),
                             allowed: DragDropEffects.Copy, modifiers: KeyModifiers.Alt);

            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The archive answer is held to the same rule: it is a Copy, and a source
    /// that allows no copy gets None rather than an answer OLE would throw
    /// away.
    /// </summary>
    [AvaloniaFact]
    public async Task Archive_files_are_offered_only_as_the_source_allows()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => (true, null) };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var over = Raise(ListingOf(window, pane), DragDrop.DragOverEvent, Words(), allowed: DragDropEffects.Move);

            Assert.Equal(DragDropEffects.None, over.DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The rule itself, both platforms. On Windows the incoming effect is the
    /// source's mask; X11 hands over the one action the source proposes and
    /// lets the target answer another, so there it limits nothing — a same-
    /// drive move from a file manager stays a move.
    /// </summary>
    [Fact]
    public void What_the_source_allows_limits_the_answer_only_where_it_is_a_mask()
    {
        Assert.Equal(DragDropEffects.Copy,
            MainWindow.Permitted(DragDropEffects.Move, DragDropEffects.Copy, masked: true));
        Assert.Equal(DragDropEffects.Move,
            MainWindow.Permitted(DragDropEffects.Move, DragDropEffects.Copy | DragDropEffects.Move, masked: true));
        Assert.Equal(DragDropEffects.None,
            MainWindow.Permitted(DragDropEffects.Link, DragDropEffects.Move, masked: true));
        Assert.Equal(DragDropEffects.Move,
            MainWindow.Permitted(DragDropEffects.Move, DragDropEffects.Copy, masked: false));

        // An event with no mask at all is one a test built by hand.
        Assert.Equal(DragDropEffects.Move,
            MainWindow.Permitted(DragDropEffects.Move, DragDropEffects.None, masked: true));
    }

    /// <summary>
    /// **What a finished move is called differs by platform, and None on X11
    /// is a rejection.** XdndFinished says "accepted" only when the action is
    /// not None, so reporting a move Vaktari performed as None made GTK play
    /// its failed-drop animation over a move that worked. Windows is the
    /// other way round: the shell wants anything but Move for a move the
    /// target did itself.
    /// </summary>
    [Fact]
    public void A_move_Vaktari_performs_is_reported_in_each_platform_s_words()
    {
        Assert.Equal(DragDropEffects.None, MainWindow.MovedByUs(windows: true));
        Assert.Equal(DragDropEffects.Move, MainWindow.MovedByUs(windows: false));
    }

    /// <summary>
    /// **What the drop was handed, and that it had gone, reaches the log.** The
    /// line was written for exactly this — an archiver clearing its temporary
    /// folder before the copy ran — and went only to stderr, which the
    /// windowed build does not have.
    /// </summary>
    [AvaloniaFact]
    public async Task A_dropped_path_already_gone_is_logged()
    {
        var into = Folder("into");
        var from = Folder("from");

        var file = Path.Combine(from, "vanishing.txt");
        File.WriteAllText(file, "x");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var data = await Carrying(window, file);

            File.Delete(file);

            Raise(ListingOf(window, pane), DragDrop.DropEvent, data, allowed: Everything);

            Assert.Contains(LogLines(), l => l.Contains(" warn ", StringComparison.Ordinal)
                                             && l.Contains("ALREADY GONE", StringComparison.Ordinal)
                                             && l.Contains("vanishing.txt", StringComparison.Ordinal));

            await Settled();
        }
        finally
        {
            window.Close();
        }
    }

    // ---- the effect handed back ----------------------------------------------

    /// <summary>
    /// **Avalonia starts a drop's effect at everything the source allowed**,
    /// and nothing set it — so a drop that took nothing reported Copy and Move
    /// back to the source. This one is refused (the archive reader says no,
    /// and there are no files) and must answer None.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drop_that_takes_nothing_reports_none()
    {
        var into = Folder("into");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(DragDropEffects.None, drop.DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A move Vaktari performs itself must not be reported as a Move.** The
    /// shell reads a Move answered from the drop as "the target copied, you
    /// delete the originals" — while Vaktari's own move of them may still be
    /// running. What it is, in the shell's words, is an optimized move, and
    /// the documented answer for one is anything but Move.
    /// </summary>
    [AvaloniaFact]
    public async Task A_move_Vaktari_performs_is_not_reported_as_a_move()
    {
        var into = Folder("into");
        var from = Folder("from");

        var file = Path.Combine(from, "moved.txt");
        File.WriteAllText(file, "x");

        var archive = new Archive();
        var (window, pane) = await Shown(into, archive);

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Shift);

            Assert.Equal(DragDropEffects.None, drop.DragEffects);

            // And the data object is told the same, which is the half of the
            // shell's optimized-move rule a source reads when it does not
            // trust the effect.
            Assert.Equal(1, archive.ToldMoved);

            // And it did move — so the None above is not a drop that refused.
            await Arrives(Path.Combine(into, "moved.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A copy is reported as the copy it was.</summary>
    [AvaloniaFact]
    public async Task A_copy_is_reported_as_a_copy()
    {
        var into = Folder("into");
        var from = Folder("from");

        var file = Path.Combine(from, "copied.txt");
        File.WriteAllText(file, "x");

        var archive = new Archive();
        var (window, pane) = await Shown(into, archive);

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Control);

            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);
            Assert.Equal(0, archive.ToldMoved);

            await Arrives(Path.Combine(into, "copied.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Shortcuts are reported as a Link. Outside the temporary folder, because
    /// a shortcut into it is skipped as one that would dangle — see
    /// AltDragShortcutTests.Outside.
    /// </summary>
    [AvaloniaFact]
    public async Task Shortcuts_are_reported_as_a_link()
    {
        var into = Outside("into");
        var from = Outside("from");

        var file = Path.Combine(from, "linked.txt");
        File.WriteAllText(file, "x");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Alt);

            Assert.Equal("created 1 shortcut(s)", pane.Status);
            Assert.Equal(DragDropEffects.Link, drop.DragEffects);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- where the drop lands ----------------------------------------------------

    /// <summary>
    /// **The drop goes into the row the ring was on.** Avalonia delivers the
    /// drop to the element the last drag-over was over, and the edge scroll
    /// can recycle that row's container for another item in between — the
    /// ring said one folder and the element now says another. Staged here on
    /// a real listing row by giving its container the other folder's entry
    /// between the drag-over and the drop, which is what recycling does to
    /// it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_drop_lands_in_the_listing_row_the_drag_over_marked()
    {
        var into = Folder("into");
        var marked = Directory.CreateDirectory(Path.Combine(into, "marked")).FullName;
        var recycled = Directory.CreateDirectory(Path.Combine(into, "recycled")).FullName;
        var from = Folder("from");

        var file = Path.Combine(from, "aimed.txt");
        File.WriteAllText(file, "x");

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            await pane.RefreshAsync();

            ListBoxItem? row = null;

            for (var i = 0; i < 300 && row is null; i++)
            {
                Pump();
                row = window.GetVisualDescendants().OfType<ListBoxItem>()
                    .FirstOrDefault(r => r.IsEffectivelyVisible && r.DataContext is FileEntry { Name: "marked" });
                if (row is null) await Task.Delay(10);
            }

            Assert.True(row is not null, "the listing drew no row for the folder");

            var other = pane.Entries.First(entry => entry.Name == "recycled");
            var data = await Carrying(window, file);

            Assert.Equal(DragDropEffects.Copy,
                Raise(row!, DragDrop.DragOverEvent, data, allowed: Everything, modifiers: KeyModifiers.Control).DragEffects);

            row!.DataContext = other;

            Raise(row, DragDrop.DropEvent, data, allowed: Everything, modifiers: KeyModifiers.Control);

            await Arrives(Path.Combine(marked, "aimed.txt"));

            Assert.False(File.Exists(Path.Combine(recycled, "aimed.txt")),
                "the drop went into the folder the row was recycled for");
        }
        finally
        {
            window.Close();
        }
    }

    // ---- the harness ----------------------------------------------------------

    private const DragDropEffects Everything =
        DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    /// <summary>
    /// The window's archive reader, answering as the test says. Stands where
    /// VirtualFileDrop stands on Windows — and on Linux, where the window has
    /// none, it is the only one.
    /// </summary>
    private sealed class Archive : IVirtualFileDrop
    {
        public Func<object, (bool Yes, string? Failure)> Answer { get; set; } = _ => (false, null);

        public Func<IReadOnlyList<string>> Taking { get; set; } = () => [];

        public int Asked { get; private set; }

        public int Took { get; private set; }

        public int ToldMoved { get; private set; }

        public bool MovedByTarget(object dataTransfer)
        {
            ToldMoved++;
            return true;
        }

        public bool Offers(object dataTransfer) => Offers(dataTransfer, out _);

        public bool Offers(object dataTransfer, out string? failure)
        {
            Asked++;

            var (yes, why) = Answer(dataTransfer);

            failure = why;
            return yes;
        }

        public IReadOnlyList<string> Take(object dataTransfer, CancellationToken token = default)
        {
            Took++;
            return Taking();
        }
    }

    private async Task<(MainWindow Window, PaneViewModel Pane)> Shown(string into, Archive archive)
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        window.Show();
        Pump();

        var shell = Own((ShellViewModel)window.DataContext!);
        var pane = shell.ActiveTab!;

        await pane.NavigateAsync(into);
        Pump();

        Assert.Equal(into, pane.CurrentPath);

        var field = typeof(MainWindow).GetField("_virtualDrop", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.True(field is not null, "MainWindow no longer keeps its archive reader where this test replaces it");

        field!.SetValue(window, archive);

        return (window, pane);
    }

    /// <summary>A drag carrying text and no files — which, to everything but
    /// the archive reader, is what a drag out of a zip looks like.</summary>
    private static DataTransfer Words()
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("not a file"));
        return data;
    }

    private static async Task<DataTransfer> Carrying(TopLevel top, string path)
    {
        var data = new DataTransfer();
        var item = await top.StorageProvider.TryGetFileFromPathAsync(path);

        Assert.True(item is not null, "the drop could not be given " + path);

        data.Add(DataTransferItem.CreateFile(item!));
        return data;
    }

    /// <summary>
    /// A drag event raised on a control so it bubbles to the window, with the
    /// effect starting where Avalonia starts it: at what the source allowed.
    /// </summary>
    private static DragEventArgs Raise(
        Control on, RoutedEvent<DragEventArgs> what, IDataTransfer data,
        DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var e = new DragEventArgs(what, data, on, new Point(on.Bounds.Width / 2, on.Bounds.Height / 2), modifiers)
        {
            DragEffects = allowed,
        };

        on.RaiseEvent(e);

        return e;
    }

    private static Control ListingOf(MainWindow window, PaneViewModel pane)
    {
        var listing = window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(l => ReferenceEquals(l.DataContext, pane) && l.IsEffectivelyVisible);

        Assert.True(listing is not null, "no visible listing carries the active pane");

        return listing!;
    }

    private string[] LogLines()
    {
        var path = Path.Combine(_logs, Log.FileName);

        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private string Folder(string what)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vaktari-archive-drop-{what}-{Guid.NewGuid():N}"[..48]);

        Directory.CreateDirectory(dir);
        _made.Add(dir);

        return dir;
    }

    /// <summary>A file where an archive reader would have written it: a
    /// folder of its own under the temporary directory.</summary>
    private string Staged(string name)
    {
        var path = Path.Combine(Folder("staged"), name);

        File.WriteAllText(path, "out of the archive");

        return path;
    }

    /// <summary>
    /// Outside the temporary folder, for the shortcut case. Asserted rather
    /// than assumed, for the reason AltDragShortcutTests.Outside gives.
    /// </summary>
    private string Outside(string what)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, $"vaktari-archive-drop-{what}-{Guid.NewGuid():N}"[..48]);

        Directory.CreateDirectory(dir);
        _made.Add(dir);

        Assert.False(Vaktari.Ui.Input.DropStaging.IsVolatile(dir, Path.GetTempPath()),
            "the test host runs from inside the temporary folder, so a shortcut from here is skipped");

        return dir;
    }

    /// <summary>
    /// Waits for a file the drop's operation puts in place, and for the pane's
    /// own refresh after it — which lands from a pool continuation, and one
    /// landing after the window has closed is the leak OwnedViewModels exists
    /// for.
    /// </summary>
    private static async Task Arrives(string path)
    {
        for (var i = 0; i < 500 && !File.Exists(path); i++)
        {
            await Task.Delay(10);
            Pump();
        }

        Assert.True(File.Exists(path), "the drop's files never arrived at " + path);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(10);
            Pump();
        }
    }

    /// <summary>Lets an operation that is going to fail finish failing before
    /// the window closes.</summary>
    private static async Task Settled()
    {
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(10);
            Pump();
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }
}
