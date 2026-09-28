using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The copy engines' free-space rule, applied to an extraction: refuse only
/// when what is needed is more than what is free, and never because the
/// question could not be answered.
/// </summary>
public sealed class ArchiveRoomTests
{
    private const long MB = 1024 * 1024;

    private static ArchiveRoom Free(long? bytes) => new(_ => bytes, _ => null);

    [Fact]
    public void More_than_is_free_is_refused_in_words()
    {
        var said = Free(100 * MB).RefuseUpFront(Path.Combine(Path.GetTempPath(), "into"), 200 * MB, 10, "a.zip");

        Assert.NotNull(said);
        Assert.StartsWith("there is not enough room on ", said);
        Assert.Contains("a.zip needs 200 MiB, 100 MiB is free", said);
    }

    /// <summary>Core-6: a draft margin of 1 GiB refused exactly this.</summary>
    [Fact]
    public void A_small_zip_onto_a_stick_with_room_proceeds()
        => Assert.Null(Free(500 * MB).RefuseUpFront("x", 10 * 1024, 3, "small.zip"));

    [Fact]
    public void Unknown_free_space_proceeds()
        => Assert.Null(Free(null).RefuseUpFront("x", long.MaxValue / 2, 1_000_000, "huge.zip"));

    /// <summary>Ten thousand empty files declare nothing and still take a
    /// cluster each.</summary>
    [Fact]
    public void Each_entry_is_charged_its_slack()
    {
        Assert.Null(Free(40 * MB).RefuseUpFront("x", 0, 10_000, "a.zip"));
        Assert.NotNull(Free(39 * MB).RefuseUpFront("x", 0, 10_000, "a.zip"));
    }

    /// <summary>
    /// **1% of the drive, between 16 MiB and 256 MiB.** A fixed 256 MiB
    /// refused a 10 KB tar onto a stick with 200 MB free; a drive whose size
    /// cannot be read, and any drive of 25.6 GB and up, keeps the old 256.
    /// </summary>
    [Theory]
    [InlineData(null, 256 * MB)]
    [InlineData(0L, 256 * MB)]
    [InlineData(1L << 40, 256 * MB)]
    [InlineData(25_600 * MB, 256 * MB)]
    [InlineData(8_192 * MB, 8_192 * MB / 100)]
    [InlineData(2_000 * MB, 20 * MB)]
    [InlineData(1_000 * MB, 16 * MB)]
    [InlineData(64 * MB, 16 * MB)]
    public void The_floor_scales_with_the_drive(long? total, long floor)
        => Assert.Equal(floor, new ArchiveRoom(_ => null, _ => null, _ => total).FloorFor("x"));

    [Fact]
    public void Below_the_floor_is_below_the_floor_given()
    {
        Assert.True(Free(255 * MB).BelowFloor("x", 256 * MB));
        Assert.False(Free(257 * MB).BelowFloor("x", 256 * MB));
        Assert.False(Free(null).BelowFloor("x", 256 * MB));
    }

    /// <summary>A run that was above the floor at one look cannot reach a
    /// full disk before the next: the interval is a quarter of the floor.</summary>
    [Theory]
    [InlineData(256 * MB, 64 * MB)]
    [InlineData(82 * MB, 82 * MB / 4)]
    [InlineData(16 * MB, 4 * MB)]
    public void The_floor_is_looked_at_four_times_before_it_could_be_used_up(long floor, long interval)
        => Assert.Equal(interval, ArchiveRoom.IntervalFor(floor));

    [Fact]
    public void The_real_room_knows_the_temp_folders_drive_size()
        => Assert.True(ArchiveRoom.Real.TotalBytes!(Path.GetTempPath()) > 0);

    [Theory]
    [InlineData("FAT32", true)]
    [InlineData("vfat", true)]
    [InlineData("msdos", true)]
    [InlineData("NTFS", false)]
    [InlineData("exFAT", false)]
    [InlineData(null, false)]
    public void Fat32_is_known_by_its_format(string? format, bool fat)
        => Assert.Equal(fat, new ArchiveRoom(_ => null, _ => format).IsFat32("x"));

    [Fact]
    public void The_real_room_answers_for_the_temp_folder()
        => Assert.NotNull(ArchiveRoom.Real.FreeBytes(Path.GetTempPath()));
}
