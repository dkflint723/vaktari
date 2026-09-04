using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Every handle this engine hands out says where it is working.
///
/// **Nothing could ask a running operation what drive it was on**, so the eject
/// command could not tell a paste onto a stick from a paste on the other side
/// of the machine, and it went ahead with both. The engine is the only thing
/// that knows: the sources and the destination are arguments to the call that
/// makes the handle, and they are gone the moment it returns.
///
/// These run anywhere — the code under test is ordinary file I/O and the trash
/// is redirected by the variable the spec itself names, the same borrow
/// <see cref="TrashDeleteOneTests"/> makes. Nothing here goes near the trash of
/// whoever is running it.
/// </summary>
public sealed class OperationPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-oppaths-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string? _before = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public OperationPathsTests()
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
    /// Both ends. A copy ONTO a drive claims it through the destination and a
    /// copy OFF one claims it through the sources, so a handle carrying only
    /// half of that answers only half the ejects.
    /// </summary>
    [Fact]
    public async Task A_copy_reports_its_sources_and_its_destination()
    {
        var source = Write("src/one.txt");
        var destination = Dir("dst");

        var handle = await Settled(
            new LinuxFileOperations().Copy([source], destination, Overwrite));

        Assert.Contains(source, handle.Paths);
        Assert.Contains(destination, handle.Paths);
    }

    /// <summary>A move is the same call underneath, and the same claim: the
    /// bytes still to be read are on the source drive.</summary>
    [Fact]
    public async Task A_move_reports_its_sources_and_its_destination()
    {
        var source = Write("src/one.txt");
        var destination = Dir("dst");

        var handle = await Settled(
            new LinuxFileOperations().Move([source], destination, Overwrite));

        Assert.Contains(source, handle.Paths);
        Assert.Contains(destination, handle.Paths);
    }

    /// <summary>A trash writes into a trash directory on the volume it deletes
    /// from, so one in flight holds the drive like any transfer.</summary>
    [Fact]
    public async Task A_trash_reports_what_it_is_binning()
    {
        var doomed = Write("one.txt");

        var handle = await Settled(new LinuxFileOperations().Trash([doomed]));

        Assert.Contains(doomed, handle.Paths);
    }

    [Fact]
    public async Task A_delete_reports_what_it_is_destroying()
    {
        var doomed = Write("one.txt");

        var handle = await Settled(new LinuxFileOperations().Delete([doomed]));

        Assert.Contains(doomed, handle.Paths);
    }
}
