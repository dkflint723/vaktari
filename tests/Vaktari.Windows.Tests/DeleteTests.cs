using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Permanent deletion, and the Windows attribute that refuses it.
///
/// **The bug.** A read-only file will not delete on Windows, where on Linux it
/// would: the permission is on the file rather than on its directory.
/// `Delete` knew that and cleared the attribute — on the top-level path only.
/// `Directory.Delete(recursive: true)` then stopped at the first read-only file
/// *inside* the tree and threw, leaving half the tree removed and half of it
/// standing. Deleting a single read-only file worked, which is what made it
/// look handled.
///
/// One `git clone` produces such a tree: git writes its pack files read-only.
///
/// **<see cref="IFileOperations.Trash"/> is not covered here**, and that is a
/// gap rather than an oversight. Exercising it means putting real items in the
/// Recycle Bin, and this application cannot take them back out again —
/// `ITrashMaintenance` is still null, which is the same missing half that
/// leaves it with no Trash view and no Restore. A test that permanently
/// littered the developer's bin to prove a progress counter is not worth it.
/// </summary>
[SupportedOSPlatform("windows")]
public class DeleteTests
{
    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        return handle;
    }

    /// <summary>The case that always worked, kept so the fix cannot regress it.</summary>
    [WindowsFact]
    public async Task A_read_only_file_is_deleted()
    {
        using var tree = new TempTree();
        var file = tree.WriteReadOnly("locked.txt");

        await Finished(new WindowsFileOperations().Delete([file]));

        Assert.False(tree.Exists("locked.txt"));
    }

    [WindowsFact]
    public async Task A_tree_holding_a_read_only_file_is_deleted_completely()
    {
        using var tree = new TempTree();
        tree.Write("repo/HEAD");
        tree.WriteReadOnly("repo/objects/pack/pack-abc.idx");
        tree.WriteReadOnly("repo/objects/pack/pack-abc.pack");
        tree.Write("repo/objects/loose/aa/bbbb");

        await Finished(new WindowsFileOperations().Delete([tree.At("repo")]));

        Assert.False(tree.Exists("repo"));
    }

    /// <summary>
    /// A read-only *directory* refuses to go for the same reason its files do,
    /// and is worth its own case because clearing the attribute on files alone
    /// would still leave this one standing.
    /// </summary>
    [WindowsFact]
    public async Task A_tree_holding_a_read_only_folder_is_deleted_completely()
    {
        using var tree = new TempTree();
        var inner = tree.Dir("outer", "inner");
        tree.Write("outer/inner/a.txt");
        File.SetAttributes(inner, File.GetAttributes(inner) | FileAttributes.ReadOnly);

        await Finished(new WindowsFileOperations().Delete([tree.At("outer")]));

        Assert.False(tree.Exists("outer"));
    }

    /// <summary>
    /// **A junction inside the folder left the folder standing with everything
    /// inside it already gone.** .NET's own recursive delete, reaching a child
    /// whose reparse tag is IO_REPARSE_TAG_MOUNT_POINT — junctions share that
    /// tag with volume mount points — called DeleteVolumeMountPoint on it,
    /// which an unelevated caller is refused. It recorded that error, removed
    /// the junction with RemoveDirectory anyway, emptied the rest of the
    /// folder, then threw and never removed the top folder.
    ///
    /// Measured on 12 September 2026, .NET 10.0.12, unelevated: deleting a
    /// folder holding `a.txt` and a junction reported
    /// "Access to the path 'j' is denied", left the folder standing and both
    /// its children gone. The person is told the delete failed, offered
    /// "Retry as administrator", and everything they asked to delete has gone
    /// regardless. A `node_modules` tree is the everyday way to own one.
    /// </summary>
    [WindowsFact]
    public async Task A_tree_holding_a_junction_is_deleted_completely()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Write("outside/nested/also-kept.txt", "elsewhere too");

        tree.Write("project/a.txt", "mine");
        tree.Write("project/node_modules/.bin/tool.cmd", "mine too");
        tree.Junction("project/node_modules/shared", outside);

        var handle = await Finished(new WindowsFileOperations().Delete([tree.At("project")]));

        // Nothing to report and nothing to offer: the delete did what it said.
        Assert.Empty(handle.Problems);
        Assert.Null(handle.Retry);
        Assert.False(tree.Exists("project"));

        // And the tree the junction pointed at is not what was deleted.
        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
        Assert.Equal("elsewhere too", tree.Read("outside", "nested", "also-kept.txt"));
    }

    /// <summary>
    /// The junction the person selected themselves. Directory.Exists answers
    /// true for it, so a walk that tests that first descends into whatever it
    /// points at and deletes a tree nobody selected — which is the failure
    /// <see cref="ReparsePointTests"/> pins for the copy and move engines, and
    /// the one a hand-written recursive delete is most likely to reintroduce.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_deleted_on_its_own_leaves_what_it_points_at()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Write("outside/nested/also-kept.txt", "elsewhere too");
        var link = tree.Junction("link", outside);

        var handle = await Finished(new WindowsFileOperations().Delete([link]));

        Assert.Empty(handle.Problems);
        Assert.False(tree.Exists("link"));

        Assert.Equal(["kept.txt", "nested"], tree.Names("outside"));
        Assert.Equal("elsewhere too", tree.Read("outside", "nested", "also-kept.txt"));
    }

    /// <summary>
    /// **What is left when something in the tree will not go**, which the tree
    /// delete answers differently from the framework's. Directory.Delete
    /// removes everything it can reach and reports the failure afterwards, so
    /// one file held open by another program cost the whole tree around it and
    /// left the person with the one file they could not have deleted anyway.
    /// The walk stops where it fails instead: what it has not reached is still
    /// there, and the item is named and offered back for a retry.
    ///
    /// Held open with no sharing, which is how another program holds a file it
    /// is reading. The file the walk reaches BEFORE the held one is not
    /// asserted either way — that would be pinning the order NTFS happens to
    /// enumerate in, and the rule is about what comes after.
    /// </summary>
    [WindowsFact]
    public async Task A_file_that_will_not_go_leaves_the_rest_of_its_tree_standing()
    {
        using var tree = new TempTree();
        tree.Write("project/a.txt", "mine");
        var busy = tree.Write("project/busy.txt", "in use");
        tree.Write("project/sub/deep.txt", "deeper");

        using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None);

        var handle = await Finished(new WindowsFileOperations().Delete([tree.At("project")]));

        // Named, and offered back, rather than reported done.
        var problem = Assert.Single(handle.Problems);
        Assert.Equal(tree.At("project"), problem.Path);
        Assert.NotNull(handle.Retry);

        // The folder stands, with the file that would not go still in it.
        Assert.Equal("in use", tree.Read("project", "busy.txt"));

        // And nothing the walk had not reached was touched.
        Assert.Equal("deeper", tree.Read("project", "sub", "deep.txt"));
    }

    [WindowsFact]
    public async Task Deleting_a_plain_tree_still_works()
    {
        using var tree = new TempTree();
        tree.Write("a/b/c.txt");
        tree.Write("a/d.txt");

        await Finished(new WindowsFileOperations().Delete([tree.At("a")]));

        Assert.False(tree.Exists("a"));
    }
}
