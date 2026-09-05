using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What is selected once a paste has finished and the listing has come back.
///
/// **The arrivals were not.** SelectionSurvivesReloadTests pins the rest of it —
/// a reload keeps the selection, and a renamed file comes back selected because
/// the prompt registers its new name — but a paste lands rows that did not
/// exist when the selection was captured, so after copying twenty files into a
/// folder they were somewhere in a thousand rows sorted by name with nothing
/// pointing at them. Explorer and Dolphin both leave the arrival selected.
///
/// The reason it was not a one-liner is here too: a conflict answered Keep both
/// RENAMES the arriving file, so the names that land are not the names that
/// were sent. The engine reports what actually landed and the pane selects
/// that, rather than guessing destination plus source name.
/// </summary>
public sealed class PastedItemsAreSelectedTests : OwnedViewModels
{
    /// <summary>
    /// **The clearing is done by the ListBox, not by the view model.** Avalonia's
    /// SelectingItemsControl empties its selection when the bound collection
    /// raises Reset, and every rebuild in the pane raises one — so a headless
    /// test that does not stand in for it starts each assertion with the old
    /// selection still sitting there. Copied from SelectionSurvivesReloadTests,
    /// which documents the same trap.
    /// </summary>
    private static void AsAListWould(PaneViewModel pane)
        => pane.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;

