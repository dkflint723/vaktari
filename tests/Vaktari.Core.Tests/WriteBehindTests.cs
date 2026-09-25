using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The queue every store's save now goes through: off the thread that asked,
/// and one at a time in the order asked.
///
/// **Two saves started together could land the older one last.** Moving a
/// store's flush onto the pool is what keeps a slow disk from freezing the
/// window, and a bare Task.Run per save would let a column drag's second
/// write overtake its first — the file would end up holding the width from
/// before. Each write here waits for the one before it.
/// </summary>
public sealed class WriteBehindTests
{
    [Fact]
    public async Task Writes_land_in_the_order_they_were_asked_for()
    {
        var writes = new WriteBehind();
        var landed = new List<int>();

        // The first is slow and the second is not: without the queue the
        // second finishes first.
        var first = writes.Enqueue(() => { Thread.Sleep(200); lock (landed) landed.Add(1); });
        var second = writes.Enqueue(() => { lock (landed) landed.Add(2); });

        await Task.WhenAll(first, second);

        Assert.Equal([1, 2], landed);
    }

    /// <summary>Asked from a thread of the test's own, which the pool cannot
    /// pick, so "not the caller's" cannot come out true by coincidence.</summary>
    [Fact]
    public async Task A_write_does_not_run_on_the_thread_that_asked()
    {
        var writes = new WriteBehind();
        int? asked = null, ran = null;
        Task? queued = null;

        var caller = new Thread(() =>
        {
            asked = Environment.CurrentManagedThreadId;
            queued = writes.Enqueue(() => ran = Environment.CurrentManagedThreadId);
        });

        caller.Start();
        caller.Join();
        await queued!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(ran);
        Assert.NotEqual(asked, ran);
    }

    /// <summary>One write that failed — a full disk, a locked temp file — must
    /// not cost every later save its turn.</summary>
    [Fact]
    public async Task A_write_that_throws_does_not_stop_the_next_one()
    {
        var writes = new WriteBehind();
        var second = false;

        _ = writes.Enqueue(() => throw new IOException("disk full"));
        await writes.Enqueue(() => second = true);

        Assert.True(second, "the write behind a failed one never ran");
    }

    /// <summary>The way out awaits Idle with nothing around it, so a write
    /// that threw must not throw again there and cut the rest of the way out
    /// short.</summary>
    [Fact]
    public async Task Waiting_for_everything_does_not_throw_what_a_write_threw()
    {
        var writes = new WriteBehind();

        var failing = writes.Enqueue(() => throw new IOException("disk full"));

        await writes.Idle;

        Assert.True(failing.IsFaulted, "the failure did not reach the one who asked for the write");
    }
}
