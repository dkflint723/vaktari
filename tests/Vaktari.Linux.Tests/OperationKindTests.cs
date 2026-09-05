using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Every handle this engine hands out says what it is DOING, not only where.
///
/// **A handle carried an id, a state, its paths and its problems, and nothing
/// that said what kind of work it was.** So with several operations going at
/// once there was no sentence to put on a row for any of them: the shell keeps
/// a list of running handles, the transfer bar follows the newest and reports
/// its progress line, and the others had nothing on screen at all — not even a
/// name. Two handles copying and binning the same folder were
/// indistinguishable.
///
/// The verb is known to the method that makes the handle and to nothing
/// afterwards, which is the same argument that put the paths on it.
///
/// These run anywhere — the code under test is ordinary file I/O and the trash
/// is redirected by the variable the spec itself names, the same borrow
/// <see cref="OperationPathsTests"/> makes.
/// </summary>
public sealed class OperationKindTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-opkind-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public OperationKindTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "share"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _before);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        return path;
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static Func<FileConflict, ValueTask<ConflictResolution>> Overwrite
        => _ => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion;
        return handle;
    }

    /// <summary>
    /// A copy says copy. Copy and move are the same private method underneath,
    /// so this and the test below it are the two halves of one branch — and
    /// getting them the wrong way round would put "Moving" on the row of an
    /// operation that leaves the originals where they are.
    /// </summary>
    [Fact]
    public async Task A_copy_says_it_is_a_copy()
    {
        var source = Write("src/one.txt");
        var destination = Dir("dst");

        var handle = await Settled(
            new LinuxFileOperations().Copy([source], destination, Overwrite));

        Assert.Equal(OperationKind.Copy, handle.Kind);

        // And the destination is LAST, which is the arrangement the row reads
        // to say where the bytes are going.
        Assert.Equal(destination, handle.Paths[^1]);
    }

    [Fact]
    public async Task A_move_says_it_is_a_move()
    {
        var source = Write("src/one.txt");
        var destination = Dir("dst");

        var handle = await Settled(
            new LinuxFileOperations().Move([source], destination, Overwrite));

        Assert.Equal(OperationKind.Move, handle.Kind);
        Assert.Equal(destination, handle.Paths[^1]);
    }

    /// <summary>The one that is recoverable, and the one that is not. A row
    /// that confused these two would be the worst sentence in the
    /// application.</summary>
    [Fact]
    public async Task A_trash_says_it_is_a_trash()
    {
        var doomed = Write("one.txt");

        var handle = await Settled(new LinuxFileOperations().Trash([doomed]));

        Assert.Equal(OperationKind.Trash, handle.Kind);
    }

    [Fact]
    public async Task A_delete_says_it_is_a_delete()
    {
        var doomed = Write("one.txt");

        var handle = await Settled(new LinuxFileOperations().Delete([doomed]));

        Assert.Equal(OperationKind.Delete, handle.Kind);
    }
}
