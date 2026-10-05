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

    /// <summary>
    /// **The floor slows to half a minute only once a native source says it is
    /// running**, and goes back to a second when it says it stopped. The wait
    /// the loop asks for is what decides how often an idle machine wakes —
    /// the whole cost this change removes.
    /// </summary>
    [Fact]
    public async Task A_live_native_source_slows_the_floor_and_a_dead_one_restores_it()
    {
        var asked = new List<TimeSpan>();
        var parked = new TaskCompletionSource();
        DeviceWatch? watch = null;

        watch = new DeviceWatch(() => "C:\\|3|1")
        {
            WaitOverride = async (delay, ct) =>
            {
                lock (asked) asked.Add(delay);

                var n = asked.Count;

                if (n == 1) watch!.UseNativeSource(true);
                if (n == 2) watch!.UseNativeSource(false);

                if (n >= 3)
                {
                    parked.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }

                return false;
            },
        };

        watch.Start();
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Dispose();

        lock (asked)
            Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)], asked.Take(3));
    }

    /// <summary>
    /// **A native source that has gone quiet says so in the log** (review M5):
    /// a fallback look that finds a change no nudge announced is the only sign
    /// of it. Once, and only while a native source claims to be running — with
    /// none, the timer finding every change is the design.
    /// </summary>
    [Fact]
    public void A_change_the_fallback_found_is_noticed_only_while_a_native_source_runs()
    {
        using var polled = new DeviceWatch(() => "");
        polled.NoticeMissed();
        Assert.False(polled.MissedByNative);

        using var native = new DeviceWatch(() => "");
        native.UseNativeSource(true);
        native.NoticeMissed();
        Assert.True(native.MissedByNative);
    }

    /// <summary>
    /// **A change only in what the source never announces is not a missed
    /// one** (QA): Windows broadcasts nothing for a network drive letter, so
    /// the fallback look finding one is the design. The provider says which
    /// part of a look is announced; a change outside it warns nothing, and a
    /// change inside it still does.
    /// </summary>
    [Fact]
    public void A_change_only_in_what_is_never_announced_is_not_missed()
    {
        static string Local(string s) => string.Join("\n", s.Split('\n').Where(l => !l.Contains("|4|")));

        using var watch = new DeviceWatch(() => "") { Announced = Local };
        watch.UseNativeSource(true);

        watch.NoticeMissed("C:\\|3|1", "C:\\|3|1\nZ:\\|4|1");
        Assert.False(watch.MissedByNative, "a mapped network letter was taken for a dead source");

        watch.NoticeMissed("C:\\|3|1", "C:\\|3|1\nE:\\|2|1");
        Assert.True(watch.MissedByNative);
    }

    /// <summary>
    /// The loop's own decision: a look after a TIMEOUT that finds a change is
    /// the missed case; the same change after a NUDGE is not.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Only_an_unannounced_change_counts_as_missed(bool nudged, bool missed)
    {
        var looks = 0;
        var done = new TaskCompletionSource();

        var watch = new DeviceWatch(() => Interlocked.Increment(ref looks) == 1 ? "C:\\|3|1" : "C:\\|3|1\nE:\\|2|1")
        {
            Settle = TimeSpan.Zero,
            WaitOverride = async (delay, ct) =>
            {
                var seen = Volatile.Read(ref looks);

                if (seen >= 2)
                {
                    done.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }

                // The second look's wait: answered as a nudge or a timeout. The
                // drain after a nudge is told the burst is over at once.
                return seen == 1 && nudged && delay != TimeSpan.Zero;
            },
        };

        watch.UseNativeSource(true);
        watch.Start();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Dispose();

        Assert.Equal(missed, watch.MissedByNative);
    }
}
