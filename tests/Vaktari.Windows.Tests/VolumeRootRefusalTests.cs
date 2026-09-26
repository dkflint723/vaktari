using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The engine never deletes, bins or moves a drive's root.
///
/// **Shift+Delete on a drive in This PC deleted what was on it** (0.11.0).
/// Delete cleared the read-only bits of the whole tree and walked it deleting
/// files before Directory.Delete refused the root at the very end. The pane
/// refuses the verbs on a volume now as well; this is the engine's own refusal
/// under it, for a route nobody has thought of yet.
///
/// **Asked of a drive letter that does not exist.** The refusal's absence must
/// never be tried on a real volume — a revert-check of this guard would then
/// be a real deletion — so the root is a letter no drive answers to, where the
/// unguarded engine fails harmlessly with a different error, and the assertion
/// on the refusal's own sentence is what tells the two failures apart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeRootRefusalTests
{
    /// <summary>A drive root no volume on this machine answers to.</summary>
    private static string UnusedRoot()
    {
        var taken = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        var letter = "ZYXWVUTSRQPONMLKJIHG".First(c => !taken.Contains(c));

        return $"{letter}:\\";
    }

    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    [Fact]
    public async Task Delete_refuses_a_drive_root()
    {
        var handle = await Settled(new WindowsFileOperations().Delete([UnusedRoot()]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
    }

    [Fact]
    public async Task Trash_refuses_a_drive_root()
    {
        // The recycle itself stands in, so an unguarded engine records the
        // attempt rather than asking the shell to recycle anything.
        var asked = new List<string>();

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                asked.AddRange(paths);
                return new RecycleResult(0, false);
            },
        };

        var handle = await Settled(ops.Trash([UnusedRoot()]));

        Assert.Equal(OperationState.Failed, handle.State);
        Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Move_refuses_a_drive_root()
    {
        var into = Directory.CreateTempSubdirectory("vaktari-volroot").FullName;

        try
        {
            var handle = await Settled(new WindowsFileOperations().Move(
                [UnusedRoot()], into, _ => ValueTask.FromResult(ConflictResolution.Skip)));

            Assert.Equal(OperationState.Failed, handle.State);
            Assert.Equal(VolumeRoots.Refusal, handle.Error?.Message);
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }

    /// <summary>And a folder is not a root: the refusal does not reach past
    /// the thing it is for.</summary>
    [Fact]
    public void A_folder_is_not_a_volume_root()
    {
        Assert.False(VolumeRoots.IsVolumeRoot(Path.Combine(Path.GetTempPath(), "anything")));
        Assert.True(VolumeRoots.IsVolumeRoot(UnusedRoot()));
    }
}
