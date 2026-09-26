using System.Runtime.Versioning;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// When the Recycle Bin sweep deletes, asked of the policy alone.
///
/// **A day count of zero swept everything older than a day.** The sweep
/// raised it to one, so a hand-edited <c>"trash": {"deleteOldFiles": true}</c>
/// with no number — which deserializes as zero, and which the settings store
/// deliberately does not fill in — emptied the bin of all but today's
/// deletions, while the Linux sweep read the same file as off. Asked of the
/// cutoff rather than of a real sweep: a test that could fail by emptying the
/// developer's own Recycle Bin is not one to run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BinSweepPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [WindowsTheory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_sweep_switched_on_with_no_days_is_off(int days)
        => Assert.Null(WindowsTrashMaintenance.AgeCutoff(
            new TrashSettings { DeleteOldFiles = true, DeleteAfterDays = days }, Now));

    /// <summary>The control: a real number still sweeps, from that many days back.</summary>
    [WindowsFact]
    public void A_sweep_with_days_cuts_off_that_many_days_back()
        => Assert.Equal(
            Now.AddDays(-30),
            WindowsTrashMaintenance.AgeCutoff(new TrashSettings { DeleteOldFiles = true, DeleteAfterDays = 30 }, Now));

    /// <summary>And switched off is off, whatever the number.</summary>
    [WindowsFact]
    public void A_sweep_switched_off_is_off()
        => Assert.Null(WindowsTrashMaintenance.AgeCutoff(
            new TrashSettings { DeleteOldFiles = false, DeleteAfterDays = 30 }, Now));

    /// <summary>
    /// The disk-share half, already off at zero on Windows: no allowance, so
    /// nothing is ever over it. Pinned here beside its twin.
    /// </summary>
    [WindowsTheory]
    [InlineData(0)]
    [InlineData(-10)]
    public void A_share_of_no_percent_allows_nothing_to_be_deleted(int percent)
        => Assert.Equal(0, WindowsTrashMaintenance.Allowance(percent));

    /// <summary>
    /// **The two rules above hold only if the sweep asks them.** The cutoff
    /// and the allowance are tested as functions, and the sweep that deletes
    /// cannot be run here without a Recycle Bin to empty — so putting the old
    /// <c>Math.Max(1, DeleteAfterDays)</c> back into the sweep, or letting a
    /// zero allowance through to the oldest-first purge, left every test above
    /// green (both measured, as one-line mutations). Read from the source,
    /// like the other call-site rules: the age half goes through
    /// <see cref="WindowsTrashMaintenance.AgeCutoff"/> and nothing else in the
    /// sweep reads the day count, and the size half deletes only under an
    /// allowance there is one of.
    /// </summary>
    [Fact]
    public void The_sweep_asks_the_cutoff_and_a_zero_allowance_deletes_nothing()
    {
        var sweep = RepoSource.Body(
            RepoSource.Read("src", "Vaktari.Windows", "WindowsTrashMaintenance.cs"),
            "private static TrashSweepResult Sweep(");

        Assert.Contains("AgeCutoff(policy, DateTimeOffset.UtcNow) is { } cutoff", sweep, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAfterDays", sweep, StringComparison.Ordinal);
        Assert.Contains("if (allowance > 0 && total > allowance)", sweep, StringComparison.Ordinal);
    }
}
