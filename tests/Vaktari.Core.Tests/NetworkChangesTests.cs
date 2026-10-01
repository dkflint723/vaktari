using System.Diagnostics;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The machine's network changes, heard once for the process and passed on:
/// one subscription however many listen, none once nobody does, and a burst
/// of events passed on twice at most. Driven through a source of the test's
/// own, which it raises by hand.
/// </summary>
public sealed class NetworkChangesTests
{
    /// <summary>Counts what subscribes and lets go, and raises on demand.</summary>
    private sealed class Source
    {
        private Action? _raise;

        public int Subscribed;
        public int LetGo;

        public IDisposable? Subscribe(Action raise)
        {
            _raise = raise;
            Subscribed++;
            return new Off(() => { _raise = null; LetGo++; });
        }

        public void Raise() => _raise?.Invoke();

        private sealed class Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }
    }

    private static readonly TimeSpan Never = TimeSpan.FromMinutes(10);

    [Fact]
    public void One_subscription_however_many_listen_and_none_once_nobody_does()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, Never);

        var a = changes.Listen(() => { });
        var b = changes.Listen(() => { });
        var c = changes.Listen(() => { });

        Assert.Equal(1, source.Subscribed);
        Assert.Equal(3, changes.Listeners);

        a.Dispose();
        b.Dispose();
        Assert.Equal(0, source.LetGo);

        c.Dispose();
        Assert.Equal(1, source.LetGo);
        Assert.False(changes.Subscribed);
        Assert.Equal(0, changes.Listeners);

        // And asked again for the next one.
        using var d = changes.Listen(() => { });
        Assert.Equal(2, source.Subscribed);
    }

    [Fact]
    public void A_change_is_passed_on_at_once_to_everyone_listening()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, Never);
        var heard = new int[2];

        using var a = changes.Listen(() => heard[0]++);
        using var b = changes.Listen(() => heard[1]++);

        source.Raise();

        Assert.Equal([1, 1], heard);
    }

    /// <summary>A listener let go of is held by nothing and hears nothing.</summary>
    [Fact]
    public void A_listener_let_go_of_hears_nothing_more()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, Never);
        var heard = 0;

        using var staying = changes.Listen(() => { });
        var leaving = changes.Listen(() => heard++);

        leaving.Dispose();
        leaving.Dispose();

        source.Raise();

        Assert.Equal(0, heard);
        Assert.Equal(1, changes.Listeners);
    }

    /// <summary>
    /// **One change of network is several events.** An address for each
    /// protocol and each adapter, and the availability besides, within a
    /// moment of each other; a waiting pane asked again for each would look
    /// at a share that many times. The first is passed on, and the rest of
    /// the burst waits for the spacing to be up.
    /// </summary>
    [Fact]
    public void A_burst_is_passed_on_once_while_the_spacing_lasts()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, Never);
        var heard = 0;

        using var listening = changes.Listen(() => Interlocked.Increment(ref heard));

        for (var i = 0; i < 50; i++) source.Raise();

        Assert.Equal(1, heard);
        Assert.Equal(1, changes.Passes);
    }

    /// <summary>And what the burst held back is passed on once, when the
    /// spacing is up — and not again after that, with nothing more heard.</summary>
    [Fact]
    public async Task What_a_burst_held_back_is_passed_on_once_when_the_spacing_is_up()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, TimeSpan.FromMilliseconds(500));
        var heard = 0;

        using var listening = changes.Listen(() => Interlocked.Increment(ref heard));

        for (var i = 0; i < 50; i++) source.Raise();

        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref heard) < 2 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);

        Assert.Equal(2, Volatile.Read(ref heard));

        await Task.Delay(1_500);

        Assert.Equal(2, Volatile.Read(ref heard));

        // The burst is over: the next change is passed on at once again.
        source.Raise();
        Assert.Equal(3, Volatile.Read(ref heard));
    }

    /// <summary>A lone change is passed on once, not once more when its
    /// spacing is up.</summary>
    [Fact]
    public async Task A_lone_change_is_passed_on_once()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, TimeSpan.FromMilliseconds(200));
        var heard = 0;

        using var listening = changes.Listen(() => Interlocked.Increment(ref heard));

        source.Raise();
        await Task.Delay(1_000);

        Assert.Equal(1, Volatile.Read(ref heard));
    }

    /// <summary>A listener that throws does not keep the change from the
    /// others.</summary>
    [Fact]
    public void A_listener_that_throws_does_not_keep_the_change_from_the_rest()
    {
        var source = new Source();
        var changes = new NetworkChanges(source.Subscribe, Never);
        var heard = false;

        using var throwing = changes.Listen(() => throw new InvalidOperationException("listener"));
        using var listening = changes.Listen(() => heard = true);

        source.Raise();

        Assert.True(heard);
    }

    /// <summary>
    /// The process's own is the system's, and the system answers here — on
    /// Windows and on Linux alike: .NET raises the address and availability
    /// events on both, and a subscription refused would be swallowed and
    /// leave nothing subscribed.
    /// </summary>
    [Fact]
    public void The_system_can_be_listened_to_on_this_platform()
    {
        var changes = NetworkChanges.Shared;

        Assert.Equal(NetworkChanges.DefaultSpacing, changes.Spacing);

        using (changes.Listen(() => { }))
        {
            Assert.True(changes.Subscribed, "the system's network events could not be subscribed to");
        }
    }
}
