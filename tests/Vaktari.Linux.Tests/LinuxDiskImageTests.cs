using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Mounting an image through udisks2: which files are offered, what is said to
/// the tool, and the parsing of what it says back — which is the risky part,
/// because the loop device it picked is only reported in prose.
/// </summary>
public sealed class LinuxDiskImageTests
{
    private static LinuxDiskImages Images(
        Func<IReadOnlyList<string>, LinuxDiskImages.CliResult>? answer = null,
        List<IReadOnlyList<string>>? spoken = null,
        IEnumerable<string>? mounts = null)
        => new()
        {
            HaveToolOverride = _ => true,
            MountLines = () => mounts ?? [],
            RunOverride = (argv, _) =>
            {
                spoken?.Add(argv);
                return Task.FromResult(answer?.Invoke(argv) ?? new LinuxDiskImages.CliResult(0, "", ""));
            },
        };

    [Theory]
    [InlineData("holiday.iso", true)]
    [InlineData("HOLIDAY.ISO", true)]
    [InlineData("disk.img", true)]
    [InlineData("disk.raw", true)]
    [InlineData("machine.vhdx", false)]
    [InlineData("machine.qcow2", false)]
    [InlineData("apple.dmg", false)]
    [InlineData("notes.txt", false)]
    [InlineData("ubuntu.iso.gz", false)]
    public void Only_images_the_loop_driver_can_present_are_offered(string name, bool offered)
        => Assert.Equal(offered, Images().CanMount(Path.Combine(Path.GetTempPath(), name)));

    /// <summary>
    /// **The loop device is only ever reported in a sentence.** udisksctl says
    /// "Mapped file … as /dev/loop3." and there is no machine-readable form, so
    /// this parse is the whole link between attaching an image and being able
    /// to mount or detach it.
    /// </summary>
    [Theory]
    [InlineData("Mapped file /home/flint/ubuntu.iso as /dev/loop3.", "/dev/loop3")]
    [InlineData("Mapped file /home/flint/x.iso as /dev/loop12.\n", "/dev/loop12")]
    [InlineData("nothing useful here", null)]
    public void The_loop_device_is_read_out_of_the_tools_sentence(string output, string? expected)
        => Assert.Equal(expected, LinuxDiskImages.LoopDeviceIn(output));

    [Theory]
    [InlineData("Mounted /dev/loop3 at /run/media/flint/Ubuntu 24.04.", "/run/media/flint/Ubuntu 24.04")]
    [InlineData("Mounted /dev/loop3 at /mnt/iso", "/mnt/iso")]
    [InlineData("no marker", null)]
    public void The_mount_point_is_read_out_of_the_tools_sentence(string output, string? expected)
        => Assert.Equal(expected, LinuxDiskImages.MountPointIn(output));

    /// <summary>
    /// Read-only, and never with a privileged flag: an image is mounted to look
    /// inside it, and asking for write access invites a polkit prompt for
    /// something nobody wanted.
    /// </summary>
    [Fact]
    public async Task Attaching_an_image_asks_for_a_read_only_loop_device()
    {
        var image = Path.Combine(Path.GetTempPath(), "vaktari-test.iso");
        await File.WriteAllTextAsync(image, "not really an iso");

        var spoken = new List<IReadOnlyList<string>>();

        try
        {
            await Images(
                argv => argv[0] switch
                {
                    "loop-setup" => new LinuxDiskImages.CliResult(
                        0, $"Mapped file {image} as /dev/loop7.", ""),
                    _ => new LinuxDiskImages.CliResult(
                        0, "Mounted /dev/loop7 at /run/media/flint/TEST.", ""),
                },
                spoken)
                .MountAsync(image, CancellationToken.None);
        }
        finally
        {
            File.Delete(image);
        }

        Assert.Equal("loop-setup", spoken[0][0]);
        Assert.Contains("-r", spoken[0]);
        Assert.Contains("--no-user-interaction", spoken[0]);

        Assert.Equal("mount", spoken[1][0]);
        Assert.Contains("/dev/loop7", spoken[1]);
    }

    /// <summary>
    /// **A failed mount must not leave the loop device behind.** An orphaned
    /// /dev/loopN is invisible in every file manager, outlives the application,
    /// and quietly holds the image file open.
    /// </summary>
    [Fact]
    public async Task A_mount_that_fails_detaches_what_it_attached()
    {
        var image = Path.Combine(Path.GetTempPath(), "vaktari-bad.iso");
        await File.WriteAllTextAsync(image, "not really an iso");

        var spoken = new List<IReadOnlyList<string>>();

        try
        {
            await Assert.ThrowsAsync<IOException>(() => Images(
                argv => argv[0] switch
                {
                    "loop-setup" => new LinuxDiskImages.CliResult(
                        0, $"Mapped file {image} as /dev/loop7.", ""),
                    "mount" => new LinuxDiskImages.CliResult(
                        1, "", "Error mounting /dev/loop7: GDBus.Error:…Error.Failed: unknown filesystem"),
                    _ => new LinuxDiskImages.CliResult(0, "", ""),
                },
                spoken)
                .MountAsync(image, CancellationToken.None));
        }
        finally
        {
            File.Delete(image);
        }

        Assert.Contains(spoken, argv => argv[0] == "loop-delete");
    }

    /// <summary>Without udisks2 the entry is not offered at all, and the reason
    /// names the package.</summary>
    [Fact]
    public void Without_udisks_the_feature_says_what_is_missing()
    {
        var images = new LinuxDiskImages { HaveToolOverride = _ => false };

        Assert.False(images.IsAvailable);
        Assert.Contains("udisks2", images.UnavailableReason);
    }

    [Fact]
    public void An_image_nobody_mounted_reports_no_mount()
        => Assert.Null(Images().MountOf(Path.Combine(Path.GetTempPath(), "never.iso")));

    /// <summary>
    /// **The kernel is asked, not a dictionary.** A loop device outlives
    /// Vaktari, so a remembered map says "not mounted" about an image that
    /// plainly is after any restart — and acting on that attaches the same file
    /// twice. This also finds an image mounted by anything else.
    /// </summary>
    [Fact]
    public void An_image_already_mounted_is_found_through_the_loop_device()
    {
        var image = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ubuntu.iso"));

        var images = new LinuxDiskImages
        {
            HaveToolOverride = _ => true,
            MountLines = () =>
            [
                "/dev/nvme0n1p2 / ext4 rw 0 0",
                "/dev/loop7 /run/media/flint/Ubuntu\\04024.04 iso9660 ro 0 0",
            ],
            BackingFileOf = device => device == "/dev/loop7" ? image : null,
        };

        var mount = images.MountOf(image);

        Assert.NotNull(mount);
        Assert.Equal("/run/media/flint/Ubuntu 24.04", mount!.MountPath);

        // A different image on the same machine is not this one.
        Assert.Null(images.MountOf(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fedora.iso"))));
    }

    /// <summary>
    /// The kernel appends " (deleted)" when the backing file is gone. It is not
    /// part of the path, and left in it would stop an ordinary image matching
    /// itself.
    /// </summary>
    [Theory]
    [InlineData("/home/flint/ubuntu.iso\n", "/home/flint/ubuntu.iso")]
    [InlineData("/home/flint/gone.iso (deleted)\n", "/home/flint/gone.iso")]
    [InlineData("  /home/flint/spaced.iso  ", "/home/flint/spaced.iso")]
    [InlineData("\n", null)]
    public void A_deleted_marker_is_not_part_of_the_backing_path(string raw, string? expected)
        => Assert.Equal(expected, LinuxDiskImages.CleanBackingFile(raw));
}
