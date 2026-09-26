using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The Linux engine never deletes, bins or moves a volume's root — the twin of
/// the Windows tests of the same name.
///
/// **A mount point that is not there**, declared one through the seam: the
/// refusal's absence must never be tried on a real volume, so the path is one
/// no filesystem holds, where the unguarded engine fails harmlessly with a
/// different error and the assertion on the refusal's own sentence tells the
/// two apart.
/// </summary>
public sealed class VolumeRootRefusalTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>>? _before = VolumeRoots.MountPointsOverride;

    private readonly string _mount =
        Path.Combine(Path.GetTempPath(), "vaktari-no-such-mount-" + Guid.NewGuid().ToString("N"));

    public VolumeRootRefusalTests() => VolumeRoots.MountPointsOverride = () => ["/", _mount];

    public void Dispose() => VolumeRoots.MountPointsOverride = _before;

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    [PosixFact]
    public async Task Delete_refuses_a_mount_point()
    {
        var handle = await Settled(new LinuxFileOperations().Delete([_mount]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [PosixFact]
    public async Task Trash_refuses_a_mount_point()
    {
        var handle = await Settled(new LinuxFileOperations().Trash([_mount]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [PosixFact]
    public async Task Move_refuses_a_mount_point()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new LinuxFileOperations().Move(
                [_mount], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }
}
