using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.Input;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Alt+drag, Explorer's other way of asking for a shortcut.
///
/// **Only one of Explorer's two shortcut gestures was implemented, and it was
/// the two-key one.** <c>DragEffect.For</c> took control and shift and no
/// alt, so Alt+drag arrived at the rule with no modifier set at all and was
/// answered by the volume default — which MOVES a file dragged inside a drive.
/// The gesture whose whole point is that the original stays put was the one
/// most likely to take it away, and the F1 sheet named neither spelling, so
/// there was nothing on screen to say which of the two the application knew.
///
/// Four separate things have to be true and each fails on its own: the rule has
/// to read alt, the window has to take Alt off the drag's modifiers and hand it
/// over, the drop has to turn that into a file on disk, and the sheet has to
/// say so. A fifth is not ours to make true and is asked of Avalonia as a
/// guard — whether a real drag reports Alt at all.
///
/// **Building a MainWindow moves four of the pane's statics off null and
/// closing it puts none of them back.** WindowServices assigns the platform's
/// search, places and shortcut providers and the trash maintenance, and this
/// class sorts near the front of the assembly — measured, a probe class placed
/// after it read back <c>search=WindowsSearchProvider</c>, so every later class
/// that loaded a search listing would have run a real recursive walk of the
/// machine. Search is the one that costs, so it is borrowed and given back the
/// way <see cref="DragGhostTests"/> borrows it.
///
/// **And <c>window.Close()</c> is not teardown, which is why the shell is owned
/// as well.** OnClosing cancels the close, awaits the session flush and closes
/// again from the continuation, so nothing has been torn down by the time the
/// test method returns — measured: a probe subscribed to <c>Closed</c>, called
/// <c>Close()</c>, pumped, and read <c>closedFired=False</c> with the pane's
/// watcher still a live FileSystemWatcher. The shell goes to
/// <see cref="OwnedViewModels.Own{T}"/> so the panes, their watcher, their two
/// timers and the git task are disposed at the end of the test instead — the
/// same probe read that watcher back as null once the shell was owned. The
/// window itself cannot be owned: MainWindow is not IDisposable and
/// <c>Own(window)</c> is CS0311, measured by compiling it.
/// </summary>
public sealed class AltDragShortcutTests : OwnedViewModels
{
    // The same shape as ExplorerConventionTests uses: two paths on one volume
    // and one on another, so "what alt did" can be told apart from "what the
    // volume rule would have said anyway".
    private static readonly string OnC = OperatingSystem.IsWindows() ? @"C:\work.txt" : "/work/a.txt";
    private static readonly string AlsoC = OperatingSystem.IsWindows() ? @"C:\other" : "/other";
    private static readonly string OnD = OperatingSystem.IsWindows() ? @"D:\backup" : "/mnt/backup";

    // ---- the rule ----------------------------------------------------------

