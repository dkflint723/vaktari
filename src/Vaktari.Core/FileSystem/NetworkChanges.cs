using System.Net.NetworkInformation;

namespace Vaktari.Core.FileSystem;

/// <summary>
/// Says when this machine's network may have come back: an address gained or
/// lost, a network found or lost.
/// </summary>
public interface INetworkChanges
{
    /// <summary>
    /// Calls <paramref name="changed"/> after each change, until the handle
    /// answered is disposed. On whatever thread noticed: the callback must
    /// return at once.
    /// </summary>
    IDisposable Listen(Action changed);
}

/// <summary>
/// The machine's network changes, heard once for the whole process and passed
/// on to everything listening.
///
/// **A share or a mapped drive answering again is news nothing else carries.**
/// A pane waiting for a folder on one (FolderReturnWatch) hears a folder above
/// it, the places list, and its own slow look — and the places list does not
/// change for a server coming back, and the slow look backs off to about a
/// minute on a share that does not answer. A VPN connecting, or Wi-Fi coming
/// back, changes the machine's addresses: that is what this hears, through
/// <see cref="NetworkChange.NetworkAddressChanged"/> and
/// <see cref="NetworkChange.NetworkAvailabilityChanged"/>, which .NET raises on
/// Windows and on Linux alike (on Linux from a netlink socket of its own).
///
/// **One subscription, however many listen.** The system is asked from the
/// first listener and let go of when the last one leaves; a listener let go
/// of is held by nothing here.
///
/// **A burst is passed on twice at most.** One change of network is several
/// events — an address for each protocol and each adapter, and the
/// availability besides — arriving within a moment. The first is passed on at
/// once; any more within <see cref="Spacing"/> of it are passed on once, when
/// that time is up, and so on while they keep coming. The second pass is not
/// waste: an address is often there a moment before a server can be reached
/// through it.
/// </summary>
public sealed class NetworkChanges : INetworkChanges
{
    /// <summary>How long after passing a change on any more are held back,
    /// to be passed on together.</summary>
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(2);

    /// <summary>The process's own, over the system's network events.</summary>
    public static NetworkChanges Shared { get; } = new(FromTheSystem, DefaultSpacing);

    private readonly Func<Action, IDisposable?> _source;

    /// <summary>Guards the listeners and the burst. Never held while calling
    /// a listener or the system.</summary>
    private readonly Lock _gate = new();

    private readonly List<Action> _listeners = [];
    private Timer? _held;
    private bool _heldBack;

    /// <summary>Serialises asking the system and letting it go, which happen
    /// outside <see cref="_gate"/>: the system raises its events under locks
    /// of its own.</summary>
    private readonly Lock _subscribing = new();

    private IDisposable? _subscription;

    /// <param name="source">Subscribes the given callback to whatever says the
    /// network changed, and answers how to let it go — or null where nothing
    /// can say. The system's events in the application; a test's own raise.</param>
    /// <param name="spacing">See <see cref="DefaultSpacing"/>.</param>
    public NetworkChanges(Func<Action, IDisposable?> source, TimeSpan spacing)
    {
        _source = source;
        Spacing = spacing;
    }

    /// <summary>How long a burst is held back for. For the tests.</summary>
    public TimeSpan Spacing { get; }

    /// <summary>How many are listening. For the tests.</summary>
    public int Listeners
    {
        get { lock (_gate) return _listeners.Count; }
    }

    /// <summary>Whether a burst's spacing is still running, so the next change
    /// would be held back rather than passed on at once. For the tests.</summary>
    internal bool SpacingRunning
    {
        get { lock (_gate) return _held is not null; }
    }

    /// <summary>Whether the source is subscribed to now. For the tests.</summary>
    public bool Subscribed
    {
        get { lock (_subscribing) return _subscription is not null; }
    }

    /// <summary>How many times a change has been passed on. For the tests.</summary>
    public int Passes => Volatile.Read(ref _passes);

    private int _passes;

    public IDisposable Listen(Action changed)
    {
        var listener = new Listener(this, changed);

        lock (_gate) _listeners.Add(listener.Changed);

        Reconcile();

        return listener;
    }

    private sealed class Listener(NetworkChanges owner, Action changed) : IDisposable
    {
        private int _gone;

        // A delegate of its own, so that two listeners handing in the same
        // method are told apart when one leaves.
        public readonly Action Changed = () => changed();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gone, 1) == 1) return;

            lock (owner._gate) owner._listeners.Remove(Changed);

            owner.Reconcile();
        }
    }

    /// <summary>Subscribed while anybody listens, and only then.</summary>
    private void Reconcile()
    {
        lock (_subscribing)
        {
            bool wanted;
            lock (_gate) wanted = _listeners.Count > 0;

            if (wanted && _subscription is null)
            {
                try
                {
                    _subscription = _source(Raised);
                }
                catch (Exception e)
                {
                    // Nothing here can say the network changed; the waits
                    // still have their own slow looks.
                    Quiet.Swallowed("network", e);
                }
            }
            else if (!wanted && _subscription is { } subscription)
            {
                _subscription = null;

                try
                {
                    subscription.Dispose();
                }
                catch (Exception e)
                {
                    Quiet.Swallowed("network", e);
                }
            }
        }
    }

    /// <summary>
    /// One event from the source: passed on now, or held back with the rest
    /// of its burst. Returns at once whichever.
    /// </summary>
    internal void Raised()
    {
        lock (_gate)
        {
            if (_held is not null)
            {
                _heldBack = true;
                return;
            }

            _held = new Timer(static n => ((NetworkChanges)n!).SpacingOver(), this, Spacing, Timeout.InfiniteTimeSpan);
        }

        PassOn();
    }

    /// <summary>The burst's time is up: what was held back is passed on, and
    /// held back again for as long, or the burst is over.</summary>
    private void SpacingOver()
    {
        lock (_gate)
        {
            if (_held is not { } timer) return;

            if (!_heldBack)
            {
                timer.Dispose();
                _held = null;
                return;
            }

            _heldBack = false;
            timer.Change(Spacing, Timeout.InfiniteTimeSpan);
        }

        PassOn();
    }

    private void PassOn()
    {
        Interlocked.Increment(ref _passes);

        Action[] listening;
        lock (_gate) listening = [.. _listeners];

        foreach (var changed in listening)
        {
            try
            {
                changed();
            }
            catch (Exception e)
            {
                Quiet.Swallowed("network", e);
            }
        }
    }

    /// <summary>The system's two events, for as long as the answer is kept.</summary>
    private static IDisposable? FromTheSystem(Action raised)
    {
        NetworkAddressChangedEventHandler address = (_, _) => raised();
        NetworkAvailabilityChangedEventHandler? availability = (_, _) => raised();

        NetworkChange.NetworkAddressChanged += address;

        try
        {
            NetworkChange.NetworkAvailabilityChanged += availability;
        }
        catch (Exception e)
        {
            // Address changes alone still say a VPN or Wi-Fi came back.
            Quiet.Swallowed("network", e);
            availability = null;
        }

        return new Unsubscribe(() =>
        {
            NetworkChange.NetworkAddressChanged -= address;

            if (availability is not null) NetworkChange.NetworkAvailabilityChanged -= availability;
        });
    }

    private sealed class Unsubscribe(Action off) : IDisposable
    {
        public void Dispose() => off();
    }
}
