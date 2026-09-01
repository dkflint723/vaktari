using Vaktari.Core.Places;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Ejecting on Linux: what is said to udisksctl, and what its answers are
/// turned into. No udisks2, no removable drive and no Linux kernel required —
/// the tool is behind a seam and the mount table is an argument.
/// </summary>
public sealed class UdisksEjectorTests
{
    private const string Mounts = """
        /dev/nvme0n1p2 / ext4 rw,relatime 0 0
        /dev/sdb1 /run/media/flint/STICK vfat rw,nosuid 0 0
        /dev/sr0 /run/media/flint/DISC iso9660 ro,nosuid 0 0
        """;

    private static string[] Lines => Mounts.Split(
        '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static UdisksEjector Ejector(
        Func<IReadOnlyList<string>, UdisksEjector.CliResult> answer,
        List<IReadOnlyList<string>>? spoken = null,
        bool haveTool = true)
        => new()
        {
            MountLines = () => Lines,
            HaveToolOverride = _ => haveTool,
            RunOverride = (argv, _) =>
            {
                spoken?.Add(argv);
                return Task.FromResult(answer(argv));
            },
        };

    private static UdisksEjector.CliResult Ok => new(0, "", "");

    /// <summary>
    /// **The single most important test in this file.**
    ///
    /// --force is a lazy unmount: MNT_DETACH returns success with write-back
    /// still pending, so the kernel is still writing to a device the person has
    /// just been told is safe to pull. Adding it is a five-character edit that
    /// reads like a robustness improvement to anyone who has not read the
    /// comment beside it — which is exactly why it is pinned here rather than
    /// trusted to review. Same for umount -l.
    /// </summary>
    [Fact]
    public async Task Force_and_lazy_unmounts_are_never_spoken()
    {
        var spoken = new List<IReadOnlyList<string>>();

        await Ejector(_ => Ok, spoken).EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.NotEmpty(spoken);

        foreach (var argv in spoken)
        {
            Assert.DoesNotContain("--force", argv);
            Assert.DoesNotContain("-f", argv);
            Assert.DoesNotContain("-l", argv);
            Assert.DoesNotContain("--lazy", argv);
        }
    }

    [Fact]
    public async Task A_stick_is_unmounted_then_powered_down()
    {
        var spoken = new List<IReadOnlyList<string>>();

        var result = await Ejector(_ => Ok, spoken)
            .EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.Equal(EjectOutcome.Ejected, result.Outcome);

        Assert.Equal(
            ["unmount", "--no-user-interaction", "-b", "/dev/sdb1"], spoken[0]);

        Assert.Equal(
            ["power-off", "--no-user-interaction", "-b", "/dev/sdb1"], spoken[1]);
    }

    /// <summary>
    /// The ordinary refusal, and it must never read as success. udisks answers
    /// through D-Bus, so its complaint arrives wrapped in a type name.
    /// </summary>
    [Fact]
    public async Task A_busy_volume_is_reported_as_in_use_and_nothing_is_powered_off()
    {
        var spoken = new List<IReadOnlyList<string>>();

        var result = await Ejector(
            argv => argv[0] == "unmount"
                ? new UdisksEjector.CliResult(1, "",
                    "Error unmounting /dev/sdb1: GDBus.Error:org.freedesktop.UDisks2.Error.DeviceBusy: target is busy")
                : Ok,
            spoken)
            .EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.Equal(EjectOutcome.InUse, result.Outcome);
        Assert.Contains("close it", result.Message);

        // Nothing was powered off after a failed unmount: the data is still
        // in flight.
        Assert.Single(spoken);
    }

    /// <summary>
    /// A device that cannot be powered down — a card reader, an internal bay —
    /// is not a failure. udisks knows whether the hardware supports it, and the
    /// filesystem was flushed either way.
    /// </summary>
    [Fact]
    public async Task A_drive_that_cannot_power_down_is_still_safe_to_unplug()
    {
        var result = await Ejector(
            argv => argv[0] == "power-off"
                ? new UdisksEjector.CliResult(1, "",
                    "GDBus.Error:org.freedesktop.UDisks2.Error.NotSupported: Drive does not support power off")
                : Ok)
            .EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.Equal(EjectOutcome.Ejected, result.Outcome);
    }

    /// <summary>
    /// Power-off failing for any OTHER reason is the honest middle: written
    /// out, but the system still has the device.
    /// </summary>
    [Fact]
    public async Task A_power_off_that_fails_for_another_reason_is_only_a_dismount()
    {
        var result = await Ejector(
            argv => argv[0] == "power-off"
                ? new UdisksEjector.CliResult(1, "", "something went wrong")
                : Ok)
            .EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.Equal(EjectOutcome.Dismounted, result.Outcome);
        Assert.Contains("safe to unplug", result.Message);
    }

    /// <summary>A disc gets the tray verb, and never power-off.</summary>
    [Fact]
    public async Task A_disc_is_unmounted_and_the_tray_is_opened()
    {
        var spoken = new List<IReadOnlyList<string>>();

        var result = await Ejector(_ => Ok, spoken)
            .EjectAsync("/run/media/flint/DISC", CancellationToken.None);

        Assert.Equal(EjectOutcome.Ejected, result.Outcome);
        Assert.Contains(spoken, argv => argv[0] == "__eject__");
        Assert.DoesNotContain(spoken, argv => argv[0] == "power-off");
    }

    /// <summary>
    /// The tray not opening does not undo the unmount: the disc is out of use
    /// even if it is still in the drive.
    /// </summary>
    [Fact]
    public async Task A_tray_that_will_not_open_still_leaves_the_disc_unmounted()
    {
        var result = await Ejector(
            argv => argv[0] == "__eject__"
                ? new UdisksEjector.CliResult(-1, "", "eject is not installed")
                : Ok)
            .EjectAsync("/run/media/flint/DISC", CancellationToken.None);

        Assert.Equal(EjectOutcome.Ejected, result.Outcome);
        Assert.Contains("tray", result.Message);
    }

    /// <summary>Without udisks2 there is no safe way to do this, and saying so
    /// is more useful than a failure.</summary>
    [Fact]
    public async Task Without_udisks_the_answer_names_what_is_missing()
    {
        var result = await Ejector(_ => Ok, haveTool: false)
            .EjectAsync("/run/media/flint/STICK", CancellationToken.None);

        Assert.Equal(EjectOutcome.NoTool, result.Outcome);
        Assert.Contains("udisks2", result.Message);
    }

    /// <summary>Eject is idempotent: a volume already gone is the goal, not an
    /// error.</summary>
    [Fact]
    public async Task A_volume_already_unmounted_reports_success_without_running_anything()
    {
        var spoken = new List<IReadOnlyList<string>>();

        var result = await Ejector(_ => Ok, spoken)
            .EjectAsync("/run/media/flint/GONE", CancellationToken.None);

        Assert.Equal(EjectOutcome.Ejected, result.Outcome);
        Assert.Empty(spoken);
    }

    /// <summary>
    /// The tool's own last clause is the readable part — and it comes for free
    /// once the D-Bus type prefix is trimmed.
    /// </summary>
    [Theory]
    [InlineData(
        "Error unmounting: GDBus.Error:org.freedesktop.UDisks2.Error.DeviceBusy: target is busy",
        "target is busy")]
    [InlineData("umount: /mnt/x: not mounted.", "not mounted.")]
    [InlineData("plain trouble", "plain trouble")]
    [InlineData("", "the drive could not be ejected")]
    public void The_tools_complaint_is_trimmed_to_its_last_clause(string stderr, string expected)
        => Assert.Equal(expected, UdisksEjector.Tidy(stderr));
}
