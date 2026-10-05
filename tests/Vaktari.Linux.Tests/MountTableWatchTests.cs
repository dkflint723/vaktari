using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The kernel's own word that the mount table changed, in place of a look at
/// /proc/mounts every second.
///
/// **What a test can prove and what it cannot.** Mounting needs privileges no
/// test takes; the wake itself was measured in a throwaway user namespace
/// (unshare -rm, a tmpfs) and is recorded on MountTableWatch. What is proved
/// here: on Linux the watch starts on the real table and stops at once
/// (Dispose writes down its own pipe rather than waiting for a timeout), and
/// on a host with no libc — the Windows runner, which runs these tests too —
/// it gives up in the POLL branch, before opening anything (review M6), so
/// the provider falls back to the one-second floor instead of throwing.
/// </summary>
public sealed class MountTableWatchTests
{
    [PosixFact]
    public void On_linux_it_starts_on_the_real_table_and_stops_at_once()
    {
        var watch = MountTableWatch.Start(() => { }, out var failure, devices: null);

        Assert.Null(failure);
        Assert.NotNull(watch);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        watch.Dispose();

        Assert.True(watch.Ended, "the watch's thread outlived Dispose");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stopping took {clock.Elapsed}");
    }

    /// <summary>
    /// Many subscribers, one watch: every test window builds its own platform,
    /// and a watch each was a thread and an inotify instance each, against a
    /// suite that already runs at WSL's inotify ceiling.
    /// </summary>
    [PosixFact]
    public void Subscribers_share_one_watch()
    {
        var subscriptions = Enumerable.Range(0, 10).Select(i => MountTableWatch.Subscribe(() => { }, out var _)).ToList();

        try
        {
            Assert.All(subscriptions, Assert.NotNull);
            Assert.Equal(1, MountTableWatch.SharedStarted);
        }
        finally
        {
            foreach (var s in subscriptions) s?.Dispose();
        }
    }

    [PosixFact]
    public void A_table_that_cannot_be_opened_gives_up_without_throwing()
    {
        var watch = MountTableWatch.Start(() => { }, out var failure, table: "/proc/self/no-such-table", devices: null);

        Assert.Null(watch);
        Assert.Equal("open", failure);
    }

    /// <summary>
    /// The trap from 2026-09-18: a LibraryImport("libc") call threw
    /// DllNotFoundException on the Windows runner and failed four tests there.
    /// Here the failure has to be the poll one — asked first — and not the
    /// open of a path Windows does not have.
    /// </summary>
    [Fact]
    public void Without_libc_it_gives_up_at_poll_before_opening_anything()
    {
        if (!OperatingSystem.IsWindows()) return;

        var watch = MountTableWatch.Start(() => { }, out var failure);

        Assert.Null(watch);
        Assert.Equal("poll", failure);
    }
}
