using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Undoing a copy leaves the history as it was before the copy.
///
/// **It left a delete on top.** The undo took the copies back through the same
/// Trash the Delete key uses, which records an undo of its own — so after a
/// rename and a copy, one Ctrl+Z put "Undo delete of notes.txt" at the top of
/// the history where the rename should have been, and the next Ctrl+Z brought
/// the copy back instead of undoing the rename.
///
/// Against a trash of this test's own making, through <c>XDG_DATA_HOME</c>.
/// </summary>
public sealed class UndoTrashStepTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-undostep-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _dataHomeBefore = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public UndoTrashStepTests()
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

    [PosixFact]
    public async Task Undoing_a_copy_leaves_the_step_before_it_on_top()
    {
        var renamed = Write("a.txt");
        var file = Write("src/notes.txt");
        var into = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;

        var ops = new LinuxFileOperations();

        await ops.RenameAsync(renamed, "b.txt", CancellationToken.None);
        var renameStep = ops.UndoDescription;

        await ops.Copy([file], into, _ => ValueTask.FromResult(ConflictResolution.Skip)).Completion;
        await ops.UndoAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(into, "notes.txt")), "the copy was not taken back");
        Assert.Equal(renameStep, ops.UndoDescription);
    }
}
