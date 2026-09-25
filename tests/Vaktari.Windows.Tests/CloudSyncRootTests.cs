using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The sync-root fixture's own promise: whatever happens to the test that made
/// a root, the machine is not left holding it.
///
/// **Two ways it was.** A constructor that failed after registering threw
/// before Create had an object to dispose, so the folder went and — for a
/// root already connected — the registration stayed. And a test host killed
/// mid-test never disposed at all, leaving both the registration and the
/// folder under the system temp. Each is made to happen here, on purpose, and
/// the root is then looked for.
///
/// These register roots of their own, so like OnlineOnlyFilesTests they fail
/// where the system refuses a registration — which is the fixture's own
/// premise failing, and worth hearing about.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CloudSyncRootTests
{
    /// <summary>
    /// **A root whose constructor fails after registering is unregistered and
    /// its folder deleted**, and the failure still reaches the caller. The
    /// failure is made through the fixture's own seam, last in the
    /// constructor, with the root registered AND connected — the most a
    /// failure there can leave behind — since a real connect to a root nobody
    /// else has connected to does not fail.
    ///
    /// The folder is made again afterwards and asked about: a deleted folder
    /// answers "not a sync root" whatever the filter holds. Measured with the
    /// cleanup removed: made again, the folder read as a sync root of this
    /// fixture's. Failing BEFORE the connect instead reddened nothing — the
    /// registration of a root nobody has connected to went with its folder —
    /// which is why the seam sits after it.
    /// </summary>
    [WindowsFact]
    public void A_root_that_fails_after_registering_is_unregistered_and_deleted()
    {
        string? made = null;
        var wasRegistered = false;

        CloudSyncRoot.AfterConnecting = root =>
        {
            made = root;
            wasRegistered = CloudSyncRoot.IsSyncRoot(root);
            throw new InvalidOperationException("the constructor failed");
        };

        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => CloudSyncRoot.Create());
            Assert.Equal("the constructor failed", thrown.Message);
        }
        finally
        {
            CloudSyncRoot.AfterConnecting = null;
        }

        Assert.NotNull(made);
        Assert.True(wasRegistered, "the root was not registered when the seam ran, so this proves nothing");

        Assert.False(Directory.Exists(made), "the failed root's folder was left behind");
        Assert.False(CloudSyncRoot.IsSyncRoot(made), "the failed root is still registered");

        Directory.CreateDirectory(made);

        try
        {
            Assert.True(CloudSyncRoot.RegisteredProvider(made) is null,
                $"'{made}' reads as a sync root again once its folder is made again");
        }
        finally
        {
            Directory.Delete(made);
        }
    }

    /// <summary>
    /// **A root left by a test host that died is swept by the next Create** —
    /// and nothing else is: not a root this host is still using, and not a
    /// folder that has the fixture's name but is no sync root. The dead host
    /// is a process id that is not running, which is all a killed host leaves
    /// to go on.
    /// </summary>
    [WindowsFact]
    public void A_root_left_by_a_killed_host_is_swept_by_the_next_one()
    {
        var dead = NotRunning();

        using var live = CloudSyncRoot.Create();
        var orphan = CloudSyncRoot.Create(dead);
        var plain = Path.Combine(Path.GetTempPath(), $"{CloudSyncRoot.FolderPrefix}{dead}-not-a-root");

        try
        {
            orphan.Placeholder("left.txt", "behind"u8.ToArray());

            // Asked before it is abandoned: from then on a sweep by another
            // class making a root in parallel may take it first, which is the
            // behaviour under test arriving early rather than a failure.
            Assert.Equal(CloudSyncRoot.ProviderName, CloudSyncRoot.RegisteredProvider(orphan.Root));

            orphan.Abandon();
            Directory.CreateDirectory(plain);

            using (CloudSyncRoot.Create())
            {
                Assert.False(Directory.Exists(orphan.Root), "the dead host's root folder was left behind");
                Assert.Null(CloudSyncRoot.RegisteredProvider(orphan.Root));

                Assert.True(CloudSyncRoot.IsSyncRoot(live.Root), "a root this host is using was swept");
                Assert.True(Directory.Exists(plain), "a folder that is no sync root was deleted");
            }
        }
        finally
        {
            orphan.Dispose();

            if (Directory.Exists(plain)) Directory.Delete(plain);
        }
    }

    /// <summary>A process id nothing is running under, highest first.</summary>
    private static int NotRunning()
    {
        for (var pid = 0x3FFF_FFF0; ; pid -= 4)
        {
            try
            {
                using var _ = System.Diagnostics.Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }
}
