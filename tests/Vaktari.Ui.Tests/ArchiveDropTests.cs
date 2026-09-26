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
    /// </summary>
    [AvaloniaFact]
    public async Task A_drop_that_throws_says_so_on_the_status_line()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => throw new InvalidOperationException("the reader broke") };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.StartsWith("that drop failed", pane.Status);
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
    /// The other side of the same rule: a leave that nothing follows IS the
    /// drag going, and what it learned goes with it — so a drop that later
    /// arrives, with nothing of its own to go on, is not handed a stale yes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_that_leaves_forgets_its_yes()
    {
        var into = Folder("into");
        var archive = new Archive { Taking = () => [Staged("stale.txt")] };
        archive.Answer = _ => archive.Asked == 1 ? (true, null) : (false, null);

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Raise(listing, DragDrop.DragOverEvent, data);
            Raise(listing, DragDrop.DragLeaveEvent, data);
            Pump();

            // What the refusal reports back is A_drop_that_takes_nothing_reports_none's
            // business; this is about what the drop was left to go on.
            Raise(listing, DragDrop.DropEvent, Words(), allowed: Everything);

            Assert.Equal(0, archive.Took);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- saying why ---------------------------------------------------------

    /// <summary>
    /// **A drag refused because a question failed now says so, in the log.**
    /// The drag-over answered None because the source did not answer, and the
    /// drag then left — which is what Windows does in place of a drop. That is
    /// the "nothing happened" the maintainer saw, and it left no trace at all.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drag_refused_by_a_failure_says_why_when_it_leaves()
    {
        var into = Folder("into");
        var archive = new Archive { Answer = _ => (false, "the source was busy") };

        var (window, pane) = await Shown(into, archive);

        try
        {
            var data = Words();
            var listing = ListingOf(window, pane);

            Assert.Equal(DragDropEffects.None, Raise(listing, DragDrop.DragOverEvent, data).DragEffects);

            Raise(listing, DragDrop.DragLeaveEvent, data);
            Pump();

            Assert.Contains(LogLines(), l => l.Contains(" warn ", StringComparison.Ordinal)
                                             && l.Contains("a drag ended with nothing taken", StringComparison.Ordinal)
                                             && l.Contains("the source was busy", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
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

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Shift);

            Assert.Equal(DragDropEffects.None, drop.DragEffects);

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

        var (window, pane) = await Shown(into, new Archive());

        try
        {
            var drop = Raise(ListingOf(window, pane), DragDrop.DropEvent, await Carrying(window, file),
                             allowed: Everything, modifiers: KeyModifiers.Control);

            Assert.Equal(DragDropEffects.Copy, drop.DragEffects);

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
