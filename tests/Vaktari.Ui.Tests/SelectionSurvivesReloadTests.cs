using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What is still selected once the listing has been built again.
///
/// **Sorting kept the selection and a reload threw it away.** ListingStateTests
/// pins the sort, which captures the paths itself; nothing covered the reload,
/// which cleared the rows before anything read them — so F5, and every refresh
/// the program does for you after a rename, a paste or an undo, came back with
/// nothing picked. The filter did the same, and the renamed file was a case of
/// its own: its old path goes with its old name.
/// </summary>
public sealed class SelectionSurvivesReloadTests : OwnedViewModels
{
    /// <summary>
    /// **The clearing is done by the ListBox, not by the view model.** Avalonia's
    /// SelectingItemsControl empties its selection when the bound collection
    /// raises Reset, and every rebuild in the pane raises one — so a headless
    /// test that does not stand in for it starts each assertion with the old
    /// selection still sitting there and passes against the bug. Both halves,
    /// because one selection model sits behind SelectedItem and SelectedItems.
    /// </summary>
    private static void AsAListWould(PaneViewModel pane)
        => pane.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;

            pane.DetailsSelection.Clear();
            pane.SelectedEntry = null;
        };

    private async Task<(PaneViewModel Pane, Folder Fs)> Listing(params string[] names)
    {
        var fs = new Folder(names);
        var pane = Own(new PaneViewModel(fs, fs) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Folder.Root);

        Assert.Equal(names.Length, pane.Entries.Count);

        AsAListWould(pane);

        return (pane, fs);
    }

    /// <summary>
    /// Sets the filter and waits for it to land. **The filter is debounced by
    /// 120 ms**, so reading the listing straight after setting the text tests
    /// nothing — the same trap FilterAndTypingTests documents.
    /// </summary>
    private static async Task Filter(PaneViewModel pane, string text)
    {
        pane.FilterText = text;

        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The rename starts its reload without awaiting it, so a test has to wait
    /// the way the window does. Bounded, and it ASSERTS at the end, so a
    /// condition that never comes true fails rather than passing quietly.
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

    // ---- a reload keeps the selection ---------------------------------------

    [AvaloniaFact]
    public async Task Refreshing_keeps_what_was_selected()
    {
        var (pane, _) = await Listing("a.txt", "b.txt", "c.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "b.txt"));

        await pane.RefreshAsync();

        Assert.Equal("b.txt", Assert.Single(pane.Selection).Name);
        Assert.Equal("b.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>
    /// The fallback for a pane driven without a list. In a real window the two
    /// cannot come apart — one selection model sits behind SelectedItem and
    /// SelectedItems — but the places that set the focused row on its own, and
    /// any pane not bound to a control, would otherwise hand a rebuild nothing
    /// to keep.
    /// </summary>
    [AvaloniaFact]
    public async Task Refreshing_keeps_the_row_the_keyboard_was_on()
    {
        var (pane, _) = await Listing("a.txt", "b.txt", "c.txt");

        pane.SelectedEntry = pane.DetailsEntries.First(e => e.Name == "b.txt");

        await pane.RefreshAsync();

        Assert.Equal("b.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>Nothing selected stays nothing selected: the restore must not
    /// invent a selection where there was none.</summary>
    [AvaloniaFact]
    public async Task Refreshing_with_nothing_selected_selects_nothing()
    {
        var (pane, _) = await Listing("a.txt", "b.txt");

        await pane.RefreshAsync();

        Assert.Empty(pane.Selection);
        Assert.Null(pane.SelectedEntry);
    }

    /// <summary>
    /// **A path only travels within one folder.** The bin's rows carry the path
    /// a file used to occupy and a search result carries one from anywhere on
    /// the machine, so a selection carried into either would match and light up
    /// a row nobody picked. The second listing here is that shape.
    /// </summary>
    [AvaloniaFact]
    public async Task Going_somewhere_else_does_not_carry_the_selection()
    {
        var (pane, fs) = await Listing("notes.txt");

        var row = Assert.Single(pane.DetailsEntries);
        pane.DetailsSelection.Add(row);

        fs.Elsewhere = row.FullPath;

        await pane.NavigateAsync(Folder.Other);

        // The match was there to be made, which is what makes the emptiness
        // below mean something.
        Assert.Equal(row.FullPath, Assert.Single(pane.Entries).FullPath);

        Assert.Empty(pane.Selection);
    }

    // ---- the filter keeps it too --------------------------------------------

    [AvaloniaFact]
    public async Task Filtering_keeps_what_is_still_showing()
    {
        var (pane, _) = await Listing("alpha.txt", "album.txt", "beta.txt");

        pane.DetailsSelection.Add(pane.DetailsEntries.First(e => e.Name == "album.txt"));

        await Filter(pane, "al");

        Assert.Equal(2, pane.Entries.Count);
        Assert.Equal("album.txt", Assert.Single(pane.Selection).Name);
    }

    // ---- and a load consumes what was registered for it ---------------------

    /// <summary>
    /// Pins the register being CONSUMED by the load: the rename engine no
    /// longer fills it (the prompt does, because only the gesture knows whether
    /// the keyboard is staying on the file or moving to the next one), so the
    /// request is made here the way the prompt makes it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_registered_path_is_selected_when_the_listing_comes_back()
    {
        var (pane, _) = await Listing("a.txt", "b.txt");

        var row = pane.DetailsEntries.First(e => e.Name == "b.txt");

        pane.SelectAfterLoad(Path.Combine(Folder.Root, "renamed.txt"));

        await pane.RenameOrThrowAsync(row, "renamed.txt");

        await Settles(() => pane.Selection.Count == 1 && pane.Selection[0].Name == "renamed.txt",
                      "the renamed file came back unselected");

        Assert.Equal("renamed.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>One folder, one bin-shaped folder beside it, and a rename that
    /// really changes what the next listing holds.</summary>
    private sealed class Folder(params string[] names) : IFileSystemProvider, IFileOperations
    {
        private readonly List<string> _names = [.. names];

        public static string Root => Path.Combine(Path.GetTempPath(), "vaktari-reselect");
        public static string Other => Path.Combine(Path.GetTempPath(), "vaktari-reselect-bin");

        /// <summary>The one row <see cref="Other"/> lists, by the path it holds
        /// somewhere else.</summary>
        public string? Elsewhere { get; set; }

        private static FileEntry Row(string name)
            => new(name, Path.Combine(Root, name), 4, DateTimeOffset.UnixEpoch, EntryFlags.None);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            yield return PathRules.Same(path, Other) && Elsewhere is { } there
                ? [new FileEntry(PathRules.LeafName(there), there, 4,
                                 DateTimeOffset.UnixEpoch, EntryFlags.None)]
                : _names.Select(Row).ToList();
        }

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
        {
            var at = _names.IndexOf(PathRules.LeafName(path));

            Assert.True(at >= 0, "the rename was asked for a file this folder does not have");
            _names[at] = newName;

            return ValueTask.CompletedTask;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

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
        public void RecordCreation(string path) { }

        /// <summary>No history, so nothing to gather into a step.</summary>
        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
