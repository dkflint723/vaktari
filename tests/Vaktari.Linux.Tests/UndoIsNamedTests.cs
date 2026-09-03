using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What the next undo is called — the Linux twin of the Windows tests, because
/// both engines keep their own undo stack and each names its own actions.
///
/// **Ctrl+Z said nothing about what it would take back**, and there was no Undo
/// row in any menu to say it either. Ten Describe implementations across the two
/// engines is ten chances for one of them to be silently empty, which is exactly
/// as unhelpful as no name at all.
///
/// Like the other Linux operation tests, these run on any platform: the code
/// under test is path arithmetic and ordinary file I/O.
/// </summary>
public sealed class UndoIsNamedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-undoname-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _dataHomeBefore = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public UndoIsNamedTests()
    {
        Directory.CreateDirectory(_root);

        // The trash case really trashes, so it goes into a bin of this test's
        // own — XDG_DATA_HOME is what the spec says decides where the trash
        // lives, and nothing here should reach the trash of whoever is running
        // it.
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHomeBefore);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_fresh_history_names_nothing()
    {
        var ops = new LinuxFileOperations();

        Assert.Null(ops.UndoDescription);
        Assert.Null(ops.RedoDescription);
    }

    /// <summary>The finding's own case: a paste into the wrong folder.</summary>
    [Fact]
    public async Task A_copy_of_one_file_is_named_after_the_file()
    {
        var note = File_("source/notes.txt");
        var wrong = Dir("wrong-folder");

        var ops = new LinuxFileOperations();

        await ops.Copy([note], wrong, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        Assert.Equal("copy of notes.txt", ops.UndoDescription);
    }

    [Fact]
    public async Task A_copy_of_several_is_counted()
    {
        var one = File_("source/one.txt");
        var two = File_("source/two.txt");
        var three = File_("source/three.txt");
        var wrong = Dir("wrong-folder");

        var ops = new LinuxFileOperations();

        await ops.Copy([one, two, three], wrong,
                       _ => ValueTask.FromResult(ConflictResolution.KeepBoth)).Completion;

        Assert.Equal("copy of 3 items", ops.UndoDescription);
    }

    [Fact]
    public async Task A_move_is_named_as_a_move()
    {
        var one = File_("source/one.txt");
        var two = File_("source/two.txt");
        var elsewhere = Dir("elsewhere");

        var ops = new LinuxFileOperations();

        await ops.Move([one, two], elsewhere,
                       _ => ValueTask.FromResult(ConflictResolution.KeepBoth)).Completion;

        Assert.Equal("move of 2 items", ops.UndoDescription);
    }

    [Fact]
    public void A_new_folder_is_named_after_itself()
    {
        var made = Dir("Reports");

        var ops = new LinuxFileOperations();

        ops.RecordCreation(made);

        Assert.Equal("creating Reports", ops.UndoDescription);
    }

    [Fact]
    public async Task A_rename_is_named_by_the_name_on_screen()
    {
        var note = File_("readme.txt");

        var ops = new LinuxFileOperations();

        await ops.RenameAsync(note, "notes.txt", CancellationToken.None);

        Assert.Equal("rename of notes.txt", ops.UndoDescription);
    }

    /// <summary>
    /// This side keeps the original paths from the start, so the name comes
    /// straight off the record rather than being carried in beside the keys the
    /// way the Windows one needs.
    /// </summary>
    [Fact]
    public async Task A_delete_is_named_by_the_file()
    {
        var note = File_("notes.txt");

        var ops = new LinuxFileOperations();

        await ops.Trash([note]).Completion;

        Assert.False(File.Exists(note), "the file was never trashed, so nothing was recorded");

        Assert.Equal("delete of notes.txt", ops.UndoDescription);
    }

    /// <summary>
    /// **What a redo would put back has its own name.** An implementation that
    /// answered the undo stack for both would read correctly in every state
    /// except the one that matters.
    /// </summary>
    [Fact]
    public async Task A_redo_is_named_after_what_it_would_put_back()
    {
        var note = File_("readme.txt");

        var ops = new LinuxFileOperations();

        await ops.RenameAsync(note, "notes.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.True(ops.CanRedo, "the rename should be redoable");

        Assert.Equal("rename of readme.txt", ops.RedoDescription);
        Assert.Null(ops.UndoDescription);
    }
}
