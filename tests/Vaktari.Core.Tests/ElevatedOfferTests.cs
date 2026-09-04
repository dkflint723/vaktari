using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which failures are worth offering administrator rights for.
///
/// **Not all of them, and that is the point.** Elevation does nothing whatever
/// about a file another program has open, a full disk or a path too long for
/// the filesystem — and a shielded button that changes nothing teaches somebody
/// to reach for the consent prompt when the consent prompt is not the answer.
/// </summary>
public sealed class ElevatedOfferTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\work" : "/work";

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    private static RetryRoot Into(string destination, string source, bool folder = false)
        => new(source, Path.Combine(destination, Path.GetFileName(source)), folder);

    /// <summary>
    /// Only the ones refused for want of permission. The locked file is still
    /// on the plain retry beside it; it is simply not on this one.
    /// </summary>
    [Fact]
    public void Only_the_failures_about_permission_are_offered()
    {
        var into = At("into");
        var locked = At("locked.txt");
        var refused = At("refused.txt");

        var offer = RetryRoots.Administrator(
            ElevatedVerb.Copy, into,
            [Into(into, locked), Into(into, refused)],
            new HashSet<string> { refused });

        Assert.NotNull(offer);
        Assert.Equal([refused], offer.Sources);
    }

    /// <summary>
    /// Nothing refused for permission offers nothing at all — the button is
    /// absent rather than present and useless.
    /// </summary>
    [Fact]
    public void A_batch_that_lost_nothing_to_permission_offers_none()
    {
        var into = At("into");

        Assert.Null(RetryRoots.Administrator(
            ElevatedVerb.Copy, into, [Into(into, At("locked.txt"))], new HashSet<string>()));
    }

    /// <summary>
    /// **A root the run renamed is left to the plain retry.** Keep both put the
    /// arriving folder under a new name, and the elevated request carries a
    /// destination and a list of sources from which the elevated copy works the
    /// target out again as destination plus leaf name. For a renamed root that
    /// arithmetic merges the retry into the folder somebody asked to keep
    /// separate — the exact fault RetryRoot carries a target to prevent.
    /// </summary>
    [Fact]
    public void A_root_the_run_renamed_is_not_offered()
    {
        var into = At("into");
        var source = At("A");

        var keptSeparately = new RetryRoot(source, Path.Combine(into, "A (2)"), true);

        Assert.Null(RetryRoots.Administrator(
            ElevatedVerb.Copy, into, [keptSeparately], new HashSet<string> { source }));
    }

    /// <summary>
    /// A delete has no destination, so that rule cannot apply and every refused
    /// root goes.
    /// </summary>
    [Fact]
    public void A_delete_offers_every_root_that_was_refused()
    {
        var one = At("one.txt");
        var two = At("two.txt");

        var offer = RetryRoots.Administrator(
            ElevatedVerb.Delete, null,
            [new RetryRoot(one, one, false), new RetryRoot(two, two, false)],
            new HashSet<string> { one, two });

        Assert.NotNull(offer);
        Assert.Null(offer.Destination);
        Assert.Equal([one, two], offer.Sources);
    }

    /// <summary>
    /// **The offer is checked by being read back.** Nothing is offered that the
    /// elevated side would refuse, so the rules about what a request may be
    /// live in one place — the side that does not trust the other.
    /// </summary>
    [Fact]
    public void An_offer_the_elevated_side_would_refuse_is_never_made()
    {
        var many = Enumerable.Range(0, ElevatedRequest.MaxSources + 1)
            .Select(i => At($"f{i}.txt"))
            .ToList();

        Assert.Null(RetryRoots.Administrator(
            ElevatedVerb.Delete, null,
            [.. many.Select(p => new RetryRoot(p, p, false))],
            new HashSet<string>(many)));
    }
}
