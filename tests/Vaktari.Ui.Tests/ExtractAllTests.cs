using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Extract all as an operation: on the bar, pausable, cancellable, and
/// reporting where it landed.
///
/// **It was a status line and a wait.** A large 7z showed "extracting…" for
/// minutes with no progress and no way to stop it. These drive the pane's
/// command against real archives on disk and watch the handle it announces.
///
/// A file of its own rather than more of ArchiveMenuTests, so it does not
/// collide with the menu split's edits there.
/// </summary>
public sealed class ExtractAllTests : OwnedViewModels
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-extractall").FullName;
    private readonly Recording _ops = new();

    public override void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }

        GC.SuppressFinalize(this);
    }

    private string At(string name) => Path.Combine(_root, name);

    private PaneViewModel Pane() => Own(new PaneViewModel(new Listing(), _ops) { CurrentPath = _root });

    private static FileEntry Row(string path)
        => new(Path.GetFileName(path), path, 0, DateTimeOffset.Now,
               Directory.Exists(path) ? EntryFlags.Directory : EntryFlags.None);

    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = At(name);

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    [AvaloniaFact]
    public async Task The_extraction_is_announced_once_as_an_extract_of_that_archive_into_its_folder()
    {
        var archive = Zip("trip.zip", ("a.txt", "a"), ("b.txt", "b"));
        var pane = Pane();
        var started = new List<IOperationHandle>();

        pane.OperationStarted += (_, h) => started.Add(h);
        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        var handle = Assert.Single(started);

        Assert.Equal(OperationKind.Extract, handle.Kind);
        Assert.Equal([archive, _root], handle.Paths);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal([At("trip")], handle.Landed);
    }

    [AvaloniaFact]
    public async Task Pausing_holds_it_and_resuming_finishes_it()
    {
        var archive = Zip("trip.zip", ("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));
        var pane = Pane();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationHandle? handle = null;

        pane.OperationStarted += (_, h) =>
        {
            handle = (OperationHandle)h;
            handle.Progressed += (_, p) =>
            {
                if (p.ItemsDone != 1 || paused.Task.IsCompleted) return;

                handle.Pause();
                paused.TrySetResult();
            };
        };

        pane.SelectedEntry = Row(archive);

        var run = pane.ExtractSelectionAsync();

        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        Assert.Equal(OperationState.Paused, handle!.State);
        Assert.False(run.IsCompleted, "the extraction carried on while paused");

        handle.Resume();

        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Directory.GetFiles(At("trip")).Select(Path.GetFileName).Order());
    }

    [AvaloniaFact]
    public async Task Cancelling_leaves_nothing_and_says_so()
    {
        var archive = Zip("trip.zip", ("a.txt", "a"), ("b.txt", "b"));
        var pane = Pane();
        IOperationHandle? handle = null;

        pane.OperationStarted += (_, h) =>
        {
            handle = h;
            ((OperationHandle)h).Cancel();
        };

        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.Equal(OperationState.Cancelled, handle!.State);
        Assert.Equal("stopped extracting trip.zip — nothing was extracted", pane.Status);
        Assert.Equal(["trip.zip"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Empty(_ops.Created);
    }

    [AvaloniaFact]
    public async Task A_damaged_archive_fails_the_operation_in_words()
    {
        var archive = At("broken.7z");

        File.WriteAllText(archive, "not a 7z at all");

        var pane = Pane();
        IOperationHandle? handle = null;

        pane.OperationStarted += (_, h) => handle = h;
        pane.SelectedEntry = Row(archive);

        await pane.ExtractSelectionAsync();

        Assert.Equal(OperationState.Failed, handle!.State);
        Assert.Equal("broken.7z is not a 7z file, or is damaged", pane.Status);
    }

    [AvaloniaFact]
    public async Task The_undo_is_told_where_it_landed_a_folder_or_a_file()
    {
        var pane = Pane();

        pane.SelectedEntry = Row(Zip("trip.zip", ("a.txt", "a"), ("b.txt", "b")));
        await pane.ExtractSelectionAsync();

        var gz = At("notes.txt.gz");

        using (var file = File.Create(gz))
        using (var stream = new GZipStream(file, CompressionLevel.Optimal))
            stream.Write("hello"u8);

        // What the first one landed comes back selected, the way a paste's
        // arrivals do, so the second archive has to be picked on its own.
        pane.SelectedEntries.Clear();
        pane.SelectedEntry = Row(gz);
        await pane.ExtractSelectionAsync();

        Assert.Equal([At("trip"), At("notes.txt")], _ops.Created);
        Assert.Equal("hello", File.ReadAllText(At("notes.txt")));
    }

    // ---- the fakes ---------------------------------------------------------

    /// <summary>Lists the folder it is asked about.</summary>
    private sealed class Listing : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            if (!Directory.Exists(path)) yield break;

            yield return [.. Directory.EnumerateFileSystemEntries(path).Select(Row)];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(File.Exists(path) || Directory.Exists(path) ? Row(path) : null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>Remembers what was made undoable and refuses everything else:
    /// Extract all writes for itself and never reaches the copy engine.</summary>
    private sealed class Recording : IFileOperations
    {
        public List<string> Created { get; } = [];

        private static IOperationHandle Refuse()
            => throw new InvalidOperationException("Extract all writes for itself");

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Refuse();

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Refuse();

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Refuse();
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Refuse();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct) => ValueTask.CompletedTask;

        public void RecordCreation(string path) => Created.Add(path);

        public IUndoGroup? BeginRenameGroup() => null;

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
