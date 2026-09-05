using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The menu route to a shortcut.
///
/// **IShortcutMaker.CreateShortcut is implemented on both platforms and had one
/// caller: the right-drag drop menu.** So the only way to make a shortcut was
/// to pick an item up with the RIGHT mouse button, drag it somewhere else, and
/// choose from the menu the drop puts up — a gesture nothing in the window
/// mentions — and there was no way at all to ask for one beside the item it
/// points at. On Linux there was no menu route of any kind.
///
/// On Windows the desktop's own "Create shortcut" is offered two hovers deep
/// under "Windows menu", and it stays there: it lands the .lnk beside the item
/// rather than in the folder on screen, it is invisible to Ctrl+Z, and it is
/// the only one of the two in a search listing and in This PC, where the row
/// these tests drive is hidden for want of a folder to write into.
///
/// These drive the command rather than the row, with the row's binding pinned
/// separately at the bottom: a command nothing calls and a row bound to
/// nothing fail in exactly the same way from the outside, and only one test
/// each can tell them apart.
/// </summary>
public sealed class CreateShortcutTests : OwnedViewModels
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-shortcut-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly IShortcutMaker? _before = PaneViewModel.Shortcuts;
    private readonly Maker _maker = new();

    public CreateShortcutTests()
    {
        Directory.CreateDirectory(_root);
        PaneViewModel.Shortcuts = _maker;
    }

    /// <summary>
    /// The seam is a static, so it is given back rather than nulled: one class
    /// in this assembly builds a real MainWindow, which assigns the platform's
    /// own maker to it, and a test that left null behind would take the real
    /// one away from everything that ran afterwards.
    /// </summary>
    public override void Dispose()
    {
        PaneViewModel.Shortcuts = _before;

        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_root, name);

    private string File_(string name)
    {
        var path = At(name);
        File.WriteAllText(path, "x");
        return path;
    }

    private PaneViewModel Pane(string? at = null)
        => Own(new PaneViewModel(new Inert(), _ops) { CurrentPath = at ?? _root });

    private readonly Recording _ops = new();

    private static FileEntry Row(string path)
        => new(Path.GetFileName(path), path, 1, DateTimeOffset.UnixEpoch, EntryFlags.None);

    // ---- what it makes -----------------------------------------------------

    /// <summary>
    /// One shortcut per selected row, in the folder on screen — which is where
    /// Explorer's own verb puts them, and the difference from the drag, whose
    /// destination is wherever the button was let go.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_selected_row_gets_a_shortcut_in_the_folder_on_screen()
    {
        var one = File_("report.pdf");
        var two = File_("notes.txt");

        var pane = Pane();

        pane.SelectedEntries.Add(Row(one));
        pane.SelectedEntries.Add(Row(two));

        await pane.CreateShortcutAsync();

        Assert.Equal([(one, _root), (two, _root)], _maker.Made);
    }

    /// <summary>
    /// **A create that writes straight to the filesystem is invisible to the
    /// undo history**, which is the fault New folder already carries a comment
    /// about: Ctrl+Z straight after it did nothing at all. The landing path is
    /// what gets recorded, not the target — undoing puts the SHORTCUT in the
    /// bin, and binning the thing it points at instead would be a data loss
    /// dressed as an undo.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_shortcut_is_recorded_so_Ctrl_Z_takes_it_back()
    {
        var target = File_("report.pdf");

        var pane = Pane();
        pane.SelectedEntry = Row(target);

        await pane.CreateShortcutAsync();

        Assert.Equal([At("report.pdf - Shortcut")], _ops.Created);
    }

    [AvaloniaFact]
    public async Task It_says_how_many_it_made()
    {
        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        await pane.CreateShortcutAsync();

        Assert.Equal("created 1 shortcut", pane.Status);

        pane.SelectedEntries.Add(Row(File_("a.txt")));
        pane.SelectedEntries.Add(Row(File_("b.txt")));

        await pane.CreateShortcutAsync();

        Assert.Equal("created 2 shortcuts", pane.Status);
    }

    /// <summary>
    /// **The listing is reloaded, so the shortcut is on screen.** A shortcut
    /// written straight to disk is not a change the pane knows about: the
    /// folder watcher will get there eventually, and until it does the row the
    /// command just made is missing from the folder it was made in.
    /// </summary>
    [AvaloniaFact]
    public async Task The_listing_is_reloaded_so_the_new_shortcut_is_on_screen()
    {
        var listing = new Inert();

        var pane = Own(new PaneViewModel(listing, _ops) { CurrentPath = _root });
        pane.SelectedEntry = Row(File_("report.pdf"));

        // The navigation the constructor's initialiser started, drained — the
        // count below is about the refresh alone.
        Dispatcher.UIThread.RunJobs();

        var before = listing.Listings;

        await pane.CreateShortcutAsync();

        Assert.True(listing.Listings > before, "nothing asked the folder for its rows again");
    }

    /// <summary>
    /// **The message is written after the reload, not before it.** A finished
    /// listing blanks the status line deliberately — Summary already shows the
    /// item count and the two sat side by side saying the same thing — so a
    /// message set first survives only until the load lands, which on a folder
    /// this small is immediately.
    /// </summary>
    [AvaloniaFact]
    public async Task The_message_outlives_the_reload_the_new_file_needs()
    {
        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        await pane.CreateShortcutAsync();

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("created 1 shortcut", pane.Status);
    }

    /// <summary>
    /// A writer that throws is other people's failure — the shell refusing a
    /// .lnk, a read-only folder — and it belongs on the status line rather than
    /// on the way out of a menu click, where nothing catches it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_writer_that_throws_is_reported_rather_than_thrown()
    {
        var doomed = File_("report.pdf");

        _maker.Refuses = doomed;

        var pane = Pane();
        pane.SelectedEntry = Row(doomed);

        await pane.CreateShortcutAsync();

        Assert.Contains("shortcut", pane.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("created", pane.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refusal stops the run and keeps what was already written.
    ///
    /// Both halves matter and neither is obvious. Carrying on would collect the
    /// same answer once per row — the first refusal is usually about the FOLDER
    /// — and rolling the earlier ones back would delete files the user asked
    /// for in order to report a failure at a later one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_refusal_stops_the_run_and_keeps_what_was_already_made()
    {
        var first = File_("a.txt");
        var doomed = File_("b.txt");
        var third = File_("c.txt");

        _maker.Refuses = doomed;

        var pane = Pane();

        pane.SelectedEntries.Add(Row(first));
        pane.SelectedEntries.Add(Row(doomed));
        pane.SelectedEntries.Add(Row(third));

        await pane.CreateShortcutAsync();

        Assert.True(File.Exists(At("a.txt - Shortcut")), "the one already written was taken back");
        Assert.False(File.Exists(At("c.txt - Shortcut")), "it carried on past the refusal");
        Assert.DoesNotContain("created", pane.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **A row can belong to a folder that is not the one on screen.** The
    /// details listing splices an expanded folder's children in underneath it,
    /// and those rows go into the same selection as everything else — so
    /// "the folder being looked at" and "beside the item" are two different
    /// places, and this one is the first. It is the rule Duplicate follows,
    /// and the one difference from the shell's own verb on Windows, which puts
    /// the .lnk beside the item instead.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_from_an_expanded_subfolder_lands_in_the_folder_on_screen()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        var inside = Path.Combine(sub, "report.pdf");
        File.WriteAllText(inside, "x");

        var pane = Pane();
        pane.SelectedEntry = Row(inside);

        await pane.CreateShortcutAsync();

        Assert.Equal([(inside, _root)], _maker.Made);
    }

    /// <summary>
    /// **The writing is not done on the UI thread.** Measured against the real
    /// writer on this machine — a throwaway Windows test, since deleted — 200
    /// shortcuts into one folder took 1907 ms, 9.5 ms each; a Ctrl+A over ten
    /// thousand rows and one click on this row is a minute and a half at that
    /// rate, and every millisecond of it would have been a window that does not
    /// repaint. The drag route has the same synchronous shape but needs the
    /// whole selection physically dragged; a menu row is two clicks from
    /// Ctrl+A.
    ///
    /// The precondition is asserted rather than assumed: if the test body ever
    /// stopped running on the UI thread the rest would pass for the wrong
    /// reason.
    /// </summary>
    [AvaloniaFact]
    public async Task The_writing_is_done_off_the_UI_thread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess(), "the test itself is not on the UI thread");

        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        await pane.CreateShortcutAsync();

        Assert.False(_maker.OnUiThread, "the shell writer ran on the dispatcher");
    }

    /// <summary>
    /// **The message lands back on the UI thread**, which is what the
    /// ConfigureAwait(true) on the refresh is for: LoadAsync awaits with
    /// ConfigureAwait(false), so a listing that gives the thread up comes back
    /// on a pool thread and everything after it would follow.
    ///
    /// The listing double has to give the thread up for this to be visible at
    /// all — Inert's completes synchronously, and an await on a finished task
    /// never leaves the thread it started on whatever it was told about the
    /// context.
    /// </summary>
    [AvaloniaFact]
    public async Task The_message_is_written_back_on_the_UI_thread()
    {
        var pane = Own(new PaneViewModel(new Slow(), _ops) { CurrentPath = _root });
        pane.SelectedEntry = Row(File_("report.pdf"));

        bool? onUi = null;

        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.Status)
                && pane.Status.StartsWith("created", StringComparison.Ordinal))
                onUi ??= Dispatcher.UIThread.CheckAccess();
        };

        await pane.CreateShortcutAsync();

        Assert.True(onUi, "the status line was written from a pool thread");
    }

    /// <summary>
    /// **The phrase handed to Failures.Describe is part of the message.** It is
    /// read by one arm — UnauthorizedAccessException — and that is the likeliest
    /// real failure here: a folder somebody may read and not write. Without
    /// this the phrase could be dropped and every other test would stay green,
    /// with the shipped message quietly degrading to "…to do that". The same
    /// message regressed once before, six methods up: NewFileAsync's catch
    /// carries the note.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_it_may_not_write_to_names_what_was_refused()
    {
        var doomed = File_("report.pdf");

        _maker.Refuses = doomed;
        _maker.Refusal = new UnauthorizedAccessException();

        var pane = Pane();
        pane.SelectedEntry = Row(doomed);

        await pane.CreateShortcutAsync();

        Assert.Equal("you do not have permission to make that shortcut", pane.Status);
    }

    /// <summary>
    /// **A pane with no operations still writes the shortcuts.** _ops is
    /// nullable — the constructor takes it as an optional argument and
    /// DuplicateSelected and TrashSelected both treat null as a real state —
    /// and the obvious "create, then record" line, <c>_ops?.RecordCreation(
    /// maker.CreateShortcut(path, into))</c>, does not do that: <c>?.</c>
    /// short-circuits the whole invocation, its arguments included, so a null
    /// there skipped the create as well and the count still went up. The
    /// recording is a separate statement outside the loop for that reason, and
    /// this is the test that can see the difference.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pane_with_no_undo_history_still_writes_the_shortcuts()
    {
        var target = File_("report.pdf");

        var pane = Own(new PaneViewModel(new Inert()) { CurrentPath = _root });
        pane.SelectedEntry = Row(target);

        await pane.CreateShortcutAsync();

        Assert.Equal([(target, _root)], _maker.Made);
        Assert.Equal("created 1 shortcut", pane.Status);
    }

    // ---- where it refuses --------------------------------------------------

    /// <summary>
    /// The row is hidden with nothing selected, so this is only reachable by
    /// executing the command directly — and "created 0 shortcuts" is what it
    /// said without the line, which reads as a success at nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_selected_is_told_so()
    {
        var pane = Pane();

        await pane.CreateShortcutAsync();

        Assert.Equal("select something to make a shortcut to", pane.Status);
    }

    /// <summary>
    /// **A bin row carries the path the item USED to occupy.** A shortcut made
    /// from one would point at a location the item no longer holds — or at
    /// whatever has been written there since — and the destination is the
    /// literal string "vaktari:trash", which on Linux is a legal relative
    /// directory name. The same pair of guards Duplicate takes.
    /// </summary>
    [AvaloniaFact]
    public async Task The_bin_is_refused_and_says_what_to_use_instead()
    {
        var pane = Pane(Vaktari.Ui.VirtualPaths.Trash);
        pane.SelectedEntry = Row(File_("report.pdf"));

        await pane.CreateShortcutAsync();

        Assert.Empty(_maker.Made);
        Assert.Contains("Restore", pane.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_listing_that_is_a_view_rather_than_a_folder_is_refused()
    {
        var pane = Pane(Vaktari.Ui.VirtualPaths.Files);
        pane.SelectedEntry = Row(File_("report.pdf"));

        await pane.CreateShortcutAsync();

        Assert.Empty(_maker.Made);
        Assert.Contains("not a folder", pane.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A platform with no idea of a shortcut must not write one, the same way
    /// it is not offered one — and must do it SILENTLY. The status line is what
    /// separates the guard from the absence of one: with the guard replaced by
    /// a null-forgiving dereference, the catch swallows the
    /// NullReferenceException and the status line reads "Object reference not
    /// set to an instance of an object" — measured — so a platform that never
    /// had the feature reports a crash at it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_platform_with_no_shortcuts_makes_nothing_and_says_nothing()
    {
        PaneViewModel.Shortcuts = null;

        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));
        pane.Status = "untouched";

        await pane.CreateShortcutAsync();

        Assert.Empty(Directory.GetFiles(_root, "*Shortcut*"));
        Assert.Equal("untouched", pane.Status);
    }

    // ---- when it is offered ------------------------------------------------

    [AvaloniaFact]
    public void Nothing_selected_is_nothing_to_point_at()
    {
        Assert.False(Pane().CanCreateShortcut);
    }

    [AvaloniaFact]
    public void A_selected_row_in_a_real_folder_is_offered_it()
    {
        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        Assert.True(pane.CanCreateShortcut);
    }

    /// <summary>
    /// The folder half. The bin, Recent, This PC and a search all have rows to
    /// select and no folder to write into.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Vaktari.Ui.VirtualPaths.Trash)]
    [InlineData(Vaktari.Ui.VirtualPaths.Files)]
    [InlineData(Vaktari.Ui.VirtualPaths.Computer)]
    public void A_view_that_is_not_a_folder_is_not_offered_it(string listing)
    {
        var pane = Pane(listing);
        pane.SelectedEntry = Row(File_("report.pdf"));

        Assert.False(pane.CanCreateShortcut);
    }

    [AvaloniaFact]
    public void A_platform_with_no_shortcuts_is_not_offered_one()
    {
        PaneViewModel.Shortcuts = null;

        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        Assert.False(pane.CanCreateShortcut);
    }

    /// <summary>
    /// IsVisible is a binding, so the row only comes and goes if the property
    /// is announced. Selecting a row is one of the two ways the answer changes.
    /// </summary>
    [AvaloniaFact]
    public void Selecting_a_row_re_asks_whether_to_offer_it()
    {
        var pane = Pane();

        var said = false;
        pane.PropertyChanged += (_, e) =>
            said |= e.PropertyName == nameof(PaneViewModel.CanCreateShortcut);

        pane.SelectedEntry = Row(File_("report.pdf"));

        Assert.True(said, "the row keeps whatever visibility the last listing left it with");
    }

    /// <summary>
    /// The same question by the other route to a selection. Clicking a row sets
    /// SelectedEntry; a rubber band, Ctrl+A and a shift-click fill the bound
    /// collection instead, and only the collection's own handler hears that.
    /// </summary>
    [AvaloniaFact]
    public void Adding_to_the_selection_re_asks_whether_to_offer_it()
    {
        var pane = Pane();

        var said = false;
        pane.PropertyChanged += (_, e) =>
            said |= e.PropertyName == nameof(PaneViewModel.CanCreateShortcut);

        pane.SelectedEntries.Add(Row(File_("report.pdf")));

        Assert.True(said, "the row keeps whatever visibility the last listing left it with");
    }

    /// <summary>
    /// The other way: walking into a listing that is a view. A change announced
    /// for IsRealFolder is not a change announced for this one — the same trap
    /// CanGoToLocation carries a comment about a few lines above it.
    /// </summary>
    [AvaloniaFact]
    public void Walking_into_a_view_re_asks_whether_to_offer_it()
    {
        var pane = Pane();
        pane.SelectedEntry = Row(File_("report.pdf"));

        var said = false;
        pane.PropertyChanged += (_, e) =>
            said |= e.PropertyName == nameof(PaneViewModel.CanCreateShortcut);

        pane.CurrentPath = Vaktari.Ui.VirtualPaths.Trash;

        // OnCurrentPathChanged hops to the UI thread, because it runs on a pool
        // thread when a listing sets the path after a ConfigureAwait.
        Dispatcher.UIThread.RunJobs();

        Assert.True(said, "the row keeps whatever visibility the last listing left it with");
    }

    // ---- the row itself ----------------------------------------------------

    /// <summary>
    /// The command reached from the menu, and the gate it is drawn under. The
    /// finding was that neither existed: grep for "shortcut" in MainWindow.axaml
    /// found the keyboard sheet and nothing else.
    ///
    /// **On the menu's DIRECT children, which the first spelling of this did
    /// not check.** It searched the file for the header and read the element
    /// that followed, so a row nested inside another MenuItem -- one hover
    /// deeper, which is exactly where the desktop's own Create shortcut sits
    /// and exactly what this row exists to be nearer than -- would have
    /// satisfied it. The same rule ArchiveMenuTests states for the same menu.
    /// </summary>
    [Fact]
    public void The_listing_menu_carries_the_row()
    {
        var avalonia = XNamespace.Get("https://github.com/avaloniaui");

        var listing = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(avalonia + "ContextMenu")
            .Single(m => (string?)m.Attribute(
                XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "DataType")
                == "vm:PaneGroupViewModel");

        var row = listing.Elements(avalonia + "MenuItem")
            .SingleOrDefault(m => (string?)m.Attribute("Header") == "Create shortcut");

        Assert.True(row is not null,
            "\"Create shortcut\" is not a direct child of the listing's context menu");

        Assert.Equal("{Binding ActiveTab.CreateShortcutCommand}",
                     (string?)row!.Attribute("Command"));
        Assert.Equal("{Binding ActiveTab.CanCreateShortcut}",
                     (string?)row.Attribute("IsVisible"));
    }

    // ---- doubles -----------------------------------------------------------

    /// <summary>
    /// Records what it was asked for and writes a real file, so a test can
    /// assert on disk as well as on the call.
    /// </summary>
    private sealed class Maker : IShortcutMaker
    {
        public List<(string Target, string Destination)> Made { get; } = [];

        /// <summary>The one target this maker refuses, for the failure path.
        /// </summary>
        public string? Refuses { get; set; }

        /// <summary>What the refusal throws. Only
        /// UnauthorizedAccessException reaches the arm of Failures.Describe
        /// that repeats the phrase the call site hands it.</summary>
        public Exception Refusal { get; set; } =
            new IOException("the shell would not make a shortcut");

        /// <summary>Whether the FIRST write ran on the UI thread. The writer
        /// is the shell on Windows and it is not fast; where it runs is a
        /// property of the command, so the double is where it is recorded.
        /// </summary>
        public bool? OnUiThread { get; private set; }

        public string CreateShortcut(string target, string destinationFolder)
        {
            OnUiThread ??= Dispatcher.UIThread.CheckAccess();

            if (Refuses is { } doomed && target == doomed) throw Refusal;

            Made.Add((target, destinationFolder));

            var landing = Path.Combine(
                destinationFolder, Path.GetFileName(target) + " - Shortcut");

            File.WriteAllText(landing, target);

            return landing;
        }
    }

    /// <summary>Accepts every operation, does none of it, and remembers what it
    /// was told to make undoable.</summary>
    private sealed class Recording : IFileOperations
    {
        public List<string> Created { get; } = [];

        private static IOperationHandle Done()
        {
            var handle = new OperationHandle();

            handle.Begin(0, 0);
            handle.Complete();

            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done();

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) => Created.Add(path);

        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>A listing that answers nothing: these tests assert on what the
    /// command wrote, never on what came back.</summary>
    private class Inert : IFileSystemProvider
    {
        /// <summary>Whether the enumeration gives the thread up. Inert's does
        /// not, which is what lets every other test here run the refresh out
        /// straight through.</summary>
        protected virtual bool Yields => false;

        /// <summary>How many times the pane has asked for the listing.
        /// </summary>
        public int Listings;

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Listings++;

            if (Yields) await Task.Delay(1, ct).ConfigureAwait(false);
            else await Task.CompletedTask;

            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// Inert, except that its enumeration gives the thread up before it
    /// answers — which a real one does, and which is what makes the thread the
    /// refresh comes back on observable at all.
    /// </summary>
    private sealed class Slow : Inert
    {
        protected override bool Yields => true;
    }
}
