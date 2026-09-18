using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// An undo or a redo that fails part-way has still changed the disk.
///
/// **A failed undo refreshed nothing.** The catch in the pane only wrote the
/// failure onto the status line: the listing kept showing the folder as it was
/// before the undo began, and the Undo and Redo rows kept naming the step as it
/// was before it ran — while the engine had put some of it back, and its
/// history had moved on. An undo that stops part-way is the ordinary case once
/// the undo is careful: it puts back what it can and leaves what it cannot.
/// </summary>
public sealed class UndoFailureRefreshesTests : OwnedViewModels
{
    private static async Task<T> InFolder<T>(Func<string, Task<T>> test)
    {
        // A real folder this test made, because navigating asks the path things
        // a fake listing cannot answer for it; removed again, and only it.
        var folder = Directory.CreateTempSubdirectory("vaktari-undofail-").FullName;

        try { return await test(folder); }
        finally { Directory.Delete(folder); }
    }

    /// <summary>
    /// The listing is read again, the rows name what the history holds NOW,
    /// and the reason is said after the reload rather than erased by it.
    /// </summary>
    [AvaloniaFact]
    public Task A_failed_undo_still_refreshes_what_it_changed() => InFolder(async folder =>
    {
        var listing = new Counting();
        var ops = new FailsPartWay { Undoable = "move of a.txt and b.txt" };
        var pane = Own(new PaneViewModel(listing, ops));

        await pane.NavigateAsync(folder);
        pane.RefreshUndoState();

        var loads = listing.Enumerations;

        await pane.UndoAsync();

        Assert.True(listing.Enumerations > loads,
                    "the undo changed the disk before it failed, and the listing was not read again");

        Assert.Equal("Undo move of a.txt", pane.UndoLabel);
        Assert.Equal("Redo move of b.txt", pane.RedoLabel);

        Assert.Equal(FailsPartWay.UndoFailure, pane.Status);

        return true;
    });

    /// <summary>And a redo, the same way round.</summary>
    [AvaloniaFact]
    public Task A_failed_redo_still_refreshes_what_it_changed() => InFolder(async folder =>
    {
        var listing = new Counting();
        var ops = new FailsPartWay { Redoable = "move of b.txt and c.txt" };
        var pane = Own(new PaneViewModel(listing, ops));

        await pane.NavigateAsync(folder);
        pane.RefreshUndoState();

        var loads = listing.Enumerations;

        await pane.RedoAsync();

        Assert.True(listing.Enumerations > loads,
                    "the redo changed the disk before it failed, and the listing was not read again");

        Assert.Equal("Undo move of b.txt", pane.UndoLabel);
        Assert.Equal("Redo move of c.txt", pane.RedoLabel);

        Assert.Equal(FailsPartWay.RedoFailure, pane.Status);

        return true;
    });

    /// <summary>
    /// An engine whose undo and redo each do half of their step and then fail,
    /// leaving its history as a careful engine leaves it: what went is on the
    /// other stack, and what did not is still where it was.
    /// </summary>
    private sealed class FailsPartWay : IFileOperations
    {
        public const string UndoFailure = "a.txt could not go back, because something with the same name is there now";
        public const string RedoFailure = "c.txt could not go forward, because something with the same name is there now";

        public string? Undoable { get; set; }
        public string? Redoable { get; set; }

        public bool CanUndo => Undoable is not null;
        public bool CanRedo => Redoable is not null;

        public string? UndoDescription => Undoable;
        public string? RedoDescription => Redoable;

        public ValueTask UndoAsync(CancellationToken ct)
        {
            Redoable = "move of b.txt";
            Undoable = "move of a.txt";

            throw new IOException(UndoFailure);
        }

        public ValueTask RedoAsync(CancellationToken ct)
        {
            Undoable = "move of b.txt";
            Redoable = "move of c.txt";

            throw new IOException(RedoFailure);
        }

        public IOperationHandle Copy(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Move(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Delete(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public IOperationHandle Trash(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        public IUndoGroup? BeginRenameGroup() => null;
    }

    /// <summary>A listing with nothing in it that counts how often it is read.</summary>
    private sealed class Counting : IFileSystemProvider
    {
        private int _enumerations;

        public int Enumerations => Volatile.Read(ref _enumerations);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _enumerations);

            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
