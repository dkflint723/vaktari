using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// How fast it is going, and how much longer.
///
/// **A copy said nothing about how long it would take.** The bar counted items
/// and bytes, which is the one thing a person can work out by looking twice;
/// what they cannot work out is whether to wait for it or go and do something
/// else.
/// </summary>
public sealed class TransferRateTests
{
    /// <summary>A clock the test drives, so nothing here has to sleep.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);

        public Func<DateTimeOffset> Read => () => Now;
    }

    private static (TransferRate Rate, Clock Clock) Watching(double windowSeconds = 5)
    {
        var clock = new Clock();

        return (new TransferRate(TimeSpan.FromSeconds(windowSeconds), clock.Read), clock);
    }

    /// <summary>One reading is a total, not a rate.</summary>
    [Fact]
    public void One_reading_says_nothing()
    {
        var (rate, _) = Watching();

        rate.Observe(1000);

        Assert.Null(rate.BytesPerSecond);
    }

    [Fact]
    public void Two_readings_a_second_apart_are_a_rate()
    {
        var (rate, clock) = Watching();

        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(1_000_000);

        Assert.Equal(1_000_000, rate.BytesPerSecond!.Value, 0);
    }

    /// <summary>
    /// **The window is what makes it useful.** An average over the whole run is
    /// wrong exactly when it matters: a copy that ran across the SSD and then
    /// hit a USB stick keeps reporting a speed it will never see again.
    /// </summary>
    [Fact]
    public void It_forgets_the_fast_start_once_the_window_has_passed()
    {
        var (rate, clock) = Watching(windowSeconds: 5);

        // A fast second.
        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(100_000_000);

        // Then ten slow ones.
        for (var i = 1; i <= 10; i++)
        {
            clock.Advance(1);
            rate.Observe(100_000_000 + (i * 1_000_000));
        }

        Assert.NotNull(rate.BytesPerSecond);

        // Near the slow rate, nowhere near the average of the two.
        Assert.InRange(rate.BytesPerSecond!.Value, 900_000, 1_100_000);
    }

    /// <summary>
    /// **A stall stops claiming a speed.** The engine reports on every buffer,
    /// every item start and every item finish, so a copy stuck inside one file
    /// reports nothing at all — and a rate that kept its last value would sit
    /// there claiming 10 MiB/s while a drive that has given up moves nothing.
    /// </summary>
    [Fact]
    public void A_transfer_that_stops_reporting_stops_claiming_a_speed()
    {
        var (rate, clock) = Watching(windowSeconds: 5);

        rate.Observe(0);
        clock.Advance(1);
        rate.Observe(1_000_000);

        Assert.NotNull(rate.BytesPerSecond);

        clock.Advance(30);

        Assert.Null(rate.BytesPerSecond);
    }

    /// <summary>
    /// Two readings with the same total are an item starting or finishing, not
    /// a transfer at zero bytes a second.
    /// </summary>
    [Fact]
    public void Readings_that_carry_no_new_bytes_are_not_a_rate()
    {
        var (rate, clock) = Watching();

        rate.Observe(500);
        clock.Advance(1);
        rate.Observe(500);

        Assert.Null(rate.BytesPerSecond);
    }

    // ---- what is left ------------------------------------------------------

    [Fact]
    public void With_a_rate_the_remainder_is_arithmetic()
    {
        var left = TransferRate.Remaining(bytesDone: 0, bytesTotal: 100, bytesPerSecond: 10);

        Assert.Equal(10, left!.Value.TotalSeconds, 3);
    }

    /// <summary>
    /// **A trash and a delete report a count and no bytes**, so there is no
    /// remainder to give — and "0 seconds left" on a delete of four files is a
    /// number invented to fill a space.
    /// </summary>
    [Fact]
    public void Without_a_total_there_is_nothing_to_estimate()
        => Assert.Null(TransferRate.Remaining(bytesDone: 0, bytesTotal: 0, bytesPerSecond: 10));

    [Fact]
    public void Without_a_rate_there_is_nothing_to_estimate()
        => Assert.Null(TransferRate.Remaining(bytesDone: 0, bytesTotal: 100, bytesPerSecond: null));

    /// <summary>
    /// **And a rate of zero is not a rate.** This is a public method taking a
    /// plain double, and dividing by it gives an infinite TimeSpan, which
    /// throws rather than reading as "for ever".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_rate_of_nothing_is_not_a_rate(double bytesPerSecond)
        => Assert.Null(TransferRate.Remaining(0, 100, bytesPerSecond));

    /// <summary>A copy at its last byte is finishing, not "0 seconds left".</summary>
    [Fact]
    public void At_the_end_there_is_nothing_left_to_say()
        => Assert.Null(TransferRate.Remaining(bytesDone: 100, bytesTotal: 100, bytesPerSecond: 10));

    // ---- how it reads ------------------------------------------------------

    [Theory]
    [InlineData(0.5, "less than a minute left")]
    [InlineData(59, "less than a minute left")]
    [InlineData(240, "about 4 min left")]
    [InlineData(3600 * 3, "about 3 hr left")]
    [InlineData(3600 * 24 * 11, "more than a day left")]
    public void It_reads_as_a_guess_because_it_is_one(double seconds, string expected)
        => Assert.Equal(expected, TransferRate.Describe(TimeSpan.FromSeconds(seconds)));
}
