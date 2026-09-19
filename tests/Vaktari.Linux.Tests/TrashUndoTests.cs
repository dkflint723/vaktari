using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Ctrl+Z after a delete, when one of the deleted things cannot come back.
///
/// **The restore ran in a loop with no catch.** Three files deleted, the
/// middle one purged from the bin since — by a trash browser, or by this
/// application's own "delete this one for good" — and Ctrl+Z put back the
/// first, threw on the second and never reached the third: a file still in
/// the bin, the undo row already gone, and a message naming a trash key the
/// person had never seen. The Windows twin swallowed each refusal instead and
/// reported the undo done.
///
/// Now every item that can come back does, whatever the others do, and what
/// could not is said by the name the person knew it by, in the sentence the
/// undo of a move uses. Nothing goes back on the undo stack for it: the item
/// is in the bin, which is where it is put back from, and an entry that can
/// only fail again would sit on top of the history and wedge Ctrl+Z against
/// itself.
///
/// Against a trash of this test's own making, through <c>XDG_DATA_HOME</c>,
/// like the other trash tests here.
/// </summary>
public sealed class TrashUndoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-trashundo-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _dataHomeBefore = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public TrashUndoTests()
    {
        Directory.CreateDirectory(_root);

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHomeBefore);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Deletes, and makes sure it went, so the undo has work to do.</summary>
    private static async Task Deleted(LinuxFileOperations ops, params string[] paths)
    {
        var handle = ops.Trash(paths);

        await handle.Completion;

        Assert.Equal(OperationState.Completed, handle.State);

        foreach (var path in paths)
            Assert.False(File.Exists(path), $"{path} was never trashed, so this measures nothing");
    }

    /// <summary>
    /// Purges one deleted file from the bin, as a trash browser would — found
    /// by where it came from, because the bin names it as it pleases.
    /// </summary>
    private static void Purged(string original)
    {
        var bin = new XdgTrashMaintenance();

        var item = Assert.Single(bin.List(), i => i.OriginalPath == original);

        bin.Delete(item.TrashName);

        Assert.DoesNotContain(bin.List(), i => i.TrashName == item.TrashName);
    }

    private static Task<Exception?> Undo(LinuxFileOperations ops)
        => Record.ExceptionAsync(() => ops.UndoAsync(CancellationToken.None).AsTask());

    /// <summary>
    /// The whole finding: the third file comes back although the second cannot.
    /// </summary>
    [Fact]
    public async Task An_item_purged_from_the_bin_does_not_stop_the_rest_coming_back()
    {
        var a = Write("a.txt");
        var b = Write("b.txt");
        var c = Write("c.txt");
        var ops = new LinuxFileOperations();

        await Deleted(ops, a, b, c);
        Purged(b);

        var said = await Undo(ops);

        Assert.True(File.Exists(a), "a.txt did not come back");
        Assert.True(File.Exists(c), "c.txt did not come back: the undo stopped at the one before it");
        Assert.False(File.Exists(b), "b.txt was purged, and came back from nowhere");

        Assert.IsAssignableFrom<IOException>(said);

        // The one still missing is in the bin, not on the stack — see the header.
        Assert.False(ops.CanUndo, "an entry that can only fail again went back on the stack");
    }

    /// <summary>
    /// **By the name the person knew, not the bin's.** Two files of one name:
    /// the bin has to call the second something else, and that something is
    /// what the old message named.
    /// </summary>
    [Fact]
    public async Task What_did_not_come_back_is_named_as_the_person_knew_it()
    {
        var first = Write("x/notes.txt");
        var second = Write("y/notes.txt");
        var ops = new LinuxFileOperations();

        await Deleted(ops, first, second);
        Purged(second);

        var said = await Undo(ops);

        Assert.True(File.Exists(first), "the first notes.txt did not come back");

        Assert.Equal(
            "notes.txt could not go back: not in the bin any more",
            Assert.IsAssignableFrom<IOException>(said).Message);
    }

    /// <summary>The sentence the undo of a move uses: three names, then a count.</summary>
    [Fact]
    public async Task Past_three_the_rest_are_counted()
    {
        var files = new[] { "a", "b", "c", "d", "e" }.Select(n => Write(n + ".txt")).ToArray();
        var ops = new LinuxFileOperations();

        await Deleted(ops, files);

        foreach (var file in files) Purged(file);

        var said = await Undo(ops);

        Assert.Equal(
            "a.txt, b.txt, c.txt and 2 more could not go back: not in the bin any more",
            Assert.IsAssignableFrom<IOException>(said).Message);
    }
}
