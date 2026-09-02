using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The band a date falls in, in the reader's own day.
///
/// **Both providers store LastWriteTimeUtc** — WindowsFileSystemProvider and
/// LinuxFileSystemProvider both pass `entry.LastWriteTimeUtc` — so the band was
/// computed on the UTC calendar day while "today" came from DateTimeOffset.Now,
/// a local one. West of Greenwich an evening save is already tomorrow in UTC,
/// so a file saved at six in the evening was filed under "Later", while the
/// Modified cell on the very same row, which converts to local time, said
/// today. East of Greenwich the same mismatch runs the other way for
/// early-morning saves.
///
/// **Every timestamp here carries offset Zero**, because that is what a real
/// entry carries. Writing them in the reader's own offset would make the tests
/// pass against the bug — the frame conversion is the whole point, and a value
/// already in the right frame converts to itself.
/// </summary>
public sealed class DateBandTests
{
    private static readonly TimeSpan Pacific = TimeSpan.FromHours(-8);
    private static readonly TimeSpan Tokyo = TimeSpan.FromHours(9);

    private static FileEntry Saved(DateTimeOffset whenUtc)
        => new("report.txt", "/f/report.txt", 1, whenUtc, EntryFlags.None);

    /// <summary>A wall-clock moment in the reader's zone, stored the way a
    /// provider stores it: converted to UTC.</summary>
    private static DateTimeOffset Utc(int year, int month, int day, int hour, TimeSpan zone)
        => new DateTimeOffset(year, month, day, hour, 0, 0, zone).ToUniversalTime();

    private static string Band(DateTimeOffset whenUtc, DateTimeOffset now)
        => Grouping.Label(Saved(whenUtc), GroupMode.Modified, now);

    /// <summary>
    /// Six in the evening in Los Angeles is two in the morning, next day, UTC.
    /// It is still today to the person who saved it.
    /// </summary>
    [Fact]
    public void An_evening_save_west_of_greenwich_is_today()
    {
        var now = new DateTimeOffset(2026, 3, 14, 20, 0, 0, Pacific);
        var saved = Utc(2026, 3, 14, 18, Pacific);

        Assert.Equal(15, saved.UtcDateTime.Day);   // the trap, stated
        Assert.Equal("Today", Band(saved, now));
    }

    /// <summary>And the evening before is yesterday, not "this week".</summary>
    [Fact]
    public void The_evening_before_is_yesterday()
    {
        var now = new DateTimeOffset(2026, 3, 14, 20, 0, 0, Pacific);
        var saved = Utc(2026, 3, 13, 18, Pacific);

        Assert.Equal("Yesterday", Band(saved, now));
    }

    /// <summary>
    /// The mirror image: early morning in Tokyo is still the previous day in
    /// UTC, so something saved an hour ago used to read "Yesterday".
    /// </summary>
    [Fact]
    public void An_early_morning_save_east_of_greenwich_is_today()
    {
        var now = new DateTimeOffset(2026, 3, 14, 8, 0, 0, Tokyo);
        var saved = Utc(2026, 3, 14, 7, Tokyo);

        Assert.Equal(13, saved.UtcDateTime.Day);   // the trap, mirrored
        Assert.Equal("Today", Band(saved, now));
    }

    /// <summary>Something genuinely in the future still says so.</summary>
    [Fact]
    public void A_real_future_date_is_still_later()
    {
        var now = new DateTimeOffset(2026, 3, 14, 20, 0, 0, Pacific);
        var saved = Utc(2026, 3, 16, 9, Pacific);

        Assert.Equal("Later", Band(saved, now));
    }

    /// <summary>An old file is unaffected by any of this.</summary>
    [Fact]
    public void A_file_from_another_year_is_labelled_by_its_year()
    {
        var now = new DateTimeOffset(2026, 3, 14, 20, 0, 0, Pacific);
        var saved = Utc(2023, 7, 2, 12, Pacific);

        Assert.Equal("2023", Band(saved, now));
    }

    /// <summary>
    /// The ordering agrees with the labels. They are computed by two separate
    /// functions, so a row could otherwise sort into one band and be drawn
    /// under another band's heading.
    /// </summary>
    [Fact]
    public void The_ordering_agrees_with_the_labels()
    {
        var now = new DateTimeOffset(2026, 3, 14, 20, 0, 0, Pacific);

        var thisEvening = Saved(Utc(2026, 3, 14, 18, Pacific));
        var lastEvening = Saved(Utc(2026, 3, 13, 18, Pacific));

        Assert.Equal("Today", Grouping.Label(thisEvening, GroupMode.Modified, now));
        Assert.Equal("Yesterday", Grouping.Label(lastEvening, GroupMode.Modified, now));

        Assert.True(Grouping.CompareGroups(thisEvening, lastEvening, GroupMode.Modified, now) < 0,
                    "today must sort ahead of yesterday");
    }
}
