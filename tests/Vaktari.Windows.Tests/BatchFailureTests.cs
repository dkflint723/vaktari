using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What a batch does when one item in it refuses.
///
/// **It abandoned the rest and named nothing.** A single try wrapped the whole
/// delete loop, so one file open in another program stopped every remaining
/// item in the selection — and the message carried the exception rather than
/// the path, so nobody could tell which file stopped it or what had already
/// gone. The copy engine had been doing this per item for a long time; delete
/// and trash never were.
///
/// And a copy asked for no room before it started, so a fifty-gigabyte tree
/// onto a drive with thirty filled the disk and failed somewhere in the middle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BatchFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-batch-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<FileStream> _held = [];

    public BatchFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var stream in _held) stream.Dispose();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string File_(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Holds a file open the way another program would.</summary>
    private string Locked(string name)
    {
        var path = File_(name);

        _held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));

        return path;
    }

    [Fact]
    public async Task One_locked_file_does_not_abandon_the_rest()
    {
        var first = File_("first.txt");
        var stuck = Locked("stuck.txt");
        var last = File_("last.txt");

        var ops = new WindowsFileOperations();
        var handle = ops.Delete([first, stuck, last]);

        await handle.Completion;

        Assert.False(System.IO.File.Exists(first), "the file before the locked one did not go");
        Assert.False(System.IO.File.Exists(last), "THE FILE AFTER THE LOCKED ONE WAS ABANDONED");
        Assert.True(System.IO.File.Exists(stuck), "the locked file went anyway");
    }

    /// <summary>And it says WHICH one, which is the difference between a
    /// message and a message worth reading.</summary>
    [Fact]
    public async Task The_one_that_refused_is_named()
    {
        var stuck = Locked("stuck.txt");

        var ops = new WindowsFileOperations();
        var handle = ops.Delete([File_("fine.txt"), stuck]);

        await handle.Completion;

        var problem = Assert.Single(handle.Problems);

        Assert.Equal(stuck, problem.Path);
    }

    // ---- room to land in ----------------------------------------------------

    /// <summary>
    /// The check is a comparison against what the plan will write, so a copy
    /// that fits proceeds untouched. Anything else here would need a full disk
    /// to test.
    /// </summary>
    [Fact]
    public async Task A_copy_that_fits_is_not_refused()
    {
        var note = File_("note.txt", "small");
        var into = Path.Combine(_root, "into");

        Directory.CreateDirectory(into);

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([note], into, _ => ValueTask.FromResult(ConflictResolution.KeepBoth));

        await handle.Completion;

        Assert.NotEqual(OperationState.Failed, handle.State);
        Assert.True(System.IO.File.Exists(Path.Combine(into, "note.txt")));
    }

    /// <summary>
    /// A move inside one volume is a rename and needs no room at all, so the
    /// check must not stand in its way — that is the case a naive size
    /// comparison would refuse on a nearly-full disk.
    /// </summary>
    [Fact]
    public async Task A_move_within_one_volume_is_not_asked_for_room()
    {
        var note = File_("note.txt");
        var into = Path.Combine(_root, "into");

        Directory.CreateDirectory(into);

        var ops = new WindowsFileOperations();
        var handle = ops.Move([note], into, _ => ValueTask.FromResult(ConflictResolution.KeepBoth));

        await handle.Completion;

        Assert.NotEqual(OperationState.Failed, handle.State);
        Assert.True(System.IO.File.Exists(Path.Combine(into, "note.txt")));
    }
}
