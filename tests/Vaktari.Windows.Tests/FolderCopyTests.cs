using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What happens to a FOLDER when the plan's target changes under it.
///
/// **Both faults here are one missing line and one unread list**, and both
/// destroy data while reporting success. Every existing test in this area used
/// a single file, which is exactly why neither was noticed: the plan for a file
/// is one item, so nothing has to travel down to anything.
///
///  - Duplicating a folder deduplicated the ROOT to "A - Copy" without
///    recording a redirect, so every descendant — planned against the original
///    name — hit the same branch and was deduplicated where it stood. The
///    result was an empty "A - Copy" beside an original now full of
///    "x - Copy.txt" twins.
///
///  - Answering Skip for a folder that already exists skipped the folder entry
///    only. Its files were still planned against the existing folder and went
///    in, merging two trees; on a MOVE the emptied source folder was then
///    deleted. Skip in Explorer and in Dolphin leaves the folder alone at both
///    ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FolderCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-foldercopy-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static async Task<IOperationHandle> Done(IOperationHandle handle)
    {
        await handle.Completion;
        return handle;
    }

    private static string[] NamesIn(string path)
        => Directory.GetFileSystemEntries(path).Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- duplicating a folder ----------------------------------------------

    /// <summary>
    /// Copying a folder into the folder it already lives in — Duplicate, or
    /// Ctrl+C then Ctrl+V without moving — is an everyday gesture, and it used
    /// to corrupt the source.
    /// </summary>
    [Fact]
    public async Task Duplicating_a_folder_copies_the_tree_and_leaves_the_original_alone()
    {
        var alpha = Dir("Alpha");
        File_(@"Alpha\x.txt");
        File_(@"Alpha\sub\y.txt");

        var ops = new WindowsFileOperations();

        await Done(ops.Copy([alpha], _root, _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        // The original is untouched: no " - Copy" twins littered inside it.
        Assert.Equal(["sub", "x.txt"], NamesIn(alpha));
        Assert.Equal(["y.txt"], NamesIn(Path.Combine(alpha, "sub")));

        // And the duplicate holds the whole tree rather than being empty.
        var copy = Path.Combine(_root, "Alpha - Copy");

        Assert.True(Directory.Exists(copy), "no duplicate was made");
        Assert.Equal(["sub", "x.txt"], NamesIn(copy));
        Assert.Equal(["y.txt"], NamesIn(Path.Combine(copy, "sub")));
    }

    // ---- skipping a folder --------------------------------------------------

    /// <summary>
    /// Skip means the folder is not touched. It used to mean "do not create the
    /// folder, but put everything into the one that is already there".
    /// </summary>
    [Fact]
    public async Task Skipping_a_folder_leaves_the_existing_one_untouched()
    {
        var source = Dir("source");
        File_(@"source\photos\holiday.jpg");
        File_(@"source\photos\notes.txt");

        var destination = Dir("destination");
        File_(@"destination\photos\already-here.jpg");

        var ops = new WindowsFileOperations();

        await Done(ops.Copy(
            [Path.Combine(source, "photos")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)));

        // Nothing merged in.
        Assert.Equal(["already-here.jpg"], NamesIn(Path.Combine(destination, "photos")));
    }

    /// <summary>
    /// **The destructive half.** On a move, the source folder was emptied into
    /// the existing one and then deleted for being empty — so answering "Skip"
    /// lost the folder entirely.
    /// </summary>
    [Fact]
    public async Task Skipping_a_folder_on_a_move_does_not_consume_the_source()
    {
        var source = Dir("source");
        File_(@"source\photos\holiday.jpg");
        File_(@"source\photos\notes.txt");

        var destination = Dir("destination");
        File_(@"destination\photos\already-here.jpg");

        var ops = new WindowsFileOperations();

        await Done(ops.Move(
            [Path.Combine(source, "photos")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)));

        var kept = Path.Combine(source, "photos");

        Assert.True(Directory.Exists(kept), "THE SOURCE FOLDER WAS CONSUMED BY A SKIP");
        Assert.Equal(["holiday.jpg", "notes.txt"], NamesIn(kept));
        Assert.Equal(["already-here.jpg"], NamesIn(Path.Combine(destination, "photos")));
    }

    /// <summary>
    /// Keep both still works and still carries the rename down — the branch
    /// that was always right, pinned so the new one cannot break it.
    /// </summary>
    [Fact]
    public async Task Keeping_both_still_puts_the_tree_in_the_new_folder()
    {
        var source = Dir("source");
        File_(@"source\photos\holiday.jpg");

        var destination = Dir("destination");
        File_(@"destination\photos\already-here.jpg");

        var ops = new WindowsFileOperations();

        await Done(ops.Copy(
            [Path.Combine(source, "photos")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        Assert.Equal(["already-here.jpg"], NamesIn(Path.Combine(destination, "photos")));
        Assert.Equal(["holiday.jpg"], NamesIn(Path.Combine(destination, "photos (2)")));
    }

    /// <summary>
    /// A folder whose name is a prefix of the skipped one must not be swept up
    /// with it — the separator is part of the containment test.
    /// </summary>
    [Fact]
    public async Task A_folder_whose_name_merely_starts_the_same_is_not_skipped()
    {
        var source = Dir("source");
        File_(@"source\work\a.txt");
        File_(@"source\work 2\b.txt");

        var destination = Dir("destination");
        File_(@"destination\work\already-here.txt");

        var ops = new WindowsFileOperations();

        await Done(ops.Copy(
            [Path.Combine(source, "work"), Path.Combine(source, "work 2")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)));

        Assert.Equal(["already-here.txt"], NamesIn(Path.Combine(destination, "work")));
        Assert.Equal(["b.txt"], NamesIn(Path.Combine(destination, "work 2")));
    }

    // ---- a folder cannot go inside itself -----------------------------------

    /// <summary>
    /// **The plan is built by walking the source**, so a destination inside it
    /// feeds the copy its own output; on a move it dismantles the tree it is
    /// halfway through reading. Explorer and Dolphin both refuse.
    /// </summary>
    [Fact]
    public async Task A_folder_cannot_be_copied_into_itself()
    {
        var alpha = Dir("Alpha");
        File_(@"Alpha\x.txt");

        var ops = new WindowsFileOperations();
        var handle = await Done(ops.Copy([alpha], alpha, _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(["x.txt"], NamesIn(alpha));
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_its_own_subfolder()
    {
        var alpha = Dir("Alpha");
        var inside = Dir("Alpha", "sub");
        File_(@"Alpha\x.txt");

        var ops = new WindowsFileOperations();
        var handle = await Done(ops.Move([alpha], inside, _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.True(Directory.Exists(alpha), "the source folder was consumed");
        Assert.Equal(["sub", "x.txt"], NamesIn(alpha));
        Assert.Empty(NamesIn(inside));
    }

    /// <summary>
    /// Dropping a selection onto a folder that is IN that selection is the same
    /// shape — the destination is one of the sources — and a six-pixel twitch
    /// over a selected folder was enough to start it.
    /// </summary>
    [Fact]
    public async Task Dropping_a_selection_onto_one_of_its_own_folders_is_refused()
    {
        var target = Dir("target");
        var friend = File_(@"friend.txt");

        var ops = new WindowsFileOperations();
        var handle = await Done(ops.Move(
            [target, friend], target, _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.True(System.IO.File.Exists(friend), "the sibling was moved into the folder");
        Assert.Empty(NamesIn(target));
    }

    /// <summary>
    /// And copying into the PARENT is untouched, which is what makes Duplicate
    /// work — the guard must not reach it.
    /// </summary>
    [Fact]
    public async Task Copying_a_folder_into_its_parent_is_still_allowed()
    {
        var alpha = Dir("Alpha");
        File_(@"Alpha\x.txt");

        var ops = new WindowsFileOperations();
        var handle = await Done(ops.Copy([alpha], _root, _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));

        Assert.NotEqual(OperationState.Failed, handle.State);
        Assert.True(Directory.Exists(Path.Combine(_root, "Alpha - Copy")));
    }

}