    /// <summary>
    /// **Alt alone is a shortcut, and the case that proves it is the one inside
    /// a single drive.** That is where the volume default answers Move, so an
    /// alt that is not read does not merely fail to make a shortcut — it takes
    /// the original away. The cross-drive and external cases are here because
    /// they would have answered Copy, which is quieter and just as wrong.
    /// </summary>
    [Fact]
    public void Alt_alone_means_a_shortcut()
    {
        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: false, shift: false, alt: true, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: false, shift: false, alt: true, internalDrag: true, [OnC], OnD));

        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: false, shift: false, alt: true, internalDrag: false, [OnC], AlsoC));
    }

    /// <summary>
    /// **Held with either of the others, alt still wins — and this test IS the
    /// decision, not a measurement of Explorer.** The four gestures Explorer
    /// documents are Ctrl+Shift or Alt for a link, Ctrl for a copy, Shift for a
    /// move; what the shell does with Ctrl+Alt was not checked, because running
    /// the shell is the only way to find out and that is not available from
    /// here. So alt is read as the deliberate key and decides, which is the same
    /// ordering that puts the Ctrl+Shift chord above its own halves. Written
    /// down here so a later measurement has one line to argue with rather than
    /// a rule to reverse-engineer.
    /// </summary>
    [Fact]
    public void Alt_outranks_the_modifiers_held_with_it()
    {
        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: true, shift: false, alt: true, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: false, shift: true, alt: true, internalDrag: true, [OnC], AlsoC));

        Assert.Equal(
            DragIntent.Link,
            DragEffect.For(control: true, shift: true, alt: true, internalDrag: true, [OnC], AlsoC));
    }

    // ---- the window ---------------------------------------------------------

    /// <summary>
    /// **The modifier has to survive the trip from the drag to the rule.** The
    /// rule above can be right and the gesture still dead: the window takes the
    /// modifiers off the drag one flag at a time, and alt was not among the
    /// flags it took. So this drives the real handler on a real window and reads
    /// the cursor it sets — and the unmodified drag beside it is what says the
    /// Link came from the key rather than from the destination.
    ///
    /// This is the cursor only. What the release then does is the next test:
    /// the two are separate handlers, and a cursor that promises a shortcut
    /// over a drop that moves the file is the worse of the two failures.
    /// </summary>
    [AvaloniaFact]
    public async Task Alt_over_the_listing_offers_a_shortcut()
    {
        var carried = Temp();
        var into = Temp();

        var file = Path.Combine(carried, "notes.txt");
        File.WriteAllText(file, "x");

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            // Owned rather than left to Close(), which defers: see the class note.
            var pane = Own(ShellOf(window)).ActiveTab!;

            await pane.NavigateAsync(into);
            Pump();

            Assert.Equal(into, pane.CurrentPath);

            // The Link would be downgraded to Copy on a platform with no idea
            // of a shortcut, and this suite runs where there is one — said out
            // loud so a null maker reads as "this machine cannot answer the
            // question" rather than as the gesture being broken.
            Assert.True(
                typeof(MainWindow)
                    .GetField("_shortcuts", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window) is not null,
                "this window has no shortcut maker, so every Link is downgraded to Copy "
                + "and nothing below can tell the two gestures apart");

            var listing = ListingOf(window, pane);
            var data = await Carrying(window, file);

            var alt = Raise(window, listing, DragDrop.DragOverEvent, data, KeyModifiers.Alt);

            Assert.Equal(DragDropEffects.Link, alt.DragEffects);

            // And without it the same drag onto the same folder is anything but
            // a shortcut — so the Link above is the key's doing.
            var plain = Raise(window, listing, DragDrop.DragOverEvent, data, KeyModifiers.None);

            Assert.NotEqual(DragDropEffects.Link, plain.DragEffects);
        }
        finally
        {
            window.Close();
            Delete(carried);
            Delete(into);
        }
    }

    /// <summary>
    /// **The cursor that promises a shortcut and the drop that makes one are
    /// two branches, and only the second leaves anything on disk.** DragOver
    /// answers with a cursor; OnDrop asks the same rule again and hands Link to
    /// the platform's shortcut maker. Nothing anywhere in this suite dropped
    /// onto a listing at all, so that second branch was reasoned about and
    /// never run — and a gesture that shows the right cursor and then moves the
    /// file is worse than one that never offered.
    ///
    /// The original still being there is the assertion this finding is really
    /// about. Alt+drag inside one drive did not merely fail to make a shortcut:
    /// it fell through to the volume rule and MOVED the file.
    ///
    /// Nothing is waited for. CreateShortcuts is a plain loop on the handler's
    /// own thread, so by the time RaiseEvent returns the maker has either run
    /// or thrown — there is no later moment for this to be true in.
    /// </summary>
    [AvaloniaFact]
    public async Task Alt_dropped_on_the_listing_makes_a_shortcut_and_keeps_the_original()
    {
        var carried = Outside("from");
        var into = Outside("into");

        var file = Path.Combine(carried, "notes.txt");
        File.WriteAllText(file, "x");

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            // Owned rather than left to Close(), which defers: see the class note.
            var pane = Own(ShellOf(window)).ActiveTab!;

            await pane.NavigateAsync(into);
            Pump();

            Assert.Equal(into, pane.CurrentPath);

            var listing = ListingOf(window, pane);
            var data = await Carrying(window, file);

            Raise(window, listing, DragDrop.DropEvent, data, KeyModifiers.Alt);

            // Said by the handler itself, and it distinguishes the two ways of
            // making nothing: a skipped volatile source counts separately, so
            // this cannot pass on a drop that quietly made no shortcut.
            Assert.Equal("created 1 shortcut(s)", pane.Status);

            var landed = Directory.GetFileSystemEntries(into);

            Assert.Single(landed);

            // Whatever this platform calls a shortcut, it is not the file: a
            // copy or a move would have put "notes.txt" here as a plain file.
            // Windows makes a .lnk under another name; Linux makes a link
            // under the same one — measured on the first Linux run, where
            // the name alone called a correct symlink a copy.
            Assert.True(
                Path.GetFileName(landed[0]) != "notes.txt" || new FileInfo(landed[0]).LinkTarget is not null,
                "a plain file named notes.txt landed, which is a copy or a move, not a shortcut");

            // Written down rather than left implied, and honestly labelled: once
            // the status line above holds, this CANNOT fail — the Link branch
            // returns before Paste is ever reached, so nothing was in a position
            // to take the original. It is here because it is the sentence the
            // finding was about, and a reader arriving at this test should not
            // have to go and check that for themselves.
            Assert.True(
                File.Exists(file),
                "the original is gone, which is the move this gesture was doing before "
                + "instead of the shortcut it was asked for");
        }
        finally
        {
            window.Close();
            Delete(carried);
            Delete(into);
        }
    }

    /// <summary>
    /// **The cursor and the drop asked the same reader two different questions,
    /// and the half the OS obeys was the one that said no.** Both handlers call
    /// DroppedFileReader.Read, which is told copy-or-move and strips every path
    /// already living in the destination when the answer is "move". OnDragOver
    /// asked it <c>effect == DragDropEffects.Copy</c> — false for a Link — so a
    /// shortcut asked for beside the original was filtered down to nothing and
    /// the cursor answered None. OnDrop asked it <c>!move</c> — true for a Link
    /// — and kept every path. Measured with this test against the handler as it
    /// stood: the DragOver line below came back None where it asserts Link, and
    /// the run stopped there. A real OLE drag obeys the cursor, so whatever the
    /// drop would have done was unreachable — which is why the drop is driven
    /// here too rather than trusted.
    ///
    /// This is the case the F1 sheet's new line advertises most: Alt+drag onto
    /// the folder a file already lives in is how Explorer puts "X - Shortcut"
    /// beside it, and it is the one spelling of the gesture that does not need
    /// a second folder on screen.
    ///
    /// Both halves are driven here rather than in two tests, because the defect
    /// was the DISAGREEMENT between them — a test of either alone stayed green
    /// through it.
    ///
    /// The drag is marked as this application's own, which is what a real
    /// Alt+drag inside one folder is. It also sharpens the unmodified control
    /// beside it: an internal drag within one drive is a MOVE, and a move onto
    /// the folder the file is already in is the no-op the reader exists to
    /// refuse — so the None below fails if the Link was bought by handing the
    /// reader "copying" unconditionally.
    /// </summary>
    [AvaloniaFact]
    public async Task Alt_onto_the_folder_the_file_already_lives_in_still_makes_a_shortcut()
    {
        var home = Outside("home");

        var file = Path.Combine(home, "notes.txt");
        File.WriteAllText(file, "x");

        UseSearch(PaneViewModel.Search);

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            // Owned rather than left to Close(), which defers: see the class note.
            var pane = Own(ShellOf(window)).ActiveTab!;

            await pane.NavigateAsync(home);
            Pump();

            Assert.Equal(home, pane.CurrentPath);

            var listing = ListingOf(window, pane);
            var data = await Carrying(window, file);

            StartedHere(window);

            // The cursor first, and it is the half that was refusing: the
            // toolkit never delivers a drop the drag-over answered None.
            var alt = Raise(window, listing, DragDrop.DragOverEvent, data, KeyModifiers.Alt);

            Assert.Equal(DragDropEffects.Link, alt.DragEffects);

            // And the same drag with no key held stays refused: within one
            // drive that is a move, and moving a file into the folder it is
            // already in achieves nothing. The Link above must be alt's doing
            // and not the already-here rule being swept away for everybody.
            var plain = Raise(window, listing, DragDrop.DragOverEvent, data, KeyModifiers.None);

            Assert.Equal(DragDropEffects.None, plain.DragEffects);

            Raise(window, listing, DragDrop.DropEvent, data, KeyModifiers.Alt);

            Assert.Equal("created 1 shortcut(s)", pane.Status);

            var landed = Directory.GetFileSystemEntries(home);

            // The original AND something that is not it: this is the one drop
            // where the two share a folder, so counting is not enough.
            Assert.Contains(landed, p => Path.GetFileName(p) == "notes.txt");
            Assert.Contains(landed, p => Path.GetFileName(p) != "notes.txt");
        }
        finally
        {
            window.Close();
            Delete(home);
        }
    }

    /// <summary>
    /// **A GUARD: nothing in this repository can make it fail.** It asks
    /// Avalonia's Win32 backend a machine fact the gesture rests on and that no
    /// headless test can otherwise reach — whether a drag in progress reports
    /// Alt at all. A drop target is handed the button and modifier state as a
    /// Win32 <c>grfKeyState</c> bitmask, and only what
    /// <c>OleDropTarget.ConvertKeyState</c> keeps out of it ever reaches
    /// <c>DragEventArgs.KeyModifiers</c>. If Alt were dropped there, everything
    /// above this line could be green and Alt+drag would still be dead on a
    /// real desktop.
    ///
    /// Asked rather than assumed because the answer was not obvious: MK_ALT is
    /// 0x20, it is the one modifier bit Win32 does NOT define beside the mouse
    /// buttons in the obvious order, and plenty of drag implementations read
    /// only shift and control. Measured here at 12.1.0: shift, control and alt
    /// all survive.
    ///
    /// It will fail on an Avalonia upgrade that renames the method or stops
    /// mapping the bit — which is the only time anybody would want to hear
    /// about it, and is exactly why it reflects on the name rather than
    /// hard-coding a remembered answer.
    /// </summary>
    [WindowsFact]
    public void The_backend_hands_alt_to_a_drag_at_all()
    {
        var win32 = Path.Combine(AppContext.BaseDirectory, "Avalonia.Win32.dll");

        Assert.True(File.Exists(win32),
            "Avalonia.Win32 is not beside the test host, so the backend that "
            + "delivers a real drag cannot be asked what it keeps");

        var convert = Assembly.LoadFrom(win32)
            .GetType("Avalonia.Win32.OleDropTarget")
            ?.GetMethod("ConvertKeyState", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.True(convert is not null,
            "Avalonia.Win32.OleDropTarget.ConvertKeyState is gone, so the step "
            + "that turns a drag's Win32 key state into KeyModifiers can no "
            + "longer be checked and has to be re-measured by hand");

        // MK_SHIFT, MK_CONTROL, MK_ALT out of winuser.h. The first two are
        // here as the control: they are known to work, so all three coming
        // back None would say the call was misread rather than that alt is
        // dropped.
        Assert.Equal(RawInputModifiers.Shift, convert!.Invoke(null, [0x0004]));
        Assert.Equal(RawInputModifiers.Control, convert.Invoke(null, [0x0008]));
        Assert.Equal(RawInputModifiers.Alt, convert.Invoke(null, [0x0020]));
    }

    // ---- the sheet ----------------------------------------------------------

    /// <summary>
    /// **A gesture nothing prints is a gesture nobody finds.** Neither spelling
    /// was on the F1 sheet, so the one that worked was as invisible as the one
    /// that did not. Both are printed on one line, the way "Menu / Shift+F10"
    /// is: two habits for one verb.
    ///
    /// Neither of the sheet's own cross-checks can reach this line — the listed
    /// side drops any entry containing "drag", and the bound side is built from
    /// KeyBindings, which no pointer gesture is — so it is checked here or
    /// nowhere.
    /// </summary>
    [Fact]
    public void The_sheet_prints_both_ways_to_ask_for_a_shortcut()
    {
        var dragging = Shortcuts.All.Single(g => g.Name == "Dragging");

        var line = dragging.Keys.SingleOrDefault(
            k => k.Does.Contains("shortcut", StringComparison.OrdinalIgnoreCase));

        Assert.True(line is not null,
            "the Dragging section of the F1 sheet says nothing about shortcuts, so the "
            + "only way to find the gesture is to already know it");

        var spellings = line!.Keys.Split(" / ", StringSplitOptions.TrimEntries);

        Assert.Contains("Alt+drag", spellings);
        Assert.Contains("Ctrl+Shift+drag", spellings);
    }

    // ---- the harness --------------------------------------------------------

    private static string Temp()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "vaktari-altdrag-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(dir);

        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>
    /// Marks the drag as one this window started, which is what a drag between
    /// two folders of Vaktari's own is.
    ///
    /// The field is per window rather than the static beside it, so nothing has
    /// to be given back — the window is closed at the end of the test that set
    /// it. <see cref="DragGhostTests"/> reaches for the static one the same way,
    /// and does give that one back.
    /// </summary>
    private static void StartedHere(MainWindow window)
    {
        var flag = typeof(MainWindow)
            .GetField("_internalDrag", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.True(flag is not null,
            "MainWindow no longer records that a drag started in this window, so a "
            + "test cannot tell an internal drag from one arriving out of Explorer");

        flag!.SetValue(window, true);
    }

    private static ShellViewModel ShellOf(MainWindow window)
        => (ShellViewModel)typeof(MainWindow)
            .GetProperty("Shell", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    /// <summary>
    /// A control the window's drop rules read as the pane's own listing.
    ///
    /// Found by data context rather than by name, because that is the question
    /// the window itself asks — PaneAt walks up from whatever the pointer hit
    /// looking for a control whose DataContext is a pane. A tab strip item
    /// carries one too and is deliberately not eligible: hovering the strip
    /// switches tabs, which would move the destination out from under the test.
    /// </summary>
    private static Control ListingOf(MainWindow window, PaneViewModel pane)
    {
        var listing = window.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(l => ReferenceEquals(l.DataContext, pane));

        Assert.True(listing is not null,
            "no listing in the window carries the active pane, so there is nowhere "
            + "to drop that the drag rules would call a folder");

        return listing!;
    }

    /// <summary>
    /// A drop carrying these paths, built the way the window's own drag builds
    /// one — through the StorageProvider, which the headless top level answers
    /// with real Bcl storage items.
    /// </summary>
    private static async Task<DataTransfer> Carrying(TopLevel top, params string[] paths)
    {
        var data = new DataTransfer();

        foreach (var path in paths)
        {
            IStorageItem? item = Directory.Exists(path)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                : await top.StorageProvider.TryGetFileFromPathAsync(path);

            Assert.True(item is not null, "the drop could not be given " + path);

            data.Add(DataTransferItem.CreateFile(item!));
        }

        return data;
    }

    /// <summary>
    /// A drag event on the listing, raised from the listing so it bubbles to
    /// the window's own handler — which is where the modifiers are read.
    ///
    /// The routed event is a parameter because the cursor and the drop are two
    /// different handlers reached the same way, and a helper that could only
    /// ask for one of them is how the drop went untested.
    /// </summary>
    private static DragEventArgs Raise(
        MainWindow window, Control on, RoutedEvent<DragEventArgs> what,
        DataTransfer data, KeyModifiers modifiers)
    {
        var centre = new Point(on.Bounds.Width / 2, on.Bounds.Height / 2);
        var point = on.TranslatePoint(centre, window) ?? centre;

        var e = new DragEventArgs(what, data, on, point, modifiers);

        on.RaiseEvent(e);

        return e;
    }

    /// <summary>
    /// A working folder that is NOT inside <c>Path.GetTempPath()</c>.
    ///
    /// **The drop skips volatile paths, and every folder this suite normally
    /// builds is one.** CreateShortcuts asks DropStaging.IsVolatile of each
    /// dropped path and passes over the ones under the temporary folder — a
    /// shortcut into an archiver's scratch directory dangles the moment the
    /// archiver tidies up. A source built the usual way, in temp, is therefore
    /// skipped and the drop leaves nothing behind, for a reason that has
    /// nothing to do with alt. The test host's own directory is the nearest
    /// writable place outside temp, and the property is asserted rather than
    /// assumed because a host that ran from temp would make this test lie.
    /// </summary>
    private static string Outside(string what)
    {
        var dir = Path.Combine(
            AppContext.BaseDirectory,
            "vaktari-altdrop-" + what + "-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(dir);

        Assert.False(
            DropStaging.IsVolatile(dir, Path.GetTempPath()),
            "the test host runs from inside the temporary folder, so a drop from here is "
            + "skipped as volatile — which would look exactly like alt going unread");

        return dir;
    }
}
