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

    [Fact]
    public void The_floor_is_256_MiB()
    {
        Assert.True(Free(255 * MB).BelowFloor("x"));
        Assert.False(Free(257 * MB).BelowFloor("x"));
        Assert.False(Free(null).BelowFloor("x"));
    }

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
