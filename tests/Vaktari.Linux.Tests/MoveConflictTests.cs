using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What "Keep both" and "Skip" really do to a move, and what undo puts back.
///
/// **Found by an audit; the Windows twin was fixed for both of these long ago
/// and the Linux copy never was, because there was no test project here at
/// all.** Two faults sharing one root — the plan's targets are decided before
/// any conflict is known about, and nothing recorded where anything actually
/// landed:
///
/// * Renaming a folder for "Keep both" left everything planned inside it still
///   pointing at the old name, so the new folder was created EMPTY while the
///   contents merged into the folder the user asked to keep separate. On a move
///   that is the source disappearing into it.
///
/// * Undo reconstructed each landing site as destination + name, true only when
///   nothing was renamed or skipped. Undoing a "Keep both" move relocated the
///   pre-existing bystander instead of the moved file; undoing a "Skip" moved
///   the very file the user had refused to move.
///
/// These run on any platform — the code under test is path arithmetic and
/// ordinary file I/O — which is what makes them runnable before CI sees them.
/// </summary>
public sealed class MoveConflictTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-moveconflict-" + Guid.NewGuid().ToString("N"));

    private readonly string _from;
    private readonly string _into;

    public MoveConflictTests()
    {
        _from = Path.Combine(_root, "from");
        _into = Path.Combine(_root, "into");

        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_into);
    }

    public void Dispose()
    {
        // Only what this test built, under its own root.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static ValueTask<ConflictResolution> Answer(ConflictResolution how)
        => ValueTask.FromResult(how);

    private static async Task Run(IOperationHandle handle)
    {
        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// **The one that loses data.** The contents must follow the folder that was
    /// renamed for them, not merge into the one already there.
    /// </summary>
    [Fact]
    public async Task Keeping_both_folders_puts_the_contents_in_the_new_one()
    {
        Write(Path.Combine(_from, "photos", "a.jpg"), "a");
        Write(Path.Combine(_from, "photos", "b.jpg"), "b");
        Write(Path.Combine(_into, "photos", "old.jpg"), "old");

        var ops = new LinuxFileOperations();

        await Run(ops.Move([Path.Combine(_from, "photos")], _into, _ => Answer(ConflictResolution.KeepBoth)));

        var kept = Path.Combine(_into, "photos (1)");

        Assert.True(Directory.Exists(kept), "the renamed folder should exist");
        Assert.True(File.Exists(Path.Combine(kept, "a.jpg")), "a.jpg belongs in the renamed folder");
        Assert.True(File.Exists(Path.Combine(kept, "b.jpg")), "b.jpg belongs in the renamed folder");

        // And the folder the user asked to keep separate is untouched.
        Assert.Equal(
            ["old.jpg"],
            Directory.GetFiles(Path.Combine(_into, "photos")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// Undo puts back what was moved. Reconstructing the landing from the name
    /// found the bystander instead.
    /// </summary>
    [Fact]
    public async Task Undoing_a_kept_both_move_puts_back_the_file_that_moved()
    {
        Write(Path.Combine(_from, "notes.txt"), "mine");
        Write(Path.Combine(_into, "notes.txt"), "theirs");

        var ops = new LinuxFileOperations();

        await Run(ops.Move([Path.Combine(_from, "notes.txt")], _into, _ => Answer(ConflictResolution.KeepBoth)));

        Assert.Equal("mine", File.ReadAllText(Path.Combine(_into, "notes (1).txt")));

        await ops.UndoAsync(CancellationToken.None);

        // Back where it came from...
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_from, "notes.txt")));

        // ...and the bystander never moved.
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_into, "notes.txt")));
    }

    /// <summary>
    /// **Skip is the sharper edge.** The user said "leave that one alone", and
    /// undo relocated exactly the file they were protecting.
    /// </summary>
    [Fact]
    public async Task Undoing_a_move_leaves_a_skipped_file_alone()
    {
        Write(Path.Combine(_from, "notes.txt"), "mine");
        Write(Path.Combine(_from, "other.txt"), "other");
        Write(Path.Combine(_into, "notes.txt"), "theirs");

        var ops = new LinuxFileOperations();

        await Run(ops.Move(
            [Path.Combine(_from, "notes.txt"), Path.Combine(_from, "other.txt")],
            _into,
            _ => Answer(ConflictResolution.Skip)));

        await ops.UndoAsync(CancellationToken.None);

        // The bystander is still there, still theirs.
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_into, "notes.txt")));

        // The skipped file never left home.
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_from, "notes.txt")));

        // And the one that did move came back.
        Assert.Equal("other", File.ReadAllText(Path.Combine(_from, "other.txt")));
    }

    /// <summary>
    /// Redo after an undo has to move the same thing forward again. It used to
    /// reconstruct a second time, find nothing, and quietly do nothing while
    /// the pane refreshed as though it had worked.
    /// </summary>
    [Fact]
    public async Task Redo_moves_it_forward_again()
    {
        Write(Path.Combine(_from, "notes.txt"), "mine");
        Write(Path.Combine(_into, "notes.txt"), "theirs");

        var ops = new LinuxFileOperations();

        await Run(ops.Move([Path.Combine(_from, "notes.txt")], _into, _ => Answer(ConflictResolution.KeepBoth)));
        await ops.UndoAsync(CancellationToken.None);

        Assert.True(ops.CanRedo);

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(_into, "notes (1).txt")));
        Assert.False(File.Exists(Path.Combine(_from, "notes.txt")));
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_into, "notes.txt")));
    }

    /// <summary>An ordinary move with no conflict still undoes cleanly — the
    /// case the old reconstruction got right, and which must stay right.</summary>
    [Fact]
    public async Task An_ordinary_move_still_undoes()
    {
        Write(Path.Combine(_from, "plain.txt"), "plain");

        var ops = new LinuxFileOperations();

        await Run(ops.Move([Path.Combine(_from, "plain.txt")], _into, _ => Answer(ConflictResolution.Overwrite)));

        Assert.True(File.Exists(Path.Combine(_into, "plain.txt")));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("plain", File.ReadAllText(Path.Combine(_from, "plain.txt")));
        Assert.False(File.Exists(Path.Combine(_into, "plain.txt")));
    }
}
