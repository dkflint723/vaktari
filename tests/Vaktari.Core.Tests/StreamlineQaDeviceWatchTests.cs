using Vaktari.Core.Places;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// **The network-letter exemption must not hide a real missed arrival** (QA,
/// qa/streamline). The fallback look's warning leaves out changes Windows
/// never announces — a letter mapped by another program — by comparing only
/// the announced part of the two looks. A stick that arrived unannounced in
/// the same thirty seconds as a mapped letter is still a dead native source,
/// and must still be said. The provider's own test only ever changes one of
/// the two at a time.
/// </summary>
public sealed class StreamlineQaDeviceWatchTests
{
    private static string Local(string snapshot)
        => string.Join("\n", snapshot.Split('\n').Where(l => !l.Contains("|4|", StringComparison.Ordinal)));

    [Theory]
    [InlineData("C:\\|3|1", "C:\\|3|1\nE:\\|2|1\nZ:\\|4|1")]   // stick and mapped letter arrive together
    [InlineData("C:\\|3|1\nZ:\\|4|1", "C:\\|3|1\nE:\\|2|1")]   // stick arrives as the mapped letter goes
    [InlineData("C:\\|3|1\nE:\\|2|0\nZ:\\|4|1", "C:\\|3|1\nE:\\|2|1")] // card pushed in as the letter goes
    public void A_real_arrival_beside_a_network_change_is_still_missed(string before, string now)
    {
        using var watch = new DeviceWatch(() => "") { Announced = Local };
        watch.UseNativeSource(true);

        watch.NoticeMissed(before, now);

        Assert.True(watch.MissedByNative, "a missed arrival was hidden by a network letter changing beside it");
    }

    [Fact]
    public void A_network_letter_alone_is_still_not_missed()
    {
        using var watch = new DeviceWatch(() => "") { Announced = Local };
        watch.UseNativeSource(true);

        watch.NoticeMissed("C:\\|3|1\nE:\\|2|1", "C:\\|3|1\nE:\\|2|1\nZ:\\|4|1");
        watch.NoticeMissed("C:\\|3|1\nE:\\|2|1\nZ:\\|4|1", "C:\\|3|1\nE:\\|2|1");

        Assert.False(watch.MissedByNative);
    }
}