            pane.DetailsSelection.Clear();
            pane.SelectedEntry = null;
        };

    /// <summary>
    /// How many times the listing has been rebuilt since the first load.
    ///
    /// **The two "leaves the selection alone" tests assert something that is
    /// already true before the operation runs**, so without this they would
    /// read the answer before the refresh had happened and pass against any
    /// bug. Counting the rebuild is not a proxy for the selection — the
    /// condition below wants BOTH, and Reselect runs inside the same dispatcher
    /// job as the rebuild that raises the last Reset, so a pass cannot be read
    /// out of the gap between them.
    /// </summary>
    private sealed class Rebuilds
    {
        public int Count { get; private set; }

        public Rebuilds(PaneViewModel pane)
            => pane.Entries.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) Count++;
            };
    }

    private async Task<(PaneViewModel Pane, Folder Fs, Rebuilds Reloaded)> Listing(
        params string[] names)
    {
        var fs = new Folder(names);
        var pane = Own(new PaneViewModel(fs, fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Folder.Root);

        Assert.Equal(names.Length, pane.Entries.Count);

        AsAListWould(pane);

        return (pane, fs, new Rebuilds(pane));
    }

    /// <summary>
    /// The operation finishes on a pool thread and posts its refresh, so a test
    /// has to wait the way the window does. Bounded, and it ASSERTS at the end,
    /// so a condition that never comes true fails rather than passing quietly.
    ///
    /// On the assertion's own subject — the names that are selected — rather
    /// than on a count of dispatcher turns.
    /// </summary>
    private static async Task Settles(Func<bool> done, string what)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done(), what);
    }

    private static string[] Selected(PaneViewModel pane)
        => pane.Selection.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- the finding --------------------------------------------------------

    [AvaloniaFact]
    public async Task Pasted_items_come_back_selected()
    {
        var (pane, _, _) = await Listing("existing.txt");

        pane.PasteInto([Elsewhere("one.txt"), Elsewhere("two.txt")], move: false);

        await Settles(() => Selected(pane) is ["one.txt", "two.txt"],
                      "the pasted files came back unselected");

        Assert.Equal("one.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// **A second operation finishing behind the paste used to eat the
    /// arrivals.** Every finished operation posts its own RefreshAsync, and
    /// that refresh cancels the load the paste started and takes the pane's
    /// generation with it — so a register read at the top of the load was spent
    /// by a load that then returned at the generation guard without ever
    /// reaching the rows, and the surviving load found nothing to select. Two
    /// operations landing back to back on one pane is what
    /// ConcurrentOperationsTests exists for; measured here with a paste and a
    /// handle that lands nothing, which is a trash or a delete arriving a
    /// moment later.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_operation_finishing_behind_the_paste_keeps_the_arrivals()
    {
        var (pane, _, _) = await Listing("existing.txt");

        pane.PasteInto([Elsewhere("one.txt"), Elsewhere("two.txt")], move: false);

        var second = new OperationHandle();

        pane.Adopt(second);
        second.Begin(0, 0);
        second.Complete();

        await Settles(() => Selected(pane) is ["one.txt", "two.txt"],
                      "a second operation's refresh swallowed the paste's arrivals");
    }

    /// <summary>
    /// **The spelling that lands is the folder's, not the source's.** Copying
    /// report.txt onto an existing Report.TXT and answering Replace writes
    /// through the entry that is already there, so the folder goes on listing
    /// Report.TXT while the engine reports the path it wrote, dst\report.txt —
    /// measured on WindowsFileOperations in
    /// Vaktari.Windows.Tests.LandedItemsTests.An_overwrite_reports_the_row_the_listing_shows.
    /// Matched as strings, those are two files and the arrival came back
    /// unselected; where it is the only arrival the whole paste selects
    /// nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task An_arrival_that_replaced_a_differently_cased_row_is_still_selected()
    {
        var (pane, _, _) = await Listing("Report.TXT");

        pane.PasteInto([Elsewhere("report.txt")], move: false);

        await Settles(() => Selected(pane) is ["Report.TXT"],
                      "the row the arrival overwrote was not selected");
    }

    /// <summary>
    /// **The name that landed, not the name that was sent.** The folder already
    /// holds report.txt, the arrival is kept as "report (2).txt", and a pane
    /// guessing destination plus source name would have lit up the file that
    /// was already there — the one the person chose NOT to replace.
    /// </summary>
    [AvaloniaFact]
    public async Task A_kept_both_arrival_is_selected_under_the_name_it_landed_with()
    {
        var (pane, fs, _) = await Listing("report.txt");

        fs.ArrivesAs["report.txt"] = "report (2).txt";

        pane.PasteInto([Elsewhere("report.txt")], move: false);

        await Settles(() => Selected(pane) is ["report (2).txt"],
                      "the kept-both arrival was not the row that came back selected");
    }

    /// <summary>
    /// **Instead of what was selected, not as well as.** Pasting into a folder
    /// with a row already picked left that row selected alongside the arrivals,
    /// and the next Delete would have taken it with them.
    /// </summary>
    [AvaloniaFact]
    public async Task What_was_selected_before_the_paste_is_not_selected_after_it()
    {
        var (pane, _, _) = await Listing("mine.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "mine.txt"));

        pane.PasteInto([Elsewhere("arrived.txt")], move: false);

        await Settles(() => Selected(pane) is ["arrived.txt"],
                      "the row selected before the paste was still selected after it");
    }

    /// <summary>
    /// **Only what arrived.** Two files are sent into a folder that already
    /// holds one of them and the clash is skipped, so the row IS on screen and
    /// is still somebody else's file. Selecting it would point the next Delete
    /// at the thing the person just chose to leave alone.
    /// </summary>
    [AvaloniaFact]
    public async Task A_skipped_item_is_not_selected_although_its_row_is_there()
    {
        var (pane, fs, _) = await Listing("clash.txt");

        fs.LandsNothing.Add("clash.txt");

        pane.PasteInto([Elsewhere("arrived.txt"), Elsewhere("clash.txt")], move: false);

        await Settles(() => Selected(pane) is ["arrived.txt"],
                      "the skipped file's row was selected as though it had arrived");

        // The row it would have selected was there to be selected, which is
        // what makes the line above mean anything.
        Assert.Contains(pane.Entries, e => e.Name == "clash.txt");
    }

    // ---- and what must NOT change ------------------------------------------

    /// <summary>
    /// A drop onto a folder ROW lands the files inside that folder, not in this
    /// listing — so a request that matches nothing on screen has to leave the
    /// selection where it was rather than clearing it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_paste_into_a_subfolder_leaves_this_folder_s_selection_alone()
    {
        var (pane, _, reloaded) = await Listing("mine.txt", "sub");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "mine.txt"));

        pane.PasteIntoFolder(
            Path.Combine(Folder.Root, "sub"), [Elsewhere("arrived.txt")], move: false);

        await Settles(() => reloaded.Count > 0 && Selected(pane) is ["mine.txt"],
                      "a paste into a subfolder disturbed this folder's selection");
    }

    /// <summary>
    /// A GUARD, and it says so: no one-line change to this fix can make it
    /// fail. A trash puts nothing anywhere, so its handle reports an empty
    /// list, and an empty list matches no row — so Reselect's row probe answers
    /// "none of it is here" whatever the count guard above it is doing. It is
    /// here because "a delete stopped keeping your place" is the regression
    /// this change could most easily cause, and SelectionSurvivesReloadTests
    /// covers only the reload with no operation behind it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_trash_leaves_the_selection_alone()
    {
        var (pane, fs, reloaded) = await Listing("a.txt", "b.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "b.txt"));

        pane.Adopt(fs.Trash([Path.Combine(Folder.Root, "gone.txt")]));

        await Settles(() => reloaded.Count > 0 && Selected(pane) is ["b.txt"],
                      "a trash cleared the selection it should have left alone");
    }

    /// <summary>
    /// **Spent by the load that used it**, the way the rename's register is.
    /// A request that outlived its listing would put the arrivals back under
    /// every later refresh — F5, a watcher tick, the reload after the next
    /// rename — dragging the selection off whatever the person had moved on to.
    /// </summary>
    [AvaloniaFact]
    public async Task The_request_is_spent_by_the_load_that_used_it()
    {
        var (pane, _, _) = await Listing("mine.txt");

        pane.PasteInto([Elsewhere("arrived.txt")], move: false);

        await Settles(() => Selected(pane) is ["arrived.txt"],
                      "the pasted file came back unselected");

        pane.DetailsSelection.Clear();
        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "mine.txt"));

        await pane.RefreshAsync();

        Assert.Equal(["mine.txt"], Selected(pane));
    }

    private static string Elsewhere(string name)
        => Path.Combine(Path.GetTempPath(), "vaktari-pasted-source", name);

    /// <summary>
    /// One folder and an engine that really changes what the next listing
    /// holds, so the selection is read off rows that arrived rather than off
    /// rows a test put there by hand.
    /// </summary>
    private sealed class Folder(params string[] names) : IFileSystemProvider, IFileOperations
    {
        private readonly List<string> _names = [.. names];

        public static string Root => Path.Combine(Path.GetTempPath(), "vaktari-pasted");

        /// <summary>Stands in for a conflict answered Keep both: the name the
        /// arrival is given, keyed by the name that was sent.</summary>
        public Dictionary<string, string> ArrivesAs { get; } = [];

        /// <summary>Sent names that arrive nowhere — a skip, or an item that
        /// failed while the rest of the batch went through.</summary>
        public HashSet<string> LandsNothing { get; } = [];

        private static FileEntry Row(string name)
            => new(name, Path.Combine(Root, name), 4, DateTimeOffset.UnixEpoch, EntryFlags.None);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return _names.Select(Row).ToList();
        }

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        /// <summary>
        /// Puts the arrivals in the folder and reports the ones that got there,
        /// which is the whole shape this test class is about. A rename on the
        /// way in changes the row AND what is reported, together — the engine
        /// is the only thing that knows both.
        ///
        /// **A name that is already here keeps ITS spelling while the report
        /// keeps the source's**, which is not a convenience: it is what NTFS
        /// does. Copying report.txt over Report.TXT writes through the existing
        /// directory entry and leaves it named Report.TXT, and the engine
        /// reports the path it was asked to write — measured on the real
        /// WindowsFileOperations in
        /// Vaktari.Windows.Tests.LandedItemsTests.An_overwrite_reports_the_row_the_listing_shows.
        /// A fake that added a second row instead would be pretending the
        /// filesystem this suite runs on is case-sensitive.
        /// </summary>
        private IOperationHandle Landing(IReadOnlyList<string> sources, string destination)
        {
            var handle = new OperationHandle();
            var landed = new List<string>();

            foreach (var source in sources)
            {
                var sent = PathRules.LeafName(source);
                var arrival = ArrivesAs.TryGetValue(sent, out var renamed) ? renamed : sent;

                if (LandsNothing.Contains(sent)) continue;

                if (PathRules.Same(destination, Root)
                    && !_names.Any(n => string.Equals(n, arrival, PathRules.Comparison)))
                    _names.Add(arrival);

                landed.Add(Path.Combine(destination, arrival));
            }

            handle.Begin(0, 0);
            handle.Arrived(landed);
            handle.Complete();

            return handle;
        }

        private static IOperationHandle Done()
        {
            var handle = new OperationHandle();

            handle.Begin(0, 0);
            handle.Complete();

            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Landing(sources, destination);

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Landing(sources, destination);

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done();
        public void RecordCreation(string path) { }

        /// <summary>No history, so nothing to gather into a step.</summary>
        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
