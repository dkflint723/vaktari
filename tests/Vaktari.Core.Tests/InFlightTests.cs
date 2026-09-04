using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which running operations a drive is not allowed to be pulled out from under.
///
/// **The eject command used to ask nothing at all.** It moved every tab off the
/// drive and called the ejector, so a paste onto a stick was still writing when
/// the volume went — and both of the ejector's success answers take the volume
/// away. The handles carried no paths, so there was nothing to ask.
///
/// Real <see cref="OperationHandle"/>s rather than a stub: the states this has
/// to distinguish are produced by Begin, Pause and Complete, and a stub that
/// simply reports a state cannot be wrong about them in the way the real one
/// could.
/// </summary>
public sealed class InFlightTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "stick");

    /// <summary>A handle mid-transfer over the given paths.</summary>
    private static OperationHandle Running(params string[] paths)
    {
        var handle = new OperationHandle { Paths = paths };

        handle.Begin(paths.Length, totalBytes: 0);

        return handle;
    }

    [Fact]
    public void A_copy_onto_the_drive_is_found()
    {
        var handle = Running(
            Path.Combine(Path.GetTempPath(), "holiday.mp4"),
            Path.Combine(Root, "videos"));

        Assert.Same(handle, Assert.Single(InFlight.On([handle], Root)));
    }

    /// <summary>The other direction counts too: a move OFF the drive is
    /// reading it, and the bytes it has not read yet are still on it.</summary>
    [Fact]
    public void A_move_off_the_drive_is_found()
    {
        var handle = Running(
            Path.Combine(Root, "photos", "one.jpg"),
            Path.Combine(Path.GetTempPath(), "archive"));

        Assert.Single(InFlight.On([handle], Root));
    }

    [Fact]
    public void An_operation_that_never_touches_the_drive_is_not_found()
    {
        var handle = Running(
            Path.Combine(Path.GetTempPath(), "work", "notes.txt"),
            Path.Combine(Path.GetTempPath(), "backup"));

        Assert.Empty(InFlight.On([handle], Root));
    }

    /// <summary>
    /// "stick" must not claim "stickers". A prefix test that stops at the
    /// character count rather than at a separator refuses ejects for a drive
    /// nothing is using — the trap PathRules.Contains documents.
    /// </summary>
    [Fact]
    public void A_neighbour_whose_name_starts_the_same_is_not_the_drive()
    {
        var handle = Running(Path.Combine(Path.GetTempPath(), "stickers", "one.png"));

        Assert.Empty(InFlight.On([handle], Root));
    }

    /// <summary>
    /// **A finished handle lingers.** The shell drops it on a continuation
    /// posted to the UI thread, so for a moment it is both completed and still
    /// in the list — and refusing an eject on account of a copy that has
    /// already ended is a veto nobody can act on.
    /// </summary>
    [Fact]
    public void An_operation_that_has_already_finished_does_not_count()
    {
        var handle = Running(Path.Combine(Root, "one.txt"));

        handle.Complete();

        Assert.Empty(InFlight.On([handle], Root));
    }

    /// <summary>A cancelled transfer has stopped writing, so it is no reason to
    /// keep the drive.</summary>
    [Fact]
    public void A_cancelled_operation_does_not_count()
    {
        var handle = Running(Path.Combine(Root, "one.txt"));

        handle.Cancelled();

        Assert.Empty(InFlight.On([handle], Root));
    }

    /// <summary>
    /// Paused is not finished. The bytes still owed would be written after the
    /// volume had gone, which is the very outcome this guard exists to prevent.
    /// </summary>
    [Fact]
    public void A_paused_transfer_still_holds_the_drive()
    {
        var handle = Running(Path.Combine(Root, "big.iso"));

        handle.Pause();

        Assert.Equal(OperationState.Paused, handle.State);
        Assert.Single(InFlight.On([handle], Root));
    }

    /// <summary>Queued and not yet started still holds it: the operation is
    /// about to write, and ejecting first only moves the failure later.</summary>
    [Fact]
    public void A_queued_transfer_still_holds_the_drive()
    {
        var handle = new OperationHandle { Paths = [Path.Combine(Root, "big.iso")] };

        Assert.Equal(OperationState.Queued, handle.State);
        Assert.Single(InFlight.On([handle], Root));
    }

    /// <summary>A handle nothing filled in claims no drive — which is what the
    /// retry offer and the tests build.</summary>
    [Fact]
    public void A_handle_with_no_paths_claims_nothing()
        => Assert.Empty(InFlight.On([Running()], Root));

    /// <summary>Only the ones that match, out of a list where some do not —
    /// the shell hands over everything it is running, not a filtered set.</summary>
    [Fact]
    public void The_ones_on_the_drive_are_picked_out_of_the_running_list()
    {
        var onIt = Running(Path.Combine(Root, "one.txt"));
        var elsewhere = Running(Path.Combine(Path.GetTempPath(), "elsewhere", "two.txt"));

        Assert.Same(onIt, Assert.Single(InFlight.On([elsewhere, onIt], Root)));
    }

    [Fact]
    public void No_drive_to_ask_about_is_no_answer()
        => Assert.Empty(InFlight.On([Running(Path.Combine(Root, "one.txt"))], root: null));

    /// <summary>
    /// And an empty one, which a place really can carry — an unmounted volume
    /// is given an empty Path deliberately.
    ///
    /// A GUARD, and it cannot be otherwise: PathRules.Contains already returns
    /// false when either side normalises to empty, so `is null` and
    /// `IsNullOrEmpty` are indistinguishable by behaviour here and no mutation
    /// separates them. The early return is worth keeping anyway — it says the
    /// question is meaningless rather than walking every running handle to
    /// discover that — and this pins the ANSWER, which is what a caller
    /// depends on.
    /// </summary>
    [Fact]
    public void An_empty_drive_to_ask_about_is_no_answer_either()
        => Assert.Empty(InFlight.On([Running(Path.Combine(Root, "one.txt"))], root: ""));
}
