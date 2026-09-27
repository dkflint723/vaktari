using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// **A plainly spelled folder whose child's name ends in a space or a dot.**
/// From the seventh review round's second pass (HOLE 7-C), the ordinary half
/// of 7-B. The copy planned each child's target through Path.GetRelativePath,
/// which folds "x..." to "x", so the landing check was asked about a good
/// name and passed; no child's SOURCE was asked at all, and "x..." was read,
/// moved and deleted through its plain spelling — its neighbour "x". The run
/// reported Completed with x's bytes written twice, the prompt named one file
/// against the other, and a skipped "x" was moved anyway; Duplicate made
/// "x (2)" holding x's bytes; a delete removed "x" and stopped half-way.
///
/// **Refused, naming the child, before anything moves** — copy, move,
/// Duplicate, delete, retry — and the undo walk will not rename one either.
/// A folder opened through "\\?\" reads every name as it is and is not
/// refused; the bin takes such a folder whole, by renaming it, and is asked
/// for it as it is. The first theories are the round's repro, pasted as
/// given. All in a temporary folder; the recycler is a recording.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingChildTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-hole7c").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(@"\\?\" + _root, "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(@"\\?\" + _root, recursive: true);
    }

    private static string Extended(string path) => @"\\?\" + path;

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    /// <summary>Every name under a folder, by its true name, and what each
    /// file holds — read through "\\?\", so nothing is folded on the way.</summary>
    private static SortedDictionary<string, string> Tree(string folder)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(Extended(folder))) return tree;

        foreach (var entry in Directory.GetFileSystemEntries(Extended(folder), "*", SearchOption.AllDirectories))
            tree[entry[(Extended(folder).Length + 1)..]] = File.Exists(entry) ? File.ReadAllText(entry) : "<folder>";

        return tree;
    }

    [WindowsTheory]
    [InlineData("x...", false)]
    [InlineData("x...", true)]
    [InlineData("notes ", false)]
    [InlineData("notes ", true)]
    public async Task A_skipped_file_is_never_replaced_or_moved_through_its_trailing_named_neighbour(string name, bool move)
    {
        var neighbour = name.TrimEnd(' ', '.');
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(Path.Combine(dst, "album"));
        File.WriteAllText(@"\\?\" + Path.Combine(album, name), "the trailing child");
        File.WriteAllText(Path.Combine(album, neighbour), "the source's own neighbour");
        File.WriteAllText(Path.Combine(dst, "album", neighbour), "the destination's own");

        // Skip the neighbour, overwrite everything else - as a person answers two prompts.
        ValueTask<ConflictResolution> Answer(FileConflict c)
            => ValueTask.FromResult(Path.GetFileName(c.Source) == neighbour ? ConflictResolution.Skip : ConflictResolution.Overwrite);

        var ops = new WindowsFileOperations();
        var handle = move ? ops.Move([album], dst, Answer) : ops.Copy([album], dst, Answer);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // RED: the destination's own file, which the person chose to keep, now holds the source's.
        Assert.Equal("the destination's own", File.ReadAllText(Path.Combine(dst, "album", neighbour)));

        // RED on a move: the file the person skipped has left the source.
        Assert.True(File.Exists(Path.Combine(album, neighbour)), "the skipped file was moved anyway");
    }

    [WindowsTheory]
    [InlineData("x...")]
    [InlineData("notes ")]
    public async Task A_trailing_named_child_is_copied_as_itself_or_refused(string name)
    {
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(dst);
        File.WriteAllText(@"\\?\" + Path.Combine(album, name), "the trailing child");
        File.WriteAllText(Path.Combine(album, name.TrimEnd(' ', '.')), "the neighbour");

        var handle = new WindowsFileOperations().Copy([album], @"\\?\" + dst, _ => ValueTask.FromResult(ConflictResolution.KeepBoth));
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // (The one change to the pasted repro: a refusal creates no "album"
        // at all, which the repro's listing did not allow for.)
        var landedIn = @"\\?\" + Path.Combine(dst, "album");
        var landed = (Directory.Exists(landedIn) ? Directory.GetFiles(landedIn) : []).Select(File.ReadAllText).Order().ToList();

        // RED: Completed, with the neighbour's bytes twice and the trailing child nowhere.
        Assert.True(handle.State == OperationState.Failed && landed.Count == 0
                    || landed.SequenceEqual(["the neighbour", "the trailing child"]),
                    $"{handle.State}: {string.Join(" | ", landed)}");
    }

    /// <summary>A folder "album" holding "x..." beside "x", and a folder
    /// "notes " beside "notes", each with a file — and where the trailing
    /// name sits, for the sentence.</summary>
    private (string Album, string Trailing) Album(string shape)
    {
        var album = Path.Combine(_root, "src", "album");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "x"), "x's own");
        File.WriteAllText(Path.Combine(album, "a.txt"), "a");

        string trailing;

        switch (shape)
        {
            case "file":
                trailing = Path.Combine(album, "x...");
                File.WriteAllText(Extended(trailing), "the trailing file");
                break;
            case "folder":
                Directory.CreateDirectory(Path.Combine(album, "notes"));
                File.WriteAllText(Path.Combine(album, "notes", "n.txt"), "the neighbour folder's");
                trailing = Path.Combine(album, "notes ");
                Directory.CreateDirectory(Extended(trailing));
                File.WriteAllText(Extended(Path.Combine(trailing, "t.txt")), "the trailing folder's");
                break;
            default:
                Directory.CreateDirectory(Path.Combine(album, "deep", "er"));
                trailing = Path.Combine(album, "deep", "er", "y.");
                File.WriteAllText(Path.Combine(album, "deep", "er", "y"), "y's own");
                File.WriteAllText(Extended(trailing), "the deep trailing file");
                break;
        }

        return (album, trailing);
    }

    /// <summary>
    /// **Copy, move and Duplicate are refused whole, in the child's own
    /// words, before anything is asked or moved**: the source tree and the
    /// destination are exactly as they were, and on a move nothing left.
    /// </summary>
    [WindowsTheory]
    [InlineData("file", "copy")]
    [InlineData("file", "move")]
    [InlineData("file", "duplicate")]
    [InlineData("folder", "copy")]
    [InlineData("folder", "move")]
    [InlineData("folder", "duplicate")]
    [InlineData("deep", "copy")]
    [InlineData("deep", "move")]
    public async Task Copy_move_and_duplicate_refuse_a_folder_holding_a_name_its_spelling_cannot_reach(string shape, string verb)
    {
        var (album, trailing) = Album(shape);
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "the destination's own");

        var before = Tree(Path.Combine(_root, "src"));
        var asked = new List<FileConflict>();

        ValueTask<ConflictResolution> Overwrite(FileConflict conflict)
        {
            asked.Add(conflict);
            return ValueTask.FromResult(ConflictResolution.Overwrite);
        }

        var ops = new WindowsFileOperations();
        var handle = await Settled(verb switch
        {
            "copy" => ops.Copy([album], dst, Overwrite),
            "move" => ops.Move([album], dst, Overwrite),
            _ => ops.Copy([album], Path.Combine(_root, "src"), Overwrite),
        });

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(ReachablePath.Refuse(trailing), handle.Error?.Message);
        Assert.Contains($"\"{PathRules.LeafName(trailing)}\"", handle.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(asked);
        Assert.Equal(before, Tree(Path.Combine(_root, "src")));
        Assert.Equal(["keep.txt"], Tree(dst).Keys);
    }

    /// <summary>
    /// **Through "\\?\", the same folder copies and moves exactly** — every
    /// name as it is, into a destination that can hold it, and the undo of
    /// the move puts each back as itself.
    /// </summary>
    [WindowsTheory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("deep")]
    public async Task Through_the_extended_prefix_the_folder_copies_moves_and_comes_back_exactly(string shape)
    {
        var (album, _) = Album(shape);
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(dst);

        var expected = Tree(album);
        var ops = new WindowsFileOperations();

        var copy = await Settled(ops.Copy([Extended(album)], Extended(dst), _ => ValueTask.FromResult(ConflictResolution.Skip)));

        Assert.Equal(OperationState.Completed, copy.State);
        Assert.Equal(expected, Tree(Path.Combine(dst, "album")));

        Directory.Delete(Extended(Path.Combine(dst, "album")), recursive: true);

        var move = await Settled(ops.Move([Extended(album)], Extended(dst), _ => ValueTask.FromResult(ConflictResolution.Skip)));

        Assert.Equal(OperationState.Completed, move.State);
        Assert.Equal(expected, Tree(Path.Combine(dst, "album")));
        Assert.False(Directory.Exists(Extended(album)));

        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(expected, Tree(album));
        Assert.False(Directory.Exists(Extended(Path.Combine(dst, "album"))));
    }

    /// <summary>
    /// **Delete refuses the folder whole, naming the child**, where it removed
    /// "x" through "x..." and then stopped half-way: every name is where it
    /// was. Through "\\?\" the same folder goes entirely.
    /// </summary>
    [WindowsTheory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("deep")]
    public async Task Delete_refuses_a_folder_holding_a_name_its_spelling_cannot_reach(string shape)
    {
        var (album, trailing) = Album(shape);
        var before = Tree(album);
        var ops = new WindowsFileOperations();

        var refused = await Settled(ops.Delete([album]));

        var problem = Assert.Single(refused.Problems);
        Assert.Equal(ReachablePath.Refuse(trailing), problem.Error.Message);
        Assert.Equal(before, Tree(album));

        var deleted = await Settled(ops.Delete([Extended(album)]));

        Assert.Empty(deleted.Problems);
        Assert.False(Directory.Exists(Extended(album)), "the folder was not deleted through \\\\?\\");
    }

    /// <summary>
    /// **A refused delete clears no marks either.** The delete clears the
    /// read-only mark of everything it is about to remove; a folder it then
    /// refuses must keep every one. A read-only file beside "x..." is still
    /// read-only after the refusal.
    /// </summary>
    [WindowsFact]
    public async Task A_refused_delete_leaves_every_read_only_mark()
    {
        var (album, _) = Album("file");
        var locked = Path.Combine(album, "a.txt");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        try
        {
            var refused = await Settled(new WindowsFileOperations().Delete([album]));

            Assert.Single(refused.Problems);
            Assert.True((File.GetAttributes(locked) & FileAttributes.ReadOnly) != 0, "the refused delete cleared a read-only mark");
        }
        finally
        {
            File.SetAttributes(locked, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// **The tree walk every recursive delete shares refuses too** — the
    /// Recycle Bin's purge is its other caller — before it removes anything.
    /// </summary>
    [WindowsTheory]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("deep")]
    public void The_shared_tree_delete_refuses_before_it_removes_anything(string shape)
    {
        var (album, trailing) = Album(shape);
        var before = Tree(album);

        var thrown = Assert.Throws<IOException>(() => WindowsFileOperations.DeleteTree(album));

        Assert.Equal(ReachablePath.Refuse(trailing), thrown.Message);
        Assert.Equal(before, Tree(album));
    }

    /// <summary>
    /// **A child's place is the text the listing gave, below its root** — not
    /// Path.GetRelativePath's answer, which folds "x..." to "x".
    /// </summary>
    [WindowsFact]
    public void A_child_is_planned_under_the_name_it_was_listed_by()
    {
        Assert.Equal("x...", WindowsFileOperations.Beneath(@"C:\src\album", @"C:\src\album\x..."));
        Assert.Equal(@"notes \n.txt", WindowsFileOperations.Beneath(@"C:\src\album", @"C:\src\album\notes \n.txt"));
        Assert.Equal("report ", WindowsFileOperations.Beneath(@"\\?\C:\src\album\", @"\\?\C:\src\album\report "));
        Assert.Throws<IOException>(() => WindowsFileOperations.Beneath(@"C:\src\album", @"C:\elsewhere\x"));
    }

    /// <summary>
    /// **What may be handed to another program**, by text: a name Win32 would
    /// fold is refused however it is spelled here, "\\?\" and "\??\" and the
    /// share forms included, because the receiver may take the prefix off.
    /// Ordinary names, in any spelling, are not.
    /// </summary>
    [WindowsFact]
    public void A_name_win32_would_fold_is_never_handed_on_however_it_is_spelled()
    {
        foreach (var path in new[]
                 {
                     @"C:\x\report ", @"C:\x\report.", @"\\?\C:\x\report ", @"\??\C:\x\album \a.txt",
                     @"\\?\UNC\server\share\report.", @"\\server\share\x...", @"\\.\C:\x\report ",
                 })
            Assert.True(ReachablePath.RefuseHandedOut(path) is not null, $"{path} would be handed on");

        foreach (var path in new[] { @"C:\x\report", @"\\?\C:\x\v1.2.txt", @"\\?\UNC\server\share\a b", @"C:\x\...\..\y" })
            Assert.Null(ReachablePath.RefuseHandedOut(path));

        Assert.Contains("\"report\"", ReachablePath.RefuseHandedOut(@"\\?\C:\x\report ")!, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The bin takes such a folder whole**, by renaming it into the bin, so
    /// it is not refused: the recycler is asked for the folder exactly as
    /// named, and for nothing inside it.
    /// </summary>
    [WindowsFact]
    public async Task The_bin_is_asked_for_the_folder_as_it_is()
    {
        var (album, _) = Album("file");
        var asked = new List<string>();
        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        var trash = await Settled(ops.Trash([album]));

        Assert.Null(trash.Error);
        Assert.Equal([album], asked);
    }

    /// <summary>
    /// **A retry re-plans the folder from what is in it now, and asks every
    /// name it will read.** The first run fails on the folder (a file stands
    /// at its name, answered Overwrite) and is offered back; before the retry
    /// the plainly spelled source gains "x..." beside its "x", and the retry
    /// is refused naming it, landing nothing and leaving the source whole.
    /// (RetryLandingTests asks the same of the landing, from a "\\?\" source.)
    /// </summary>
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retry_asks_every_name_it_will_read(bool move)
    {
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(album, "x"), "the source's own x");
        File.WriteAllText(Path.Combine(dst, "album"), "a file standing where the folder goes");

        var ops = new WindowsFileOperations();
        var first = await Settled(move
            ? ops.Move([album], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite))
            : ops.Copy([album], dst, _ => ValueTask.FromResult(ConflictResolution.Overwrite)));

        Assert.True(first.Retry is not null, $"the first run offered no retry: {first.State} {first.Error?.Message}");

        File.Delete(Path.Combine(dst, "album"));
        var late = Path.Combine(album, "x...");
        File.WriteAllText(Extended(late), "arrived before the retry");

        var again = await Settled(first.Retry!.Again());

        Assert.Equal(OperationState.Failed, again.State);
        Assert.Equal(ReachablePath.Refuse(late), again.Error?.Message);
        Assert.False(Directory.Exists(Extended(Path.Combine(dst, "album"))), "the retry landed something");
        Assert.Equal("the source's own x", File.ReadAllText(Path.Combine(album, "x")));
        Assert.Equal("arrived before the retry", File.ReadAllText(Extended(late)));
    }

    /// <summary>
    /// **The undo walk never renames through a folded name.** A folder moved
    /// back cannot always go back whole — here its old name is taken again —
    /// and the walk then moves its children one by one. A child "x..." that
    /// arrived after the move would be renamed through "x", its neighbour:
    /// instead it waits, and the undo says so, while "x" goes back as itself.
    /// </summary>
    [WindowsFact]
    public async Task The_undo_walk_leaves_a_name_it_cannot_reach_and_says_so()
    {
        var album = Path.Combine(_root, "src", "album");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(album, "x"), "x's own");

        var ops = new WindowsFileOperations();
        var move = await Settled(ops.Move([album], dst, _ => ValueTask.FromResult(ConflictResolution.Skip)));
        Assert.Equal(OperationState.Completed, move.State);

        var moved = Path.Combine(dst, "album");
        File.WriteAllText(Extended(Path.Combine(moved, "x...")), "arrived after the move");
        Directory.CreateDirectory(album);

        var undo = await Record.ExceptionAsync(async () => await ops.UndoAsync(CancellationToken.None));

        Assert.NotNull(undo);
        Assert.Contains("x...", undo.Message, StringComparison.Ordinal);
        Assert.Equal("x's own", File.ReadAllText(Path.Combine(album, "x")));
        Assert.Equal("arrived after the move", File.ReadAllText(Extended(Path.Combine(moved, "x..."))));
        Assert.False(File.Exists(Extended(Path.Combine(album, "x..."))));
    }
}
