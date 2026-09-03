using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Three things a pane carried, dropped or resolved wrongly.
///
///  - **The filter followed you into the next folder.** Type "report" to find
///    something, open a folder from the results, and the new folder came up
///    filtered by a word with nothing to do with it — reading as an empty
///    folder.
///  - **Delete, Delete, Delete did not work.** After the rows went nothing was
///    selected, so the next Delete had nothing to act on and the keyboard had
///    lost its place entirely.
///  - **A relative path went nowhere.** ".." or "src" typed in the address bar
///    resolved against the process's working directory rather than the folder
///    on screen.
/// </summary>
public sealed class PaneStateTests : OwnedViewModels
{
    private static FileEntry Entry(string name, string root)
        => new(name, Path.Combine(root, name), 2, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private async Task<(PaneViewModel Pane, Listing Fs)> Pane(params string[] names)
    {
        var fs = new Listing(names);

        // A real IFileOperations, because TrashSelected returns early without
        // one — and it is TrashSelected that decides where the keyboard goes.
        var pane = Own(new PaneViewModel(fs, new Quiet(), null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(Path.GetTempPath());

        return (pane, fs);
    }

    // ---- the filter does not follow ------------------------------------------

    [AvaloniaFact]
    public async Task Entering_another_folder_clears_the_filter()
    {
        var (pane, _) = await Pane("alpha.txt", "beta.txt");

        pane.FilterText = "alpha";
        await Task.Delay(200);

        Assert.Equal("alpha", pane.FilterText);

        await pane.NavigateAsync(Path.Combine(Path.GetTempPath(), "elsewhere"));

        Assert.Equal("", pane.FilterText);
    }

    /// <summary>Refreshing where you are is not leaving, so the filter
    /// stays.</summary>
    [AvaloniaFact]
    public async Task Staying_put_keeps_the_filter()
    {
        var (pane, _) = await Pane("alpha.txt");

        pane.FilterText = "alpha";
        await Task.Delay(200);

        await pane.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("alpha", pane.FilterText);
    }

    // ---- the keyboard keeps its place ----------------------------------------

    /// <summary>
    /// The row after the deleted one is chosen BEFORE the operation, because
    /// afterwards the rows it refers to are gone.
    /// </summary>
    [AvaloniaFact]
    public async Task Deleting_a_row_moves_the_selection_to_the_next_one()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt", "c.txt");

        var second = pane.DetailsEntries.First(e => e.Name == "b.txt");

        pane.SelectedEntry = second;
        pane.DetailsSelection.Add(second);

        pane.TrashSelectedCommand.Execute(null);

        // The row goes, the way the watcher delivers it.
        fs.Raise(new FileSystemChange(ChangeKind.Removed, second.FullPath!));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("c.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>Deleting the last row falls back to the new last one rather
    /// than to nothing.</summary>
    [AvaloniaFact]
    public async Task Deleting_the_last_row_falls_back_to_the_one_before()
    {
        var (pane, fs) = await Pane("a.txt", "b.txt");

        var last = pane.DetailsEntries.First(e => e.Name == "b.txt");

        pane.SelectedEntry = last;
        pane.DetailsSelection.Add(last);

        pane.TrashSelectedCommand.Execute(null);

        fs.Raise(new FileSystemChange(ChangeKind.Removed, last.FullPath!));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("a.txt", pane.SelectedEntry?.Name);
    }

    /// <summary>Accepts every operation and does nothing: the filesystem side
    /// is not what these tests are about.</summary>
    private sealed class Quiet : IFileOperations
    {
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

        public void RecordCreation(string path) { }

        public bool CanUndo => false;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class Listing : IFileSystemProvider
    {
        private readonly string[] _names;
        private Action<FileSystemChange>? _onChange;

        public Listing(string[] names) => _names = names;

        public void Raise(FileSystemChange change) => _onChange?.Invoke(change);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [.. _names.Select(n => Entry(n, path))];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
        {
            _onChange = onChange;
            return new Nothing();
        }

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
