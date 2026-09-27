using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The kernel's question from the other side — pinned by the third review
/// round: a mount reached through a link that leads to "/" itself, and where
/// the question is asked.
/// </summary>
public sealed class MountRootOnDiskTests
{
    private static async Task<IOperationHandle> Settled(IOperationHandle handle)
    {
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return handle;
    }

    /// <summary>
    /// **A link to "/" in the middle of a path reaches the mounts under it.**
    /// "/proc/self/root/proc" is /proc, and so is "toroot/proc" where toroot
    /// leads to "/", or "up/proc" where up is "../.." from /tmp. The kernel
    /// resolves them itself, so its answer is a mount for each, with the
    /// attribute and by st_dev alike; the link and a folder under the mount
    /// are not. Asked, never acted on.
    /// </summary>
    [PosixFact]
    public void Through_a_link_to_the_root_the_kernel_still_sees_the_mount()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-toroot").FullName;
        var toRoot = Path.Combine(holder, "toroot");
        File.CreateSymbolicLink(toRoot, "/");

        var up = Path.Combine(holder, "up");
        File.CreateSymbolicLink(up, string.Join('/', holder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(_ => "..")));

        try
        {
            foreach (var attribute in new[] { true, false })
            {
                foreach (var mount in new[] { "/proc/self/root/proc", toRoot + "/proc", toRoot + "/proc/", toRoot + "/dev/shm", up + "/proc" })
                    Assert.True(MountRootOnDisk.Is(mount, attribute), $"{mount} was not a mount (attribute {attribute})");

                foreach (var folder in new[] { toRoot, up, "/proc/self/root", toRoot + "/proc/self", toRoot + "/etc" })
                    Assert.False(MountRootOnDisk.Is(folder, attribute), $"{folder} was a mount (attribute {attribute})");
            }

            Assert.Equal(VolumeRoots.Refusal, VolumeRoots.RefuseOnDisk([holder, toRoot + "/proc"], MountRootOnDisk.Is));
        }
        finally
        {
            File.Delete(toRoot);
            File.Delete(up);
            Directory.Delete(holder, recursive: true);
        }
    }

    /// <summary>
    /// **Asked in the worker, not on the thread that pressed the key.** The
    /// seam waits for the Delete call to have returned before it answers;
    /// asked on the caller's thread it would be answering before the call
    /// returned.
    /// </summary>
    [PosixFact]
    public async Task The_kernel_is_asked_after_the_call_has_returned()
    {
        var holder = Directory.CreateTempSubdirectory("vaktari-offthread").FullName;
        var file = Path.Combine(holder, "a");
        File.WriteAllText(file, "a");

        using var returned = new ManualResetEventSlim();
        var askedAfterReturn = new List<bool>();

        var ops = new LinuxFileOperations
        {
            RootOnDisk = path =>
            {
                var after = returned.Wait(TimeSpan.FromSeconds(5));
                lock (askedAfterReturn) askedAfterReturn.Add(after);
                return MountRootOnDisk.Is(path);
            },
        };

        try
        {
            var handle = ops.Delete([file]);
            returned.Set();
            await Settled(handle);

            Assert.Equal(OperationState.Completed, handle.State);
            Assert.NotEmpty(askedAfterReturn);
            Assert.All(askedAfterReturn, Assert.True);
        }
        finally
        {
            Directory.Delete(holder, recursive: true);
        }
    }
}
