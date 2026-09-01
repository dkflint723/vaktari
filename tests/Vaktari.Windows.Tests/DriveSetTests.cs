using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The drive snapshot the watcher polls — and above all, what it refuses to ask.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveSetTests
{
    private static (string, DriveType, Func<bool>) Drive(
        string name, DriveType type, bool ready = true)
        => (name, type, () => ready);

    /// <summary>
    /// **The test this whole design exists for.** A mapped drive whose server
    /// is gone answers IsReady only after the SMB timeout — the freeze the
    /// sidebar already carries a comment about. Asked once at startup that was
    /// a stall; asked every second it is a machine that hangs forever.
    ///
    /// Asserted by call count, not by output: a snapshot that filtered network
    /// drives out AFTER probing them would look identical and still freeze.
    /// </summary>
    [Fact]
    public void A_network_drive_is_never_even_asked_whether_it_is_ready()
    {
        var asked = false;

        var line = DriveSet.Snapshot([
            ("Z:\\", DriveType.Network, () => { asked = true; return true; }),
        ]);

        Assert.False(asked, "a network drive must not be probed — that call blocks");
        Assert.Equal("", line);
    }

    [Fact]
    public void A_drive_with_no_root_is_skipped_unasked()
    {
        var asked = false;

        DriveSet.Snapshot([("A:\\", DriveType.NoRootDirectory, () => { asked = true; return true; })]);

        Assert.False(asked);
    }

    /// <summary>
    /// Readiness belongs in the key: a card reader keeps its letter with no
    /// card in it, so a key made of letters alone cannot see a card arrive.
    /// </summary>
    [Fact]
    public void Readiness_is_part_of_the_key()
    {
        var empty = DriveSet.Snapshot([Drive("E:\\", DriveType.Removable, ready: false)]);
        var full = DriveSet.Snapshot([Drive("E:\\", DriveType.Removable, ready: true)]);

        Assert.NotEqual(empty, full);
    }

    [Fact]
    public void A_stick_arriving_changes_the_snapshot()
    {
        var before = DriveSet.Snapshot([Drive("C:\\", DriveType.Fixed)]);
        var after = DriveSet.Snapshot([Drive("C:\\", DriveType.Fixed), Drive("E:\\", DriveType.Removable)]);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Enumeration order is not promised to be stable, and an unstable order
    /// would announce a change on a machine where nothing happened.
    /// </summary>
    [Fact]
    public void The_order_drives_arrive_in_does_not_matter()
    {
        var one = DriveSet.Snapshot([Drive("C:\\", DriveType.Fixed), Drive("E:\\", DriveType.Removable)]);
        var other = DriveSet.Snapshot([Drive("E:\\", DriveType.Removable), Drive("C:\\", DriveType.Fixed)]);

        Assert.Equal(one, other);
    }

    /// <summary>
    /// A drive that throws when asked is not ready — the same reading
    /// BuildDrives already takes — rather than an exception out of a loop that
    /// runs every second.
    /// </summary>
    [Fact]
    public void A_drive_that_throws_when_asked_counts_as_not_ready()
    {
        var line = DriveSet.Snapshot([
            ("E:\\", DriveType.Removable, (Func<bool>)(() => throw new IOException("device not ready"))),
        ]);

        Assert.Equal("E:\\|2|0", line);
    }

    /// <summary>
    /// The real snapshot must answer without throwing and without blocking on
    /// whatever this machine happens to have attached. Not a mock: the point is
    /// that the live call is safe.
    /// </summary>
    [Fact]
    public void The_real_snapshot_answers_promptly()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var snapshot = DriveSet.Snapshot();

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            $"the snapshot took {started.Elapsed} — something in it is blocking");

        // A machine always has at least one local volume.
        Assert.NotEqual("", snapshot);
    }
}
