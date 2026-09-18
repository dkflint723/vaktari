using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Undoing the move of a folder puts back what the move took, and nothing else.
///
/// **The undo of a moved folder went through the trash's cross-device route,
/// which copied whatever a rename refused.** Directory.Move refused a source
/// folder that was standing again, the fallback copied the moved tree into it
/// following every link, and then removed the landed tree recursively.
/// Measured on 2026-09-12 in WSL Fedora before this change: a file written at
/// the source since stopped the undo part-way with copies left in both places
/// and Ctrl+Z and Ctrl+Y both gone; links came back as full copies of what
/// they pointed at; a dangling link stopped the undo part-way; and a redo
/// after an undo that merged moved the whole re-created folder, taking a file
/// nobody had moved.
///
/// Its own root and its own trash, as <see cref="UndoMergeTests"/> has.
/// </summary>
public sealed class UndoMovedFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-undomoved-" + Guid.NewGuid().ToString("N"));

    private readonly string? _dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public UndoMovedFolderTests()
    {
        Directory.CreateDirectory(At("data"));
        Directory.CreateDirectory(At("dst"));
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

    /// <summary>The move of src/A into dst, which must have left nothing
    /// behind, or what follows measures something else.</summary>
    private async Task Moved(LinuxFileOperations ops)
    {
        var handle = ops.Move([At("src", "A")], At("dst"), Overwrite);

        await handle.Completion;

        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        Assert.False(Directory.Exists(At("src", "A")), "the move left its source standing, so this measures nothing");
    }

    private static Task<Exception?> Undo(LinuxFileOperations ops)
        => Record.ExceptionAsync(() => ops.UndoAsync(CancellationToken.None).AsTask());

    /// <summary>
    /// A rename that answers "another device" for one leaf name and does the
    /// real thing for every other, so the arm that copies can be reached with
    /// one temp folder instead of two filesystems.
    /// </summary>
    private static Func<string, string, int> Elsewhere(string leaf)
        => (from, to) =>
        {
            if (Path.GetFileName(from) == leaf) return Renames.NotSameDevice;

            return Renames.WithoutReplacing(from, to);
        };

    /// <summary>
    /// **A file written at the source since is not replaced, and nothing is
    /// left in two places.** Everything that can go back goes back and leaves
    /// the destination; the moved file whose name is taken stays where the move
    /// put it; the undo says so; and once the way is clear, Ctrl+Z finishes.
    /// </summary>
    [PosixFact]
    public async Task Undoing_a_move_never_replaces_a_file_written_at_the_source_since()
    {
        Write("src/A/a.txt", "moved");
        Write("src/A/b.txt", "moved too");
        Write("src/A/sub/c.txt", "moved three");
        var ops = new LinuxFileOperations();

        await Moved(ops);
        Write("src/A/b.txt", "written since");

        var said = await Undo(ops);

        Assert.Equal("written since", File.ReadAllText(At("src", "A", "b.txt")));
        Assert.Equal("moved too", File.ReadAllText(At("dst", "A", "b.txt")));

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.Equal("moved three", File.ReadAllText(At("src", "A", "sub", "c.txt")));

        Assert.False(File.Exists(At("dst", "A", "a.txt")), "a.txt went back and was also left at the destination");
        Assert.False(Directory.Exists(At("dst", "A", "sub")), "sub went back and was also left at the destination");

        Assert.Contains("b.txt", Assert.IsAssignableFrom<IOException>(said).Message);
        Assert.True(ops.CanUndo, "what could not go back is no longer undoable");

        File.Delete(At("src", "A", "b.txt"));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved too", File.ReadAllText(At("src", "A", "b.txt")));
        Assert.False(Directory.Exists(At("dst", "A")), "the emptied destination folder was left behind");
    }

    /// <summary>
    /// **A redo moves only what the undo put back.** The undo merged into a
    /// folder somebody had re-created at the source, holding a file of their
    /// own; the redo used to move that whole folder, their file with it.
    /// </summary>
    [PosixFact]
    public async Task Redoing_an_undo_that_merged_leaves_what_was_already_at_the_source()
    {
        Write("src/A/a.txt", "moved");
        var ops = new LinuxFileOperations();

        await Moved(ops);
        Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("somebody else's", File.ReadAllText(At("src", "A", "other.txt")));
        Assert.Equal("moved", File.ReadAllText(At("dst", "A", "a.txt")));
        Assert.False(File.Exists(At("dst", "A", "other.txt")), "the redo moved a file the undo never put back");
    }

    /// <summary>
    /// **Links come back as links.** A link to a folder and a link to a file
    /// inside the moved folder used to come back as full copies of what they
    /// pointed at, a photo library duplicated whole into the source folder.
    /// </summary>
    [PosixFact]
    public async Task Undoing_a_move_puts_links_back_as_links()
    {
        Write("library/photo.jpg", "photo");
        Write("library/more/raw.cr2", "raw");
        Write("elsewhere/note.txt", "note");
        Write("src/A/a.txt", "moved");
        Directory.CreateSymbolicLink(At("src", "A", "lib"), At("library"));
        File.CreateSymbolicLink(At("src", "A", "note"), At("elsewhere", "note.txt"));
        var ops = new LinuxFileOperations();

        await Moved(ops);
        Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(At("library"), new DirectoryInfo(At("src", "A", "lib")).LinkTarget);
        Assert.Equal(At("elsewhere", "note.txt"), new FileInfo(At("src", "A", "note")).LinkTarget);

        Assert.Equal(["more", "photo.jpg"],
            Directory.EnumerateFileSystemEntries(At("library")).Select(p => Path.GetFileName(p)).Order());

        Assert.False(Directory.Exists(At("dst", "A")), "the emptied destination folder was left behind");
    }

    /// <summary>
    /// **A folder the move made travels back whole, and forward whole again**,
    /// carrying a file somebody saved into it between the two presses. Moving a
    /// folder moves what is in it now, and that has to be as true of the redo as
    /// of the move.
    ///
    /// Run twice, once where the folder is renamed back and once where the
    /// rename answers "another device" so the walk copies child by child
    /// instead. The two arms must agree: which one runs turns on where the
    /// destination is, and a person cannot see that.
    /// </summary>
    [PosixFact]
    public async Task A_folder_the_move_made_travels_whole_whichever_arm_runs()
    {
        await Whole(elsewhere: false);

        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(At("data"));
        Directory.CreateDirectory(At("dst"));

        await Whole(elsewhere: true);
    }

    private async Task Whole(bool elsewhere)
    {
        var arm = elsewhere ? "the walk" : "the one rename";

        Write("src/A/a.txt", "moved");

        var ops = new LinuxFileOperations { RenameForUndo = elsewhere ? Elsewhere("A") : null };

        await Moved(ops);

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.False(Directory.Exists(At("dst", "A")), $"{arm}: the emptied destination folder was left behind");

        Write("src/A/after.txt", "saved since");

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("dst", "A", "a.txt")));
        Assert.Equal("saved since", File.ReadAllText(At("dst", "A", "after.txt")));
        Assert.False(Directory.Exists(At("src", "A")), $"{arm}: the folder was left standing at the source");
    }

    /// <summary>
    /// **A file that cannot be renamed back travels instead, wearing what it
    /// wore.** Across a device boundary there is no rename to be had, so the
    /// file is copied beside its name under a staging name, given its extended
    /// attributes and then its mode — in that order, because a restrictive mode
    /// set first refuses the attributes that follow — renamed in without
    /// replacing, and only then removed from where it was.
    ///
    /// Reached through RenameForUndo rather than by finding two filesystems: the
    /// seam answers EXDEV for this one file and does the real rename for
    /// everything else, so the arm runs with one temp folder.
    /// </summary>
    [PosixFact]
    public async Task A_file_that_cannot_be_renamed_back_travels_with_its_mode()
    {
        Write("src/A/a.txt", "moved");
        Write("src/A/b.txt", "moved too");

        var wore = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(At("src", "A", "a.txt"), wore);

        // **Asserted as well as the mode, because File.Copy carries the mode by
        // itself on Unix.** Measured by revert-check: with FileMetadata.Carry
        // deleted the mode still arrived, so a test that asks only about the
        // mode proves nothing about the line that carries it. The time is what
        // a plain copy loses.
        var dated = new DateTime(2019, 3, 7, 11, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(At("src", "A", "a.txt"), dated);

        var ops = new LinuxFileOperations { RenameForUndo = Elsewhere("a.txt") };

        await Moved(ops);

        // Standing again, so the walk merges into it rather than renaming the
        // folder back whole — which is what puts the file on its own arm.
        Directory.CreateDirectory(At("src", "A"));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.Equal(wore, File.GetUnixFileMode(At("src", "A", "a.txt")));
        Assert.Equal(dated, File.GetLastWriteTimeUtc(At("src", "A", "a.txt")));
        Assert.Equal("moved too", File.ReadAllText(At("src", "A", "b.txt")));

        Assert.False(File.Exists(At("dst", "A", "a.txt")), "the file travelled and was also left behind");
        Assert.False(Directory.Exists(At("dst", "A")), "the emptied destination folder was left behind");

        // Nothing of the walk's own making is left lying beside it.
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(At("src", "A")).Select(Path.GetFileName),
            name => name!.Contains("vaktari-", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The same bystander, by the arm the test above cannot reach.** When the
    /// rename answers "another device" rather than "that name is taken", the
    /// walk runs with the source folder standing — and it must still not treat
    /// the folder as its own. Whether the name was free is what making it
    /// answers, never what the caller meant to do.
    ///
    /// Measured on the Windows twin before it was fixed: keyed on the intention,
    /// the redo took the whole folder and carried off a file nobody had moved.
    /// The merged test above stays green over that, because its rename comes
    /// back EEXIST and never reaches this arm.
    /// </summary>
    [PosixFact]
    public async Task Redoing_leaves_the_bystander_when_the_rename_says_another_device()
    {
        Write("src/A/a.txt", "moved");
        var ops = new LinuxFileOperations { RenameForUndo = Elsewhere("A") };

        await Moved(ops);

        Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.Equal("somebody else's", File.ReadAllText(At("src", "A", "other.txt")));

        await ops.RedoAsync(CancellationToken.None);

        Assert.Equal("moved", File.ReadAllText(At("dst", "A", "a.txt")));
        Assert.Equal("somebody else's", File.ReadAllText(At("src", "A", "other.txt")));
        Assert.False(File.Exists(At("dst", "A", "other.txt")), "the redo carried off a file the undo never put back");
    }

    /// <summary>
    /// **A name that turns up between the look and the rename is not replaced.**
    /// .NET has no rename that refuses a taken name — File.Move without
    /// overwrite looks first and renames after — and measured in this
    /// distribution a file created in that window was replaced in about 17,000
    /// of 30,000 attempts. The walk asks the kernel to decide in one call
    /// instead, so the window is not there to lose.
    ///
    /// The seam plants the file and then hands the same rename to the real
    /// renameat2, returning what it returns: planting and answering 0 itself
    /// would record the step as done with the entry still where it was, which is
    /// a worse fault than the one this is about, and the test would pass while
    /// asserting nothing.
    /// </summary>
    [PosixFact]
    public async Task A_name_that_turns_up_between_the_look_and_the_rename_is_not_replaced()
    {
        Write("src/A/a.txt", "moved");
        var ops = new LinuxFileOperations
        {
            RenameForUndo = (from, to) =>
            {
                if (Path.GetFileName(from) == "a.txt" && !File.Exists(to))
                    File.WriteAllText(to, "arrived in the window");

                return Renames.WithoutReplacing(from, to);
            },
        };

        await Moved(ops);

        Directory.CreateDirectory(At("src", "A"));

        var said = await Undo(ops);

        Assert.Equal("arrived in the window", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.Equal("moved", File.ReadAllText(At("dst", "A", "a.txt")));

        Assert.IsAssignableFrom<IOException>(said);
    }

    /// <summary>
    /// **One undo at a time, and none offered while one runs.** Both walks move
    /// things on disk, and two at once would interleave renames over the same
    /// names — holding Ctrl+Z down is enough to ask for it. The second press is
    /// dropped rather than queued: a press remembered and applied later is
    /// applied to a folder the person has stopped looking at.
    ///
    /// Held open at the rename seam, which is the only place the walk waits for
    /// anything, so the state can be read while it is genuinely mid-walk.
    /// </summary>
    [PosixFact]
    public async Task While_one_undo_walks_no_other_is_offered_or_runs()
    {
        Write("src/A/a.txt", "moved");
        Write("src/A/b.txt", "moved too");

        using var inside = new SemaphoreSlim(0);
        using var released = new SemaphoreSlim(0);

        var ops = new LinuxFileOperations
        {
            RenameForUndo = (from, to) =>
            {
                if (Path.GetFileName(from) == "a.txt")
                {
                    inside.Release();
                    released.Wait();
                }

                return Renames.WithoutReplacing(from, to);
            },
        };

        // **An older step underneath, or this proves nothing.** The walk pops
        // the step it is running before it starts, so with one on the stack
        // CanUndo answers false while it walks whether it is gated or not —
        // measured on the Windows twin, as a mutation that stayed green.
        Write("src/old.txt", "the older move");
        var older = ops.Move([At("src", "old.txt")], At("dst"), Overwrite);
        await older.Completion;
        Assert.Null(older.Error);

        await Moved(ops);
        Directory.CreateDirectory(At("src", "A"));

        var walking = ops.UndoAsync(CancellationToken.None).AsTask();

        Assert.True(await inside.WaitAsync(TimeSpan.FromSeconds(10)), "the walk never reached the seam");

        Assert.False(ops.CanUndo, "Ctrl+Z was still offered while a walk was running");
        Assert.False(ops.CanRedo, "Ctrl+Y was still offered while a walk was running");

        // A second press while the first is walking: it does nothing at all,
        // rather than starting a second walk over the same names.
        await ops.UndoAsync(CancellationToken.None);

        released.Release();
        await walking;

        Assert.Equal("moved", File.ReadAllText(At("src", "A", "a.txt")));
        Assert.Equal("moved too", File.ReadAllText(At("src", "A", "b.txt")));

        // Offered again once it has finished, and the press that arrived
        // mid-walk was dropped rather than applied late: the older move is still
        // where it landed, and still the next thing Ctrl+Z would reach.
        Assert.True(ops.CanRedo, "the finished walk left nothing to redo");
        Assert.True(ops.CanUndo, "the older step went with the press that was dropped");
        Assert.Equal("the older move", File.ReadAllText(At("dst", "old.txt")));
    }

    /// <summary>A link whose target has gone is still something the move took,
    /// and it comes back as the same link rather than stopping the undo.</summary>
    [PosixFact]
    public async Task Undoing_a_move_puts_back_a_link_whose_target_has_gone()
    {
        Write("src/A/a.txt", "moved");
        File.CreateSymbolicLink(At("src", "A", "gone"), At("nowhere", "x"));
        Write("src/A/z.txt", "moved too");
        var ops = new LinuxFileOperations();

        await Moved(ops);
        Write("src/A/other.txt", "somebody else's");

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(At("nowhere", "x"), new FileInfo(At("src", "A", "gone")).LinkTarget);
        Assert.Equal("moved too", File.ReadAllText(At("src", "A", "z.txt")));

        Assert.False(Directory.Exists(At("dst", "A")), "the emptied destination folder was left behind");
    }
}
