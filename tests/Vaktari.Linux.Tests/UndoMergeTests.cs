using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Undo takes back what a copy or a move wrote there, and nothing else.
///
/// **Undoing a copy merged into a folder sent the whole folder to the trash.**
/// The undo recorded each root's landing, and a folder that already existed
/// was merged into rather than made. Measured here before the fix: copying
/// "photos" onto an existing "photos", choosing to overwrite, and undoing put
/// photos, photos/new.jpg and photos/already-here.jpg in the trash — and the
/// copy had never touched the last of them.
///
/// The trash is this class's own. XdgTrash reads XDG_DATA_HOME on every use,
/// so pointing it inside the temp root keeps the real one out of these tests;
/// the assembly runs its classes one at a time, so nothing else sees it.
/// </summary>
public sealed class UndoMergeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-undomerge-" + Guid.NewGuid().ToString("N"));

    private readonly string? _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public UndoMergeTests()
    {
        Directory.CreateDirectory(At("data"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", At("data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHome);

        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private void Write(string relative, string content)
    {
        var path = At(relative.Split('/'));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static ValueTask<ConflictResolution> Overwrite(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task Done(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);
    }

    /// <summary>What the trash holds, by name.</summary>
    private IEnumerable<string> Binned()
    {
        var files = At("data", "Trash", "files");

        return Directory.Exists(files)
            ? Directory.EnumerateFileSystemEntries(files).Select(p => Path.GetFileName(p)).Order()
            : [];
    }

    [PosixFact]
    public async Task Undoing_a_copy_merged_into_a_folder_takes_back_only_what_arrived()
    {
        Write("src/photos/new.jpg", "new");
        Write("src/photos/raw/one.cr2", "new");
        Write("dst/photos/already-here.jpg", "old");

        var ops = new LinuxFileOperations();

        await Done(ops.Copy([At("src", "photos")], At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(At("dst", "photos", "already-here.jpg")));
        Assert.False(File.Exists(At("dst", "photos", "new.jpg")));
        Assert.False(Directory.Exists(At("dst", "photos", "raw")));
        Assert.Equal(["new.jpg", "raw"], Binned());
    }

    /// <summary>
    /// And the move: only what moved goes back, into a source folder the move
    /// had swept away as empty. What was already at the destination stays
    /// there.
    /// </summary>
    [PosixFact]
    public async Task Undoing_a_move_merged_into_a_folder_moves_back_only_what_moved()
    {
        Write("src/photos/new.jpg", "new");
        Write("dst/photos/already-here.jpg", "old");

        var ops = new LinuxFileOperations();

        await Done(ops.Move([At("src", "photos")], At("dst"), Overwrite));

        Assert.False(Directory.Exists(At("src", "photos")), "the move did not sweep its emptied source, so this proves nothing");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(At("src", "photos", "new.jpg")));
        Assert.Equal("old", File.ReadAllText(At("dst", "photos", "already-here.jpg")));
        Assert.False(File.Exists(At("dst", "photos", "new.jpg")));
        Assert.False(File.Exists(At("src", "photos", "already-here.jpg")));
    }

    /// <summary>A folder the copy made is still taken back whole — the case
    /// that was always right, kept right.</summary>
    [PosixFact]
    public async Task Undoing_a_copy_into_a_new_folder_still_takes_the_folder()
    {
        Write("src/photos/new.jpg", "new");
        Directory.CreateDirectory(At("dst"));

        var ops = new LinuxFileOperations();

        await Done(ops.Copy([At("src", "photos")], At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(Directory.Exists(At("dst", "photos")));
        Assert.Equal(["photos"], Binned());
    }

    /// <summary>
    /// A file the copy replaced is something it wrote, so undo still takes it
    /// back, as it always did. The version it replaced cannot come back from
    /// anywhere, and leaving the copy in place would make Ctrl+Z quietly reach
    /// past it to an older step.
    /// </summary>
    [PosixFact]
    public async Task Undoing_a_copy_that_replaced_a_file_still_takes_it_back()
    {
        Write("src/notes.txt", "new");
        Write("dst/notes.txt", "old");

        var ops = new LinuxFileOperations();

        await Done(ops.Copy([At("src", "notes.txt")], At("dst"), Overwrite));

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(File.Exists(At("dst", "notes.txt")));
        Assert.Equal(["notes.txt"], Binned());
    }
}
