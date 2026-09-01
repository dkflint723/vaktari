using Vaktari.Core.Places;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The device watch: the decision about when a change is worth announcing, and
/// the loop's refusal to die on a bad look.
///
/// Every test here runs with no hardware, no operating system and no clock,
/// because the whole platform surface is one Func returning a string and the
/// whole wait is one injectable Func. That is the reason the type is shaped
/// this way.
/// </summary>
public sealed class DeviceWatchTests
{
    /// <summary>
    /// **The first look announces nothing.** At startup the sidebar has just
    /// been built from exactly these volumes, so raising here would be a
    /// rebuild to redraw what is already on screen — and it would happen on
    /// every launch.
    /// </summary>
    [Fact]
    public void The_first_look_only_establishes_the_baseline()
    {
        var raised = 0;
        using var watch = new DeviceWatch(() => "");
        watch.Changed += (_, _) => raised++;

        Assert.False(watch.Observe("C:\\|3|1"));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_changed_look_is_announced_once()
    {
        var raised = 0;
        using var watch = new DeviceWatch(() => "");
        watch.Changed += (_, _) => raised++;

        watch.Observe("C:\\|3|1");

        Assert.True(watch.Observe("C:\\|3|1\nE:\\|2|1"));
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The load-bearing negative: an idle machine must be silent. A watch that
    /// announced every tick would rebuild the sidebar once a second forever,
    /// each rebuild enumerating drives.
    /// </summary>
    [Fact]
    public void An_unchanged_look_announces_nothing()
    {
        var raised = 0;
        using var watch = new DeviceWatch(() => "");
        watch.Changed += (_, _) => raised++;

        watch.Observe("C:\\|3|1");

        Assert.False(watch.Observe("C:\\|3|1"));
        Assert.False(watch.Observe("C:\\|3|1"));
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// A drive becoming ready is a change on its own — the letter did not move.
    /// This is a card reader with a card pushed into it, or an optical drive
    /// with a disc dropped in, and it is why readiness is part of the key
    /// rather than a filter applied before it.
    /// </summary>
    [Fact]
    public void Readiness_changing_under_a_steady_letter_is_a_change()
    {
        using var watch = new DeviceWatch(() => "");

        watch.Observe("E:\\|2|0");

        Assert.True(watch.Observe("E:\\|2|1"));
    }

    /// <summary>
    /// A watch that has been disposed must not raise: the sidebar it would
    /// rebuild is going away.
    /// </summary>
    [Fact]
    public void A_disposed_watch_stays_quiet()
    {
        var raised = 0;
        var watch = new DeviceWatch(() => "");
        watch.Changed += (_, _) => raised++;

        watch.Observe("C:\\|3|1");
        watch.Dispose();

        Assert.False(watch.Observe("D:\\|3|1"));
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// **Nudge is called from inside a window procedure.** An exception
    /// escaping one of those does not fail the feature, it ends the process —
    /// so this must be safe in every state, including after disposal.
    /// </summary>
    [Fact]
    public void Nudging_a_disposed_watch_does_not_throw()
    {
        var watch = new DeviceWatch(() => "");
        watch.Dispose();

        watch.Nudge();
        watch.Nudge();
    }

    /// <summary>
    /// **One unreadable look must not end detection for the run.** The mount
    /// table can be read mid-write, and a drive can vanish between being listed
    /// and being asked about — if either killed the loop, the sidebar would
    /// stop noticing devices for the rest of the session, silently.
    /// </summary>
    [Fact]
    public async Task A_look_that_throws_does_not_end_the_watch()
    {
        var looks = 0;
        var announced = new TaskCompletionSource();

        var watch = new DeviceWatch(() =>
        {
            var n = Interlocked.Increment(ref looks);

            if (n == 1) throw new IOException("the table was being written");

            return n == 2 ? "C:\\|3|1" : "C:\\|3|1\nE:\\|2|1";
        })
        {
            // Never actually waits: returns "timed out" for the first few
            // rounds, then parks until the watch is disposed, so the loop is
            // driven rather than raced against.
            WaitOverride = async (_, ct) =>
            {
                if (Volatile.Read(ref looks) >= 3)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }

                return false;
            },
        };

        watch.Changed += (_, _) => announced.TrySetResult();
        watch.Start();

        var finished = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        watch.Dispose();

        Assert.Same(announced.Task, finished);
        Assert.True(looks >= 3);
    }

    /// <summary>
    /// A nudge is drained before looking, so one device announcing itself once
    /// per partition costs one rebuild rather than four.
    /// </summary>
    [Fact]
    public async Task A_burst_of_nudges_collapses_into_one_look()
    {
        var looks = 0;
        var settled = new TaskCompletionSource();

        var watch = new DeviceWatch(() =>
        {
            Interlocked.Increment(ref looks);
            return "C:\\|3|1";
        })
        {
            // True means "nudged". Four in a row stand for a four-partition
            // stick; then the drain is told the burst is over, and after the
            // look the loop parks.
            WaitOverride = async (_, ct) =>
            {
                var seen = Volatile.Read(ref looks);

                if (seen == 0)
                {
                    if (Interlocked.Increment(ref _nudges) <= 4) return true;
                    return false;
                }

                settled.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return false;
            },
        };

        watch.Start();

        await Task.WhenAny(settled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        watch.Dispose();

        // Five waits — four nudges and the one that ended the drain — produced
        // exactly one look.
        Assert.Equal(1, looks);
    }

    private int _nudges;
}
