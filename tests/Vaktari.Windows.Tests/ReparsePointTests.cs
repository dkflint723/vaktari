using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Junctions, which look exactly like folders to a recursive walk and are not.
///
/// **The bug.** `BuildPlan` enumerated with `SearchOption.AllDirectories`, whose
/// options set `AttributesToSkip = 0`, so a junction was descended into like any
/// other directory. Three things follow, in increasing order of harm: a copy
/// silently duplicates whatever tree the junction points at; a move then deletes
/// the originals out of that tree, which the user never selected and may not
/// even know is involved; and a junction resolving back to an ancestor makes the
/// walk run until the path length stops it.
///
/// The two other recursive walks in this assembly already refused to follow
/// them — `WindowsSearchProvider` skips the attribute outright, and
/// `WindowsPropertiesProvider` notes that "a junction can point at an ancestor,
/// and following one turns a measurement into a loop". This was the walk that
/// did not.
/// </summary>
[SupportedOSPlatform("windows")]
public class ReparsePointTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Overwrite
        => _ => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        // Generous, but bounded: the failure this guards against is a walk that
        // does not terminate, and a hung test run reports nothing useful.
        var done = await Task.WhenAny(handle.Completion, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(done == handle.Completion, "the operation did not finish — the walk is still recursing");

        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        return handle;
    }

    [WindowsFact]
    public async Task A_copy_does_not_recurse_through_a_junction_that_points_at_its_own_parent()
    {
        using var tree = new TempTree();
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt", "content");
        tree.Junction("tree/loop", source);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([source], tree.At("dst"), Overwrite));

        Assert.Equal("content", tree.Read("dst", "tree", "real.txt"));
    }

    /// <summary>
    /// Reproduced rather than followed — and reproduced as a junction, because
    /// `Directory.CreateSymbolicLink` needs Developer Mode and would make this
    /// fail on a machine in its default configuration.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_is_copied_as_a_junction()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        var source = tree.Dir("tree");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([source], tree.At("dst"), Overwrite));

        var landed = new DirectoryInfo(tree.At("dst", "tree", "link"));
        Assert.True(landed.Exists);
        Assert.Equal(outside, landed.LinkTarget);
    }

    /// <summary>
    /// **A junction moved onto a taken file, answered Overwrite, replaces it.**
    /// The answer was not carried out: CopyLink made a junction by first making
    /// a folder at the name, which a file there refused, so the item failed
    /// with the junction still at the source and the file still at the name,
    /// whatever had been asked — measured. The Linux engine lost the link in the
    /// same place. The answer is carried out now, and the source goes only once
    /// the junction stands at the name.
    /// </summary>
    /// <summary>
    /// **This application never writes a junction that cannot say where it
    /// points.** A junction stores an object-manager name — <c>\??\</c> and a
    /// local path — so a UNC share cannot be expressed as one. The kernel does
    /// not refuse it: measured, FSCTL_SET_REPARSE_POINT accepts
    /// <c>\\127.0.0.1\C$\Windows</c>, the entry answers Directory.Exists true
    /// and reads its target back correctly, and entering it throws "the
    /// filename, directory name, or volume label syntax is incorrect".
    ///
    /// So reproducing such a link produced one that looked right and was
    /// unusable, and the fallback that exists for a junction which cannot
    /// express the target never ran, because nothing threw. What is asked now
    /// is whether a junction can SAY it, not whether the path is fully
    /// qualified — a UNC path is both fully qualified and unsayable.
    ///
    /// Either a working link arrives or the copy refuses; a junction pointing
    /// at a share is the one outcome ruled out. Unprivileged, the refusal is
    /// what happens, because the other road is a symbolic link.
    /// </summary>
    [WindowsFact]
    public async Task A_link_to_a_share_is_never_reproduced_as_a_junction()
    {
        using var tree = new TempTree();
        var source = tree.Dir("from", "share");

        // The fixture the probe showed how to build: the kernel accepts this.
        Native.CreateJunction(source, @"\\127.0.0.1\C$\Windows");

        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([source], tree.At("dst"), Overwrite));

        var landed = tree.At("dst", "share");

        if (!Path.Exists(landed)) return;   // refused out loud, which is the other allowed answer

        var target = new DirectoryInfo(landed).LinkTarget;

        Assert.False(
            ReparseTags.Of(landed) == 0xA0000003u && target is not null && target.StartsWith(@"\\", StringComparison.Ordinal),
            $"a junction was written pointing at {target}, which a junction cannot say");
    }

    /// <summary>
    /// **A folder wearing a tag that is not a link is still a folder.** These
    /// operations decided what a link was by the ReparsePoint attribute alone,
    /// while the walk stopped believing that in 0633df3 — a link is a reparse
    /// point whose tag is a NAME SURROGATE, and the app execution aliases under
    /// WindowsApps, cloud placeholders and third-party tags carry the attribute
    /// without being links.
    ///
    /// The disagreement decided whether a folder could be written over: the
    /// refusal that protects one is skipped for anything the old answer called a
    /// link, so a folder holding a tag no filter owns was replaced by a link
    /// rather than refused.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_wearing_a_tag_that_is_not_a_link_is_not_replaced_by_one()
    {
        // **The rule only exists where the tag can be read**, and reading one is
        // a call into the operating system that Core may not make — so the
        // platform adopts the reader and the walk asks through it. Adopted here
        // the way WindowsPlatform does, and left adopted, as SafeWalkLinkTests
        // does: every walk in this assembly wants the platform's answer, and
        // without it "a link" falls back to the attribute this test is about.
        SafeWalk.ReparseTag = ReparseTags.Of;

        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");

        var link = tree.Junction("from/blocked", outside);

        // An empty folder at the destination name, wearing a third-party tag: a
        // reparse point, and not a link.
        var blocked = tree.Dir("dst", "blocked");
        ReparseFixture.SetThirdParty(blocked, directory: true);

        try
        {
            await Finished(new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

            // Asked of the disk rather than of the handle: an operation can
            // finish while refusing one item, and what matters is that the
            // folder is still the folder.
            Assert.Equal(
                0x00001234u,
                ReparseTags.Of(blocked) ?? 0u);

            Assert.True(Path.Exists(link), "the junction was taken away although it never landed");
        }
        finally
        {
            // Tolerant on purpose: if the move DID replace the folder, the tag
            // standing there is no longer the one this put on, and a throwing
            // finally would replace the assertion that says so.
            try { ReparseFixture.RemoveThirdParty(blocked, directory: true); }
            catch (Exception) { /* the assertion above is the news */ }
        }
    }

    /// <summary>
    /// **A read-only junction being replaced left its old self behind.** The
    /// replace dance renames what is at the name aside, lands the new link, and
    /// removes the one it set aside — but the removal cleared the read-only mark
    /// only when File.Exists said so, and File.Exists is false for every
    /// directory link. So the mark survived into Directory.Delete, which refuses
    /// one, and the refusal was swallowed because tidying must not report a
    /// landed link as a failure. What was left was a staging name in the
    /// person's folder, for ever.
    /// </summary>
    [WindowsFact]
    public async Task Replacing_a_read_only_junction_leaves_nothing_of_it_behind()
    {
        using var tree = new TempTree();
        var first = tree.Dir("first");
        var second = tree.Dir("second");
        tree.Write("first/a.txt", "the first");
        tree.Write("second/b.txt", "the second");

        var standing = tree.Junction("dst/link", first);
        File.SetAttributes(standing, File.GetAttributes(standing) | FileAttributes.ReadOnly);

        var arriving = tree.Junction("from/link", second);

        try
        {
            await Finished(new WindowsFileOperations().Move([arriving], tree.At("dst"), Overwrite));

            Assert.Equal(second, new DirectoryInfo(standing).LinkTarget);

            // Nothing of the walk's own making is left in the folder.
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(tree.At("dst")).Select(Path.GetFileName),
                name => name!.Contains("vaktari-", StringComparison.Ordinal));

            Assert.Equal("the first", tree.Read("first", "a.txt"));
        }
        finally
        {
            if (Path.Exists(standing))
                File.SetAttributes(standing, File.GetAttributes(standing) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// **A junction moved into its own folder under a second name is not lost.**
    /// The guard that catches a paste into the folder it already lives in
    /// compared path TEXT, so a junction standing beside that folder and
    /// pointing at it walked straight past: the name was then taken by the entry
    /// itself, the clash was answered, CopyLink did its replace dance on the
    /// source's own directory entry, and the DeleteLink after it removed what
    /// had just landed. The junction was gone from both names with the operation
    /// reporting Completed.
    ///
    /// The same fault 3c9a45c ended for a link moved into a DIFFERENT folder
    /// reached by another name, in the case its tests did not cover. Measured on
    /// the Linux twin first, where a symlinked folder is the everyday way to
    /// hold two names for one place.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_moved_into_its_own_folder_under_a_second_name_is_not_lost()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");

        var from = tree.Dir("from");
        var link = tree.Junction("from/link", outside);

        // The link's OWN folder, under a second name.
        var alias = tree.At("alias");
        tree.Junction("alias", from);

        await Finished(new WindowsFileOperations().Move([link], alias, Overwrite));

        Assert.True(
            Path.Exists(link) || Path.Exists(Path.Combine(alias, "link")),
            "the junction was deleted from the only folder it was ever in");

        Assert.True(
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "what is standing at the name is no longer a junction");

        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
    }

    [WindowsFact]
    public async Task Moving_a_junction_onto_a_taken_file_replaces_it()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");

        var link = tree.Junction("from/link", outside);

        tree.Dir("dst");
        tree.Write("dst/link", "somebody else's");

        var handle = await Finished(
            new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

        var landed = tree.At("dst", "link");

        // Answered Overwrite, so the answer is carried out: the junction is
        // standing at the name, pointing where it always did.
        Assert.True(
            (File.GetAttributes(landed) & FileAttributes.ReparsePoint) != 0,
            "the answer was Overwrite, so the junction should be standing there now");

        Assert.Equal(outside, new DirectoryInfo(landed).LinkTarget);
        Assert.False(Directory.Exists(link), "it was moved, so the source should have gone");

        Assert.Equal([landed], handle.Landed);
        Assert.Empty(handle.Problems);

        // What it pointed at is untouched, as ever.
        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
    }

    /// <summary>
    /// **A folder at the name is refused, never turned into a junction.**
    /// Overwrite asks for a file to be replaced; a folder is somebody's, and
    /// making one into a link — or emptying it to make room for one — is a
    /// larger act than the answer gave. The folder here is empty on purpose:
    /// that is the case a junction can be laid over without anything failing
    /// on the way.
    /// </summary>
    [WindowsFact]
    public async Task Moving_a_junction_onto_a_taken_folder_is_refused_with_a_sentence()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");

        var link = tree.Junction("from/link", outside);
        var taken = tree.Dir("dst", "link");

        var handle = await Finished(
            new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

        // Both facts in one assertion, so a failure reports each of them rather
        // than stopping at whichever happens to be checked first.
        var kept = Directory.Exists(link);
        var converted = (File.GetAttributes(taken) & FileAttributes.ReparsePoint) != 0;

        Assert.True(
            kept && !converted,
            $"junction kept at the source: {kept}; folder at the name turned into a junction: {converted}");

        Assert.Empty(handle.Landed);
        Assert.Contains("folder", Assert.Single(handle.Problems).Error.Message);
    }

    /// <summary>
    /// A folder with something in it is refused the same way: the rule is about
    /// what a folder is, not about whether a junction could be laid over it.
    /// </summary>
    [WindowsFact]
    public async Task Moving_a_junction_onto_a_folder_with_something_in_it_is_refused()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        var link = tree.Junction("from/link", outside);
        tree.Write("dst/link/inside.txt", "somebody else's");

        var handle = await Finished(
            new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

        Assert.Equal("somebody else's", tree.Read("dst", "link", "inside.txt"));
        Assert.True(Directory.Exists(link), "nothing was written, so the junction must still be at the source");

        Assert.Empty(handle.Landed);
        Assert.Contains("folder", Assert.Single(handle.Problems).Error.Message);

        Assert.Equal(["link"], tree.Names("dst"));
    }

    /// <summary>
    /// **What stood at the name survives a junction that cannot be made.** The
    /// replacement is made first, under a staging name, and nothing at the name
    /// is touched until it exists — so a volume that refuses the link, as FAT
    /// and exFAT refuse every reparse point, leaves the name as it was rather
    /// than leaving neither. The refusal is stood in for here, because no volume
    /// to hand refuses a junction while letting the name be taken.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_that_cannot_be_made_leaves_what_was_at_the_name()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        var link = tree.Junction("from/link", outside);
        var taken = tree.Write("dst/link", "somebody else's");

        var ops = new WindowsFileOperations
        {
            BeforeLinking = _ => throw new IOException("this volume takes no reparse points"),
        };

        var handle = await Finished(ops.Move([link], tree.At("dst"), Overwrite));

        Assert.Equal("somebody else's", File.ReadAllText(taken));
        Assert.True(Directory.Exists(link), "the junction was not made, so it must still be at the source");

        Assert.Empty(handle.Landed);
        Assert.Single(handle.Problems);

        // And nothing half-made is left beside it.
        Assert.Equal(["link"], tree.Names("dst"));
    }

    /// <summary>
    /// **A junction at the name is replaced like a file.** Directory.Exists
    /// answers true for one, and it is still not a folder: replacing it removes
    /// that junction and never what it pointed at.
    /// </summary>
    [WindowsFact]
    public async Task Moving_a_junction_onto_a_taken_junction_replaces_it()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        var elsewhere = tree.Dir("elsewhere");
        tree.Write("elsewhere/kept.txt", "somebody else's");

        var link = tree.Junction("from/link", outside);
        tree.Junction("dst/link", elsewhere);

        var handle = await Finished(
            new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

        var landed = tree.At("dst", "link");

        Assert.Equal(outside, new DirectoryInfo(landed).LinkTarget);
        Assert.False(Directory.Exists(link), "it was moved, so the source should have gone");
        Assert.Equal([landed], handle.Landed);

        // Neither junction's target was touched, and nothing is left beside it.
        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
        Assert.Equal("somebody else's", tree.Read("elsewhere", "kept.txt"));
        Assert.Equal(["link"], tree.Names("dst"));
    }

    /// <summary>
    /// A read-only file at the name is replaced and leaves nothing behind.
    /// Windows refuses to delete a read-only file where Linux would not, and
    /// the file at the name is removed only after the junction stands in its
    /// place, from the staging name it was renamed aside to.
    /// </summary>
    [WindowsFact]
    public async Task Moving_a_junction_onto_a_read_only_file_leaves_nothing_behind()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        var link = tree.Junction("from/link", outside);

        var taken = tree.Write("dst/link", "somebody else's");
        File.SetAttributes(taken, FileAttributes.ReadOnly);

        var handle = await Finished(
            new WindowsFileOperations().Move([link], tree.At("dst"), Overwrite));

        Assert.Equal(outside, new DirectoryInfo(taken).LinkTarget);
        Assert.Equal([taken], handle.Landed);
        Assert.Equal(["link"], tree.Names("dst"));
    }

    /// <summary>
    /// The copy goes through the same arm. Answered Overwrite onto a taken
    /// file, the junction is written there, and the one it was copied from
    /// stays where it is.
    /// </summary>
    [WindowsFact]
    public async Task Copying_a_junction_onto_a_taken_file_replaces_it()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        var link = tree.Junction("from/link", outside);
        var taken = tree.Write("dst/link", "somebody else's");

        var handle = await Finished(
            new WindowsFileOperations().Copy([link], tree.At("dst"), Overwrite));

        Assert.Equal(outside, new DirectoryInfo(taken).LinkTarget);
        Assert.Equal(outside, new DirectoryInfo(link).LinkTarget);
        Assert.Equal([taken], handle.Landed);
    }

    /// <summary>
    /// The destructive one. The junction's contents were copied to the
    /// destination and then deleted from the source side — which, through a
    /// junction, is a completely different tree on disk.
    /// </summary>
    [WindowsFact]
    public async Task A_move_does_not_delete_through_a_junction()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Write("outside/nested/also-kept.txt", "elsewhere too");
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt", "mine");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Move([source], tree.At("dst"), Overwrite));

        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
        Assert.Equal("elsewhere too", tree.Read("outside", "nested", "also-kept.txt"));
    }

    /// <summary>The link itself goes, since the user did ask to move it.</summary>
    [WindowsFact]
    public async Task A_moved_junction_leaves_the_source_side_clean()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt");
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Move([source], tree.At("dst"), Overwrite));

        Assert.False(tree.Exists("tree"));
        Assert.Equal(outside, new DirectoryInfo(tree.At("dst", "tree", "link")).LinkTarget);
    }

    /// <summary>
    /// **And undoing that move, which ends in the same recursive delete.**
    /// `UndoMove.MoveDirectory` copies the folder back when it cannot rename it
    /// — across volumes, or onto a source folder still standing — and then
    /// removes what it copied from. `Directory.Delete(recursive: true)` is
    /// refused by a junction inside, so the undo threw: the folder came back to
    /// the source correctly and a gutted shell of it was left at the
    /// destination, the step was gone from the undo stack — Ctrl+Z pops before
    /// it works — and no redo was recorded.
    ///
    /// The source folder is left standing here by a file the move could not
    /// take, which is the everyday way to reach that branch: one file open in
    /// another program, then Ctrl+Z. The lock is released before the undo,
    /// since its only job is to leave the folder behind.
    /// </summary>
    [WindowsFact]
    public async Task Undoing_a_move_of_a_folder_holding_a_junction_leaves_nothing_behind()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Write("outside/nested/also-kept.txt", "elsewhere too");

        tree.Write("src/project/a.txt", "mine");
        var held = tree.Write("src/project/busy.txt", "in use");
        tree.Write("src/project/node_modules/.bin/tool.cmd", "mine too");
        tree.Junction("src/project/node_modules/shared", outside);
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await Finished(ops.Move([tree.At("src", "project")], tree.At("dst"), Overwrite));

        Assert.True(tree.Exists("src", "project"),
            "the move swept its source folder away, so the undo renames and this proves nothing");

        await ops.UndoAsync(CancellationToken.None);

        // The destination goes whole — no shell where the folder was.
        Assert.False(tree.Exists("dst", "project"));

        // What moved is back, beside the file that never left.
        Assert.Equal("mine", tree.Read("src", "project", "a.txt"));
        Assert.Equal("in use", tree.Read("src", "project", "busy.txt"));
        Assert.Equal("mine too", tree.Read("src", "project", "node_modules", ".bin", "tool.cmd"));

        // The junction is back as a junction, not as a copy of the tree.
        Assert.Equal(outside,
            new DirectoryInfo(tree.At("src", "project", "node_modules", "shared")).LinkTarget);

        // And what it points at was never the undo's to touch.
        Assert.Equal(["kept.txt", "nested"], tree.Names("outside"));
        Assert.Equal("elsewhere too", tree.Read("outside", "nested", "also-kept.txt"));

        // The step is on the redo stack rather than lost with the exception.
        Assert.True(ops.CanRedo);
    }

    /// <summary>
    /// A junction the user selected directly, rather than one found inside a
    /// folder. `Directory.Exists` answers true for it, so it has to be tested
    /// for before the folder branch or it is walked as a folder.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_selected_on_its_own_is_copied_as_a_link()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        var link = tree.Junction("link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([link], tree.At("dst"), Overwrite));

        Assert.Equal(outside, new DirectoryInfo(tree.At("dst", "link")).LinkTarget);
        Assert.Equal(["kept.txt"], tree.Names("outside"));
    }
}
